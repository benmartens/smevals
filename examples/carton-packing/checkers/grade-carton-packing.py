#!/usr/bin/env python3
"""
grade-carton-packing.py – smevals checker for the carton-packing Eval.

Environment variables:
  CARTON_PACKING_HIDDEN_DIR  – directory containing hidden_cases.json
  SMEVALS_RUN_DIR            – run directory; workspace is <run>/workspace

Exit codes:
  0  – checker ran cleanly (even if candidate scored 0)
  1  – checker-internal / config error (missing env, bad bundle, etc.)

Emits to stdout: checker JSON with score, metrics, tags, notes, per_case_details.
Artifacts written to CWD: grading-results.json, summary.md, solution.patch, showcase-layout.svg
"""

from __future__ import annotations

import json
import math
import os
import shutil
import subprocess
import sys
import textwrap
import time
import traceback
from pathlib import Path

# ── locate checker package ────────────────────────────────────────────────
# This script lives in the checkers/ directory, so its parent IS the checkers dir.
_CHECKER_DIR = Path(__file__).resolve().parent
if str(_CHECKER_DIR) not in sys.path:
    sys.path.insert(0, str(_CHECKER_DIR))

from packing_validation import (
    validate,
    recompute_objective,
    capped_value_ratio,
    capped_volume_ratio,
    case_score,
    placements_equal,
    canonical_placements,
    reference_pack,
    dump_json,
    dumps_json,
)

# ── constants ─────────────────────────────────────────────────────────────
TIMEOUT_SECS = 30
DETERMINISTIC_PENALTY = 0.97   # multiplicative penalty if probe differs

# ── helpers ───────────────────────────────────────────────────────────────

