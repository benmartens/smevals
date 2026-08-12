"""Independent validation, scoring, heuristics, and exact reference solving."""

from __future__ import annotations

import itertools
import json
from fractions import Fraction
from typing import Any, Callable


def dump_json(obj: Any, path: str) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(obj, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def canonical_json(obj: Any) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _problem_maps(problem: dict) -> tuple[dict[str, dict], dict[str, dict]]:
    return (
        {node["id"]: node for node in problem.get("nodes", [])},
        {shard["id"]: shard for shard in problem.get("shards", [])},
    )


def _exclusion_set(problem: dict) -> set[tuple[str, str]]:
    return {
        (item.get("shardId"), item.get("nodeId"))
        for item in problem.get("exclusions", [])
    }


def _current_map(problem: dict) -> dict[str, frozenset[str]]:
    return {
        item["shardId"]: frozenset(item.get("nodeIds", []))
        for item in problem.get("currentPlacements", [])
    }


def _validate_problem(problem: dict) -> list[dict]:
    issues: list[dict] = []
    if not isinstance(problem, dict):
        return [{"code": "invalid_problem", "detail": "problem must be an object"}]

    nodes: dict[str, dict] = {}
    raw_nodes = problem.get("nodes")
    if not isinstance(raw_nodes, list):
        issues.append({"code": "invalid_nodes", "detail": "nodes must be an array"})
        raw_nodes = []
    for node in raw_nodes:
        if not isinstance(node, dict):
            issues.append({"code": "invalid_node", "detail": repr(node)})
            continue
        node_id = node.get("id")
        if (
            not isinstance(node_id, str)
            or not node_id
            or not isinstance(node.get("zone"), str)
            or not node["zone"]
            or not isinstance(node.get("capacity"), int)
            or isinstance(node.get("capacity"), bool)
            or node["capacity"] <= 0
        ):
            issues.append({"code": "invalid_node", "detail": repr(node)})
            continue
        if node_id in nodes:
            issues.append({"code": "duplicate_node_id", "detail": node_id})
        nodes[node_id] = node

    shards: dict[str, dict] = {}
    raw_shards = problem.get("shards")
    if not isinstance(raw_shards, list):
        issues.append({"code": "invalid_shards", "detail": "shards must be an array"})
        raw_shards = []
    for shard in raw_shards:
        if not isinstance(shard, dict):
            issues.append({"code": "invalid_shard", "detail": repr(shard)})
            continue
        shard_id = shard.get("id")
        if (
            not isinstance(shard_id, str)
            or not shard_id
            or not isinstance(shard.get("size"), int)
            or isinstance(shard.get("size"), bool)
            or shard["size"] <= 0
            or not isinstance(shard.get("replicationFactor"), int)
            or isinstance(shard.get("replicationFactor"), bool)
            or shard["replicationFactor"] <= 0
        ):
            issues.append({"code": "invalid_shard", "detail": repr(shard)})
            continue
        if shard_id in shards:
            issues.append({"code": "duplicate_shard_id", "detail": shard_id})
        shards[shard_id] = shard

    exclusions: set[tuple[str, str]] = set()
    raw_exclusions = problem.get("exclusions")
    if not isinstance(raw_exclusions, list):
        issues.append(
            {"code": "invalid_exclusions", "detail": "exclusions must be an array"}
        )
        raw_exclusions = []
    for item in raw_exclusions:
        if not isinstance(item, dict):
            issues.append({"code": "invalid_exclusion", "detail": repr(item)})
            continue
        pair = (item.get("shardId"), item.get("nodeId"))
        if pair[0] not in shards or pair[1] not in nodes:
            issues.append({"code": "invalid_exclusion", "detail": repr(item)})
        elif pair in exclusions:
            issues.append({"code": "duplicate_exclusion", "detail": repr(item)})
        exclusions.add(pair)

    current: set[str] = set()
    raw_current = problem.get("currentPlacements")
    if not isinstance(raw_current, list):
        issues.append(
            {
                "code": "invalid_current_placements",
                "detail": "currentPlacements must be an array",
            }
        )
        raw_current = []
    for placement in raw_current:
        if not isinstance(placement, dict):
            issues.append({"code": "invalid_current_placement", "detail": repr(placement)})
            continue
        shard_id = placement.get("shardId")
        node_ids = placement.get("nodeIds")
        if shard_id not in shards or not isinstance(node_ids, list):
            issues.append({"code": "invalid_current_placement", "detail": repr(placement)})
            continue
        if shard_id in current:
            issues.append({"code": "duplicate_current_shard", "detail": shard_id})
        current.add(shard_id)
        if (
            len(node_ids) != shards[shard_id]["replicationFactor"]
            or len(set(node_ids)) != len(node_ids)
            or any(node_id not in nodes for node_id in node_ids)
        ):
            issues.append({"code": "invalid_current_placement", "detail": shard_id})

    for shard_id, shard in shards.items():
        if shard_id not in current:
            issues.append({"code": "missing_current_shard", "detail": shard_id})
        eligible = [
            node
            for node in nodes.values()
            if node["capacity"] >= shard["size"]
            and (shard_id, node["id"]) not in exclusions
        ]
        if len(eligible) < shard["replicationFactor"]:
            issues.append({"code": "infeasible_shard", "detail": shard_id})
    return issues


def maximum_zone_diversity(
    problem: dict,
    shard: dict,
    nodes: dict[str, dict] | None = None,
) -> int:
    nodes = nodes or _problem_maps(problem)[0]
    exclusions = _exclusion_set(problem)
    zones = {
        node["zone"]
        for node in nodes.values()
        if node["capacity"] >= shard["size"]
        and (shard["id"], node["id"]) not in exclusions
    }
    return min(shard["replicationFactor"], len(zones))


def placement_options(problem: dict, shard: dict) -> list[tuple[str, ...]]:
    nodes, _ = _problem_maps(problem)
    exclusions = _exclusion_set(problem)
    eligible = sorted(
        node_id
        for node_id, node in nodes.items()
        if node["capacity"] >= shard["size"]
        and (shard["id"], node_id) not in exclusions
    )
    diversity = maximum_zone_diversity(problem, shard, nodes)
    return [
        combo
        for combo in itertools.combinations(eligible, shard["replicationFactor"])
        if len({nodes[node_id]["zone"] for node_id in combo}) == diversity
    ]


def _metrics(
    problem: dict,
    placements: dict[str, tuple[str, ...]],
) -> dict:
    nodes, shards = _problem_maps(problem)
    current = _current_map(problem)
    loads = {node_id: 0 for node_id in nodes}
    moved_bytes = 0
    moved_count = 0
    for shard_id, node_ids in placements.items():
        shard = shards[shard_id]
        for node_id in node_ids:
            loads[node_id] += shard["size"]
            if node_id not in current[shard_id]:
                moved_bytes += shard["size"]
                moved_count += 1
    utilizations = [
        Fraction(loads[node_id], nodes[node_id]["capacity"])
        for node_id in sorted(nodes)
    ]
    maximum = max(utilizations, default=Fraction(0))
    minimum = min(utilizations, default=Fraction(0))
    spread = maximum - minimum
    return {
        "node_loads": loads,
        "maximum_utilization": maximum,
        "utilization_spread": spread,
        "moved_bytes": moved_bytes,
        "moved_replica_count": moved_count,
    }


def objective_tuple(
    problem: dict,
    placements: dict[str, tuple[str, ...]],
) -> tuple:
    metrics = _metrics(problem, placements)
    canonical = tuple(
        (shard_id, placements[shard_id])
        for shard_id in sorted(placements)
    )
    return (
        metrics["maximum_utilization"],
        metrics["utilization_spread"],
        metrics["moved_bytes"],
        metrics["moved_replica_count"],
        canonical,
    )


def validate(problem: dict, result: dict) -> dict:
    issues = _validate_problem(problem)
    nodes, shards = _problem_maps(problem) if not issues else ({}, {})
    exclusions = _exclusion_set(problem)
    placements: dict[str, tuple[str, ...]] = {}

    target = result.get("targetPlacements") if isinstance(result, dict) else None
    if not isinstance(target, list):
        issues.append(
            {
                "code": "missing_target_placements",
                "detail": "targetPlacements must be an array",
            }
        )
        return {"valid": False, "issues": issues, "metrics": None}

    shard_order = [
        placement.get("shardId") if isinstance(placement, dict) else None
        for placement in target
    ]
    if shard_order != sorted(shard_order, key=lambda value: "" if value is None else value):
        issues.append(
            {"code": "noncanonical_shard_order", "detail": "sort by shardId"}
        )

    loads = {node_id: 0 for node_id in nodes}
    for placement in target:
        if not isinstance(placement, dict):
            issues.append({"code": "invalid_placement", "detail": repr(placement)})
            continue
        shard_id = placement.get("shardId")
        node_ids = placement.get("nodeIds")
        if shard_id not in shards:
            issues.append({"code": "unknown_shard", "detail": repr(shard_id)})
            continue
        if shard_id in placements:
            issues.append({"code": "duplicate_shard", "detail": shard_id})
            continue
        if not isinstance(node_ids, list) or any(
            not isinstance(node_id, str) for node_id in node_ids
        ):
            issues.append({"code": "invalid_node_ids", "detail": shard_id})
            continue
        if node_ids != sorted(node_ids):
            issues.append({"code": "noncanonical_node_order", "detail": shard_id})
        if len(node_ids) != shards[shard_id]["replicationFactor"]:
            issues.append({"code": "replica_count", "detail": shard_id})
        if len(set(node_ids)) != len(node_ids):
            issues.append({"code": "duplicate_node", "detail": shard_id})

        known_unique = set()
        for node_id in node_ids:
            if node_id not in nodes:
                issues.append(
                    {"code": "unknown_node", "detail": f"{shard_id}/{node_id}"}
                )
                continue
            if node_id in known_unique:
                continue
            known_unique.add(node_id)
            if (shard_id, node_id) in exclusions:
                issues.append(
                    {"code": "excluded_node", "detail": f"{shard_id}/{node_id}"}
                )
            loads[node_id] += shards[shard_id]["size"]

        diversity = len({nodes[node_id]["zone"] for node_id in known_unique})
        required = maximum_zone_diversity(problem, shards[shard_id], nodes)
        if diversity != required:
            issues.append(
                {
                    "code": "zone_diversity",
                    "detail": f"{shard_id}: {diversity} != {required}",
                }
            )
        placements[shard_id] = tuple(node_ids)

    for shard_id in shards:
        if shard_id not in placements:
            issues.append({"code": "missing_shard", "detail": shard_id})
    for node_id, load in loads.items():
        if load > nodes[node_id]["capacity"]:
            issues.append(
                {
                    "code": "capacity_exceeded",
                    "detail": (
                        f"{node_id}: {load} > {nodes[node_id]['capacity']}"
                    ),
                }
            )

    if issues:
        return {"valid": False, "issues": issues, "metrics": None}
    metrics = _metrics(problem, placements)
    metrics.update(
        {
            "maximum_utilization_float": float(metrics["maximum_utilization"]),
            "utilization_spread_float": float(metrics["utilization_spread"]),
        }
    )
    return {"valid": True, "issues": [], "metrics": metrics}


def serialize_metrics(metrics: dict) -> dict:
    maximum = metrics["maximum_utilization"]
    spread = metrics["utilization_spread"]
    return {
        "nodeLoads": dict(sorted(metrics["node_loads"].items())),
        "maximumUtilization": {
            "numerator": maximum.numerator,
            "denominator": maximum.denominator,
        },
        "utilizationSpread": {
            "numerator": spread.numerator,
            "denominator": spread.denominator,
        },
        "movedBytes": metrics["moved_bytes"],
        "movedReplicaCount": metrics["moved_replica_count"],
    }


def deserialize_objective(reference: dict) -> tuple[Fraction, Fraction, int, int]:
    metrics = reference["metrics"]
    maximum = metrics["maximumUtilization"]
    spread = metrics["utilizationSpread"]
    return (
        Fraction(maximum["numerator"], maximum["denominator"]),
        Fraction(spread["numerator"], spread["denominator"]),
        int(metrics["movedBytes"]),
        int(metrics["movedReplicaCount"]),
    )


def _result(placements: dict[str, tuple[str, ...]]) -> dict:
    return {
        "targetPlacements": [
            {"shardId": shard_id, "nodeIds": list(placements[shard_id])}
            for shard_id in sorted(placements)
        ]
    }


def reference_rebalance(problem: dict) -> dict:
    """Exact bounded branch-and-bound over per-shard replica combinations."""
    issues = _validate_problem(problem)
    if issues:
        raise ValueError(f"invalid reference problem: {issues}")
    nodes, shards = _problem_maps(problem)
    node_ids = sorted(nodes)
    current = _current_map(problem)
    options = {
        shard_id: placement_options(problem, shard)
        for shard_id, shard in shards.items()
    }
    if any(not shard_options for shard_options in options.values()):
        raise ValueError("problem has no feasible per-shard placement")

    order = sorted(
        shards,
        key=lambda shard_id: (
            len(options[shard_id]),
            -shards[shard_id]["size"],
            shard_id,
        ),
    )
    remaining_bytes = [0] * (len(order) + 1)
    for index in range(len(order) - 1, -1, -1):
        shard = shards[order[index]]
        remaining_bytes[index] = (
            remaining_bytes[index + 1]
            + shard["size"] * shard["replicationFactor"]
        )

    loads = {node_id: 0 for node_id in node_ids}
    assigned: dict[str, tuple[str, ...]] = {}
    best_objective: tuple | None = None
    best_result: dict | None = None
    seen: dict[tuple[int, tuple[int, ...]], tuple] = {}

    def can_stay_at_best(index: int) -> bool:
        if best_objective is None:
            return True
        maximum: Fraction = best_objective[0]
        available = 0
        for node_id in node_ids:
            node = nodes[node_id]
            limit = maximum.numerator * node["capacity"] // maximum.denominator
            available += max(0, limit - loads[node_id])
        return available >= remaining_bytes[index]

    def search(index: int, moved_bytes: int, moved_count: int) -> None:
        nonlocal best_objective, best_result
        current_max = max(
            (
                Fraction(loads[node_id], nodes[node_id]["capacity"])
                for node_id in node_ids
            ),
            default=Fraction(0),
        )
        if best_objective is not None and current_max > best_objective[0]:
            return
        if not can_stay_at_best(index):
            return

        state = (index, tuple(loads[node_id] for node_id in node_ids))
        prefix = tuple(
            (shard_id, assigned[shard_id])
            for shard_id in sorted(assigned)
        )
        state_value = (moved_bytes, moved_count, prefix)
        previous = seen.get(state)
        if previous is not None and previous <= state_value:
            return
        seen[state] = state_value

        if index == len(order):
            candidate = objective_tuple(problem, assigned)
            if best_objective is None or candidate < best_objective:
                best_objective = candidate
                best_result = _result(assigned)
            return

        shard_id = order[index]
        shard = shards[shard_id]
        dynamic_options = []
        for combo in options[shard_id]:
            if any(
                loads[node_id] + shard["size"] > nodes[node_id]["capacity"]
                for node_id in combo
            ):
                continue
            projected = loads.copy()
            for node_id in combo:
                projected[node_id] += shard["size"]
            projected_util = [
                Fraction(projected[node_id], nodes[node_id]["capacity"])
                for node_id in node_ids
            ]
            additions = sum(
                1 for node_id in combo if node_id not in current[shard_id]
            )
            dynamic_options.append(
                (
                    max(projected_util),
                    max(projected_util) - min(projected_util),
                    additions * shard["size"],
                    additions,
                    combo,
                )
            )

        for _, _, added_bytes, additions, combo in sorted(dynamic_options):
            for node_id in combo:
                loads[node_id] += shard["size"]
            assigned[shard_id] = combo
            search(index + 1, moved_bytes + added_bytes, moved_count + additions)
            del assigned[shard_id]
            for node_id in combo:
                loads[node_id] -= shard["size"]

    search(0, 0, 0)
    if best_result is None:
        raise ValueError("problem has no capacity-feasible placement")
    return best_result


def _first_solution(
    problem: dict,
    option_key: Callable[[dict, dict, tuple[str, ...], dict[str, int]], tuple],
) -> dict:
    nodes, shards = _problem_maps(problem)
    options = {
        shard_id: placement_options(problem, shard)
        for shard_id, shard in shards.items()
    }
    order = sorted(
        shards,
        key=lambda shard_id: (
            len(options[shard_id]),
            -shards[shard_id]["size"],
            shard_id,
        ),
    )
    loads = {node_id: 0 for node_id in nodes}
    assigned: dict[str, tuple[str, ...]] = {}

    def search(index: int) -> bool:
        if index == len(order):
            return True
        shard_id = order[index]
        shard = shards[shard_id]
        choices = sorted(
            options[shard_id],
            key=lambda combo: option_key(problem, shard, combo, loads),
        )
        for combo in choices:
            if any(
                loads[node_id] + shard["size"] > nodes[node_id]["capacity"]
                for node_id in combo
            ):
                continue
            assigned[shard_id] = combo
            for node_id in combo:
                loads[node_id] += shard["size"]
            if search(index + 1):
                return True
            for node_id in combo:
                loads[node_id] -= shard["size"]
            del assigned[shard_id]
        return False

    return _result(assigned) if search(0) else {"targetPlacements": []}


def first_feasible(problem: dict) -> dict:
    return _first_solution(
        problem,
        lambda _problem, _shard, combo, _loads: combo,
    )


def balance_greedy(problem: dict) -> dict:
    nodes, _ = _problem_maps(problem)
    current = _current_map(problem)

    def key(
        _problem: dict,
        shard: dict,
        combo: tuple[str, ...],
        loads: dict[str, int],
    ) -> tuple:
        projected = loads.copy()
        for node_id in combo:
            projected[node_id] += shard["size"]
        utilization = [
            Fraction(projected[node_id], nodes[node_id]["capacity"])
            for node_id in sorted(nodes)
        ]
        additions = sum(
            node_id not in current[shard["id"]]
            for node_id in combo
        )
        return (
            max(utilization),
            max(utilization) - min(utilization),
            additions * shard["size"],
            additions,
            combo,
        )

    return _first_solution(problem, key)


def case_score(
    candidate: tuple[Fraction, Fraction, int, int],
    reference: tuple[Fraction, Fraction, int, int],
) -> float:
    candidate_max, candidate_spread, candidate_bytes, candidate_count = candidate
    reference_max, reference_spread, reference_bytes, reference_count = reference
    if candidate_max <= 0 or candidate_max < reference_max:
        return 0.0

    primary_ratio = min(1.0, float(reference_max / candidate_max))
    score = 0.70 * primary_ratio**8
    if candidate_max != reference_max:
        return score

    if candidate_spread < reference_spread:
        return 0.0
    if candidate_spread == reference_spread:
        spread_ratio = 1.0
    elif reference_spread == 0:
        spread_ratio = 1.0 / (1.0 + 8.0 * float(candidate_spread))
    else:
        spread_ratio = min(1.0, float(reference_spread / candidate_spread))
    score += 0.15 * spread_ratio
    if candidate_spread != reference_spread:
        return score

    byte_ratio = (
        1.0
        if candidate_bytes == reference_bytes
        else (reference_bytes + 1) / (candidate_bytes + 1)
    )
    score += 0.10 * min(1.0, byte_ratio)
    if candidate_bytes != reference_bytes:
        return score

    count_ratio = (
        1.0
        if candidate_count == reference_count
        else (reference_count + 1) / (candidate_count + 1)
    )
    score += 0.05 * min(1.0, count_ratio)
    return min(1.0, score)
