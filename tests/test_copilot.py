import json
import math
import subprocess
from pathlib import Path

import pytest

import smevals.copilot
from smevals.copilot import (
    CopilotRunnerError,
    build_command,
    extract_final_response,
    parse_options,
    prepare_workspace,
    run,
)
from smevals.text import read_text, write_text


def env_for(tmp_path, copilot=None, **extra):
    eval_dir = tmp_path / "eval"
    run_dir = tmp_path / "run"
    eval_dir.mkdir()
    run_dir.mkdir()
    config = {
        "name": "default",
        "runner": "smevals-copilot",
        "model": "gpt-5-mini",
        "copilot": copilot or {},
    }
    return {
        "SMEVALS_PROMPT": "Reply with OK",
        "SMEVALS_MODEL": "gpt-5-mini",
        "SMEVALS_RUN_DIR": str(run_dir),
        "SMEVALS_EVAL_DIR": str(eval_dir),
        "SMEVALS_CONFIG": json.dumps(config),
        "COPILOT_ALLOW_ALL": "true",
        "COPILOT_GITHUB_TOKEN": "token",
    } | extra


def copilot_events(content):
    return (
        json.dumps({"type": "assistant.message", "data": {"content": content}})
        + "\n"
        + json.dumps({"type": "result", "exitCode": 0})
        + "\n"
    ).encode("utf-8")


def test_default_options_are_restricted():
    options = parse_options(None)
    command = build_command(
        "copilot", "hello", "gpt-5-mini", options, Path("workspace")
    )

    assert options.permissions == "prompt"
    assert "--allow-all" not in command
    assert not any(arg.startswith("--allow-tool") for arg in command)
    assert "--output-format=json" in command
    assert "--no-custom-instructions" in command
    assert "--disable-builtin-mcps" in command
    assert "--no-remote-export" in command
    assert "--no-experimental" in command
    assert "--disallow-temp-dir" in command


def test_workspace_options_allow_only_file_tools_by_default():
    options = parse_options(
        {
            "permissions": "workspace",
            "allow_tools": ["shell(python:*)", "read"],
            "deny_tools": ["memory"],
            "allow_urls": ["https://example.com"],
            "effort": "low",
            "max_ai_credits": 30,
        }
    )
    command = build_command(
        "copilot", "hello", "gpt-5-mini", options, Path("workspace")
    )

    assert "--allow-tool=read,write,shell(python:*)" in command
    assert "--deny-tool=memory" in command
    assert "--allow-url=https://example.com" in command
    assert "--effort" in command
    assert "--max-ai-credits" in command
    assert "--allow-all" not in command


def test_unrestricted_profile_is_explicit():
    options = parse_options({"permissions": "unrestricted"})
    command = build_command(
        "copilot", "hello", "gpt-5-mini", options, Path("workspace")
    )
    assert "--allow-all" in command


def test_extract_final_response_preserves_raw_markdown():
    output = "\n".join(
        [
            json.dumps({"type": "assistant.message", "data": {"content": "working"}}),
            json.dumps({"type": "tool.execution_complete", "data": {}}),
            json.dumps(
                {
                    "type": "assistant.message",
                    "data": {"content": "| a | b |\n|---|---|\n| 1 | 2 |"},
                }
            ),
        ]
    )
    assert extract_final_response(output) == "| a | b |\n|---|---|\n| 1 | 2 |"


def test_extract_final_response_requires_json_assistant_message():
    with pytest.raises(CopilotRunnerError, match="without an assistant.message"):
        extract_final_response(json.dumps({"type": "result", "exitCode": 0}))
    with pytest.raises(CopilotRunnerError, match="line 1 is invalid"):
        extract_final_response("not-json")


@pytest.mark.parametrize(
    "config, message",
    [
        ({"permissions": "unknown"}, "permissions"),
        ({"allow_tools": "write"}, "allow_tools"),
        ({"effort": "forever"}, "effort"),
        ({"max_ai_credits": 29}, "max_ai_credits"),
        ({"max_ai_credits": math.nan}, "max_ai_credits"),
        ({"secret_env_vars": ["NOT-VALID"]}, "invalid name"),
        ({"unexpected": True}, "unsupported"),
        (
            {"permissions": "unrestricted", "allow_tools": ["write"]},
            "redundant",
        ),
    ],
)
def test_invalid_options_are_rejected(config, message):
    with pytest.raises(CopilotRunnerError, match=message):
        parse_options(config)


def test_workspace_is_copied_without_mutating_fixture(tmp_path):
    env = env_for(
        tmp_path,
        {"permissions": "workspace"},
        SMEVALS_TASK_COPILOT_WORKSPACE="fixtures/project",
    )
    eval_dir = Path(env["SMEVALS_EVAL_DIR"])
    run_dir = Path(env["SMEVALS_RUN_DIR"])
    fixture = eval_dir / "fixtures" / "project"
    fixture.mkdir(parents=True)
    write_text(fixture / "status.txt", "original\n")

    workspace = prepare_workspace(env, eval_dir, run_dir)
    write_text(workspace / "status.txt", "changed\n")

    assert read_text(fixture / "status.txt") == "original\n"
    assert read_text(workspace / "status.txt") == "changed\n"


