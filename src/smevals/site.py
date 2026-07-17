"""Shared data layer for the HTML report, plus the live server and
static site builder.

Both `smevals serve` and `smevals build` produce the same site shape:

    index.html            the app (single self-contained file)
    index.json            site manifest: one entry per eval
    evals/<slug>/eval.json    everything about one eval
    evals/<slug>/runs/...     run + grade artifacts

serve generates the JSON per request straight from the eval
directories (mtime-cached), so the page updates live as Runs and
Grades land. build writes the same JSON to disk and copies artifacts,
producing a self-contained static site; each build call adds or
refreshes ONE eval and merges into an existing site's index.json.
"""

import json
import mimetypes
import shutil
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from importlib.resources import files
from pathlib import Path

import yaml

_yaml_cache = {}


def cached_yaml(path):
    key = str(path)
    mtime = path.stat().st_mtime_ns
    hit = _yaml_cache.get(key)
    if hit and hit[0] == mtime:
        return hit[1]
    data = yaml.safe_load(path.read_text())
    _yaml_cache[key] = (mtime, data)
    return data


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def collect_eval(eval_path, grader_name):
    "Everything the app needs to render one eval, as one JSON document"
    doc = cached_yaml(eval_path / "eval.yaml")
    tasks = [cached_yaml(p) for p in sorted((eval_path / "tasks").glob("*.yaml"))]
    configs = [cached_yaml(p) for p in sorted((eval_path / "configs").glob("*.yaml"))]
    grader_path = eval_path / "graders" / f"{grader_name}.yaml"
    grader = cached_yaml(grader_path) if grader_path.exists() else None

    rows = []
    runs_root = eval_path / "runs"
    if runs_root.exists():
        for run_file in sorted(runs_root.rglob("run.yaml")):
            run_dir = run_file.parent
            run = cached_yaml(run_file)
            task = run.get("task")
            config = run.get("config", {})
            row = {
                "run": str(run_dir.relative_to(runs_root)),
                "task": task.get("name") if isinstance(task, dict) else task,
                "config": config.get("name"),
                "model": config.get("model"),
                "started": run.get("started"),
                "duration": run.get("duration_seconds"),
                "exit_code": run.get("exit_code"),
                "imported_from": run.get("imported_from"),
                "files": sorted(
                    p.name for p in run_dir.iterdir() if p.is_file()
                ),
            }
            grade_file = run_dir / "grades" / grader_name / "grade.yaml"
            if grade_file.exists():
                grade = cached_yaml(grade_file)
                row |= {
                    "outcome": grade.get("outcome"),
                    "score": grade.get("score"),
                    "graded": grade.get("graded"),
                    "tags": grade.get("tags") or [],
                    "checks": grade.get("checks") or [],
                    "grade_files": sorted(
                        p.name
                        for p in grade_file.parent.iterdir()
                        if p.is_file() and p.name != "grade.yaml"
                    ),
                }
            rows.append(row)

    return {
        "eval": {
            "name": doc.get("name") or eval_path.name,
            "description": doc.get("description", ""),
            "tasks": tasks,
            "configs": configs,
            "grader_name": grader_name,
            "grader": grader,
        },
        "rows": rows,
        "generated": now_iso(),
    }


def eval_summary(slug, data):
    "The index.json entry for one eval"
    rows = data["rows"]
    best = None
    groups = {}
    for row in rows:
        if row.get("score") is not None:
            groups.setdefault((row["config"], row["model"]), []).append(row["score"])
    for (config, model), scores in groups.items():
        mean = sum(scores) / len(scores)
        if best is None or mean > best["score"]:
            best = {
                "config": config,
                "model": model,
                "score": round(mean, 3),
                "runs": len(scores),
            }
    return {
        "slug": slug,
        "name": data["eval"]["name"],
        "description": data["eval"]["description"],
        "runs": len(rows),
        "graded": sum(1 for r in rows if r.get("graded")),
        "fails": sum(1 for r in rows if r.get("outcome") == "fail"),
        "best": best,
        "updated": data["generated"],
    }


def app_html():
    return (files("smevals") / "app.html").read_text()


# --- live server ---------------------------------------------------------


def run_server(evals, grader_name, host, port):
    "evals: dict of slug -> eval directory Path"

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            path = self.path.split("?")[0]
            if path in ("/", "/index.html"):
                self.reply(200, app_html().encode(), "text/html")
            elif path == "/index.json":
                entries = [
                    eval_summary(slug, collect_eval(eval_path, grader_name))
                    for slug, eval_path in evals.items()
                ]
                self.reply_json(
                    {"evals": entries, "live": True, "generated": now_iso()}
                )
            elif path.startswith("/evals/"):
                self.serve_eval(path.removeprefix("/evals/"))
            else:
                self.reply(404, b"not found", "text/plain")

        def serve_eval(self, rest):
            slug, _, tail = rest.partition("/")
            eval_path = evals.get(slug)
            if eval_path is None:
                return self.reply(404, b"no such eval", "text/plain")
            if tail == "eval.json":
                return self.reply_json(collect_eval(eval_path, grader_name))
            if tail.startswith("runs/"):
                runs_root = (eval_path / "runs").resolve()
                target = (eval_path / tail).resolve()
                if str(target).startswith(str(runs_root)) and target.is_file():
                    ctype = (
                        mimetypes.guess_type(target.name)[0] or "text/plain"
                    )
                    return self.reply(200, target.read_bytes(), ctype)
            self.reply(404, b"not found", "text/plain")

        def reply_json(self, data):
            self.reply(200, json.dumps(data).encode(), "application/json")

        def reply(self, status, body, ctype):
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer((host, port), Handler)
    server.serve_forever()


# --- static build --------------------------------------------------------


def build_eval(eval_path, site_dir, grader_name, slug):
    "Add or refresh one eval in a static site directory"
    data = collect_eval(eval_path, grader_name)

    eval_dir = site_dir / "evals" / slug
    if eval_dir.exists():
        shutil.rmtree(eval_dir)
    eval_dir.mkdir(parents=True)
    (eval_dir / "eval.json").write_text(json.dumps(data))

    runs_root = eval_path / "runs"
    if runs_root.exists():
        shutil.copytree(runs_root, eval_dir / "runs")

    index_file = site_dir / "index.json"
    entries = []
    if index_file.exists():
        try:
            entries = json.loads(index_file.read_text()).get("evals", [])
        except json.JSONDecodeError:
            entries = []
    entries = [e for e in entries if e.get("slug") != slug]
    entries.append(eval_summary(slug, data))
    entries.sort(key=lambda e: e["slug"])
    index_file.write_text(
        json.dumps({"evals": entries, "live": False, "generated": now_iso()})
    )
    (site_dir / "index.html").write_text(app_html())
    return slug, len(data["rows"])
