from pathlib import Path

from tools.build_benchmark_comparison import BENCHMARKS, build_payload, render


def write_report(root: Path, slug: str, rows: list[dict]) -> None:
    import json

    directory = root / slug
    directory.mkdir(parents=True)
    (directory / "report.json").write_text(
        json.dumps(
            {
                "eval": slug,
                "grader": "default",
                "grader_version": "abc1234",
                "rows": rows,
            }
        ),
        encoding="utf-8",
    )


def test_comparison_preserves_missing_runs_and_embeds_theme(tmp_path):
    for index, (slug, _) in enumerate(BENCHMARKS):
        write_report(
            tmp_path,
            slug,
            [
                {
                    "model": "model-a",
                    "outcome": "pass",
                    "score": 1.0 - index * 0.1,
                    "tags": ["deterministic"],
                    "metrics": {
                        "deterministic": True,
                        "valid_plan_rate": 1.0,
                    },
                }
            ],
        )
    (tmp_path / BENCHMARKS[0][0] / "models.json").write_text(
        '{"models":[{"id":"model-a","label":"Model A"},'
        '{"id":"model-b","label":"Model B"}]}',
        encoding="utf-8",
    )

    payload = build_payload(tmp_path)
    model_b = next(
        item for item in payload["modelSummaries"] if item["model"] == "model-b"
    )
    assert model_b["coverage"] == 0
    assert model_b["averageScore"] is None

    document = render(payload)
    assert 'new URLSearchParams(window.location.search).get("scoutTheme")' in document
    assert "--cp-accent: #b11f4b;" in document
    assert "const DATA =" in document
    assert "Missing runs remain missing" in document