def test_run_uses_copied_workspace_as_cwd(tmp_path, monkeypatch):
    env = env_for(
        tmp_path,
        {"permissions": "workspace"},
        SMEVALS_TASK_COPILOT_WORKSPACE="fixtures/project",
    )
    fixture = Path(env["SMEVALS_EVAL_DIR"]) / "fixtures" / "project"
    fixture.mkdir(parents=True)
    write_text(fixture / "status.txt", "original\n")
    captured = {}

    monkeypatch.setattr(smevals.copilot.shutil, "which", lambda name: "copilot.exe")

    def fake_run(command, **kwargs):
        captured.update(kwargs)
        return subprocess.CompletedProcess(command, 0, copilot_events("done\n"), b"")

    monkeypatch.setattr(smevals.copilot.subprocess, "run", fake_run)

    assert run(env) == 0
    assert captured["cwd"] == Path(env["SMEVALS_RUN_DIR"]) / "workspace"
    assert read_text(fixture / "status.txt") == "original\n"


@pytest.mark.parametrize("relative", ["../outside", "fixtures/../../outside"])
def test_workspace_rejects_traversal(tmp_path, relative):
    env = env_for(
        tmp_path,
        {"permissions": "workspace"},
        SMEVALS_TASK_COPILOT_WORKSPACE=relative,
    )
    with pytest.raises(CopilotRunnerError, match="within the Eval"):
        prepare_workspace(
            env,
            Path(env["SMEVALS_EVAL_DIR"]),
            Path(env["SMEVALS_RUN_DIR"]),
        )


def test_workspace_rejects_eval_root(tmp_path):
    env = env_for(
        tmp_path,
        {"permissions": "workspace"},
        SMEVALS_TASK_COPILOT_WORKSPACE=".",
    )
    with pytest.raises(CopilotRunnerError, match="not the Eval root"):
        prepare_workspace(
            env,
            Path(env["SMEVALS_EVAL_DIR"]),
            Path(env["SMEVALS_RUN_DIR"]),
        )


def test_workspace_rejects_source_containing_run_dir(tmp_path):
    eval_dir = tmp_path / "eval"
    run_dir = eval_dir / "runs" / "task" / "config" / "model" / "timestamp"
    run_dir.mkdir(parents=True)
    env = {
        "SMEVALS_TASK_COPILOT_WORKSPACE": "runs",
    }
    with pytest.raises(CopilotRunnerError, match="current Run directory"):
        prepare_workspace(env, eval_dir, run_dir)


def test_share_session_must_be_a_filename():
    with pytest.raises(CopilotRunnerError, match="cannot contain a directory"):
        parse_options({"share_session": "logs/session.md"})
    with pytest.raises(CopilotRunnerError, match="cannot contain a directory"):
        parse_options({"share_session": ".."})


def test_run_invokes_copilot_with_clean_environment(tmp_path, monkeypatch, capsys):
    env = env_for(
        tmp_path,
        {
            "permissions": "workspace",
            "secret_env_vars": ["SERVICE_TOKEN"],
            "share_session": True,
        },
        SERVICE_TOKEN="sensitive",
    )
    captured = {}

    monkeypatch.setattr(smevals.copilot.shutil, "which", lambda name: "copilot.exe")

    def fake_run(command, **kwargs):
        captured["command"] = command
        captured.update(kwargs)
        return subprocess.CompletedProcess(
            command,
            0,
            copilot_events("final answer\n"),
            b"diagnostic\n",
        )

    monkeypatch.setattr(smevals.copilot.subprocess, "run", fake_run)

    result = run(env)
    stdout, stderr = capsys.readouterr()

    assert result == 0
    assert stdout == "final answer\n"
    assert stderr == "diagnostic\n"
    assert captured["cwd"] == Path(env["SMEVALS_RUN_DIR"])
    assert "COPILOT_ALLOW_ALL" not in captured["env"]
    assert not any(key.startswith("SMEVALS_") for key in captured["env"])
    assert captured["env"]["COPILOT_GITHUB_TOKEN"] == "token"
    assert captured["env"]["SERVICE_TOKEN"] == "sensitive"
    assert any(
        arg
        == "--secret-env-vars=COPILOT_GITHUB_TOKEN,GH_TOKEN,GITHUB_TOKEN,SERVICE_TOKEN"
        for arg in captured["command"]
    )
    assert any(arg.endswith("copilot-session.md") for arg in captured["command"])


def test_run_propagates_copilot_exit_code(tmp_path, monkeypatch):
    env = env_for(tmp_path)
    monkeypatch.setattr(smevals.copilot.shutil, "which", lambda name: "copilot.exe")
    monkeypatch.setattr(
        smevals.copilot.subprocess,
        "run",
        lambda command, **kwargs: subprocess.CompletedProcess(command, 7, "", "failed"),
    )
    assert run(env) == 7


def test_failed_run_reports_missing_final_response(tmp_path, monkeypatch, capsys):
    env = env_for(tmp_path)
    monkeypatch.setattr(smevals.copilot.shutil, "which", lambda name: "copilot.exe")
    stdout = (json.dumps({"type": "result", "exitCode": 7}) + "\n").encode("utf-8")
    monkeypatch.setattr(
        smevals.copilot.subprocess,
        "run",
        lambda command, **kwargs: subprocess.CompletedProcess(
            command, 7, stdout, b""
        ),
    )

    assert run(env) == 7
    assert "without an assistant.message" in capsys.readouterr().err


def test_run_requires_installed_copilot(tmp_path, monkeypatch):
    monkeypatch.setattr(smevals.copilot.shutil, "which", lambda name: None)
    with pytest.raises(CopilotRunnerError, match="not found on PATH"):
        run(env_for(tmp_path))
