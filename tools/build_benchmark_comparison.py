from __future__ import annotations

import argparse
import html
import json
from pathlib import Path
from statistics import mean


BENCHMARKS = (
    ("carton-packing", "Carton packing"),
    ("query-optimizer", "Query optimizer"),
    ("field-service-route-planner", "Field-service route planner"),
    ("replicated-shard-rebalancer", "Replicated-shard rebalancer"),
)


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def model_roster(results_root: Path, reports: dict[str, dict]) -> list[dict]:
    configured: dict[str, dict] = {}
    for slug, _ in BENCHMARKS:
        path = results_root / slug / "models.json"
        if not path.exists():
            continue
        for model in load_json(path).get("models", []):
            configured.setdefault(model["id"], model)

    for report in reports.values():
        for row in report.get("rows", []):
            configured.setdefault(
                row["model"],
                {"id": row["model"], "label": row["model"], "enabled": True},
            )

    return list(configured.values())


def valid_rate(metrics: dict) -> float | None:
    preferred = (
        "valid_layout_rate",
        "valid_plan_rate",
        "valid_route_rate",
        "valid_rebalance_rate",
        "valid_placement_rate",
    )
    for key in preferred:
        value = metrics.get(key)
        if isinstance(value, (int, float)):
            return float(value)
    for key, value in metrics.items():
        if key.startswith("valid_") and key.endswith("_rate"):
            if isinstance(value, (int, float)):
                return float(value)
    return None


def build_payload(results_root: Path) -> dict:
    reports: dict[str, dict] = {}
    benchmark_items = []
    for slug, label in BENCHMARKS:
        report_path = results_root / slug / "report.json"
        if not report_path.exists():
            raise FileNotFoundError(f"Missing benchmark report: {report_path}")
        report = load_json(report_path)
        reports[slug] = report
        benchmark_items.append(
            {
                "slug": slug,
                "label": label,
                "grader": report.get("grader"),
                "graderVersion": report.get("grader_version"),
                "rows": report.get("rows", []),
            }
        )

    roster = model_roster(results_root, reports)
    enabled_models = [
        model for model in roster if model.get("enabled", True)
    ]
    model_order = [model["id"] for model in enabled_models]
    labels = {
        model["id"]: model.get("label", model["id"]) for model in roster
    }

    row_lookup = {
        (benchmark["slug"], row["model"]): row
        for benchmark in benchmark_items
        for row in benchmark["rows"]
    }
    summaries = []
    for model_id in model_order:
        rows = [
            row_lookup[(slug, model_id)]
            for slug, _ in BENCHMARKS
            if (slug, model_id) in row_lookup
        ]
        rates = [
            rate
            for row in rows
            if (rate := valid_rate(row.get("metrics", {}))) is not None
        ]
        summaries.append(
            {
                "model": model_id,
                "label": labels.get(model_id, model_id),
                "coverage": len(rows),
                "averageScore": mean(row["score"] for row in rows) if rows else None,
                "passes": sum(row.get("outcome") == "pass" for row in rows),
                "deterministic": sum(
                    bool(row.get("metrics", {}).get("deterministic"))
                    for row in rows
                ),
                "averageValidRate": mean(rates) if rates else None,
            }
        )

    benchmark_stats = []
    for item in benchmark_items:
        scores = [float(row["score"]) for row in item["rows"]]
        benchmark_stats.append(
            {
                "slug": item["slug"],
                "label": item["label"],
                "runCount": len(scores),
                "averageScore": mean(scores) if scores else None,
                "passCount": sum(
                    row.get("outcome") == "pass" for row in item["rows"]
                ),
                "minScore": min(scores) if scores else None,
                "maxScore": max(scores) if scores else None,
            }
        )

    complete = [item for item in summaries if item["coverage"] == len(BENCHMARKS)]
    top = max(
        complete or summaries,
        key=lambda item: (
            item["averageScore"] if item["averageScore"] is not None else -1,
            item["coverage"],
            item["label"],
        ),
    )
    hardest = min(
        benchmark_stats,
        key=lambda item: (
            item["averageScore"] if item["averageScore"] is not None else 2,
            item["label"],
        ),
    )
    widest = max(
        benchmark_stats,
        key=lambda item: (
            (item["maxScore"] or 0) - (item["minScore"] or 0),
            item["label"],
        ),
    )
    missing = len(BENCHMARKS) * len(model_order) - sum(
        item["coverage"] for item in summaries
    )

    return {
        "benchmarks": benchmark_items,
        "benchmarkStats": benchmark_stats,
        "models": enabled_models,
        "modelSummaries": summaries,
        "findings": [
            (
                f"{top['label']} has the strongest average normalized score "
                f"among the models with the best available coverage."
            ),
            (
                f"{hardest['label']} has the lowest mean score "
                f"({hardest['averageScore']:.2f}) across its graded runs."
            ),
            (
                f"{widest['label']} has the widest model spread "
                f"({widest['minScore']:.2f} to {widest['maxScore']:.2f})."
            ),
            (
                f"{missing} model-benchmark cells are missing because a run "
                "was unavailable, timed out, or was not graded; missing cells "
                "are not converted to zero scores."
            ),
        ],
    }


