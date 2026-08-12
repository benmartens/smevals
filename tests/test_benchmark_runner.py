from pathlib import Path

from smevals.text import read_text


ROOT = Path(__file__).parents[1]
COMMON = ROOT / "examples" / "benchmark-common" / "Invoke-CopilotCodingBenchmark.ps1"
PUBLISH = ROOT / "examples" / "benchmark-common" / "Publish-BenchmarkSnapshot.ps1"


def test_common_runner_preserves_hidden_case_isolation_and_resume():
    source = read_text(COMMON)
    assert "Test-SuccessfulModelRun" in source
    assert "Hidden cases already exist" in source
    assert "generate_hidden_cases.py" in source
    assert "SetEnvironmentVariable" in source
    assert "--regrade" in source


def test_snapshot_publisher_requires_complete_private_output():
    source = read_text(PUBLISH)
    for artifact in (
        "report.md",
        "report.json",
        "DEMO.md",
        "hidden_cases.json",
        "models.json",
        "site",
        "README.md",
        "Working-tree base commit",
        "Hidden-case seed",
        "Missing graded runs",
        "timeout or failed Run",
    ):
        assert artifact in source
    assert "destination already exists" in source


def test_all_benchmarks_use_common_runner():
    wrappers = {
        "carton-packing": ("implement-packer", "CARTON_PACKING_HIDDEN_DIR"),
        "query-optimizer": ("implement-optimizer", "QUERY_OPTIMIZER_HIDDEN_DIR"),
        "field-service-route-planner": (
            "implement-planner",
            "FIELD_SERVICE_ROUTE_PLANNER_HIDDEN_DIR",
        ),
        "replicated-shard-rebalancer": (
            "implement-rebalancer",
            "REPLICATED_SHARD_REBALANCER_HIDDEN_DIR",
        ),
    }
    for benchmark, expected in wrappers.items():
        source = read_text(
            ROOT
            / "examples"
            / benchmark
            / "benchmark"
            / f"Run-{''.join(part.title() for part in benchmark.split('-'))}Benchmark.ps1"
        )
        assert "Invoke-CopilotCodingBenchmark.ps1" in source
        assert expected[0] in source
        assert expected[1] in source
