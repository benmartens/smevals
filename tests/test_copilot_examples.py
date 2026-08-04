import json
import os
from pathlib import Path

import pytest
import yaml

from smevals.programs import resolve_program, run_program
from smevals.text import read_text, write_text


ROOT = Path(__file__).parents[1]


@pytest.mark.parametrize(
    "example",
    ["haiku", "markdown-tables", "pelican-riding-a-bicycle"],
)
def test_upstream_examples_have_copilot_configs(example):
    config = yaml.safe_load(
        read_text(ROOT / "examples" / example / "configs" / "copilot.yaml")
    )
    assert config["name"] == "copilot"
    assert config["runner"] == "smevals-copilot"
    assert config["copilot"]["permissions"] == "prompt"
    assert config["copilot"]["max_ai_credits"] >= 30


def run_checker(tmp_path, relative, output, extra_env=None):
    run_dir = tmp_path / "run"
    grade_dir = tmp_path / "grade"
    run_dir.mkdir()
    grade_dir.mkdir()
    write_text(run_dir / "output.txt", output)
    checker = resolve_program(str(ROOT / relative), ROOT)
    env = os.environ | {"SMEVALS_RUN_DIR": str(run_dir)} | (extra_env or {})
    return run_program(checker, cwd=grade_dir, env=env), grade_dir


def test_haiku_checker_accepts_copilot_output(tmp_path):
    result, _ = run_checker(
        tmp_path,
        "examples/haiku/checkers/haiku-structure",
        "Steel circles whisper\nRoads unfold beneath the wheels\nMorning carries on",
    )
    assert result.returncode == 0
    assert json.loads(result.stdout)["score"] == 1.0


def test_markdown_checker_accepts_raw_copilot_markdown(tmp_path):
    result, _ = run_checker(
        tmp_path,
        "examples/markdown-tables/checkers/table-check",
        "| name | age | city |\n|---|---|---|\n| Ann | 34 | Oslo |\n| Bo | 55 | Lima |",
        {
            "SMEVALS_TASK_CSV": "name,age,city\nAnn,34,Oslo\nBo,55,Lima",
        },
    )
    assert result.returncode == 0
    assert json.loads(result.stdout)["score"] == 1.0


def test_pelican_svg_checker_extracts_copilot_output(tmp_path):
    result, grade_dir = run_checker(
        tmp_path,
        "examples/pelican-riding-a-bicycle/checkers/extract-svg",
        "```svg\n<svg xmlns=\"http://www.w3.org/2000/svg\"><path/></svg>\n```",
        {"SMEVALS_CHECK_CREATES": "extracted.svg"},
    )
    assert result.returncode == 0
    assert json.loads(result.stdout)["score"] == 1.0
    assert read_text(grade_dir / "extracted.svg").startswith("<svg")
