"""
Carton-packing independent validation, scoring, and reference packer.

Domain contract
---------------
Problem JSON:
  container: {width, depth, height, maxWeight}
  cartons:   [{id, width, depth, height, quantity, weight, value, keepUpright}]

Result JSON:
  placements: [{cartonId, instance, x, y, z, width, depth, height}]

Validation rules enforced here (with structured issue codes):
  BOUNDS          – placement box extends outside container
  OVERLAP         – positive-volume intersection between two placements
  ORIENTATION     – keepUpright carton placed with wrong height
  INVALID_ORIENT  – dimension triple not a valid rotation of the original
  QUANTITY        – more instances than allowed
  DUP_INSTANCE    – (cartonId, instance) pair appears more than once
  BAD_INSTANCE    – instance index < 0 or >= quantity
  WEIGHT          – total packed weight exceeds maxWeight
  SUPPORT         – placement at z>0 whose base is not 100% covered

Objective: lexicographic max (total_value, total_volume).
"""

from __future__ import annotations

import json
import math
import random
from itertools import permutations
from typing import Any

# ---------------------------------------------------------------------------
# JSON I/O helpers
# ---------------------------------------------------------------------------

def load_json(path: str) -> Any:
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def dump_json(obj: Any, path: str) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(obj, fh, indent=2, ensure_ascii=False)
        fh.write("\n")


def dumps_json(obj: Any) -> str:
    return json.dumps(obj, indent=2, ensure_ascii=False)


# ---------------------------------------------------------------------------
# Orientation helpers
# ---------------------------------------------------------------------------

def all_orientations(w: int, d: int, h: int) -> list[tuple[int, int, int]]:
    """Return all distinct (width, depth, height) axis-aligned permutations, sorted for stability."""
    return sorted({p for p in permutations([w, d, h])})


def upright_orientations(w: int, d: int, h: int) -> list[tuple[int, int, int]]:
    """keepUpright: height stays original; width/depth may swap."""
    seen = set()
    result = []
    for (pw, pd) in [(w, d), (d, w)]:
        t = (pw, pd, h)
        if t not in seen:
            seen.add(t)
            result.append(t)
    return result


def valid_orientations(w: int, d: int, h: int, keep_upright: bool) -> list[tuple[int, int, int]]:
    if keep_upright:
        return upright_orientations(w, d, h)
    return all_orientations(w, d, h)


# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------

def _intervals_overlap(a0: int, a1: int, b0: int, b1: int) -> bool:
    """True if open intervals (a0,a1) and (b0,b1) overlap (positive length)."""
    return a0 < b1 and b0 < a1


def boxes_overlap(p: dict, q: dict) -> bool:
    """Positive-volume overlap between two placements."""
    return (
        _intervals_overlap(p["x"], p["x"] + p["width"],  q["x"], q["x"] + q["width"])
        and _intervals_overlap(p["y"], p["y"] + p["depth"],  q["y"], q["y"] + q["depth"])
        and _intervals_overlap(p["z"], p["z"] + p["height"], q["z"], q["z"] + q["height"])
    )


# ---------------------------------------------------------------------------
# Support check: coordinate-compression rectangle-union area
# ---------------------------------------------------------------------------

def _rect_union_area(rects: list[tuple[int, int, int, int]]) -> int:
    """Exact area of the union of axis-aligned rectangles.

    Each rect is (x0, y0, x1, y1) with integer coords.
    Uses coordinate compression + sweep.
    """
    if not rects:
        return 0
    xs = sorted({x for r in rects for x in (r[0], r[2])})
    ys = sorted({y for r in rects for y in (r[1], r[3])})
    xi = {v: i for i, v in enumerate(xs)}
    yi = {v: i for i, v in enumerate(ys)}
    grid = [[0] * (len(ys) - 1) for _ in range(len(xs) - 1)]
    for (x0, y0, x1, y1) in rects:
        for i in range(xi[x0], xi[x1]):
            for j in range(yi[y0], yi[y1]):
                grid[i][j] = 1
    area = 0
    for i in range(len(xs) - 1):
        for j in range(len(ys) - 1):
            if grid[i][j]:
                area += (xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j])
    return area


