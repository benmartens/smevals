#!/usr/bin/env python3
"""Generate bounded hidden route-planning cases and exact references."""

from __future__ import annotations

import argparse
import random
import secrets
import sys
from pathlib import Path


CHECKERS = Path(__file__).resolve().parent.parent / "checkers"
if str(CHECKERS) not in sys.path:
    sys.path.insert(0, str(CHECKERS))

from route_validation import dump_json, reference_solve, validate


CASE_WEIGHTS = {
    "skills": 0.5,
    "time_windows": 1.5,
    "clustering": 2.5,
    "value_trap": 3.0,
    "waiting": 1.0,
    "asymmetric_travel": 2.0,
    "cross_technician": 3.0,
    "showcase": 1.5,
}


def _matrix(
    locations: list[str],
    default: int,
    overrides: list[tuple[str, str, int]],
) -> dict[str, dict[str, int]]:
    matrix = {
        origin: {
            destination: (0 if origin == destination else default)
            for destination in locations
        }
        for origin in locations
    }
    for origin, destination, minutes in overrides:
        matrix[origin][destination] = minutes
    return matrix


def _problem(
    depot: str,
    locations: list[str],
    technicians: list[dict],
    jobs: list[dict],
    *,
    default: int,
    overrides: list[tuple[str, str, int]],
) -> dict:
    return {
        "depot": depot,
        "travelTimes": _matrix(
            [depot, *locations], default, overrides
        ),
        "technicians": technicians,
        "jobs": jobs,
    }


def _job(
    job_id: str,
    location: str,
    skills: list[str],
    duration: int,
    window_start: int,
    window_end: int,
    value: int,
) -> dict:
    return {
        "id": job_id,
        "location": location,
        "requiredSkills": skills,
        "duration": duration,
        "windowStart": window_start,
        "windowEnd": window_end,
        "value": value,
    }


