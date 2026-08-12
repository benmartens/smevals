#!/usr/bin/env python3
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import tempfile
from pathlib import Path

BENCHMARK_DIR = Path(__file__).resolve().parent
CHECKER_DIR = BENCHMARK_DIR.parent / "checkers"
for path in (BENCHMARK_DIR, CHECKER_DIR):
    if str(path) not in sys.path:
        sys.path.insert(0, str(path))

import query_validation as qv


def _load_hidden_generator():
    path = BENCHMARK_DIR / "generate_hidden_cases.py"
    spec = importlib.util.spec_from_file_location(
        "query_optimizer_generate_hidden_cases",
        path,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


generate_hidden_cases = _load_hidden_generator()


def first_valid(problem: dict) -> dict:
    ids = sorted(table["id"] for table in problem["tables"])
    nodes = {
        table_id: {"operator": "tableScan", "tableId": table_id}
        for table_id in ids
    }
    groups = {table_id: {table_id} for table_id in ids}
    while len(nodes) > 1:
        keys = sorted(nodes)
        chosen = None
        for left_key in keys:
            for right_key in keys:
                if left_key >= right_key:
                    continue
                if qv.has_crossing_join(problem, groups[left_key], groups[right_key]):
                    chosen = (left_key, right_key)
                    break
            if chosen:
                break
        if chosen is None:
            return {"plan": None}
        left_key, right_key = chosen
        new_key = min(left_key, right_key)
        nodes[new_key] = {
            "operator": "hashJoin",
            "left": nodes[left_key],
            "right": nodes[right_key],
        }
        groups[new_key] = groups[left_key] | groups[right_key]
        del nodes[right_key]
        del groups[right_key]
    return {"plan": next(iter(nodes.values()))}


def greedy(problem: dict) -> dict:
    tables = qv.table_map(problem)
    leaf_plans = {}
    for table_id, table in tables.items():
        choices = [{"operator": "tableScan", "tableId": table_id}]
        for item in table.get("indexes", []):
            choices.append(
                {
                    "operator": "indexSeek",
                    "tableId": table_id,
                    "indexColumn": item["column"],
                }
            )
        valid = []
        for choice in choices:
            report = qv.validate(
                {
                    **problem,
                    "tables": [table],
                    "predicates": [
                        predicate
                        for predicate in problem["predicates"]
                        if predicate["tableId"] == table_id
                    ],
                    "joins": [],
                },
                {"plan": choice},
            )
            if report["valid"]:
                valid.append((report["metrics"]["total_cost"], qv.canonical_json(choice), choice))
        leaf_plans[table_id] = min(valid)[2]

    current_id = min(
        tables,
        key=lambda table_id: (
            qv.estimate_filtered_rows(problem, tables[table_id]),
            table_id,
        ),
    )
    current_tables = {current_id}
    current_plan = leaf_plans[current_id]
    remaining = set(tables) - current_tables
    while remaining:
        choices = []
        for table_id in sorted(remaining):
            if not qv.has_crossing_join(problem, current_tables, {table_id}):
                continue
            left_plan, right_plan = (
                (current_plan, leaf_plans[table_id])
                if min(current_tables) < table_id
                else (leaf_plans[table_id], current_plan)
            )
            for operator in qv.JOIN_OPERATORS:
                plan = {
                    "operator": operator,
                    "left": left_plan,
                    "right": right_plan,
                }
                subset = current_tables | {table_id}
                subproblem = {
                    **problem,
                    "tables": [tables[item] for item in sorted(subset)],
                    "predicates": [
                        predicate
                        for predicate in problem["predicates"]
                        if predicate["tableId"] in subset
                    ],
                    "joins": [
                        join
                        for join in problem["joins"]
                        if join["leftTable"] in subset and join["rightTable"] in subset
                    ],
                }
                report = qv.validate(subproblem, {"plan": plan})
                if report["valid"]:
                    choices.append(
                        (
                            report["metrics"]["total_cost"],
                            table_id,
                            operator,
                            plan,
                        )
                    )
        if not choices:
            return {"plan": None}
        _, table_id, _, current_plan = min(choices)
        current_tables.add(table_id)
        remaining.remove(table_id)
    return {"plan": current_plan}


def evaluate(bundle: dict, strategy) -> dict:
    weighted = 0.0
    total_weight = 0.0
    cases = []
    for case in bundle["cases"]:
        result = strategy(case)
        report = qv.validate(case["problem"], result)
        if report["valid"]:
            cost = report["metrics"]["total_cost"]
            score = qv.cost_ratio(cost, case["reference"]["cost"])
        else:
            cost = None
            score = 0.0
        weight = float(case["weight"])
        weighted += score * weight
        total_weight += weight
        cases.append(
            {
                "id": case["id"],
                "valid": report["valid"],
                "cost": cost,
                "score": round(score, 6),
            }
        )
    return {"score": round(weighted / total_weight, 6), "cases": cases}


def calibrate(seed: int) -> dict:
    with tempfile.TemporaryDirectory(prefix="query-optimizer-calibration-") as temp:
        path = generate_hidden_cases.generate(temp, seed)
        bundle = json.loads(path.read_text(encoding="utf-8"))
    results = {
        "reference": evaluate(
            bundle,
            lambda case: {"plan": case["reference"]["plan"]},
        ),
        "empty": evaluate(bundle, lambda case: {"plan": None}),
        "first_valid": evaluate(bundle, lambda case: first_valid(case["problem"])),
        "greedy": evaluate(bundle, lambda case: greedy(case["problem"])),
    }
    assert results["reference"]["score"] == 1.0
    assert results["empty"]["score"] == 0.0
    assert 0.0 < results["first_valid"]["score"] < results["greedy"]["score"] < 1.0
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