def check_support(placement: dict, all_placements: list[dict]) -> bool:
    """Return True if placement is on the floor (z==0) or 100% base covered."""
    bx0 = placement["x"]
    by0 = placement["y"]
    bx1 = bx0 + placement["width"]
    by1 = by0 + placement["depth"]
    bz  = placement["z"]

    if bz == 0:
        return True

    base_area = placement["width"] * placement["depth"]

    supports = []
    for q in all_placements:
        if q is placement:
            continue
        # q's top face must be exactly at bz
        if q["z"] + q["height"] != bz:
            continue
        # horizontal overlap
        ox0 = max(bx0, q["x"])
        ox1 = min(bx1, q["x"] + q["width"])
        oy0 = max(by0, q["y"])
        oy1 = min(by1, q["y"] + q["depth"])
        if ox1 > ox0 and oy1 > oy0:
            supports.append((ox0, oy0, ox1, oy1))

    covered = _rect_union_area(supports)
    return covered == base_area


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def validate(problem: dict, result: dict) -> dict:
    """
    Validate a candidate result against the problem.

    Returns:
      {
        "valid": bool,
        "issues": [{"code": str, "detail": str}, ...],
        "total_value": int,
        "total_volume": int,
        "packed_weight": int,
      }
    """
    issues: list[dict] = []

    container = problem["container"]
    cw = container["width"]
    cd = container["depth"]
    ch = container["height"]
    max_weight = container["maxWeight"]

    carton_map: dict[str, dict] = {c["id"]: c for c in problem["cartons"]}

    placements = result.get("placements", [])

    # Count instances per cartonId and check duplicates
    instance_seen: set[tuple] = set()
    instance_count: dict[str, int] = {}

    total_value = 0
    total_volume = 0
    packed_weight = 0

    for idx, p in enumerate(placements):
        cid = p.get("cartonId", "")
        inst = p.get("instance", -1)
        px, py, pz = p.get("x", 0), p.get("y", 0), p.get("z", 0)
        pw, pd, ph = p.get("width", 0), p.get("depth", 0), p.get("height", 0)

        key = (cid, inst)
        if key in instance_seen:
            issues.append({"code": "DUP_INSTANCE", "detail": f"({cid}, {inst}) appears more than once"})
        instance_seen.add(key)
        instance_count[cid] = instance_count.get(cid, 0) + 1

        # Instance index range check (0-based, must be < quantity)
        if cid in carton_map:
            allowed = carton_map[cid].get("quantity", 1)
            if not isinstance(inst, int) or inst < 0 or inst >= allowed:
                issues.append({
                    "code": "BAD_INSTANCE",
                    "detail": f"placement {idx} ({cid}): instance={inst} out of range [0, {allowed})",
                })

        # Bounds check
        if px < 0 or py < 0 or pz < 0 or px + pw > cw or py + pd > cd or pz + ph > ch:
            issues.append({"code": "BOUNDS", "detail": f"placement {idx} ({cid}:{inst}) out of container"})

        if cid not in carton_map:
            issues.append({"code": "UNKNOWN_ID", "detail": f"cartonId {cid!r} not in problem"})
            continue

        carton = carton_map[cid]
        ow, od, oh = carton["width"], carton["depth"], carton["height"]
        keep_up = carton.get("keepUpright", False)

        # Orientation validity
        valid_orients = valid_orientations(ow, od, oh, keep_up)
        if (pw, pd, ph) not in valid_orients:
            if keep_up:
                issues.append({"code": "ORIENTATION", "detail": f"placement {idx} ({cid}:{inst}) keepUpright violated"})
            else:
                issues.append({"code": "INVALID_ORIENT", "detail": f"placement {idx} ({cid}:{inst}) not a valid rotation"})

        total_value += carton.get("value", 0)
        total_volume += pw * pd * ph
        packed_weight += carton.get("weight", 0)

    # Quantity checks
    for cid, cnt in instance_count.items():
        if cid in carton_map:
            allowed = carton_map[cid].get("quantity", 1)
            if cnt > allowed:
                issues.append({"code": "QUANTITY", "detail": f"{cid}: placed {cnt} but max is {allowed}"})

    # Weight check
    if packed_weight > max_weight:
        issues.append({"code": "WEIGHT", "detail": f"packed weight {packed_weight} exceeds maxWeight {max_weight}"})

    # Overlap check (O(n^2) – acceptable for eval sizes)
    for i in range(len(placements)):
        for j in range(i + 1, len(placements)):
            if boxes_overlap(placements[i], placements[j]):
                pi = placements[i]
                pj = placements[j]
                issues.append({
                    "code": "OVERLAP",
                    "detail": f"placement {i} ({pi.get('cartonId')}:{pi.get('instance')}) overlaps {j} ({pj.get('cartonId')}:{pj.get('instance')})",
                })

    # Support check
    for idx, p in enumerate(placements):
        if p.get("z", 0) > 0:
            if not check_support(p, placements):
                issues.append({
                    "code": "SUPPORT",
                    "detail": f"placement {idx} ({p.get('cartonId')}:{p.get('instance')}) at z={p.get('z')} not fully supported",
                })

    if placements != canonical_placements(placements):
        issues.append({
            "code": "ORDER",
            "detail": (
                "placements must be sorted by cartonId, instance, x, y, z"
            ),
        })

    return {
        "valid": len(issues) == 0,
        "issues": issues,
        "total_value": total_value,
        "total_volume": total_volume,
        "packed_weight": packed_weight,
    }


