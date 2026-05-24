# Pull Request

<!-- Title: use a conventional-commit style summary, e.g. "feat(api): add ...". -->

## Summary
<!-- What does this PR change and why? 1-3 sentences. -->

## Pre-PR Analysis (required — complete all four before review/approval)
1. **Problem solved** — <!-- what problem/need does this address? (reference it in "Linked issue" below). If it solves no stated problem, justify why it should exist. -->
2. **Impact** — <!-- impact of any & all changes: files/areas/consumers affected, behavior + side effects, breaking changes, data/schema/API/migration/perf/security, regression risk. -->
3. **Docs** — <!-- README/docs updated, OR "no docs change needed: <reason>". -->
4. **Plan / scope** — <!-- detailed scope so a human/AI understands the work: approach, in/out of scope, files touched, acceptance criteria. -->

## Linked issue
<!-- "Closes #NNN" so the issue auto-closes on merge. -->
Closes #

## Test evidence
<!-- How was this verified? Paste output or describe. -->
- [ ] `./scripts/verify.ps1` passes locally (build + tests required; format advisory)
- [ ] New/changed behavior is covered by tests (no fake/mock-only tests)
- [ ] Smoke tests pass if API/MCP surface changed (`scripts/smoke-api.ps1`)

## Checklist

- [ ] Mutations route through the shared service layer (audit trail intact — see #88)
- [ ] No GitHub Actions added (intentional — local-first project)
- [ ] No secrets committed; CORS/local-only assumptions unchanged
- [ ] Docs/README updated if behavior or API changed
- [ ] Pre-PR Analysis above completed (problem · impact · docs · plan/scope)
