from pathlib import Path
import importlib.util
import json
import sys
import xml.etree.ElementTree as ET

import yaml

from smevals.text import read_text


ROOT = Path(__file__).parents[1]
EVAL = ROOT / "examples" / "replicated-shard-rebalancer"
STARTER = EVAL / "fixtures" / "starter"


def test_replicated_shard_rebalancer_eval_contract():
    task = yaml.safe_load(
        read_text(EVAL / "tasks" / "implement-rebalancer.yaml")
    )
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
        "showcase-cluster.svg",
    ]


def test_replicated_shard_rebalancer_starter_is_dependency_free_net10():
    projects = sorted(STARTER.rglob("*.csproj"))
    assert len(projects) == 3
    for project in projects:
        root = ET.parse(project).getroot()
        assert root.find(".//TargetFramework").text == "net10.0"
        assert root.findall(".//PackageReference") == []


def test_replicated_shard_rebalancer_starter_is_intentionally_incomplete():
    source = read_text(
        STARTER
        / "src"
        / "ReplicatedShardRebalancer"
        / "ReplicatedShardRebalancer.cs"
    )
    assert "TODO" in source
    assert "RebalanceResult.Empty" in source


def test_replicated_shard_rebalancer_hidden_cases_are_excluded():
    ignore = read_text(EVAL / ".gitignore")
    assert "benchmark/private/" in ignore
    assert "**/__pycache__/" in ignore
    assert list(STARTER.rglob("hidden_cases.json")) == []


def test_replicated_shard_rebalancer_models_match_current_roster():
    actual = json.loads(read_text(EVAL / "benchmark" / "models.json"))
    expected = json.loads(
        read_text(
            ROOT
            / "examples"
            / "carton-packing"
            / "benchmark"
            / "models.json"
        )
    )
    assert actual == expected
    model_ids = [model["id"] for model in actual["models"]]
    assert len(model_ids) == len(set(model_ids)) == 14
    assert len(
        [model for model in actual["models"] if model.get("enabled", True)]
    ) == 13


def test_replicated_shard_rebalancer_wrapper_uses_shared_runner_contract():
    wrapper = read_text(
        EVAL / "benchmark" / "Run-ReplicatedShardRebalancerBenchmark.ps1"
    )
    assert "Invoke-CopilotCodingBenchmark.ps1" in wrapper
    assert "-TaskName 'implement-rebalancer'" in wrapper
    assert (
        "-HiddenEnvironmentVariable "
        "'REPLICATED_SHARD_REBALANCER_HIDDEN_DIR'"
    ) in wrapper
    for parameter in (
        "EvalDirectory",
        "ModelsPath",
        "ModelTimeoutSeconds",
        "PreflightTimeoutSeconds",
        "SkipModelPreflight",
        "PreflightOnly",
        "BuildOnly",
    ):
        assert f"-{parameter}" in wrapper


def test_replicated_shard_rebalancer_calibration_separates_baselines():
    path = EVAL / "benchmark" / "calibrate.py"
    spec = importlib.util.spec_from_file_location(
        "replicated_shard_rebalancer_calibration",
        path,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)

    results = module.calibrate(8675309)["results"]
    assert results["reference"]["score"] == 1.0
    assert results["invalid"]["score"] == 0.0
    assert results["first_feasible"]["score"] < 0.7
    assert results["balance_greedy"]["score"] > 0.7
    assert (
        results["first_feasible"]["score"]
        < results["balance_greedy"]["score"]
        < results["reference"]["score"]
    )
