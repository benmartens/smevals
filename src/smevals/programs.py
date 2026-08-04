import os
import re
import shlex
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

from .text import decode_output


class ProgramError(ValueError):
    pass


@dataclass(frozen=True)
class Program:
    argv: tuple[str, ...]
    path: Path

    @property
    def display(self):
        if os.name == "nt":
            return subprocess.list2cmdline(self.argv)
        return shlex.join(self.argv)


def resolve_program(spec, base_dir):
    if not isinstance(spec, str) or not spec.strip():
        raise ProgramError("program must be a non-empty string")

    spec = spec.strip()
    base_dir = Path(base_dir)
    local = Path(spec)
    if not local.is_absolute():
        local = base_dir / local

    if local.exists():
        if not local.is_file():
            raise ProgramError(f"{local.resolve()} is not a file")
        return _program_for_path(local.resolve())

    if Path(spec).is_absolute() or "/" in spec or "\\" in spec:
        raise ProgramError(f"{local.resolve()} is not a file")

    found = shutil.which(spec)
    if not found:
        raise ProgramError(f"{spec!r} was not found on PATH")
    return _program_for_path(Path(found).resolve())


def run_program(program, *, cwd, env):
    result = subprocess.run(
        list(program.argv),
        cwd=cwd,
        env=env,
        capture_output=True,
    )
    result.stdout = decode_output(result.stdout)
    result.stderr = decode_output(result.stderr)
    return result


def _program_for_path(path):
    if os.name != "nt":
        if not os.access(path, os.X_OK):
            raise ProgramError(f"{path} is not executable")
        return Program((str(path),), path)

    suffix = path.suffix.lower()
    if suffix in (".exe", ".com", ".cmd", ".bat"):
        return Program((str(path),), path)
    if suffix in (".py", ".pyw"):
        return Program((sys.executable, str(path)), path)
    if suffix == ".ps1":
        powershell = shutil.which("pwsh") or shutil.which("powershell")
        if not powershell:
            raise ProgramError(f"{path} requires PowerShell, but none was found")
        return Program(
            (
                powershell,
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(path),
            ),
            path,
        )

    header = path.read_bytes()[:512]
    if header.startswith(b"MZ"):
        return Program((str(path),), path)
    if not header.startswith(b"#!"):
        raise ProgramError(
            f"{path} is not a recognized Windows executable or script"
        )

    try:
        first_line = header.splitlines()[0][2:].decode("utf-8").strip()
    except UnicodeDecodeError as ex:
        raise ProgramError(f"{path} has a non-UTF-8 shebang") from ex
    return Program(tuple(_shebang_command(first_line, path)), path)


def _shebang_command(shebang, path):
    normalized = shebang.lower().replace("\\", "/")
    if re.search(r"(^|/)python(?:w|\d+(?:\.\d+)*)?(?:\.exe)?(?:\s|$)", normalized):
        return [sys.executable, str(path)]

    try:
        parts = shlex.split(shebang)
    except ValueError as ex:
        raise ProgramError(f"{path} has an invalid shebang: {ex}") from ex
    if not parts:
        raise ProgramError(f"{path} has an empty shebang")

    interpreter = parts.pop(0)
    if Path(interpreter).name.lower() in ("env", "env.exe"):
        if parts[:1] == ["-S"]:
            parts.pop(0)
        if not parts:
            raise ProgramError(f"{path} has an invalid env shebang")
        interpreter = parts.pop(0)

    name = Path(interpreter).name
    if name.lower() in ("python", "python3", "python.exe", "python3.exe"):
        executable = sys.executable
    else:
        executable = shutil.which(name)
    if not executable:
        raise ProgramError(
            f"{path} requires interpreter {name!r}, but it was not found on PATH"
        )
    return [executable, *parts, str(path)]
