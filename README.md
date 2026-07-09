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

Once we have gathered Runs, we use a **Grader**, configured by a **Methodology**, to evaluate each Run. A Grader is a reusable CLI program that evaluates a Run and produces a **Grade**. A Methodology combines the Grader with additional configuration, including the sequence of Checks along with any rubrics and expected values.

Graders use a sequence of **Checks** to evaluate a Run. Checks are individual assertions or measurements. Some may be simple, such as “does the output contain this text?” Others may be more complex, such as “how does an LLM judge assess this output?”

A **Grade** is the result of applying a Methodology to a Run. It may contain a pass/fail outcome and/or a numeric score. Grades can also include additional notes which are not used for scoring but may help interpret the results in the future.

We may later change our Methodology for evaluating Runs without executing them again. A single Run can therefore be evaluated multiple times, producing multiple Grades using different Methodologies.
