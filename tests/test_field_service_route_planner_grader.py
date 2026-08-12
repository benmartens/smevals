from __future__ import annotations

import importlib.util
import os
import random
import sys
from types import SimpleNamespace
from pathlib import Path


ROOT = Path(__file__).parents[1]
CHECKERS = ROOT / "examples" / "field-service-route-planner" / "checkers"
BENCHMARK = ROOT / "examples" / "field-service-route-planner" / "benchmark"


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


rv = load_module(CHECKERS / "route_validation.py", "route_validation")
generator = load_module(
    BENCHMARK / "generate_hidden_cases.py",
    "field_service_route_hidden_cases",
)
grader = load_module(
    CHECKERS / "grade-field-service-route-planner.py",
    "field_service_route_grader",
)


SIMPLE = {
    "depot": "d",
    "travelTimes": {
        "d": {"d": 0, "a": 4, "b": 8},
        "a": {"d": 4, "a": 0, "b": 2},
        "b": {"d": 5, "a": 9, "b": 0},
    },
    "technicians": [
        {
            "id": "tech",
            "skills": ["repair"],
            "shiftStart": 0,
            "shiftEnd": 50,
        }
    ],
    "jobs": [
        {
            "id": "a-job",
            "location": "a",
            "requiredSkills": ["repair"],
            "duration": 5,
            "windowStart": 0,
            "windowEnd": 30,
            "value": 10,
        },
        {
            "id": "b-job",
            "location": "b",
            "requiredSkills": ["repair"],
            "duration": 5,
            "windowStart": 0,
            "windowEnd": 35,
            "value": 10,
        },
    ],
}


def test_reference_solver_is_valid_and_uses_minimum_travel_order():
    plan = rv.reference_solve(SIMPLE)
    report = rv.validate(SIMPLE, plan)
    assert report["valid"], report["issues"]
    assert report["metrics"] == {"served_value": 20, "total_travel": 11}
    assert plan["routes"][0]["jobIds"] == ["a-job", "b-job"]


def test_validator_recomputes_wait_service_and_asymmetric_return():
    problem = {
        **SIMPLE,
        "jobs": [
            {
                **SIMPLE["jobs"][0],
                "windowStart": 20,
                "windowEnd": 40,
            }
        ],
    }
    result = {"routes": [{"technicianId": "tech", "jobIds": ["a-job"]}]}
    report = rv.validate(problem, result)
    assert report["valid"]
    timing = report["route_timings"][0]
    assert timing["stops"][0] == {
        "jobId": "a-job",
        "arrival": 4,
        "serviceStart": 20,
        "serviceEnd": 25,
    }
    assert timing["returnTime"] == 29
    assert timing["travel"] == 8


def test_validator_rejects_duplicate_job_and_missing_skills():
    problem = {
        **SIMPLE,
        "technicians": [
            {
                "id": "other",
                "skills": [],
                "shiftStart": 0,
                "shiftEnd": 50,
            },
            SIMPLE["technicians"][0],
        ],
    }
    result = {
        "routes": [
            {"technicianId": "other", "jobIds": ["a-job"]},
            {"technicianId": "tech", "jobIds": ["a-job"]},
        ]
    }
    codes = {issue["code"] for issue in rv.validate(problem, result)["issues"]}
    assert {"duplicate_job", "missing_skills"}.issubset(codes)


def test_validator_rejects_time_window_shift_and_route_order():
    problem = {
        **SIMPLE,
        "technicians": [
            {
                "id": "a-tech",
                "skills": ["repair"],
                "shiftStart": 0,
                "shiftEnd": 10,
            },
            {
                "id": "z-tech",
                "skills": ["repair"],
                "shiftStart": 0,
                "shiftEnd": 50,
            },
        ],
        "jobs": [{**SIMPLE["jobs"][1], "windowEnd": 9}],
    }
    result = {
        "routes": [
            {"technicianId": "z-tech", "jobIds": []},
            {"technicianId": "a-tech", "jobIds": ["b-job"]},
        ]
    }
    codes = {issue["code"] for issue in rv.validate(problem, result)["issues"]}
    assert {"time_window", "shift_return", "noncanonical_routes"}.issubset(codes)


def test_scoring_is_capped_and_travel_only_breaks_equal_value():
    assert rv.capped_value_ratio(30, 20) == 1.0
    assert rv.travel_quality(12, 10, 20, 20) == 10 / 12
    assert rv.travel_quality(1, 10, 19, 20) == 0.0
    assert rv.case_score(1.0, 1.0) == 1.0


def test_generated_cases_cover_contract_and_have_exact_valid_references():
    cases = generator.build_cases(random.Random(12345))
    assert len(cases) == 8
    tags = {tag for case in cases for tag in case["tags"]}
    assert {
        "skills",
        "time_windows",
        "clustering",
        "value_trap",
        "waiting",
        "asymmetric_travel",
        "cross_technician",
    }.issubset(tags)
    for case in cases:
        result = {"routes": case["reference"]["routes"]}
        report = rv.validate(case["problem"], result)
        assert report["valid"], (case["id"], report["issues"])
        assert report["metrics"]["served_value"] == case["reference"]["servedValue"]
        assert report["metrics"]["total_travel"] == case["reference"]["totalTravel"]


def test_grader_locates_cli_in_alternate_release_folder(tmp_path):
    dll = (
        tmp_path
        / "src"
        / "FieldServiceRoutePlanner.Cli"
        / "bin"
        / "Release"
        / "net10.0-windows"
        / "FieldServiceRoutePlanner.Cli.dll"
    )
    dll.parent.mkdir(parents=True)
    dll.write_bytes(b"")
    assert grader._locate_dll(tmp_path) == dll


def test_grader_hides_hidden_case_directory_from_candidate(
    monkeypatch, tmp_path
):
    captured = {}

    def fake_run(*args, **kwargs):
        captured.update(kwargs)
        return SimpleNamespace(returncode=0, stderr="")

    monkeypatch.setattr(grader.subprocess, "run", fake_run)
    monkeypatch.setenv(
        "FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR",
        str(tmp_path / "hidden"),
    )

    grader._run_candidate(
        tmp_path / "candidate.dll",
        tmp_path / "problem.json",
        tmp_path / "result.json",
    )

    assert (
        "FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR"
        not in captured["env"]
    )
    assert os.environ["FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR"]