def build_cases(rng: random.Random) -> list[dict]:
    cases: list[dict] = []

    skills_problem = _problem(
        "hub",
        ["panel", "sink", "plant"],
        [
            {
                "id": "electric",
                "skills": ["electrical"],
                "shiftStart": 0,
                "shiftEnd": 130,
            },
            {
                "id": "multi",
                "skills": ["electrical", "plumbing"],
                "shiftStart": 0,
                "shiftEnd": 130,
            },
            {
                "id": "plumber",
                "skills": ["plumbing"],
                "shiftStart": 0,
                "shiftEnd": 130,
            },
        ],
        [
            _job("breaker", "panel", ["electrical"], 18, 0, 100, 16),
            _job("leak", "sink", ["plumbing"], 20, 0, 100, 17),
            _job(
                "pump",
                "plant",
                ["electrical", "plumbing"],
                25,
                10,
                115,
                25 + rng.randint(0, 3),
            ),
        ],
        default=18,
        overrides=[
            ("hub", "panel", 8),
            ("panel", "hub", 9),
            ("hub", "sink", 7),
            ("sink", "hub", 8),
            ("hub", "plant", 12),
            ("plant", "hub", 11),
        ],
    )
    cases.append(
        {
            "id": "skills",
            "description": "Single- and multi-skill assignment",
            "tags": ["skills"],
            "problem": skills_problem,
        }
    )

    early_value = 12 + rng.randint(0, 3)
    windows_problem = _problem(
        "depot",
        ["early-site", "late-site"],
        [
            {
                "id": "tech",
                "skills": ["repair"],
                "shiftStart": 0,
                "shiftEnd": 100,
            }
        ],
        [
            _job("late", "late-site", ["repair"], 15, 45, 80, 18),
            _job("early", "early-site", ["repair"], 12, 0, 30, early_value),
        ],
        default=25,
        overrides=[
            ("depot", "early-site", 6),
            ("early-site", "late-site", 8),
            ("late-site", "depot", 7),
            ("depot", "late-site", 8),
            ("late-site", "early-site", 22),
            ("early-site", "depot", 6),
        ],
    )
    cases.append(
        {
            "id": "time_windows",
            "description": "Early job must precede a later-window job",
            "tags": ["time_windows", "ordering"],
            "problem": windows_problem,
        }
    )

    cluster_value = 8 + rng.randint(0, 2)
    cluster_problem = _problem(
        "depot",
        ["far", "c1", "c2", "c3"],
        [
            {
                "id": "tech",
                "skills": ["inspect"],
                "shiftStart": 0,
                "shiftEnd": 62,
            }
        ],
        [
            _job("far-first", "far", ["inspect"], 10, 0, 55, 20),
            _job("cluster-1", "c1", ["inspect"], 9, 0, 55, cluster_value),
            _job("cluster-2", "c2", ["inspect"], 9, 0, 55, cluster_value),
            _job("cluster-3", "c3", ["inspect"], 9, 0, 55, cluster_value),
        ],
        default=30,
        overrides=[
            ("depot", "far", 25),
            ("far", "depot", 25),
            ("depot", "c1", 5),
            ("depot", "c2", 6),
            ("depot", "c3", 6),
            ("c1", "c2", 2),
            ("c2", "c3", 2),
            ("c3", "depot", 5),
            ("c1", "depot", 5),
            ("c2", "depot", 5),
            ("c2", "c1", 2),
            ("c3", "c2", 2),
            ("c3", "c1", 3),
            ("c1", "c3", 3),
        ],
    )
    cases.append(
        {
            "id": "clustering",
            "description": "A compact cluster beats one distant job",
            "tags": ["clustering", "value_tradeoff"],
            "problem": cluster_problem,
        }
    )

    pair_value = 11 + rng.randint(0, 2)
    value_problem = _problem(
        "base",
        ["large", "left", "right"],
        [
            {
                "id": "tech",
                "skills": ["repair"],
                "shiftStart": 0,
                "shiftEnd": 50,
            }
        ],
        [
            _job("large-first", "large", ["repair"], 22, 0, 45, 19),
            _job("small-left", "left", ["repair"], 7, 0, 40, pair_value),
            _job("small-right", "right", ["repair"], 7, 0, 40, pair_value),
        ],
        default=24,
        overrides=[
            ("base", "large", 11),
            ("large", "base", 11),
            ("base", "left", 5),
            ("left", "right", 4),
            ("right", "base", 5),
            ("base", "right", 5),
            ("right", "left", 4),
            ("left", "base", 5),
        ],
    )
    cases.append(
        {
            "id": "value_trap",
            "description": "Two moderate jobs beat one high individual value",
            "tags": ["value_trap"],
            "problem": value_problem,
        }
    )

    waiting_problem = _problem(
        "yard",
        ["north", "south", "east"],
        [
            {
                "id": "tech",
                "skills": ["inspect"],
                "shiftStart": 10,
                "shiftEnd": 150,
            }
        ],
        [
            _job("morning", "north", ["inspect"], 12, 20, 55, 9),
            _job("appointment", "south", ["inspect"], 18, 85, 115, 18),
            _job("after", "east", ["inspect"], 12, 105, 135, 12),
        ],
        default=20,
        overrides=[
            ("yard", "north", 7),
            ("north", "south", 8),
            ("south", "east", 6),
            ("east", "yard", 8),
            ("yard", "south", 13),
            ("south", "yard", 10),
            ("yard", "east", 10),
            ("north", "yard", 8),
            ("east", "south", 6),
            ("south", "north", 9),
            ("east", "north", 10),
            ("north", "east", 10),
        ],
    )
    cases.append(
        {
            "id": "waiting",
            "description": "Waiting is required between appointments",
            "tags": ["waiting", "time_windows"],
            "problem": waiting_problem,
        }
    )

    asymmetric_problem = _problem(
        "hub",
        ["one-way-a", "one-way-b"],
        [
            {
                "id": "tech",
                "skills": ["service"],
                "shiftStart": 0,
                "shiftEnd": 58,
            }
        ],
        [
            _job("b-first-in-input", "one-way-b", ["service"], 8, 0, 45, 13),
            _job("a-second-in-input", "one-way-a", ["service"], 8, 0, 45, 13),
        ],
        default=25,
        overrides=[
            ("hub", "one-way-a", 5),
            ("one-way-a", "one-way-b", 3),
            ("one-way-b", "hub", 5),
            ("hub", "one-way-b", 4),
            ("one-way-b", "one-way-a", 30),
            ("one-way-a", "hub", 20),
        ],
    )
    cases.append(
        {
            "id": "asymmetric_travel",
            "description": "Directed travel makes only one ordering efficient",
            "tags": ["asymmetric_travel", "ordering"],
            "problem": asymmetric_problem,
        }
    )

    cross_problem = _problem(
        "dispatch",
        ["electric-site", "pipe-site"],
        [
            {
                "id": "a-generalist",
                "skills": ["electrical", "plumbing"],
                "shiftStart": 0,
                "shiftEnd": 48,
            },
            {
                "id": "b-plumber",
                "skills": ["plumbing"],
                "shiftStart": 0,
                "shiftEnd": 48,
            },
        ],
        [
            _job("plumbing-first", "pipe-site", ["plumbing"], 18, 0, 42, 15),
            _job(
                "electrical-only",
                "electric-site",
                ["electrical"],
                18,
                0,
                42,
                22,
            ),
        ],
        default=22,
        overrides=[
            ("dispatch", "pipe-site", 9),
            ("pipe-site", "dispatch", 9),
            ("dispatch", "electric-site", 9),
            ("electric-site", "dispatch", 9),
        ],
    )
    cases.append(
        {
            "id": "cross_technician",
            "description": "Reserve the scarce multi-skilled technician",
            "tags": ["cross_technician", "skills"],
            "problem": cross_problem,
        }
    )

    showcase_problem = _problem(
        "central",
        ["n1", "n2", "s1", "s2", "remote"],
        [
            {
                "id": "alpha",
                "skills": ["electrical", "inspect"],
                "shiftStart": 0,
                "shiftEnd": 145,
            },
            {
                "id": "beta",
                "skills": ["plumbing", "inspect"],
                "shiftStart": 5,
                "shiftEnd": 150,
            },
            {
                "id": "gamma",
                "skills": ["electrical", "plumbing"],
                "shiftStart": 0,
                "shiftEnd": 135,
            },
        ],
        [
            _job("n-panel", "n1", ["electrical"], 16, 0, 70, 18),
            _job("n-inspect", "n2", ["inspect"], 12, 20, 95, 12),
            _job("s-leak", "s1", ["plumbing"], 20, 0, 80, 19),
            _job("s-audit", "s2", ["inspect"], 14, 60, 125, 14),
            _job(
                "remote-pump",
                "remote",
                ["electrical", "plumbing"],
                24,
                30,
                120,
                27 + rng.randint(0, 4),
            ),
            _job("late-panel", "n1", ["electrical"], 15, 85, 130, 16),
        ],
        default=22,
        overrides=[
            ("central", "n1", 8),
            ("n1", "n2", 4),
            ("n2", "central", 9),
            ("central", "s1", 7),
            ("s1", "s2", 4),
            ("s2", "central", 8),
            ("central", "remote", 18),
            ("remote", "central", 16),
            ("n2", "remote", 12),
            ("remote", "n1", 13),
            ("s2", "remote", 11),
            ("remote", "s1", 12),
            ("n1", "central", 8),
            ("s1", "central", 7),
        ],
    )
    cases.append(
        {
            "id": "showcase",
            "description": "Mixed assignment, windows, clusters, and skills",
            "tags": [
                "skills",
                "time_windows",
                "clustering",
                "cross_technician",
            ],
            "problem": showcase_problem,
        }
    )

    completed = []
    for case in cases:
        plan = reference_solve(case["problem"])
        report = validate(case["problem"], plan)
        assert report["valid"], (case["id"], report["issues"])
        completed.append(
            {
                **case,
                "weight": CASE_WEIGHTS[case["id"]],
                "reference": {
                    "servedValue": report["metrics"]["served_value"],
                    "totalTravel": report["metrics"]["total_travel"],
                    "routes": plan["routes"],
                },
            }
        )
    return completed


def generate(output_dir: str, seed: int | None = None) -> Path:
    if seed is None:
        seed = secrets.randbits(31)
    cases = build_cases(random.Random(seed))
    bundle = {
        "schema_version": 1,
        "seed": seed,
        "cases": cases,
        "probe_case_id": "showcase",
    }
    output = Path(output_dir)
    output.mkdir(parents=True, exist_ok=True)
    path = output / "hidden_cases.json"
    dump_json(bundle, str(path))
    print(f"Generated {len(cases)} cases (seed={seed}) -> {path}")
    return path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--seed", type=int)
    args = parser.parse_args()
    generate(args.output, args.seed)


if __name__ == "__main__":
    main()
