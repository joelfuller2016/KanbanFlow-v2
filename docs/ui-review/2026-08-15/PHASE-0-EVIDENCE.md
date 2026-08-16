# Phase 0 evidence and compatibility contract

- **Parent epic:** [#322](https://github.com/joelfuller2016/KanbanFlow-v2/issues/322)
- **Evidence PR base:** `3245d16c6a937d051fff8e7e111d1639b6364f16`
- **Capture source:** `652c60df67c74279f3b66e45202ba351b528faf4`
- **Decision state:** proposed for maintainer approval by this evidence PR
- **Application-code scope:** none

This document publishes the evidence required by Phase 0 of the
[reviewed implementation plan](IMPLEMENTATION-PLAN.md). Merging the evidence PR approves the
dated modernization direction and the contracts below. It does not claim that a geometry,
accessibility, performance, or screenshot baseline is trustworthy; Phase 0A must land first.

## Provenance

The evidence branch was created from a fresh `origin/master` worktree at the exact SHA above. Six
reviewed source files—the original plan, review README, and four images—were copied from the
canonical checkout by explicit filename. The selector inventory was generated from the base as the
seventh manifest artifact. The two capture and base revisions have no differences under
`src/KanbanFlow/Components`, `src/KanbanFlow/wwwroot`, `package.json`, or `scripts/ui-e2e.mjs`; PR
#415 has no UI, evidence, or harness changes.

| File | Reviewed source SHA-256 | PR file SHA-256 | Bytes | Dimensions |
| --- | --- | --- | ---: | ---: |
| `IMPLEMENTATION-PLAN.md` | `F1F184FB186CCD6481B7862AD7BD7830818B10F7BBB4DC0C476FF4E2D9171EFA` | `DC02C20797B2B50AAD039F7BEE482A3FD967F1F1C924B223DDD2A40964AA57AC` | 70,273 | — |
| `README.md` | `AFC7C88510B9D9AE124475021DADBECCC998684D955424F8F5ACC28F1E41416D` | `F4433F1EF52D675CB18866D14089B72A4DC550633673217B46A70183123FE287` | 9,720 | — |
| `SELECTOR-INVENTORY.md` | Generated from the exact base | `35EC34E14E04F38197B01D785205BA44C509170E44DE7F24E85D588100B5FDF8` | 26,728 | — |
| `current-projects.png` | `391A17905655C5D847604D42A072A20A864C766E135928F6F2AF629BD78F03DF` | `391A17905655C5D847604D42A072A20A864C766E135928F6F2AF629BD78F03DF` | 28,865 | 1440 × 1000 |
| `current-board.png` | `407D178944DFED1FE14A75AEC2B981BAD45E705C7561AD7821B1C6AABE584BDD` | `407D178944DFED1FE14A75AEC2B981BAD45E705C7561AD7821B1C6AABE584BDD` | 93,057 | 1440 × 1000 |
| `current-task-detail.png` | `A08812A6B6F3CD061B6324ACBBD23C148546DF87C9DC9EA8F474C051227557EB` | `A08812A6B6F3CD061B6324ACBBD23C148546DF87C9DC9EA8F474C051227557EB` | 107,862 | 1440 × 1000 |
| `proposed-board-redesign.png` | `6100F7195B42842107F775C4E7FEBD58E16EE8AE9827FD5D4D75E871320C5684` | `6100F7195B42842107F775C4E7FEBD58E16EE8AE9827FD5D4D75E871320C5684` | 1,280,688 | 1505 × 1045 |

`README.md` and `IMPLEMENTATION-PLAN.md` were copied and hash-verified first, then reconciled in this
PR so the published contract is internally consistent and reviewable. Their source hashes remain in
the table; their PR-file hashes identify the corrected versions. All four images remain
byte-identical to their source copies. The selector inventory was generated directly from the base
revision. The plan records the images as local/untracked in its historical planning context and
identifies this evidence PR as their publication step.

The original current-state screenshot files have raster dimensions of 1440 × 1000 and the proposed
composition is 1505 × 1045. The original ad-hoc capture did not preserve the browser build, device
scale factor, font manifest, route/record-ID manifest, server log, or database manifest; those
fields are explicitly **not captured**, not inferred. The conceptual mock's authoring method, tool,
prompt, and source-reference record were not retained either. This is why the original images remain
review evidence rather than baselines.

### Exact-head browser smoke evidence

After the original bundle was published, PR #416 commit
`77aa135f435f62933c26d3fb099a580091baf3d4` was built and served from its isolated worktree. The
three clean viewport captures and [browser-run record](BROWSER-RUN-EVIDENCE.md) below are a separate
exact-head smoke-evidence set; they do not replace the original design-review files.

| File | SHA-256 | Bytes | Dimensions |
| --- | --- | ---: | ---: |
| `BROWSER-RUN-EVIDENCE.md` | `4BE082CEBFB9030AFEE27D01058C98EFB022F8804EA50F510DB778AD15988DAF` | 5,659 | — |
| `browser-run-77aa135-projects-1440x1000.png` | `02ED96CF33B0104BDAA523FE68FCFEC630E3AD5F521891AE6E6CB453916B1CB2` | 28,871 | 1440 × 1000 |
| `browser-run-77aa135-board-1440x1000.png` | `407D178944DFED1FE14A75AEC2B981BAD45E705C7561AD7821B1C6AABE584BDD` | 93,057 | 1440 × 1000 |
| `browser-run-77aa135-task-detail-1440x1000.png` | `E767385AAA6070D8263B4556EB9B4D3CFEB5C3B16B1130B6EAA1272D1F13C4C7` | 107,089 | 1440 × 1000 |

The run records Microsoft Edge `151.0.4129.86`, Playwright `1.60.0`, device scale factor `1`, the
exact routes and seeded IDs, a fresh environment-selected temporary database, an OS-assigned
loopback listener, the real `/api/admin/health` response, loaded styles and JavaScript runtimes, and
zero collected page, console, request, or same-origin HTTP errors. A visible Edge window confirmed
the same listener; the committed images came from a separate headless Edge context without browser
chrome. The captured `src/KanbanFlow` tree is
`8d12f9738851b1f10f33f9fb1f95436a80bc341e`; the evidence-only follow-up must leave that tree and
all runtime inputs unchanged.

This remains manual browser-smoke and human-review evidence, not a deterministic geometry, pixel,
Axe, performance, or accessibility baseline. Phase 0A remains required.

## Decision register

| Area | Phase 0 decision |
| --- | --- |
| Board height | The board work surface fills the remaining viewport. Lane card stacks remain content-height, preserving PR #335's removal of the large one-card void. Empty and short lanes retain a tested drop target. |
| Detail breakpoint | At `1280px` and wider, task detail is a modeless complementary pane sized `clamp(32.5rem, 42vw, 35rem)`. Below `1280px` it is a modal end drawer; below `576px` it is a full-width modal sheet. |
| Outline with detail | From `1280px` through `1599px`, opening detail auto-collapses the outline and leaves a persistent reopen control. At `1600px` and wider it remains open only when the board still keeps at least `640px` and two complete `220px` lanes. |
| Projects grid | Conditional. Re-run completed issue #281's one- and twenty-project layout checks after Phase 0A, and add the plan's separate three-project review state. Change the grid only if measured evidence proves a remaining gap. |
| AI and Requirements | Proposed pending written approval linked in both #279 and #322: preserve completed issue #58's compact/collapsed default and present these as clear on-demand utilities rather than permanent vertical chrome. Approval intentionally supersedes original #322 step 10 and #279 AC3; #279 retains agent-presence semantics while #322 owns disclosure and layout. No later PR may treat this split as active before both links exist. |
| Palette | Preserve current pixels through Phase 0A. Use issue #370's recommended Option A as the interim rule: stock Bootstrap primary with white passes AA at `4.5008:1`; do not restyle the app merely for margin. Phase 1 must add the reproducible guard or record a separately approved palette decision before tokens freeze. |
| Spacing and radii | Use the `4, 8, 12, 16, 24, 32px` spacing scale, restrained neutral borders/shadows, and `8–12px` radii. |
| Typography and targets | Retain the system/Bootstrap type stack initially. Persistent labels remain approximately `12–14px` or larger. Pointer targets are at least `24px`; frequent actions target at least `36px`, with `44px` preferred for primary touch actions. |
| Evidence status | The images are human-review evidence and a composition target, not pixel-perfect baselines. DOM, behavior, accessibility, geometry, persistence, and asset checks become hard gates after Phase 0A. |

## Issue ownership and sequencing

| Issue | Current issue scope | Proposed #322 boundary |
| --- | --- | --- |
| [#109](https://github.com/joelfuller2016/KanbanFlow-v2/issues/109) | Task-detail dependencies and related-requirement data | #322 owns responsive presentation and hierarchy only; it does not duplicate data work. |
| [#110](https://github.com/joelfuller2016/KanbanFlow-v2/issues/110) | Claimed-by card signal | #279 owns the agent-presence semantics; #322 places the existing signal in the card hierarchy. |
| [#111](https://github.com/joelfuller2016/KanbanFlow-v2/issues/111) | Project progress summary | #279 owns agent-context meaning; #322 integrates the existing summary into compact header composition. |
| [#113](https://github.com/joelfuller2016/KanbanFlow-v2/issues/113) | Disabled-AI affordance | #322 may compact the utility shell but does not change button availability or semantics. |
| [#114](https://github.com/joelfuller2016/KanbanFlow-v2/issues/114) | Per-card actions inside the AI panel | #322 may compact the panel container but does not duplicate action behavior. |
| [#116](https://github.com/joelfuller2016/KanbanFlow-v2/issues/116) and [#306](https://github.com/joelfuller2016/KanbanFlow-v2/issues/306) | Relative timestamps, absolute hover text, and UTC correctness | #322 consumes the coordinated timestamp treatment; it does not implement a competing formatter or UTC fix. |
| [#167](https://github.com/joelfuller2016/KanbanFlow-v2/issues/167) | Blazor detail state/identity, transient-action errors, and JS correctness | #322 owns the visible Saving/Saved/Error presentation and responsive layout; mechanics are coordinated with #167. |
| [#328](https://github.com/joelfuller2016/KanbanFlow-v2/issues/328) | Completed task activity timeline | Reuse the shipped timeline; do not rebuild activity under #109 or #322. |
| [#279](https://github.com/joelfuller2016/KanbanFlow-v2/issues/279) | Agent-presence language across surfaces; its live AC3 also proposes expanding AI by default | Remains a peer epic for agent semantics. The proposed compact disclosure/layout split is pending written approval in both #279 and #322 and is not active merely because this evidence PR exists. |
| [#74](https://github.com/joelfuller2016/KanbanFlow-v2/issues/74) | Completed modal accessibility contract | Remains authoritative below `1280px`. Desktop intentionally uses non-modal `aside`/complementary semantics without scrim, inert board, focus trap, or `aria-modal`. |
| [#119](https://github.com/joelfuller2016/KanbanFlow-v2/issues/119) and [#342](https://github.com/joelfuller2016/KanbanFlow-v2/issues/342) | Empty-lane affordance and remaining #342 browser-coverage gaps. Geometry, containment, and the `/ideas` add affordance are covered; placeholder-drag behavior, complete error collection, explicit empty-column drops on both boards, and complete parentage coverage remain. | Must be resolved or explicitly sequenced before Phase 5 changes empty-lane presentation or interaction. |
| [#314](https://github.com/joelfuller2016/KanbanFlow-v2/issues/314), [#351](https://github.com/joelfuller2016/KanbanFlow-v2/issues/351), [#352](https://github.com/joelfuller2016/KanbanFlow-v2/issues/352), [#353](https://github.com/joelfuller2016/KanbanFlow-v2/issues/353) | UI harness truthfulness, process/port isolation, build isolation, and asset proof | Phase 0A closes or coordinates these before any visual baseline. |
| [#346](https://github.com/joelfuller2016/KanbanFlow-v2/issues/346) and [#370](https://github.com/joelfuller2016/KanbanFlow-v2/issues/370) | Theme-ready surfaces and primary-pairing policy | Phase 1 coordinates token and contrast work; this PR changes no pixels. |

The live #279 body still lists #109, #110, #111, #113, #114, and #116 as children. This table
proposes the cross-epic split; it remains pending until written approval is linked in both #279 and
#322 so neither issue silently retains contradictory acceptance criteria.

No new child issues are created by the evidence PR. The parent and adjacent issues remain the
canonical ownership ledger until Phase 0 review identifies a genuinely unowned implementation gap.

## Compatibility contract

Every existing `data-testid` in the proposed component boundary is frozen. The exact declaration,
SortableJS, JavaScript interop, callback, and shared `/ideas` snapshot is recorded in
[SELECTOR-INVENTORY.md](SELECTOR-INVENTORY.md). Later PRs may add selectors, but they may remove or
rename a frozen selector only with an explicit migration test and PR callout.

The load-bearing DOM contracts are:

- `.board-column-cards[data-status]` contains only direct draggable roots with a positive
  `data-task-id`; the empty state remains a sibling.
- `.feature-tasks[data-feature-id]` may contain expanded non-task siblings, but only direct roots
  carrying `data-task-id` are draggable.
- SortableJS continues to use `draggable: '[data-task-id]'` and sends the next task identity, not a
  rendered-child index.
- `OnCardMoved(taskId, newStatus, beforeTaskId)` and
  `OnTaskReordered(taskId, newFeatureId, beforeTaskId)` retain their signatures and identity-
  anchored semantics.
- `/ideas` shares `KanbanBoard`, `KanbanColumn`, SortableJS initialization, and dialog behavior;
  route-specific task-board styling must not leak into it.

## Phase 0A gate before visual work

The next PR is the first implementation PR. It must provide a unique loopback port, fresh database,
isolated worktree identity, unconditional terminal summary, static-asset and computed-style proof,
global page/console/request failure handling, failure artifacts, serial non-shared builds, an Axe
serious/critical gate, and deterministic `capture`/`serve` commands. At evidence base
`3245d16c6a937d051fff8e7e111d1639b6364f16`, the script contains 48 named UI checks (one pure
helper guard and 47 browser scenarios); the current pass count must be measured again on the final
pushed evidence head rather than copied from prose.

The evidence run reproduced the pre-existing API smoke failure tracked by
[#338](https://github.com/joelfuller2016/KanbanFlow-v2/issues/338): recall currently accepts
`k=0` although the smoke contract expects validation failure. This docs-only PR changes no API
surface and does not call that red master baseline green. The overall Phase 0 green-baseline exit
remains pending #338 or an explicit maintainer decision to narrow that gate.

The following decisions clarify ambiguous cumulative/runbook wording in the copied plan and are
authoritative for Phase 0A:

- Resolve the review ref to one exact SHA before creating a worktree, create the worktree from that
  SHA, assert that `HEAD` matches, and remove the detached worktree from a `finally` path. Preserve
  it only when a failed run explicitly reports the diagnostic location.
- With `UI_E2E_PORT` unset, empty, or `0`, launch the child on an OS-assigned loopback port; a valid
  explicit `1..65535` override wins and must bind exactly or fail immediately. Prove the recorded
  child PID owns either listener. Isolate both `bin` and `obj` per bare run, prove two independent
  runs can coexist, and require startup to report the expected environment and database identity
  before tests proceed.
- Capture `verify.ps1` output and exit status without allowing PowerShell's native-error preference
  to skip result classification. A hard build/test failure remains red; an advisory exit is reported
  distinctly and is never silently called green.
- Reset SeedData's Sample Project before deterministic capture fixtures. Capture may create Home
  empty/one/three/many states sequentially; serve mode exposes one Home state plus distinct
  empty/normal/stress project-board routes taken from the generated fixture manifest, never an
  assumed `/project/1`, and not several simultaneous versions of `/`. Ideas uses its one canonical
  `/ideas` route with manifest-defined empty-then-populated reset/state steps, not two simultaneous
  Ideas routes.
- Phase 0A gates current application states and supplies the shared runner, assets, failure handling,
  artifacts, Axe baseline, and capture/serve foundation. Filters chips, Move to, modeless detail,
  save/error feedback, landmarks, and other future states are added by their owning visual PRs.
- Always preserve complete app stdout/stderr and a manifest. Successful capture manifests contain a
  complete record for every planned screenshot. Failure manifests preserve completed screenshot
  records and add explicit unavailable entries—with reasons and null hash/byte/dimension fields—for
  every unproduced planned capture or browser/page artifact. Add screenshot, DOM, browser version,
  and page diagnostics whenever a browser/page exists; pre-launch failures cannot provide them.
- A deliberate teardown failure produces a nonzero harness result and a preserved diagnostic path.
  This is the chosen refinement of #352's older cleanup wording.
- Discover and validate every critical fingerprinted same-origin asset from rendered DOM: Bootstrap
  CSS/JS, `app.css`, scoped `KanbanFlow.styles.css`, SortableJS, `kanbanflow.js`, and Blazor. Prove
  expected MIME/nonempty-or-cached-loaded state, readable app/Bootstrap/scoped style rules,
  app/framework/scoped computed sentinels, and Bootstrap/Sortable/KanbanFlow/Blazor runtime
  readiness before trusting behavior or geometry.
- Keep the UI/Axe suite as an explicit separate local hard gate until a maintainer deliberately
  expands the scheduled PowerShell build/test job and its browser dependencies.

Until that PR lands, do not record screenshot pixels, geometry, performance, or Axe output as an
approved baseline.
