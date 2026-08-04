"""Tests for eval discovery, the static site builder and the live server."""

import http.client
import json
import socket
import threading
import time

import click
import pytest

from conftest import read_yaml, write_grade, write_run
from smevals import site
from smevals.cli import discover_evals, resolve_eval_slugs
from smevals.text import read_text


def graded_eval(make_eval, tmp_path, name="demo", models=("m-1",), scores=(1.0,)):
    eval_dir = make_eval(name=name, runner=None, root=tmp_path)
    grader_doc = read_yaml(eval_dir / "graders" / "default.yaml")
    for model, score in zip(models, scores):
        run_dir = write_run(eval_dir / "runs", model=model)
        write_grade(
            run_dir,
            grader_doc,
            score=score,
            outcome="pass" if score >= 0.5 else "fail",
        )
    return eval_dir


# --- discovery -----------------------------------------------------------


def test_discover_evals_recurses_and_stops_at_evals(make_eval, tmp_path):
    suite = tmp_path / "suite"
    a = make_eval(name="a", root=suite)
    b = make_eval(name="b", root=suite / "nested")
    make_eval(name="hidden", root=suite / ".secret")
    # A decoy eval.yaml inside an Eval must not be discovered: descent
    # stops at each Eval so runs/ trees are never scanned
    decoy = a / "runs" / "decoy"
    decoy.mkdir(parents=True)
    (decoy / "eval.yaml").write_text("name: decoy\n")

    assert list(discover_evals(suite)) == [a, b]


def test_resolve_eval_slugs_rejects_duplicates(make_eval, tmp_path):
    suite = tmp_path / "suite"
    make_eval(name="one", root=suite)
    dupe = make_eval(name="two", root=suite)
    (dupe / "eval.yaml").write_text("name: one\n")  # same name, different dir
    with pytest.raises(click.ClickException, match="Duplicate eval slug 'one'"):
        resolve_eval_slugs([suite])


def test_resolve_eval_slugs_errors_on_empty_dir(tmp_path):
    (tmp_path / "empty").mkdir()
    with pytest.raises(click.ClickException, match="No Evals found"):
        resolve_eval_slugs([tmp_path / "empty"])


# --- data layer ----------------------------------------------------------


def test_collect_eval_rows_and_grades(make_eval, tmp_path):
    eval_dir = graded_eval(make_eval, tmp_path)
    data = site.collect_eval(eval_dir)
    assert data["eval"]["name"] == "demo"
    assert data["eval"]["default_grader"] == "default"
    assert [t["name"] for t in data["eval"]["tasks"]] == ["first"]
    (row,) = data["rows"]
    assert row["task"] == "first"
    assert row["model"] == "m-1"
    assert "output.txt" in row["files"]
    grade = row["grades"]["default"]
    assert grade["outcome"] == "pass"
    assert grade["score"] == 1.0
    # grade.yaml itself is not listed among the grade's artifact files
    assert grade["files"] == ["grader.yaml"]


def test_eval_summary_picks_best_mean(make_eval, tmp_path):
    eval_dir = graded_eval(
        make_eval,
        tmp_path,
        models=("m-good", "m-good", "m-bad"),
        scores=(1.0, 0.8, 0.4),
    )
    data = site.collect_eval(eval_dir)
    summary = site.eval_summary("demo", data)
    assert summary["runs"] == 3
    assert summary["graded"] == 3
    assert summary["fails"] == 1
    assert summary["best"] == {
        "config": "default",
        "model": "m-good",
        "score": 0.9,
        "runs": 2,
    }


# --- static build --------------------------------------------------------


