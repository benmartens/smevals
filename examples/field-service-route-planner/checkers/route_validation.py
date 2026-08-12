"""Independent validation, scoring, heuristics, and exact reference solver."""

from __future__ import annotations

import json
from functools import lru_cache
from typing import Any


def load_json(path: str) -> Any:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def dump_json(value: Any, path: str) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def canonical_routes(problem: dict, routes: dict[str, list[str]]) -> dict:
    return {
        "routes": [
            {"technicianId": tech["id"], "jobIds": list(routes.get(tech["id"], []))}
            for tech in sorted(problem["technicians"], key=lambda item: item["id"])
        ]
    }


def _travel(problem: dict, origin: str, destination: str) -> int | None:
    value = problem.get("travelTimes", {}).get(origin, {}).get(destination)
    return value if isinstance(value, int) and value >= 0 else None


def _has_skills(technician: dict, job: dict) -> bool:
    return set(job.get("requiredSkills", [])).issubset(
        technician.get("skills", [])
    )


def evaluate_route(
    problem: dict,
    technician: dict,
    job_ids: list[str],
    *,
    require_skills: bool = True,
) -> dict:
    jobs = {job["id"]: job for job in problem["jobs"]}
    location = problem["depot"]
    current_time = technician["shiftStart"]
    travel_total = 0
    stops = []
    issues = []
    for job_id in job_ids:
        job = jobs.get(job_id)
        if job is None:
            issues.append({"code": "unknown_job", "detail": repr(job_id)})
            continue
        if require_skills and not _has_skills(technician, job):
            issues.append(
                {
                    "code": "missing_skills",
                    "detail": f"{technician['id']} cannot serve {job_id}",
                }
            )
        travel = _travel(problem, location, job["location"])
        if travel is None:
            issues.append(
                {
                    "code": "travel_matrix",
                    "detail": f"{location!r} -> {job['location']!r}",
                }
            )
            continue
        arrival = current_time + travel
        service_start = max(arrival, job["windowStart"])
        service_end = service_start + job["duration"]
        if service_end > job["windowEnd"]:
            issues.append(
                {
                    "code": "time_window",
                    "detail": f"{job_id} ends at {service_end}",
                }
            )
        travel_total += travel
        stops.append(
            {
                "jobId": job_id,
                "arrival": arrival,
                "serviceStart": service_start,
                "serviceEnd": service_end,
            }
        )
        location = job["location"]
        current_time = service_end

    return_travel = _travel(problem, location, problem["depot"])
    if return_travel is None:
        issues.append(
            {
                "code": "travel_matrix",
                "detail": f"{location!r} -> {problem['depot']!r}",
            }
        )
        return_travel = 0
    travel_total += return_travel
    return_time = current_time + return_travel
    if return_time > technician["shiftEnd"]:
        issues.append(
            {
                "code": "shift_return",
                "detail": f"{technician['id']} returns at {return_time}",
            }
        )
    return {
        "valid": not issues,
        "issues": issues,
        "stops": stops,
        "returnTime": return_time,
        "travel": travel_total,
    }


def _validate_problem(problem: dict) -> list[dict]:
    issues: list[dict] = []
    depot = problem.get("depot")
    technicians = problem.get("technicians")
    jobs = problem.get("jobs")
    if not isinstance(depot, str) or not depot.strip():
        issues.append({"code": "invalid_depot", "detail": "depot"})
    if not isinstance(technicians, list) or not isinstance(jobs, list):
        return issues + [{"code": "invalid_problem", "detail": "collections"}]

    technician_ids = []
    for technician in technicians:
        technician_ids.append(technician.get("id"))
        skills = technician.get("skills")
        if (
            not isinstance(technician.get("id"), str)
            or not technician["id"].strip()
            or not isinstance(skills, list)
            or any(not isinstance(skill, str) or not skill for skill in skills)
            or len(skills) != len(set(skills))
            or not isinstance(technician.get("shiftStart"), int)
            or not isinstance(technician.get("shiftEnd"), int)
            or technician.get("shiftStart", -1) < 0
            or technician.get("shiftEnd", -1)
            < technician.get("shiftStart", 0)
        ):
            issues.append(
                {
                    "code": "invalid_technician",
                    "detail": repr(technician.get("id")),
                }
            )
    if len(technician_ids) != len(set(technician_ids)):
        issues.append({"code": "duplicate_technician", "detail": "IDs"})

    job_ids = []
    locations = {depot}
    for job in jobs:
        job_ids.append(job.get("id"))
        locations.add(job.get("location"))
        skills = job.get("requiredSkills")
        if (
            not isinstance(job.get("id"), str)
            or not job["id"].strip()
            or not isinstance(job.get("location"), str)
            or not job["location"].strip()
            or not isinstance(skills, list)
            or any(not isinstance(skill, str) or not skill for skill in skills)
            or len(skills) != len(set(skills))
            or not all(
                isinstance(job.get(field), int)
                for field in (
                    "duration",
                    "windowStart",
                    "windowEnd",
                    "value",
                )
            )
            or job.get("duration", -1) < 0
            or job.get("windowStart", -1) < 0
            or job.get("windowEnd", -1) < job.get("windowStart", 0)
            or job.get("value", -1) < 0
        ):
            issues.append(
                {"code": "invalid_job", "detail": repr(job.get("id"))}
            )
    if len(job_ids) != len(set(job_ids)):
        issues.append({"code": "duplicate_job_id", "detail": "IDs"})

    for origin in locations:
        for destination in locations:
            if not isinstance(origin, str) or not isinstance(destination, str):
                continue
            if _travel(problem, origin, destination) is None:
                issues.append(
                    {
                        "code": "travel_matrix",
                        "detail": f"{origin!r} -> {destination!r}",
                    }
                )
    return issues


