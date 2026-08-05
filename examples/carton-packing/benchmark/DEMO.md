# Carton Packing Demo

Before presenting, serve `benchmark\private\site` with
`python -m http.server 8000` and open `http://127.0.0.1:8000`.

1. Open `fixtures\starter\README.md` and show the incomplete
   `CartonPacker.Pack`.
2. Explain that models can run the visible .NET harness, while the exact
   benchmark cases do not exist yet.
3. Open the static site's full model-matrix leaderboard.
4. Compare `valid_layout_rate`, `average_value_ratio`, support-related tags,
   and total scores.
5. Open `showcase-layout.svg` for a high-scoring and low-scoring model.
6. Compare their `solution.patch` artifacts.
7. Show `summary.md` for one failed scenario and one strong scenario.
8. Emphasize that every model was graded against the same generated bundle and
   that the presentation performs no live model calls.