def render(payload: dict) -> str:
    encoded = json.dumps(payload, ensure_ascii=False).replace("</", "<\\/")
    findings = "\n".join(
        f"<li>{html.escape(item)}</li>" for item in payload["findings"]
    )
    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Copilot coding benchmark comparison</title>
<script>
  (() => {{
    const param = new URLSearchParams(window.location.search).get("scoutTheme");
    const theme =
      param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    document.documentElement.setAttribute("data-theme", theme);
  }})();
</script>
<style>
:root {{
  color-scheme: light;
  --cp-bg: #f7f4ef;
  --cp-bg-elevated: #fcfbf8;
  --cp-surface: #ffffff;
  --cp-surface-soft: #f5f5f5;
  --cp-border: #dedede;
  --cp-border-strong: #919191;
  --cp-text: #242424;
  --cp-text-muted: #5c5c5c;
  --cp-text-soft: #6f6f6f;
  --cp-accent: #b11f4b;
  --cp-accent-hover: #9a1a41;
  --cp-accent-soft: rgba(177, 31, 75, 0.08);
  --cp-accent-fg: #ffffff;
  --cp-success: #16a34a;
  --cp-danger: #dc2626;
  --cp-warning: #f59e0b;
  --cp-link: #0078d4;
  --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.12);
  --cp-overlay: rgba(255, 255, 255, 0.8);
  --cp-panel: rgba(255, 255, 255, 0.86);
  --cp-panel-strong: rgba(255, 255, 255, 0.96);
  --cp-sheen: rgba(255, 255, 255, 0.55);
  --cp-highlight: rgba(177, 31, 75, 0.12);
}}
html[data-theme="dark"] {{
  color-scheme: dark;
  --cp-bg: #3d3b3a;
  --cp-bg-elevated: #343231;
  --cp-surface: #292929;
  --cp-surface-soft: #2e2e2e;
  --cp-border: #474747;
  --cp-border-strong: #5f5f5f;
  --cp-text: #dedede;
  --cp-text-muted: #919191;
  --cp-text-soft: #b0b0b0;
  --cp-accent: #fd8ea1;
  --cp-accent-hover: #fb7b91;
  --cp-accent-soft: rgba(253, 142, 161, 0.14);
  --cp-accent-fg: #1a1a1a;
  --cp-success: #4ade80;
  --cp-danger: #f87171;
  --cp-warning: #fbbf24;
  --cp-link: #4da6ff;
  --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.32);
  --cp-overlay: rgba(41, 41, 41, 0.88);
  --cp-panel: rgba(41, 41, 41, 0.72);
  --cp-panel-strong: rgba(41, 41, 41, 0.96);
  --cp-sheen: rgba(255, 255, 255, 0.04);
  --cp-highlight: rgba(253, 142, 161, 0.12);
}}
* {{ box-sizing: border-box; }}
body {{
  margin: 0;
  background: var(--cp-bg);
  color: var(--cp-text);
  font-family: "Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif;
}}
main {{ width: min(1440px, calc(100% - 32px)); margin: 0 auto; padding: 32px 0 64px; }}
h1 {{ margin: 0 0 8px; font-size: clamp(2rem, 5vw, 3.5rem); letter-spacing: -0.04em; }}
h2 {{ margin: 0 0 16px; font-size: 1.35rem; }}
p {{ color: var(--cp-text-muted); line-height: 1.55; }}
.eyebrow {{ color: var(--cp-accent); font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; }}
.grid {{ display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 16px; margin: 24px 0; }}
.card {{ background: var(--cp-surface); border: 1px solid var(--cp-border); border-radius: 16px; padding: 20px; }}
.metric {{ font-size: 2rem; font-weight: 700; }}
.muted {{ color: var(--cp-text-muted); }}
.section {{ margin-top: 24px; }}
.table-wrap {{ overflow-x: auto; border: 1px solid var(--cp-border); border-radius: 16px; background: var(--cp-surface); }}
table {{ border-collapse: collapse; width: 100%; min-width: 860px; }}
th, td {{ border-bottom: 1px solid var(--cp-border); padding: 12px; text-align: left; }}
th {{ background: var(--cp-surface-soft); color: var(--cp-text-muted); font-size: 0.82rem; text-transform: uppercase; letter-spacing: 0.04em; }}
tr:last-child td {{ border-bottom: 0; }}
.score {{
  font-variant-numeric: tabular-nums;
  font-weight: 700;
  background: linear-gradient(
    to right,
    var(--cp-highlight) var(--score-pct),
    var(--cp-surface) var(--score-pct)
  );
}}
.missing {{ color: var(--cp-text-muted); background: var(--cp-surface-soft); }}
.pass {{ color: var(--cp-success); }}
.fail {{ color: var(--cp-danger); }}
.controls {{ display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 16px; }}
label {{ color: var(--cp-text-muted); font-weight: 600; }}
select {{
  margin-left: 8px; padding: 8px 12px; border: 1px solid var(--cp-border-strong);
  border-radius: 0.625rem; background: var(--cp-surface); color: var(--cp-text);
  font: inherit;
}}
ul {{ margin: 0; padding-left: 20px; }}
li {{ margin: 8px 0; line-height: 1.45; }}
code {{ font-family: Consolas, "Courier New", Courier, monospace; color: var(--cp-accent); }}
@media (max-width: 900px) {{ .grid {{ grid-template-columns: repeat(2, minmax(0, 1fr)); }} }}
@media (max-width: 560px) {{ main {{ width: min(100% - 20px, 1440px); padding-top: 20px; }} .grid {{ grid-template-columns: 1fr; }} }}
</style>
</head>
<body>
<main>
  <div class="eyebrow">GitHub Copilot CLI · smevals</div>
  <h1>Coding benchmark comparison</h1>
  <p>Four deterministic agentic coding tasks, normalized within each benchmark. Missing runs remain missing rather than being scored as zero.</p>
  <div id="summary" class="grid"></div>
  <section class="card">
    <h2>Findings</h2>
    <ul>{findings}</ul>
  </section>
  <section class="section">
    <h2>Model × benchmark</h2>
    <div class="table-wrap"><table id="matrix"></table></div>
  </section>
  <section class="section card">
    <h2>Benchmark detail</h2>
    <div class="controls">
      <label>Benchmark<select id="benchmarkSelect"></select></label>
      <label>Sort<select id="sortSelect"><option value="score">Score</option><option value="model">Model</option></select></label>
    </div>
    <div class="table-wrap"><table id="detail"></table></div>
  </section>
  <section class="section card">
    <h2>Interpretation</h2>
    <p>Scores express performance relative to each benchmark's independent reference objective. They support within-task ranking and broad cross-task consistency analysis, but raw metrics differ: packing optimizes value and volume, query planning minimizes execution cost, routing maximizes served value then minimizes travel, and shard rebalancing minimizes utilization and movement.</p>
  </section>
