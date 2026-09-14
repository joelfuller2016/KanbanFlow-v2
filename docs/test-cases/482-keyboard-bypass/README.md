# #482 — Keyboard bypass (partial delivery)

## Contract

This slice makes the existing board reachable without traversing the outline. It does not replace the outline with a roving ARIA tree and does not close #482.

Traceability: [issue #482](https://github.com/joelfuller2016/KanbanFlow-v2/issues/482),
[implementation PR #523](https://github.com/joelfuller2016/KanbanFlow-v2/pull/523),
[executable browser scenario](../../../scripts/project-skip-navigation.browser.mjs),
[layout](../../../src/KanbanFlow/Components/Layout/ProjectViewLayout.razor),
[board heading](../../../src/KanbanFlow/Components/Shared/KanbanBoard.razor),
[module](../../../src/KanbanFlow/wwwroot/js/project-skip-links.js),
[styles](../../../src/KanbanFlow/wwwroot/app.css), and
[asset loading](../../../src/KanbanFlow/Components/App.razor).

| Case | Expected behavior | Executable evidence |
|---|---|---|
| Loaded 170-task board | First Tab reaches search bypass; Enter focuses actual search input | `project-skip-navigation.browser.mjs` |
| Cards bypass | Second Tab reaches cards bypass; Enter focuses named board heading; first card title within five total Tabs | Same browser scenario |
| Existing title action | Enter and Space open details, Escape closes | Same scenario plus existing UI suite |
| Filtered URL | Search text/query parameters survive bypass | Same scenario |
| Empty results | Board heading remains a real destination | Same scenario |
| Narrow screen | Focused link is visible within 390px; no page-wide overflow; closed outline remains inert | Same scenario |
| High contrast | Visible focus outline under forced colors | Same scenario |
| Missing project | Focus moves to named main fallback, not a missing card | Same scenario |
| Native fallback/module failure | Link retains project/query and focuses the actual search/cards destination; next Tab bypasses the outline | Browser regression added following independent review |
| Navigation/modal isolation | Bypass survives navigation and does not move behind a visible modal | Browser regression added following independent review |

## Implementation boundaries

- Layout links are before navigation, visually hidden except when focused.
- Existing search ID and named board heading are reused.
- `tabindex=-1` on heading/main enables programmatic focus without adding normal Tab stops.
- Delegated module handler retains no Blazor circuit/component references and does not rewrite filter state.
- Native fallback must account for the application's `<base href="/">`.
- Preserve outline checkboxes/buttons, SortableJS containers/identity, card activation, and local-value filter typing protection.
- No API/model/schema/sync/deployment changes; no live database tests.

## Reproduce

From the isolated worktree, after installing dependencies and Chromium:

The following block uses Bash syntax. In PowerShell 7, replace its inline UI environment
assignment with `$env:UI_E2E_PORT='5278'; $env:SKIP_BUILD='1'; npm run test:ui`.

```bash
npm ci
npx playwright install chromium
pwsh -NoProfile -File scripts/verify.ps1
npm run test:scripts
# Reserve an unused test port; do not run multiple builds/UI suites in one worktree.
UI_E2E_PORT=5278 SKIP_BUILD=1 npm run test:ui
pwsh -NoProfile -File scripts/smoke-api.ps1 -StartupTimeoutSeconds 90
node scripts/framing-ab.mjs all
```

MCP smoke requires an explicitly booted disposable app: set `KANBANFLOW_API_URL` to that instance and run `node scripts/smoke-mcp.mjs <fixture-project-id>`. Discover/create the fixture project; do not assume production project 1 or point this mutation suite at the installed app. Run `/api/admin/run-tests?suite=all` only on that disposable instance.

Optional embedding calls may exceed smoke-api's five-second client budget if the configured provider is slow/unavailable. For the offline/fail-soft profile, explicitly use an unreachable loopback embedding endpoint and a bounded `KANBANFLOW_LLM_TIMEOUT_MS`; record that configuration. It proves keyword fallback and validation, not a live provider's success path.

## Red/green discipline

The original code was tested before implementation: the new scenario failed because the first Tab had no `skip-to-board-search` target, while the existing 55 scenarios passed. The independent reviewer later identified a separate native-fallback defect; it receives its own failing regression before repair. No failing assertion was weakened to manufacture green output.

The [full run record](runs/2026-09-14-f39c404e992a.md) is pinned to implementation commit `f39c404e992a` and separate from this stable contract. A successful prior run is not verification of a later edit. Report intermittent failures and advisory formatter drift alongside the final passing run. Human workflow acceptance remains outstanding; browser automation is not human approval.


The [PR review follow-up](runs/2026-09-14-pr523-review.md) records later corrections and their actual verification. The standalone `node scripts/verify-project-skip-links.mjs` check uses Chromium with the real module/styles and a small DOM fixture; it starts no Kanban server and exercises no database. It complements the [full application scenario](../../../scripts/project-skip-navigation.browser.mjs), whose rerun remains pending for that follow-up.

Latest review correction: [breadcrumb load generation at 37050d83b18c](runs/2026-09-14-37050d83b18c.md), with the rerun canonical gate and explicit pending application/browser verification.
