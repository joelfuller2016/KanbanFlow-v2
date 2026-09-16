# Workflow and UI review: KanbanFlow

This is the original review of the source heads named below. Subsequent review work integrated PR #511 at `6345d5d`, pushed corrections at `a95c1ea`, and obtained CodeRabbit approval of PR #521 at `599a714`. The original findings and evidence are retained as historical observations. [PR #523 follow-up](../test-cases/482-keyboard-bypass/runs/2026-09-14-pr523-review.md) records its own corrections.

## Scope and evidence

Reviewed `master` at `8cdd684322eec15485a400c81c3372999c1789fa`, the installed IIS application, the connected native MCP project, the bodies of **182 unique open GitHub issues**, and both open PRs. Ten decision/UI issue threads received full-comment review: #515, #509, #493, #501, #147, #482, #481, #465, #492, and #491. Other issue comments were not exhaustively reviewed.

PR review covered paginated file inventories, diffs, comments, inline review threads, and live check state. #511 has 121 changed files; generated SVG geometry was not semantically reviewed. #521 has three changed files. These PR findings are **static review**, not claims that either existing PR was built or tested in this session. The implementation and executable testing in this branch are only the bounded keyboard-bypass slice below.

The installed app returned HTTP 200, opened a Blazor WebSocket, and produced no page errors or failed HTTP responses in its read-only smoke check. All mutation tests used separate test processes and disposable databases. No installed application, live task, existing branch, or deployment was changed.

## Findings, ordered by impact

### 1. Existing workflow PR needs integration and correctness work, not a competing rewrite

