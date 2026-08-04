"""
tests/test_carton_packing_grader.py

Tests for the carton-packing checker subsystem.
No model calls; no network; no dotnet invocation.
Imports packing_validation and generate_hidden_cases by path.
"""

from __future__ import annotations

import importlib.util
import json
import random
import sys
import tempfile
from pathlib import Path

import pytest

# ---------------------------------------------------------------------------
# Import helpers
# ---------------------------------------------------------------------------

_CHECKERS_DIR = (
    Path(__file__).resolve().parent.parent / "examples" / "carton-packing" / "checkers"
)
_BENCHMARK_DIR = (
    Path(__file__).resolve().parent.parent / "examples" / "carton-packing" / "benchmark"
)


def _load_module(path: Path, name: str):
    """Load a Python source file as a module by path, bypassing package resolution."""
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


pv = _load_module(_CHECKERS_DIR / "packing_validation.py", "packing_validation")
gh = _load_module(_BENCHMARK_DIR / "generate_hidden_cases.py", "generate_hidden_cases")

# Load the grade module once for artifact tests (path-based import)
_GRADE_MOD = _load_module(
    _CHECKERS_DIR / "grade-carton-packing.py",
    "grade_carton_packing",
)

# ---------------------------------------------------------------------------
# Shared fixtures
# ---------------------------------------------------------------------------

SIMPLE_PROBLEM = {
    "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
    "cartons": [
        {
            "id": "A",
            "width": 4,
            "depth": 4,
            "height": 4,
            "quantity": 2,
            "weight": 5,
            "value": 10,
            "keepUpright": False,
        }
    ],
}

PERFECT_PLACEMENT = {
    "placements": [
        {
            "cartonId": "A",
            "instance": 0,
            "x": 0,
            "y": 0,
            "z": 0,
            "width": 4,
            "depth": 4,
            "height": 4,
        },
        {
            "cartonId": "A",
            "instance": 1,
            "x": 4,
            "y": 0,
            "z": 0,
            "width": 4,
            "depth": 4,
            "height": 4,
        },
    ]
}

# Several seeds used to exercise parametrisation; keep small for test speed
_INVARIANT_SEEDS = [42, 99, 777, 1111, 2025]


# ---------------------------------------------------------------------------
# Orientation tests
# ---------------------------------------------------------------------------


class TestOrientations:
    def test_all_orientations_count(self):
        orients = pv.all_orientations(1, 2, 3)
        assert len(orients) == 6

    def test_all_orientations_symmetric(self):
        orients = pv.all_orientations(2, 2, 3)
        assert len(orients) == 3  # two identical dims reduce count

    def test_all_orientations_sorted(self):
        """all_orientations must be deterministically sorted (not hash-ordered)."""
        o1 = pv.all_orientations(1, 2, 3)
        o2 = pv.all_orientations(1, 2, 3)
        assert o1 == o2
        assert o1 == sorted(o1)

    def test_upright_orientations(self):
        orients = pv.upright_orientations(3, 4, 5)
        assert len(orients) == 2
        assert all(o[2] == 5 for o in orients)

    def test_upright_cubic_single(self):
        orients = pv.upright_orientations(3, 3, 3)
        assert len(orients) == 1

    def test_valid_orientations_normal(self):
        orients = pv.valid_orientations(1, 2, 3, keep_upright=False)
        assert len(orients) == 6

    def test_valid_orientations_upright(self):
        orients = pv.valid_orientations(3, 4, 5, keep_upright=True)
        assert all(o[2] == 5 for o in orients)


# ---------------------------------------------------------------------------
# Geometry / overlap tests
# ---------------------------------------------------------------------------


class TestOverlap:
    def test_no_overlap_adjacent(self):
        p = {"x": 0, "y": 0, "z": 0, "width": 4, "depth": 4, "height": 4}
        q = {"x": 4, "y": 0, "z": 0, "width": 4, "depth": 4, "height": 4}
        assert not pv.boxes_overlap(p, q)

    def test_no_overlap_touching_y(self):
        p = {"x": 0, "y": 0, "z": 0, "width": 4, "depth": 4, "height": 4}
        q = {"x": 0, "y": 4, "z": 0, "width": 4, "depth": 4, "height": 4}
        assert not pv.boxes_overlap(p, q)

    def test_overlap_partial(self):
        p = {"x": 0, "y": 0, "z": 0, "width": 5, "depth": 5, "height": 5}
        q = {"x": 3, "y": 3, "z": 3, "width": 5, "depth": 5, "height": 5}
        assert pv.boxes_overlap(p, q)

    def test_overlap_contained(self):
        big = {"x": 0, "y": 0, "z": 0, "width": 10, "depth": 10, "height": 10}
        small = {"x": 2, "y": 2, "z": 2, "width": 3, "depth": 3, "height": 3}
        assert pv.boxes_overlap(big, small)

    def test_no_overlap_stacked(self):
        p = {"x": 0, "y": 0, "z": 0, "width": 4, "depth": 4, "height": 4}
        q = {"x": 0, "y": 0, "z": 4, "width": 4, "depth": 4, "height": 4}
        assert not pv.boxes_overlap(p, q)


# ---------------------------------------------------------------------------
# Support tests
# ---------------------------------------------------------------------------


