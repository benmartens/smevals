import json
import os
import re
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

import click
import yaml


@click.group()
@click.version_option()
def cli():
    "TODO help"


def slugify(text):
    return re.sub(r"[^a-zA-Z0-9._-]+", "-", text).strip("-")


def load_yaml(path):
    return yaml.safe_load(path.read_text())


def load_eval(eval_path):
    eval_file = eval_path / "eval.yaml"
    if not eval_file.exists():
        raise click.ClickException(f"{eval_path} is not an Eval - no eval.yaml found")
    return load_yaml(eval_file)


def resolve_runs_root(eval_path, eval_doc, runs_dir):
    if runs_dir is None:
        return eval_path / "runs"
    # An external runs dir may hold runs from many evals
    return runs_dir / slugify(eval_doc.get("name") or eval_path.name)


runs_dir_option = click.option(
    "--runs-dir",
    type=click.Path(file_okay=False, path_type=Path),
    default=None,
    help="Runs directory, if kept outside the eval (namespaced by eval name)",
)


def scalar_env_vars(prefix, mapping):
    "Scalar mapping values as env vars, e.g. submission -> SMEVAL_TASK_SUBMISSION"
    return {
        prefix + re.sub(r"[^A-Za-z0-9]+", "_", key).upper(): str(value)
        for key, value in mapping.items()
        if isinstance(value, (str, int, float, bool))
    }


def normalize_tag(tag):
    return re.sub(r"[^a-z0-9]+", "_", str(tag).lower()).strip("_")


def normalize_check_info(info):
    """Apply the check result contract to checker-emitted fields.

    Checkers own five keys: score (float 0-1), metrics (dict of
    name -> number|bool), tags (list of slugs), notes (str), details
    (dict). Anything else is folded into details rather than trusted
    at the top level, so core-owned keys can never be clobbered.
    """
    out = {}
    if info.get("score") is not None:
        out["score"] = float(info["score"])
    if isinstance(info.get("metrics"), dict):
        out["metrics"] = info["metrics"]
    if isinstance(info.get("tags"), list):
        out["tags"] = sorted({normalize_tag(t) for t in info["tags"] if str(t).strip()})
    if info.get("notes"):
        out["notes"] = str(info["notes"])
    details = info.get("details") if isinstance(info.get("details"), dict) else {}
    extras = {
        key: value
        for key, value in info.items()
        if key not in ("score", "metrics", "tags", "notes", "details")
    }
    if details or extras:
        out["details"] = details | extras
    return out


@cli.command()
@click.argument(
    "eval_path", type=click.Path(exists=True, file_okay=False, path_type=Path)
)
@click.option(
    "models",
    "-m",
    "--model",
    multiple=True,
    help="Model(s) to run against, defaults to the model in the config",
)
@click.option(
    "-c",
    "--config",
    "config_name",
    default="default",
    show_default=True,
    help="Name of the config to use",
)
@click.option(
    "tasks",
    "-t",
    "--task",
    multiple=True,
    help="Task(s) to run, defaults to every task in the eval",
)
@runs_dir_option
def run(eval_path, models, config_name, tasks, runs_dir):
    "Execute the Tasks in an Eval, recording each Run"
    eval_doc = load_eval(eval_path)
    runs_root = resolve_runs_root(eval_path, eval_doc, runs_dir)

    config_path = eval_path / "configs" / f"{config_name}.yaml"
    if not config_path.exists():
        available = sorted(p.stem for p in (eval_path / "configs").glob("*.yaml"))
        raise click.ClickException(
            f"No config named {config_name!r} - available configs: "
            + (", ".join(available) or "(none)")
        )
    config = load_yaml(config_path)

    runner = (config_path.parent / config["runner"]).resolve()
    if not (runner.is_file() and os.access(runner, os.X_OK)):
        raise click.ClickException(f"Runner {runner} is not an executable file")

    task_files = sorted((eval_path / "tasks").glob("*.yaml"))
    if tasks:
        available = {p.stem: p for p in task_files}
        missing = [t for t in tasks if t not in available]
        if missing:
            raise click.ClickException(
                f"No such task(s): {', '.join(missing)} - available tasks: "
                + ", ".join(sorted(available))
            )
        task_files = [available[t] for t in tasks]
    if not task_files:
        raise click.ClickException(f"No tasks found in {eval_path / 'tasks'}")

    models = list(models) or [config["model"]]

    failures = 0
    for task_file in task_files:
        task = load_yaml(task_file)
        for model in models:
            failures += not execute_run(runs_root, task, config_name, runner, model)
    if failures:
        raise click.ClickException(f"{failures} run(s) failed")


