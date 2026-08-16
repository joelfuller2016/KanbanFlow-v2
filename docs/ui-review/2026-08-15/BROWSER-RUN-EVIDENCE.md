# PR #416 exact-head browser run evidence

- **PR:** [#416 — UI modernization evidence and compatibility contract](https://github.com/joelfuller2016/KanbanFlow-v2/pull/416)
- **Captured runtime commit:** `77aa135f435f62933c26d3fb099a580091baf3d4`
- **Branch:** `docs/322-ui-modernization-evidence`
- **Captured UI tree:** `src/KanbanFlow` tree `8d12f9738851b1f10f33f9fb1f95436a80bc341e`
- **Capture window:** approximately 2026-08-15 22:39:30–55 EDT / 2026-08-16 02:39:30–55 UTC
- **Purpose:** manual browser smoke and human-review evidence of the UI actually served by the PR

This run verifies that the pushed evidence PR can be built and served from its isolated worktree.
Because #416 changes documentation and evidence only, the browser shows the current application UI;
the proposed redesign remains a conceptual composition, not an implemented “after” state.

## Runtime and capture identity

| Field | Recorded value |
| --- | --- |
| Application | Exact built DLL from the captured worktree; `ASPNETCORE_ENVIRONMENT=Development` |
| Listener | OS-assigned `http://127.0.0.1:60874` from `--urls http://127.0.0.1:0`; exact process ownership was verified before review |
| Process | Exact listener-owning child PID verified before navigation; the transient PID is not retained as durable identity |
| Database | `KANBANFLOW_DB_PATH` in GUID-named `%TEMP%\kf-ui-pr416-browser-*`; startup reported `source=env`, `existing=false`; SeedData created Sample Project; the normal user database was not used |
| Health | `GET /api/admin/health` returned HTTP 200, `status=ok`, runtime `.NET 10.0.11`; app start `2026-08-16T02:23:51.8614608Z` |
| Operating system | Microsoft Windows 11 Enterprise `10.0.26200` (build 26200) |
| Browser | Microsoft Edge `151.0.4129.86`, launched through Playwright's `msedge` channel |
| Capture driver | Playwright `1.60.0` |
| Viewport | `1440 × 1000`, device scale factor `1`, viewport screenshots |
| Presentation | `en-US`, `America/New_York`, light color scheme, reduced motion requested |
| Fonts | `document.fonts.status=loaded`; computed body stack `"Helvetica Neue", Helvetica, Arial, sans-serif` |
| Interaction | Navigation and task-detail open only; no create, edit, drag, toggle, filter, or delete action |

The visible Edge window was opened on the same listener and showed `KanbanFlow`; the clean viewport
images below were captured in a separate headless Edge context so browser chrome does not obscure
the application.

## Loaded UI evidence

The capture attached page-error, console-error, request-failure, and same-origin HTTP-error
collectors before navigation. All four collections were empty after the complete Projects → board →
task-detail capture flows.

| Check | Result |
| --- | --- |
| Bootstrap stylesheet | Loaded fingerprinted same-origin asset; 1,298 readable rules |
| Application stylesheet | Loaded fingerprinted same-origin asset; 163 readable rules |
| Scoped component stylesheet | Loaded fingerprinted same-origin asset; 18 readable rules |
| JavaScript runtimes | Bootstrap, Sortable, KanbanFlow, and Blazor globals all ready |
| Application style sentinel | `--kf-board-drop-floor: 6rem` |
| Page errors | 0 |
| Console errors | 0 |
| Failed requests | 0 |
| Same-origin responses ≥ 400 | 0 |

## Screenshot manifest

| State | Route and fixture | File | SHA-256 | Bytes | Dimensions |
| --- | --- | --- | --- | ---: | ---: |
| Projects | `/`; Sample Project `1` | `browser-run-77aa135-projects-1440x1000.png` | `02ED96CF33B0104BDAA523FE68FCFEC630E3AD5F521891AE6E6CB453916B1CB2` | 28,871 | 1440 × 1000 |
| Board | `/project/1`; Backlog 2, To Do 1, In Progress 1, Review 0, Done 1 | `browser-run-77aa135-board-1440x1000.png` | `407D178944DFED1FE14A75AEC2B981BAD45E705C7561AD7821B1C6AABE584BDD` | 93,057 | 1440 × 1000 |
| Task detail | `/project/1?task=2`; Registration form `2`, four subtasks | `browser-run-77aa135-task-detail-1440x1000.png` | `E767385AAA6070D8263B4556EB9B4D3CFEB5C3B16B1130B6EAA1272D1F13C4C7` | 107,089 | 1440 × 1000 |

The fresh board capture is byte-for-byte identical to `current-board.png`, independently confirming
that the original board evidence still represents the UI served by this PR.

### Projects

![PR #416 Projects page at 1440 by 1000](browser-run-77aa135-projects-1440x1000.png)

### Board

![PR #416 project board at 1440 by 1000](browser-run-77aa135-board-1440x1000.png)

### Task detail

![PR #416 Registration form task detail at 1440 by 1000](browser-run-77aa135-task-detail-1440x1000.png)

## Interpretation and limitations

These images are manual browser-smoke and review evidence. They are **not** deterministic geometry,
pixel-diff, Axe, performance, or accessibility baselines; Phase 0A remains required before any such
claim. The evidence covers one desktop viewport only. It does not cover tablet/phone layouts, zoom,
forced colors, or a full reduced-motion interaction audit.

The proposed composition is `1505 × 1045`, while these current-state images are `1440 × 1000`, so
they are not pixel-comparable before/after pairs. The proposed mock's authoring method, tool, prompt,
and source-reference record were not retained; no provenance is inferred. Server stdout/stderr and
the disposable database file were not committed. The exact environment facts above were observed
during the run, and the three committed PNG hashes preserve its reviewable outputs.

Committing this evidence necessarily advances the PR head beyond the captured runtime commit. The
follow-up is valid only while its final diff changes no runtime inputs and the final
`src/KanbanFlow` tree remains `8d12f9738851b1f10f33f9fb1f95436a80bc341e`.