def test_build_creates_self_contained_site(invoke, make_eval, tmp_path):
    eval_a = graded_eval(make_eval, tmp_path, name="alpha")
    eval_b = graded_eval(make_eval, tmp_path, name="beta")
    site_dir = tmp_path / "site"
    invoke("build", eval_a, eval_b, "-o", site_dir)

    assert read_text(site_dir / "index.html") == site.app_html()
    index = json.loads(read_text(site_dir / "index.json"))
    assert index["live"] is False
    assert [e["slug"] for e in index["evals"]] == ["alpha", "beta"]

    data = json.loads(read_text(site_dir / "evals" / "alpha" / "eval.json"))
    assert len(data["rows"]) == 1
    # Run artifacts are copied into the site
    copied = site_dir / "evals" / "alpha" / "runs" / data["rows"][0]["run"]
    assert (copied / "output.txt").exists()
    assert (copied / "grades" / "default" / "grade.yaml").exists()


def test_build_refreshes_one_eval_without_touching_others(invoke, make_eval, tmp_path):
    eval_a = graded_eval(make_eval, tmp_path, name="alpha")
    eval_b = graded_eval(make_eval, tmp_path, name="beta")
    site_dir = tmp_path / "site"
    invoke("build", eval_a, "-o", site_dir)
    invoke("build", eval_b, "-o", site_dir)

    # Add a run to alpha and rebuild only alpha
    grader_doc = read_yaml(eval_a / "graders" / "default.yaml")
    write_grade(write_run(eval_a / "runs"), grader_doc, score=0.5)
    invoke("build", eval_a, "-o", site_dir)

    index = json.loads(read_text(site_dir / "index.json"))
    by_slug = {e["slug"]: e for e in index["evals"]}
    assert set(by_slug) == {"alpha", "beta"}
    assert by_slug["alpha"]["runs"] == 2
    assert by_slug["beta"]["runs"] == 1


# --- live server ---------------------------------------------------------


@pytest.fixture
def server(make_eval, tmp_path):
    eval_dir = graded_eval(make_eval, tmp_path)
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    threading.Thread(
        target=site.run_server,
        args=({"demo": eval_dir}, "default", "127.0.0.1", port),
        daemon=True,
    ).start()

    def get(path):
        for attempt in range(100):
            try:
                conn = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
                conn.request("GET", path)
                response = conn.getresponse()
                return (
                    response.status,
                    response.getheader("Content-Type"),
                    response.read(),
                )
            except ConnectionRefusedError:
                time.sleep(0.05)
        raise AssertionError("server never came up")

    return get, eval_dir


def test_serve_index_and_eval_json(server):
    get, eval_dir = server
    status, ctype, body = get("/")
    assert status == 200
    assert ctype == "text/html"

    status, ctype, body = get("/index.json")
    assert status == 200
    index = json.loads(body)
    assert index["live"] is True
    assert index["evals"][0]["slug"] == "demo"

    status, _, body = get("/evals/demo/eval.json")
    assert status == 200
    assert len(json.loads(body)["rows"]) == 1

    status, _, _ = get("/evals/nope/eval.json")
    assert status == 404


def test_serve_run_artifacts_with_inline_yaml(server):
    get, eval_dir = server
    data = site.collect_eval(eval_dir)
    rel = data["rows"][0]["run"]
    # YAML is served as text/plain so browsers render it inline
    status, ctype, body = get(f"/evals/demo/runs/{rel}/run.yaml")
    assert status == 200
    assert ctype == "text/plain; charset=utf-8"
    assert b"exit_code" in body

    status, _, body = get(f"/evals/demo/runs/{rel}/output.txt")
    assert status == 200
    assert body == b"hello world\n"


def test_serve_refuses_prefix_sibling_of_runs(server):
    # A sibling dir whose name merely starts with "runs" must not be
    # reachable - a string-prefix containment check would let it through
    get, eval_dir = server
    secret = eval_dir / "runs-secret" / "secret.txt"
    secret.parent.mkdir()
    secret.write_text("do not serve me")
    status, _, _ = get("/evals/demo/runs/../runs-secret/secret.txt")
    assert status == 404


def test_serve_refuses_paths_outside_runs(server):
    get, eval_dir = server
    # http.client sends the path verbatim - no client-side normalization
    status, _, _ = get("/evals/demo/runs/../eval.yaml")
    assert status == 404
    status, _, _ = get("/evals/demo/runs/../../../etc/passwd")
    assert status == 404
