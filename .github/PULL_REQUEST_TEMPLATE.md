# Pull Request

<!-- Title: use a conventional-commit style summary, e.g. "feat(api): add ...". -->

## Summary
<!-- What does this PR change and why? 1-3 sentences. -->

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
