#!/usr/bin/env python3
from __future__ import annotations

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

from query_validation import canonical_json, cost_ratio, dump_json, validate

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
    solution = workspace / "QueryOptimizer.sln"
    if not solution.exists():
        return False, "QueryOptimizer.sln not found"
    try:
        result = subprocess.run(
            ["dotnet", "build", str(solution), "-c", "Release", "--nologo", "-v", "q"],
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
    root = workspace / "src" / "QueryOptimizer.Cli" / "bin" / "Release"
    preferred = root / "net10.0" / "QueryOptimizer.Cli.dll"
    if preferred.exists():
        return preferred
    return next(iter(sorted(root.glob("**/QueryOptimizer.Cli.dll"))), None)


def run_candidate(
    dll: Path,
    problem_path: Path,
    result_path: Path,
) -> tuple[int | None, str]:
    candidate_environment = os.environ.copy()
    candidate_environment.pop("QUERY_OPTIMIZER_HIDDEN_DIR", None)
    try:
        result = subprocess.run(
            ["dotnet", str(dll), str(problem_path), str(result_path)],
            capture_output=True,
            text=True,
            timeout=TIMEOUT_SECONDS,
            env=candidate_environment,
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

    detail = {
        "case_id": case_id,
        "weight": float(case.get("weight", 1.0)),
        "valid": False,
        "score": 0.0,
        "cost_ratio": 0.0,
        "candidate_cost": None,
        "reference_cost": int(case["reference"]["cost"]),
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

    candidate_cost = int(report["metrics"]["total_cost"])
    ratio = cost_ratio(candidate_cost, detail["reference_cost"])
    detail.update(
        {
            "valid": True,
            "score": round(ratio, 6),
            "cost_ratio": round(ratio, 6),
            "candidate_cost": candidate_cost,
            "metrics": report["metrics"],
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
                for marker in ("/bin/", "/obj/", "/runs/", ".dll", ".exe", ".pdb")
            )
        if not skip:
            lines.append(line)
    return "".join(lines) or "# no source-level differences\n"


def plan_svg(plan: dict | None, title: str) -> str:
    if not isinstance(plan, dict):
        return (
            '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="80">'
            '<text x="12" y="42" font-family="monospace">No valid showcase plan</text>'
            "</svg>\n"
        )

    positions: list[tuple[dict, float, int]] = []
    leaf_index = 0

    def place(node: dict, depth: int) -> float:
        nonlocal leaf_index
        left = node.get("left")
        right = node.get("right")
        if isinstance(left, dict) and isinstance(right, dict):
            left_x = place(left, depth + 1)
            right_x = place(right, depth + 1)
            x = (left_x + right_x) / 2
        else:
            x = float(leaf_index)
            leaf_index += 1
        positions.append((node, x, depth))
        return x

    place(plan, 0)
    max_depth = max(depth for _, _, depth in positions)
    width = max(520, leaf_index * 150 + 40)
    height = (max_depth + 1) * 100 + 70
    x_for = lambda value: 40 + value * 150
    y_for = lambda depth: 55 + depth * 100
    coordinate = {id(node): (x_for(x), y_for(depth)) for node, x, depth in positions}
    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}">',
        '<rect width="100%" height="100%" fill="#f8f9fa"/>',
        f'<text x="16" y="24" font-family="monospace" font-size="14">{title}</text>',
    ]
    for node, _, _ in positions:
        x, y = coordinate[id(node)]
        for child_name in ("left", "right"):
            child = node.get(child_name)
            if isinstance(child, dict):
                child_x, child_y = coordinate[id(child)]
                lines.append(
                    f'<line x1="{x + 55}" y1="{y + 42}" x2="{child_x + 55}" '
                    f'y2="{child_y}" stroke="#6b7280" stroke-width="2"/>'
                )
    for node, _, _ in positions:
        x, y = coordinate[id(node)]
        operator = str(node.get("operator", "?"))
        detail = str(node.get("tableId") or node.get("indexColumn") or "")
        fill = "#dbeafe" if operator in ("tableScan", "indexSeek") else "#dcfce7"
        lines.extend(
            [
                f'<rect x="{x}" y="{y}" width="110" height="42" rx="6" '
                f'fill="{fill}" stroke="#374151"/>',
                f'<text x="{x + 55}" y="{y + 17}" text-anchor="middle" '
                f'font-family="monospace" font-size="11">{operator}</text>',
                f'<text x="{x + 55}" y="{y + 33}" text-anchor="middle" '
                f'font-family="monospace" font-size="10">{detail}</text>',
            ]
        )
    lines.append("</svg>")
    return "\n".join(lines) + "\n"


def summary_markdown(score: float, metrics: dict, details: list[dict]) -> str:
    lines = [
        "# Query-Optimizer Grading Summary",
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
            "| Case | Weight | Valid | Score | Candidate cost | Reference cost | Runtime ms |",
            "|---|---:|:---:|---:|---:|---:|---:|",
        ]
    )
    for detail in details:
        lines.append(
            f"| {detail['case_id']} | {detail['weight']} | "
            f"{'yes' if detail['valid'] else 'no'} | {detail['score']:.4f} | "
            f"{detail['candidate_cost'] or '-'} | {detail['reference_cost']} | "
            f"{detail['runtime_ms']} |"
        )
    return "\n".join(lines) + "\n"


def write_artifacts(
    result: dict,
    workspace: Path,
    details: list[dict],
    showcase_plan: dict | None,
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
    Path("showcase-plan.svg").write_text(
        plan_svg(showcase_plan, "Query optimizer physical plan"),
        encoding="utf-8",
        newline="\n",
    )


def main() -> None:
    started = time.monotonic()
    hidden_dir = os.environ.get("QUERY_OPTIMIZER_HIDDEN_DIR", "")
    run_dir = os.environ.get("SMEVALS_RUN_DIR", "")
    if not hidden_dir:
        checker_error("QUERY_OPTIMIZER_HIDDEN_DIR is not set")
    if not run_dir:
        checker_error("SMEVALS_RUN_DIR is not set")

    bundle = load_bundle(hidden_dir)
    cases = bundle.get("cases", [])
    workspace = Path(run_dir) / "workspace"
    temp_dir = Path("_grade_tmp")
    temp_dir.mkdir(parents=True, exist_ok=True)
    build_ok, build_log = build_solution(workspace)
    if not build_ok:
        result = {
            "score": 0.0,
            "metrics": {
                "build_ok": False,
                "valid_plan_rate": 0.0,
                "average_cost_ratio": 0.0,
                "deterministic": False,
                "hidden_cases_valid": 0,
                "hidden_cases_total": len(cases),
                "runtime_ms": int((time.monotonic() - started) * 1000),
            },
            "tags": ["build_failed"],
            "notes": f"Build failed: {build_log[:500]}",
            "per_case_details": [],
        }
        write_artifacts(result, workspace, [], None)
        shutil.rmtree(temp_dir, ignore_errors=True)
        emit(result)
        return

    dll = locate_dll(workspace)
    if dll is None:
        checker_error("QueryOptimizer.Cli.dll not found after successful build")

    details = [grade_case(case, dll, temp_dir) for case in cases]
    total_weight = sum(float(case.get("weight", 1.0)) for case in cases)
    weighted_score = (
        sum(detail["score"] * detail["weight"] for detail in details) / total_weight
        if total_weight
        else 0.0
    )
    average_ratio = weighted_score
    valid_count = sum(1 for detail in details if detail["valid"])
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
        "valid_plan_rate": round(valid_count / len(cases), 4) if cases else 0.0,
        "average_cost_ratio": round(average_ratio, 4),
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

    showcase_plan = None
    showcase_path = temp_dir / "dense_twelve_result.json"
    if showcase_path.exists():
        try:
            showcase_plan = json.loads(
                showcase_path.read_text(encoding="utf-8")
            ).get("plan")
        except (OSError, json.JSONDecodeError):
            showcase_plan = None

    result = {
        "score": round(weighted_score, 6),
        "metrics": metrics,
        "tags": tags,
        "notes": "; ".join(notes) if notes else "All cases valid and deterministic",
        "per_case_details": details,
    }
    write_artifacts(result, workspace, details, showcase_plan)
    shutil.rmtree(temp_dir, ignore_errors=True)
    emit(result)


if __name__ == "__main__":
    main()
