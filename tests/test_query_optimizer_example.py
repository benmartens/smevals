from pathlib import Path
import importlib.util
import json
import sys
import xml.etree.ElementTree as ET

import yaml

from smevals.text import read_text


ROOT = Path(__file__).parents[1]
EVAL = ROOT / "examples" / "query-optimizer"
STARTER = EVAL / "fixtures" / "starter"


def test_query_optimizer_eval_contract():
    task = yaml.safe_load(read_text(EVAL / "tasks" / "implement-optimizer.yaml"))
    config = yaml.safe_load(read_text(EVAL / "configs" / "copilot.yaml"))
    grader = yaml.safe_load(read_text(EVAL / "graders" / "default.yaml"))

    assert task["copilot_workspace"] == "fixtures/starter"
    assert config["runner"] == "smevals-copilot"
    assert config["copilot"]["permissions"] == "workspace"
    assert "shell(dotnet:*)" in config["copilot"]["allow_tools"]
    assert config["copilot"]["max_ai_credits"] == 500
    assert grader["scoring"]["pass_threshold"] == 0.7
    assert grader["checks"][0]["creates"] == [
        "grading-results.json",
        "summary.md",
        "solution.patch",
        "showcase-plan.svg",
    ]


def test_query_optimizer_starter_is_dependency_free_net10():
    projects = sorted(STARTER.rglob("*.csproj"))
    assert len(projects) == 3
    for project in projects:
        root = ET.parse(project).getroot()
        assert root.find(".//TargetFramework").text == "net10.0"
        assert root.findall(".//PackageReference") == []


def test_query_optimizer_starter_is_intentionally_incomplete():
    source = read_text(
        STARTER / "src" / "QueryOptimizer" / "QueryOptimizer.cs"
    )
    assert "TODO" in source
    assert "QueryPlan.Empty" in source


def test_query_optimizer_hidden_cases_are_excluded():
    assert "benchmark/private/" in read_text(EVAL / ".gitignore")
    assert list(STARTER.rglob("hidden_cases.json")) == []


def test_query_optimizer_benchmark_models_are_unique():
    config = json.loads(read_text(EVAL / "benchmark" / "models.json"))
    model_ids = [model["id"] for model in config["models"]]
    assert len(model_ids) == 14
    assert len(set(model_ids)) == 14
    assert len([model for model in config["models"] if model.get("enabled", True)]) == 13


def test_query_optimizer_calibration_separates_baselines():
    path = EVAL / "benchmark" / "calibrate.py"
    spec = importlib.util.spec_from_file_location(
        "query_optimizer_calibration", path
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)

    results = module.calibrate(8675309)["results"]
    assert results["reference"]["score"] == 1.0
    assert results["empty"]["score"] == 0.0
    assert results["first_valid"]["score"] < results["greedy"]["score"]
    assert results["greedy"]["score"] < 0.7