def execute_run(runs_root, task, config_name, runner, model):
    "Execute a single Run and record it, returning True on success"
    started = datetime.now(timezone.utc)
    timestamp = started.strftime("%Y-%m-%dT%H-%M-%SZ")
    parent = runs_root / task["name"] / config_name / slugify(model)
    run_dir = parent / timestamp
    # Repeat runs in the same second get a numeric suffix
    suffix = 1
    while run_dir.exists():
        suffix += 1
        run_dir = parent / f"{timestamp}-{suffix}"
    run_dir.mkdir(parents=True)

    click.echo(f"{task['name']} / {config_name} / {model} ... ", nl=False)
    env = (
        os.environ
        | scalar_env_vars("SMEVAL_TASK_", task)
        | {
            "SMEVAL_MODEL": model,
            "SMEVAL_TASK": task["name"],
            "SMEVAL_RUN_DIR": str(run_dir.resolve()),
        }
    )
    # Not every Task is a single prompt - some carry other data instead
    if "prompt" in task:
        env["SMEVAL_PROMPT"] = task["prompt"]
    t0 = time.monotonic()
    result = subprocess.run(
        [str(runner)], cwd=run_dir, env=env, capture_output=True, text=True
    )
    duration = time.monotonic() - t0

    (run_dir / "output.txt").write_text(result.stdout)
    if result.stderr:
        (run_dir / "stderr.txt").write_text(result.stderr)
    # run.yaml is written last: its presence marks a complete Run.
    # The full task is embedded so the Run stays self-describing.
    record = {
        "task": task,
        "config": {
            "name": config_name,
            "runner": str(runner),
            "model": model,
        },
        "started": started.isoformat(),
        "duration_seconds": round(duration, 2),
        "exit_code": result.returncode,
    }
    (run_dir / "run.yaml").write_text(yaml.safe_dump(record, sort_keys=False))

    ok = result.returncode == 0
    status = "ok" if ok else f"FAILED (exit {result.returncode})"
    relative = os.path.relpath(run_dir)
    display = relative if len(relative) < len(str(run_dir)) else str(run_dir)
    click.echo(f"{status} ({duration:.1f}s) -> {display}")
    return ok


@cli.command()
@click.argument(
    "eval_path", type=click.Path(exists=True, file_okay=False, path_type=Path)
)
@click.option(
    "-g",
    "--grader",
    "grader_name",
    default="default",
    show_default=True,
    help="Name of the grader to apply",
)
@click.option(
    "--regrade",
    is_flag=True,
    help="Also grade runs that already have a Grade from this grader",
)
@runs_dir_option
def grade(eval_path, grader_name, regrade, runs_dir):
    "Apply a Grader to each Run in an Eval, producing Grades"
    eval_doc = load_eval(eval_path)
    runs_root = resolve_runs_root(eval_path, eval_doc, runs_dir)

    grader_path = eval_path / "graders" / f"{grader_name}.yaml"
    if not grader_path.exists():
        available = sorted(p.stem for p in (eval_path / "graders").glob("*.yaml"))
        raise click.ClickException(
            f"No grader named {grader_name!r} - available graders: "
            + (", ".join(available) or "(none)")
        )
    grader = load_yaml(grader_path)

    run_files = sorted(runs_root.rglob("run.yaml"))
    if not run_files:
        raise click.ClickException(f"No runs found in {runs_root}")

    graded = skipped = stale = failures = 0
    for run_file in run_files:
        run_dir = run_file.parent
        grade_dir = run_dir / "grades" / grader_name
        if (grade_dir / "grade.yaml").exists() and not regrade:
            if grade_matches_grader(grade_dir, grader):
                skipped += 1
            else:
                stale += 1
            continue
        click.echo(f"{run_dir.relative_to(runs_root)} ... ", nl=False)
        record = grade_run(run_dir, grade_dir, grader, grader_path)
        graded += 1
        failures += record["outcome"] != "pass"
        score = record["score"]
        score_display = "" if score is None else f" score={score}"
        click.echo(f"{record['outcome']}{score_display}")

    if skipped:
        click.echo(f"Skipped {skipped} up-to-date grade(s)")
    if stale:
        click.echo(
            f"{stale} existing grade(s) came from an older version of "
            f"grader {grader_name!r} - use --regrade to discard and redo them"
        )
    if failures:
        raise click.ClickException(f"{failures} of {graded} run(s) graded as fail")


