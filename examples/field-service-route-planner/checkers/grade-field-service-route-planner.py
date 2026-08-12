#!/usr/bin/env python3
"""smevals checker for the field-service route-planner benchmark."""

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

from route_validation import (
    capped_value_ratio,
    case_score,
    dump_json,
    travel_quality,
    validate,
)


TIMEOUT_SECONDS = 30
DETERMINISM_PENALTY = 0.97
ARTIFACTS = (
    "grading-results.json",
    "summary.md",
    "solution.patch",
    "showcase-route.svg",
)


def _emit(payload: dict) -> None:
    sys.stdout.buffer.write(
        (json.dumps(payload, indent=2, ensure_ascii=False) + "\n").encode(
            "utf-8"
        )
    )
    sys.stdout.buffer.flush()


def _error(message: str) -> None:
    payload = {
        "score": 0.0,
        "metrics": {},
        "tags": ["checker_error"],
        "notes": message,
        "per_case_details": [],
    }
    _write_artifacts(payload, None, None, None)
    _emit(payload)
    raise SystemExit(1)


def _load_bundle(hidden_directory: str) -> dict:
    path = Path(hidden_directory) / "hidden_cases.json"
    if not path.exists():
        _error(f"hidden_cases.json not found in {hidden_directory}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        _error(f"Cannot parse hidden bundle: {exc}")


def _build(workspace: Path) -> tuple[bool, str]:
    solution = workspace / "FieldServiceRoutePlanner.sln"
    if not solution.exists():
        return False, "FieldServiceRoutePlanner.sln not found"
    try:
        process = subprocess.run(
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
        return (
            process.returncode == 0,
            (process.stdout or "") + (process.stderr or ""),
        )
    except FileNotFoundError:
        return False, "dotnet not found on PATH"
    except subprocess.TimeoutExpired:
        return False, "build timed out"


def _locate_dll(workspace: Path) -> Path | None:
    root = (
        workspace
        / "src"
        / "FieldServiceRoutePlanner.Cli"
        / "bin"
        / "Release"
    )
    preferred = (
        root / "net10.0" / "FieldServiceRoutePlanner.Cli.dll"
    )
    if preferred.exists():
        return preferred
    return next(
        iter(
            sorted(
                root.glob("**/FieldServiceRoutePlanner.Cli.dll")
            )
        ),
        None,
    )


def _run_candidate(
    dll: Path, problem_path: Path, result_path: Path
) -> tuple[int | None, str]:
    candidate_environment = os.environ.copy()
    candidate_environment.pop("FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR", None)
    try:
        process = subprocess.run(
            ["dotnet", str(dll), str(problem_path), str(result_path)],
            capture_output=True,
            text=True,
            timeout=TIMEOUT_SECONDS,
            env=candidate_environment,
        )
        return process.returncode, (process.stderr or "").strip()
    except subprocess.TimeoutExpired:
        return None, f"timed out after {TIMEOUT_SECONDS}s"
    except Exception as exc:
        return None, str(exc)


def _zero_detail(case: dict, code: str, detail: str) -> dict:
    reference = case.get("reference", {})
    return {
        "case_id": case.get("id", "unknown"),
        "weight": float(case.get("weight", 1.0)),
        "valid": False,
        "score": 0.0,
        "value_ratio": 0.0,
        "travel_ratio": 0.0,
        "candidate_value": 0,
        "candidate_travel": 0,
        "reference_value": reference.get("servedValue", 0),
        "reference_travel": reference.get("totalTravel", 0),
        "issues": [{"code": code, "detail": detail}],
        "runtime_ms": 0,
    }


def _grade_case(case: dict, dll: Path, work: Path) -> dict:
    case_id = case.get("id", "unknown")
    problem_path = work / f"{case_id}-problem.json"
    result_path = work / f"{case_id}-result.json"
    dump_json(case["problem"], str(problem_path))
    started = time.monotonic()
    return_code, stderr = _run_candidate(dll, problem_path, result_path)
    elapsed = int((time.monotonic() - started) * 1000)
    if return_code is None:
        detail = _zero_detail(case, "run_failure", stderr)
        detail["runtime_ms"] = elapsed
        return detail
    if not result_path.exists():
        detail = _zero_detail(
            case,
            "run_failure",
            f"exit {return_code}: {stderr}".strip(),
        )
        detail["runtime_ms"] = elapsed
        return detail
    try:
        result = json.loads(result_path.read_text(encoding="utf-8"))
    except Exception as exc:
        detail = _zero_detail(case, "parse_error", str(exc))
        detail["runtime_ms"] = elapsed
        return detail
    try:
        report = validate(case["problem"], result)
    except Exception as exc:
        detail = _zero_detail(case, "validator_error", str(exc))
        detail["runtime_ms"] = elapsed
        return detail

    reference = case["reference"]
    candidate_value = report["metrics"]["served_value"]
    candidate_travel = report["metrics"]["total_travel"]
    if report["valid"]:
        value_ratio = capped_value_ratio(
            candidate_value, reference["servedValue"]
        )
        travel_ratio = travel_quality(
            candidate_travel,
            reference["totalTravel"],
            candidate_value,
            reference["servedValue"],
        )
        score = case_score(value_ratio, travel_ratio)
    else:
        value_ratio = travel_ratio = score = 0.0
    detail = {
        "case_id": case_id,
        "weight": float(case.get("weight", 1.0)),
        "valid": report["valid"],
        "score": round(score, 6),
        "value_ratio": round(value_ratio, 6),
        "travel_ratio": round(travel_ratio, 6),
        "candidate_value": candidate_value,
        "candidate_travel": candidate_travel,
        "reference_value": reference["servedValue"],
        "reference_travel": reference["totalTravel"],
        "issues": report["issues"],
        "runtime_ms": elapsed,
        "_result": result,
        "_timings": report["route_timings"],
    }
    if stderr:
        detail["cli_stderr"] = stderr
    if return_code != 0:
        detail["cli_exit_code"] = return_code
    return detail


def _deterministic_probe(case: dict, dll: Path, work: Path) -> bool:
    problem_path = work / "probe-problem.json"
    first_path = work / "probe-first.json"
    second_path = work / "probe-second.json"
    dump_json(case["problem"], str(problem_path))
    for output in (first_path, second_path):
        return_code, _ = _run_candidate(dll, problem_path, output)
        if return_code is None or not output.exists():
            return False
    try:
        first = json.loads(first_path.read_text(encoding="utf-8"))
        second = json.loads(second_path.read_text(encoding="utf-8"))
        return first == second
    except Exception:
        return False


def _solution_patch(workspace: Path) -> str:
    starter = CHECKER_DIR.parent / "fixtures" / "starter"
    try:
        process = subprocess.run(
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
        output = []
        skipping = False
        for line in process.stdout.splitlines(keepends=True):
            if line.startswith("diff --git"):
                normalized = line.replace("\\", "/").lower()
                skipping = any(
                    marker in normalized
                    for marker in (
                        "/bin/",
                        "/obj/",
                        ".dll",
                        ".pdb",
                        ".exe",
                    )
                )
            if not skipping:
                output.append(line)
        return "".join(output) or "# no source-level differences\n"
    except Exception as exc:
        return f"# patch generation failed: {exc}\n"


def _summary(payload: dict) -> str:
    lines = [
        "# Field-Service Route-Planner Grading Summary",
        "",
        f"**Overall score**: {payload['score']:.4f}",
        "",
        "## Metrics",
        "",
        "| Metric | Value |",
        "|---|---:|",
    ]
    for name, value in payload.get("metrics", {}).items():
        lines.append(f"| {name} | {value} |")
    lines += [
        "",
        "## Per-case results",
        "",
        "| Case | Weight | Valid | Score | Value ratio | Travel ratio | Runtime ms |",
        "|---|---:|:---:|---:|---:|---:|---:|",
    ]
    for detail in payload.get("per_case_details", []):
        lines.append(
            f"| {detail['case_id']} | {detail['weight']} | "
            f"{'yes' if detail['valid'] else 'no'} | "
            f"{detail['score']:.4f} | {detail['value_ratio']:.4f} | "
            f"{detail['travel_ratio']:.4f} | {detail['runtime_ms']} |"
        )
    return "\n".join(lines) + "\n"


def _showcase_svg(
    problem: dict | None,
    result: dict | None,
    timings: list[dict] | None,
) -> str:
    if problem is None or result is None or timings is None:
        return (
            '<svg xmlns="http://www.w3.org/2000/svg" width="420" '
            'height="70"><text x="12" y="40" font-family="monospace">'
            "No showcase route available</text></svg>\n"
        )
    technicians = {
        tech["id"]: tech for tech in problem["technicians"]
    }
    jobs = {job["id"]: job for job in problem["jobs"]}
    width = 980
    left = 150
    right = 35
    row_height = 90
    height = 55 + row_height * len(timings)
    minimum = min(tech["shiftStart"] for tech in technicians.values())
    maximum = max(tech["shiftEnd"] for tech in technicians.values())
    span = max(1, maximum - minimum)

    def x_at(value: int) -> float:
        return left + (value - minimum) * (width - left - right) / span

    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}">',
        '<rect width="100%" height="100%" fill="#f8fafc"/>',
        '<text x="18" y="27" font-family="sans-serif" font-size="18" '
        'font-weight="bold" fill="#172554">Showcase technician timelines</text>',
    ]
    colors = ["#2563eb", "#059669", "#d97706", "#7c3aed"]
    for row, timing in enumerate(timings):
        tech_id = timing["technicianId"]
        tech = technicians[tech_id]
        y = 55 + row * row_height
        lines.append(
            f'<text x="12" y="{y + 25}" font-family="monospace" '
            f'font-size="13" fill="#111827">{html.escape(tech_id)}</text>'
        )
        lines.append(
            f'<line x1="{x_at(tech["shiftStart"]):.1f}" y1="{y + 25}" '
            f'x2="{x_at(tech["shiftEnd"]):.1f}" y2="{y + 25}" '
            'stroke="#94a3b8" stroke-width="3"/>'
        )
        previous_end = tech["shiftStart"]
        for index, stop in enumerate(timing["stops"]):
            start = stop["serviceStart"]
            end = stop["serviceEnd"]
            if start > previous_end:
                lines.append(
                    f'<line x1="{x_at(previous_end):.1f}" y1="{y + 25}" '
                    f'x2="{x_at(start):.1f}" y2="{y + 25}" '
                    'stroke="#64748b" stroke-width="2" stroke-dasharray="4 3"/>'
                )
            color = colors[index % len(colors)]
            rectangle_width = max(3.0, x_at(end) - x_at(start))
            lines.append(
                f'<rect x="{x_at(start):.1f}" y="{y + 9}" '
                f'width="{rectangle_width:.1f}" height="32" rx="4" '
                f'fill="{color}" fill-opacity="0.82"/>'
            )
            label = stop["jobId"]
            if label in jobs:
                label += f" @ {jobs[label]['location']}"
            lines.append(
                f'<text x="{x_at(start) + 4:.1f}" y="{y + 30}" '
                'font-family="sans-serif" font-size="11" fill="white">'
                f"{html.escape(label)}</text>"
            )
            previous_end = end
        lines.append(
            f'<text x="{left}" y="{y + 63}" font-family="monospace" '
            f'font-size="11" fill="#475569">return={timing["returnTime"]}, '
            f'travel={timing["travel"]}</text>'
        )
    lines.append("</svg>")
    return "\n".join(lines) + "\n"


def _write_artifacts(
    payload: dict,
    workspace: Path | None,
    showcase_problem: dict | None,
    showcase_detail: dict | None,
) -> None:
    clean_payload = json.loads(json.dumps(payload))
    for detail in clean_payload.get("per_case_details", []):
        detail.pop("_result", None)
        detail.pop("_timings", None)
    Path("grading-results.json").write_text(
        json.dumps(clean_payload, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    Path("summary.md").write_text(
        _summary(clean_payload), encoding="utf-8", newline="\n"
    )
    Path("solution.patch").write_text(
        _solution_patch(workspace) if workspace else "# unavailable\n",
        encoding="utf-8",
        newline="\n",
    )
    Path("showcase-route.svg").write_text(
        _showcase_svg(
            showcase_problem,
            showcase_detail.get("_result") if showcase_detail else None,
            showcase_detail.get("_timings") if showcase_detail else None,
        ),
        encoding="utf-8",
        newline="\n",
    )


def main() -> None:
    hidden_directory = os.environ.get(
        "FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR"
    )
    run_directory = os.environ.get("SMEVALS_RUN_DIR")
    if not hidden_directory or not run_directory:
        _error(
            "FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR and SMEVALS_RUN_DIR "
            "must be set"
        )
    bundle = _load_bundle(hidden_directory)
    workspace = Path(run_directory) / "workspace"
    built, build_log = _build(workspace)
    if not built:
        payload = {
            "score": 0.0,
            "metrics": {"build_succeeded": False},
            "tags": ["build_failed"],
            "notes": build_log[-4000:],
            "per_case_details": [],
        }
        _write_artifacts(payload, workspace, None, None)
        _emit(payload)
        return
    dll = _locate_dll(workspace)
    if dll is None:
        _error("Built CLI DLL was not found")

    work = Path.cwd() / ".field-service-route-grading-work"
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    try:
        details = [_grade_case(case, dll, work) for case in bundle["cases"]]
        probe = next(
            case
            for case in bundle["cases"]
            if case["id"] == bundle["probe_case_id"]
        )
        deterministic = _deterministic_probe(probe, dll, work)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    total_weight = sum(detail["weight"] for detail in details)
    weighted_score = sum(
        detail["score"] * detail["weight"] for detail in details
    ) / total_weight
    if not deterministic:
        weighted_score *= DETERMINISM_PENALTY
    valid_count = sum(detail["valid"] for detail in details)
    average_value = sum(detail["value_ratio"] for detail in details) / len(
        details
    )
    average_travel = sum(
        detail["travel_ratio"] for detail in details
    ) / len(details)
    tags = []
    if not deterministic:
        tags.append("nondeterministic")
    if valid_count != len(details):
        tags.append("invalid_routes")
    for detail in details:
        if detail["score"] < 0.999:
            case = next(
                item
                for item in bundle["cases"]
                if item["id"] == detail["case_id"]
            )
            tags.extend(case.get("tags", []))
    tags = sorted(set(tags))
    payload = {
        "score": round(weighted_score, 6),
        "metrics": {
            "build_succeeded": True,
            "valid_route_rate": round(valid_count / len(details), 6),
            "average_value_ratio": round(average_value, 6),
            "average_travel_ratio": round(average_travel, 6),
            "deterministic": deterministic,
        },
        "tags": tags,
        "notes": "Independent validation; exact bounded references.",
        "per_case_details": details,
    }
    showcase_index = next(
        index
        for index, case in enumerate(bundle["cases"])
        if case["id"] == bundle["probe_case_id"]
    )
    _write_artifacts(
        payload,
        workspace,
        bundle["cases"][showcase_index]["problem"],
        details[showcase_index],
    )
    clean_payload = json.loads(
        Path("grading-results.json").read_text(encoding="utf-8")
    )
    _emit(clean_payload)


if __name__ == "__main__":
    main()
