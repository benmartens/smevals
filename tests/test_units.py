"""Unit tests for the small pure helpers in smevals.cli."""

import os

import pytest

from smevals.cli import (
    normalize_check_info,
    normalize_tag,
    scalar_env_vars,
    slugify,
)
from smevals.text import decode_output


class TestSlugify:
    def test_replaces_runs_of_unsafe_characters(self):
        assert slugify("gpt-4o (new)") == "gpt-4o-new"
        assert slugify("us.anthropic/claude v2") == "us.anthropic-claude-v2"

    def test_preserves_case_dots_dashes_underscores(self):
        assert slugify("My-Model_1.5") == "My-Model_1.5"

    def test_strips_leading_and_trailing_dashes(self):
        assert slugify("  weird!  ") == "weird"


class TestNormalizeTag:
    def test_lowercase_snake_case(self):
        assert normalize_tag("Wearing A Hat!") == "wearing_a_hat"

    def test_non_string_input(self):
        assert normalize_tag(42) == "42"

    def test_strips_edge_underscores(self):
        assert normalize_tag("--ok--") == "ok"


class TestScalarEnvVars:
    def test_scalars_become_stringified_env_vars(self):
        result = scalar_env_vars(
            "SMEVALS_TASK_",
            {"name": "x", "count": 3, "ratio": 0.5, "flag": True},
        )
        assert result == {
            "SMEVALS_TASK_NAME": "x",
            "SMEVALS_TASK_COUNT": "3",
            "SMEVALS_TASK_RATIO": "0.5",
            "SMEVALS_TASK_FLAG": "True",
        }

    def test_non_scalar_values_are_skipped(self):
        result = scalar_env_vars("P_", {"items": [1, 2], "meta": {"a": 1}, "ok": "y"})
        assert result == {"P_OK": "y"}

    def test_key_normalization(self):
        result = scalar_env_vars("P_", {"multi word-key": "v"})
        assert result == {"P_MULTI_WORD_KEY": "v"}


class TestNormalizeCheckInfo:
    def test_score_coerced_to_float(self):
        assert normalize_check_info({"score": 1}) == {"score": 1.0}

    def test_none_score_omitted(self):
        assert normalize_check_info({"score": None}) == {}

    def test_non_dict_metrics_ignored(self):
        assert normalize_check_info({"metrics": "high"}) == {}

    def test_tags_normalized_deduped_sorted(self):
        info = normalize_check_info(
            {"tags": ["Wearing A Hat", "wearing_a_hat", "Zebra", "", "  "]}
        )
        assert info == {"tags": ["wearing_a_hat", "zebra"]}

    def test_notes_stringified_and_empty_dropped(self):
        assert normalize_check_info({"notes": 42}) == {"notes": "42"}
        assert normalize_check_info({"notes": ""}) == {}

    def test_unknown_keys_folded_into_details(self):
        info = normalize_check_info({"output": "raw text", "custom": 1})
        assert info == {"details": {"output": "raw text", "custom": 1}}

    def test_details_merged_with_extras(self):
        info = normalize_check_info({"details": {"a": 1}, "b": 2})
        assert info == {"details": {"a": 1, "b": 2}}

    def test_core_keys_cannot_be_clobbered(self):
        # A malicious/buggy checker emitting core-owned keys sees them
        # demoted to details, never trusted at the top level
        info = normalize_check_info({"ok": False, "checker": "evil", "skipped": True})
        assert set(info) == {"details"}
        assert info["details"] == {"ok": False, "checker": "evil", "skipped": True}


def test_decode_output_accepts_utf8_bytes():
    assert decode_output("snowman: \u2603".encode("utf-8")) == "snowman: \u2603"


def test_decode_output_normalizes_newlines():
    assert decode_output(b"one\r\ntwo\rthree\n") == "one\ntwo\nthree\n"


@pytest.mark.skipif(os.name != "nt", reason="Windows native output encoding")
def test_decode_output_accepts_windows_1252():
    assert decode_output(b"em dash: \x97") == "em dash: \u2014"
