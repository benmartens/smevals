from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


ROOT = Path(__file__).parents[1]
CHECKERS = ROOT / "examples" / "query-optimizer" / "checkers"
BENCHMARK = ROOT / "examples" / "query-optimizer" / "benchmark"


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


qv = load_module(CHECKERS / "query_validation.py", "query_validation")
generator = load_module(
    BENCHMARK / "generate_hidden_cases.py",
    "query_optimizer_hidden_cases",
)


SIMPLE = {
    "memoryLimitRows": 100,
    "tables": [
        {
            "id": "a",
            "rows": 1000,
            "scanCostPerRow": 3,
            "indexes": [
                {
                    "column": "kind",
                    "seekStartupCost": 20,
                    "lookupCostPerRow": 2,
                }
            ],
        },
        {
            "id": "b",
            "rows": 500,
            "scanCostPerRow": 4,
            "indexes": [],
        },
    ],
    "predicates": [
        {
            "tableId": "a",
            "column": "kind",
            "selectivityPermille": 10,
            "indexable": True,
        }
    ],
    "joins": [
        {
            "leftTable": "a",
            "rightTable": "b",
            "selectivityPermille": 20,
        }
    ],
}


def test_reference_plan_is_valid_and_optimal_for_simple_problem():
    reference = qv.reference_optimize(SIMPLE)
    report = qv.validate(SIMPLE, reference)
    assert report["valid"]
    assert reference["plan"]["left"]["operator"] == "indexSeek"


def test_validator_rejects_cross_join():
    problem = {**SIMPLE, "joins": []}
    result = {
        "plan": {
            "operator": "hashJoin",
            "left": {"operator": "tableScan", "tableId": "a"},
            "right": {"operator": "tableScan", "tableId": "b"},
        }
    }
    report = qv.validate(problem, result)
    assert "cross_join" in {issue["code"] for issue in report["issues"]}


def test_validator_rejects_noncanonical_children():
    result = {
        "plan": {
            "operator": "hashJoin",
            "left": {"operator": "tableScan", "tableId": "b"},
            "right": {"operator": "tableScan", "tableId": "a"},
        }
    }
    report = qv.validate(SIMPLE, result)
    assert "noncanonical_children" in {
        issue["code"] for issue in report["issues"]
    }


def test_validator_rejects_unusable_index():
    result = {
        "plan": {
            "operator": "hashJoin",
            "left": {
                "operator": "indexSeek",
                "tableId": "a",
                "indexColumn": "missing",
            },
            "right": {"operator": "tableScan", "tableId": "b"},
        }
    }
    report = qv.validate(SIMPLE, result)
    assert "invalid_index_seek" in {
        issue["code"] for issue in report["issues"]
    }


def test_validator_rejects_whitespace_padded_operator():
    result = {
        "plan": {
            "operator": " hashJoin ",
            "left": {"operator": "tableScan", "tableId": "a"},
            "right": {"operator": "tableScan", "tableId": "b"},
        }
    }
    report = qv.validate(SIMPLE, result)
    assert "unknown_operator" in {
        issue["code"] for issue in report["issues"]
    }


def test_cost_ratio_is_capped():
    assert qv.cost_ratio(200, 100) == 0.5
    assert qv.cost_ratio(50, 100) == 1.0


def test_generated_cases_have_valid_references():
    cases = generator.build_cases(__import__("random").Random(12345))
    assert len(cases) == 7
    for case in cases:
        result = {"plan": case["reference"]["plan"]}
        report = qv.validate(case["problem"], result)
        assert report["valid"], (case["id"], report["issues"])
        assert report["metrics"]["total_cost"] == case["reference"]["cost"]