</main>
<script>
const DATA = {encoded};
const byBenchmark = new Map(DATA.benchmarks.map(item => [item.slug, item]));
const modelLabels = new Map(DATA.models.map(item => [item.id, item.label || item.id]));
const format = value => value == null ? "—" : Number(value).toFixed(2);

function renderSummary() {{
  const runs = DATA.benchmarks.reduce((total, item) => total + item.rows.length, 0);
  const cells = [
    ["Benchmarks", DATA.benchmarks.length],
    ["Enabled models", DATA.models.length],
    ["Graded runs", runs],
    ["Missing cells", DATA.benchmarks.length * DATA.models.length - runs],
  ];
  document.querySelector("#summary").innerHTML = cells.map(([label, value]) =>
    `<div class="card"><div class="metric">${{value}}</div><div class="muted">${{label}}</div></div>`
  ).join("");
}}

function renderMatrix() {{
  const lookup = new Map();
  DATA.benchmarks.forEach(item => item.rows.forEach(row => lookup.set(`${{item.slug}}|${{row.model}}`, row)));
  const head = `<thead><tr><th>Model</th>${{DATA.benchmarks.map(item => `<th>${{item.label}}</th>`).join("")}}<th>Average</th><th>Coverage</th></tr></thead>`;
  const summaries = [...DATA.modelSummaries].sort((a, b) =>
    (b.averageScore ?? -1) - (a.averageScore ?? -1) || b.coverage - a.coverage || a.label.localeCompare(b.label)
  );
  const body = summaries.map(model => {{
    const cells = DATA.benchmarks.map(item => {{
      const row = lookup.get(`${{item.slug}}|${{model.model}}`);
      if (!row) return `<td class="missing">Missing</td>`;
      return `<td class="score" style="--score-pct:${{row.score * 100}}%">${{format(row.score)}} <span class="${{row.outcome === "pass" ? "pass" : "fail"}}">${{row.outcome}}</span></td>`;
    }}).join("");
    return `<tr><td><strong>${{model.label}}</strong><br><code>${{model.model}}</code></td>${{cells}}<td>${{format(model.averageScore)}}</td><td>${{model.coverage}}/${{DATA.benchmarks.length}}</td></tr>`;
  }}).join("");
  document.querySelector("#matrix").innerHTML = head + `<tbody>${{body}}</tbody>`;
}}

