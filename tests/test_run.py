"""Tests for `smevals run` and the Runner contract."""

from datetime import datetime, timezone

import smevals.cli
from conftest import read_yaml, run_dirs


def test_run_records_output_and_run_yaml(invoke, make_eval):
    eval_dir = make_eval()
    result = invoke("run", eval_dir)

    dirs = run_dirs(eval_dir)
    assert len(dirs) == 1
    run_dir = dirs[0]
    # runs/<task>/<config>/<model-slug>/<timestamp>/
    assert run_dir.relative_to(eval_dir / "runs").parts[:3] == (
        "first",
        "default",
        "test-model",
    )
    assert (run_dir / "output.txt").read_text() == "model=test-model\nSay hello\n"
    assert not (run_dir / "stderr.txt").exists()

    record = read_yaml(run_dir / "run.yaml")
    assert record["task"] == {"name": "first", "prompt": "Say hello"}
    assert record["config"]["name"] == "default"
    assert record["config"]["model"] == "test-model"
    assert record["config"]["runner"].endswith("run-llm")
    assert record["exit_code"] == 0
    assert isinstance(record["duration_seconds"], float)
    datetime.fromisoformat(record["started"])  # parseable timestamp
    assert "ok" in result.output


def test_prompt_env_only_set_when_task_has_one(invoke, make_eval):
    runner = """\
#!/bin/sh
printf '%s\\n' "${SMEVALS_PROMPT-unset}"
printf '%s\\n' "$SMEVALS_TASK_PAYLOAD"
printf '%s\\n' "$SMEVALS_TASK"
"""
    eval_dir = make_eval(tasks={"data-task": {"payload": "abc"}}, runner=runner)
    invoke("run", eval_dir)
    output = (run_dirs(eval_dir)[0] / "output.txt").read_text()
    assert output == "unset\nabc\ndata-task\n"


def test_stderr_captured_when_present(invoke, make_eval):
    eval_dir = make_eval(runner="#!/bin/sh\necho warned >&2\necho hello\n")
    invoke("run", eval_dir)
    assert (run_dirs(eval_dir)[0] / "stderr.txt").read_text() == "warned\n"


def test_failing_runner_marks_run_failed_and_exits_nonzero(invoke, make_eval):
    eval_dir = make_eval(runner="#!/bin/sh\necho partial\nexit 3\n")
    result = invoke("run", eval_dir, expect_exit=1)
    assert "1 run(s) failed" in result.output

    run_dir = run_dirs(eval_dir)[0]
    assert read_yaml(run_dir / "run.yaml")["exit_code"] == 3
    # The run is still recorded, output included
    assert (run_dir / "output.txt").read_text() == "partial\n"


def test_model_options_override_config_and_are_slugified(invoke, make_eval):
    eval_dir = make_eval()
    invoke("run", eval_dir, "-m", "My Model", "-m", "other")
    model_dirs = {d.relative_to(eval_dir / "runs").parts[2] for d in run_dirs(eval_dir)}
    assert model_dirs == {"My-Model", "other"}
    # The exact (unslugified) model name is preserved in run.yaml
    models = {read_yaml(d / "run.yaml")["config"]["model"] for d in run_dirs(eval_dir)}
    assert models == {"My Model", "other"}


def test_task_selection(invoke, make_eval):
    eval_dir = make_eval(
        tasks={"first": {"prompt": "one"}, "second": {"prompt": "two"}}
    )
    invoke("run", eval_dir, "-t", "second")
    dirs = run_dirs(eval_dir)
    assert len(dirs) == 1
    assert dirs[0].relative_to(eval_dir / "runs").parts[0] == "second"


def test_unknown_task_error_lists_available(invoke, make_eval):
    eval_dir = make_eval(
        tasks={"first": {"prompt": "one"}, "second": {"prompt": "two"}}
    )
    result = invoke("run", eval_dir, "-t", "nope", expect_exit=1)
    assert "No such task(s): nope" in result.output
    assert "first, second" in result.output


def test_unknown_config_error_lists_available(invoke, make_eval):
    eval_dir = make_eval()
    result = invoke("run", eval_dir, "-c", "prod", expect_exit=1)
    assert "No config named 'prod'" in result.output
    assert "default" in result.output


def test_runner_must_be_executable(invoke, make_eval):
    eval_dir = make_eval(runner=None)
    (eval_dir / "run-llm").write_text("#!/bin/sh\necho hi\n")  # not chmod +x
    result = invoke("run", eval_dir, expect_exit=1)
    assert "is not an executable file" in result.output


def test_not_an_eval_error(invoke, tmp_path):
    result = invoke("run", tmp_path, expect_exit=1)
    assert "no eval.yaml found" in result.output


def test_invalid_grader_fails_before_any_run(invoke, make_eval):
    eval_dir = make_eval()
    result = invoke("run", eval_dir, "-g", "nosuch", expect_exit=1)
    assert "No grader named 'nosuch'" in result.output
    assert not (eval_dir / "runs").exists()


def test_grade_flag_grades_each_run_immediately(invoke, make_eval):
    eval_dir = make_eval()
    result = invoke("run", eval_dir, "-g")
    assert "grade: pass" in result.output
    grade = read_yaml(run_dirs(eval_dir)[0] / "grades" / "default" / "grade.yaml")
    assert grade["outcome"] == "pass"


def test_grade_flag_failure_exits_nonzero(invoke, make_eval):
    eval_dir = make_eval(
        graders={
            "default": {"checks": [{"checker": "contains", "value": "zzz-absent"}]}
        }
    )
    result = invoke("run", eval_dir, "-g", expect_exit=1)
    assert "grade: fail" in result.output
    assert "1 run(s) graded as fail" in result.output


def test_runs_dir_option_namespaces_by_eval_name(invoke, make_eval, tmp_path):
    eval_dir = make_eval(name="my-eval")
    external = tmp_path / "external-runs"
    invoke("run", eval_dir, "--runs-dir", external)
    assert not (eval_dir / "runs").exists()
    dirs = run_dirs(external / "my-eval")
    assert len(dirs) == 1


def test_same_second_runs_get_numeric_suffix(invoke, make_eval, monkeypatch):
    class FrozenDatetime(datetime):
        @classmethod
        def now(cls, tz=None):
            return datetime(2026, 1, 1, tzinfo=timezone.utc)

    monkeypatch.setattr(smevals.cli, "datetime", FrozenDatetime)
    eval_dir = make_eval()
    invoke("run", eval_dir)
    invoke("run", eval_dir)
    names = sorted(d.name for d in run_dirs(eval_dir))
    assert names == ["2026-01-01T00-00-00Z", "2026-01-01T00-00-00Z-2"]
