#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import random
import secrets
import sys
from pathlib import Path

CHECKER_DIR = Path(__file__).resolve().parent.parent / "checkers"
if str(CHECKER_DIR) not in sys.path:
    sys.path.insert(0, str(CHECKER_DIR))

from query_validation import dump_json, objective, reference_optimize


CASE_WEIGHTS = {
    "selective_index": 0.25,
    "join_order_trap": 2.0,
    "memory_spill": 2.0,
    "star_schema": 3.0,
    "chain_eight": 3.0,
    "snowflake_ten": 4.0,
    "dense_twelve": 4.0,
}


def table(
    table_id: str,
    rows: int,
    scan: int,
    indexes: list[dict] | None = None,
) -> dict:
    return {
        "id": table_id,
        "rows": rows,
        "scanCostPerRow": scan,
        "indexes": indexes or [],
    }


def index(column: str, startup: int, lookup: int) -> dict:
    return {
        "column": column,
        "seekStartupCost": startup,
        "lookupCostPerRow": lookup,
    }


def predicate(
    table_id: str,
    column: str,
    selectivity: int,
    indexable: bool = True,
) -> dict:
    return {
        "tableId": table_id,
        "column": column,
        "selectivityPermille": selectivity,
        "indexable": indexable,
    }


def join(left: str, right: str, selectivity: int) -> dict:
    return {
        "leftTable": left,
        "rightTable": right,
        "selectivityPermille": selectivity,
    }


def _random_connected_problem(
    rng: random.Random,
    count: int,
    shape: str,
) -> dict:
    ids = [f"t{index:02d}" for index in range(count)]
    tables = []
    predicates = []
    joins = []
    for index_value, table_id in enumerate(ids):
        rows = rng.randint(2_000, 120_000) * (1 + index_value % 3)
        indexes = []
        if index_value % 2 == 0:
            indexes.append(index("filter", rng.randint(10, 80), rng.randint(1, 4)))
            predicates.append(
                predicate(
                    table_id,
                    "filter",
                    rng.choice([3, 8, 15, 40, 120, 400]),
                )
            )
        elif index_value % 3 == 0:
            predicates.append(
                predicate(
                    table_id,
                    "status",
                    rng.choice([50, 150, 500]),
                    indexable=False,
                )
            )
        tables.append(table(table_id, rows, rng.randint(2, 6), indexes))

    if shape == "star":
        for table_id in ids[1:]:
            joins.append(join(ids[0], table_id, rng.choice([1, 3, 8, 20, 80])))
    elif shape == "chain":
        for left, right in zip(ids, ids[1:]):
            joins.append(join(left, right, rng.choice([1, 5, 15, 60, 200])))
    else:
        for index_value in range(1, count):
            parent = ids[(index_value - 1) // 2]
            joins.append(join(parent, ids[index_value], rng.choice([1, 4, 12, 50, 150])))
        for _ in range(max(1, count // 3)):
            left_index = rng.randrange(count)
            right_index = rng.randrange(count)
            if left_index != right_index:
                edge = {ids[left_index], ids[right_index]}
                if all({item["leftTable"], item["rightTable"]} != edge for item in joins):
                    joins.append(join(ids[left_index], ids[right_index], rng.choice([2, 10, 40])))

    return {
        "memoryLimitRows": rng.choice([50, 200, 1_000, 5_000]),
        "tables": tables,
        "predicates": predicates,
        "joins": joins,
    }


def build_cases(rng: random.Random) -> list[dict]:
    cases = [
        {
            "id": "selective_index",
            "description": "A selective index is much cheaper than a full scan",
            "problem": {
                "memoryLimitRows": 100,
                "tables": [
                    table("events", rng.randint(40_000, 80_000), 4, [index("tenant", 25, 2)])
                ],
                "predicates": [predicate("events", "tenant", rng.randint(3, 12))],
                "joins": [],
            },
        },
        {
            "id": "join_order_trap",
            "description": "Joining the two largest inputs first creates an expensive intermediate",
            "problem": {
                "memoryLimitRows": 500,
                "tables": [
                    table("customers", 2_000, 3, [index("region", 20, 2)]),
                    table("orders", 160_000, 3),
                    table("regions", 25, 4),
                    table("returns", 50_000, 3, [index("flag", 15, 2)]),
                ],
                "predicates": [
                    predicate("customers", "region", 40),
                    predicate("returns", "flag", 10),
                ],
                "joins": [
                    join("customers", "orders", 8),
                    join("customers", "regions", 40),
                    join("orders", "returns", 2),
                ],
            },
        },
        {
            "id": "memory_spill",
            "description": "Hash joins spill under a tight memory budget",
            "problem": {
                "memoryLimitRows": 12,
                "tables": [
                    table("a", 900, 3),
                    table("b", 1_100, 3),
                    table("c", 80, 5, [index("kind", 10, 2)]),
                ],
                "predicates": [predicate("c", "kind", 25)],
                "joins": [join("a", "b", 8), join("b", "c", 20)],
            },
        },
        {
            "id": "star_schema",
            "description": "Six-table star with selective dimensions",
            "problem": _random_connected_problem(rng, 6, "star"),
        },
        {
            "id": "chain_eight",
            "description": "Eight-table chain with alternating selective leaves",
            "problem": _random_connected_problem(rng, 8, "chain"),
        },
        {
            "id": "snowflake_ten",
            "description": "Ten-table snowflake with extra join edges",
            "problem": _random_connected_problem(rng, 10, "snowflake"),
        },
        {
            "id": "dense_twelve",
            "description": "Twelve-table workload requiring broad join-order search",
            "problem": _random_connected_problem(rng, 12, "snowflake"),
        },
    ]
    for case in cases:
        reference = reference_optimize(case["problem"])
        case["reference"] = {
            "cost": objective(case["problem"], reference),
            "plan": reference["plan"],
        }
        case["weight"] = CASE_WEIGHTS[case["id"]]
    return cases


def generate(output: str | Path, seed: int | None = None) -> Path:
    chosen_seed = secrets.randbits(63) if seed is None else seed
    rng = random.Random(chosen_seed)
    bundle = {
        "schema_version": 1,
        "seed": chosen_seed,
        "cases": build_cases(rng),
        "probe_case_id": "snowflake_ten",
    }
    output_path = Path(output)
    output_path.mkdir(parents=True, exist_ok=True)
    bundle_path = output_path / "hidden_cases.json"
    dump_json(bundle, str(bundle_path))
    return bundle_path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--seed", type=int)
    args = parser.parse_args()
    path = generate(args.output, args.seed)
    print(path)


if __name__ == "__main__":
    main()
