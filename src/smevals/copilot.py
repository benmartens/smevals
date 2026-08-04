import json
import math
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

from .text import configure_utf8_stdio, decode_output


class CopilotRunnerError(ValueError):
    pass


@dataclass(frozen=True)
class CopilotOptions:
    permissions: str
    allow_tools: tuple[str, ...]
    deny_tools: tuple[str, ...]
    allow_urls: tuple[str, ...]
    agent: str | None
    effort: str | None
    max_ai_credits: int | float | None
    custom_instructions: bool
    github_mcp: bool
    share_session: str | None
    secret_env_vars: tuple[str, ...]


ALLOWED_KEYS = {
    "permissions",
    "allow_tools",
    "deny_tools",
    "allow_urls",
    "agent",
    "effort",
    "max_ai_credits",
    "custom_instructions",
    "github_mcp",
    "share_session",
    "secret_env_vars",
}
PERMISSION_PROFILES = {"prompt", "workspace", "unrestricted"}
EFFORT_LEVELS = {"none", "minimal", "low", "medium", "high", "xhigh", "max"}
DEFAULT_SECRET_ENV_VARS = (
    "COPILOT_GITHUB_TOKEN",
    "GH_TOKEN",
    "GITHUB_TOKEN",
)


def main():
    configure_utf8_stdio()
    try:
        return run()
    except CopilotRunnerError as ex:
        print(f"smevals-copilot: {ex}", file=sys.stderr)
        return 2


def run(environ=None):
    environ = dict(os.environ if environ is None else environ)
    prompt = _required_env(environ, "SMEVALS_PROMPT")
    model = _required_env(environ, "SMEVALS_MODEL")
    run_dir = _required_directory(environ, "SMEVALS_RUN_DIR")
    eval_dir = _required_directory(environ, "SMEVALS_EVAL_DIR")
    config = _load_config(environ)
    options = parse_options(config.get("copilot"))

    workspace = prepare_workspace(environ, eval_dir, run_dir)
    cwd = workspace or run_dir
    executable = shutil.which("copilot")
    if not executable:
        raise CopilotRunnerError(
            "GitHub Copilot CLI was not found on PATH; install it and run "
            "'copilot login' before executing this Eval"
        )

    command = build_command(executable, prompt, model, options, cwd)
    child_env = {
        key: value
        for key, value in environ.items()
        if not key.startswith("SMEVALS_") and key != "COPILOT_ALLOW_ALL"
    }
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            env=child_env,
            capture_output=True,
        )
    except OSError as ex:
        raise CopilotRunnerError(f"could not start Copilot CLI: {ex}") from ex

    stdout = decode_output(result.stdout)
    sys.stderr.write(decode_output(result.stderr))
    if stdout:
        try:
            response = extract_final_response(stdout)
        except CopilotRunnerError as ex:
            if result.returncode == 0:
                raise
            print(f"smevals-copilot: {ex}", file=sys.stderr)
            response = ""
        sys.stdout.write(response)
    return result.returncode


def extract_final_response(output):
    response = None
    for line_number, line in enumerate(output.splitlines(), 1):
        if not line.strip():
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as ex:
            raise CopilotRunnerError(
                f"Copilot JSON output line {line_number} is invalid: {ex}"
            ) from ex
        if event.get("type") != "assistant.message":
            continue
        content = (event.get("data") or {}).get("content")
        if isinstance(content, str):
            response = content
    if response is None:
        raise CopilotRunnerError(
            "Copilot completed without an assistant.message response"
        )
    return response


def parse_options(value):
    if value is None:
        value = {}
    if not isinstance(value, dict):
        raise CopilotRunnerError("config 'copilot' must be a mapping")

    unknown = sorted(set(value) - ALLOWED_KEYS)
    if unknown:
        raise CopilotRunnerError(
            "unsupported copilot setting(s): " + ", ".join(unknown)
        )

    permissions = value.get("permissions", "prompt")
    if not isinstance(permissions, str) or permissions not in PERMISSION_PROFILES:
        raise CopilotRunnerError(
            "copilot.permissions must be one of: "
            + ", ".join(sorted(PERMISSION_PROFILES))
        )

    allow_tools = _string_list(value, "allow_tools")
    deny_tools = _string_list(value, "deny_tools")
    allow_urls = _string_list(value, "allow_urls")
    secret_env_vars = _string_list(value, "secret_env_vars")
    for name in secret_env_vars:
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name):
            raise CopilotRunnerError(
                f"copilot.secret_env_vars contains invalid name {name!r}"
            )

    agent = _optional_string(value, "agent")
    effort = _optional_string(value, "effort")
    if effort is not None and effort not in EFFORT_LEVELS:
        raise CopilotRunnerError(
            "copilot.effort must be one of: " + ", ".join(sorted(EFFORT_LEVELS))
        )

    max_ai_credits = value.get("max_ai_credits")
    if max_ai_credits is not None:
        if (
            isinstance(max_ai_credits, bool)
            or not isinstance(max_ai_credits, (int, float))
            or not math.isfinite(max_ai_credits)
            or max_ai_credits < 30
        ):
            raise CopilotRunnerError(
                "copilot.max_ai_credits must be at least 30"
            )

    custom_instructions = _boolean(value, "custom_instructions", False)
    github_mcp = _boolean(value, "github_mcp", False)
    share_session = _share_session(value.get("share_session", False))

    if permissions == "unrestricted" and (allow_tools or allow_urls):
        raise CopilotRunnerError(
            "copilot.allow_tools and allow_urls are redundant with "
            "permissions: unrestricted"
        )

    return CopilotOptions(
        permissions=permissions,
        allow_tools=allow_tools,
        deny_tools=deny_tools,
        allow_urls=allow_urls,
        agent=agent,
        effort=effort,
        max_ai_credits=max_ai_credits,
        custom_instructions=custom_instructions,
        github_mcp=github_mcp,
        share_session=share_session,
        secret_env_vars=tuple(
            dict.fromkeys((*DEFAULT_SECRET_ENV_VARS, *secret_env_vars))
        ),
    )


