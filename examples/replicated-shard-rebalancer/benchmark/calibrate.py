#!/usr/bin/env python3
from __future__ import annotations

import argparse
import importlib.util
import json
import random
import sys
from pathlib import Path

BENCHMARK_DIR = Path(__file__).resolve().parent
CHECKER_DIR = BENCHMARK_DIR.parent / "checkers"
for path in (BENCHMARK_DIR, CHECKER_DIR):
    if str(path) not in sys.path:
        sys.path.insert(0, str(path))

import rebalancer_validation as rv


def _load_generator():
    path = BENCHMARK_DIR / "generate_hidden_cases.py"
    spec = importlib.util.spec_from_file_location(
        "replicated_shard_rebalancer_hidden_cases",
        path,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


generator = _load_generator()


def evaluate(cases: list[dict], strategy) -> dict:
    weighted = 0.0
    total_weight = 0.0
    details = []
    for case in cases:
        result = strategy(case)
        report = rv.validate(case["problem"], result)
        if report["valid"]:
            metrics = report["metrics"]
            candidate = (
                metrics["maximum_utilization"],
                metrics["utilization_spread"],
                metrics["moved_bytes"],
                metrics["moved_replica_count"],
            )
            score = rv.case_score(
                candidate,
                rv.deserialize_objective(case["reference"]),
            )
        else:
            score = 0.0
        weight = float(case["weight"])
        weighted += score * weight
        total_weight += weight
        details.append(
            {
                "id": case["id"],
                "valid": report["valid"],
                "score": round(score, 6),
            }
        )
    return {
        "score": round(weighted / total_weight, 6),
        "cases": details,
    }


def calibrate(seed: int) -> dict:
    cases = generator.build_cases(random.Random(seed))
    results = {
        "reference": evaluate(
            cases,
            lambda case: {
                "targetPlacements": case["reference"]["targetPlacements"]
            },
        ),
        "invalid": evaluate(
            cases,
            lambda _case: {"targetPlacements": []},
        ),
        "first_feasible": evaluate(
            cases,
            lambda case: rv.first_feasible(case["problem"]),
        ),
        "balance_greedy": evaluate(
            cases,
            lambda case: rv.balance_greedy(case["problem"]),
        ),
    }
    assert results["reference"]["score"] == 1.0
    assert results["invalid"]["score"] == 0.0
    assert (
        0.0
        < results["first_feasible"]["score"]
        < 0.7
        < results["balance_greedy"]["score"]
        < 1.0
    )
    return {"seed": seed, "results": results}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seed", type=int, default=8675309)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = calibrate(args.seed)
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8", newline="\n")
    print(text, end="")


if __name__ == "__main__":
    main()