class TestSupport:
    def test_on_floor_always_ok(self):
        p = {"x": 0, "y": 0, "z": 0, "width": 5, "depth": 5, "height": 5}
        assert pv.check_support(p, [p])

    def test_fully_supported_by_one(self):
        base = {"x": 0, "y": 0, "z": 0, "width": 5, "depth": 5, "height": 3}
        top = {"x": 0, "y": 0, "z": 3, "width": 5, "depth": 5, "height": 2}
        assert pv.check_support(top, [base, top])

    def test_partial_support_rejected(self):
        base = {"x": 0, "y": 0, "z": 0, "width": 3, "depth": 5, "height": 3}
        top = {"x": 0, "y": 0, "z": 3, "width": 5, "depth": 5, "height": 2}
        assert not pv.check_support(top, [base, top])

    def test_union_of_two_supports(self):
        left = {"x": 0, "y": 0, "z": 0, "width": 3, "depth": 4, "height": 2}
        right = {"x": 3, "y": 0, "z": 0, "width": 3, "depth": 4, "height": 2}
        top = {"x": 0, "y": 0, "z": 2, "width": 6, "depth": 4, "height": 2}
        assert pv.check_support(top, [left, right, top])

    def test_gap_in_union_rejected(self):
        left = {"x": 0, "y": 0, "z": 0, "width": 2, "depth": 4, "height": 2}
        right = {"x": 4, "y": 0, "z": 0, "width": 2, "depth": 4, "height": 2}
        top = {"x": 0, "y": 0, "z": 2, "width": 6, "depth": 4, "height": 2}
        # gap from x=2..4 means only 4/6 of base covered
        assert not pv.check_support(top, [left, right, top])


# ---------------------------------------------------------------------------
# Validation tests
# ---------------------------------------------------------------------------


class TestValidation:
    def test_valid_simple(self):
        r = pv.validate(SIMPLE_PROBLEM, PERFECT_PLACEMENT)
        assert r["valid"] is True
        assert r["issues"] == []

    def test_noncanonical_order_rejected(self):
        result = {
            "placements": list(reversed(PERFECT_PLACEMENT["placements"]))
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        assert "ORDER" in [issue["code"] for issue in r["issues"]]

    def test_bounds_violation(self):
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 8,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                }
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        assert not r["valid"]
        codes = [i["code"] for i in r["issues"]]
        assert "BOUNDS" in codes

    def test_overlap_detected(self):
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
                {
                    "cartonId": "A",
                    "instance": 1,
                    "x": 2,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        codes = [i["code"] for i in r["issues"]]
        assert "OVERLAP" in codes

    def test_quantity_exceeded(self):
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
                {
                    "cartonId": "A",
                    "instance": 1,
                    "x": 4,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
                {
                    "cartonId": "A",
                    "instance": 2,
                    "x": 0,
                    "y": 4,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        codes = [i["code"] for i in r["issues"]]
        assert "QUANTITY" in codes

    def test_weight_exceeded(self):
        problem = {
            "container": {"width": 20, "depth": 20, "height": 20, "maxWeight": 5},
            "cartons": [
                {
                    "id": "heavy",
                    "width": 5,
                    "depth": 5,
                    "height": 5,
                    "quantity": 2,
                    "weight": 4,
                    "value": 10,
                    "keepUpright": False,
                }
            ],
        }
        result = {
            "placements": [
                {
                    "cartonId": "heavy",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 5,
                    "depth": 5,
                    "height": 5,
                },
                {
                    "cartonId": "heavy",
                    "instance": 1,
                    "x": 5,
                    "y": 0,
                    "z": 0,
                    "width": 5,
                    "depth": 5,
                    "height": 5,
                },
            ]
        }
        r = pv.validate(problem, result)
        codes = [i["code"] for i in r["issues"]]
        assert "WEIGHT" in codes

    def test_keepupright_violation(self):
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
            "cartons": [
                {
                    "id": "vase",
                    "width": 3,
                    "depth": 3,
                    "height": 8,
                    "quantity": 1,
                    "weight": 1,
                    "value": 5,
                    "keepUpright": True,
                }
            ],
        }
        result = {
            "placements": [
                # height should be 8, but placed with height=3 (laid on side)
                {
                    "cartonId": "vase",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 3,
                    "depth": 8,
                    "height": 3,
                }
            ]
        }
        r = pv.validate(problem, result)
        codes = [i["code"] for i in r["issues"]]
        assert "ORIENTATION" in codes

    def test_invalid_orientation(self):
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
            "cartons": [
                {
                    "id": "B",
                    "width": 1,
                    "depth": 2,
                    "height": 3,
                    "quantity": 1,
                    "weight": 1,
                    "value": 5,
                    "keepUpright": False,
                }
            ],
        }
        result = {
            "placements": [
                # (2, 2, 3) is not a permutation of (1, 2, 3)
                {
                    "cartonId": "B",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 2,
                    "depth": 2,
                    "height": 3,
                }
            ]
        }
        r = pv.validate(problem, result)
        codes = [i["code"] for i in r["issues"]]
        assert "INVALID_ORIENT" in codes

    def test_duplicate_instance(self):
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 4,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                },
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        codes = [i["code"] for i in r["issues"]]
        assert "DUP_INSTANCE" in codes

    def test_support_violation(self):
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
            "cartons": [
                {
                    "id": "base",
                    "width": 5,
                    "depth": 5,
                    "height": 3,
                    "quantity": 1,
                    "weight": 5,
                    "value": 5,
                    "keepUpright": False,
                },
                {
                    "id": "top",
                    "width": 8,
                    "depth": 8,
                    "height": 3,
                    "quantity": 1,
                    "weight": 5,
                    "value": 5,
                    "keepUpright": False,
                },
            ],
        }
        result = {
            "placements": [
                {
                    "cartonId": "base",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 5,
                    "depth": 5,
                    "height": 3,
                },
                # top at z=3, but only partially supported by base (5×5 < 8×8)
                {
                    "cartonId": "top",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 3,
                    "width": 8,
                    "depth": 8,
                    "height": 3,
                },
            ]
        }
        r = pv.validate(problem, result)
        codes = [i["code"] for i in r["issues"]]
        assert "SUPPORT" in codes

    def test_bad_instance_negative(self):
        """Instance index < 0 must emit BAD_INSTANCE."""
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": -1,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                }
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        codes = [i["code"] for i in r["issues"]]
        assert "BAD_INSTANCE" in codes

    def test_bad_instance_ge_quantity(self):
        """Instance index >= quantity must emit BAD_INSTANCE."""
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 5,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                }
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        codes = [i["code"] for i in r["issues"]]
        assert "BAD_INSTANCE" in codes

    def test_bad_instance_exactly_quantity(self):
        """Instance index == quantity is out of range [0, quantity)."""
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 2,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                }
            ]
        }
        # quantity=2 so valid range is [0,1]
        r = pv.validate(SIMPLE_PROBLEM, result)
        codes = [i["code"] for i in r["issues"]]
        assert "BAD_INSTANCE" in codes

    def test_malformed_placement_missing_fields(self):
        """Placement with missing coordinate fields must not crash the validator."""
        result = {"placements": [{"cartonId": "A"}]}
        r = pv.validate(SIMPLE_PROBLEM, result)
        assert isinstance(r, dict)
        assert "valid" in r
        assert "issues" in r

    def test_malformed_placement_extra_fields(self):
        """Extra unknown fields must not crash the validator."""
        result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 0,
                    "y": 0,
                    "z": 0,
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                    "unknown_field": True,
                }
            ]
        }
        r = pv.validate(SIMPLE_PROBLEM, result)
        assert r["valid"]

    def test_empty_placements_valid(self):
        """Empty placements list is valid (zero items packed)."""
        r = pv.validate(SIMPLE_PROBLEM, {"placements": []})
        assert r["valid"]

    def test_placements_key_missing(self):
        """Missing placements key treated as empty."""
        r = pv.validate(SIMPLE_PROBLEM, {})
        assert r["valid"]