def grade_matches_grader(grade_dir, grader):
    "Was this Grade produced by the current version of the grader spec?"
    snapshot = grade_dir / "grader.yaml"
    if not snapshot.exists():
        return False
    # Parsed comparison: comment and formatting edits don't count
    return yaml.safe_load(snapshot.read_text()) == grader


def grade_run(run_dir, grade_dir, grader, grader_path):
    "Apply every Check in a Grader to one Run, writing grade.yaml"
    # Discarded grades are really discarded - no stale artifacts survive
    if grade_dir.exists():
        shutil.rmtree(grade_dir)
    grade_dir.mkdir(parents=True)
    # Snapshot the grader spec so each Grade records exactly how it
    # was produced, even after the grader is later edited
    (grade_dir / "grader.yaml").write_text(grader_path.read_text())
    # Older run.yaml files recorded just the task name, newer the full task
    task = load_yaml(run_dir / "run.yaml").get("task")
    task_name = task.get("name") if isinstance(task, dict) else task
    results = []
    halted = False
    for check in grader["checks"]:
        name = check["checker"]
        if halted:
            results.append({"checker": name, "skipped": True})
            continue
        if name in BUILTIN_CHECKERS:
            ok, info = BUILTIN_CHECKERS[name](check, run_dir, grade_dir)
        else:
            ok, info = execute_checker_program(
                check, run_dir, grade_dir, grader_path.parent, task_name
            )
        info = normalize_check_info(info)
        if ok and check.get("creates") and not (grade_dir / check["creates"]).exists():
            ok = False
            info["notes"] = f"did not create promised file {check['creates']}"
        # normalize_check_info guarantees info can't clobber core keys
        results.append({"checker": name, "ok": ok} | info)
        if not ok and check.get("required"):
            halted = True

    # The score for the Grade is the last score any check produced
    score = next(
        (r["score"] for r in reversed(results) if r.get("score") is not None), None
    )
    threshold = (grader.get("scoring") or {}).get("pass_threshold")
    if halted or not all(r["ok"] for r in results if "ok" in r):
        outcome = "fail"
    elif score is not None and threshold is not None:
        outcome = "pass" if score >= threshold else "fail"
    else:
        outcome = "pass"

    record = {
        "grader": grader["name"],
        "graded": datetime.now(timezone.utc).isoformat(),
        "outcome": outcome,
        "score": score,
        # Union of every check's tags, for cheap filtering and faceting
        "tags": sorted({t for r in results for t in r.get("tags", [])}),
        "checks": results,
    }
    (grade_dir / "grade.yaml").write_text(yaml.safe_dump(record, sort_keys=False))
    return record


def execute_checker_program(check, run_dir, grade_dir, grader_dir, task_name):
    "Run a CLI Checker, returning (ok, extra result fields)"
    checker = (grader_dir / check["checker"]).resolve()
    if not (checker.is_file() and os.access(checker, os.X_OK)):
        return False, {"notes": f"Checker {checker} is not an executable file"}
    # Absolute path: checkers run with cwd set to the grade workspace.
    # Scalar check config keys become individual env vars, for shell scripts.
    env = (
        os.environ
        | scalar_env_vars("SMEVAL_CHECK_", check)
        | {
            "SMEVAL_RUN_DIR": str(run_dir.resolve()),
            "SMEVAL_CHECK": json.dumps(check),
        }
    )
    if task_name:
        env["SMEVAL_TASK"] = task_name
    result = subprocess.run(
        [str(checker)], cwd=grade_dir, env=env, capture_output=True, text=True
    )
    ok = result.returncode == 0
    info = {}
    if result.stdout.strip():
        try:
            info = json.loads(result.stdout)
        except json.JSONDecodeError:
            info = {"output": result.stdout.strip()}
    if not ok and result.stderr.strip():
        info.setdefault("notes", result.stderr.strip())
    return ok, info


def check_contains(check, run_dir, grade_dir):
    "Built-in: does the Run output contain a string?"
    value = check["value"]
    if value in (run_dir / "output.txt").read_text():
        return True, {}
    return False, {"notes": f"output.txt does not contain {value!r}"}


def check_xml_valid(check, run_dir, grade_dir):
    "Built-in: is a workspace (or Run) file well-formed XML?"
    path = grade_dir / check["file"]
    if not path.exists():
        path = run_dir / check["file"]
    if not path.exists():
        return False, {"notes": f"no such file: {check['file']}"}
    try:
        ET.parse(path)
        return True, {}
    except ET.ParseError as ex:
        return False, {"notes": f"XML parse error: {ex}"}


BUILTIN_CHECKERS = {
    "contains": check_contains,
    "xml-valid": check_xml_valid,
}