def validate(problem: dict, result: dict) -> dict:
    issues = _validate_problem(problem)
    technicians = {
        item["id"]: item
        for item in problem.get("technicians", [])
        if isinstance(item.get("id"), str)
    }
    jobs = {
        item["id"]: item
        for item in problem.get("jobs", [])
        if isinstance(item.get("id"), str)
    }
    routes = result.get("routes") if isinstance(result, dict) else None
    if not isinstance(routes, list):
        routes = []
        issues.append({"code": "invalid_result", "detail": "routes"})

    actual_ids = [
        route.get("technicianId")
        for route in routes
        if isinstance(route, dict)
    ]
    expected_ids = sorted(technicians)
    if any(not isinstance(value, str) for value in actual_ids):
        issues.append({"code": "invalid_route", "detail": "technician ID"})
    elif actual_ids != sorted(actual_ids):
        issues.append({"code": "noncanonical_routes", "detail": "route order"})
    if actual_ids != expected_ids:
        issues.append(
            {"code": "technician_routes", "detail": "one route per technician"}
        )

    assigned: set[str] = set()
    served_value = 0
    total_travel = 0
    timings = []
    for route in routes:
        if not isinstance(route, dict):
            issues.append({"code": "invalid_route", "detail": repr(route)})
            continue
        technician_id = route.get("technicianId")
        job_ids = route.get("jobIds")
        if technician_id not in technicians:
            issues.append(
                {
                    "code": "unknown_technician",
                    "detail": repr(technician_id),
                }
            )
            continue
        if not isinstance(job_ids, list) or any(
            not isinstance(job_id, str) for job_id in job_ids
        ):
            issues.append(
                {"code": "invalid_route", "detail": repr(technician_id)}
            )
            continue
        for job_id in job_ids:
            if job_id not in jobs:
                issues.append({"code": "unknown_job", "detail": repr(job_id)})
                continue
            if job_id in assigned:
                issues.append({"code": "duplicate_job", "detail": job_id})
            else:
                assigned.add(job_id)
            served_value += jobs[job_id]["value"]
        timing = evaluate_route(problem, technicians[technician_id], job_ids)
        issues.extend(timing["issues"])
        total_travel += timing["travel"]
        timings.append(
            {
                "technicianId": technician_id,
                "stops": timing["stops"],
                "returnTime": timing["returnTime"],
                "travel": timing["travel"],
            }
        )

    return {
        "valid": not issues,
        "issues": issues,
        "metrics": {
            "served_value": served_value,
            "total_travel": total_travel,
        },
        "route_timings": timings,
    }


def recompute_objective(problem: dict, result: dict) -> tuple[int, int]:
    report = validate(problem, result)
    return (
        report["metrics"]["served_value"],
        report["metrics"]["total_travel"],
    )


def capped_value_ratio(candidate_value: int, reference_value: int) -> float:
    if reference_value <= 0:
        return 1.0 if candidate_value == 0 else 0.0
    return min(1.0, max(0.0, candidate_value / reference_value))


def travel_quality(
    candidate_travel: int,
    reference_travel: int,
    candidate_value: int,
    reference_value: int,
) -> float:
    if candidate_value != reference_value:
        return 0.0
    if candidate_travel == 0:
        return 1.0 if reference_travel == 0 else 0.0
    return min(1.0, max(0.0, reference_travel / candidate_travel))


def case_score(value_ratio: float, travel_ratio: float) -> float:
    return 0.95 * value_ratio + 0.05 * travel_ratio


def _plan_key(plan: dict) -> tuple:
    return tuple(
        (route["technicianId"], tuple(route["jobIds"]))
        for route in plan["routes"]
    )


