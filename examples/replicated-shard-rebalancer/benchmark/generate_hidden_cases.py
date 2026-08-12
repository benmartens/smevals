#!/usr/bin/env python3
from __future__ import annotations

import argparse
import random
import secrets
import sys
from pathlib import Path

CHECKER_DIR = Path(__file__).resolve().parent.parent / "checkers"
if str(CHECKER_DIR) not in sys.path:
    sys.path.insert(0, str(CHECKER_DIR))

from rebalancer_validation import (
    dump_json,
    reference_rebalance,
    serialize_metrics,
    validate,
)


CASE_WEIGHTS = {
    "overloaded_pair": 2.0,
    "uneven_shard_sizes": 3.0,
    "zone_scarcity": 2.0,
    "maintenance_exclusions": 2.5,
    "three_zone_anti_affinity": 3.0,
    "movement_balance_tradeoff": 4.0,
    "coordinated_swaps": 4.0,
    "movement_tiebreak": 2.5,
}


def node(node_id: str, zone: str, capacity: int) -> dict:
    return {"id": node_id, "zone": zone, "capacity": capacity}


def shard(shard_id: str, size: int, replicas: int) -> dict:
    return {
        "id": shard_id,
        "size": size,
        "replicationFactor": replicas,
    }


def placement(shard_id: str, *node_ids: str) -> dict:
    return {"shardId": shard_id, "nodeIds": sorted(node_ids)}


def exclusion(shard_id: str, node_id: str) -> dict:
    return {"shardId": shard_id, "nodeId": node_id}


def problem(
    nodes: list[dict],
    shards: list[dict],
    current: list[dict],
    exclusions: list[dict] | None = None,
) -> dict:
    return {
        "nodes": nodes,
        "shards": shards,
        "currentPlacements": current,
        "exclusions": exclusions or [],
    }


