"""Shared fixtures: scaffold Evals in a tmp dir and fabricate Runs/Grades.

Runners and Checkers are real executables (sh or Python scripts written
with the current interpreter's shebang), so these tests exercise the
documented subprocess contracts, not internal shortcuts.
"""

import itertools
import sys
import textwrap

import pytest
import yaml
from click.testing import CliRunner

from smevals.cli import cli, slugify
from smevals.text import read_text, write_text


def python_script(body):
    "An executable script body using the same interpreter as the tests"
    return f"#!{sys.executable}\n" + textwrap.dedent(body)


ECHO_RUNNER = python_script("""\
    import os

    print(f"model={os.environ['SMEVALS_MODEL']}")
    print(os.environ.get("SMEVALS_PROMPT", "<no prompt>"))
    """)


def write_executable(path, body):
    path.parent.mkdir(parents=True, exist_ok=True)
    write_text(path, body)
    path.chmod(0o755)
    return path


def read_yaml(path):
    return yaml.safe_load(read_text(path))


def run_dirs(root):
    "Every Run directory under an eval or runs root, in sorted order"
    if (root / "runs").exists():
        root = root / "runs"
    return sorted(p.parent for p in root.rglob("run.yaml"))


@pytest.fixture
def invoke():
    runner = CliRunner()

    def _invoke(*args, expect_exit=0):
        result = runner.invoke(cli, [str(a) for a in args], catch_exceptions=False)
        assert result.exit_code == expect_exit, result.output
        return result

    return _invoke


@pytest.fixture
def make_eval(tmp_path):
    def _make(
        name="demo",
        *,
        tasks=None,
        configs=None,
        graders=None,
        runner=ECHO_RUNNER,
        checkers=None,
        root=None,
    ):
        eval_dir = (root or tmp_path) / name
        (eval_dir / "tasks").mkdir(parents=True)
        (eval_dir / "configs").mkdir()
        (eval_dir / "graders").mkdir()
        write_text(
            eval_dir / "eval.yaml",
            yaml.safe_dump({"name": name, "description": f"The {name} eval"})
        )
        if runner is not None:
            write_executable(eval_dir / "run-llm", runner)
        if tasks is None:
            tasks = {"first": {"prompt": "Say hello"}}
        for stem, doc in tasks.items():
            write_text(
                eval_dir / "tasks" / f"{stem}.yaml",
                yaml.safe_dump({"name": stem} | doc)
            )
        if configs is None:
            configs = {"default": {"runner": "../run-llm", "model": "test-model"}}
        for stem, doc in configs.items():
            write_text(
                eval_dir / "configs" / f"{stem}.yaml",
                yaml.safe_dump({"name": stem} | doc)
            )
        if graders is None:
            graders = {
                "default": {"checks": [{"checker": "contains", "value": "hello"}]}
            }
        for stem, doc in graders.items():
            write_text(
                eval_dir / "graders" / f"{stem}.yaml",
                yaml.safe_dump({"name": stem} | doc)
            )
        for rel, body in (checkers or {}).items():
            write_executable(eval_dir / "checkers" / rel, body)
        return eval_dir

    return _make


# --- fabricated runs and grades, for report/site tests -------------------

_stamp = itertools.count()


def write_run(
    runs_root,
    *,
    task="first",
    config="default",
    model="test-model",
    output="hello world\n",
    exit_code=0,
):
    "Fabricate one Run directory in the documented on-disk format"
    n = next(_stamp)
    run_dir = (
        runs_root
        / task
        / config
        / slugify(model)
        / f"2026-01-01T{n // 3600:02d}-{n // 60 % 60:02d}-{n % 60:02d}Z"
    )
    run_dir.mkdir(parents=True)
    write_text(run_dir / "output.txt", output)
    write_text(
        run_dir / "run.yaml",
        yaml.safe_dump(
            {
                "task": {"name": task},
                "config": {"name": config, "runner": "run-llm", "model": model},
                "started": "2026-01-01T00:00:00+00:00",
                "duration_seconds": 0.5,
                "exit_code": exit_code,
            },
            sort_keys=False,
        )
    )
    return run_dir


def write_grade(
    run_dir,
    snapshot_doc,
    *,
    grader="default",
    outcome="pass",
    score=None,
    tags=(),
    checks=(),
):
    "Fabricate a Grade whose snapshot matches snapshot_doc"
    grade_dir = run_dir / "grades" / grader
    grade_dir.mkdir(parents=True)
    write_text(grade_dir / "grader.yaml", yaml.safe_dump(snapshot_doc))
    write_text(
        grade_dir / "grade.yaml",
        yaml.safe_dump(
            {
                "grader": grader,
                "graded": "2026-01-01T00:00:01+00:00",
                "outcome": outcome,
                "score": score,
                "tags": list(tags),
                "checks": list(checks),
            },
            sort_keys=False,
        )
    )
    return grade_dir