def build_command(executable, prompt, model, options, cwd):
    command = [
        executable,
        "-p",
        prompt,
        "-s",
        "--output-format=json",
        "--stream=off",
        "--no-color",
        "--no-ask-user",
        "--no-remote",
        "--no-remote-export",
        "--no-auto-update",
        "--no-experimental",
        "--disallow-temp-dir",
        "--model",
        model,
    ]
    if not options.custom_instructions:
        command.append("--no-custom-instructions")
    if not options.github_mcp:
        command.append("--disable-builtin-mcps")
    if options.agent:
        command.extend(("--agent", options.agent))
    if options.effort:
        command.extend(("--effort", options.effort))
    if options.max_ai_credits is not None:
        command.extend(("--max-ai-credits", str(options.max_ai_credits)))

    allow_tools = list(options.allow_tools)
    if options.permissions == "workspace":
        allow_tools = ["read", "write", *allow_tools]
    if options.permissions == "unrestricted":
        command.append("--allow-all")
    elif allow_tools:
        command.append("--allow-tool=" + ",".join(dict.fromkeys(allow_tools)))

    for tool in options.deny_tools:
        command.append(f"--deny-tool={tool}")
    for url in options.allow_urls:
        command.append(f"--allow-url={url}")
    if options.secret_env_vars:
        command.append("--secret-env-vars=" + ",".join(options.secret_env_vars))
    if options.share_session:
        command.append(f"--share={cwd / options.share_session}")
    return command


def prepare_workspace(environ, eval_dir, run_dir):
    relative = environ.get("SMEVALS_TASK_COPILOT_WORKSPACE")
    if not relative:
        return None
    if not isinstance(relative, str) or not relative.strip():
        raise CopilotRunnerError("copilot_workspace must be a non-empty path")

    raw = Path(relative)
    if raw.is_absolute():
        raise CopilotRunnerError("copilot_workspace must be relative to the Eval")
    source = (eval_dir / raw).resolve()
    _require_within(source, eval_dir, "copilot_workspace")
    if source == eval_dir:
        raise CopilotRunnerError(
            "copilot_workspace must be a subdirectory, not the Eval root"
        )
    try:
        run_dir.relative_to(source)
    except ValueError:
        pass
    else:
        raise CopilotRunnerError(
            "copilot_workspace cannot contain the current Run directory"
        )
    if not source.is_dir():
        raise CopilotRunnerError(f"copilot_workspace is not a directory: {source}")
    _validate_workspace_tree(source, eval_dir)

    destination = run_dir / "workspace"
    if destination.exists():
        raise CopilotRunnerError(f"workspace destination already exists: {destination}")
    shutil.copytree(source, destination)
    return destination


def _load_config(environ):
    value = environ.get("SMEVALS_CONFIG")
    if value is None:
        raise CopilotRunnerError("SMEVALS_CONFIG is not set")
    try:
        config = json.loads(value)
    except json.JSONDecodeError as ex:
        raise CopilotRunnerError(f"SMEVALS_CONFIG is not valid JSON: {ex}") from ex
    if not isinstance(config, dict):
        raise CopilotRunnerError("SMEVALS_CONFIG must contain a JSON object")
    return config


def _required_env(environ, name):
    value = environ.get(name)
    if not isinstance(value, str) or not value.strip():
        raise CopilotRunnerError(f"{name} is not set")
    return value


def _required_directory(environ, name):
    path = Path(_required_env(environ, name)).resolve()
    if not path.is_dir():
        raise CopilotRunnerError(f"{name} is not a directory: {path}")
    return path


def _string_list(mapping, key):
    value = mapping.get(key, [])
    if not isinstance(value, list) or any(
        not isinstance(item, str) or not item.strip() for item in value
    ):
        raise CopilotRunnerError(f"copilot.{key} must be a list of strings")
    return tuple(item.strip() for item in value)


def _optional_string(mapping, key):
    value = mapping.get(key)
    if value is None:
        return None
    if not isinstance(value, str) or not value.strip():
        raise CopilotRunnerError(f"copilot.{key} must be a non-empty string")
    return value.strip()


def _boolean(mapping, key, default):
    value = mapping.get(key, default)
    if not isinstance(value, bool):
        raise CopilotRunnerError(f"copilot.{key} must be true or false")
    return value


def _share_session(value):
    if value is False or value is None:
        return None
    if value is True:
        return "copilot-session.md"
    if not isinstance(value, str) or not value.strip():
        raise CopilotRunnerError(
            "copilot.share_session must be false, true, or a filename"
        )
    filename = value.strip()
    if filename in (".", "..") or Path(filename).name != filename:
        raise CopilotRunnerError(
            "copilot.share_session filename cannot contain a directory"
        )
    return filename


def _require_within(path, root, label):
    try:
        path.relative_to(root)
    except ValueError as ex:
        raise CopilotRunnerError(f"{label} must stay within the Eval") from ex


def _validate_workspace_tree(source, eval_dir):
    for current, directories, filenames in os.walk(source):
        current_path = Path(current)
        for name in [*directories, *filenames]:
            path = current_path / name
            if path.is_symlink():
                raise CopilotRunnerError(
                    f"copilot_workspace cannot contain symlinks: {path}"
                )
            _require_within(path.resolve(), eval_dir, "copilot_workspace")
