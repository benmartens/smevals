import os
import subprocess
import sys

import pytest

import smevals.programs
from conftest import python_script, write_executable
from smevals.programs import ProgramError, resolve_program, run_program
from smevals.text import write_text


def test_relative_program_takes_precedence_and_runs(tmp_path):
    script = write_executable(
        tmp_path / "tool",
        python_script("""\
            import os
            print(os.environ["PROGRAM_TEST"])
            """),
    )

    program = resolve_program("tool", tmp_path)
    result = run_program(
        program,
        cwd=tmp_path,
        env=os.environ | {"PROGRAM_TEST": "portable"},
    )

    assert program.path == script.resolve()
    assert result.returncode == 0
    assert result.stdout == "portable\n"


def test_bare_program_falls_back_to_path(tmp_path, monkeypatch):
    script = write_executable(
        tmp_path / "path-tool.py",
        python_script("print('from-path')\n"),
    )
    monkeypatch.setattr(smevals.programs.shutil, "which", lambda name: str(script))

    program = resolve_program("path-tool", tmp_path / "elsewhere")
    result = run_program(program, cwd=tmp_path, env=os.environ.copy())

    assert program.path == script.resolve()
    assert result.stdout == "from-path\n"


def test_relative_path_does_not_fall_back_to_path(tmp_path, monkeypatch):
    called = False

    def fake_which(name):
        nonlocal called
        called = True

    monkeypatch.setattr(smevals.programs.shutil, "which", fake_which)

    with pytest.raises(ProgramError, match="is not a file"):
        resolve_program("../missing", tmp_path)
    assert called is False


def test_missing_bare_program_reports_path_lookup(tmp_path, monkeypatch):
    monkeypatch.setattr(smevals.programs.shutil, "which", lambda name: None)
    with pytest.raises(ProgramError, match="was not found on PATH"):
        resolve_program("missing-tool", tmp_path)


def test_local_directory_does_not_fall_back_to_path(tmp_path, monkeypatch):
    (tmp_path / "tool").mkdir()
    called = False

    def fake_which(name):
        nonlocal called
        called = True

    monkeypatch.setattr(smevals.programs.shutil, "which", fake_which)
    with pytest.raises(ProgramError, match="is not a file"):
        resolve_program("tool", tmp_path)
    assert called is False


@pytest.mark.skipif(os.name != "nt", reason="Windows command-script behavior")
def test_windows_cmd_program_runs(tmp_path):
    script = tmp_path / "tool.cmd"
    write_text(script, "@echo off\necho command-script\n")

    program = resolve_program("tool.cmd", tmp_path)
    result = run_program(program, cwd=tmp_path, env=os.environ.copy())

    assert result.returncode == 0
    assert result.stdout == "command-script\n"


@pytest.mark.skipif(os.name != "nt", reason="Windows script validation")
def test_windows_rejects_unrecognized_script(tmp_path):
    script = tmp_path / "tool"
    write_text(script, "not a program\n")

    with pytest.raises(ProgramError, match="not a recognized Windows"):
        resolve_program("tool", tmp_path)


@pytest.mark.skipif(os.name != "nt", reason="Windows shebang parsing")
def test_windows_reports_non_utf8_shebang(tmp_path):
    script = tmp_path / "tool"
    script.write_bytes(b"#!/usr/bin/env python3 # \xff\n")

    with pytest.raises(ProgramError, match="non-UTF-8 shebang"):
        resolve_program("tool", tmp_path)


@pytest.mark.skipif(os.name == "nt", reason="POSIX executable-bit behavior")
def test_posix_requires_executable_bit(tmp_path):
    script = tmp_path / "tool"
    write_text(script, python_script("print('nope')\n"))

    with pytest.raises(ProgramError, match="is not executable"):
        resolve_program("tool", tmp_path)


def test_program_display_includes_interpreter_for_python_scripts(tmp_path):
    script = write_executable(tmp_path / "tool.py", "print('ok')\n")

    program = resolve_program("tool.py", tmp_path)

    if os.name == "nt":
        assert sys.executable in program.display
    else:
        assert program.display == str(script.resolve())