# ---------------------------------------------------------------------------
# Objective recomputation
# ---------------------------------------------------------------------------


class TestObjective:
    def test_recompute_matches_sum(self):
        v, vol = pv.recompute_objective(SIMPLE_PROBLEM, PERFECT_PLACEMENT)
        assert v == 20  # 2 × value 10
        assert vol == 128  # 2 × 4×4×4

    def test_recompute_ignores_candidate_totals(self):
        result_with_bad_totals = {
            "total_value": 9999,
            "placements": PERFECT_PLACEMENT["placements"],
        }
        v, vol = pv.recompute_objective(SIMPLE_PROBLEM, result_with_bad_totals)
        assert v == 20

    def test_empty_placements(self):
        v, vol = pv.recompute_objective(SIMPLE_PROBLEM, {"placements": []})
        assert v == 0 and vol == 0


# ---------------------------------------------------------------------------
# Capped ratio and scoring
# ---------------------------------------------------------------------------


class TestRatios:
    def test_capped_at_1(self):
        assert pv.capped_value_ratio(200, 100) == 1.0
        assert pv.capped_volume_ratio(500, 300) == 1.0

    def test_below_ref(self):
        r = pv.capped_value_ratio(50, 100)
        assert abs(r - 0.5) < 1e-9

    def test_zero_ref(self):
        assert pv.capped_value_ratio(0, 0) == 1.0

    def test_score_weights(self):
        s = pv.case_score(1.0, 0.0)
        assert abs(s - 0.9) < 1e-9
        s2 = pv.case_score(0.0, 1.0)
        assert abs(s2 - 0.1) < 1e-9

    def test_candidate_beating_reference_capped(self):
        assert pv.capped_value_ratio(9999, 100) == 1.0
        assert pv.case_score(1.0, 1.0) == 1.0


# ---------------------------------------------------------------------------
# Canonical / deterministic
# ---------------------------------------------------------------------------


