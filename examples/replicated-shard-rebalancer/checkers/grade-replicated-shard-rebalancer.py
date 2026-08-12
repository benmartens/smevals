#!/usr/bin/env python3
from __future__ import annotations

import html
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

CHECKER_DIR = Path(__file__).resolve().parent
if str(CHECKER_DIR) not in sys.path:
    sys.path.insert(0, str(CHECKER_DIR))

from rebalancer_validation import (
    canonical_json,
    case_score,
    deserialize_objective,
    dump_json,
    serialize_metrics,
    validate,
)

TIMEOUT_SECONDS = 30
DETERMINISTIC_PENALTY = 0.97


def emit(payload: dict) -> None:
    sys.stdout.buffer.write(
        (json.dumps(payload, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    )
    sys.stdout.buffer.flush()


def checker_error(message: str) -> None:
    emit(
        {
            "score": 0.0,
            "metrics": {},
            "tags": ["checker_error"],
            "notes": message,
            "per_case_details": [],
        }
    )
    raise SystemExit(1)


def load_bundle(hidden_dir: str) -> dict:
    path = Path(hidden_dir) / "hidden_cases.json"
    if not path.exists():
        checker_error(f"hidden_cases.json not found in {hidden_dir}")
    return json.loads(path.read_text(encoding="utf-8"))


def build_solution(workspace: Path) -> tuple[bool, str]:
    solution = workspace / "ReplicatedShardRebalancer.sln"
    if not solution.exists():
        return False, "ReplicatedShardRebalancer.sln not found"
    try:
        result = subprocess.run(
            [
                "dotnet",
                "build",
                str(solution),
                "-c",
                "Release",
                "--nologo",
                "-v",
                "q",
            ],
            capture_output=True,
            text=True,
            timeout=120,
        )
    except FileNotFoundError:
        return False, "dotnet not found on PATH"
    except subprocess.TimeoutExpired:
        return False, "build timed out"
    return result.returncode == 0, result.stdout + result.stderr


def locate_dll(workspace: Path) -> Path | None:
    root = (
        workspace
        / "src"
        / "ReplicatedShardRebalancer.Cli"
        / "bin"
        / "Release"
    )
    preferred = root / "net10.0" / "ReplicatedShardRebalancer.Cli.dll"
    if preferred.exists():
        return preferred
    return next(
        iter(sorted(root.glob("**/ReplicatedShardRebalancer.Cli.dll"))),
        None,
    )


def run_candidate(
    dll: Path,
    problem_path: Path,
    result_path: Path,
) -> tuple[int | None, str]:
    environment = os.environ.copy()
    environment.pop("REPLICATED_SHARD_REBALANCER_HIDDEN_DIR", None)
    try:
        result = subprocess.run(
            ["dotnet", str(dll), str(problem_path), str(result_path)],
            capture_output=True,
            text=True,
            timeout=TIMEOUT_SECONDS,
            env=environment,
        )
        return result.returncode, (result.stderr or "").strip()
    except subprocess.TimeoutExpired:
        return None, f"timed out after {TIMEOUT_SECONDS}s"
    except OSError as exc:
        return None, str(exc)


def grade_case(case: dict, dll: Path, temp_dir: Path) -> dict:
    case_id = case["id"]
    problem_path = temp_dir / f"{case_id}_problem.json"
    result_path = temp_dir / f"{case_id}_result.json"
    dump_json(case["problem"], str(problem_path))
    started = time.monotonic()
    return_code, stderr = run_candidate(dll, problem_path, result_path)
    runtime_ms = int((time.monotonic() - started) * 1000)
    reference = deserialize_objective(case["reference"])

    detail = {
        "case_id": case_id,
        "category": case.get("category", ""),
        "weight": float(case.get("weight", 1.0)),
        "valid": False,
        "score": 0.0,
        "candidate": None,
        "reference": case["reference"]["metrics"],
        "issues": [],
        "runtime_ms": runtime_ms,
    }
    if return_code is None:
        detail["issues"] = [{"code": "RUN_FAILURE", "detail": stderr}]
        return detail
    if not result_path.exists():
        detail["issues"] = [
            {
                "code": "RUN_FAILURE",
                "detail": f"exit {return_code}: {stderr}".strip(),
            }
        ]
        return detail
    try:
        result = json.loads(result_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        detail["issues"] = [{"code": "PARSE_ERROR", "detail": str(exc)}]
        return detail

    report = validate(case["problem"], result)
    if not report["valid"]:
        detail["issues"] = report["issues"]
        if stderr:
            detail["cli_stderr"] = stderr
        return detail

    metrics = report["metrics"]
    candidate = (
        metrics["maximum_utilization"],
        metrics["utilization_spread"],
        metrics["moved_bytes"],
        metrics["moved_replica_count"],
    )
    score = case_score(candidate, reference)
    detail.update(
        {
            "valid": True,
            "score": round(score, 6),
            "candidate": serialize_metrics(metrics),
        }
    )
    if return_code != 0:
        detail["cli_exit_code"] = return_code
    if stderr:
        detail["cli_stderr"] = stderr
    return detail


def deterministic_probe(case: dict, dll: Path, temp_dir: Path) -> bool:
    problem_path = temp_dir / "probe_problem.json"
    first_path = temp_dir / "probe_first.json"
    second_path = temp_dir / "probe_second.json"
    dump_json(case["problem"], str(problem_path))
    first_code, _ = run_candidate(dll, problem_path, first_path)
    second_code, _ = run_candidate(dll, problem_path, second_path)
    if (
        first_code is None
        or second_code is None
        or not first_path.exists()
        or not second_path.exists()
    ):
        return False
    try:
        first = json.loads(first_path.read_text(encoding="utf-8"))
        second = json.loads(second_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    return canonical_json(first) == canonical_json(second)


def write_patch(workspace: Path) -> str:
    starter = CHECKER_DIR.parent / "fixtures" / "starter"
    if not starter.exists():
        return "# starter fixture not found\n"
    try:
        result = subprocess.run(
            [
                "git",
                "diff",
                "--no-index",
                "--diff-filter=ACMRT",
                str(starter),
                str(workspace),
            ],
            capture_output=True,
            text=True,
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return f"# patch generation failed: {exc}\n"
    lines: list[str] = []
    skip = False
    for line in result.stdout.splitlines(keepends=True):
        if line.startswith("diff --git"):
            normalized = line.replace("\\", "/")
            skip = any(
                marker in normalized
                for marker in (
                    "/bin/",
                    "/obj/",
                    "/runs/",
                    ".dll",
                    ".exe",
                    ".pdb",
                )
            )
        if not skip:
            lines.append(line)
    return "".join(lines) or "# no source-level differences\n"


def cluster_svg(problem: dict | None, result: dict | None) -> str:
    if not isinstance(problem, dict) or not isinstance(result, dict):
        return (
            '<svg xmlns="http://www.w3.org/2000/svg" width="440" height="90">'
            '<rect width="100%" height="100%" fill="#f8f9fa"/>'
            '<text x="16" y="48" font-family="monospace">'
            "No valid showcase cluster"
            "</text></svg>\n"
        )

    nodes = sorted(problem.get("nodes", []), key=lambda item: item["id"])
    shards = {item["id"]: item for item in problem.get("shards", [])}
    assigned = {node["id"]: [] for node in nodes}
    loads = {node["id"]: 0 for node in nodes}
    for placement in result.get("targetPlacements", []):
        shard_id = placement.get("shardId")
        if shard_id not in shards:
            continue
        for node_id in placement.get("nodeIds", []):
            if node_id in assigned:
                assigned[node_id].append(shard_id)
                loads[node_id] += shards[shard_id]["size"]

    width = max(620, 70 + len(nodes) * 145)
    height = 300
    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}">',
        '<rect width="100%" height="100%" fill="#f8f9fa"/>',
        '<text x="18" y="27" font-family="sans-serif" font-size="16" '
        'font-weight="bold">Replicated shard showcase</text>',
    ]
    colors = ["#dbeafe", "#dcfce7", "#fef3c7", "#fce7f3"]
    zones = sorted({node["zone"] for node in nodes})
    color_for = {zone: colors[index % len(colors)] for index, zone in enumerate(zones)}
    for index, node in enumerate(nodes):
        x = 35 + index * 145
        capacity = node["capacity"]
        load = loads[node["id"]]
        ratio = min(1.0, load / capacity)
        bar_height = int(150 * ratio)
        lines.extend(
            [
                f'<rect x="{x}" y="55" width="112" height="200" rx="8" '
                f'fill="{color_for[node["zone"]]}" stroke="#374151"/>',
                f'<rect x="{x + 13}" y="{225 - bar_height}" width="32" '
                f'height="{bar_height}" fill="#2563eb"/>',
                f'<rect x="{x + 13}" y="75" width="32" height="150" '
                'fill="none" stroke="#64748b"/>',
                f'<text x="{x + 56}" y="72" text-anchor="middle" '
                f'font-family="monospace" font-size="12">{html.escape(node["id"])}</text>',
                f'<text x="{x + 56}" y="246" text-anchor="middle" '
                f'font-family="monospace" font-size="11">{load}/{capacity}</text>',
                f'<text x="{x + 56}" y="272" text-anchor="middle" '
                f'font-family="sans-serif" font-size="11">'
                f'zone {html.escape(node["zone"])}</text>',
            ]
        )
        for shard_index, shard_id in enumerate(sorted(assigned[node["id"]])[:7]):
            lines.append(
                f'<text x="{x + 52}" y="{96 + shard_index * 17}" '
                f'font-family="monospace" font-size="10">'
                f'{html.escape(shard_id)}</text>'
            )
    lines.append("</svg>")
    return "\n".join(lines) + "\n"


def summary_markdown(score: float, metrics: dict, details: list[dict]) -> str:
    lines = [
        "# Replicated-Shard-Rebalancer Grading Summary",
        "",
        f"**Overall score**: {score:.4f}",
        "",
        "## Metrics",
        "",
        "| Metric | Value |",
        "|---|---:|",
    ]
    lines.extend(f"| {key} | {value} |" for key, value in metrics.items())
    lines.extend(
        [
            "",
            "## Per-case results",
            "",
            "| Case | Category | Weight | Valid | Score | Runtime ms |",
            "|---|---|---:|:---:|---:|---:|",
        ]
    )
    for detail in details:
        lines.append(
            f"| {detail['case_id']} | {detail['category']} | "
            f"{detail['weight']} | {'yes' if detail['valid'] else 'no'} | "
            f"{detail['score']:.4f} | {detail['runtime_ms']} |"
        )
    return "\n".join(lines) + "\n"


def write_artifacts(
    result: dict,
    workspace: Path,
    details: list[dict],
    showcase_problem: dict | None,
    showcase_result: dict | None,
) -> None:
    Path("grading-results.json").write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    Path("summary.md").write_text(
        summary_markdown(result["score"], result["metrics"], details),
        encoding="utf-8",
        newline="\n",
    )
    Path("solution.patch").write_text(
        write_patch(workspace),
        encoding="utf-8",
        newline="\n",
    )
    Path("showcase-cluster.svg").write_text(
        cluster_svg(showcase_problem, showcase_result),
        encoding="utf-8",
        newline="\n",
    )


def main() -> None:
    started = time.monotonic()
    hidden_dir = os.environ.get(
        "REPLICATED_SHARD_REBALANCER_HIDDEN_DIR",
        "",
    )
    run_dir = os.environ.get("SMEVALS_RUN_DIR", "")
    if not hidden_dir:
        checker_error("REPLICATED_SHARD_REBALANCER_HIDDEN_DIR is not set")
    if not run_dir:
        checker_error("SMEVALS_RUN_DIR is not set")

    bundle = load_bundle(hidden_dir)
    cases = bundle.get("cases", [])
    workspace = Path(run_dir) / "workspace"
    temp_dir = Path("_grade_replicated_shards")
    temp_dir.mkdir(parents=True, exist_ok=True)
    build_ok, build_log = build_solution(workspace)
    if not build_ok:
        result = {
            "score": 0.0,
            "metrics": {
                "build_ok": False,
                "valid_target_rate": 0.0,
                "average_objective_score": 0.0,
                "deterministic": False,
                "hidden_cases_valid": 0,
                "hidden_cases_total": len(cases),
                "runtime_ms": int((time.monotonic() - started) * 1000),
            },
            "tags": ["build_failed"],
            "notes": f"Build failed: {build_log[:500]}",
            "per_case_details": [],
        }
        write_artifacts(result, workspace, [], None, None)
        shutil.rmtree(temp_dir, ignore_errors=True)
        emit(result)
        return

    dll = locate_dll(workspace)
    if dll is None:
        checker_error(
            "ReplicatedShardRebalancer.Cli.dll not found after successful build"
        )

    details = [grade_case(case, dll, temp_dir) for case in cases]
    total_weight = sum(detail["weight"] for detail in details)
    weighted_score = (
        sum(detail["score"] * detail["weight"] for detail in details)
        / total_weight
        if total_weight
        else 0.0
    )
    average_score = weighted_score
    valid_count = sum(detail["valid"] for detail in details)
    probe = next(
        (case for case in cases if case["id"] == bundle.get("probe_case_id")),
        None,
    )
    deterministic = deterministic_probe(probe, dll, temp_dir) if probe else False
    if probe and not deterministic:
        weighted_score *= DETERMINISTIC_PENALTY

    tags = []
    if valid_count == len(cases):
        tags.append("all_valid")
    if deterministic:
        tags.append("deterministic")
    if weighted_score >= 0.9:
        tags.append("high_score")
    for detail in details:
        for issue in detail.get("issues", []):
            tag = f"issue_{issue.get('code', '').lower()}"
            if tag not in tags:
                tags.append(tag)

    metrics = {
        "build_ok": True,
        "valid_target_rate": round(valid_count / len(cases), 4) if cases else 0.0,
        "average_objective_score": round(average_score, 4),
        "deterministic": deterministic,
        "hidden_cases_valid": valid_count,
        "hidden_cases_total": len(cases),
        "runtime_ms": int((time.monotonic() - started) * 1000),
    }
    notes = []
    if not deterministic:
        notes.append(f"deterministic penalty applied ({DETERMINISTIC_PENALTY}x)")
    if valid_count < len(cases):
        notes.append(f"{len(cases) - valid_count} case(s) invalid")

    showcase_case = next(
        (
            case
            for case in cases
            if case["id"] == bundle.get("showcase_case_id")
        ),
        None,
    )
    showcase_result = None
    if showcase_case:
        showcase_path = (
            temp_dir / f"{showcase_case['id']}_result.json"
        )
        if showcase_path.exists():
            try:
                parsed = json.loads(showcase_path.read_text(encoding="utf-8"))
                if validate(showcase_case["problem"], parsed)["valid"]:
                    showcase_result = parsed
            except (OSError, json.JSONDecodeError):
                showcase_result = None

    result = {
        "score": round(weighted_score, 6),
        "metrics": metrics,
        "tags": tags,
        "notes": "; ".join(notes) if notes else "All cases valid and deterministic",
        "per_case_details": details,
    }
    write_artifacts(
        result,
        workspace,
        details,
        showcase_case["problem"] if showcase_case else None,
        showcase_result,
    )
    shutil.rmtree(temp_dir, ignore_errors=True)
    emit(result)


if __name__ == "__main__":
    main()
