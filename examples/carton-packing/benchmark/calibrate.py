#!/usr/bin/env python3
from __future__ import annotations

import argparse
import copy
import json
import random
import sys
import tempfile
from pathlib import Path


BENCHMARK_DIR = Path(__file__).resolve().parent
CHECKER_DIR = BENCHMARK_DIR.parent / "checkers"
for path in (BENCHMARK_DIR, CHECKER_DIR):
    if str(path) not in sys.path:
        sys.path.insert(0, str(path))

import generate_hidden_cases
import packing_validation


def evaluate(bundle: dict, result_factory) -> dict:
    cases = []
    weighted_score = 0.0
    total_weight = 0.0
    for case in bundle["cases"]:
        result = result_factory(case)
        validation = packing_validation.validate(case["problem"], result)
        if validation["valid"]:
            value, volume = packing_validation.recompute_objective(
                case["problem"], result
            )
            value_ratio = packing_validation.capped_value_ratio(
                value, case["reference"]["value"]
            )
            volume_ratio = packing_validation.capped_volume_ratio(
                volume, case["reference"]["volume"]
            )
            score = packing_validation.case_score(value_ratio, volume_ratio)
        else:
            value = volume = 0
            value_ratio = volume_ratio = score = 0.0
        cases.append(
            {
                "id": case["id"],
                "weight": case.get("weight", 1.0),
                "valid": validation["valid"],
                "score": round(score, 6),
                "value": value,
                "volume": volume,
                "issues": validation["issues"],
            }
        )
        weight = float(case.get("weight", 1.0))
        weighted_score += score * weight
        total_weight += weight
    return {
        "score": round(weighted_score / total_weight, 6),
        "cases": cases,
    }


def greedy_result(case: dict, key) -> dict:
    cartons = sorted(case["problem"]["cartons"], key=key)
    return packing_validation._run_packer(
        case["problem"], cartons, "height_asc"
    )


def floor_first_fit(case: dict) -> dict:
    problem = case["problem"]
    container = problem["container"]
    placements = []
    packed_weight = 0
    for carton in sorted(
        problem["cartons"],
        key=lambda item: (-item["value"], item["id"]),
    ):
        orientations = packing_validation.valid_orientations(
            carton["width"],
            carton["depth"],
            carton["height"],
            carton.get("keepUpright", False),
        )
        for instance in range(carton["quantity"]):
            placed = False
            for width, depth, height in orientations:
                if packed_weight + carton["weight"] > container["maxWeight"]:
                    continue
                if height > container["height"]:
                    continue
                for y in range(container["depth"] - depth + 1):
                    for x in range(container["width"] - width + 1):
                        candidate = {
                            "cartonId": carton["id"],
                            "instance": instance,
                            "x": x,
                            "y": y,
                            "z": 0,
                            "width": width,
                            "depth": depth,
                            "height": height,
                        }
                        if any(
                            packing_validation.boxes_overlap(candidate, existing)
                            for existing in placements
                        ):
                            continue
                        placements.append(candidate)
                        packed_weight += carton["weight"]
                        placed = True
                        break
                    if placed:
                        break
                if placed:
                    break
    return {
        "placements": packing_validation.canonical_placements(placements)
    }


def invalid_result(case: dict) -> dict:
    result = copy.deepcopy(case["reference"])
    placements = result.get("placements", [])
    if placements:
        placements[0]["x"] = case["problem"]["container"]["width"]
    return {"placements": placements}


def calibrate(seed: int) -> dict:
    with tempfile.TemporaryDirectory(prefix="carton-packing-calibration-") as tmp:
        bundle_path = generate_hidden_cases.generate(tmp, seed)
        bundle = json.loads(bundle_path.read_text(encoding="utf-8"))

    strategies = {
        "reference": lambda case: {
            "placements": copy.deepcopy(case["reference"]["placements"])
        },
        "empty": lambda case: {"placements": []},
        "floor_first_fit": floor_first_fit,
        "value_greedy": lambda case: greedy_result(
            case, lambda carton: (-carton["value"], carton["id"])
        ),
        "density_greedy": lambda case: greedy_result(
            case,
            lambda carton: (
                -carton["value"]
                / (carton["width"] * carton["depth"] * carton["height"]),
                carton["id"],
            ),
        ),
        "invalid_bounds_mutant": invalid_result,
    }
    results = {
        name: evaluate(bundle, factory) for name, factory in strategies.items()
    }

    assert results["reference"]["score"] == 1.0
    assert results["empty"]["score"] == 0.0
    assert 0.0 < results["floor_first_fit"]["score"] < 1.0
    assert results["invalid_bounds_mutant"]["score"] < 1.0
    for result in results.values():
        assert 0.0 <= result["score"] <= 1.0

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