class TestCanonical:
    def test_canonical_order(self):
        placements = [
            {
                "cartonId": "B",
                "instance": 0,
                "x": 5,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            },
            {
                "cartonId": "A",
                "instance": 1,
                "x": 4,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            },
            {
                "cartonId": "A",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            },
        ]
        canon = pv.canonical_placements(placements)
        assert canon[0]["cartonId"] == "A" and canon[0]["instance"] == 0
        assert canon[1]["cartonId"] == "A" and canon[1]["instance"] == 1
        assert canon[2]["cartonId"] == "B"

    def test_placements_equal(self):
        a = [
            {
                "cartonId": "A",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            }
        ]
        b = [
            {
                "cartonId": "A",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            }
        ]
        assert pv.placements_equal(a, b)

    def test_placements_not_equal_diff_pos(self):
        a = [
            {
                "cartonId": "A",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            }
        ]
        b = [
            {
                "cartonId": "A",
                "instance": 0,
                "x": 1,
                "y": 0,
                "z": 0,
                "width": 4,
                "depth": 4,
                "height": 4,
            }
        ]
        assert not pv.placements_equal(a, b)

    def test_placements_order_independent(self):
        p = {
            "cartonId": "A",
            "instance": 0,
            "x": 0,
            "y": 0,
            "z": 0,
            "width": 4,
            "depth": 4,
            "height": 4,
        }
        q = {
            "cartonId": "B",
            "instance": 0,
            "x": 4,
            "y": 0,
            "z": 0,
            "width": 4,
            "depth": 4,
            "height": 4,
        }
        assert pv.placements_equal([p, q], [q, p])

    def test_canonicalization_stable_across_calls(self):
        """canonical_placements must produce the same result every time."""
        placements = [
            {
                "cartonId": "Z",
                "instance": 0,
                "x": 9,
                "y": 0,
                "z": 0,
                "width": 1,
                "depth": 1,
                "height": 1,
            },
            {
                "cartonId": "A",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 1,
                "depth": 1,
                "height": 1,
            },
            {
                "cartonId": "M",
                "instance": 0,
                "x": 5,
                "y": 0,
                "z": 0,
                "width": 1,
                "depth": 1,
                "height": 1,
            },
        ]
        c1 = pv.canonical_placements(placements)
        c2 = pv.canonical_placements(placements)
        assert [p["cartonId"] for p in c1] == [p["cartonId"] for p in c2]
        assert c1[0]["cartonId"] == "A"


# ---------------------------------------------------------------------------
# Reference packer
# ---------------------------------------------------------------------------


class TestReferencePacker:
    def test_packs_something(self):
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
            "cartons": [
                {
                    "id": "A",
                    "width": 3,
                    "depth": 3,
                    "height": 3,
                    "quantity": 5,
                    "weight": 2,
                    "value": 5,
                    "keepUpright": False,
                }
            ],
        }
        result = pv.reference_pack(problem, seed=42)
        assert len(result["placements"]) > 0

    def test_packs_valid(self):
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 200},
            "cartons": [
                {
                    "id": "A",
                    "width": 4,
                    "depth": 4,
                    "height": 4,
                    "quantity": 4,
                    "weight": 5,
                    "value": 10,
                    "keepUpright": False,
                }
            ],
        }
        result = pv.reference_pack(problem, seed=42)
        r = pv.validate(problem, result)
        assert r["valid"], r["issues"]

    def test_deterministic(self):
        problem = {
            "container": {"width": 12, "depth": 12, "height": 12, "maxWeight": 200},
            "cartons": [
                {
                    "id": "A",
                    "width": 3,
                    "depth": 4,
                    "height": 5,
                    "quantity": 4,
                    "weight": 3,
                    "value": 8,
                    "keepUpright": False,
                }
            ],
        }
        r1 = pv.reference_pack(problem, seed=7)
        r2 = pv.reference_pack(problem, seed=7)
        assert pv.placements_equal(r1["placements"], r2["placements"])

    def test_rotation_used_when_needed(self):
        problem = {
            "container": {"width": 5, "depth": 5, "height": 2, "maxWeight": 100},
            "cartons": [
                {
                    "id": "flat",
                    "width": 2,
                    "depth": 5,
                    "height": 5,
                    "quantity": 1,
                    "weight": 1,
                    "value": 10,
                    "keepUpright": False,
                }
            ],
        }
        result = pv.reference_pack(problem, seed=42)
        assert len(result["placements"]) > 0
        r = pv.validate(problem, result)
        assert r["valid"]

    def test_weight_constraint_respected(self):
        problem = {
            "container": {"width": 20, "depth": 20, "height": 20, "maxWeight": 10},
            "cartons": [
                {
                    "id": "heavy",
                    "width": 5,
                    "depth": 5,
                    "height": 5,
                    "quantity": 4,
                    "weight": 4,
                    "value": 10,
                    "keepUpright": False,
                }
            ],
        }
        result = pv.reference_pack(problem, seed=42)
        r = pv.validate(problem, result)
        assert r["valid"]
        assert r["packed_weight"] <= 10


# ---------------------------------------------------------------------------
# Generate hidden cases – bundle-level tests
# ---------------------------------------------------------------------------


