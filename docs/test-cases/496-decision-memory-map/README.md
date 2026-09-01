# TC-496 — Decision & Memory Map

## Authority and status

| Field | Value |
|---|---|
| Test case | `TC-496` |
| Requirement | [Issue #496 — Render the existing MCP memory graph as a read-only Decision & Memory Map](https://github.com/joelfuller2016/KanbanFlow-v2/issues/496) |
| Implementation | [PR #497](https://github.com/joelfuller2016/KanbanFlow-v2/pull/497) |
| Delivery slice | Phase 0 contract/safety and Phase 1 read-only visualization |
| Status | Active; latest recorded run passed; see PR #497 for current delivery state |
| Implementation evidence | [`d7be7da5d98d850607d955fe7cdb64522503f45f`](https://github.com/joelfuller2016/KanbanFlow-v2/commit/d7be7da5d98d850607d955fe7cdb64522503f45f) |
| Latest run | [2026-08-30 · `d7be7da5d98d`](runs/2026-08-30-d7be7da5d98d.md) |
| Canonical automation | [`DecisionGraphProjectionTests.cs`](../../../tests/KanbanFlow.Tests/DecisionGraphProjectionTests.cs) and the `#496` scenario in [`ui-e2e.mjs`](../../../scripts/ui-e2e.mjs) |

## Goal

Prove that a human can inspect explicit Codex-, Claude-, or human-authored memory records through a
read-only KanbanFlow UI without introducing a second graph store, a new graph REST contract, an MCP
fork, or any attempt to request or reconstruct model-hidden reasoning.

The test must prove both the pure graph projection and the real system boundary from public writes
to the interactive Blazor page. It also records the navigation, hydration, cancellation,
accessibility, privacy, and bounded-rendering regressions discovered during review, distinguishing
automated guards from source-review-only invariants.

## Architecture under test

```text
public REST record writes + native MCP relation writes
                         |
                         v
existing MemoryNode / Observation / MemoryRelation tables
                         |
                         v
IMemoryService.ReadGraphAsync (maximum 900 source nodes, no tracking)
                         |
                         v
DecisionGraphProjection (80 nodes / 320 unique edges / 50 visible observations)
                         |
                         v
/memory -> DecisionGraph map, list, details, filters, and state messages
```

Current implementation anchors; the linked run pins observed behavior to its evidence commit:

- [`MemoryMap.razor`](../../../src/KanbanFlow/Components/Pages/MemoryMap.razor) owns the `/memory`
  route, namespace loading, URL state, async states, cancellation, and safe error presentation.
- [`DecisionGraphProjection.cs`](../../../src/KanbanFlow/Services/DecisionGraphProjection.cs) owns
  deterministic classification, selection, layout, caps, edge identity, and filter semantics.
- [`DecisionGraph.razor`](../../../src/KanbanFlow/Components/Shared/DecisionGraph.razor) and its
  [scoped CSS](../../../src/KanbanFlow/Components/Shared/DecisionGraph.razor.css) render the map,
  accessible list, details, focus states, dark-theme tokens, and narrow-screen scroll regions.
- [`NavMenu.razor`](../../../src/KanbanFlow/Components/Layout/NavMenu.razor) exposes the global Graph
  navigation entry.
- [`MemoryService.ReadGraphAsync`](../../../src/KanbanFlow/Services/MemoryService.cs) remains the
  sole graph read path used by this UI.

## Ownership and privacy contract

- The graph contains deliberately stored durable records only: context, questions, options,
  decisions, evidence, outcomes, actions, and observations.
- The page does not request, infer, or reconstruct private model reasoning. It renders deliberately
  stored strings as data, so the no-secrets boundary relies on writer discipline and normal Razor
  encoding rather than redaction.
- Test fixtures must not contain credentials, secrets, private scratchpads, or raw internal
  reasoning.
- GitHub is canonical for the issue, code, review, approval, merge, and concise publishable outcome.
- KanbanFlow owns the local phase tree, acceptance state, detailed graph, relationships, layout,
  filters, and coordination state.
- Current automation is GitHub to KanbanFlow. This case must not publish the detailed graph back to
  GitHub or exercise a last-writer-wins two-way sync.

## In scope

- Reuse of `MemoryNode`, `Observation`, `MemoryRelation`, and `IMemoryService` with no schema or
  transport fork.
- Project-, Feature-, Agent-, and Session-scope namespaces, defaulting to Agent / `knowledge-graph`.
- Current, non-superseded records only; the service deterministically takes the oldest 900 current
  records before loading their related observations and relations.
- Context, Question, Option, Decision, Evidence, Outcome, Action, and Other lanes.
- Case-insensitive entity tags plus `MemoryType.Decision` precedence.
- Deterministic handling of cycles, self-links, disconnected nodes, dangling endpoints, duplicate
  logical edges, dense graphs, large observation histories, and rendering caps.
- Node-cap priority in this order: decisions, immediate decision neighbors, questions/options/
  outcomes, then the remaining records.
- Observation selection that retains an older search-matching observation, fills remaining detail
  slots from the newest history, and displays the selected observations chronologically.
- Directed SVG relations and readable incoming/outgoing relation lists.
- Inspectable content, type, tags, source, confidence, timestamps, observations, and provenance.
- Namespace, text (`q`), and kind (`kind`) URL state; deep links; reload; replacement-history
  behavior; and preservation of unsubmitted scope/namespace drafts.
- Loading, error, empty, no-match, source-cap, node-cap, edge-cap, and observation-cap states.
- Pointer and keyboard selection, visible focus, a semantic list fallback, concise assistive
  announcements, reduced-motion styling, dark-theme styling, and 390-pixel scrolling.
- Global navigation, documentation, review remediation, local dogfood, and sync-boundary evidence.

## Explicit non-goals

- Graph editing or deletion from the UI.
- A second database, third-party memory server, graph library, plugin, skill, or new MCP tool.
- A whole-graph REST endpoint or a change to the existing MCP contract.
- Project/run lenses, durable task identity, attempts, or session lineage; those remain with #336
  and #276.
- Live agent presence or streaming; that remains with #323.
- Broad KanbanFlow-to-GitHub publishing or graph-wide synchronization; that remains with #361.
- A formal color-contrast, Axe, pixel-diff, or performance baseline.

## Acceptance traceability

| AC | Requirement | Primary guard | Expected result |
|---:|---|---|---|
| 1 | Reuse the existing graph and service | Source review, full build/test gate | No migration, duplicate store, graph REST route, or MCP contract change appears in the PR. |
| 2 | Distinguishable semantic lanes | Classification theory plus real browser fixture | Declared decisions win; mixed-case/free-form tags map deterministically to the expected lane. |
| 3 | Directed, inspectable relationships | Projection edge tests, browser MCP fixture, and source review | Only visible endpoints render; the browser asserts incoming verbs, while projection/source review covers outgoing direction and detail rendering. |
| 4 | Safe inspectable details | Browser detail assertions and Razor source review | Confidence, tags, source, timestamps, observations, and relations render without raw HTML. |
| 5 | URL-addressable controls and explicit states | Browser URL/reload/draft assertions; component source branches; cap-flag tests | `scope`, `scopeKey`, `q`, and `kind` survive tested navigation; distinct state branches exist; source/render-cap flags are deterministic. |
| 6 | Determinism and malformed-data tolerance | Cycle/self-link/dangling/dense/duplicate tests | Repeated builds have identical nodes, coordinates, and edges; bad endpoints are omitted safely. |
| 7 | Accessible fallback and responsive use | Browser keyboard/list/live-status/390px assertions plus CSS review | The list supports keyboard selection; the narrow viewport scrolls horizontally and uses a bounded vertical container with two-axis overflow in CSS. |
| 8 | Explicit-record privacy boundary | Browser privacy-note assertion and source review | The page states the boundary and never displays raw infrastructure exceptions. |
| 9 | No GitHub graph write path or Actions | PR diff and sync dry-run review | The feature is read-only and makes no GitHub mutation or workflow change. |
| 10 | Local verification passes | Commands below | Hard build/test gates and the complete browser suite pass at the recorded commit. |

## Prerequisites

- A checkout of the commit being tested. Record the full SHA in the run file.
- PowerShell 7 and the repository's .NET 10 SDK.
- Node.js, npm dependencies, and the Playwright browser installed from the repository lockfile.
- A free loopback port. The browser suite defaults to `5198`; set `UI_E2E_PORT` when another run may
  be active.
- No production database requirement. The browser harness creates a uniquely named temporary
  directory and SQLite database, starts its own app, and removes both on teardown.
- Network access is needed only for GitHub/status readback or package installation, not for the
  isolated feature scenario itself.

## Automated fixture

The browser scenario creates a unique Agent-scope namespace named
`ui-decision-map-<timestamp>` inside its disposable database:

| Record | Write surface | Type/tag | Purpose |
|---|---|---|---|
| Context | `POST /api/memory` | `Semantic` / `context` | Proves a normal semantic record and incoming context relation. |
| Decision | `POST /api/memory` | `Decision` / `OPEN_QUESTION, chosen` | Proves explicit Decision precedence over a conflicting question tag and renders confidence `0.9`. |
| Evidence | `POST /api/memory` | `Semantic` / uppercase `EVIDENCE` | Proves case-insensitive tag classification and text filtering. |
| Evidence observation | native MCP `add_observations` | Explicit observation text | Proves an MCP-written observation reaches the selected-record details. |
| Context -> Decision | native MCP `create_relations` | `frames` | Proves one directed relationship. |
| Evidence -> Decision | native MCP `create_relations` | `supports` | Proves a second directed relationship and readable detail text. |

Unit fixtures independently cover every lane through representative normalized tags, 900 source
records, 80 rendered records, dense relations beyond 320, 1,000 observations, cycles, self-links,
dangling endpoints, duplicate logical edges, and invalid inputs.

## Execution commands

Run from the repository root.

### 1. Full hard gate

```powershell
./scripts/verify.ps1
```

Pass when restore succeeds, the warnings-as-errors build has zero warnings/errors, every xUnit test
passes, and the script prints `Build + tests passed. Safe to push.` The repository-wide formatter
check is currently advisory; record its warning but do not misreport it as a product-test failure.

### 2. Focused projection suite

```powershell
dotnet test tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj `
  --filter "FullyQualifiedName~DecisionGraphProjectionTests"
```

Pass when every discovered focused case passes. The `d7be7da` run baseline is 20 cases, including
the theory's ten data rows.

### 3. Targeted MCP and memory-service suites

```powershell
dotnet test tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj --no-restore `
  --filter "FullyQualifiedName~McpKnowledgeGraphToolsTests"

dotnet test tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj --no-restore `
  --filter "FullyQualifiedName~MemoryServiceTests"
```

Pass when all targeted knowledge-graph and memory-service cases pass. The latest recorded baseline is
30 MCP knowledge-graph cases plus 64 memory-service cases.

### 4. Script tests

```powershell
npm run test:scripts
```

Pass when all script tests pass. This protects shared sync/priority behavior touched by the broader
repository even though #496 adds no sync writer.

### 5. Complete browser suite

```powershell
$env:UI_E2E_PORT = '5600'  # choose a free port
npm run test:ui
```

Pass when every discovered scenario passes and the harness tears down its app and temporary
database. The `d7be7da` run baseline is 50 scenarios. Do not run two suites on the same port.

### 6. Optional live dogfood smoke

```powershell
./scripts/start-kanbanflow.ps1
```

Open:

```text
http://127.0.0.1:5123/memory?scope=Agent&scopeKey=kanbanflow-496
```

Use only a deliberately seeded local namespace. Verify HTTP 200, the page title, namespace, privacy
notice, expected record/relation counts, and Graph navigation. This is supplemental evidence; the
disposable browser fixture is the repeatable gate.

### Issue-plan variance: full wrapper MCP smoke

Issue #496's test plan also names `npm run test:mcp`. The required targeted backend suites above and
the disposable browser scenario's real native `create_relations` call cover the MCP behavior changed
or consumed by this slice. Because the slice did not change the MCP/API contract and the broader
script defaults to a live loopback service, the latest run records that full wrapper smoke as **Not
run** instead of silently treating it as completed. Run it only against an isolated loopback instance
when the MCP/API surface changes or a reviewer makes it a merge condition.

## Detailed automated matrix

| ID | Behavior | Automated evidence |
|---|---|---|
| TC-496-01 | Ten representative normalized tag/type rows and unknown enum values map to a lane | `Classify_UsesMemoryTypeAndNormalizedEntityTags` |
| TC-496-02 | Explicit Decision type wins over conflicting tags | `Classify_ExplicitDecisionTypeWinsOverConflictingTags` |
| TC-496-03 | Search includes observations and removes cross-filter edges | `Build_SearchesObservationsAndOmitsEdgesWhoseOtherEndpointIsFilteredOut` |
| TC-496-04 | Cycles, self-links, and dangling edges remain deterministic | `Build_IsDeterministicAndToleratesCyclesSelfLinksAndDanglingEdges` |
| TC-496-05 | Node cap retains a decision and its neighbor, with honest omission | `Build_RenderCapKeepsDecisionAndItsNeighborAndReportsOmission` |
| TC-496-06 | Lanes and the 900-node source-cap flag are deterministic | `Build_AssignsDistinctSemanticLanesAndHonestStoreCapWarning` |
| TC-496-07 | Null graph and non-positive limits fail at the guard | `Build_RejectsNullGraphOrNonPositiveRenderCap` |
| TC-496-08 | Lane filtering removes other lanes and cross-lane edges | `Build_LaneFilterKeepsOnlyTheRequestedLaneAndDropsCrossLaneEdges` |
| TC-496-09 | Dense relations cap at 320 and case-only duplicates consume no slot | `Build_BoundsDenseRelationsDeterministicallyAndPrioritizesDecisionEdges` |
| TC-496-10 | Search sees all 1,000 observations while details show a bounded 50 | `Build_BoundsVisibleObservationsButSearchesTheCompleteHistory` |
| TC-496-11 | One full lane produces a canvas taller than 10,000 pixels | `Build_OneFullLaneProducesAViewportScrollableCanvas` |
| TC-496-12 | Real REST records and native MCP edges reach the Blazor map | `Decision map renders explicit agent records with filters and list fallback (#496)` |
| TC-496-13 | Whitespace namespace validation preserves the loaded graph and URL | Same browser scenario |
| TC-496-14 | `q`/`kind` deep links, reload, clear, and replacement history work | Same browser scenario |
| TC-496-15 | Sequential input and unsubmitted scope/namespace drafts survive URL re-entry | Same browser scenario |
| TC-496-16 | Pointer/keyboard selection produces concise status and readable incoming/outgoing relations | Same browser scenario |
| TC-496-17 | List focus is visible and Enter selects the focused record | Same browser scenario |
| TC-496-18 | A 390 x 844 viewport scrolls horizontally and retains a bounded vertical container | Same browser scenario |
| TC-496-19 | Graph navigation and the explicit-record privacy notice render | Same browser scenario |

## Review-derived regression guards

The case is not complete unless these review outcomes remain true:

- Logical edge identity is case-insensitive while the lowest-ID original relation remains the
  deterministic display record.
- `q` and `kind` are query parameters, use replacement navigation, and do not trigger another graph
  read when only filters change.
- Self-authored filter navigation is recognized exactly and cannot overwrite newer keystrokes or an
  unsubmitted scope/namespace draft. External same-namespace back/forward transitions remain an
  explicit browser-coverage gap below.
- Source review shows that each graph load owns and disposes its cancellation source after the
  awaited read; successors and component disposal cancel but do not prematurely dispose another
  operation's source. There is no deterministic overlapping-load browser test yet.
- Raw exception messages are logged structurally and replaced by a stable user-safe message.
- The complete detail panel is not an ARIA live region. Selection changes are announced only through
  the concise, visually hidden selection status; the separate graph-count summary also uses
  `aria-live`.
- The public `DecisionGraph` component retains a compatible public `DecisionGraphView` parameter.
  Making the records internal would cause inconsistent accessibility; a narrow immutable public DTO
  is a separate refactor, not a silent visibility change.
- Post-reload browser mutations first prove the InteractiveServer circuit is attached. Visibility of
  prerendered HTML alone is not an interactivity signal.
- Geometry assertions query and measure inside one browser task so Blazor hydration cannot replace
  the selected DOM generation between those operations.

## Failability checks

These are recommended mutation checks when changing the projection or URL lifecycle. The latest run
records whether they were actually performed.

- Remove both raw-tag case normalization and case-insensitive comparison; TC-496-01 and TC-496-12
  must fail.
- Remove case normalization from edge identity; TC-496-09 must fail.
- Retain edges whose endpoint was filtered or omitted; TC-496-03/08 must fail.
- Stop prioritizing decisions and neighbors at the node cap; TC-496-05 must fail.
- Rehydrate every form field on self-authored filter navigation; TC-496-15 must fail.
- Restore `aria-live` to the whole details panel; TC-496-16 must fail.
- Measure Playwright element handles across hydration instead of atomically; the connected-node
  diagnostic or geometry guard must expose the stale generation.

## Pass/fail contract

Pass a recorded implementation run only when:

1. The full hard gate, focused projection suite, targeted MCP/memory-service suites, script tests,
   and full browser suite are green.
2. The PR diff contains no schema migration, new graph REST endpoint, MCP contract fork, graph UI
   mutation, GitHub write path, GitHub Action, or new graph dependency.
3. The fixture reaches the UI through real public REST/MCP paths and a real disposable SQLite store.
4. Every review-derived regression above is implemented and its available automated guard is green.
5. Any manual/source-review-only checks and known gaps are explicit in the run record.

Fail the run on any hard-gate failure, nondeterministic output, leaked raw exception, lost URL/draft
state, inaccessible record, unreported truncation, graph write-back, or production-data dependency.

## Cleanup

- The automated browser run owns its app process, uniquely named temporary directory, and SQLite
  file. Its top-level success/error cleanup must terminate the process tree and recursively remove
  the directory.
- Do not seed the user's normal `~/.kanbanflow/kanbanflow.db` for automation.
- A live dogfood namespace is durable by design. Delete it only through the existing memory service
  or MCP tools when the owner explicitly asks; never remove the database to clean one fixture.
- Clear `UI_E2E_PORT` after an overridden local run if the shell will be reused.

## Known coverage limits and follow-ups

- The browser scenario proves a representative Context/Decision/Evidence graph; the focused unit
  suite covers the remaining lanes and pathological sizes.
- The browser fixture asserts one representative observation plus incoming and outgoing relation
  details. It does not directly assert the rendered tags, source, or Created field.
- The 900-record service cap bounds nodes only. `ReadGraphAsync` still materializes every observation
  and relation attached to those returned nodes before the UI applies its 50/320 display caps. A
  relation- or observation-heavy namespace can therefore remain expensive.
- The service deliberately selects the oldest 900 current records. Newer records can be absent, and
  an exactly 900-record result conservatively reports that the source may be incomplete.
- Text search covers record content/tags/source and observation content/source. It does not search
  relation verbs, IDs, confidence, or dates; lane classification is a fixed tag heuristic rather
  than an ontology.
- Empty, no-match, loading, load-error, and individual truncation messages exist, but the latest
  browser run does not fault-inject every state. Cap semantics are unit-tested; the remaining state
  branches are source-reviewed/manual candidates for a future focused browser case.
- Dark-theme and reduced-motion styles are implemented with shared tokens/media queries, but no Axe,
  contrast-ratio, screenshot, or reduced-motion automation is claimed. Browser E2E is Chromium-only.
- Stored content uses normal Razor encoding and no new `MarkupString`; the #496 browser fixture is
  not a hostile-HTML test.
- SVG edges are intentionally hidden from assistive technology and do not carry on-canvas verb
  labels. Direction and relation verbs are available in the selected record's semantic detail list.
- URL state includes namespace and filters, not the selected record or Map/List mode. Reload resets
  selection and view mode. Data updates require manual Refresh; there is no live subscription.
- Invalid scope/kind fallback, external same-namespace back/forward transitions, refresh after a new
  write, overlapping-load cancellation, and a fault-injected safe `ILogger` error path do not have
  deterministic browser cases yet.
- The explicit-record notice is a usage boundary, not redaction, secret scanning, authentication, or
  authorization. The local application remains intentionally unauthenticated.
- Projection view records remain public because the public Blazor component exposes them through a
  public parameter; they currently contain no-tracking but mutable EF entity instances. A narrow
  immutable public DTO remains a possible follow-up.
- The issue plan's broader `npm run test:mcp` smoke is explicitly **Not run** in the latest record.
  Required targeted MCP/memory-service suites and the browser's native `create_relations` path passed;
  a future MCP/API change must run the broader smoke against an isolated service.
- Phase 2 project/run lenses, Phase 3 capture adapters, and Phase 4 live overlays/selective publishing
  remain follow-up work under #336, #276, #323, and #361.
- Merge approval is external to this test case. A green run must not be reported as a merged PR.

## Run evidence policy

Create a new immutable file under `runs/` for every commit presented as verified. Record environment,
commands, counts, failures and fixes, review state, dogfood state, skipped checks, and remaining
limitations. Link the new run from the registry and replace this file's “Latest run” pointer.