def build_cases(rng: random.Random) -> list[dict]:
    small = rng.choice([2, 3])
    medium = rng.choice([4, 5])
    cases = [
        {
            "id": "overloaded_pair",
            "description": "Replicas begin concentrated on one node pair",
            "category": "overload",
            "problem": problem(
                [
                    node("a1", "a", 12 + small),
                    node("a2", "a", 12 + small),
                    node("b1", "b", 12 + small),
                    node("b2", "b", 12 + small),
                ],
                [
                    shard("s1", 4, 2),
                    shard("s2", 4, 2),
                    shard("s3", 4, 2),
                    shard("s4", 4, 2),
                ],
                [
                    placement("s1", "a1", "b1"),
                    placement("s2", "a1", "b1"),
                    placement("s3", "a1", "b1"),
                    placement("s4", "a1", "b1"),
                ],
            ),
        },
        {
            "id": "uneven_shard_sizes",
            "description": "Large and small shards must be composed globally",
            "category": "uneven shard sizes",
            "problem": problem(
                [
                    node("n1", "red", 15),
                    node("n2", "red", 13),
                    node("n3", "blue", 14),
                    node("n4", "blue", 12),
                    node("n5", "green", 10),
                ],
                [
                    shard("large", 7, 2),
                    shard("medium", medium, 2),
                    shard("small-a", small, 2),
                    shard("small-b", 3, 2),
                ],
                [
                    placement("large", "n1", "n3"),
                    placement("medium", "n1", "n3"),
                    placement("small-a", "n2", "n4"),
                    placement("small-b", "n2", "n4"),
                ],
            ),
        },
        {
            "id": "zone_scarcity",
            "description": "Three replicas have only two eligible zones",
            "category": "zone scarcity",
            "problem": problem(
                [
                    node("a1", "a", 12),
                    node("a2", "a", 12),
                    node("a3", "a", 10),
                    node("b1", "b", 12),
                    node("b2", "b", 10),
                ],
                [
                    shard("catalog", 4, 3),
                    shard("events", 3, 3),
                    shard("profiles", 2, 3),
                ],
                [
                    placement("catalog", "a1", "a2", "b1"),
                    placement("events", "a1", "a2", "b1"),
                    placement("profiles", "a1", "a3", "b2"),
                ],
            ),
        },
        {
            "id": "maintenance_exclusions",
            "description": "Per-shard exclusions force selective evacuation",
            "category": "exclusions",
            "problem": problem(
                [
                    node("e1", "east", 12),
                    node("e2", "east", 12),
                    node("w1", "west", 12),
                    node("w2", "west", 12),
                    node("c1", "central", 12),
                ],
                [
                    shard("alpha", 5, 2),
                    shard("beta", 4, 2),
                    shard("gamma", 3, 2),
                    shard("delta", 2, 2),
                ],
                [
                    placement("alpha", "e1", "w1"),
                    placement("beta", "e1", "w1"),
                    placement("gamma", "e2", "w2"),
                    placement("delta", "e2", "w2"),
                ],
                [
                    exclusion("alpha", "e1"),
                    exclusion("beta", "w1"),
                    exclusion("gamma", "c1"),
                    exclusion("delta", "e2"),
                ],
            ),
        },
        {
            "id": "three_zone_anti_affinity",
            "description": "Each triple replica shard must span all three zones",
            "category": "anti-affinity",
            "problem": problem(
                [
                    node("a1", "a", 11),
                    node("a2", "a", 11),
                    node("b1", "b", 11),
                    node("b2", "b", 11),
                    node("c1", "c", 11),
                    node("c2", "c", 11),
                ],
                [
                    shard("p", 5, 3),
                    shard("q", 4, 3),
                    shard("r", 3, 3),
                    shard("s", 2, 3),
                ],
                [
                    placement("p", "a1", "b1", "c1"),
                    placement("q", "a1", "b1", "c1"),
                    placement("r", "a2", "b2", "c2"),
                    placement("s", "a2", "b2", "c2"),
                ],
            ),
        },
        {
            "id": "movement_balance_tradeoff",
            "description": "A feasible zero-move layout loses to better utilization",
            "category": "movement/balance tradeoff",
            "problem": problem(
                [
                    node("a1", "a", 18),
                    node("a2", "a", 10),
                    node("b1", "b", 18),
                    node("b2", "b", 10),
                ],
                [
                    shard("hot-a", 6, 2),
                    shard("hot-b", 6, 2),
                    shard("cold-a", 3, 2),
                    shard("cold-b", 3, 2),
                ],
                [
                    placement("hot-a", "a1", "b1"),
                    placement("hot-b", "a1", "b1"),
                    placement("cold-a", "a1", "b1"),
                    placement("cold-b", "a2", "b2"),
                ],
            ),
        },
        {
            "id": "coordinated_swaps",
            "description": "Large replicas require coordinated cross-zone swaps",
            "category": "coordinated swaps",
            "problem": problem(
                [
                    node("a1", "a", 10),
                    node("a2", "a", 10),
                    node("b1", "b", 10),
                    node("b2", "b", 10),
                ],
                [
                    shard("large-a", 6, 2),
                    shard("large-b", 6, 2),
                    shard("small-a", 4, 2),
                    shard("small-b", 4, 2),
                ],
                [
                    placement("large-a", "a1", "b1"),
                    placement("large-b", "a1", "b1"),
                    placement("small-a", "a2", "b2"),
                    placement("small-b", "a2", "b2"),
                ],
                [
                    exclusion("large-a", "b1"),
                    exclusion("large-b", "a1"),
                ],
            ),
        },
        {
            "id": "movement_tiebreak",
            "description": "Equal balance is resolved by bytes before replica count",
            "category": "movement tie-breaking",
            "problem": problem(
                [
                    node("a1", "a", 14),
                    node("a2", "a", 14),
                    node("b1", "b", 14),
                    node("b2", "b", 14),
                ],
                [
                    shard("large", 6, 2),
                    shard("medium", 4, 2),
                    shard("tiny-a", 2, 2),
                    shard("tiny-b", 2, 2),
                ],
                [
                    placement("large", "a1", "b1"),
                    placement("medium", "a2", "b2"),
                    placement("tiny-a", "a1", "b2"),
                    placement("tiny-b", "a2", "b1"),
                ],
            ),
        },
    ]

    for case in cases:
        reference = reference_rebalance(case["problem"])
        report = validate(case["problem"], reference)
        if not report["valid"]:
            raise AssertionError((case["id"], report["issues"]))
        case["reference"] = {
            "targetPlacements": reference["targetPlacements"],
            "metrics": serialize_metrics(report["metrics"]),
        }
        case["weight"] = CASE_WEIGHTS[case["id"]]
    return cases


def generate(output: str | Path, seed: int | None = None) -> Path:
    chosen_seed = secrets.randbits(63) if seed is None else seed
    bundle = {
        "schema_version": 1,
        "seed": chosen_seed,
        "cases": build_cases(random.Random(chosen_seed)),
        "probe_case_id": "coordinated_swaps",
        "showcase_case_id": "three_zone_anti_affinity",
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
    print(generate(args.output, args.seed))


if __name__ == "__main__":
    main()