# ---------------------------------------------------------------------------
# Objective recomputation
# ---------------------------------------------------------------------------

def recompute_objective(problem: dict, result: dict) -> tuple[int, int]:
    """Return (total_value, total_volume) recomputed from placements; never trust candidate."""
    carton_map = {c["id"]: c for c in problem["cartons"]}
    total_value = 0
    total_volume = 0
    for p in result.get("placements", []):
        cid = p.get("cartonId")
        if cid in carton_map:
            total_value += carton_map[cid].get("value", 0)
        pw, pd, ph = p.get("width", 1), p.get("depth", 1), p.get("height", 1)
        total_volume += pw * pd * ph
    return total_value, total_volume


# ---------------------------------------------------------------------------
# Canonical placement ordering (deterministic comparison)
# ---------------------------------------------------------------------------

def canonical_key(p: dict) -> tuple:
    return (
        p.get("cartonId", ""),
        p.get("instance", 0),
        p.get("x", 0),
        p.get("y", 0),
        p.get("z", 0),
    )


def canonical_placements(placements: list[dict]) -> list[dict]:
    return sorted(placements, key=canonical_key)


def placements_equal(a: list[dict], b: list[dict]) -> bool:
    ca = canonical_placements(a)
    cb = canonical_placements(b)
    if len(ca) != len(cb):
        return False
    for p, q in zip(ca, cb):
        if canonical_key(p) != canonical_key(q):
            return False
        if (p.get("width"), p.get("depth"), p.get("height")) != (q.get("width"), q.get("depth"), q.get("height")):
            return False
    return True


# ---------------------------------------------------------------------------
# Capped quality ratio helpers
# ---------------------------------------------------------------------------

def capped_value_ratio(candidate_value: int, reference_value: int) -> float:
    if reference_value <= 0:
        return 1.0 if candidate_value >= 0 else 0.0
    return min(1.0, candidate_value / reference_value)


def capped_volume_ratio(candidate_volume: int, reference_volume: int) -> float:
    if reference_volume <= 0:
        return 1.0 if candidate_volume >= 0 else 0.0
    return min(1.0, candidate_volume / reference_volume)


def case_score(value_ratio: float, volume_ratio: float) -> float:
    return 0.9 * value_ratio + 0.1 * volume_ratio


# ---------------------------------------------------------------------------
# Reference packer (deterministic multi-start bounded heuristic)
# ---------------------------------------------------------------------------

