# KanbanFlow test cases

This directory is the registry for feature-level test cases that need more context than an
individual unit or browser test can carry. A test case defines the reusable contract; a file under
that case's `runs/` directory records what was actually executed at one immutable commit.

## Registry

| ID | Feature | Stable case | Latest recorded run | Status |
|---|---|---|---|---|
| TC-496 | Decision & Memory Map | [Case](496-decision-memory-map/README.md) | [2026-08-30 · `d7be7da5d98d`](496-decision-memory-map/runs/2026-08-30-d7be7da5d98d.md) | Passed at the pinned implementation commit |
| TC-509 | Human decisions on the existing workflow board | [Case](509-unified-workflow/README.md) | [2026-09-07 · `b923a62b6a9c`](509-unified-workflow/runs/2026-09-07-b923a62b6a9c.md) | Automated checks passed; actual human usability review pending |

## Conventions

- Use `docs/test-cases/<issue>-<slug>/README.md` for the stable specification.
- Put measured results in `runs/YYYY-MM-DD-<12-character-commit>.md`. Never silently carry a
  result forward to another commit.
- Link every case to its issue, implementation PR, executable tests, and source files.
- Separate automated assertions, manual/source-review checks, observed dogfood evidence, and known
  gaps. “Implemented” is not a synonym for “automatically tested.”
- State fixture ownership and cleanup. Automated cases must not write to the user's live database.
- Keep secrets, credentials, private scratchpads, and model-hidden reasoning out of fixtures and
  evidence.
- Record failed or flaky attempts when they changed the implementation or harness; they are useful
  regression history, not noise to erase.
- Treat screenshots as evidence only when their runtime, viewport, commit, and capture procedure are
  recorded. No screenshot is required for a behavior already proven deterministically in code.

## Result vocabulary

| Result | Meaning |
|---|---|
| Passed | Every hard criterion in that run passed; declared gaps remain outside the hard gate. |
| Failed | At least one hard criterion failed. Record the failure before fixing or rerunning it. |
| Blocked | The case could not execute because a prerequisite was unavailable. |
| Not run | The check was deliberately skipped; the run must state why and what evidence replaced it. |

Test runs are evidence, not approval. GitHub remains authoritative for issue state, code review,
approval, and merge state.
