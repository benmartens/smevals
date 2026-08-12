"""Independent validation, costing, and reference optimization."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from typing import Any

COST_CAP = 9_000_000_000_000_000
JOIN_OPERATORS = ("nestedLoop", "hashJoin", "mergeJoin")


def dump_json(obj: Any, path: str) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(obj, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def canonical_json(obj: Any) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def saturating_add(*values: int) -> int:
    total = 0
    for value in values:
        if total >= COST_CAP - value:
            return COST_CAP
        total += value
    return total


def saturating_multiply(*values: int) -> int:
    result = 1
    for value in values:
        if value == 0:
            return 0
        if result > COST_CAP // value:
            return COST_CAP
        result *= value
    return min(COST_CAP, result)


def scale_ceiling(value: int, permille: int) -> int:
    return max(1, saturating_add(saturating_multiply(value, permille), 999) // 1000)


def sort_cost(rows: int) -> int:
    levels = max(1, math.ceil(math.log2(max(1, rows))))
    return saturating_multiply(rows, levels, 2)


def table_map(problem: dict) -> dict[str, dict]:
    return {table["id"]: table for table in problem.get("tables", [])}


def estimate_filtered_rows(problem: dict, table: dict) -> int:
    rows = int(table["rows"])
    predicates = sorted(
        (
            predicate
            for predicate in problem.get("predicates", [])
            if predicate["tableId"] == table["id"]
        ),
        key=lambda predicate: predicate["column"],
    )
    for predicate in predicates:
        rows = scale_ceiling(rows, int(predicate["selectivityPermille"]))
    return max(1, rows)


def estimate_rows(problem: dict, table_ids: set[str] | frozenset[str]) -> int:
    tables = table_map(problem)
    rows = 1
    for table_id in sorted(table_ids):
        rows = saturating_multiply(
            rows,
            estimate_filtered_rows(problem, tables[table_id]),
        )
    joins = sorted(
        (
            join
            for join in problem.get("joins", [])
            if join["leftTable"] in table_ids and join["rightTable"] in table_ids
        ),
        key=lambda join: tuple(
            sorted((join["leftTable"], join["rightTable"]))
        ),
    )
    for join in joins:
        rows = scale_ceiling(rows, int(join["selectivityPermille"]))
    return max(1, rows)


def has_crossing_join(problem: dict, left: set[str], right: set[str]) -> bool:
    return any(
        (
            join["leftTable"] in left
            and join["rightTable"] in right
        )
        or (
            join["rightTable"] in left
            and join["leftTable"] in right
        )
        for join in problem.get("joins", [])
    )


def _validate_problem(problem: dict) -> list[dict]:
    issues: list[dict] = []
    tables: dict[str, dict] = {}
    if not isinstance(problem.get("memoryLimitRows"), int) or problem["memoryLimitRows"] <= 0:
        issues.append({"code": "invalid_memory", "detail": "memoryLimitRows must be positive"})
    for table in problem.get("tables", []):
        table_id = table.get("id")
        if (
            not isinstance(table_id, str)
            or not table_id
            or not isinstance(table.get("rows"), int)
            or table["rows"] <= 0
            or not isinstance(table.get("scanCostPerRow"), int)
            or table["scanCostPerRow"] <= 0
        ):
            issues.append({"code": "invalid_table", "detail": f"invalid table {table_id!r}"})
            continue
        if table_id in tables:
            issues.append({"code": "duplicate_table_id", "detail": table_id})
        tables[table_id] = table
        for index in table.get("indexes", []):
            if (
                not index.get("column")
                or index.get("seekStartupCost", -1) < 0
                or index.get("lookupCostPerRow", 0) <= 0
            ):
                issues.append({"code": "invalid_index", "detail": table_id})
    for predicate in problem.get("predicates", []):
        if (
            predicate.get("tableId") not in tables
            or not predicate.get("column")
            or not 1 <= predicate.get("selectivityPermille", 0) <= 1000
        ):
            issues.append({"code": "invalid_predicate", "detail": repr(predicate)})
    for join in problem.get("joins", []):
        if (
            join.get("leftTable") not in tables
            or join.get("rightTable") not in tables
            or join.get("leftTable") == join.get("rightTable")
            or not 1 <= join.get("selectivityPermille", 0) <= 1000
        ):
            issues.append({"code": "invalid_join", "detail": repr(join)})
    return issues


@dataclass(frozen=True)
class Evaluation:
    tables: frozenset[str]
    rows: int
    cost: int
    peak_memory_rows: int
    operator_count: int


def _leaf_cost(problem: dict, table: dict, operator: str, index_column: str | None) -> int | None:
    filtered_rows = estimate_filtered_rows(problem, table)
    if operator == "tableScan":
        if index_column is not None:
            return None
        return saturating_add(
            saturating_multiply(table["rows"], table["scanCostPerRow"]),
            saturating_multiply(filtered_rows, 2),
        )

    index = next(
        (index for index in table.get("indexes", []) if index["column"] == index_column),
        None,
    )
    predicate = next(
        (
            predicate
            for predicate in problem.get("predicates", [])
            if predicate["tableId"] == table["id"]
            and predicate["column"] == index_column
            and predicate.get("indexable", True)
        ),
        None,
    )
    if index is None or predicate is None:
        return None
    matched_rows = scale_ceiling(table["rows"], predicate["selectivityPermille"])
    return saturating_add(
        index["seekStartupCost"],
        saturating_multiply(matched_rows, index["lookupCostPerRow"]),
        saturating_multiply(filtered_rows, 2),
    )


def join_local_cost(
    operator: str,
    left_rows: int,
    right_rows: int,
    output_rows: int,
    memory_limit_rows: int,
) -> tuple[int, int]:
    input_rows = saturating_add(left_rows, right_rows)
    if operator == "nestedLoop":
        return (
            saturating_add(
                saturating_multiply(left_rows, right_rows),
                output_rows,
            ),
            1,
        )
    if operator == "hashJoin":
        build_rows = min(left_rows, right_rows)
        spill_rows = max(0, build_rows - memory_limit_rows)
        return (
            saturating_add(
                saturating_multiply(input_rows, 4),
                output_rows,
                saturating_multiply(spill_rows, 20),
            ),
            min(build_rows, memory_limit_rows),
        )
    return (
        saturating_add(
            sort_cost(left_rows),
            sort_cost(right_rows),
            saturating_multiply(input_rows, 2),
            output_rows,
        ),
        min(input_rows, max(1, memory_limit_rows)),
    )


def validate(problem: dict, result: dict) -> dict:
    issues = _validate_problem(problem)
    tables = table_map(problem)
    root = result.get("plan") if isinstance(result, dict) else None
    if not isinstance(root, dict):
        issues.append({"code": "missing_plan", "detail": "result.plan must be an object"})
        return {"valid": False, "issues": issues, "metrics": None}

    def evaluate(node: dict, path: str) -> Evaluation | None:
        operator = node.get("operator")
        if operator in ("tableScan", "indexSeek"):
            if node.get("left") is not None or node.get("right") is not None:
                issues.append({"code": "leaf_children", "detail": path})
            table_id = node.get("tableId")
            if table_id not in tables:
                issues.append({"code": "unknown_table", "detail": f"{path}: {table_id!r}"})
                return None
            index_column = node.get("indexColumn")
            cost = _leaf_cost(problem, tables[table_id], operator, index_column)
            if cost is None:
                code = "scan_index" if operator == "tableScan" else "invalid_index_seek"
                issues.append({"code": code, "detail": path})
                return None
            return Evaluation(
                frozenset((table_id,)),
                estimate_filtered_rows(problem, tables[table_id]),
                cost,
                1,
                1,
            )

        if operator not in JOIN_OPERATORS:
            issues.append({"code": "unknown_operator", "detail": f"{path}: {operator!r}"})
            return None
        if node.get("tableId") is not None or node.get("indexColumn") is not None:
            issues.append({"code": "join_fields", "detail": path})
        left_node = node.get("left")
        right_node = node.get("right")
        if not isinstance(left_node, dict) or not isinstance(right_node, dict):
            issues.append({"code": "missing_child", "detail": path})
            return None
        left = evaluate(left_node, path + ".left")
        right = evaluate(right_node, path + ".right")
        if left is None or right is None:
            return None
        if left.tables & right.tables:
            issues.append({"code": "duplicate_table", "detail": path})
        if min(left.tables) >= min(right.tables):
            issues.append({"code": "noncanonical_children", "detail": path})
        if not has_crossing_join(problem, set(left.tables), set(right.tables)):
            issues.append({"code": "cross_join", "detail": path})
        all_tables = left.tables | right.tables
        rows = estimate_rows(problem, all_tables)
        local_cost, local_memory = join_local_cost(
            operator,
            left.rows,
            right.rows,
            rows,
            problem["memoryLimitRows"],
        )
        return Evaluation(
            all_tables,
            rows,
            saturating_add(left.cost, right.cost, local_cost),
            max(local_memory, left.peak_memory_rows, right.peak_memory_rows),
            left.operator_count + right.operator_count + 1,
        )

    evaluation = evaluate(root, "plan")
    if evaluation is not None and evaluation.tables != frozenset(tables):
        issues.append({"code": "table_coverage", "detail": "root must contain every table exactly once"})
    metrics = None
    if evaluation is not None and not issues:
        metrics = {
            "estimated_rows": evaluation.rows,
            "total_cost": evaluation.cost,
            "peak_memory_rows": evaluation.peak_memory_rows,
            "operator_count": evaluation.operator_count,
        }
    return {"valid": not issues and metrics is not None, "issues": issues, "metrics": metrics}


@dataclass(frozen=True)
class Candidate:
    cost: int
    rows: int
    peak_memory_rows: int
    operator_count: int
    plan: dict


def _better(candidate: Candidate, current: Candidate | None) -> bool:
    if current is None:
        return True
    return (
        candidate.cost,
        candidate.peak_memory_rows,
        candidate.operator_count,
        canonical_json(candidate.plan),
    ) < (
        current.cost,
        current.peak_memory_rows,
        current.operator_count,
        canonical_json(current.plan),
    )


def reference_optimize(problem: dict) -> dict:
    """Exact subset dynamic programming for connected workloads."""
    issues = _validate_problem(problem)
    if issues:
        raise ValueError(f"invalid reference problem: {issues}")
    ids = sorted(table["id"] for table in problem["tables"])
    if not ids:
        return {"plan": None}
    tables = table_map(problem)
    bit_for = {table_id: 1 << index for index, table_id in enumerate(ids)}
    tables_for_mask = {
        mask: frozenset(
            table_id for table_id in ids if mask & bit_for[table_id]
        )
        for mask in range(1, 1 << len(ids))
    }
    rows_for_mask = {
        mask: estimate_rows(problem, table_ids)
        for mask, table_ids in tables_for_mask.items()
    }
    best: dict[int, Candidate] = {}

    for table_id in ids:
        mask = bit_for[table_id]
        table = tables[table_id]
        leaf_candidates = [
            ("tableScan", None),
            *[
                ("indexSeek", index["column"])
                for index in sorted(table.get("indexes", []), key=lambda item: item["column"])
            ],
        ]
        for operator, index_column in leaf_candidates:
            cost = _leaf_cost(problem, table, operator, index_column)
            if cost is None:
                continue
            plan = {"operator": operator, "tableId": table_id}
            if index_column is not None:
                plan["indexColumn"] = index_column
            candidate = Candidate(cost, rows_for_mask[mask], 1, 1, plan)
            if _better(candidate, best.get(mask)):
                best[mask] = candidate

    full_mask = (1 << len(ids)) - 1
    for size in range(2, len(ids) + 1):
        for mask in range(1, full_mask + 1):
            if mask.bit_count() != size:
                continue
            lowest_bit = mask & -mask
            left_mask = (mask - 1) & mask
            while left_mask:
                right_mask = mask ^ left_mask
                if (
                    right_mask
                    and left_mask & lowest_bit
                    and left_mask in best
                    and right_mask in best
                    and has_crossing_join(
                        problem,
                        set(tables_for_mask[left_mask]),
                        set(tables_for_mask[right_mask]),
                    )
                ):
                    left = best[left_mask]
                    right = best[right_mask]
                    output_rows = rows_for_mask[mask]
                    for operator in JOIN_OPERATORS:
                        local_cost, local_memory = join_local_cost(
                            operator,
                            left.rows,
                            right.rows,
                            output_rows,
                            problem["memoryLimitRows"],
                        )
                        candidate = Candidate(
                            saturating_add(left.cost, right.cost, local_cost),
                            output_rows,
                            max(
                                local_memory,
                                left.peak_memory_rows,
                                right.peak_memory_rows,
                            ),
                            left.operator_count + right.operator_count + 1,
                            {
                                "operator": operator,
                                "left": left.plan,
                                "right": right.plan,
                            },
                        )
                        if _better(candidate, best.get(mask)):
                            best[mask] = candidate
                left_mask = (left_mask - 1) & mask

    if full_mask not in best:
        raise ValueError("query graph is disconnected")
    return {"plan": best[full_mask].plan}


def objective(problem: dict, result: dict) -> int:
    validation = validate(problem, result)
    if not validation["valid"]:
        raise ValueError(validation["issues"])
    return int(validation["metrics"]["total_cost"])


def cost_ratio(candidate_cost: int, reference_cost: int) -> float:
    if candidate_cost <= 0 or reference_cost <= 0:
        return 0.0
    return min(1.0, reference_cost / candidate_cost)
