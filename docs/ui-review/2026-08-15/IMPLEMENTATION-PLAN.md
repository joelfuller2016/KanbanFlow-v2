# KanbanFlow UI modernization implementation plan

- **Date:** 2026-08-15
- **Parent epic:** [#322 — Give KanbanFlow a visual language](https://github.com/joelfuller2016/KanbanFlow-v2/issues/322)
- **Planning baseline:** UI tree from origin/master at 3245d16; the screenshots were captured from
  fix/414-atomic-dependency-cycle at 652c60d, whose UI files were byte-identical to that baseline
- **Status:** Reviewed plan only; no application code has been changed
- **Evidence inventory:** docs/ui-review/2026-08-15/current-projects.png, current-board.png,
  current-task-detail.png, and proposed-board-redesign.png. These files were local/untracked during
  planning and are published by the Phase 0 evidence PR before their GitHub links are treated as
  evidence.

This is a proposed dated implementation-plan refresh for issue #322. It becomes the active sequence
only after the Phase 0 maintainer decision; until then, the issue body remains canonical. The
proposal does not erase the original evidence, correction comments, or issue history.

## 1. Outcome

Make KanbanFlow feel like a focused work surface rather than a collection of equally weighted
controls. A user should be able to answer three questions without hunting:

1. What work exists?
2. What is ready, blocked, claimed, or complete?
3. What is the next safe action?

The redesign keeps the current domain model, routes, workflow statuses, REST and MCP contracts, and
local-first architecture. It changes hierarchy, density, responsive behavior, accessibility,
interaction feedback, and test coverage.

### Measurable success criteria

- At 1440 by 1000, the first card area's top coordinate is no greater than 220 CSS pixels and the
  board work surface continues to the viewport bottom.
- The board, not utility chrome, owns most of the project page's usable height.
- At 1280 CSS pixels and wider, opening a task leaves at least 640 CSS pixels for the board, keeps
  at least two 220-pixel lanes fully visible, clips no toolbar control, and uses no modal scrim.
- Below 1280 CSS pixels, task detail is a true modal drawer with contained focus, an inert
  background, Escape handling, and opener-focus restoration.
- Search stays immediately available. Every other filter is no more than two actions away, and
  active filters remain visible as removable chips.
- Every pointer target is at least 24 by 24 CSS pixels; frequent actions target 36 by 36 or larger.
- Completed items remain readable: no card, outline row, badge, or control is de-emphasized by
  container-level opacity.
- Every state or hierarchy signal uses more than color alone.
- No existing data-testid or SortableJS identity contract changes without an explicit migration
  test.
- Automated functional, keyboard, accessibility, static-asset, and screenshot-verification gates
  pass at the supported viewport matrix.

## 2. Decisions and compatibility contract

These decisions reconcile the new mock with work that has already shipped.

### 2.1 Board height

The **board work surface** fills the remaining viewport, but each lane's card surface remains
content-height. Sticky lane headers and a neutral board canvas provide continuity without restoring
the large gray one-card void removed by PR #335. Empty and short lanes retain a clear, tested drop
target. Any later move to full-height lane surfaces requires a deliberate change to #322's geometry
contract and its regression tests.

### 2.2 AI tools and Requirements

The recommended refresh keeps AI tools and Requirements compact or collapsed by default, following
completed issue #58.
They become clear labeled utility actions such as “Requirements · 3” and “AI tools”; their existing
content opens on demand. Phase 0 must explicitly approve this divergence from the original #322 step
that proposed expanding AI by default, because the current screenshot shows that permanent utility
height competes with the work surface.

### 2.3 Task-detail behavior

- At 1280 CSS pixels and wider: a modeless complementary pane using
  clamp(32.5rem, 42vw, 35rem), or 520–560 CSS pixels.
- Below 1280 CSS pixels: modal end drawer.
- Below 576 CSS pixels: full-width modal sheet.

Desktop modeless behavior must not inherit aria-modal, a focus trap, a scrim, or an inert board.
It renders as an aside/complementary landmark. From 1280–1599 pixels the outline auto-collapses,
with a persistent reopen control, to preserve at least 640 pixels and two full lanes for the board.
At 1600 and wider the outline remains open only when the measured board width still meets that
contract. Mobile modal behavior must provide all four.

### 2.4 Home and portfolio handoff

Completed #281 remains the source of Home summary facts. The later-created portfolio epic #420 now
owns cross-project information architecture: #421 owns derived state/activity, #422 owns Home
lenses/cards/Empty disclosure/grid behavior, and #423 owns Home discovery and views. This plan
records current Home evidence and supplies shared visual/accessibility primitives; it does not
authorize a separate #322 Projects-grid implementation. #424 separately owns Ideas totals/paging,
and #425 remains decision-gated metadata.

### 2.5 Visual verification

The generated mock is a composition target, not pixel-perfect production output. DOM, accessibility,
geometry, persistence, and asset checks are hard gates. Review screenshots are a required human
approval artifact. Whole-page pixel matching is not a gate until deterministic fixtures and the
open UI harness reliability issues are fixed.

### 2.6 Frozen implementation contracts

Preserve:

- .board-column-cards[data-status] and its direct task children.
- .feature-tasks[data-feature-id], with task roots carrying data-task-id as direct children.
  Expanded non-task subtask siblings may coexist.
- Stable keyed task roots, the Sortable draggable selector, and the next-task identity scan that
  skips non-task siblings.
- The empty-column state as a sibling of the SortableJS container.
- WorkStatus names and order.
- Identity-anchored move semantics and persisted SortOrder.
- Current task-filter semantics, URL round-trip behavior, task deep links, and text reset token.
- Existing data-testid values across Home, Project, Ideas, Settings, dialogs, and detail.
- Shared board behavior used by /ideas unless a change is explicitly scoped to the task board;
  coordinate shared `KanbanBoard`/`KanbanColumn` changes with #424's paging work.

## 3. Existing issue ownership

Do not create another broad redesign epic. Issue #322 remains the umbrella and this plan is its
dated refresh.

| Area | Existing owner or history | Plan treatment |
| --- | --- | --- |
| Visual language, board density, outline hierarchy, target size, axe | #322 | Parent epic |
| AI/Requirements compaction | #58, completed | Preserve compact default; improve presentation only |
| Board filters | #112 / PR #362, completed | Preserve behavior; compact presentation |
| Project-card summaries | #281 / PR #377, completed | Preserve its progress, next-task, recency, claim, and count facts |
| Derived portfolio state and activity | #421, open under peer epic #420 | Consume its state/activity contract; do not derive a competing Home model under #322 |
| Home lenses, cards, Empty disclosure, and grid | #422, open under peer epic #420 | #422 owns implementation; #322 contributes visual primitives, evidence, and accessibility coordination |
| Home discovery and views | #423, open under peer epic #420 | #423 owns search/filter/sort/group and compact/card view state |
| Ideas totals and paging | #424, open under peer epic #420 | #424 owns retrieval; coordinate any shared `KanbanBoard`/`KanbanColumn` change and preserve task-board contracts |
| Portfolio focus/lifecycle/tags | #425, decision-gated under peer epic #420 | No implementation until its capability-specific gate passes; #322 adds no duplicate metadata |
| Board dead-space and column geometry | PR #335, completed | Preserve content-height card stacks |
| Empty-column interaction and tests | #119 and #342, open | Coordinate before lane changes |
| Task detail dependencies and related requirements | #109, open | Coordinate; activity timeline already shipped in #328 |
| Claimed-by card signal | #110, open | Use existing field; do not duplicate |
| Project summary header | #111, open | Integrate into compact header, not another permanent row |
| Disabled AI affordances | #113, open | Preserve ownership |
| Relative timestamps and UTC correctness | #116 and #306, open | Coordinate; no duplicate timestamp work |
| Truncated-name behavior | #117, open | Preserve and re-scope to remaining real gaps |
| Task-detail state/render/JS hardening | #167, open | Coordinate save, identity, error, and initialization work |
| Task-detail modal accessibility | #74, completed | Preserve as the narrow-mode contract; desktop uses a complementary region |
| Agent-specific presentation | #279, open | Peer epic; not absorbed here |
| Dependency graph visualization | #407, open | Out of scope for this visual pass |
| Confirm-modal keyboard behavior | #200, open | Separate; not the task-detail drawer |
| Theme token readiness | #346, open | Coordinate; no dark-mode toggle in this plan |
| Primary-color contrast policy | #370, open | Use its decision; do not reopen corrected #367 |
| UI harness reliability | #314, #351, #352, #353, open | Prerequisites for trustworthy visual automation |
| Documentation count drift | #345, open | Do not pin mutable counts without its chosen guard/convention |

The later-created #420–#425 portfolio issues are peer ownership, not #322 children. Do not create
another #322 Home/grid, Home-discovery, Ideas-paging, or portfolio-metadata implementation. The
remaining legitimate #322 child gaps are:

1. Desktop task-detail information architecture and explicit save feedback.
2. Post-#112 filter presentation compaction with unchanged filter semantics.
3. Deterministic screenshot capture and harness integration, sequenced with #314/#351/#352/#353.
   Axe remains execution work already owned by #322, even if delivered through a child issue.

### Phase dependency map

| Phase | Depends on / coordinates with | Blocking decision |
| --- | --- | --- |
| 0 evidence and contract | #322, #279 ownership audit | Approve dated refresh and publish evidence |
| 0A minimum harness trust | #314, #351, #352, #353 | Trustworthy summary/assets/artifacts before visual baselines |
| 1 docs/tokens/shell | #322, #346, #370 | Primary pairing and source-reference location |
| 2 Home evidence/handoff, coordination only | completed #281; #421/#422/#423; #200 remains separate | Link measurements to #422; no separate #322 Home code |
| 3 outline | #322 and completed outline accessibility contracts | Preserve drag DOM and non-hover access |
| 4 utilities/filters | completed #58/#112; #111/#113 | Compact header without semantic/filter change |
| 5 board/cards | #110, #119, #342, #424; #407 excluded | Empty-lane behavior first; shared board changes coordinate with Ideas paging |
| 6 detail | #109, #116/#306, #167, completed #74/#328 | Desktop complementary vs narrow modal contract |
| 7 performance/errors | #167 | Reproducible benchmark and privacy-safe telemetry |
| 8 rollout/docs | #322, #345 | Generated/current counts rather than stale pinned totals |

## 4. Scope and non-goals

### In scope

- Application shell, landmarks, heading hierarchy, and stable navigation.
- Home evidence plus reusable visual/accessibility primitives consumed by #420/#422/#423.
- Project outline hierarchy, selection, contextual actions, and responsive drawer behavior.
- Project header, Requirements/AI utility presentation, task count, and filters.
- Board work-surface geometry, lane headers, task-card hierarchy, completion treatment, and a
  non-drag Move to action.
- Responsive task detail, persistence feedback, focus behavior, and section hierarchy.
- Existing-data presentation: priority, blockers, claim state, subtasks, feature, dependencies,
  requirements, and activity where already loaded.
- Design tokens, forced-colors/reduced-motion behavior, performance safeguards, structured UI
  error logging, documentation, tests, and screenshot evidence.

### Out of scope

- Database migrations, new workflow statuses, or new domain fields.
- Dates, estimates, avatars, analytics, dashboards, or tags added only for the mock.
- REST or MCP schema changes.
- New AI capabilities or changes to task-ranking logic.
- Dependency arrows or graph visualization owned by #407.
- Authorization redesign.
- A frontend build system, Tailwind, Storybook, or a new component framework.
- Dark-mode selection. Tokens should be theme-ready, but #346 owns hard-coded light surfaces.
- Repair of pre-existing backend persistence concerns that are not required by the UI change.
- Portfolio state/activity (#421), Home lenses/cards/grid (#422), Home discovery/views (#423),
  Ideas totals/paging (#424), and decision-gated portfolio metadata (#425).

## 5. Delivery plan

The work should ship as reviewable dependent PRs. Accessibility and tests accompany every PR; they
are not deferred to the last phase.

### Phase 0 — Baseline, contract, and design approval

1. Resolve the intended review ref to one immutable SHA, then create the evidence PR from a fresh
   worktree detached at that SHA. Copy the reviewed local plan/images into it by explicit file path,
   record SHA-256 hashes before and after, and never assume an untracked canonical folder appears in
   a new worktree.
2. Record the exact base SHA and publish the review directory before treating its GitHub links as
   evidence.
3. Complete Phase 0A below before recording any geometry, accessibility, performance, or visual
   baseline.
4. Record the approved board-height, split-pane threshold, Home ownership handoff, palette,
   spacing, radius, and typography decisions.
5. Inventory every data-testid, SortableJS selector, JS interop entry point, and shared /ideas board
   selector touched by the proposed components.
6. Resolve issue ownership for #109/#110/#111/#113/#116 because #279 also references them,
   coordinate the task-detail save/error/identity work with #167, and record #420/#421/#422/#423/
   #424/#425 as peer portfolio ownership rather than #322 children.
7. Record that completed #74 remains the modal contract below 1280 pixels, while the proposed
   desktop layout intentionally adopts non-modal complementary-region semantics.
8. Resolve #370's primary-color policy or record an approved interim pairing before tokens freeze.
9. Resolve/sequence #119 and #342 before Phase 5 changes empty-lane presentation or interaction.

**Exit:** approved visual contract, exact baseline SHA, preserved-selector inventory, green baseline,
and no duplicate child issues.

### Phase 0A — Minimum trustworthy UI harness

This is the first implementation PR and precedes every visual PR.

1. With `UI_E2E_PORT` unset, empty, or `0`, let the child bind an OS-assigned loopback port and read
   the bound URL from its startup stream. A valid explicit override from `1` through `65535` wins and
   must bind exactly that port or fail immediately if unavailable. In both paths, prove the exact
   child PID owns the listener, use a fresh DB, and build/test from an isolated worktree with per-run
   `bin` and `obj` outputs.
2. Print an unconditional terminal PASS/FAIL summary even when setup or teardown fails (#314).
3. Discover every critical fingerprinted same-origin asset from the rendered DOM and validate
   Bootstrap CSS/JS, `app.css`, scoped `KanbanFlow.styles.css`, SortableJS, `kanbanflow.js`, and
   Blazor. Require expected MIME types, nonempty 200 bodies (or browser-proven loaded state for a
   cached 304), readable application/Bootstrap/scoped cssRules, known computed-style sentinels, and
   ready Bootstrap, Sortable, KanbanFlow, and Blazor runtimes before trusting behavior or geometry
   (#353).
4. Fail on pageerror, non-allowlisted console errors, and unexpected same-origin failures. The
   allowlist is explicit in source and limited to deliberate navigation aborts and SignalR teardown.
5. On every failure save complete server stdout/stderr and a failure manifest with
   `runStatus=failed`, the failure stage/reason, the immutable `plannedCaptures[]` matrix, every
   completed screenshot record, and an explicit `status=unavailable` record for each planned
   capture that could not be produced. Store ad-hoc failure screenshots, DOM snapshots, browser
   versions, and page diagnostics in a separate `failureArtifacts[]` collection so they cannot be
   mistaken for planned representative captures. The exact-head trusted plan defines four required
   name/kind records—viewport screenshot, DOM snapshot, browser version, and page diagnostics—and
   every failed run emits each exactly once as `captured` or `unavailable` with an explicit reason.
   When a browser/page exists, preserve those available diagnostics together with the exact SHA,
   port, DB path, and failing scenario.
6. Keep scenarios within one run serial. Independent bare runs may coexist only because each owns a
   unique port, DB, `BaseOutputPath`, and `BaseIntermediateOutputPath`; never share a mutable build
   output with another live run (#351/#352).
7. Add @axe-core/playwright now and make serious/critical results a hard gate with a reviewed,
   issue-linked exception list. Broader WCAG A/AA assertions remain explicit keyboard/DOM tests.
8. Freeze these commands:
   - npm run test:ui — functional, static-asset, console/error, and Axe hard gate.
   - npm run test:ui:shots -- --mode capture --out OUTPUT_DIR — deterministic human-review
     artifacts; non-zero on setup, asset, state, or capture failure.
   - npm run test:ui:shots -- --mode serve --out OUTPUT_DIR — seed and serve the deterministic
     manual fixture, printing project/task IDs, routes, PID, port, DB, and manifest.

**Exit:** the baseline commands fail reliably under deliberate asset, startup, assertion, and
teardown mutations. A teardown failure returns nonzero and preserves/reports its diagnostic path;
early visual PRs can use the same capture and Axe foundation.

### Phase 1 — Design docs, tokens, landmarks, and shell

Likely files: docs/design/ui/*, app.css, MainLayout.razor, ProjectViewLayout.razor, and NavMenu.razor.

1. Add design-tokens.md, wireframes.md, interactions.md, and accessibility.md so existing source
   references resolve. The chosen home is docs/design/ui; update every current source comment from
   design/ui/* to docs/design/ui/* in the same PR.
2. Define --kf-* tokens for canvas, surface, border, text, primary, status, priority, depth,
   spacing, radii, shadows, focus, header height, outline width, and detail width.
3. Derive compatible values from Bootstrap variables where practical; do not edit bundled
   Bootstrap.
4. Add a skip link, main-content landmark, one page-level h1, and a correct h1/h2/h3 hierarchy.
5. Stabilize global navigation across Projects, Project, Ideas, and Settings.
6. Namespace structural rules by route/component while migrating so /ideas does not inherit task
   board assumptions.

**Acceptance:**

- Skip link is first, visible on focus, and moves focus to main content.
- Every route has exactly one meaningful h1 and a main landmark.
- No horizontal document overflow at any supported viewport.
- Navigation does not jump or disappear between routes.
- Focus treatment is visible in normal and forced-color modes.
- No raw user content is added to telemetry/logs or unsafe unencoded attributes. Razor-encoded
  names remain allowed where required for accessible names and title text.

### Phase 2 — Home evidence and portfolio handoff (coordination only)

No Home runtime files are owned by this #322 phase.

1. Re-run completed #281's one-, three-, and twenty-project checks at the review viewports and link
   the measured evidence to #422.
2. Preserve #281's progress, next-task, recency, claim, and count contracts while #421 establishes
   the one derived state/activity model consumed by Home.
3. Let #422 own Home lenses, card/list presentation, Empty disclosure, post-create behavior, and any
   grid change; let #423 own Home search/filter/sort/group and view persistence.
4. Supply #322 tokens, landmarks, target-size rules, selector compatibility, and the shared Axe
   foundation without implementing a competing Home layout.
5. Keep #425 metadata decision-gated and separate from this evidence handoff.

**Acceptance:**

- One-, three-, and twenty-project evidence is linked to #422 with no separate #322 Home code.
- #421/#422/#423 are the sole owners of their state, presentation, and discovery contracts.
- Existing Home selectors and completed #281 summary facts remain compatibility inputs.
- Create, edit, delete, and menu regression evidence remains available. Confirmation-modal focus
  trapping, Escape, and exact restoration remain owned by open #200.

### Phase 3 — Project outline

Likely files: ProjectOutline.razor, project-scoped CSS, and outline accessibility JS.

1. Replace the stack of equally strong cards with semantic nested lists, indentation, connector
   rails, readable type hierarchy, and one selected-row state.
2. Keep feature/task/subtask icons and text as non-color hierarchy channels.
3. Retain disclosure state and accessible progress text.
4. Share SelectedTaskId between board and outline.
5. Move add-child, edit, and destructive actions into contextual controls that remain visible on
   keyboard focus and touch. Hover cannot be the only path.
6. Preserve direct-child drag structure and identity-anchored callbacks.
7. Remove inline completion opacity; use a checkmark/Done text, quiet surface, and title treatment.

**Acceptance:**

- A user can identify the selected item and its parent without color.
- Screen-reader list hierarchy is meaningful without an incomplete ARIA tree pattern.
- Expand/collapse, edit, completion, add child, outline drag, and focus restoration work.
- At narrow widths, the closed outline is inert and absent from the accessibility tree.
- All visible actions have an accessible name that says what will be created or changed.

### Phase 4 — Compact project header, utilities, and filters

Likely files: ProjectView.razor, BoardFilterBar.razor, and small extracted utility components.

1. Extract Requirements and AI presentation from ProjectView into focused components without
   changing their services or API calls.
2. Use compact Requirements · N and AI tools actions in the project header.
3. Put the task count with the Board heading.
4. Keep search visible. Put assignee, claimed-by, priorities, and Hide done in a labeled Filters
   disclosure.
5. Show active values as removable chips and retain Clear all.
6. Keep disclosure contents mounted when closed so local text buffers are not remounted.
7. Preserve immediate filtering, debounced URL writes, task deep-link query parameters, reset
   tokens, and one polite summary live region.
8. Route every externally initiated text-filter removal through one method that updates Filter and
   increments TextResetToken together. Removing Search, Assignee, or Claimed-by chips must update
   the mounted input, board result, summary, and URL atomically.

**Acceptance:**

- Collapsed utilities do not consume a permanent board row.
- Search is one action away; all other filters are at most two.
- Fast typing, mid-string caret position, reset, Back navigation, URL sharing/reload, and task deep
  links remain correct.
- Filtered drag persists the intended identity anchor.
- Active chips are keyboard removable and their changes are announced once.
- Separate Search, Assignee, and Claimed-by chip tests prove DOM value, resolved cards, summary, and
  URL agree after removal.

### Phase 5 — Board lanes, cards, and movement

Likely files: KanbanBoard.razor, KanbanColumn.razor, KanbanCard.razor, task-board CSS, and
kanbanflow.js.

**Prerequisites:** the #119 inline empty-column decision and #342 regression guard are resolved or
explicitly incorporated before changing the lane structure.

1. Make the board canvas fill the remaining workspace while preserving content-height card stacks.
2. Use two explicit lane layers: a transparent full-workspace track containing the sticky header
   and drop affordance, plus an inner content-height card surface. Long inner surfaces may scroll
   within the bounded workspace; short surfaces must not stretch or paint a tall gray block.
3. Keep board horizontal scrolling contained.
4. Make task title dominant and feature a readable, non-italic secondary label.
5. Use consistent priority/status tokens and two-channel signals.
6. Show conditional metadata only when supported: blocked state, claim state, and subtask progress.
7. Add a keyboard/touch Move to menu through the existing task-move path; drag remains the pointer
   shortcut.
8. Make completion explicit and undoable without using container opacity.
9. Set SortableJS animation to zero when prefers-reduced-motion is active.
10. Keep /ideas terminal, collapsed, inert, and empty-state behavior isolated from task-board
    styles. Coordinate shared component changes with #424, which owns Ideas totals/paging and may
    add expansion callbacks without changing task-board behavior.
11. Add a DOM-contract E2E assertion after the semantic outline refactor: direct task roots retain
    data-task-id, expanded non-task siblings may coexist, and the draggable/next-identity selectors
    still skip those siblings.

**Acceptance:**

- WorkStatus order, task identity, persisted SortOrder, and @key behavior are unchanged.
- Drag within and across columns persists after reload, including under active filters.
- Moving one item never visually or persistently moves another item.
- Keyboard/touch users can perform the same status move without dragging.
- An empty column still accepts a drop and its inline add action works.
- Completed content, badges, and controls retain AA contrast.
- /ideas functional and geometry scenarios remain green.

### Phase 6 — Responsive task detail and persistence feedback

Likely files: TaskDetailPanel.razor, ProjectView.razor, EditableMarkdown.razor,
ActivityTimeline.razor, scoped CSS, and detail focus JS.

1. Place detail inside the project workspace layout.
2. Render an aside/complementary modeless pane at 1280 pixels and wider using
   clamp(32.5rem, 42vw, 35rem); reflow the board without a scrim.
3. Render an accessible modal drawer below 1280 pixels and a full-width sheet below 576 pixels.
   The narrow contract preserves completed #74; the desktop contract deliberately changes the
   region to non-modal and must be documented and tested separately.
4. Use a sticky header and compact metadata grid for Feature, Status, Priority, Assignee, and claim
   state.
5. Show dependencies as relationship rows, not an input-like empty box.
6. Keep one EditableMarkdown and one ActivityTimeline implementation; split the large panel into
   presentation sections without copying behavior.
7. Progressively disclose long History and deeply nested subtask lists with correctly named
   controls. Dependency state and acceptance-criteria guidance remain visible even when empty so
   their meaning and next action are not hidden.
8. Add one coordinated Saving…, Saved, and recoverable Error state with polite announcement.
9. Serialize or version overlapping saves so a slower reload cannot overwrite a later edit.
10. Coordinate parameter mutation, component identity, transient-error, fire-and-forget, and JS
    initialization changes with open #167 instead of duplicating that work.

**Acceptance:**

- Desktop detail has no aria-modal, scrim, inert board, or focus trap.
- At both 1280 by 720 and 1440 by 1000, it leaves at least 640 pixels for the board and two fully
  visible 220-pixel lanes; the outline auto-collapses with a visible reopen action. At 1600 and
  wider, keeping the outline open must still satisfy the same measurements. No toolbar clips and
  the document does not overflow horizontally.
- Mobile detail has dialog semantics, inert background, Tab/Shift+Tab containment, Escape close,
  and exact-opener focus restoration.
- Escape inside a Markdown editor restores the seeded value, suppresses the blur commit caused by
  leaving edit mode, and returns focus to that editor's read control. Only a subsequent Escape, when
  no inner editor owns the key, may close the drawer and restore focus to its exact opener.
- The first desktop pane viewport shows metadata, dependency state, description, and the start of
  acceptance criteria without a large blank region.
- Deep-linked tasks open once and preserve filter query state.
- Rapid edits do not display stale values; a failed edit leaves recoverable content and visible
  feedback.
- Activity work already delivered by #328 is reused, not rebuilt under #109.

### Phase 7 — Performance, error handling, and observability

Apply these constraints during every phase and finish them in one focused review.

1. Precompute ordered requirements and feature/task/subtask lookup tables once per project load.
2. Keep filter resolution and expensive list construction out of render paths.
3. Do not widen the project query or add per-card queries without a measured projection plan.
4. Record query counts for normal and stress fixtures; prevent N+1 card metadata reads.
5. Avoid reinitializing SortableJS on unrelated renders. Remove fixed timing delays only after a
   render-safe replacement is proven.
6. Add structured ILogger events for load, save, AI utility, drag/reorder, JS initialization, and
   detail failures.
7. Log operation, route, project/task IDs, duration, and category only—never names, descriptions,
   searches, comments, Markdown, tokens, or credentials.
8. Treat navigation cancellation/disconnection as expected; unexpected failures need both
   user-visible feedback and a privacy-safe log.

**Acceptance:**

- A pure render triggers no database query.
- No per-card or per-outline-row query appears in normal or stress fixtures.
- The reproducible benchmark in section 6 records p50/p95 board readiness and filter latency. Its
  p50 does not regress more than 10 percent and p95 more than 20 percent from the same-machine
  Phase 0 baseline without documented approval.
- Long lanes scroll inside the workspace; the document and board do not gain competing vertical
  scrollbars.
- Every unexpected user-initiated asynchronous failure is visible and diagnosable without exposing
  content. Navigation cancellation, renderer disposal, and expected disconnect teardown remain
  quiet.

### Phase 8 — Integrated accessibility, visual evidence, documentation, and rollout

1. Add the automated and manual gates in sections 6–8 below.
2. Capture the required before/after screenshot matrix from the deterministic isolated fixture.
3. Update the UI runbook and known harness limitations. Coordinate #345 before changing mutable
   README counts; prefer generated counts or commands over copied point-in-time totals.
4. Attach representative desktop, tablet, mobile, keyboard-focus, and error-state screenshots to
   every visual PR.
5. Ship one concern per PR in dependency order and leave compatibility selectors until the final
   cleanup.

**Exit:** all functional/a11y/visual gates pass, issue links are current, documentation matches the
actual commands, and rollback requires only an ordinary PR revert—no schema or data repair.

## 6. Automated test plan

### 6.0 PowerShell execution and environment contract

Every PowerShell terminal used by this runbook must start with this prelude. Without native-command
error propagation, a failing git/npm/npx/dotnet command followed by cleanup can make the outer
PowerShell process exit successfully.

~~~powershell
if ($PSVersionTable.PSVersion -lt [version]'7.3') {
    throw 'This runbook requires PowerShell 7.3 or newer.'
}
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Save-ProcessEnvironment([string[]]$Names) {
    $snapshot = @{}
    foreach ($name in $Names) {
        $snapshot[$name] = [Environment]::GetEnvironmentVariable(
            $name,
            [EnvironmentVariableTarget]::Process
        )
    }
    return $snapshot
}

function Restore-ProcessEnvironment([hashtable]$Snapshot) {
    foreach ($entry in $Snapshot.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            [EnvironmentVariableTarget]::Process
        )
    }
}

function Wait-TaskBounded {
    param(
        [Parameter(Mandatory)][Threading.Tasks.Task]$Task,
        [ValidateRange(1000, 60000)][int]$TimeoutMilliseconds
    )

    $timeoutTask = [Threading.Tasks.Task]::Delay($TimeoutMilliseconds)
    $winner = [Threading.Tasks.Task]::WhenAny(
        [Threading.Tasks.Task[]]@($Task, $timeoutTask)
    ).GetAwaiter().GetResult()
    if (-not [object]::ReferenceEquals($winner, $Task)) {
        return [pscustomobject]@{ Status = 'timeout'; Error = $null }
    }
    try {
        $Task.GetAwaiter().GetResult()
        return [pscustomobject]@{ Status = 'completed'; Error = $null }
    }
    catch {
        return [pscustomobject]@{ Status = 'faulted'; Error = $_.Exception.Message }
    }
}

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(1000, 600000)][int]$TimeoutMilliseconds = 120000,
        [ValidateRange(1000, 60000)][int]$StreamDrainTimeoutMilliseconds = 10000
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Failed to start git.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $streamDrainTask = [Threading.Tasks.Task]::WhenAll(
            [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
        )
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $killError = $null
            try {
                $process.Kill($true)
            }
            catch {
                $killError = $_.Exception.Message
            }
            $exitedAfterKill = $process.WaitForExit(10000)
            $drainResult = Wait-TaskBounded -Task $streamDrainTask `
                -TimeoutMilliseconds $StreamDrainTimeoutMilliseconds
            $drainDetail = switch ($drainResult.Status) {
                'completed' { '' }
                'timeout' {
                    " Redirected stream drain exceeded $StreamDrainTimeoutMilliseconds ms."
                }
                default { " Redirected stream drain failed: $($drainResult.Error)" }
            }
            $killDetail = if ($null -eq $killError) { '' } else { " Kill reported: $killError" }
            if (-not $exitedAfterKill) {
                throw "git timed out after $TimeoutMilliseconds ms and the Git process did not " +
                    "exit within the 10000 ms termination grace period.$killDetail$drainDetail"
            }
            $timeoutStderr = if ($drainResult.Status -eq 'completed') {
                $stderrTask.GetAwaiter().GetResult().Trim()
            }
            else {
                ''
            }
            $stderrDetail = if ([string]::IsNullOrWhiteSpace($timeoutStderr)) { '' } else {
                " stderr: $timeoutStderr"
            }
            throw "git timed out after $TimeoutMilliseconds ms.$killDetail$drainDetail$stderrDetail"
        }
        $drainResult = Wait-TaskBounded -Task $streamDrainTask `
            -TimeoutMilliseconds $StreamDrainTimeoutMilliseconds
        if ($drainResult.Status -eq 'timeout') {
            throw "git exited, but redirected streams did not close within " +
                "$StreamDrainTimeoutMilliseconds ms."
        }
        if ($drainResult.Status -eq 'faulted') {
            throw "git redirected stream drain failed: $($drainResult.Error)"
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "git failed with exit $($process.ExitCode): $($stderr.Trim())"
        }
        return $stdout
    }
    finally {
        $process.Dispose()
    }
}

function Get-RegisteredWorktreePaths {
    param([Parameter(Mandatory)][string]$Repository)

    $raw = Invoke-GitChecked -Arguments @(
        '-C', $Repository, 'worktree', 'list', '--porcelain', '-z'
    )
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($field in $raw.Split([char]0)) {
        if ($field.StartsWith('worktree ', [StringComparison]::Ordinal)) {
            $candidate = $field.Substring('worktree '.Length).Trim()
            [void]$paths.Add([IO.Path]::GetFullPath($candidate).TrimEnd('\'))
        }
    }
    return $paths.ToArray()
}
~~~

The checked Git helper accepts only a 1,000–600,000 ms process timeout and a
1,000–60,000 ms stream-drain timeout; they default to 120,000 ms and 10,000 ms. On process timeout
it terminates the full process tree, allows a bounded 10,000 ms exit grace period, performs only a
bounded combined-stream
drain, preserves the process-timeout error as primary, and disposes the process in all cases. Phase
0A tests cover invalid bounds; success, nonzero exit, and startup failure; normal and timeout-path
stream-drain timeout/fault; timeout/recursive-kill; post-kill grace failure; timeout-error precedence;
and disposal.
Every executable Git command in the PowerShell runbook goes through this helper; remote fetches use
its bounded 600,000 ms override. A static AST test fails if a `git` or `git.exe` command invocation is
added to any PowerShell fence, so setup, identity validation, publication, and cleanup cannot bypass
the timeout/process-tree/stream-drain contract.

Snapshot every process variable before changing it and restore it in finally. Required names across
the runbook are KANBANFLOW_DB_PATH, ASPNETCORE_ENVIRONMENT, KANBANFLOW_API_URL, UI_E2E_PORT, and
SKIP_BUILD. Removing a variable is correct only when its prior value was null.

### 6.1 Baseline and hard gates

Run from the canonical checkout, derive its repository path, and create a fresh worktree at the
literal review SHA. Replace `$reviewSha` for each PR and record actual counts rather than copying
stale README numbers.

~~~powershell
$baselineEnv = Save-ProcessEnvironment @(
    'KANBANFLOW_DB_PATH',
    'ASPNETCORE_ENVIRONMENT',
    'KANBANFLOW_API_URL',
    'UI_E2E_PORT',
    'SKIP_BUILD'
)
$repo = [IO.Path]::GetFullPath(
    (Invoke-GitChecked -Arguments @('rev-parse', '--show-toplevel')).Trim()
).TrimEnd('\')
$reviewSha = '3245d16c6a937d051fff8e7e111d1639b6364f16'
$expectedHead = $reviewSha
$work = Join-Path $env:TEMP ('kf-ui-verify-' + [guid]::NewGuid().ToString('N'))
$callerLocation = (Get-Location).Path
$worktreeCreated = $false
$preserveWorktree = $false
try {
    [void](Invoke-GitChecked -Arguments @(
        '-C', $repo, 'fetch', '--prune', 'origin'
    ) -TimeoutMilliseconds 600000)
    $reviewObject = "$reviewSha^{commit}"
    [void](Invoke-GitChecked -Arguments @('-C', $repo, 'cat-file', '-e', $reviewObject))
    [void](Invoke-GitChecked -Arguments @(
        '-C', $repo, 'worktree', 'add', '--detach', $work, $reviewSha
    ))
    $worktreeCreated = $true
    $actualHead = (Invoke-GitChecked -Arguments @('-C', $work, 'rev-parse', 'HEAD')).Trim()
    if ($actualHead -ne $reviewSha) {
        throw "Expected review SHA $reviewSha, got $actualHead."
    }
    Set-Location $work
    $actualRoot = [IO.Path]::GetFullPath(
        (Invoke-GitChecked -Arguments @('rev-parse', '--show-toplevel')).Trim()
    ).TrimEnd('\')
    $expectedRoot = [IO.Path]::GetFullPath($work).TrimEnd('\')
    if (-not $actualRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The verification worktree identity does not match.'
    }

    dotnet --version
    node --version
    pwsh --version
    npm ci
    npx playwright install chromium

    $savedNativePreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $verifyOutput = @(& pwsh -NoProfile -File .\scripts\verify.ps1 2>&1)
        $verifyExit = $LASTEXITCODE
    } finally {
        $PSNativeCommandUseErrorActionPreference = $savedNativePreference
    }
    $verifyOutput | ForEach-Object { Write-Host $_ }
    $verifyLines = @($verifyOutput | ForEach-Object { $_.ToString().TrimEnd() })
    if ($verifyLines.Count -eq 0 -or
        $verifyLines[-1] -ne 'Build + tests passed. Safe to push.') {
        throw "verify.ps1 did not report hard success (exit $verifyExit)."
    }
    if ($verifyExit -ne 0) {
        Write-Warning "verify.ps1 hard gates passed but its advisory exit was $verifyExit."
    }
    npm run test:scripts

    # Keep the UI/Axe gate inside this exact-SHA worktree lifecycle.
    [Environment]::SetEnvironmentVariable(
        'UI_E2E_PORT',
        $null,
        [EnvironmentVariableTarget]::Process
    )
    [Environment]::SetEnvironmentVariable(
        'SKIP_BUILD',
        $null,
        [EnvironmentVariableTarget]::Process
    )
    npm run test:ui

    # When the MCP wrapper is required, invoke it here before API smoke.
    # pwsh -NoProfile -File .\scripts\smoke-mcp-isolated.ps1 -ExpectedHead $expectedHead
    pwsh -NoProfile -File .\scripts\smoke-api.ps1
} catch {
    if ($worktreeCreated) {
        $preserveWorktree = $true
        Write-Warning "Verification failed; detached diagnostic worktree preserved at $work"
    }
    throw
} finally {
    try {
        Set-Location $repo
        if ($worktreeCreated -and -not $preserveWorktree) {
            try {
                $resolvedWork = [IO.Path]::GetFullPath($work).TrimEnd('\')
                $registeredBefore = @(Get-RegisteredWorktreePaths -Repository $repo)
                $exactBefore = @($registeredBefore | Where-Object {
                    $_.Equals($resolvedWork, [StringComparison]::OrdinalIgnoreCase)
                })
                if ($exactBefore.Count -ne 1) {
                    throw 'The verification worktree is not registered exactly once.'
                }
                [void](Invoke-GitChecked -Arguments @(
                    '-C', $repo, 'worktree', 'remove', $resolvedWork
                ))
                $registeredAfter = @(Get-RegisteredWorktreePaths -Repository $repo)
                $exactAfter = @($registeredAfter | Where-Object {
                    $_.Equals($resolvedWork, [StringComparison]::OrdinalIgnoreCase)
                })
                if ($exactAfter.Count -ne 0 -or (Test-Path -LiteralPath $resolvedWork)) {
                    throw "Worktree remains registered or present after removal: $resolvedWork"
                }
            } catch {
                Write-Warning "Worktree cleanup failed; inspect the registered path: $work"
                throw
            }
        }
    } finally {
        Restore-ProcessEnvironment $baselineEnv
        Set-Location $callerLocation
    }
}
~~~

Require the verify script's final success line and zero hard failures. Formatting is advisory. The
current script-unit set includes a live GitHub-backed case that may report SKIP when offline; record
SKIP separately from PASS. At the published evidence base, `smoke-api.ps1` reaches the pre-existing
[#338](https://github.com/joelfuller2016/KanbanFlow-v2/issues/338) failure because recall accepts
`k=0`. Report that command as an issue-linked known failure, not PASS; it continues to block the
Phase 0 green-baseline exit until #338 is fixed and this exact command succeeds. Do not mask any
other smoke failure under that exception.

### 6.2 UI E2E

Run scenarios within one UI E2E invocation serially. By default, the Phase 0A runner must launch its
child on an OS-assigned loopback port, read the bound address from that child's startup stream, and
prove the exact child PID owns the listener before navigation. A caller-side probe followed by
releasing the port is not sufficient. Each bare run builds into unique `BaseOutputPath` and
`BaseIntermediateOutputPath` directories and launches that exact DLL.

Section 6.1 invokes the frozen default `npm run test:ui` command inside its exact-SHA worktree `try`,
after the verified build/script tests and before API smoke. Do not invoke the gate from the caller
checkout after that outer `finally` removes the worktree. Dynamic port/build allocation belongs
inside the runner. A valid explicit `UI_E2E_PORT=1..65535` must instead bind exactly that port and
fail before tests if it is invalid, busy, or owned by another PID. Test both automatic and explicit
paths. `SKIP_BUILD=1` is only an exclusive immutable-prebuild shortcut immediately after building
the same worktree/SHA; it does not prove #352. Prove build isolation with two concurrent bare runs,
each using distinct output/intermediate directories, ports, and DBs while scenarios inside each run
remain serial. The pre-Phase 0A script at the evidence base still defaults to a fixed port and cannot
satisfy this contract, so its best-effort unique-port result is evidence only and does not close
#351. At evidence base `3245d16c6a937d051fff8e7e111d1639b6364f16`, the script has 48 named checks:
one helper guard and 47 browser scenarios. Require the final summary and compare the count to the
baseline captured in Phase 0. If the harness dies without a summary, treat the run as failed; issue
#314 owns making that unmistakable.

Required additions:

- Static-asset trust gate: discover fingerprinted same-origin URLs from rendered DOM; validate
  expected MIME types and nonempty 200 bodies or browser-proven loaded state for cached 304s;
  require readable application/Bootstrap/scoped cssRules and known computed sentinels; and prove
  Bootstrap, Sortable, KanbanFlow, and Blazor runtime readiness. Coordinate #353.
- Fail on pageerror, unexpected console error, failed same-origin request, or unhandled response.
- A failure manifest plus complete app stdout/stderr on every failure. Retain the immutable
  `plannedCaptures[]` matrix, preserve completed screenshot records, and mark every unproduced
  planned capture explicitly unavailable with a reason. Store any ad-hoc failure screenshot, DOM,
  browser version, and page diagnostics in `failureArtifacts[]`, separate from the one-to-one
  planned screenshot results. Require exactly one captured-or-unavailable record for each trusted
  `viewport-screenshot`, `dom-snapshot`, `browser-version`, and `page-diagnostics` contract,
  together with the exact SHA/port/DB path.
- Manifest-contract tests cover a complete success; a one-record manifest truncated below the
  source-controlled capture plan; wrong plan path, hash, manifest schema, trusted-plan schema, or
  required failure-artifact name/kind/storage matrix;
  missing, unexpected, null, and case-only duplicate entries; JSON objects where arrays are required;
  nested-array records; boolean, string, or fractional values where JSON integers are required;
  route, fixture-state, and viewport mismatches; incomplete captured metadata; a missing planned
  source or source hash/byte/raster mismatch; a renamed JPEG or other non-PNG payload with a `.png`
  filename; an unavailable result in a success manifest; a
  complete pre-launch failure with one unavailable record and reason for each of the four trusted
  diagnostics; omission or duplication of any required diagnostic name/kind; a mid-run failure that
  preserves captured representative and diagnostic records while marking the remainder unavailable;
  publication rejection for running/failed manifests; and a
  clean serve shutdown that becomes successful only after the complete trusted plan validates.
  Raster dimensions may legitimately differ from viewport dimensions for a full-page capture.
- Publication-contract tests prove the index names exactly the approved PNG subset and frozen
  full-run-manifest hash; staged hash/byte/raster corruption, a source swap, a staged reparse point,
  and a forced pre-rename copy failure leave no final bundle; a destination collision creates no
  new/partial bundle and leaves the pre-existing destination byte-for-byte unchanged; an owned
  staging failure removes only known files; and an atomic rename leaves the complete bundle while a
  later worktree-cleanup failure reports the exact worktree without removing valid evidence.
- Geometry assertions for the board top, contained scrolling, content-height short lanes, minimum
  target size, split-pane width, no desktop scrim, and no document horizontal overflow.
- Phase 0A functional scenarios for current application behavior and `/ideas` regressions. Filter
  disclosure/chips, keyboard Move to, outline selection, desktop split pane, mobile drawer, save
  states, and other future behavior are added by the visual PR that first introduces each state.
- Per-scenario isolation or a documented fixture reset so one failure cannot contaminate later
  scenarios.
- A closed-but-mounted Filters disclosure is inert/non-focusable and absent from the accessibility
  tree while retaining its local input buffer.
- The console/request gate uses a source-controlled allowlist for deliberate navigation aborts and
  SignalR teardown; every allowlisted event is counted and printed.

### 6.3 Unit/component/service tests

Add focused tests close to the behavior:

- Token/class/state helpers: every priority/status/depth has text or glyph in addition to color.
- View-model projection: blockers, claim state, subtask progress, counts, and ordering are computed
  once with no N+1 reads.
- Filter state: popover presentation cannot change parsing, serialization, alias handling,
  debouncing, reset-token behavior, or task query preservation.
- Removing Search, Assignee, and Claimed-by chips each increments TextResetToken and synchronizes
  input DOM value, filter result, live summary, and URL.
- Detail mutation coordinator: Saving → Saved, failed save → Error, rapid later edit wins, and stale
  reload cannot overwrite the draft.
- Responsive state adapter: desktop modeless and narrow modal state transitions, including media
  query setup/disposal.
- Accessible names for progress, menus, disclosure buttons, move actions, and destructive actions.
- Existing SSR structure tests updated for main, h1, and skip-link semantics without replacing
  browser behavior tests.
- A real SQLite test with an EF command interceptor asserts bounded project-load query count and
  zero database commands during pure rerender/filter presentation.
- Markdown security regressions keep script/event handlers, unsafe URL schemes, and layout-escaping
  style properties inert while benign Markdown remains visible.
- Markdown Escape restores the seeded value, suppresses the blur commit triggered by leaving edit
  mode, returns focus to the editor's read control, and lets only a later unowned Escape close the
  responsive detail surface.

### 6.4 Accessibility automation

Phase 0A adds @axe-core/playwright as a dev dependency. Run Axe only after each route/state has
settled. Fail on serious or critical violations and push failures into the harness's real
results/exit path; every exception must link an existing issue and have a removal condition.

Scan routes and record IDs emitted by the generated fixture manifest; never assume project ID `1`:

- the manifest Home route
- the manifest's normal project-board route
- that project route with filters open and active chips once their owning PR lands
- that project route with task detail open in desktop and mobile modes once Phase 6 lands
- the manifest's canonical Ideas route, scanned empty and then populated through its scripted
  fixture-reset/state steps (or an explicitly approved Development-only fixture seam)
- the manifest Settings route
- the manifest Error route
- create/edit/delete confirmation dialogs for semantic/Axe regression only. Keyboard trap/Escape
  remediation remains #200 unless that issue is explicitly pulled into this epic.

Automated keyboard flows:

- Skip link → main.
- Global navigation.
- Outline open/close, disclosure, contextual actions, and restoration.
- Search, Filters open/close, chip removal, and Clear all.
- Card Move to without drag.
- Desktop detail navigation without trapping.
- Mobile detail Tab/Shift+Tab containment, Escape, and opener restoration.
- Markdown Enter/Space/Escape behavior and link activation.
- Confirmation-dialog controls and existing keyboard behavior without claiming #200 complete.

### 6.5 API, MCP, and generic-board regression

The UI plan changes no public contract, but the complete release candidate still runs:

- scripts/smoke-api.ps1 against its self-managed temporary DB.
- The existing /ideas E2E suite because the board/CSS/JS code is shared.
- MCP smoke only against a fresh disposable app, never a valued database.

Add a scripts/smoke-mcp-isolated.ps1 wrapper so the safe path is one command in one verified
worktree:

When MCP smoke is required, invoke this command at the marked point inside the section 6.1 `try`,
before API smoke and before the outer worktree cleanup:

~~~powershell
pwsh -NoProfile -File .\scripts\smoke-mcp-isolated.ps1 -ExpectedHead $expectedHead
~~~

The wrapper is part of the test-foundation work and must:

1. Enforce the section 6.0 native-command policy.
2. Verify show-toplevel and HEAD equal the caller's exact worktree/ExpectedHead.
3. Snapshot and restore KANBANFLOW_DB_PATH, ASPNETCORE_ENVIRONMENT, and KANBANFLOW_API_URL.
4. Create a GUID-named temp directory and allocate a unique loopback port.
5. Launch the already-built DLL as a hidden background process with redirected stdout/stderr.
6. Verify that exact PID owns the listener and that startup diagnostics report source=env,
   existing=false, and the exact temp DB path before any mutation.
7. Set KANBANFLOW_API_URL, run health, and run npm run test:mcp in the same verified worktree.
8. Stop and wait for the confirmed PID, verify the listener is gone, validate the resolved temp
   path, delete it, restore the environment, and propagate the original failure.

The MCP and in-app suites mutate their target and clean up best-effort; the wrapper must refuse a DB
outside its validated temporary directory and must never point at the user's normal board.

### 6.6 Reproducible UI performance and query budget

Add npm run test:ui:perf. It uses a Release build, the deterministic stress fixture, and an isolated
DB/port. The fixture contains five statuses, at least twenty tasks per status, long labels, nested
subtasks, blockers, claims, and active filters.

Definitions:

- **Board readiness:** navigation starts immediately before page.goto; readiness ends only after the
  board and summary are rendered, fonts are ready, the expected task identities are present, and a
  new data-sortable-ready marker confirms JS initialization.
- **Filter latency:** starts when the input event is dispatched and ends after the expected summary
  text/card identity set is present plus two animation frames.
- **Query budget:** a real SQLite/EF command-interceptor test records project-load commands and
  asserts zero commands during a pure rerender. It is not inferred from browser timing.

Method:

1. Same machine, exact base/head SHA, Release configuration, browser version, fixture, viewport,
   locale/time zone, power mode, and idle-load preflight.
2. Three discarded warm-up runs, then eleven measured runs per metric.
3. Save raw samples, p50, p95, environment, base SHA, and head SHA as JSON.
4. Gate p50 at no more than 10 percent and p95 at no more than 20 percent slower than baseline.
5. If either threshold fails, return the machine to idle and rerun once. A repeated failure needs a
   documented reviewer decision; a one-off failure is retained as noise evidence, not discarded.

## 7. Screenshot verification protocol

### 7.1 Deterministic fixture

Create a dedicated scripts/ui-screenshots.mjs rather than adding screenshots to arbitrary points in
the stateful UI E2E sequence.

The script must:

1. Build or require an explicitly matching prebuilt SHA.
2. Choose a unique loopback port and temporary KANBANFLOW_DB_PATH.
3. Start in Development.
4. Seed a fresh, named fixture through existing APIs: empty, normal, and stress projects plus
   scripted sequential reset/populate steps for the single canonical `/ideas` route.
5. Use stable names/content and mask volatile timestamps or IDs.
6. Disable animation/caret blink for capture while separately testing reduced motion.
7. Never use networkidle as readiness because Blazor Server keeps SignalR alive. Wait for health, a
   proven interactive action/marker, target data-testid, document.fonts.ready, the relevant API or
   state predicate, and two animation frames.
8. Assert CSS/JS assets are loaded before capturing.
9. Save SHA, browser/OS, locale, time zone, deviceScaleFactor, color scheme, font scale,
   reduced-motion/forced-colors setting, PID, port, and DB path in a manifest. Add a
   source-controlled `scripts/ui-screenshot-plan.json` at the exact tested head with `schemaVersion`,
   a nonempty `captures[]` matrix, and a `requiredFailureArtifacts[]` matrix. The latter fixes these
   name/kind/storage contracts: `viewport-screenshot`/`viewportScreenshot`/file,
   `dom-snapshot`/`domSnapshot`/file, `browser-version`/`browserVersion`/inline, and
   `page-diagnostics`/`pageDiagnostics`/file. Each trusted capture record has a basename-only unique `.png`
   `filename`, a root-relative `routeTemplate` whose only substitutions are positive JSON-integer
   `{fixture:NAME}` IDs, nonempty `fixtureState`, and positive `viewport.width` and
   `viewport.height`; schema versions and all dimensions/counts are native JSON integers, never
   booleans, numeric strings, or fractions. The manifest records the fixed plan path, SHA-256, and
   schema version, plus a
   resolved `plannedCaptures[]` array derived from that trusted file. A failure before route IDs can
   be resolved may leave `route=null` only with a nonempty `routeUnavailableReason`; a successful
   manifest requires every route resolved. The manifest also contains exactly one `screenshots[]`
   result for every independently trusted capture and no unexpected results. Each result repeats
   the resolved identity fields and adds
   `status`. A `status=captured` result includes SHA-256, byte count, and raster width/height computed
   from the final on-disk bytes. Reject null records, path-bearing or case-insensitively duplicate
   filenames, wrong JSON collection/record shapes, and any route, fixture-state, or viewport
   mismatch among the trusted plan, resolved plan, and result arrays. On success,
   set `runStatus=success` and write the manifest only after every planned result is captured and
   complete. On failure, still write `runStatus=failed`, the failure stage/reason, all completed
   `captured` results, and exactly one `status=unavailable` result with an explicit reason and null
   hash/byte/raster-dimension fields for every remaining planned capture. Put browser/page failure
   diagnostics in a separate `failureArtifacts[]` collection. Emit exactly one record for every
   trusted required name/kind/storage contract; optional diagnostics are additive and cannot reuse a
   required name. Each record has `captured` or `unavailable` status. A captured file record has a
   basename-only filename, SHA-256, and byte count; a captured inline value is nonempty. An
   unavailable record has an explicit reason and null captured fields. Even a pre-launch failure
   emits all four required records as unavailable. This keeps failures diagnosable without adding
   unexpected entries to `screenshots[]`.
10. Provide deterministic loading, delayed-save, and forced-failure seams or request interceptors so
    Loading, Saving, and Error are asserted before capture rather than raced.
11. Capture board/detail as viewport screenshots so fixed-height and overflow defects remain
    visible. Home/Settings may additionally use full-page screenshots.
12. Capture failure artifacts and always stop the exact app process.

Do not use whole-page pixel equality as the first gate. Use semantic and geometry assertions as hard
gates, require side-by-side reviewer approval, and add narrowly cropped stable-region baselines only
after #351/#352/#353 make the harness trustworthy.

### 7.2 Required routes and states

| Surface | Required states |
| --- | --- |
| Projects | empty, one project, three projects, long content, menu open, create/delete dialog |
| Project board | normal seed, empty column, one-card column, 12-plus-card lane, filters closed/open/active, outline open/closed |
| Task detail | desktop modeless, mobile modal, long Markdown, dependencies, long subtasks, Saving, Saved, recoverable Error |
| Ideas | empty, populated, menu open, confirmation modal, terminal/collapsed lane |
| Settings | before tests and results displayed |
| Error | styled error route with shared shell |

### 7.3 Viewports

- 1440 by 1000: canonical comparison with this review.
- 1280 by 720 and 1279 by 720: exact desktop split-pane boundary.
- 1024 by 768: wrapping, empty-placeholder, and tablet/desktop transition discriminator.
- 768 by 1024: tablet portrait.
- 576 by 844 and 575 by 844: exact full-width-sheet boundary.
- 390 by 844: phone and full-width drawer.

Also review 200 percent browser zoom, Windows forced colors, and reduced motion. Zoom is a reflow
test, not a screenshot scaled to 200 percent.

This is not an all-state-by-all-viewport cross-product:

- 1440 by 1000: every core route/state.
- 1280/1279 pair: task-detail boundary, toolbar, two-lane visibility, and outline collapse.
- 1024 by 768 and 768 by 1024: board scrolling, outline drawer, filters, and modal detail.
- 576/575 pair: task-detail sheet boundary and toolbar wrapping.
- 390 by 844: core Home/board/detail/Ideas/Settings phone flow.

Chromium is the automated support gate. Firefox/WebKit are exploratory until the repository declares
and continuously tests them.

### 7.4 Screenshot naming and storage

Use:

~~~text
docs/ui-review/2026-08-15/capture-FULL_HEAD_SHA/STATE-WIDTHxHEIGHT-before.png
docs/ui-review/2026-08-15/capture-FULL_HEAD_SHA/STATE-WIDTHxHEIGHT-after.png
docs/ui-review/2026-08-15/capture-FULL_HEAD_SHA/capture-run-manifest-SHORT_SHA.json
docs/ui-review/2026-08-15/capture-FULL_HEAD_SHA/publication-index.json
~~~

This fixed date is the #322 modernization review root. Phase 0A and later #322 captures use the
per-head bundle convention; another initiative creates and reviews a different dated root before
changing the literal runbook path. The existing root-level current-state images, browser-smoke files,
and conceptual composition predate Phase 0A and remain explicitly documented historical evidence.
Each new bundle belongs to one exact head and is materialized with the same-parent atomic rename in
section 8.6, then reviewed and committed normally; before and after images from different heads
therefore live in different bundles. The capture-run manifest inventories the full transient run,
while `publication-index.json` lists the approved PNG subset in this bundle and binds it to the
frozen run-manifest hash.
Keep transient failures and full matrices under ignored ui-e2e-artifacts/SHORT_SHA/. Commit only the
representative evidence reviewed in the PR. Because the repository currently ignores PNGs, prefer a
narrow !docs/ui-review/**/*.png exception and explicit file paths; otherwise intentionally force-add
only reviewed images. Do not glob-add unrelated local screenshots.

### 7.5 Reviewer checklist

For every representative before/after pair:

- Correct SHA, route, fixture, viewport, browser, and Development environment are recorded.
- Global navigation and project identity are stable.
- Board cards start no lower than 220 pixels at 1440 by 1000.
- The board work surface reaches the viewport bottom while short card stacks do not paint a large
  gray void.
- No clipped text, badge collision, hidden focus, accidental overlap, double scrollbar, or document
  horizontal overflow.
- Outline selection and parent hierarchy remain understandable in grayscale.
- Completed content remains readable and controls retain contrast.
- Desktop detail preserves board context; mobile detail is clearly modal.
- Loading, empty, saving, saved, and error states look intentional.
- 200 percent zoom, forced colors, and reduced motion remain usable.
- Browser page errors and non-allowlisted console/same-origin failures are zero; the manifest prints
  every deliberate navigation/SignalR teardown event allowed by source.

## 8. Manual UI runbook

### 8.1 Prepare the exact source

Run the section 6.0 prelude first. Replace `$reviewSha` with the literal head being reviewed for
every PR; the evidence-base value below is not a moving alias.

~~~powershell
$repo = [IO.Path]::GetFullPath(
    (Invoke-GitChecked -Arguments @('rev-parse', '--show-toplevel')).Trim()
).TrimEnd('\')
$reviewSha = '3245d16c6a937d051fff8e7e111d1639b6364f16'
$work = Join-Path $env:TEMP ('kf-ui-run-' + [guid]::NewGuid().ToString('N'))
[void](Invoke-GitChecked -Arguments @(
    '-C', $repo, 'fetch', '--prune', 'origin'
) -TimeoutMilliseconds 600000)
$reviewObject = "$reviewSha^{commit}"
[void](Invoke-GitChecked -Arguments @('-C', $repo, 'cat-file', '-e', $reviewObject))
[void](Invoke-GitChecked -Arguments @(
    '-C', $repo, 'worktree', 'add', '--detach', $work, $reviewSha
))
$actualHead = (Invoke-GitChecked -Arguments @('-C', $work, 'rev-parse', 'HEAD')).Trim()
if ($actualHead -ne $reviewSha) {
    throw "Expected review SHA $reviewSha, got $actualHead."
}
Set-Location $work
(Invoke-GitChecked -Arguments @('-C', $work, 'status', '--short', '--branch')).TrimEnd()
$expectedHead = $reviewSha
$actualRoot = [IO.Path]::GetFullPath(
    (Invoke-GitChecked -Arguments @('-C', $work, 'rev-parse', '--show-toplevel')).Trim()
).TrimEnd('\')
$expectedRoot = [IO.Path]::GetFullPath($work).TrimEnd('\')
if (-not $actualRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The UI worktree identity does not match.'
}
~~~

Do not use the stale local master or a temp directory that merely resolves upward into the
profile-root Git repository. git rev-parse --show-toplevel must return the worktree just created.

### 8.2 Build, seed, and launch a disposable Development app

~~~powershell
npm ci
npx playwright install chromium
$savedNativePreference = $PSNativeCommandUseErrorActionPreference
try {
    $PSNativeCommandUseErrorActionPreference = $false
    $verifyOutput = @(& pwsh -NoProfile -File .\scripts\verify.ps1 2>&1)
    $verifyExit = $LASTEXITCODE
} finally {
    $PSNativeCommandUseErrorActionPreference = $savedNativePreference
}
$verifyOutput | ForEach-Object { Write-Host $_ }
$verifyLines = @($verifyOutput | ForEach-Object { $_.ToString().TrimEnd() })
if ($verifyLines.Count -eq 0 -or
    $verifyLines[-1] -ne 'Build + tests passed. Safe to push.') {
    throw "verify.ps1 did not report hard success (exit $verifyExit)."
}
if ($verifyExit -ne 0) {
    Write-Warning "verify.ps1 hard gates passed but its advisory exit was $verifyExit."
}

$shotDir = Join-Path $env:TEMP ('kf-ui-artifacts-' + [guid]::NewGuid().ToString('N'))
npm run test:ui:shots -- --mode serve --out $shotDir
~~~

The serve command is implemented in Phase 0A. It stays in the foreground, owns a unique port and
GUID-named DB directory, snapshots/restores process environment, seeds empty/normal/stress states,
and prints/writes manifest.json containing canonicalRepo, worktree, headSha, baseUrl, PID, port,
dbPath, dbSource=env, dbExisted=false, a `fixtureIds` map and resolved routes, Ideas reset/populate
steps, log paths, the trusted capture-plan path/hash/schema, and the planned-capture/result records
defined in section 7.1. Before entering foreground review, serve mode
creates its deterministic representative screenshot set through the same capture pipeline. Images
taken outside that pipeline remain transient and cannot be approved by the cleanup procedure. The
manifest reports `runStatus=running` while foreground review is active. An intentional clean Ctrl+C
shutdown may change it to `success` only after the one-to-one planned-capture/result validator
passes. Any setup, capture, runtime, or teardown error changes it to `failed` with the section 7.1
availability records. The command must clean up while preserving screenshots/manifests.

### 8.3 Verify identity and assets before judging the UI

In a second terminal, run the section 6.0 prelude first, then:

~~~powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$shotDir = (Read-Host 'Paste the exact output directory printed by the serve command').Trim()
$manifest = Get-Content -LiteralPath (Join-Path $shotDir 'manifest.json') -Raw | ConvertFrom-Json
Set-Location -LiteralPath $manifest.worktree
$servedHead = (Invoke-GitChecked -Arguments @(
    '-C', [string]$manifest.worktree, 'rev-parse', 'HEAD'
)).Trim()
if ($servedHead -ne $manifest.headSha) {
    throw 'The review terminal is not on the served SHA.'
}
if ($manifest.dbSource -ne 'env' -or $manifest.dbExisted -ne $false) {
    throw 'Refusing to review or mutate a non-fresh database.'
}
$base = $manifest.baseUrl
Invoke-RestMethod "$base/api/admin/health"
Invoke-WebRequest "$base/" | Select-Object StatusCode
$owner = Get-NetTCPConnection -State Listen -LocalPort $manifest.port |
    Where-Object OwningProcess -eq $manifest.pid
if (-not $owner) {
    throw 'The manifest process does not own the review listener.'
}
~~~

In browser DevTools:

1. Confirm the page URL uses the intended port.
2. Confirm the HTML references the critical fingerprinted Bootstrap, app, scoped, SortableJS,
   kanbanflow, and Blazor assets.
3. Confirm those requests return 200 or 304.
4. Confirm application/Bootstrap/scoped style sheets expose readable non-zero rules and their known
   computed-style sentinels match.
5. Confirm Bootstrap, Sortable, KanbanFlow, and Blazor runtimes report ready and the console is free
   of page errors before capture.

### 8.4 Manual interaction sequence

1. **Projects:** use the manifest's Home route plus its empty/normal/stress project-board routes;
   review the sequential empty/one/three/many Home captures rather than assuming several versions of
   `/` are simultaneously served. Keyboard through navigation, New Project, first card, Open, menu,
   and confirmation. Treat #200 as a known separate gap rather than claiming an Escape/trap fix
   here.
2. **Outline:** open/close, disclose levels, select task, add at each supported level, toggle done,
   drag, contextual menu, and focus restoration.
3. **Board header:** open Requirements and AI tools, close them, search, open Filters, add/remove
   every filter, reload a shared URL, use Back, and retain task deep link.
4. **Board:** drag within/across lanes, reload to confirm persistence, drop into empty lane, use
   Move to with keyboard only, toggle completion, and inspect blocked/claimed/progress metadata.
5. **Task detail desktop:** open from a card, tab into/out of the modeless pane, edit each field,
   verify Saving/Saved/Error, open a link, close, and verify focus returns.
6. **Task detail mobile:** verify background inertness, Tab/Shift+Tab containment, Escape ordering,
   full-width layout, and focus restoration.
7. **Ideas and Settings:** use the canonical Ideas route and execute the manifest's empty-then-
   populated reset/state steps sequentially; repeat shared board/modal/navigation smoke. Run
   Settings tests only after manifest DB identity remains source=env/existing=false.
8. Repeat key flows at every viewport, 200 percent zoom, forced colors, and reduced motion.

### 8.5 Troubleshooting

**Wrong or unstyled UI**

- Verify git rev-parse --show-toplevel and git rev-parse HEAD.
- Confirm ASPNETCORE_ENVIRONMENT is Development.
- Confirm the browser port belongs to this worktree's process.
- Inspect fingerprinted CSS/JS responses and cssRules. A 200 HTML response alone is not proof the
  UI assets loaded.
- Stop older listeners before restarting; do not launch a second copy on the same port.

**UI E2E reports unrelated feature/task failures**

- At the pre-Phase 0A baseline, a fixed-port collision can send one run to another run's isolated DB;
  do not treat that run as trustworthy. The Phase 0A runner must bind an OS-assigned port internally
  and verify exact child-PID ownership. Scenarios inside each run remain serial; independent default
  runs may coexist with isolated ports, DBs, and build outputs; see #351.

**Build copy/lock errors**

- The pre-Phase 0A harness and `SKIP_BUILD`/shared-output shortcuts are exclusive: another UI run may
  own the same bin/obj output. Bare Phase 0A runs instead use per-run output/intermediate paths and
  must pass the two-concurrent-run guard; see #352.

**Plausible geometry on an unstyled page**

- Require the static-asset/cssRules gate from #353 before trusting geometry or screenshots.

**No UI E2E summary**

- Treat the run as failed, inspect the first causal error and process exit, then rerun the whole
  sequential suite. Issue #314 owns an unconditional terminal summary.

**Unexpected empty board or wrong IDs**

- Confirm KANBANFLOW_DB_PATH points to the new disposable file. SeedData only creates the Sample
  Project when the database has zero projects.

### 8.6 Shutdown and cleanup

1. Stop the foreground serve command with Ctrl+C and let it restore environment/clean its DB.
2. In either terminal, reload the external artifact manifest so cleanup is self-contained.
3. Confirm the manifest PID and listener are gone.
4. Require the successful manifest to contain exactly one complete result for every immutable
   planned capture and no unexpected result. Validate every planned source before any repository
   write, stage the approved screenshots, frozen full-run manifest, and a publication index in a new
   per-head bundle, then publish the verified bundle with one same-parent atomic directory rename.
   The index lists the bundled PNG subset and binds it to the full-run manifest hash, whose remaining
   screenshot records may refer to transient files outside the bundle. Preserve a failure directory
   for diagnosis; do not publish its partial captures.
5. Resolve and validate the detached worktree before asking Git to remove it, then prove the exact
   path is no longer registered or present. If cleanup fails after the atomic evidence rename, keep
   the published bundle and report the worktree path for an explicit retry; do not roll back valid
   evidence merely to hide a cleanup failure.

Run the section 6.0 prelude in this terminal first; the cleanup uses its checked Git helpers.

~~~powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$shotDirInput = (Read-Host 'Paste the exact external artifact directory').Trim()
$shotDir = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $shotDirInput).Path
).TrimEnd('\')
$manifestPath = Join-Path $shotDir 'manifest.json'
$manifestItem = Get-Item -LiteralPath $manifestPath -Force
if (($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The external manifest cannot be a reparse point.'
}
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$manifestText = $strictUtf8.GetString($manifestBytes).TrimStart([char]0xFEFF)
$manifest = $manifestText | ConvertFrom-Json
if (-not ($manifest -is [pscustomobject])) {
    throw 'The external manifest must be one JSON object.'
}
$repoInput = (Read-Host 'Paste the trusted canonical checkout path').Trim()
if ($manifest.canonicalRepo -isnot [string] -or
    $manifest.worktree -isnot [string] -or
    $manifest.headSha -isnot [string] -or
    [string]::IsNullOrWhiteSpace([string]$manifest.canonicalRepo) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.worktree) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.headSha)) {
    throw 'The external manifest is missing repository/worktree identity fields.'
}
$repo = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $repoInput).Path).TrimEnd('\')
$manifestRepo = [IO.Path]::GetFullPath([string]$manifest.canonicalRepo).TrimEnd('\')
$work = [IO.Path]::GetFullPath([string]$manifest.worktree).TrimEnd('\')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$workLeaf = Split-Path -Leaf $work
if (-not ($work + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $workLeaf -notmatch '^kf-ui-run-[0-9a-f]{32}$') {
    throw 'The worktree path from the manifest failed validation.'
}

$actualRepo = [IO.Path]::GetFullPath(
    (Invoke-GitChecked -Arguments @('-C', $repo, 'rev-parse', '--show-toplevel')).Trim()
).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $repo '.git') -PathType Container) -or
    -not $actualRepo.Equals($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The trusted canonical checkout identity does not match.'
}
if (-not $manifestRepo.Equals($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The external manifest does not name the trusted canonical repository.'
}
$repoCommonOutput = Invoke-GitChecked -Arguments @(
    '-C', $repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'
)
$repoCommon = [IO.Path]::GetFullPath($repoCommonOutput.Trim()).TrimEnd('\')
$workCommonOutput = Invoke-GitChecked -Arguments @(
    '-C', $work, 'rev-parse', '--path-format=absolute', '--git-common-dir'
)
$workCommon = [IO.Path]::GetFullPath($workCommonOutput.Trim()).TrimEnd('\')
if (-not $repoCommon.Equals($workCommon, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The review worktree does not belong to the trusted canonical repository.'
}
$workHead = (Invoke-GitChecked -Arguments @('-C', $work, 'rev-parse', 'HEAD')).Trim()
if ($workHead -ne [string]$manifest.headSha) {
    throw 'The review worktree HEAD does not match the external manifest.'
}
$workStatus = Invoke-GitChecked -Arguments @(
    '-C', $work, 'status', '--porcelain', '--untracked-files=all'
)
if (-not [string]::IsNullOrWhiteSpace($workStatus)) {
    throw 'The review worktree is not clean; preserve it and inspect before cleanup.'
}

if (Get-Process -Id $manifest.pid -ErrorAction SilentlyContinue) {
    throw 'The review app is still running.'
}
if (Get-NetTCPConnection -State Listen -LocalPort $manifest.port -ErrorAction SilentlyContinue) {
    throw 'The review listener is still active.'
}

$shotRoot = [IO.Path]::GetFullPath($shotDir).TrimEnd('\') + '\'
$shotLeaf = Split-Path -Leaf $shotDir
if (-not $shotRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $shotLeaf -notmatch '^kf-ui-artifacts-[0-9a-f]{32}$') {
    throw 'The artifact source failed validation.'
}
$shotDir = $shotRoot.TrimEnd('\')
$shotDirItem = Get-Item -LiteralPath $shotDir -Force
if (($shotDirItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The artifact source cannot be a reparse point.'
}

$reviewDir = Join-Path $repo 'docs\ui-review\2026-08-15'
if ($manifest.runStatus -isnot [string] -or
    [string]$manifest.runStatus -cne 'success') {
    throw 'Only a successful capture/serve manifest can publish screenshots.'
}
if ([string]$manifest.headSha -notmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
    throw 'The successful manifest has an invalid head SHA.'
}
$trustedPlanRelative = 'scripts/ui-screenshot-plan.json'
if ($manifest.capturePlanPath -isnot [string] -or
    $manifest.capturePlanSha256 -isnot [string] -or
    $manifest.capturePlanSchemaVersion -isnot [long] -or
    [string]$manifest.capturePlanPath -cne $trustedPlanRelative -or
    [string]$manifest.capturePlanSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    $manifest.capturePlanSchemaVersion -ne 1) {
    throw 'The manifest does not identify the required trusted capture plan.'
}
$trustedPlanPath = [IO.Path]::GetFullPath((Join-Path $work $trustedPlanRelative))
if (-not ($trustedPlanPath + '\').StartsWith(
        $work + '\',
        [StringComparison]::OrdinalIgnoreCase
    ) -or -not (Test-Path -LiteralPath $trustedPlanPath -PathType Leaf)) {
    throw 'The exact-head trusted capture plan is missing or outside the worktree.'
}
$trustedPlanItem = Get-Item -LiteralPath $trustedPlanPath -Force
if (($trustedPlanItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The exact-head trusted capture plan cannot be a reparse point.'
}
$trustedPlanBytes = [IO.File]::ReadAllBytes($trustedPlanPath)
$trustedPlanHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($trustedPlanBytes)
)
if (-not $trustedPlanHash.Equals(
        [string]$manifest.capturePlanSha256,
        [StringComparison]::OrdinalIgnoreCase
    )) {
    throw 'The manifest capture-plan hash does not match the exact-head trusted plan.'
}
$trustedPlanText = $strictUtf8.GetString($trustedPlanBytes).TrimStart([char]0xFEFF)
$trustedPlan = $trustedPlanText | ConvertFrom-Json
if (-not ($trustedPlan -is [pscustomobject]) -or
    $trustedPlan.schemaVersion -isnot [long] -or
    $trustedPlan.schemaVersion -ne 1 -or
    $trustedPlan.captures -isnot [Array] -or
    @($trustedPlan.captures).Count -eq 0 -or
    $trustedPlan.requiredFailureArtifacts -isnot [Array] -or
    @($trustedPlan.requiredFailureArtifacts).Count -ne 4 -or
    -not ($manifest.fixtureIds -is [pscustomobject])) {
    throw 'The exact-head trusted capture plan or fixture-ID map has an invalid shape.'
}
$requiredFailureContracts = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::Ordinal
)
$requiredFailureContracts.Add('viewport-screenshot', [pscustomobject]@{
    kind = 'viewportScreenshot'; storage = 'file'
})
$requiredFailureContracts.Add('dom-snapshot', [pscustomobject]@{
    kind = 'domSnapshot'; storage = 'file'
})
$requiredFailureContracts.Add('browser-version', [pscustomobject]@{
    kind = 'browserVersion'; storage = 'inline'
})
$requiredFailureContracts.Add('page-diagnostics', [pscustomobject]@{
    kind = 'pageDiagnostics'; storage = 'file'
})
$seenFailureContracts = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal
)
foreach ($contract in @($trustedPlan.requiredFailureArtifacts)) {
    if (-not ($contract -is [pscustomobject]) -or
        $contract.name -isnot [string] -or
        $contract.kind -isnot [string] -or
        $contract.storage -isnot [string] -or
        -not $requiredFailureContracts.ContainsKey([string]$contract.name) -or
        -not $seenFailureContracts.Add([string]$contract.name)) {
        throw 'The trusted capture plan has an invalid failure-artifact contract.'
    }
    $requiredContract = $requiredFailureContracts[[string]$contract.name]
    if (-not ([string]$requiredContract.kind).Equals(
            [string]$contract.kind,
            [StringComparison]::Ordinal
        ) -or
        -not ([string]$requiredContract.storage).Equals(
            [string]$contract.storage,
            [StringComparison]::Ordinal
        )) {
        throw "Trusted failure-artifact contract mismatch: $($contract.name)"
    }
}
$expectedByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($template in @($trustedPlan.captures)) {
    if (-not ($template -is [pscustomobject]) -or
        -not ($template.viewport -is [pscustomobject]) -or
        $template.filename -isnot [string] -or
        $template.routeTemplate -isnot [string] -or
        $template.fixtureState -isnot [string] -or
        $template.viewport.width -isnot [long] -or
        $template.viewport.height -isnot [long]) {
        throw 'The trusted capture plan contains a non-object record or viewport.'
    }
    $expectedName = [string]$template.filename
    $routeTemplate = [string]$template.routeTemplate
    $expectedFixtureState = [string]$template.fixtureState
    if ([IO.Path]::GetFileName($expectedName) -ne $expectedName -or
        [IO.Path]::GetExtension($expectedName) -ine '.png' -or
        [string]::IsNullOrWhiteSpace($routeTemplate) -or
        [string]::IsNullOrWhiteSpace($expectedFixtureState) -or
        $template.viewport.width -le 0 -or
        $template.viewport.height -le 0 -or
        $expectedByName.ContainsKey($expectedName)) {
        throw "Trusted capture plan has an invalid or duplicate record: $expectedName"
    }
    $resolvedRoute = $routeTemplate
    foreach ($token in [regex]::Matches(
        $routeTemplate,
        '\{fixture:([A-Za-z][A-Za-z0-9]*)\}'
    )) {
        $fixtureName = $token.Groups[1].Value
        $fixtureProperties = @($manifest.fixtureIds.PSObject.Properties | Where-Object {
            $_.Name.Equals($fixtureName, [StringComparison]::Ordinal)
        })
        if ($fixtureProperties.Count -ne 1 -or
            $fixtureProperties[0].Value -isnot [long] -or
            $fixtureProperties[0].Value -le 0) {
            throw "Manifest cannot resolve capture-plan fixture ID: $fixtureName"
        }
        $resolvedRoute = $resolvedRoute.Replace(
            $token.Value,
            [Convert]::ToString(
                $fixtureProperties[0].Value,
                [Globalization.CultureInfo]::InvariantCulture
            )
        )
    }
    if ($resolvedRoute.Contains('{') -or $resolvedRoute.Contains('}') -or
        -not $resolvedRoute.StartsWith('/') -or $resolvedRoute.StartsWith('//')) {
        throw "Trusted capture plan produced an invalid route: $expectedName"
    }
    $expectedByName.Add($expectedName, [pscustomobject]@{
        filename = $expectedName
        route = $resolvedRoute
        fixtureState = $expectedFixtureState
        viewport = [pscustomobject]@{
            width = $template.viewport.width
            height = $template.viewport.height
        }
    })
}

if ($manifest.plannedCaptures -isnot [Array] -or
    $manifest.screenshots -isnot [Array]) {
    throw 'The successful manifest requires JSON arrays for plans and results.'
}
$plannedCaptureRecords = @($manifest.plannedCaptures)
$screenshotRecords = @($manifest.screenshots)
if ($plannedCaptureRecords.Count -ne $expectedByName.Count -or
    $screenshotRecords.Count -ne $expectedByName.Count) {
    throw 'The successful manifest does not match the trusted capture-plan cardinality.'
}
$plannedByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($planned in $plannedCaptureRecords) {
    if (-not ($planned -is [pscustomobject]) -or
        -not ($planned.viewport -is [pscustomobject]) -or
        $planned.filename -isnot [string] -or
        $planned.route -isnot [string] -or
        $planned.fixtureState -isnot [string] -or
        $planned.viewport.width -isnot [long] -or
        $planned.viewport.height -isnot [long]) {
        throw 'The successful manifest contains a non-object plan or viewport.'
    }
    $plannedName = [string]$planned.filename
    $plannedRoute = [string]$planned.route
    $plannedFixtureState = [string]$planned.fixtureState
    if ([IO.Path]::GetFileName($plannedName) -ne $plannedName -or
        [IO.Path]::GetExtension($plannedName) -ine '.png' -or
        [string]::IsNullOrWhiteSpace($plannedRoute) -or
        -not $plannedRoute.StartsWith('/') -or
        $plannedRoute.StartsWith('//') -or
        [string]::IsNullOrWhiteSpace($plannedFixtureState) -or
        $planned.viewport.width -le 0 -or
        $planned.viewport.height -le 0 -or
        -not $expectedByName.ContainsKey($plannedName) -or
        $plannedByName.ContainsKey($plannedName)) {
        throw "Successful manifest has an invalid, unexpected, or duplicate plan: $plannedName"
    }
    $expected = $expectedByName[$plannedName]
    if (-not ([string]$expected.filename).Equals(
            $plannedName,
            [StringComparison]::Ordinal
        ) -or
        -not ([string]$expected.route).Equals($plannedRoute, [StringComparison]::Ordinal) -or
        -not ([string]$expected.fixtureState).Equals(
            $plannedFixtureState,
            [StringComparison]::Ordinal
        ) -or
        $expected.viewport.width -ne $planned.viewport.width -or
        $expected.viewport.height -ne $planned.viewport.height) {
        throw "Manifest plan does not match the exact-head trusted capture: $plannedName"
    }
    $plannedByName.Add($plannedName, $planned)
}
$seenScreenshotNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($record in $screenshotRecords) {
    if (-not ($record -is [pscustomobject]) -or
        -not ($record.viewport -is [pscustomobject]) -or
        $record.filename -isnot [string] -or
        $record.route -isnot [string] -or
        $record.fixtureState -isnot [string] -or
        $record.status -isnot [string] -or
        $record.sha256 -isnot [string] -or
        $record.viewport.width -isnot [long] -or
        $record.viewport.height -isnot [long] -or
        $record.bytes -isnot [long] -or
        $record.width -isnot [long] -or
        $record.height -isnot [long]) {
        throw 'The successful manifest contains a non-object result or viewport.'
    }
    $recordName = [string]$record.filename
    $recordRoute = [string]$record.route
    $recordFixtureState = [string]$record.fixtureState
    if ([IO.Path]::GetFileName($recordName) -ne $recordName -or
        [IO.Path]::GetExtension($recordName) -ine '.png' -or
        -not $seenScreenshotNames.Add($recordName) -or
        -not $plannedByName.ContainsKey($recordName) -or
        [string]$record.status -cne 'captured' -or
        [string]$record.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $record.bytes -le 0 -or
        $record.width -le 0 -or
        $record.height -le 0 -or
        [string]::IsNullOrWhiteSpace($recordRoute) -or
        [string]::IsNullOrWhiteSpace($recordFixtureState) -or
        $record.viewport.width -le 0 -or
        $record.viewport.height -le 0) {
        throw "Successful manifest has an invalid, unexpected, or incomplete result: $recordName"
    }
    $planned = $plannedByName[$recordName]
    if (-not ([string]$planned.filename).Equals($recordName, [StringComparison]::Ordinal) -or
        -not ([string]$planned.route).Equals($recordRoute, [StringComparison]::Ordinal) -or
        -not ([string]$planned.fixtureState).Equals(
            $recordFixtureState,
            [StringComparison]::Ordinal
        ) -or
        $planned.viewport.width -ne $record.viewport.width -or
        $planned.viewport.height -ne $record.viewport.height) {
        throw "Screenshot result identity does not match its planned capture: $recordName"
    }
}

Add-Type -AssemblyName System.Drawing
$validatedArtifactsByName = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($record in $screenshotRecords) {
    $canonicalName = [string]$record.filename
    $source = Join-Path $shotDir $canonicalName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Successful manifest screenshot does not exist: $source"
    }
    $sourceItem = Get-Item -LiteralPath $source
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Successful manifest screenshot cannot be a reparse point: $source"
    }
    $signatureStream = [IO.File]::Open(
        $source,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read
    )
    try {
        $signatureBytes = [byte[]]::new(8)
        $signatureLength = $signatureStream.Read($signatureBytes, 0, $signatureBytes.Length)
    }
    finally {
        $signatureStream.Dispose()
    }
    if ($signatureLength -ne 8 -or
        [Convert]::ToHexString($signatureBytes) -cne '89504E470D0A1A0A') {
        throw "Successful manifest screenshot is not PNG-encoded: $source"
    }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $image = [Drawing.Image]::FromFile($source)
    try {
        $sourceWidth = $image.Width
        $sourceHeight = $image.Height
        $sourceFormat = $image.RawFormat.Guid
    }
    finally {
        $image.Dispose()
    }
    if ($sourceItem.Length -ne $record.bytes -or
        -not $sourceHash.Equals([string]$record.sha256, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceFormat -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or
        $sourceWidth -ne $record.width -or
        $sourceHeight -ne $record.height) {
        throw "Screenshot does not match its capture-manifest result: $canonicalName"
    }
    $validatedArtifactsByName.Add($canonicalName, [pscustomobject]@{
        Source = $source
        Leaf = $canonicalName
        Hash = ([string]$record.sha256).ToUpperInvariant()
        Bytes = $record.bytes
        Width = $record.width
        Height = $record.height
        Route = [string]$record.route
        FixtureState = [string]$record.fixtureState
        ViewportWidth = $record.viewport.width
        ViewportHeight = $record.viewport.height
        IsImage = $true
        IsManifest = $false
        IsPublicationIndex = $false
    })
}

$approvedNames = @((Read-Host 'Comma-separated approved screenshot filenames').Split(
    ',',
    [System.StringSplitOptions]::RemoveEmptyEntries
) | ForEach-Object Trim | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($approvedNames.Count -eq 0) {
    throw 'At least one representative screenshot must be preserved before worktree cleanup.'
}
$approvedNameSet = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
$approvedEntries = [Collections.Generic.List[object]]::new()
foreach ($name in $approvedNames) {
    if ([IO.Path]::GetFileName($name) -ne $name -or
        [IO.Path]::GetExtension($name) -ine '.png' -or
        -not $approvedNameSet.Add($name)) {
        throw "Screenshot approval must name a unique PNG without a path: $name"
    }
    if (-not $validatedArtifactsByName.ContainsKey($name)) {
        throw "Approved screenshot is not in the validated capture set: $name"
    }
    [void]$approvedEntries.Add($validatedArtifactsByName[$name])
}

$reviewDirFull = [IO.Path]::GetFullPath($reviewDir).TrimEnd('\')
$reviewDirExisted = Test-Path -LiteralPath $reviewDirFull -PathType Container
if (-not $reviewDirExisted) {
    throw "The tracked review directory must exist before publication: $reviewDirFull"
}
$reviewDirItem = Get-Item -LiteralPath $reviewDirFull -Force
if (($reviewDirItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The review directory cannot be a reparse point: $reviewDirFull"
}
$publicationLeaf = 'capture-' + ([string]$manifest.headSha).ToLowerInvariant()
$publicationDir = Join-Path $reviewDirFull $publicationLeaf
if (-not ([IO.Path]::GetDirectoryName($publicationDir)).Equals(
        $reviewDirFull,
        [StringComparison]::OrdinalIgnoreCase
    ) -or $publicationLeaf -notmatch '^capture-(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
    (Test-Path -LiteralPath $publicationDir)) {
    throw "Refusing to overwrite an existing capture publication: $publicationDir"
}
$manifestLeaf = 'capture-run-manifest-' +
    ([string]$manifest.headSha).Substring(0, 7) + '.json'
$manifestHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($manifestBytes)
)
$publicationIndexObject = [ordered]@{
    schemaVersion = 1
    headSha = [string]$manifest.headSha
    capturePlan = [ordered]@{
        path = $trustedPlanRelative
        sha256 = $trustedPlanHash
        schemaVersion = 1
    }
    captureRunManifest = [ordered]@{
        filename = $manifestLeaf
        sha256 = $manifestHash
        bytes = $manifestBytes.LongLength
    }
    approvedScreenshots = @($approvedEntries | ForEach-Object {
        [ordered]@{
            filename = $_.Leaf
            sha256 = $_.Hash
            bytes = $_.Bytes
            width = $_.Width
            height = $_.Height
            route = $_.Route
            fixtureState = $_.FixtureState
            viewport = [ordered]@{
                width = $_.ViewportWidth
                height = $_.ViewportHeight
            }
        }
    })
}
$publicationIndexJson = $publicationIndexObject | ConvertTo-Json -Depth 8
$publicationIndexBytes = [Text.UTF8Encoding]::new($false).GetBytes(
    $publicationIndexJson + "`n"
)
$publicationIndexHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($publicationIndexBytes)
)
$publishEntries = [Collections.Generic.List[object]]::new()
foreach ($entry in $approvedEntries) {
    [void]$publishEntries.Add($entry)
}
[void]$publishEntries.Add([pscustomobject]@{
    Source = $manifestPath
    Leaf = $manifestLeaf
    Hash = $manifestHash
    Bytes = [int64]$manifestBytes.LongLength
    Width = 0
    Height = 0
    IsImage = $false
    IsManifest = $true
    IsPublicationIndex = $false
})
[void]$publishEntries.Add([pscustomobject]@{
    Source = $null
    Leaf = 'publication-index.json'
    Hash = $publicationIndexHash
    Bytes = $publicationIndexBytes.LongLength
    Width = 0
    Height = 0
    IsImage = $false
    IsManifest = $false
    IsPublicationIndex = $true
})

# All manifest, source, and destination checks above finish before this first repository write.
$stageLeaf = '.kf-ui-publish-' + [guid]::NewGuid().ToString('N')
$publishStage = Join-Path $reviewDirFull $stageLeaf
if (-not ([IO.Path]::GetDirectoryName($publishStage)).Equals(
        $reviewDirFull,
        [StringComparison]::OrdinalIgnoreCase
    ) -or $stageLeaf -notmatch '^\.kf-ui-publish-[0-9a-f]{32}$' -or
    (Test-Path -LiteralPath $publishStage)) {
    throw 'The evidence staging directory failed validation.'
}
$publicationSucceeded = $false
$stageCreated = $false
$publicationError = $null
$rollbackError = $null
try {
    $createdStage = New-Item -ItemType Directory -Path $publishStage
    $stageCreated = $true
    $createdStageFull = [IO.Path]::GetFullPath($createdStage.FullName).TrimEnd('\')
    if (-not $createdStageFull.Equals(
            $publishStage,
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        -not ([IO.Path]::GetDirectoryName($createdStageFull)).Equals(
            $reviewDirFull,
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        ($createdStage.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The created evidence staging directory failed its identity check.'
    }
    foreach ($entry in $publishEntries) {
        $stagedPath = Join-Path $publishStage $entry.Leaf
        if ($entry.IsManifest) {
            [IO.File]::WriteAllBytes($stagedPath, $manifestBytes)
        }
        elseif ($entry.IsPublicationIndex) {
            [IO.File]::WriteAllBytes($stagedPath, $publicationIndexBytes)
        }
        else {
            Copy-Item -LiteralPath $entry.Source -Destination $stagedPath
        }
        $stagedItem = Get-Item -LiteralPath $stagedPath
        if (($stagedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Staged evidence cannot be a reparse point: $($entry.Leaf)"
        }
        $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
        if ($stagedItem.Length -ne $entry.Bytes -or
            -not $stagedHash.Equals($entry.Hash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Staged evidence failed byte/hash verification: $($entry.Leaf)"
        }
        if ($entry.IsImage) {
            $stagedImage = [Drawing.Image]::FromFile($stagedPath)
            try {
                if ($stagedImage.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or
                    $stagedImage.Width -ne $entry.Width -or
                    $stagedImage.Height -ne $entry.Height) {
                    throw "Staged evidence failed PNG format/dimension verification: $($entry.Leaf)"
                }
            }
            finally {
                $stagedImage.Dispose()
            }
        }
    }

    # The staging and destination directories share a parent, so this publishes the set together.
    [IO.Directory]::Move($publishStage, $publicationDir)
    $publicationSucceeded = $true
}
catch {
    $publicationError = $_
}
finally {
    if (-not $publicationSucceeded -and $stageCreated -and
        (Test-Path -LiteralPath $publishStage -PathType Container)) {
        try {
            $stageItem = Get-Item -LiteralPath $publishStage -Force
            if (($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to traverse a reparse-point staging path: $publishStage"
            }
            foreach ($entry in $publishEntries) {
                $stagedPath = Join-Path $publishStage $entry.Leaf
                if (Test-Path -LiteralPath $stagedPath -PathType Leaf) {
                    Remove-Item -LiteralPath $stagedPath -Force
                }
            }
            if (@(Get-ChildItem -LiteralPath $publishStage -Force).Count -eq 0) {
                Remove-Item -LiteralPath $publishStage -Force
            }
            else {
                throw "Staging cleanup found unexpected content: $publishStage"
            }
        }
        catch {
            $rollbackError = $_
        }
    }
}
if ($null -ne $rollbackError) {
    throw "Evidence publication failed and staging cleanup failed; preserve $publishStage. $rollbackError"
}
if ($null -ne $publicationError) {
    throw $publicationError
}
if (-not $publicationSucceeded) {
    throw 'Evidence publication did not complete.'
}

Set-Location $repo
$resolvedWork = $work
$leaf = Split-Path -Leaf $resolvedWork
if (-not $resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $leaf -notmatch '^kf-ui-run-[0-9a-f]{32}$') {
    throw 'The worktree cleanup target failed validation.'
}
try {
    $registeredBefore = @(Get-RegisteredWorktreePaths -Repository $repo)
    $exactBefore = @($registeredBefore | Where-Object {
        $_.Equals($resolvedWork, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($exactBefore.Count -ne 1) {
        throw 'The exact worktree must be registered once immediately before cleanup.'
    }
    [void](Invoke-GitChecked -Arguments @('-C', $repo, 'worktree', 'remove', $resolvedWork))
    $registeredAfter = @(Get-RegisteredWorktreePaths -Repository $repo)
    $exactAfter = @($registeredAfter | Where-Object {
        $_.Equals($resolvedWork, [StringComparison]::OrdinalIgnoreCase)
    })
}
catch {
    throw "Worktree cleanup failed for $resolvedWork; the published evidence bundle remains for " +
        "an explicit retry. $($_.Exception.Message)"
}
if ($exactAfter.Count -ne 0 -or (Test-Path -LiteralPath $resolvedWork)) {
    throw "Worktree $resolvedWork remains registered or present after removal; the published " +
        'evidence bundle remains for an explicit retry.'
}
~~~

The serve script may recursively remove only its own resolved GUID-named data directory after
verifying it is an absolute child of the system temp root, is not the temp root/profile/repository,
its PID/listener is gone, and its environment has been restored. Never run cleanup against the
canonical checkout, the user's normal kanbanflow.db, the profile root, or an unresolved path.
The external kf-ui-artifacts directory is retained until its representative evidence is committed;
it is not recursively deleted by this runbook.

## 9. PR sequence and definition of done

Recommended PR sequence:

1. Publish design evidence and the selector/issue-ownership inventory.
2. Minimum harness trust: summary, unique process identity, static assets, failure artifacts,
   deterministic capture/serve commands, and Axe.
3. Design docs, tokens, landmarks, and shell.
4. Compact project utilities and filter presentation.
5. Semantic outline.
6. Board lanes/cards and Move to.
7. Responsive task detail and save feedback.
8. Home evidence/visual-primitives handoff to #420/#422/#423; no separate #322 Projects-grid PR.
9. Performance/query guard, representative after-screenshots, documentation, and compatibility CSS
   cleanup.

Every PR must:

- Link #322 and its narrow child/related issue.
- State the exact base/head SHA and scope boundary.
- Include before/after screenshots for affected viewports and states.
- Add behavior assertions, not only screenshots.
- Run focused tests, verify.ps1, script tests where relevant, API smoke, full UI E2E on a unique
  port, and /ideas regressions for shared board changes.
- Report any issue-linked known red baseline separately and keep the applicable phase exit blocked
  until its removal condition passes; never include it in a green count.
- Report actual pass/fail/skip counts and the final UI summary.
- Preserve unrelated work and never use the normal user database.
- Resolve valid review threads and recheck the final pushed head.
- Remain revertible without schema/data repair.

The epic is complete only when all measurable criteria in section 1 pass, representative evidence is
committed, the runbook has been executed from a fresh worktree, open adjacent issues are correctly
linked rather than duplicated, and the final GitHub review state is clean.
