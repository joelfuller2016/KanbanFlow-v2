# KanbanFlow working agreements

These repository rules record the user's September 7, 2026 direction. Apply them with the user's local development agreements.

- Inspect the current PR head before validation. Bring in newer commits without losing unrelated local work.
- Rebuild the current application source and test the PR before reporting it ready. Use the existing `scripts/verify.ps1` gate before every push. A `--no-build` test run is valid only after rebuilding that same source; earlier artifacts and earlier PR results are not evidence for later code.
- Record the source commit, environment, commands, exit codes, and actual results. Run relevant API, native/stdio MCP and Playwright CLI checks when those surfaces change. Keep transient databases, logs and screenshots in an ignored directory within this repository.
- Keep requests, plans, work cards, progress, decisions and results in the existing KanbanFlow app. Read the latest request context before acting, acknowledge human decisions explicitly, and revise the brief/plan before continuing affected work. A saved board message is not proof that an agent read it or stopped a process.
- Preserve the current UI by default. A major UI change needs a bounded change description, verification of each affected interaction, and actual user testing with recorded feedback before adoption. Automated browser checks and agent review do not constitute human usability approval.
- Make human review concrete: show what changed, which task is active, what is waiting for a decision, how to redirect the work, and the resulting acknowledgement and revised plan. Keep unfinished human checks marked pending.

The [workflow operating guide](docs/request-workflow.md) owns the collaboration procedure. [TC-509](docs/test-cases/509-unified-workflow/README.md) owns the repeatable browser/MCP checks and human review checklist. Update those records rather than creating a competing plan.
