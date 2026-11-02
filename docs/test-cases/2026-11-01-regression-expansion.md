# Workflow and transport regression contract: canonical location

The reusable [PR-584 contract](584-workflow-transport-regressions/README.md) and its
[registry entry](README.md#registry) now follow this repository's stable-case/run conventions.
There is no separate GitHub issue; [PR #584](https://github.com/joelfuller2016/KanbanFlow-v2/pull/584),
request `9327981b-5625-4912-90ee-efdae8ad9928` (project 2), and the
[implementation plan](../plans/2026-11-01-pr-review-and-regression-expansion.md) own this work.

All authored behavior, requirement/test mappings, rerun guidance and execution history from this
former combined record are retained in the canonical case and these separate run records:

- [Unmodified baseline and dirty-source focused evidence, base `789c6b4743f4`](584-workflow-transport-regressions/runs/2026-11-01-789c6b4743f4.md).
- [Broad failures and dirty Settings harness controls, base `a97b3c20b6e7`](584-workflow-transport-regressions/runs/2026-11-01-a97b3c20b6e7.md).
- [Recorded clean-source gates and the later blocking Settings counterexample at `485d1c40f760`](584-workflow-transport-regressions/runs/2026-11-01-485d1c40f760.md).

This file remains as a compatibility pointer for existing links. Run results are evidence for their
recorded source, with failures and limitations preserved; they do not establish merge or human approval.
