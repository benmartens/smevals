#!/usr/bin/env python3
"""
generate_hidden_cases.py – Generate the hidden benchmark bundle for the
carton-packing Eval.

Usage:
    python generate_hidden_cases.py --output DIR [--seed INT]

If --seed is omitted, a cryptographically random integer is chosen and
persisted in the bundle so the same bundle can be regenerated later.
Fixed explicit seeds are always reproducible.

Generates one JSON file: hidden_cases.json
Schema:
  {
    "schema_version": 1,
    "seed": <int>,
    "cases": [ { "id", "description", "problem", "reference" } ],
    "probe_case_id": <str>   -- deterministic probe case for consistency check
  }

Reference includes: value, volume, placements (from reference packer).
All case dimensions are parameterized from the RNG seed so exact hidden
problems are not revealed by reading this source. Two distinct cases are
designed to defeat naive highest-value and highest-value-density greediness.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import secrets
import sys
from pathlib import Path

# Allow importing from checker dir even when run standalone
_HERE = Path(__file__).resolve().parent.parent / "checkers"
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))

from packing_validation import reference_pack, recompute_objective, dump_json


CASE_WEIGHTS = {
    "exact_basic": 0.25,
    "rotation_required": 0.25,
    "weight_value_tradeoff": 3.0,
    "upright_only": 0.25,
    "mixed_quantities": 1.0,
    "support_stacking": 4.0,
    "greedy_trap": 4.0,
    "dense_showcase": 1.0,
}


# ---------------------------------------------------------------------------
# Case builders – all dimensions derived from rng, properties preserved
# ---------------------------------------------------------------------------

def _make_problem(container: dict, cartons: list[dict]) -> dict:
    return {"container": container, "cartons": cartons}


def _ref(problem: dict, seed: int) -> dict:
    result = reference_pack(problem, seed=seed)
    value, volume = recompute_objective(problem, result)
    return {"value": value, "volume": volume, "placements": result["placements"]}





def _build_cases(rng: random.Random, seed: int) -> list[dict]:
    cases: list[dict] = []

    # ------------------------------------------------------------------
    # 1. Exact / basic – single carton fills the container exactly.
    #    Dimensions parameterized; property preserved by construction.
    # ------------------------------------------------------------------
    cw1 = rng.randint(6, 14)
    cd1 = rng.randint(6, 14)
    ch1 = rng.randint(6, 14)
    wt1 = rng.randint(3, 8)
    v1  = rng.randint(50, 150)
    p1 = _make_problem(
        {"width": cw1, "depth": cd1, "height": ch1, "maxWeight": wt1 * 2},
        [{"id": "exact_fill", "width": cw1, "depth": cd1, "height": ch1,
          "quantity": 1, "weight": wt1, "value": v1, "keepUpright": False}],
    )
    cases.append({"id": "exact_basic",
                  "description": "Single carton fills container exactly",
                  "problem": p1})

    # ------------------------------------------------------------------
    # 2. Rotation required.
    #
    #    Guarantees by construction:
    #      - ow2 > cw2 → original width doesn't fit along X-axis
    #      - ow2 <= cd2 → the rotated orientation (od2, ow2, oh2) fits:
    #          od2 <= cw2 (X) ✓   ow2 <= cd2 (Y) ✓   oh2 <= ch2 (Z) ✓
    #    So exactly one rotation always works, unrotated never works.
    # ------------------------------------------------------------------
    cw2 = rng.randint(4, 7)
    cd2 = rng.randint(cw2 + 3, 14)   # cd2 > cw2 strictly, room for ow2
    ch2 = rng.randint(4, 9)
    # Original width chosen in [cw2+1, cd2]: too wide for X, fits for Y
    ow2 = rng.randint(cw2 + 1, cd2)
    od2 = rng.randint(1, cw2)        # fits along X in rotated form
    oh2 = rng.randint(1, ch2)        # fits along Z in any orientation
    qty2 = rng.randint(1, 3)
    p2 = _make_problem(
        {"width": cw2, "depth": cd2, "height": ch2, "maxWeight": qty2 * 20},
        [{"id": "rot_item", "width": ow2, "depth": od2, "height": oh2,
          "quantity": qty2, "weight": rng.randint(3, 8),
          "value": rng.randint(15, 40), "keepUpright": False}],
    )
    cases.append({"id": "rotation_required",
                  "description": "Must rotate: original width exceeds container width; rotated orientation always fits",
                  "problem": p2})

    # ------------------------------------------------------------------
    # 3. Weight / value tradeoff – GREEDY TRAP #1.
    #    One heavy high-value item versus two lighter items whose combined
    #    value exceeds the heavy one.  maxWeight allows heavy alone OR
    #    the pair, but not heavy + either light.
    # ------------------------------------------------------------------
    side3 = rng.randint(6, 12)
    half3 = side3 // 2
    hw3   = rng.randint(8, 14)      # heavy weight
    hv3   = rng.randint(40, 70)     # heavy value (highest individually)
    # light pair: weight such that heavy + either light exceeds maxWeight
    lw3   = hw3 // 2 + 1
    # pair combined value > heavy value
    lv3   = (hv3 + rng.randint(5, 25)) // 2 + 1
    # maxWeight: pair fits (2*lw3 <= maxWeight) but heavy+light doesn't (hw3+lw3 > maxWeight)
    max_wt3 = 2 * lw3  # exactly fits pair; heavy alone fits too (hw3 < 2*lw3 when lw3>hw3/2)
    p3 = _make_problem(
        {"width": side3, "depth": side3, "height": side3, "maxWeight": max_wt3},
        [
            {"id": "heavy_item", "width": side3, "depth": side3, "height": half3,
             "quantity": 1, "weight": hw3, "value": hv3, "keepUpright": False},
            {"id": "light_a", "width": side3, "depth": side3, "height": half3,
             "quantity": 1, "weight": lw3, "value": lv3, "keepUpright": False},
            {"id": "light_b", "width": side3, "depth": side3, "height": half3,
             "quantity": 1, "weight": lw3, "value": lv3, "keepUpright": False},
        ],
    )
    cases.append({"id": "weight_value_tradeoff",
                  "description": "Greedy-by-value picks heavy item; optimal picks two lighter items for more total value",
                  "problem": p3})

    # ------------------------------------------------------------------
    # 4. Upright only – keepUpright constraint must be respected.
    # ------------------------------------------------------------------
    cw4  = rng.randint(14, 24)
    cd4  = rng.randint(8, 14)
    cup_h = rng.randint(4, 8)
    cup_w = rng.randint(2, 4)
    cup_d = rng.randint(2, 4)
    cup_q = rng.randint(4, 8)
    flat_w = rng.randint(4, 7)
    flat_d = rng.randint(4, 7)
    flat_h = rng.randint(1, 3)
    flat_q = rng.randint(1, 3)
    p4 = _make_problem(
        {"width": cw4, "depth": cd4, "height": cup_h + flat_h + 1, "maxWeight": 500},
        [
            {"id": "upright_item", "width": cup_w, "depth": cup_d, "height": cup_h,
             "quantity": cup_q, "weight": rng.randint(1, 4), "value": rng.randint(8, 18),
             "keepUpright": True},
            {"id": "flat_item", "width": flat_w, "depth": flat_d, "height": flat_h,
             "quantity": flat_q, "weight": rng.randint(2, 5), "value": rng.randint(5, 12),
             "keepUpright": False},
        ],
    )
    cases.append({"id": "upright_only",
                  "description": "keepUpright items must not be rotated to lay flat",
                  "problem": p4})

    # ------------------------------------------------------------------
    # 5. Mixed quantities – multiple item types, varying quantities.
    # ------------------------------------------------------------------
    cs5 = rng.randint(10, 16)
    q5a = rng.randint(4, 10)
    q5b = rng.randint(2, 6)
    q5c = rng.randint(1, 3)
    s5a = rng.randint(2, 4)
    s5b = rng.choice([4, 6, 5])
    p5 = _make_problem(
        {"width": cs5, "depth": cs5, "height": cs5, "maxWeight": 600},
        [
            {"id": "sm_cube", "width": s5a, "depth": s5a, "height": s5a,
             "quantity": q5a, "weight": 1, "value": rng.randint(3, 7), "keepUpright": False},
            {"id": "rect_bar", "width": s5b, "depth": s5a, "height": s5a,
             "quantity": q5b, "weight": 2, "value": rng.randint(6, 12), "keepUpright": False},
            {"id": "slab", "width": cs5, "depth": cs5, "height": s5a,
             "quantity": q5c, "weight": 8, "value": rng.randint(15, 30), "keepUpright": False},
        ],
    )
    cases.append({"id": "mixed_quantities",
                  "description": "Multiple item types and quantities",
                  "problem": p5})

    # ------------------------------------------------------------------
    # 6. Support / stacking – three keepUpright layers, coverage exact.
    #
    #    Even width/depth chosen so halving is exact.  keepUpright=True on
    #    all items prevents rotation of slabs into unrelated walls.
    #    Layout:
    #      Layer 0 (z=0):       1 base  cw6×cd6×sl6  covers container floor
    #      Layer 1 (z=sl6):     2 mids  cw6×(cd6/2)×sl6  cover base exactly
    #      Layer 2 (z=2*sl6):   4 tops  (cw6/2)×(cd6/2)×sl6  cover mids exactly
    # ------------------------------------------------------------------
    cw6 = rng.choice([8, 10, 12])      # even width
    cd6 = rng.choice([8, 10, 12])      # even depth
    sl6 = rng.randint(2, 4)            # layer height
    ch6 = 3 * sl6 + rng.randint(0, 2) # room for 3 layers
    p6 = _make_problem(
        {"width": cw6, "depth": cd6, "height": ch6, "maxWeight": 300},
        [
            {"id": "base_slab", "width": cw6, "depth": cd6, "height": sl6,
             "quantity": 1, "weight": 8, "value": rng.randint(10, 20),
             "keepUpright": True},
            {"id": "mid_half",  "width": cw6, "depth": cd6 // 2, "height": sl6,
             "quantity": 2, "weight": 4, "value": rng.randint(8, 15),
             "keepUpright": True},
            {"id": "top_qtr",   "width": cw6 // 2, "depth": cd6 // 2, "height": sl6,
             "quantity": 4, "weight": 2, "value": rng.randint(4, 9),
             "keepUpright": True},
        ],
    )
    cases.append({"id": "support_stacking",
                  "description": "Three keepUpright slab layers; each must be fully supported by the layer below",
                  "problem": p6})

    # ------------------------------------------------------------------
    # 7. Greedy trap – GREEDY TRAP #2.
    #
    #    Guaranteed invariants (proven by construction):
    #      A. big_v > small_v           (big has highest individual value)
    #      B. big_v/big_vol > small_v/small_vol  (big has higher value density)
    #      C. grid_cap * small_v > big_v  (all small beats big alone)
    #      D. reference.value > big_v   (reference packer avoids big)
    #
    #    Construction:
    #      side7 ∈ {8,10,12}, big_s = side7-1 → 1-unit gaps after placing big
    #      → no 2×2×2 cube fits in any residual gap → big+residual = big_v
    #      small_s = 2 → grid_cap = (side7//2)³ tiles container perfectly
    #      small_q = grid_cap → weight-ascending packer fills container with
    #        small items only, then big can't fit → value = grid_cap*small_v > big_v
    #
    #    Window for small_v: (big_v/grid_cap, 8*big_v/big_vol)
    #      Lower bound guarantees C; upper bound guarantees B.
    #      min_big_v ensures this window always contains an integer.
    # ------------------------------------------------------------------
    side7    = rng.choice([8, 10, 12])
    big_s    = side7 - 1
    small_s  = 2
    grid_cap = (side7 // small_s) ** 3   # perfect tiling: grid_cap * small_s³ = side7³
    big_vol  = big_s ** 3
    small_vol = small_s ** 3              # = 8

    # Minimum big_v so window (big_v/grid_cap, 8*big_v/big_vol) spans ≥1 integer:
    # width = big_v*(8*grid_cap - big_vol)/(big_vol*grid_cap) ≥ 1
    # → big_v ≥ ceil(big_vol*grid_cap / (8*grid_cap - big_vol))
    denom    = small_vol * grid_cap - big_vol   # always > 0 (proven for side7 ∈ {8,10,12})
    min_big_v = (big_vol * grid_cap + denom - 1) // denom   # ceil division
    big_v    = min_big_v + rng.randint(0, min_big_v // 2)

    # small_v chosen in the open interval (big_v/grid_cap, small_vol*big_v/big_vol)
    small_v_lo = big_v // grid_cap + 1
    small_v_hi = (small_vol * big_v - 1) // big_vol   # floor of strict upper bound
    if small_v_hi < small_v_lo:
        small_v_hi = small_v_lo   # safety; invariant B may be boundary-tight but C holds
    small_v = rng.randint(small_v_lo, small_v_hi)
    small_q = grid_cap   # all fit without big; exactly tiles container

    # Runtime assertions (guard against future parametrisation errors)
    assert big_v > small_v, f"greedy_trap: big_v={big_v} must exceed small_v={small_v}"
    assert big_v * small_vol >= small_v * big_vol, (
        f"greedy_trap: density invariant failed big_v={big_v} small_v={small_v}"
    )
    assert grid_cap * small_v > big_v, (
        f"greedy_trap: total small value {grid_cap*small_v} must exceed big_v={big_v}"
    )

    p7 = _make_problem(
        {"width": side7, "depth": side7, "height": side7, "maxWeight": 2000},
        [
            {"id": "big_item",   "width": big_s,   "depth": big_s,   "height": big_s,
             "quantity": 1, "weight": 10, "value": big_v, "keepUpright": False},
            {"id": "small_item", "width": small_s, "depth": small_s, "height": small_s,
             "quantity": small_q, "weight": 1, "value": small_v, "keepUpright": False},
        ],
    )
    cases.append({
        "id": "greedy_trap",
        "description": (
            "Greedy-by-value and greedy-by-density both pick big_item; "
            "optimal packs grid_cap small items for higher total value"
        ),
        "problem": p7,
        # Store the key invariant values as metadata so tests can assert them
        "_trap_meta": {
            "big_v": big_v, "small_v": small_v,
            "big_vol": big_vol, "small_vol": small_vol,
            "grid_cap": grid_cap, "side7": side7,
        },
    })

    # ------------------------------------------------------------------
    # 8. Dense showcase – rich mixed scenario, deterministic probe.
    # ------------------------------------------------------------------
    cw8 = rng.randint(16, 24)
    cd8 = rng.randint(12, 18)
    ch8 = rng.randint(10, 16)
    p8 = _make_problem(
        {"width": cw8, "depth": cd8, "height": ch8, "maxWeight": 400},
        [
            {"id": "book",    "width": rng.randint(4, 6), "depth": rng.randint(3, 5), "height": rng.randint(1, 3),
             "quantity": rng.randint(4, 8), "weight": rng.randint(2, 4), "value": rng.randint(8, 16),
             "keepUpright": False},
            {"id": "vase",    "width": rng.randint(2, 4), "depth": rng.randint(2, 4),
             "height": rng.randint(6, 10),
             "quantity": rng.randint(2, 4), "weight": rng.randint(3, 6), "value": rng.randint(12, 22),
             "keepUpright": True},
            {"id": "box_med", "width": rng.randint(3, 5), "depth": rng.randint(3, 5),
             "height": rng.randint(3, 5),
             "quantity": rng.randint(3, 6), "weight": rng.randint(4, 7), "value": rng.randint(7, 14),
             "keepUpright": False},
            {"id": "plank",   "width": rng.randint(6, 12), "depth": rng.randint(2, 3), "height": 1,
             "quantity": rng.randint(4, 7), "weight": rng.randint(1, 3), "value": rng.randint(4, 8),
             "keepUpright": False},
            {"id": "tiny_cube", "width": 2, "depth": 2, "height": 2,
             "quantity": rng.randint(8, 14), "weight": 1, "value": rng.randint(2, 5),
             "keepUpright": False},
        ],
    )
    cases.append({"id": "dense_showcase",
                  "description": "Dense mixed scenario: deterministic probe case",
                  "problem": p8})

    return cases


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def generate(output_dir: str, seed: int | None = None) -> Path:
    """
    Generate the hidden bundle into output_dir/hidden_cases.json.

    If seed is None, a cryptographically random integer is chosen.
    Returns the Path to the written file.
    """
    if seed is None:
        seed = secrets.randbits(31)   # 31-bit positive integer

    rng = random.Random(seed)
    raw_cases = _build_cases(rng, seed)

    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    bundle_cases = []
    for c in raw_cases:
        ref = _ref(c["problem"], seed)
        bundle_cases.append({
            "id": c["id"],
            "description": c["description"],
            "weight": CASE_WEIGHTS[c["id"]],
            "problem": c["problem"],
            "reference": ref,
        })

    bundle = {
        "schema_version": 1,
        "seed": seed,
        "cases": bundle_cases,
        "probe_case_id": "dense_showcase",
    }

    out_file = out / "hidden_cases.json"
    dump_json(bundle, str(out_file))
    print(f"Generated {len(bundle_cases)} cases (seed={seed}) -> {out_file}")
    return out_file


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Generate hidden benchmark cases for carton-packing Eval"
    )
    parser.add_argument(
        "--output", required=True, metavar="DIR",
        help="Directory to write hidden_cases.json",
    )
    parser.add_argument(
        "--seed", type=int, default=None,
        help="RNG seed (omit for cryptographically random; explicit seeds are reproducible)",
    )
    args = parser.parse_args()
    generate(args.output, args.seed)


if __name__ == "__main__":
    main()