class _ExtremePointPacker:
    """Extreme-point 3D bin packer for a single container."""

    def __init__(self, cw: int, cd: int, ch: int, max_weight: int):
        self.cw = cw
        self.cd = cd
        self.ch = ch
        self.max_weight = max_weight
        self.placements: list[dict] = []
        self.packed_weight = 0
        # Extreme points start at origin
        self._eps: list[tuple[int, int, int]] = [(0, 0, 0)]

    def _fits(self, x: int, y: int, z: int, w: int, d: int, h: int) -> bool:
        if x + w > self.cw or y + d > self.cd or z + h > self.ch:
            return False
        # Check overlap with existing placements
        candidate = {"x": x, "y": y, "z": z, "width": w, "depth": d, "height": h}
        for p in self.placements:
            if boxes_overlap(candidate, p):
                return False
        return True

    def _support_ok(self, x: int, y: int, z: int, w: int, d: int, h: int) -> bool:
        if z == 0:
            return True
        candidate = {"x": x, "y": y, "z": z, "width": w, "depth": d, "height": h}
        dummy = list(self.placements) + [candidate]
        # remove candidate from dummy for support check – we check placements only
        return check_support(candidate, self.placements)

    def try_place(self, cid: str, inst: int, w: int, d: int, h: int, weight: int) -> bool:
        if self.packed_weight + weight > self.max_weight:
            return False
        # Try each extreme point, prefer lowest z then lowest x then lowest y
        for (x, y, z) in sorted(self._eps, key=lambda p: (p[2], p[0], p[1])):
            if self._fits(x, y, z, w, d, h) and self._support_ok(x, y, z, w, d, h):
                self.placements.append({
                    "cartonId": cid,
                    "instance": inst,
                    "x": x, "y": y, "z": z,
                    "width": w, "depth": d, "height": h,
                })
                self.packed_weight += weight
                # Add new extreme points
                self._eps.append((x + w, y, z))
                self._eps.append((x, y + d, z))
                self._eps.append((x, y, z + h))
                # Deduplicate
                self._eps = list(set(self._eps))
                return True
        return False


def _run_packer(
    problem: dict,
    carton_order: list[dict],
    orient_strategy: str,
) -> dict:
    """Pack using a fixed carton order and orientation preference."""
    container = problem["container"]
    packer = _ExtremePointPacker(
        container["width"], container["depth"], container["height"],
        container["maxWeight"],
    )
    instance_counts: dict[str, int] = {}

    for carton in carton_order:
        cid = carton["id"]
        quantity = carton.get("quantity", 1)
        keep_up = carton.get("keepUpright", False)
        ow, od, oh = carton["width"], carton["depth"], carton["height"]
        orients = valid_orientations(ow, od, oh, keep_up)

        # Orientation ordering strategy
        if orient_strategy == "volume_desc":
            orients = sorted(orients, key=lambda o: -(o[0] * o[1] * o[2]))
        elif orient_strategy == "height_asc":
            orients = sorted(orients, key=lambda o: o[2])
        # else: as-is

        placed = instance_counts.get(cid, 0)
        for _ in range(quantity):
            placed_this = False
            for (w, d, h) in orients:
                if packer.try_place(cid, placed, w, d, h, carton.get("weight", 0)):
                    placed += 1
                    placed_this = True
                    break
            if not placed_this:
                break  # Can't fit any more of this carton
        instance_counts[cid] = placed

    return {"placements": canonical_placements(packer.placements)}


def reference_pack(problem: dict, seed: int = 42) -> dict:
    """
    Deterministic multi-start reference packer.

    Tries several item orderings and orientation strategies, returns the
    lexicographically best (value, volume) result found.

    Reference ratios are capped at 1.0; this packer is NOT claimed optimal.
    """
    cartons = problem["cartons"]
    rng = random.Random(seed)

    # Build candidate orderings
    orderings: list[list[dict]] = []

    # By value descending
    orderings.append(sorted(cartons, key=lambda c: -c.get("value", 0)))
    # By value density (value / volume) descending
    orderings.append(sorted(cartons, key=lambda c: -(c.get("value", 0) / max(1, c["width"] * c["depth"] * c["height"]))))
    # By volume descending
    orderings.append(sorted(cartons, key=lambda c: -(c["width"] * c["depth"] * c["height"])))
    # By weight ascending (lighter first)
    orderings.append(sorted(cartons, key=lambda c: c.get("weight", 0)))
    # Shuffle several times
    for i in range(4):
        shuffled = list(cartons)
        rng.shuffle(shuffled)
        orderings.append(shuffled)

    orient_strategies = ["volume_desc", "height_asc", "default"]

    best_result: dict | None = None
    best_value = -1
    best_volume = -1

    for order in orderings:
        for strat in orient_strategies:
            result = _run_packer(problem, order, strat)
            v, vol = recompute_objective(problem, result)
            if (v, vol) > (best_value, best_volume):
                best_value = v
                best_volume = vol
                best_result = result

    return best_result or {"placements": []}
