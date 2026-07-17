# smeval

A tool for small model evals.

`smeval` is a Python CLI utility for running evals against LLMs and agent harnesses.

## Vocabulary

The top-level concept is an **Eval**: a collection of Tasks used to determine how good a particular model or model-and-harness configuration is at a specific high-level capability, such as text-to-SQL, drawing a pelican riding a bicycle, or evaluating whether an implementation satisfies a provided specification.

Evals can optionally be grouped into **Suites** of related Evals, primarily as a mechanism for organizing them on disk.

An **Eval** is a collection of **Tasks**. These are the individual exercises that a model must complete for its abilities to be evaluated.

A **Config** describes the setup used to attempt Tasks. It specifies a model and may include model parameters, system prompts, tools and other settings.

To gather evidence, we create a **Run**. A Run is the immutable record of executing one Task against one Config using a **Runner**. A Runner is a reusable CLI program that may send prompts directly to a model or build on an agent harness such as Codex or Pi.

The same Task and Config can be executed multiple times, producing multiple Runs to help account for non-deterministic results. Each Run includes a timestamp to help track these.

Once we have gathered Runs, we apply a **Checklist** to each Run to produce a **Grade**. A Checklist is an ordered sequence of **Checks**, plus the rules for combining their results into that Grade.

Checks are individual assertions or measurements. Some may be simple, such as “does the output contain this text?” Others may be more complex, such as “render this SVG to an image and have an LLM judge assess it”. Each Check names the **Checker** that performs it, along with configuration for that Checker such as patterns, rubrics or expected values. A Check can be marked as required, in which case its failure halts the Checklist and skips the remaining Checks.

A **Checker** is a named operation *or* a reusable CLI program that implements one kind of Check. `contains` and `xml-valid` are named operations, `../checkers/render-svg` might be a custom program. The same Checker can be used by many Checks across many Checklists. The Checks in a Checklist execute in order and share a working directory, so a Checker may create files - such as a rendered image - which are kept with the Grade as artifacts and available to later Checks in the sequence.

A **Grade** is the result of applying a Checklist to a Run. It records the result of each Check and may contain an overall pass/fail outcome and/or a numeric score. Grades can also include additional notes which are not used for scoring but may help interpret the results in the future.

We may later change the Checklist we use to evaluate Runs without executing them again. A single Run can therefore be evaluated multiple times, producing multiple Grades using different Checklists.
