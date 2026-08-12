#!/usr/bin/env python3
"""Calibrate route-planner scoring against deterministic local strategies."""

from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import random
import sys
from pathlib import Path


BENCHMARK = Path(__file__).resolve().parent
CHECKERS = BENCHMARK.parent / "checkers"
for path in (BENCHMARK, CHECKERS):
    if str(path) not in sys.path:
        sys.path.insert(0, str(path))

import route_validation


def _load_hidden_generator():
    path = BENCHMARK / "generate_hidden_cases.py"
    spec = importlib.util.spec_from_file_location(
        "field_service_route_generate_hidden_cases",
        path,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


generate_hidden_cases = _load_hidden_generator()


def evaluate(cases: list[dict], result_factory) -> dict:
    details = []
    weighted = 0.0
    total_weight = 0.0
    for case in cases:
        result = result_factory(case)
        report = route_validation.validate(case["problem"], result)
        reference = case["reference"]
        if report["valid"]:
            candidate_value = report["metrics"]["served_value"]
            candidate_travel = report["metrics"]["total_travel"]
            value_ratio = route_validation.capped_value_ratio(
                candidate_value, reference["servedValue"]
            )
            travel_ratio = route_validation.travel_quality(
                candidate_travel,
                reference["totalTravel"],
                candidate_value,
                reference["servedValue"],
            )
            score = route_validation.case_score(value_ratio, travel_ratio)
        else:
            candidate_value = candidate_travel = 0
            value_ratio = travel_ratio = score = 0.0
        weight = float(case.get("weight", 1.0))
        weighted += score * weight
        total_weight += weight
        details.append(
            {
                "id": case["id"],
                "valid": report["valid"],
                "score": round(score, 6),
                "servedValue": candidate_value,
                "totalTravel": candidate_travel,
                "valueRatio": round(value_ratio, 6),
                "travelRatio": round(travel_ratio, 6),
                "issues": report["issues"],
            }
        )
    return {
        "score": round(weighted / total_weight, 6),
        "cases": details,
    }


def _empty(case: dict) -> dict:
    return route_validation.canonical_routes(case["problem"], {})


def _invalid(case: dict) -> dict:
    result = _empty(case)
    if len(result["routes"]) >= 2:
        result["routes"].reverse()
    else:
        result["routes"].append(
            {"technicianId": "unknown", "jobIds": []}
        )
    return result


def calibrate(seed: int) -> dict:
    cases = generate_hidden_cases.build_cases(random.Random(seed))
    strategies = {
        "reference": lambda case: {
            "routes": copy.deepcopy(case["reference"]["routes"])
        },
        "invalid": _invalid,
        "empty": _empty,
        "simple_first_feasible": lambda case: route_validation.first_feasible(
            case["problem"]
        ),
        "strong_beam": lambda case: route_validation.beam_solve(
            case["problem"], width=12
        ),
    }
    results = {
        name: evaluate(cases, factory)
        for name, factory in strategies.items()
    }
    assert results["reference"]["score"] == 1.0
    assert results["invalid"]["score"] == 0.0
    assert results["empty"]["score"] == 0.0
    assert (
        results["simple_first_feasible"]["score"]
        < results["strong_beam"]["score"]
    )
    assert results["simple_first_feasible"]["score"] < 0.7
    assert results["strong_beam"]["score"] > 0.7
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