[#511](https://github.com/joelfuller2016/KanbanFlow-v2/pull/511), head `516432fe4b8c83fa05e7bfe5f6a23e7c6262ce48`, is draft, conflicts with the current base, and has a changes-requested review. Its CodeRabbit SUCCESS status says **review skipped because it is draft**. All 29 review threads are marked resolved; this is not proof the current code is approved or tested.

The PR already supplies request capture, plans, result reporting/acceptance, audit additions, full-content handling, and outline navigation. Preserve this investment. Reconcile its conflicts against current master, resolve the findings below, and run all gates on the exact integrated head before requesting human acceptance.

Source-checked concerns at that head:

- **P1: unsafe smoke ordering ([#522](https://github.com/joelfuller2016/KanbanFlow-v2/issues/522)).** `scripts/smoke-workflow.mjs:12–15` checks only for a supplied URL, then begins the workflow/mutation sequence. Its disposable-target restriction does not execute until `verifyAuditTransports` at line 94; that helper's lines 5–6 reject non-loopback/default production-port URLs only after earlier writes. Validate before any request/process that can write. Prove a rejected target receives zero mutations. Port checks alone do not prove a database is disposable; a self-booted fixture is safer.
- **P1: request/project attachment race.** `RequestWorkspace.razor:144–149` awaits project creation from the current request and then calls `Attach()`. `Submit` reads the then-current request ID/version at lines 122–125. The request selector remains enabled and creation has not set `busy`. Selection A→B during the await can attach B to the project created for A. Capture originating identity/version and establish operation ownership before awaiting; test reversed completion order.
- **P2: stale task-context assignment.** `TaskRequestContext.razor:24–29` checks task identity, assigns `context = await ...`, then checks identity again. The late assignment can overwrite B's context before returning from an obsolete A load. Use local results and commit both context/task only after a generation check. Include same-ID overlapping refreshes.
- **Retry evidence remains incomplete.** Capture creates a new submission/key on each click. Server idempotency for a reused key is not proof that a real UI retry after a lost response reuses that key. The PR thread already retracts the earlier same-object justification. Test actual retry/reconciliation before declaring it complete.
- **Acknowledged content-recovery limitation:** GET/check followed by ordinary PUT is not compare-and-swap. Do not broaden concurrent repair writes until there is a conditional-write contract or controlled maintenance window.

No unverified static finding was silently patched into this accessibility branch.

### 2. Small memory PR is independent but has an incomplete continuation contract

[#521](https://github.com/joelfuller2016/KanbanFlow-v2/pull/521), head `a19de6efd95077e8487910cfbe765cf4e710f288`, is draft and mergeable. Its only successful check also skips draft review; there were no substantive reviews.

It adds current project-scoped memory to native project context, returns at most 200 entries, and signals truncation. The bounded namespace/version filtering is a sensible small scope. However, its documented continuation guidance must name a usable path: existing native `read_graph`, `search_nodes`, and `open_nodes` operate in **Agent**, not **Project**, scope. A numeric project scope key does not change that namespace. A native-only caller cannot recover omitted older project decisions through those tools.

Document a supported continuation or add bounded Project-memory retrieval, and demonstrate retrieval of an intentionally omitted older decision. Review it separately from #511, preserving both PRs' additions to `KanbanMcpTools.cs` when integrated.

### 3. Backlog and board state need reconciliation

Open issue titles are not an implementation ledger:

- #481's single-line-card complaint is partly stale: master already has the WebKit-safe two-line title span, accessible name, full-title hover and focus reveal from #487. Numeric readability/human acceptance remains separate; do not implement the same clamp again.
- #465's claim that the panel has no focus trap is partly stale: the shared dialog helper and a real keyboard regression exist. Exact opener restoration and outline handoff still need their residual criteria checked, especially with #511.
- #253's narrow verification exit fix, #498's real database-health work, and #336's durable-link foundation are already represented in master. Umbrella issues retain additional acceptance, so neither “all missing” nor automatic closure is accurate.
- #149/#332/#446 overlap with #511. #405/#227/#226 overlap on task generation. #407/#411/#265 should share dependency/critical-path computation rather than create duplicate engines.

The native MCP snapshot for project 2 contained 168 tasks while GitHub had 182 open issues at the inventory snapshot. These are different populations, not an arithmetic sync invariant. More importantly, MCP still offered older closed-issue work as unfinished (for example the former #468/#462/#463/#381 items), and its next-work list included issues whose original premise has changed. Reconcile by durable GitHub identity and **remaining acceptance**, not title prefix or blind bulk status updates. This review did not run a live sync or close those records.

### 4. Keyboard access is a concrete, low-risk UI improvement now

Master places an expanded outline before board controls in DOM order. Each task contributes checkbox/title/Add controls. [#482](https://github.com/joelfuller2016/KanbanFlow-v2/issues/482) historically measured 547 Tabs to search and 556 to a card. Those historical counts are not presented as fresh measurements.

This branch implements **only the bypass portion of #482**:

- Two visible-on-focus links precede navigation: **Skip to board search** and **Skip to board cards**.
- Search receives real input focus. Cards use the existing named board heading, made programmatically focusable without adding a normal Tab stop.
- The project layout gains a named `main` landmark and a safe destination for loading/not-found/error pages.
- Focus navigation preserves query/deep-link state and does not escape an open modal.
- No outline control is removed from keyboard access, no DOM ordering is changed, and no database/API/drag behavior is changed.

The regression fixture retains **170 real tasks and more than 500 outline controls**. Search is reached with one Tab plus Enter; the first card title is reached within five Tabs using the second bypass. Empty-filter, narrow-view, forced-colors, fallback, and existing card activation paths are exercised. The full arrow-key/one-tab-stop outline requirement remains open and must be integrated with #511 rather than duplicated here. This PR must **not** say `Closes #482`.

## A better everyday workflow

Use one explicit flow:

1. **Capture:** keep the original human request and GitHub identity; record intended outcome and constraints.
2. **Triage:** identify duplicates, current implementation, and remaining acceptance before assigning priority. Separate epics, decisions, bugs, and executable tasks.
3. **Ready:** require a bounded plan, dependencies, an owner/claim, acceptance criteria, and an isolated branch/worktree. A high-priority item with unresolved decisions is not ready.
4. **Work:** record brief progress/blockers against the task. Show the distinction between claimed, actively executing, and stale claims; recent audit activity alone is not a heartbeat.
5. **Verify:** associate test results and artifacts with the exact commit and base. Run .NET, script, browser, API, native/stdio MCP, and in-app suites as applicable; preserve failures and environment conditions, not just the last green count.
6. **Review:** treat reported results, independently verified results, and human acceptance as separate states. A resolved thread or skipped bot check is not approval.
7. **Complete and reconcile:** merge only after acceptance, then reconcile GitHub and board using explicit field ownership. Keep historical execution records immutable; update the current task summary rather than layering contradictory status reports.

Practical queue policy: finish/rebase a small number of active PRs before opening another broad redesign. Keep one current head/base/gate record per PR. The existing no-GitHub-Actions decision can remain; its local execution gate still needs an exact-head, auditable result that maintainers actually enforce (#147).

## UI priorities from a user's perspective

| Priority | Improvement | Why / existing owner |
|---|---|---|
| Now | Keyboard bypass | Shipped in this branch; removes the long navigation barrier without changing familiar controls (#482, partial). |
| Next | Repair and finish Requests → Plan → Results | #511 already carries this workflow. Put the user's next action before optional AI controls; keep reported vs accepted results distinct. |
| Next | Readable high-density Task Table | #492: reuse board filters, task drawer, durable links and sorting contracts. Boards communicate flow; a table is better for scanning a large backlog. Do not mutate Kanban SortOrder merely by sorting the table. |
| Next | Visible explanations for unavailable actions | #113: disabled Generate Tasks / Assign to AI currently rely on hover-only “Coming soon.” Show a clear badge/explanation rather than imply they work. |
| Then | Active / Needs attention / All portfolio views | #421 → #422 → #423: derive one trustworthy state classifier before polishing discovery. Show blocked work, stale claims and review needs, not just project counts. |
| Then | Truthful Ideas paging | #424: make hidden records, per-status totals, and load-more behavior explicit. A clean screen must not imply that omitted work does not exist. |
| Then | Read-only sync inspector | #491: show source, last sync, field ownership and drift. Publishing/conflict resolution is a separate reviewed write contract, not just another button. |
| Later | Roving outline and dependency visualization | Integrate with #511 and #482's remaining navigation design; reuse dependency computation across #407/#411/#265. |

Do not begin with a theme rewrite or a second agent cockpit. The largest usability gain comes from trustworthy work state, quick navigation, readable task discovery, and a clear next action. Automated browser testing is not a substitute for the user's eventual acceptance of this workflow.

## Verification record

See [the keyboard-bypass test contract](../test-cases/482-keyboard-bypass/README.md) for reproducible commands, red/green evidence, scope exclusions, and the final run record. Raw logs and the full 182-issue classification appendix are retained in the review workspace; the PR description carries the final gate results and remaining limitations.
