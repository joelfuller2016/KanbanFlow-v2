# Issue Quality Checklist

Every issue must have **well-defined scope** and a concrete **how + what** plan — never a
problem-only stub. This is the issue-side companion to the PR template's *Pre-PR Analysis gate*
(#176): the analysis happens when the issue is written, the verification when the PR is opened.

> Origin: roadmap #163 ("Issues stay well-defined: Summary · Evidence `file:line@commit` · Fix plan ·
> Acceptance criteria · Test plan"). This file makes that standard explicit and reusable.

## ANALYSIS — do this BEFORE writing the issue (never assume)

- [ ] **Read the actual code** involved (models, services, endpoints) and cite `file:line @ <commit>` as evidence.
- [ ] **Verify the conventions** against the repo — don't assume defaults:
  - Schema evolution = `EnsureCreated()` + `EnsureXxx` startup helpers in `Program.cs` (**not** EF migrations).
  - **No GitHub Actions** (local testing only). **Open access** — local-first/no-auth by design; host/CORS restrictions are intentionally NOT enforced (open-access policy, #170).
  - **Audit trail is sacred** — every mutation (UI / REST / MCP) emits an `ActivityEvent`.
  - **No fake tests** — real SQLite, real assertions.
- [ ] **Search existing issues + PRs** for duplicates / overlap, and flag any **conflict** with another open issue; link them.
- [ ] **Place it** — assign a priority (P0–P3 in the title) and a milestone (M1–M5, see #163).

## CONTENT — the issue MUST contain

1. **Summary** — what + why, one paragraph.
2. **Evidence** — `file:line @ commit` proving the problem / current state.
3. **Scope** — explicit in-scope / out-of-scope.
4. **Fix plan (HOW)** — numbered, concrete; names the files/methods to touch.
5. **Acceptance criteria (WHAT-done)** — testable checkboxes.
6. **Test plan** — real tests (xUnit / `scripts/smoke-api.ps1`), no fakes.
7. **Constraints honored** — the conventions above that this change touches.
8. **Dependencies / sequencing** — what must land first; milestone.
9. **Open questions** — each with a stated default.
10. **Labels** — for triage + CodeRabbit auto-planning.

## Leverage

CodeRabbit auto-planning is enabled (issues with actionable labels get a codebase-aware Fix plan).
For an under-planned issue, comment `@coderabbitai plan`, then review and fold the result in.

> A problem-only issue is not "ready". If it can't pass this checklist, it isn't actionable yet.