class TestGenerateHiddenCases:
    def test_generates_8_cases(self):
        with tempfile.TemporaryDirectory() as td:
            out_file = gh.generate(td, seed=42)
            b = json.loads(out_file.read_text(encoding="utf-8"))
            assert len(b["cases"]) == 8

    def test_fixed_seed_reproducible(self):
        with tempfile.TemporaryDirectory() as td1, tempfile.TemporaryDirectory() as td2:
            b1 = json.loads(gh.generate(td1, seed=99).read_text(encoding="utf-8"))
            b2 = json.loads(gh.generate(td2, seed=99).read_text(encoding="utf-8"))
            for c1, c2 in zip(b1["cases"], b2["cases"]):
                assert c1["id"] == c2["id"]
                assert c1["problem"] == c2["problem"]
                assert c1["reference"]["value"] == c2["reference"]["value"]
                assert c1["reference"]["volume"] == c2["reference"]["volume"]

    def test_different_seeds_produce_different_problems(self):
        with tempfile.TemporaryDirectory() as td1, tempfile.TemporaryDirectory() as td2:
            b1 = json.loads(gh.generate(td1, seed=1111).read_text(encoding="utf-8"))
            b2 = json.loads(gh.generate(td2, seed=9999).read_text(encoding="utf-8"))
            diffs = sum(
                1 for c1, c2 in zip(b1["cases"], b2["cases"])
                if c1["problem"] != c2["problem"]
            )
            assert diffs > 0, "Different seeds produced identical problems"

    def test_different_seeds_same_case_ids(self):
        with tempfile.TemporaryDirectory() as td1, tempfile.TemporaryDirectory() as td2:
            b1 = json.loads(gh.generate(td1, seed=1).read_text(encoding="utf-8"))
            b2 = json.loads(gh.generate(td2, seed=2).read_text(encoding="utf-8"))
            assert [c["id"] for c in b1["cases"]] == [c["id"] for c in b2["cases"]]

    def test_omitted_seed_generates_random_seed(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=None).read_text(encoding="utf-8"))
            assert b["seed"] is not None
            assert isinstance(b["seed"], int)

    def test_schema_version(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=42).read_text(encoding="utf-8"))
            assert b["schema_version"] == 1

    def test_probe_case_id_present(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=42).read_text(encoding="utf-8"))
            probe_id = b.get("probe_case_id")
            assert probe_id
            assert probe_id in [c["id"] for c in b["cases"]]

    def test_all_references_valid(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=42).read_text(encoding="utf-8"))
            for case in b["cases"]:
                r = pv.validate(
                    case["problem"],
                    {"placements": case["reference"]["placements"]},
                )
                assert r["valid"], f"Case {case['id']} reference invalid: {r['issues']}"

    def test_greedy_traps_present(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=42).read_text(encoding="utf-8"))
        found = {c["id"] for c in b["cases"]} & {"weight_value_tradeoff", "greedy_trap"}
        assert len(found) >= 2

    def test_reference_value_non_zero_for_nontrivial(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=42).read_text(encoding="utf-8"))
        for c in b["cases"]:
            if c["id"] != "exact_basic":
                assert c["reference"]["value"] > 0, f"{c['id']} reference value is 0"

    def test_bundle_seed_matches_argument(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=12345).read_text(encoding="utf-8"))
            assert b["seed"] == 12345

    def test_all_references_valid_different_seed(self):
        with tempfile.TemporaryDirectory() as td:
            b = json.loads(gh.generate(td, seed=777).read_text(encoding="utf-8"))
            for case in b["cases"]:
                r = pv.validate(
                    case["problem"],
                    {"placements": case["reference"]["placements"]},
                )
                assert r["valid"], f"Case {case['id']} (seed=777) invalid: {r['issues']}"


# ---------------------------------------------------------------------------
# Case invariants – multi-seed property assertions
# ---------------------------------------------------------------------------


def _raw_cases(seed: int) -> list[dict]:
    """Return raw case list (with _trap_meta) directly from _build_cases."""
    rng = random.Random(seed)
    return gh._build_cases(rng, seed)