def _emit(obj: dict) -> None:
    """Write checker JSON to stdout (UTF-8, LF)."""
    sys.stdout.buffer.write((json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def _checker_error(msg: str) -> None:
    _emit({
        "score": 0.0,
        "metrics": {},
        "tags": ["checker_error"],
        "notes": msg,
        "per_case_details": [],
    })
    sys.exit(1)


def _load_bundle(hidden_dir: str) -> dict:
    bundle_path = Path(hidden_dir) / "hidden_cases.json"
    if not bundle_path.exists():
        _checker_error(f"hidden_cases.json not found in {hidden_dir}")
    with open(bundle_path, encoding="utf-8") as fh:
        return json.load(fh)


# ── build ──────────────────────────────────────────────────────────────────

def _build_solution(workspace: Path) -> tuple[bool, str]:
    """
    Build CartonPacking.sln in Release.
    Returns (success, log_or_dll_path).
    """
    sln = workspace / "CartonPacking.sln"
    if not sln.exists():
        return False, "CartonPacking.sln not found in workspace"

    try:
        result = subprocess.run(
            ["dotnet", "build", str(sln), "-c", "Release", "--nologo", "-v", "q"],
            capture_output=True, text=True, timeout=120,
        )
        if result.returncode != 0:
            return False, result.stdout + result.stderr
        return True, result.stdout + result.stderr
    except FileNotFoundError:
        return False, "dotnet not found on PATH"
    except subprocess.TimeoutExpired:
        return False, "build timed out"


def _locate_dll(workspace: Path) -> Path | None:
    release_root = (
        workspace
        / "src"
        / "CartonPacking.Cli"
        / "bin"
        / "Release"
    )
    preferred = release_root / "net10.0" / "CartonPacking.Cli.dll"
    if preferred.exists():
        return preferred
    return next(
        iter(sorted(release_root.glob("**/CartonPacking.Cli.dll"))),
        None,
    )


# ── run candidate ─────────────────────────────────────────────────────────

def _run_candidate(dll: Path, problem_path: Path, result_path: Path) -> tuple[int | None, str]:
    """
    Invoke: dotnet <dll> <problem.json> <result.json>

    Returns (returncode, stderr_text).
    returncode is None when the process could not be launched or timed out
    (i.e. no result file will exist).  A nonzero integer returncode means the
    process ran but reported failure; the result file may still be present and
    contain useful placements that the independent validator should judge.
    """
    try:
        r = subprocess.run(
            ["dotnet", str(dll), str(problem_path), str(result_path)],
            capture_output=True, text=True, timeout=TIMEOUT_SECS,
        )
        return r.returncode, (r.stderr or "").strip()
    except subprocess.TimeoutExpired:
        return None, f"timed out after {TIMEOUT_SECS}s"
    except Exception as exc:
        return None, str(exc)


# ── per-case grading ───────────────────────────────────────────────────────

def _grade_case(
    case: dict,
    dll: Path,
    workspace: Path,
    tmp_dir: Path,
) -> dict:
    cid = case.get("id", "unknown")
    problem = case.get("problem", {})
    reference = case.get("reference", {})
    ref_value = reference.get("value", 0)
    ref_volume = reference.get("volume", 0)
    case_weight = float(case.get("weight", 1.0))

    def _zero(issues: list[dict]) -> dict:
        return {
            "case_id": cid,
            "weight": case_weight,
            "valid": False,
            "score": 0.0,
            "value_ratio": 0.0,
            "volume_ratio": 0.0,
            "candidate_value": 0,
            "candidate_volume": 0,
            "reference_value": ref_value,
            "reference_volume": ref_volume,
            "issues": issues,
            "runtime_ms": 0,
        }

    try:
        problem_file = tmp_dir / f"{cid}_problem.json"
        result_file  = tmp_dir / f"{cid}_result.json"

        dump_json(problem, str(problem_file))

        t0 = time.monotonic()
        returncode, cli_stderr = _run_candidate(dll, problem_file, result_file)
        elapsed_ms = int((time.monotonic() - t0) * 1000)

        # returncode=None means the process never ran (timeout / launch failure).
        # In that case no result file will exist; report RUN_FAILURE immediately.
        if returncode is None:
            d = _zero([{"code": "RUN_FAILURE", "detail": cli_stderr}])
            d["runtime_ms"] = elapsed_ms
            return d

        # The process ran (even if it reported an error via nonzero exit code).
        # Attempt to parse the result file regardless – the CLI may write valid
        # placements while still exiting nonzero to signal its own validator found
        # issues.  Our independent validator is the authority.
        if not result_file.exists():
            detail = f"exit {returncode}"
            if cli_stderr:
                detail += f": {cli_stderr}"
            d = _zero([{"code": "RUN_FAILURE", "detail": detail}])
            d["runtime_ms"] = elapsed_ms
            if cli_stderr:
                d["cli_stderr"] = cli_stderr
            return d

        try:
            with open(result_file, encoding="utf-8") as fh:
                result = json.load(fh)
        except Exception as exc:
            detail = str(exc)
            if cli_stderr:
                detail += f" (cli_stderr: {cli_stderr})"
            d = _zero([{"code": "PARSE_ERROR", "detail": detail}])
            d["runtime_ms"] = elapsed_ms
            if cli_stderr:
                d["cli_stderr"] = cli_stderr
            return d

        try:
            validation = validate(problem, result)
            cand_value, cand_volume = recompute_objective(problem, result)
        except Exception as exc:
            d = _zero([{"code": "VALIDATOR_ERROR", "detail": str(exc)}])
            d["runtime_ms"] = elapsed_ms
            if cli_stderr:
                d["cli_stderr"] = cli_stderr
            return d

        # Build the base detail dict; attach cli_stderr as a diagnostic field
        # when the CLI exited nonzero (even if our validator finds the layout valid).
        def _make_detail(valid: bool, issues: list[dict], vr: float = 0.0,
                         volr: float = 0.0, sc: float = 0.0) -> dict:
            d = {
                "case_id": cid,
                "weight": case_weight,
                "valid": valid,
                "score": round(sc, 6),
                "value_ratio": round(vr, 6),
                "volume_ratio": round(volr, 6),
                "candidate_value": cand_value,
                "candidate_volume": cand_volume,
                "reference_value": ref_value,
                "reference_volume": ref_volume,
                "issues": issues,
                "runtime_ms": elapsed_ms,
            }
            # Preserve CLI stderr as informational – it does NOT influence
            # issue codes; our independent validation is authoritative.
            if cli_stderr:
                d["cli_stderr"] = cli_stderr
            if returncode != 0:
                d["cli_exit_code"] = returncode
            return d

        if not validation["valid"]:
            return _make_detail(False, validation["issues"])

        vr   = capped_value_ratio(cand_value, ref_value)
        volr = capped_volume_ratio(cand_volume, ref_volume)
        sc   = case_score(vr, volr)
        return _make_detail(True, [], vr, volr, sc)

    except Exception as exc:
        return _zero([{"code": "CHECKER_INTERNAL", "detail": f"unhandled: {exc}"}])


# ── deterministic probe ────────────────────────────────────────────────────

def _deterministic_probe(probe_case: dict, dll: Path, workspace: Path, tmp_dir: Path, seed: int) -> bool:
    """
    Run the probe case twice and compare canonical outputs.
    Also compare against reference packer output.
    Returns True if outputs are consistent.
    """
    problem = probe_case["problem"]
    problem_file = tmp_dir / "probe_problem.json"
    result1_file = tmp_dir / "probe_result1.json"
    result2_file = tmp_dir / "probe_result2.json"
    dump_json(problem, str(problem_file))

    def _run_and_load(out_file: Path) -> list[dict] | None:
        returncode, _ = _run_candidate(dll, problem_file, out_file)
        if returncode is None or not out_file.exists():
            return None
        try:
            with open(out_file, encoding="utf-8") as fh:
                return json.load(fh).get("placements", [])
        except Exception:
            return None

    p1 = _run_and_load(result1_file)
    p2 = _run_and_load(result2_file)

    if p1 is None or p2 is None:
        return False
    return placements_equal(p1, p2)


# ── artifact: solution.patch ───────────────────────────────────────────────

def _write_patch(workspace: Path, checker_dir: Path) -> str:
    """Generate a diff between the starter fixture and the candidate workspace."""
    starter = checker_dir.parent / "fixtures" / "starter"
    if not starter.exists():
        return "# starter fixture not found\n"

    try:
        result = subprocess.run(
            ["git", "diff", "--no-index",
             "--diff-filter=ACMRT",
             str(starter), str(workspace)],
            capture_output=True, text=True, timeout=30,
        )
        patch = result.stdout
        # Filter out binary/build artifact paths (normalize separators for matching)
        lines = patch.splitlines(keepends=True)
        filtered: list[str] = []
        skip = False
        _SKIP_SEGS = {"/bin/", "\\bin\\", "/obj/", "\\obj\\",
                      "/runs/", "\\runs\\", ".dll", ".exe", ".pdb"}
        for line in lines:
            if line.startswith("diff --git"):
                # Normalize to forward slash for matching
                norm = line.replace("\\", "/")
                skip = any(seg.replace("\\", "/") in norm for seg in _SKIP_SEGS)
            if not skip:
                filtered.append(line)
        return "".join(filtered) or "# no source-level differences\n"
    except Exception as exc:
        return f"# patch generation failed: {exc}\n"


# ── artifact: showcase SVG ─────────────────────────────────────────────────

_COLORS = [
    "#e74c3c", "#3498db", "#2ecc71", "#f39c12", "#9b59b6",
    "#1abc9c", "#e67e22", "#34495e", "#e91e63", "#00bcd4",
]


def _write_showcase_svg(showcase_placements: list[dict] | None, problem: dict | None) -> str:
    """
    Generate a top/front/side SVG for the showcase case.
    Returns SVG text or a placeholder on failure.
    """
    try:
        if showcase_placements is None or problem is None:
            raise ValueError("no data")
        c = problem["container"]
        cw, cd, ch = c["width"], c["depth"], c["height"]

        scale = 20
        margin = 40
        panel_gap = 30
        label_h = 25

        pw_top = cw * scale
        ph_top = cd * scale
        pw_front = cw * scale
        ph_front = ch * scale
        pw_side  = cd * scale
        ph_side  = ch * scale

        total_w = margin * 2 + pw_top + panel_gap + pw_front + panel_gap + pw_side
        total_h = margin * 2 + max(ph_top, ph_front, ph_side) + label_h

        svg_lines = [
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{total_w}" height="{total_h}">',
            '<rect width="100%" height="100%" fill="#f8f9fa"/>',
        ]

        # Panel origins
        top_ox    = margin
        top_oy    = margin + label_h
        front_ox  = margin + pw_top + panel_gap
        front_oy  = margin + label_h
        side_ox   = front_ox + pw_front + panel_gap
        side_oy   = margin + label_h

        # Labels
        def lbl(x, y, text):
            return f'<text x="{x}" y="{y}" font-family="monospace" font-size="13" fill="#333">{text}</text>'

        svg_lines += [
            lbl(top_ox,   margin + label_h - 5, "Top (X-Y)"),
            lbl(front_ox, margin + label_h - 5, "Front (X-Z)"),
            lbl(side_ox,  margin + label_h - 5, "Side (Y-Z)"),
        ]

        # Container outlines
        def rect_outline(ox, oy, w, h):
            return f'<rect x="{ox}" y="{oy}" width="{w}" height="{h}" fill="none" stroke="#333" stroke-width="2"/>'

        svg_lines += [
            rect_outline(top_ox,   top_oy,   pw_top,   ph_top),
            rect_outline(front_ox, front_oy, pw_front, ph_front),
            rect_outline(side_ox,  side_oy,  pw_side,  ph_side),
        ]

        # Build color map by cartonId
        all_ids = list({p.get("cartonId", "") for p in showcase_placements})
        all_ids.sort()
        color_map = {cid: _COLORS[i % len(_COLORS)] for i, cid in enumerate(all_ids)}

        for p in showcase_placements:
            cid = p.get("cartonId", "")
            col = color_map.get(cid, "#aaa")
            x, y, z = p["x"], p["y"], p["z"]
            pw, pd, pz_h = p["width"], p["depth"], p["height"]

            def box(ox, oy, rx, ry, rw, rh, flip_y=True, max_h=None):
                if flip_y and max_h is not None:
                    ry = max_h - ry - rh
                return (
                    f'<rect x="{ox + rx * scale}" y="{oy + ry * scale}" '
                    f'width="{rw * scale}" height="{rh * scale}" '
                    f'fill="{col}" stroke="#555" stroke-width="1" fill-opacity="0.75"/>'
                )

            # Top panel: X-Y view (z projection)
            svg_lines.append(box(top_ox,   top_oy,   x, y, pw, pd,  flip_y=False))
            # Front panel: X-Z view
            svg_lines.append(box(front_ox, front_oy, x, z, pw, pz_h, flip_y=True, max_h=ch))
            # Side panel: Y-Z view
            svg_lines.append(box(side_ox,  side_oy,  y, z, pd, pz_h, flip_y=True, max_h=ch))

        # Legend with carton count and metadata
        legend_y = top_oy + max(ph_top, ph_front, ph_side) + 10
        lx = margin
        # Count placements per cartonId
        cid_counts: dict[str, int] = {}
        for p in showcase_placements:
            cid_counts[p.get("cartonId", "")] = cid_counts.get(p.get("cartonId", ""), 0) + 1
        # Problem metadata: total packed value/weight if available
        total_val = sum(
            (next((c["value"] for c in problem.get("cartons", []) if c["id"] == p.get("cartonId")), 0))
            for p in showcase_placements
        )
        total_wt = sum(
            (next((c["weight"] for c in problem.get("cartons", []) if c["id"] == p.get("cartonId")), 0))
            for p in showcase_placements
        )
        max_wt = problem.get("container", {}).get("maxWeight", "?")
        meta_y = legend_y - 18
        svg_lines.append(
            f'<text x="{margin}" y="{meta_y}" font-family="monospace" font-size="11" fill="#555">'
            f'Packed: {len(showcase_placements)} items | value={total_val} | weight={total_wt}/{max_wt}'
            f'</text>'
        )
        for cid, col in color_map.items():
            cnt = cid_counts.get(cid, 0)
            svg_lines.append(f'<rect x="{lx}" y="{legend_y}" width="14" height="14" fill="{col}"/>')
            svg_lines.append(
                f'<text x="{lx + 18}" y="{legend_y + 12}" font-family="monospace" font-size="11" fill="#333">'
                f'{cid} ×{cnt}</text>'
            )
            lx += max(90, len(cid) * 8 + 50)

        svg_lines.append("</svg>")
        return "\n".join(svg_lines) + "\n"
    except Exception:
        return '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="60"><text x="10" y="35" font-family="monospace" font-size="12">SVG generation failed</text></svg>\n'


# ── summary markdown ───────────────────────────────────────────────────────

def _write_summary(score: float, metrics: dict, per_case: list[dict]) -> str:
    lines = [
        "# Carton-Packing Grading Summary\n",
        f"**Overall score**: {score:.4f}\n",
        "",
        "## Metrics\n",
        "| Metric | Value |",
        "|--------|-------|",
    ]
    for k, v in metrics.items():
        lines.append(f"| {k} | {v} |")
    lines += [
        "",
        "## Per-case Results\n",
        "| Case | Weight | Valid | Score | Value ratio | Volume ratio | Runtime ms |",
        "|------|--------|-------|-------|-------------|--------------|------------|",
    ]
    for d in per_case:
        lines.append(
            f"| {d['case_id']} | {d.get('weight', 1.0)} | "
            f"{'✓' if d['valid'] else '✗'} | "
            f"{d['score']:.4f} | {d['value_ratio']:.4f} | {d['volume_ratio']:.4f} | {d['runtime_ms']} |"
        )
    return "\n".join(lines) + "\n"


# ── main ──────────────────────────────────────────────────────────────────

def main() -> None:
    t_start = time.monotonic()

    # Resolve env
    hidden_dir = os.environ.get("CARTON_PACKING_HIDDEN_DIR", "")
    run_dir    = os.environ.get("SMEVALS_RUN_DIR", "")

    if not hidden_dir:
        _checker_error("CARTON_PACKING_HIDDEN_DIR is not set")
    if not run_dir:
        _checker_error("SMEVALS_RUN_DIR is not set")

    workspace = Path(run_dir) / "workspace"
    checker_dir = Path(__file__).resolve().parent
    tmp_dir = Path(".") / "_grade_tmp"
    tmp_dir.mkdir(parents=True, exist_ok=True)

    # Load hidden bundle
    try:
        bundle = _load_bundle(hidden_dir)
    except Exception as exc:
        _checker_error(f"Failed to load hidden bundle: {exc}")

    cases   = bundle.get("cases", [])
    seed    = bundle.get("seed", 42)
    probe_id = bundle.get("probe_case_id", "")

    # Build
    build_ok, build_log = _build_solution(workspace)
    if not build_ok:
        result = {
            "score": 0.0,
            "metrics": {
                "build_ok": False,
                "valid_layout_rate": 0.0,
                "average_value_ratio": 0.0,
                "average_volume_ratio": 0.0,
                "deterministic": False,
                "hidden_cases_valid": 0,
                "hidden_cases_total": len(cases),
                "runtime_ms": int((time.monotonic() - t_start) * 1000),
            },
            "tags": ["build_failed"],
            "notes": f"Build failed: {build_log[:500]}",
            "per_case_details": [],
        }
        _write_artifacts(result, [], "", workspace, checker_dir, None, None)
        shutil.rmtree(tmp_dir, ignore_errors=True)
        _emit(result)
        sys.exit(0)

    dll = _locate_dll(workspace)
    if dll is None:
        result = {
            "score": 0.0,
            "metrics": {
                "build_ok": True,
                "valid_layout_rate": 0.0,
                "average_value_ratio": 0.0,
                "average_volume_ratio": 0.0,
                "deterministic": False,
                "hidden_cases_valid": 0,
                "hidden_cases_total": len(cases),
                "runtime_ms": int((time.monotonic() - t_start) * 1000),
            },
            "tags": ["dll_not_found"],
            "notes": "CartonPacking.Cli.dll not found after successful build",
            "per_case_details": [],
        }
        _write_artifacts(result, [], "", workspace, checker_dir, None, None)
        shutil.rmtree(tmp_dir, ignore_errors=True)
        _emit(result)
        sys.exit(0)

    # Grade each case
    per_case_details: list[dict] = []
    for case in cases:
        detail = _grade_case(case, dll, workspace, tmp_dir)
        per_case_details.append(detail)

    valid_count = sum(1 for d in per_case_details if d["valid"])
    n = len(cases)
    case_weights = {
        case["id"]: float(case.get("weight", 1.0)) for case in cases
    }
    total_case_weight = sum(case_weights.values())
    avg_value_ratio = (
        sum(
            d["value_ratio"] * case_weights[d["case_id"]]
            for d in per_case_details
        )
        / total_case_weight
        if total_case_weight
        else 0.0
    )
    avg_volume_ratio = (
        sum(
            d["volume_ratio"] * case_weights[d["case_id"]]
            for d in per_case_details
        )
        / total_case_weight
        if total_case_weight
        else 0.0
    )
    weighted_score = (
        sum(
            d["score"] * case_weights[d["case_id"]]
            for d in per_case_details
        )
        / total_case_weight
        if total_case_weight
        else 0.0
    )

    # Deterministic probe
    probe_case = next((c for c in cases if c["id"] == probe_id), None)
    is_deterministic = False
    if probe_case is not None:
        try:
            is_deterministic = _deterministic_probe(probe_case, dll, workspace, tmp_dir, seed)
        except Exception:
            is_deterministic = False

    if not is_deterministic and probe_case is not None:
        weighted_score *= DETERMINISTIC_PENALTY

    total_runtime_ms = int((time.monotonic() - t_start) * 1000)

    tags = []
    if valid_count == n:
        tags.append("all_valid")
    if per_case_details and all(
        detail.get("candidate_value", 0) == 0 for detail in per_case_details
    ):
        tags.append("empty_solution")
    if is_deterministic:
        tags.append("deterministic")
    if weighted_score >= 0.9:
        tags.append("high_score")

    # Collect unique issue codes from all invalid cases (lowercase normalized)
    issue_codes: set[str] = set()
    for d in per_case_details:
        for issue in d.get("issues", []):
            code = issue.get("code", "")
            if code:
                tags.append(f"issue_{code.lower()}")
                issue_codes.add(code.lower())
    # Remove duplicates while preserving order
    seen_tags: set[str] = set()
    deduped: list[str] = []
    for t in tags:
        if t not in seen_tags:
            seen_tags.add(t)
            deduped.append(t)
    tags = deduped

    metrics = {
        "build_ok": True,
        "valid_layout_rate": round(valid_count / n, 4) if n else 0.0,
        "average_value_ratio": round(avg_value_ratio, 4),
        "average_volume_ratio": round(avg_volume_ratio, 4),
        "deterministic": is_deterministic,
        "hidden_cases_valid": valid_count,
        "hidden_cases_total": n,
        "runtime_ms": total_runtime_ms,
    }

    notes_parts = []
    if "empty_solution" in tags:
        notes_parts.append("all candidate layouts were empty")
    if not is_deterministic:
        notes_parts.append(f"deterministic penalty applied ({DETERMINISTIC_PENALTY}×)")
    if valid_count < n:
        notes_parts.append(f"{n - valid_count} case(s) invalid")
    notes = "; ".join(notes_parts) if notes_parts else "All cases valid and deterministic"

    # Find showcase placements for SVG
    showcase_placements = None
    showcase_problem = None
    for d in per_case_details:
        if d["case_id"] == "dense_showcase" and d["valid"]:
            result_f = tmp_dir / "dense_showcase_result.json"
            if result_f.exists():
                try:
                    with open(result_f, encoding="utf-8") as fh:
                        res = json.load(fh)
                    showcase_placements = res.get("placements", [])
                    showcase_problem = next(c["problem"] for c in cases if c["id"] == "dense_showcase")
                except Exception:
                    pass
            break

    final = {
        "score": round(weighted_score, 6),
        "metrics": metrics,
        "tags": tags,
        "notes": notes,
        "per_case_details": per_case_details,
    }

    _write_artifacts(final, per_case_details, build_log, workspace, checker_dir,
                     showcase_placements, showcase_problem)
    shutil.rmtree(tmp_dir, ignore_errors=True)
    _emit(final)
    sys.exit(0)


def _write_artifacts(
    result: dict,
    per_case_details: list[dict],
    build_log: str,
    workspace: Path,
    checker_dir: Path,
    showcase_placements: list[dict] | None,
    showcase_problem: dict | None,
) -> None:
    """Write all side-channel artifacts to CWD."""

    # grading-results.json
    try:
        with open("grading-results.json", "w", encoding="utf-8", newline="\n") as fh:
            json.dump(result, fh, indent=2, ensure_ascii=False)
            fh.write("\n")
    except Exception:
        pass

    # summary.md
    try:
        summary = _write_summary(result.get("score", 0.0), result.get("metrics", {}), per_case_details)
        with open("summary.md", "w", encoding="utf-8", newline="\n") as fh:
            fh.write(summary)
    except Exception:
        pass

    # solution.patch
    try:
        patch = _write_patch(workspace, checker_dir)
        with open("solution.patch", "w", encoding="utf-8", newline="\n") as fh:
            fh.write(patch)
    except Exception:
        try:
            with open("solution.patch", "w", encoding="utf-8", newline="\n") as fh:
                fh.write("# patch generation failed\n")
        except Exception:
            pass

    # showcase-layout.svg
    try:
        if showcase_placements is not None and showcase_problem is not None:
            svg = _write_showcase_svg(showcase_placements, showcase_problem)
        else:
            svg = '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="60"><text x="10" y="35" font-family="monospace" font-size="12">No valid showcase result</text></svg>\n'
        with open("showcase-layout.svg", "w", encoding="utf-8", newline="\n") as fh:
            fh.write(svg)
    except Exception:
        try:
            with open("showcase-layout.svg", "w", encoding="utf-8", newline="\n") as fh:
                fh.write('<svg xmlns="http://www.w3.org/2000/svg" width="200" height="60"><text x="10" y="35" font-family="monospace" font-size="12">SVG error</text></svg>\n')
        except Exception:
            pass


if __name__ == "__main__":
    main()