def reference_solve(problem: dict) -> dict:
    """Exact bounded dynamic-programming search over assignment and order."""
    technicians = sorted(problem["technicians"], key=lambda item: item["id"])
    jobs = sorted(problem["jobs"], key=lambda item: item["id"])
    depot = problem["depot"]
    initial_states = tuple(
        (depot, technician["shiftStart"]) for technician in technicians
    )

    @lru_cache(maxsize=None)
    def solve(mask: int, states: tuple[tuple[str, int], ...]):
        best = None
        return_cost = 0
        can_stop = True
        for index, technician in enumerate(technicians):
            travel = _travel(problem, states[index][0], depot)
            if (
                travel is None
                or states[index][1] + travel > technician["shiftEnd"]
            ):
                can_stop = False
                break
            return_cost += travel
        if can_stop:
            best = (0, return_cost, tuple(() for _ in technicians))

        for job_index, job in enumerate(jobs):
            bit = 1 << job_index
            if not mask & bit:
                continue
            for tech_index, technician in enumerate(technicians):
                if not _has_skills(technician, job):
                    continue
                location, current_time = states[tech_index]
                travel = _travel(problem, location, job["location"])
                if travel is None:
                    continue
                service_start = max(
                    current_time + travel, job["windowStart"]
                )
                service_end = service_start + job["duration"]
                if (
                    service_end > job["windowEnd"]
                    or service_end > technician["shiftEnd"]
                ):
                    continue
                next_states = list(states)
                next_states[tech_index] = (job["location"], service_end)
                future = solve(mask ^ bit, tuple(next_states))
                if future is None:
                    continue
                suffix = [tuple(route) for route in future[2]]
                suffix[tech_index] = (job["id"],) + suffix[tech_index]
                candidate = (
                    job["value"] + future[0],
                    travel + future[1],
                    tuple(suffix),
                )
                if best is None or (
                    -candidate[0],
                    candidate[1],
                    candidate[2],
                ) < (-best[0], best[1], best[2]):
                    best = candidate
        return best

    solved = solve((1 << len(jobs)) - 1, initial_states)
    if solved is None:
        return canonical_routes(problem, {})
    routes = {
        technician["id"]: list(solved[2][index])
        for index, technician in enumerate(technicians)
    }
    return canonical_routes(problem, routes)


def first_feasible(problem: dict) -> dict:
    routes = {
        tech["id"]: []
        for tech in sorted(problem["technicians"], key=lambda item: item["id"])
    }
    assigned = set()
    for job in problem["jobs"]:
        for technician in sorted(
            problem["technicians"], key=lambda item: item["id"]
        ):
            if not _has_skills(technician, job):
                continue
            candidate = routes[technician["id"]] + [job["id"]]
            if evaluate_route(problem, technician, candidate)["valid"]:
                routes[technician["id"]] = candidate
                assigned.add(job["id"])
                break
    return canonical_routes(problem, routes)


def beam_solve(problem: dict, width: int = 12) -> dict:
    technicians = sorted(problem["technicians"], key=lambda item: item["id"])
    jobs = sorted(problem["jobs"], key=lambda item: item["id"])
    depot = problem["depot"]
    initial = (
        (1 << len(jobs)) - 1,
        tuple((depot, tech["shiftStart"]) for tech in technicians),
        tuple(() for _ in technicians),
        0,
        0,
    )
    beam = [initial]
    complete: list[tuple[int, int, tuple[tuple[str, ...], ...]]] = []
    for _ in range(len(jobs) + 1):
        expanded = []
        for mask, states, routes, value, travel_so_far in beam:
            return_cost = 0
            can_stop = True
            for index, technician in enumerate(technicians):
                leg = _travel(problem, states[index][0], depot)
                if (
                    leg is None
                    or states[index][1] + leg > technician["shiftEnd"]
                ):
                    can_stop = False
                    break
                return_cost += leg
            if can_stop:
                complete.append((value, travel_so_far + return_cost, routes))
            for job_index, job in enumerate(jobs):
                bit = 1 << job_index
                if not mask & bit:
                    continue
                for tech_index, technician in enumerate(technicians):
                    if not _has_skills(technician, job):
                        continue
                    leg = _travel(
                        problem, states[tech_index][0], job["location"]
                    )
                    if leg is None:
                        continue
                    start = max(
                        states[tech_index][1] + leg, job["windowStart"]
                    )
                    end = start + job["duration"]
                    if (
                        end > job["windowEnd"]
                        or end > technician["shiftEnd"]
                    ):
                        continue
                    next_states = list(states)
                    next_states[tech_index] = (job["location"], end)
                    next_routes = [tuple(route) for route in routes]
                    next_routes[tech_index] += (job["id"],)
                    expanded.append(
                        (
                            mask ^ bit,
                            tuple(next_states),
                            tuple(next_routes),
                            value + job["value"],
                            travel_so_far + leg,
                        )
                    )
        if not expanded:
            break
        deduplicated = {}
        for state in expanded:
            key = (state[0], state[1], state[2])
            previous = deduplicated.get(key)
            if previous is None or state[4] < previous[4]:
                deduplicated[key] = state
        beam = sorted(
            deduplicated.values(),
            key=lambda state: (
                -state[3],
                state[4],
                state[2],
                state[1],
            ),
        )[:width]

    if not complete:
        return canonical_routes(problem, {})
    best = min(complete, key=lambda item: (-item[0], item[1], item[2]))
    return canonical_routes(
        problem,
        {
            technician["id"]: list(best[2][index])
            for index, technician in enumerate(technicians)
        },
    )