class TestCaseInvariants:
    """
    Assert structural guarantees for rotation_required, support_stacking, and
    greedy_trap across several representative seeds.
    """

    # --- rotation_required -----------------------------------------------

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_rotation_unrotated_does_not_fit(self, seed):
        """The carton's original width must exceed the container width."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "rotation_required")
        prob = c["problem"]
        cont = prob["container"]
        carton = prob["cartons"][0]
        assert carton["width"] > cont["width"], (
            f"seed={seed}: ow={carton['width']} should exceed cw={cont['width']}"
        )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_rotation_rotated_always_fits(self, seed):
        """At least one rotation of the carton must fit in the container."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "rotation_required")
        prob = c["problem"]
        cont = prob["container"]
        carton = prob["cartons"][0]
        ow, od, oh = carton["width"], carton["depth"], carton["height"]
        orients = pv.all_orientations(ow, od, oh)
        fits = any(
            pw <= cont["width"] and pd <= cont["depth"] and ph <= cont["height"]
            for (pw, pd, ph) in orients
        )
        assert fits, f"seed={seed}: no rotation of ({ow},{od},{oh}) fits in container {cont}"

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_rotation_reference_positive(self, seed):
        """Reference packer must pack at least one item (rotation case)."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "rotation_required")
        ref = gh._ref(c["problem"], seed)
        assert ref["value"] > 0, f"seed={seed}: rotation_required reference value is 0"
        assert len(ref["placements"]) > 0

    # --- support_stacking ------------------------------------------------

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_support_stacking_even_dims(self, seed):
        """Container width and depth must be even (exact halving for mid/top layers)."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "support_stacking")
        cont = c["problem"]["container"]
        assert cont["width"] % 2 == 0, f"seed={seed}: container width {cont['width']} not even"
        assert cont["depth"] % 2 == 0, f"seed={seed}: container depth {cont['depth']} not even"

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_support_stacking_coverage_exact(self, seed):
        """Two mid slabs and four top slabs cover the same footprint as the base."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "support_stacking")
        cartons = {ct["id"]: ct for ct in c["problem"]["cartons"]}
        base = cartons["base_slab"]
        mid  = cartons["mid_half"]
        top  = cartons["top_qtr"]
        base_area = base["width"] * base["depth"]
        mid_area  = 2 * mid["width"] * mid["depth"]
        top_area  = 4 * top["width"] * top["depth"]
        assert mid_area == base_area, (
            f"seed={seed}: 2×mid area {mid_area} != base area {base_area}"
        )
        assert top_area == mid_area, (
            f"seed={seed}: 4×top area {top_area} != 2×mid area {mid_area}"
        )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_support_stacking_keepupright(self, seed):
        """All stacking items must have keepUpright=True."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "support_stacking")
        for ct in c["problem"]["cartons"]:
            assert ct["keepUpright"] is True, (
                f"seed={seed}: {ct['id']} keepUpright should be True"
            )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_support_stacking_reference_valid_and_stacked(self, seed):
        """Reference must be valid and include at least one placement with z > 0."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "support_stacking")
        ref = gh._ref(c["problem"], seed)
        r = pv.validate(c["problem"], {"placements": ref["placements"]})
        assert r["valid"], f"seed={seed}: support_stacking reference invalid: {r['issues']}"
        has_stacked = any(p["z"] > 0 for p in ref["placements"])
        assert has_stacked, f"seed={seed}: support_stacking reference has no stacked items"

    # --- greedy_trap (GREEDY TRAP #2) ------------------------------------

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_greedy_trap_invariant_A_individual_value(self, seed):
        """Invariant A: big_v > small_v (big has highest individual value)."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "greedy_trap")
        meta = c["_trap_meta"]
        assert meta["big_v"] > meta["small_v"], (
            f"seed={seed}: big_v={meta['big_v']} must exceed small_v={meta['small_v']}"
        )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_greedy_trap_invariant_B_density(self, seed):
        """Invariant B: big has strictly higher value density than small."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "greedy_trap")
        meta = c["_trap_meta"]
        # big_v/big_vol > small_v/small_vol ↔ big_v*small_vol > small_v*big_vol
        assert meta["big_v"] * meta["small_vol"] > meta["small_v"] * meta["big_vol"], (
            f"seed={seed}: density invariant failed "
            f"big_v={meta['big_v']} big_vol={meta['big_vol']} "
            f"small_v={meta['small_v']} small_vol={meta['small_vol']}"
        )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_greedy_trap_invariant_C_total_value(self, seed):
        """Invariant C: grid_cap × small_v > big_v (all-small beats big-alone)."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "greedy_trap")
        meta = c["_trap_meta"]
        total_small = meta["grid_cap"] * meta["small_v"]
        assert total_small > meta["big_v"], (
            f"seed={seed}: {meta['grid_cap']}×{meta['small_v']}={total_small} "
            f"must exceed big_v={meta['big_v']}"
        )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_greedy_trap_invariant_D_reference_beats_big(self, seed):
        """Invariant D: reference packer finds the small-item solution (value > big_v)."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "greedy_trap")
        big_v = c["_trap_meta"]["big_v"]
        ref = gh._ref(c["problem"], seed)
        r = pv.validate(c["problem"], {"placements": ref["placements"]})
        assert r["valid"], f"seed={seed}: greedy_trap reference invalid: {r['issues']}"
        _, ref_vol = pv.recompute_objective(c["problem"], {"placements": ref["placements"]})
        assert ref["value"] > big_v, (
            f"seed={seed}: reference value {ref['value']} must exceed big_v={big_v}"
        )

    @pytest.mark.parametrize("seed", _INVARIANT_SEEDS)
    def test_greedy_trap_no_small_items_in_residual(self, seed):
        """With big_s = side7-1, the 1-unit residual cannot hold any 2×2×2 cube."""
        cases = _raw_cases(seed)
        c = next(x for x in cases if x["id"] == "greedy_trap")
        meta = c["_trap_meta"]
        side7  = meta["side7"]
        big_s  = side7 - 1   # by construction
        small_s = 2           # by construction
        # Residual strips are 1 unit wide in each dimension — too narrow for small_s=2
        assert side7 - big_s == 1, f"seed={seed}: expected 1-unit gap, got {side7-big_s}"
        assert small_s > 1, "small_s must be > 1 for residual argument to hold"


# ---------------------------------------------------------------------------
# Artifact fallbacks (grade module helpers)
# ---------------------------------------------------------------------------


class TestArtifactFallbacks:
    def test_svg_placeholder_on_empty_list(self):
        svg = _GRADE_MOD._write_showcase_svg(
            [],
            {"container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
             "cartons": []},
        )
        assert "<svg" in svg

    def test_svg_on_none_returns_placeholder(self):
        svg = _GRADE_MOD._write_showcase_svg(None, None)
        assert "<svg" in svg

    def test_svg_with_real_data(self):
        placements = [
            {
                "cartonId": "A",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 3,
                "depth": 3,
                "height": 3,
            }
        ]
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
            "cartons": [
                {
                    "id": "A",
                    "width": 3,
                    "depth": 3,
                    "height": 3,
                    "quantity": 1,
                    "weight": 1,
                    "value": 5,
                    "keepUpright": False,
                }
            ],
        }
        svg = _GRADE_MOD._write_showcase_svg(placements, problem)
        assert "<svg" in svg
        assert "Top" in svg
        assert "Front" in svg

    def test_svg_legend_contains_counts(self):
        placements = [
            {
                "cartonId": "box",
                "instance": 0,
                "x": 0,
                "y": 0,
                "z": 0,
                "width": 2,
                "depth": 2,
                "height": 2,
            },
            {
                "cartonId": "box",
                "instance": 1,
                "x": 2,
                "y": 0,
                "z": 0,
                "width": 2,
                "depth": 2,
                "height": 2,
            },
        ]
        problem = {
            "container": {"width": 10, "depth": 10, "height": 10, "maxWeight": 100},
            "cartons": [
                {
                    "id": "box",
                    "width": 2,
                    "depth": 2,
                    "height": 2,
                    "quantity": 2,
                    "weight": 1,
                    "value": 5,
                    "keepUpright": False,
                }
            ],
        }
        svg = _GRADE_MOD._write_showcase_svg(placements, problem)
        assert "×2" in svg or "x2" in svg.lower() or "2" in svg

    def test_patch_fallback_no_crash(self):
        result = _GRADE_MOD._write_patch(
            Path("/nonexistent/workspace"),
            _CHECKERS_DIR,
        )
        assert isinstance(result, str)
        assert len(result) > 0


# ---------------------------------------------------------------------------
# _grade_case: nonzero-exit / stderr-preservation behaviour
# ---------------------------------------------------------------------------

# Minimal valid problem used across these tests.
_GC_PROBLEM = {
    "container": {"width": 4, "depth": 4, "height": 4, "maxWeight": 100},
    "cartons": [
        {
            "id": "A",
            "width": 2,
            "depth": 2,
            "height": 2,
            "quantity": 1,
            "weight": 1,
            "value": 10,
            "keepUpright": False,
        }
    ],
}

_GC_VALID_RESULT = {
    "placements": [
        {
            "cartonId": "A",
            "instance": 0,
            "x": 0,
            "y": 0,
            "z": 0,
            "width": 2,
            "depth": 2,
            "height": 2,
        }
    ]
}

_GC_CASE = {
    "id": "gc_test",
    "problem": _GC_PROBLEM,
    "reference": {"value": 10, "volume": 8, "placements": []},
}


def _make_fake_dll(tmp_path: Path, returncode: int, result_json: str | None,
                   stderr: str = "") -> tuple[Path, Path]:
    """
    Write a tiny Python 'dll shim' that:
      - writes result_json to argv[2] (if not None)
      - prints stderr_text to stderr
      - exits with returncode
    Returns (fake_dll, workspace).
    """
    shim = tmp_path / "fake_cli.py"
    lines = ["import sys, os"]
    if result_json is not None:
        escaped = result_json.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")
        lines.append(f'with open(sys.argv[2], "w", encoding="utf-8") as f: f.write("{escaped}")')
    if stderr:
        escaped_err = stderr.replace("\\", "\\\\").replace('"', '\\"')
        lines.append(f'sys.stderr.write("{escaped_err}\\n")')
    lines.append(f"sys.exit({returncode})")
    shim.write_text("\n".join(lines), encoding="utf-8")

    # Create a fake "dll" that the grade module will call as: dotnet <dll> <p> <r>
    # We monkeypatch _run_candidate in the test instead of using dotnet, so this
    # path just needs to exist to satisfy the function signature.
    dll = tmp_path / "CartonPacking.Cli.dll"
    dll.touch()
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    return dll, workspace


def _grade_via_monkeypatch(monkeypatch, returncode: int | None, stderr: str,
                            result_to_write: dict | None, tmp_path: Path) -> dict:
    """
    Call _grade_case with a monkeypatched _run_candidate that:
      - returns (returncode, stderr)
      - optionally writes result_to_write to the result_file path
    """
    import json as _json

    def _fake_run(dll, problem_path, result_path):
        if result_to_write is not None:
            result_path.write_text(_json.dumps(result_to_write), encoding="utf-8")
        return returncode, stderr

    monkeypatch.setattr(_GRADE_MOD, "_run_candidate", _fake_run)

    dll = tmp_path / "fake.dll"
    dll.touch()
    workspace = tmp_path / "workspace"
    workspace.mkdir(exist_ok=True)
    tmp_dir = tmp_path / "tmp"
    tmp_dir.mkdir(exist_ok=True)

    return _GRADE_MOD._grade_case(_GC_CASE, dll, workspace, tmp_dir)


class TestGradeCaseRunBehavior:
    """
    Tests for the nonzero-exit tolerance and stderr-preservation in _grade_case.
    Uses monkeypatching of _run_candidate; no dotnet required.
    """

    def test_exit_zero_valid_result_scores(self, monkeypatch, tmp_path):
        """Exit 0 with a valid result produces a positive score."""
        detail = _grade_via_monkeypatch(
            monkeypatch, returncode=0, stderr="",
            result_to_write=_GC_VALID_RESULT, tmp_path=tmp_path
        )
        assert detail["valid"] is True
        assert detail["score"] > 0
        assert "cli_stderr" not in detail

    def test_exit_nonzero_with_valid_result_still_scores(self, monkeypatch, tmp_path):
        """
        Candidate CLI exits nonzero (reports own validation failure) but writes
        a geometrically valid result file -> independent validator must accept it.
        """
        detail = _grade_via_monkeypatch(
            monkeypatch, returncode=1, stderr="cli found issues",
            result_to_write=_GC_VALID_RESULT, tmp_path=tmp_path
        )
        assert detail["valid"] is True, (
            "independent validator should accept a geometrically valid layout "
            f"even when CLI exits nonzero; issues={detail.get('issues')}"
        )
        assert detail["score"] > 0
        # stderr must be preserved as diagnostic, not override issue codes
        assert detail.get("cli_stderr") == "cli found issues"
        assert detail.get("cli_exit_code") == 1
        # No run_failure issue from the nonzero exit
        codes = [i["code"] for i in detail.get("issues", [])]
        assert "RUN_FAILURE" not in codes

    def test_exit_nonzero_invalid_result_scores_zero(self, monkeypatch, tmp_path):
        """
        Nonzero exit AND invalid placements -> independent validator rejects;
        score=0 but not a RUN_FAILURE.
        """
        bad_result = {
            "placements": [
                {
                    "cartonId": "A",
                    "instance": 0,
                    "x": 999,  # out of bounds
                    "y": 0,
                    "z": 0,
                    "width": 2,
                    "depth": 2,
                    "height": 2,
                }
            ]
        }
        detail = _grade_via_monkeypatch(
            monkeypatch, returncode=2, stderr="out of bounds detected",
            result_to_write=bad_result, tmp_path=tmp_path
        )
        assert detail["valid"] is False
        assert detail["score"] == 0.0
        codes = [i["code"] for i in detail["issues"]]
        assert "RUN_FAILURE" not in codes
        assert "BOUNDS" in codes
        assert detail.get("cli_stderr") == "out of bounds detected"
        assert detail.get("cli_exit_code") == 2

    def test_launch_failure_returncode_none_is_run_failure(self, monkeypatch, tmp_path):
        """
        returncode=None means process could not launch (timeout/exception).
        Must report RUN_FAILURE regardless of result file state.
        """
        detail = _grade_via_monkeypatch(
            monkeypatch, returncode=None, stderr="timed out after 60s",
            result_to_write=None, tmp_path=tmp_path
        )
        assert detail["valid"] is False
        assert detail["score"] == 0.0
        codes = [i["code"] for i in detail["issues"]]
        assert "RUN_FAILURE" in codes

    def test_exit_nonzero_no_result_file_is_run_failure(self, monkeypatch, tmp_path):
        """
        Nonzero exit + no result file -> RUN_FAILURE (nothing to validate).
        """
        detail = _grade_via_monkeypatch(
            monkeypatch, returncode=1, stderr="crash before output",
            result_to_write=None, tmp_path=tmp_path
        )
        assert detail["valid"] is False
        codes = [i["code"] for i in detail["issues"]]
        assert "RUN_FAILURE" in codes

    def test_exit_nonzero_unparseable_json_is_parse_error(self, monkeypatch, tmp_path):
        """
        Result file exists but contains garbage JSON -> PARSE_ERROR, not RUN_FAILURE.
        """
        import json as _json

        def _fake_run(dll, problem_path, result_path):
            result_path.write_text("not json {{{{", encoding="utf-8")
            return 1, "cli wrote garbage"

        monkeypatch.setattr(_GRADE_MOD, "_run_candidate", _fake_run)
        dll = tmp_path / "fake.dll"; dll.touch()
        workspace = tmp_path / "workspace"; workspace.mkdir(exist_ok=True)
        tmp_dir = tmp_path / "tmp"; tmp_dir.mkdir(exist_ok=True)
        detail = _GRADE_MOD._grade_case(_GC_CASE, dll, workspace, tmp_dir)

        assert detail["valid"] is False
        codes = [i["code"] for i in detail["issues"]]
        assert "PARSE_ERROR" in codes
        assert "RUN_FAILURE" not in codes

    def test_stderr_not_in_issue_codes(self, monkeypatch, tmp_path):
        """
        cli_stderr must appear only as a diagnostic field; independent issue
        codes must remain unchanged by its presence.
        """
        detail = _grade_via_monkeypatch(
            monkeypatch, returncode=0, stderr="some warning",
            result_to_write=_GC_VALID_RESULT, tmp_path=tmp_path
        )
        issue_codes = [i["code"] for i in detail.get("issues", [])]
        assert "some warning" not in issue_codes
        # cli_stderr preserved even on exit 0 when non-empty
        assert detail.get("cli_stderr") == "some warning"


def test_deterministic_probe_accepts_zero_exit(monkeypatch, tmp_path):
    result = {"placements": []}

    def _fake_run(dll, problem_path, result_path):
        result_path.write_text(json.dumps(result), encoding="utf-8")
        return 0, ""

    monkeypatch.setattr(_GRADE_MOD, "_run_candidate", _fake_run)
    probe = {
        "problem": {
            "container": {
                "width": 1,
                "depth": 1,
                "height": 1,
                "maxWeight": 1,
            },
            "cartons": [],
        }
    }
    assert _GRADE_MOD._deterministic_probe(
        probe,
        tmp_path / "fake.dll",
        tmp_path,
        tmp_path,
        seed=1,
    )


def test_locate_dll_accepts_alternate_release_subfolder(tmp_path):
    dll = (
        tmp_path
        / "src"
        / "CartonPacking.Cli"
        / "bin"
        / "Release"
        / "net10.0-windows"
        / "CartonPacking.Cli.dll"
    )
    dll.parent.mkdir(parents=True)
    dll.write_bytes(b"assembly")
    assert _GRADE_MOD._locate_dll(tmp_path) == dll
