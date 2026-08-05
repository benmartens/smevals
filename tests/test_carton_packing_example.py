from pathlib import Path
import importlib.util
import sys
import xml.etree.ElementTree as ET

import yaml

from smevals.text import read_text


ROOT = Path(__file__).parents[1]
EVAL = ROOT / "examples" / "carton-packing"
STARTER = EVAL / "fixtures" / "starter"


def test_carton_packing_eval_contract():
    task = yaml.safe_load(read_text(EVAL / "tasks" / "implement-packer.yaml"))
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
        "showcase-layout.svg",
    ]


def test_starter_is_dependency_free_net10():
    projects = sorted(STARTER.rglob("*.csproj"))
    assert len(projects) == 3
    for project in projects:
        root = ET.parse(project).getroot()
        assert root.find(".//TargetFramework").text == "net10.0"
        assert root.findall(".//PackageReference") == []


def test_starter_engine_is_intentionally_incomplete():
    source = read_text(STARTER / "src" / "CartonPacking" / "CartonPacker.cs")
    assert "TODO" in source
    assert "PackingResult.Empty" in source


def test_hidden_cases_are_not_committed_or_copied_to_starter():
    assert "benchmark/private/" in read_text(EVAL / ".gitignore")
    assert list(STARTER.rglob("hidden_cases.json")) == []


def test_benchmark_has_fourteen_unique_models():
    import json

    config = json.loads(read_text(EVAL / "benchmark" / "models.json"))
    model_ids = [model["id"] for model in config["models"]]
    assert len(model_ids) == 14
    assert len(set(model_ids)) == 14
    assert {
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
        "claude-opus-5",
        "claude-sonnet-5",
        "claude-haiku-4.5",
        "mai-code-1-flash-picker",
    }.issubset(model_ids)
    enabled = [model for model in config["models"] if model.get("enabled", True)]
    assert len(enabled) == 13
    assert all(model["effort"] in {"high", "xhigh", "max"} for model in enabled)
    haiku = next(
        model for model in config["models"]
        if model["id"] == "claude-haiku-4.5"
    )
    assert haiku["enabled"] is False


def test_calibration_separates_floor_only_from_strong_greedy():
    path = EVAL / "benchmark" / "calibrate.py"
    spec = importlib.util.spec_from_file_location(
        "carton_packing_calibration", path
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)

    results = module.calibrate(8675309)["results"]
    assert results["reference"]["score"] == 1.0
    assert results["empty"]["score"] == 0.0
    assert results["floor_first_fit"]["score"] < 0.7
    assert results["value_greedy"]["score"] > 0.7