function metricSummary(metrics) {{
  return Object.entries(metrics || {{}})
    .filter(([, value]) => typeof value === "number" || typeof value === "boolean")
    .slice(0, 5)
    .map(([key, value]) => `${{key}}=${{typeof value === "number" ? Number(value).toFixed(3).replace(/\\.000$/, "") : value}}`)
    .join(" · ");
}}

function renderDetail() {{
  const slug = document.querySelector("#benchmarkSelect").value;
  const sort = document.querySelector("#sortSelect").value;
  const item = byBenchmark.get(slug);
  const rows = [...item.rows].sort((a, b) => sort === "model"
    ? (modelLabels.get(a.model) || a.model).localeCompare(modelLabels.get(b.model) || b.model)
    : b.score - a.score || a.model.localeCompare(b.model)
  );
  const head = `<thead><tr><th>Model</th><th>Score</th><th>Outcome</th><th>Tags</th><th>Selected metrics</th></tr></thead>`;
  const body = rows.map(row => `<tr>
    <td><strong>${{modelLabels.get(row.model) || row.model}}</strong><br><code>${{row.model}}</code></td>
    <td class="score" style="--score-pct:${{row.score * 100}}%">${{format(row.score)}}</td>
    <td class="${{row.outcome === "pass" ? "pass" : "fail"}}">${{row.outcome}}</td>
    <td>${{(row.tags || []).join(", ") || "—"}}</td>
    <td>${{metricSummary(row.metrics) || "—"}}</td>
  </tr>`).join("");
  document.querySelector("#detail").innerHTML = head + `<tbody>${{body}}</tbody>`;
}}

function initializeControls() {{
  const select = document.querySelector("#benchmarkSelect");
  select.innerHTML = DATA.benchmarks.map(item => `<option value="${{item.slug}}">${{item.label}}</option>`).join("");
  select.addEventListener("change", renderDetail);
  document.querySelector("#sortSelect").addEventListener("change", renderDetail);
}}

renderSummary();
renderMatrix();
initializeControls();
renderDetail();
</script>
</body>
</html>
"""


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--results-root",
        type=Path,
        default=Path(__file__).parents[1] / "benchmark-results",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path.home()
        / "Desktop"
        / "smevals-copilot-benchmark-comparison.html",
    )
    args = parser.parse_args()
    payload = build_payload(args.results_root)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(render(payload), encoding="utf-8", newline="\n")
    print(args.output)


if __name__ == "__main__":
    main()
