from pathlib import Path
import importlib.util
import json
import sys
import xml.etree.ElementTree as ET

import yaml

from smevals.text import read_text


ROOT = Path(__file__).parents[1]
EVAL = ROOT / "examples" / "field-service-route-planner"
STARTER = EVAL / "fixtures" / "starter"


def test_field_service_route_planner_eval_contract():
    task = yaml.safe_load(read_text(EVAL / "tasks" / "implement-planner.yaml"))
    config = yaml.safe_load(read_text(EVAL / "configs" / "copilot.yaml"))
    grader = yaml.safe_load(read_text(EVAL / "graders" / "default.yaml"))

    assert task["copilot_workspace"] == "fixtures/starter"
    assert config["runner"] == "smevals-copilot"
    assert config["copilot"]["permissions"] == "workspace"
    assert "shell(dotnet:*)" in config["copilot"]["allow_tools"]
    assert config["copilot"]["effort"] == "high"
    assert config["copilot"]["max_ai_credits"] == 500
    assert grader["scoring"]["pass_threshold"] == 0.7
    assert grader["checks"][0]["creates"] == [
        "grading-results.json",
        "summary.md",
        "solution.patch",
        "showcase-route.svg",
    ]


def test_starter_is_dependency_free_net10():
    projects = sorted(STARTER.rglob("*.csproj"))
    assert len(projects) == 3
    for project in projects:
        root = ET.parse(project).getroot()
        assert root.find(".//TargetFramework").text == "net10.0"
        assert root.findall(".//PackageReference") == []


def test_starter_planner_is_intentionally_incomplete():
    source = read_text(
        STARTER
        / "src"
        / "FieldServiceRoutePlanner"
        / "RoutePlanner.cs"
    )
    assert "TODO" in source
    assert "RoutePlan.Empty" in source


def test_hidden_cases_are_excluded():
    assert "benchmark/private/" in read_text(EVAL / ".gitignore")
    assert list(STARTER.rglob("hidden_cases.json")) == []


def test_benchmark_models_match_current_roster():
    actual = json.loads(read_text(EVAL / "benchmark" / "models.json"))
    expected = json.loads(
        read_text(ROOT / "examples" / "carton-packing" / "benchmark" / "models.json")
    )
    assert actual == expected
    assert len(actual["models"]) == 14
    assert len(
        [model for model in actual["models"] if model.get("enabled", True)]
    ) == 13


def test_wrapper_uses_shared_runner_contract():
    wrapper = read_text(
        EVAL / "benchmark" / "Run-FieldServiceRoutePlannerBenchmark.ps1"
    )
    assert "Invoke-CopilotCodingBenchmark.ps1" in wrapper
    assert "-TaskName 'implement-planner'" in wrapper
    assert (
        "-HiddenEnvironmentVariable "
        "'FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR'"
    ) in wrapper


def test_calibration_separates_baselines():
    path = EVAL / "benchmark" / "calibrate.py"
    spec = importlib.util.spec_from_file_location(
        "field_service_route_calibration", path
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)

    results = module.calibrate(8675309)["results"]
    assert results["reference"]["score"] == 1.0
    assert results["invalid"]["score"] == 0.0
    assert results["empty"]["score"] == 0.0
    assert results["simple_first_feasible"]["score"] < 0.7
    assert (
        results["simple_first_feasible"]["score"]
        < results["strong_beam"]["score"]
    )
    assert results["strong_beam"]["score"] > 0.7
