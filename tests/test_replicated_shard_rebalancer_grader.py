from __future__ import annotations

import importlib.util
import itertools
import random
import sys
from fractions import Fraction
from pathlib import Path


ROOT = Path(__file__).parents[1]
CHECKERS = ROOT / "examples" / "replicated-shard-rebalancer" / "checkers"
BENCHMARK = ROOT / "examples" / "replicated-shard-rebalancer" / "benchmark"


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


rv = load_module(
    CHECKERS / "rebalancer_validation.py",
    "rebalancer_validation",
)
generator = load_module(
    BENCHMARK / "generate_hidden_cases.py",
    "replicated_shard_rebalancer_hidden_cases_tests",
)


SIMPLE = {
    "nodes": [
        {"id": "a", "zone": "z1", "capacity": 10},
        {"id": "b", "zone": "z1", "capacity": 10},
        {"id": "c", "zone": "z2", "capacity": 10},
        {"id": "d", "zone": "z2", "capacity": 10},
    ],
    "shards": [
        {"id": "s1", "size": 5, "replicationFactor": 2},
        {"id": "s2", "size": 5, "replicationFactor": 2},
    ],
    "currentPlacements": [
        {"shardId": "s1", "nodeIds": ["a", "c"]},
        {"shardId": "s2", "nodeIds": ["a", "c"]},
    ],
    "exclusions": [],
}


def result(s1: list[str], s2: list[str]) -> dict:
    return {
        "targetPlacements": [
            {"shardId": "s1", "nodeIds": s1},
            {"shardId": "s2", "nodeIds": s2},
        ]
    }


def test_reference_is_valid_and_exact_for_small_problem():
    reference = rv.reference_rebalance(SIMPLE)
    report = rv.validate(SIMPLE, reference)
    assert report["valid"], report["issues"]

    options = [
        rv.placement_options(SIMPLE, shard)
        for shard in SIMPLE["shards"]
    ]
    objectives = []
    for first, second in itertools.product(*options):
        candidate = result(list(first), list(second))
        candidate_report = rv.validate(SIMPLE, candidate)
        if candidate_report["valid"]:
            placements = {"s1": first, "s2": second}
            objectives.append(rv.objective_tuple(SIMPLE, placements))
    reference_placements = {
        item["shardId"]: tuple(item["nodeIds"])
        for item in reference["targetPlacements"]
    }
    assert rv.objective_tuple(SIMPLE, reference_placements) == min(objectives)


def test_validator_derives_load_and_movement_metrics():
    report = rv.validate(SIMPLE, result(["a", "c"], ["b", "d"]))
    assert report["valid"]
    assert report["metrics"]["node_loads"] == {
        "a": 5,
        "b": 5,
        "c": 5,
        "d": 5,
    }
    assert report["metrics"]["maximum_utilization"] == Fraction(1, 2)
    assert report["metrics"]["utilization_spread"] == 0
    assert report["metrics"]["moved_bytes"] == 10
    assert report["metrics"]["moved_replica_count"] == 2


def test_validator_rejects_duplicate_excluded_and_unknown_nodes():
    problem = {
        **SIMPLE,
        "exclusions": [{"shardId": "s1", "nodeId": "a"}],
    }
    candidate = result(["a", "a"], ["b", "missing"])
    codes = {
        issue["code"]
        for issue in rv.validate(problem, candidate)["issues"]
    }
    assert {"duplicate_node", "excluded_node", "unknown_node"} <= codes


def test_validator_rejects_capacity_and_zone_diversity_failures():
    low_capacity = {
        **SIMPLE,
        "nodes": [
            {"id": "a", "zone": "z1", "capacity": 9},
            {"id": "b", "zone": "z1", "capacity": 10},
            {"id": "c", "zone": "z2", "capacity": 9},
            {"id": "d", "zone": "z2", "capacity": 10},
        ],
    }
    overloaded = result(["a", "c"], ["a", "c"])
    codes = {
        issue["code"]
        for issue in rv.validate(low_capacity, overloaded)["issues"]
    }
    assert "capacity_exceeded" in codes

    same_zone = result(["a", "b"], ["c", "d"])
    codes = {
        issue["code"]
        for issue in rv.validate(SIMPLE, same_zone)["issues"]
    }
    assert "zone_diversity" in codes


def test_validator_requires_canonical_output_order():
    candidate = {
        "targetPlacements": [
            {"shardId": "s2", "nodeIds": ["d", "b"]},
            {"shardId": "s1", "nodeIds": ["c", "a"]},
        ]
    }
    codes = {
        issue["code"] for issue in rv.validate(SIMPLE, candidate)["issues"]
    }
    assert "noncanonical_shard_order" in codes
    assert "noncanonical_node_order" in codes


def test_generated_cases_cover_contract_and_have_exact_valid_references():
    cases = generator.build_cases(random.Random(12345))
    assert len(cases) == 8
    assert {
        "overload",
        "uneven shard sizes",
        "zone scarcity",
        "exclusions",
        "anti-affinity",
        "movement/balance tradeoff",
        "coordinated swaps",
        "movement tie-breaking",
    } == {case["category"] for case in cases}
    for case in cases:
        reference = {
            "targetPlacements": case["reference"]["targetPlacements"]
        }
        report = rv.validate(case["problem"], reference)
        assert report["valid"], (case["id"], report["issues"])
        assert (
            rv.serialize_metrics(report["metrics"])
            == case["reference"]["metrics"]
        )


def test_lexicographic_case_score_rewards_exact_reference():
    reference = (Fraction(1, 2), Fraction(0), 4, 1)
    assert rv.case_score(reference, reference) == 1.0
    assert rv.case_score(
        (Fraction(3, 4), Fraction(0), 0, 0),
        reference,
    ) < 0.7
    assert rv.case_score(
        (Fraction(1, 2), Fraction(1, 4), 0, 0),
        reference,
    ) < 0.85
