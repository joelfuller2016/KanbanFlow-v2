# KanbanFlow v2 — Requirements

> **Version:** 2.0 | **Date:** 2026-05-09 | **Status:** Active
>
> Reframes v1 (2026-04-12) around the AI-first vision documented in [README.md](../README.md). v1 was organised P0/P1/P2 by entity. v2 is organised by **capability** — what an AI agent must be able to do — because that's the system's actual shape.
>
> The README is the canonical short version of this document. This file is the expanded reference: full data-model field specs, per-capability acceptance criteria, current implementation status, and the gap list to "vision-complete".

---

## 1. Vision

KanbanFlow v2 is a **project knowledge graph** with three jobs: it **documents** projects (every entity is structured documentation), **directs** AI agents (given a project, says "what's next, with the context to act"), and **records** every change in an immutable audit trail. The kanban UI is one view of the graph, optimised for humans. The REST API and MCP server are the views optimised for AI agents.

This is not a kanban app with an API bolted on. The API is the product. The kanban is a side effect of the data model.

**See README §"Vision"** for the short version.

---

## 2. Tech stack

| Layer | Choice |
|---|---|
| Framework | .NET 10, Blazor Server (Interactive Server render mode) |
| Database | SQLite via EF Core 10.0.5 |
| CSS | Bootstrap 5.3 |
| API | Minimal APIs |
| Drag-and-drop | SortableJS via JS interop |
| Markdown | Markdig (referenced; not yet wired — see R-4.2) |
| MCP | Node 18+ stdio JSON-RPC wrapper (`scripts/mcp-server.mjs`) |
| Auth | None — single-trust-boundary tool |

---

## 3. Data model

```
Project
  ├── name (required, ≤200)
  ├── description (markdown body)
  ├── goals (markdown body)
  ├── readme (markdown body — primary documentation)
  ├── createdAt, updatedAt
  ├── Requirement (priority P0/P1/P2, status, optional Feature/Task link)
  ├── Feature (recursive — features contain features via parentFeatureId)
  │     ├── name, description (markdown), sortOrder
  │     └── TaskItem
  │           ├── name, description (markdown)
  │           ├── status (Backlog | Todo | InProgress | Review | Done)
  │           ├── priority (Low | Medium | High | Critical)
  │           ├── assignee, claimedBy, claimedAt, completedAt, sortOrder
  │           ├── AcceptanceCriterion (text, met, sortOrder)
  │           ├── Subtask (recursive — subtasks contain subtasks via parentSubtaskId)
  │           ├── TaskComment (Note | Decision | Handoff | Verification | system events)
  │           └── TaskDependency (DependsOn | Blocks; cycle-protected DAG)
  └── ActivityEvent (append-only audit log; survives entity deletion)
```

Every text field on every entity is documentation that agents can retrieve, summarise, or update.

### 3.1 Project

| Field | Type | Required | Notes |
|---|---|---|---|
| Id | int | yes (auto) | |
| Name | string | yes | ≤200 chars |
| Description | string | no | markdown body |
| Goals | string | no | markdown body — high-level objectives |
| Readme | string | no | markdown body — primary documentation; agents read+update this |
| CreatedAt | DateTime | yes | |
| UpdatedAt | DateTime | yes | |

### 3.2 Feature

| Field | Type | Required | Notes |
|---|---|---|---|
| Id | int | yes (auto) | |
| ProjectId | int | yes | FK → Project (cascade delete) |
| ParentFeatureId | int? | no | recursive; null = top-level; cycle-protected |
| Name | string | yes | ≤200 chars |
| Description | string | no | markdown body |
| SortOrder | int | yes | within parent feature |
| CreatedAt, UpdatedAt | DateTime | yes | |

### 3.3 TaskItem

| Field | Type | Required | Notes |
|---|---|---|---|
| Id | int | yes (auto) | |
| FeatureId | int | yes | FK → Feature (cascade delete) |
| Name | string | yes | ≤500 chars |
| Description | string | no | markdown body |
| Status | enum | yes | `Backlog | Todo | InProgress | Review | Done` |
| Priority | enum | yes | `Low | Medium | High | Critical` |
| Assignee | string? | no | free-text label |
| ClaimedBy | string? | no | claim system actor; case-insensitive comparison |
| ClaimedAt | DateTime? | no | set on successful claim |
| SortOrder | int | yes | within feature + status column |
| CreatedAt, UpdatedAt | DateTime | yes | |
| CompletedAt | DateTime? | no | set when Status → Done; cleared otherwise |

### 3.4 Subtask

| Field | Type | Required | Notes |
|---|---|---|---|
| Id, TaskItemId | int | yes | FK → TaskItem (cascade) |
| ParentSubtaskId | int? | no | recursive; null = top-level; cycle-protected |
| Text | string | yes | |
| Completed | bool | yes | |
| SortOrder | int | yes | |
| CreatedAt | DateTime | yes | |
| CompletedAt | DateTime? | no | |

### 3.5 AcceptanceCriterion

First-class checklist items per task. Distinct from `description` text bullets.

| Field | Type | Required |
|---|---|---|
| Id, TaskItemId | int | yes |
| Text | string | yes |
| Met | bool | yes |
| SortOrder | int | yes |
| CreatedAt, UpdatedAt | DateTime | yes |

### 3.6 Requirement

P0/P1/P2 trackable requirements scoped to a project. Optionally linked to a Feature or Task.

| Field | Type | Required | Notes |
|---|---|---|---|
| Id, ProjectId | int | yes | |
| Priority | enum | yes | `P0 | P1 | P2` |
| Status | enum | yes | open/met/dropped (see code) |
| Name | string | yes | |
| Description | string | no | markdown |
| FeatureId | int? | no | optional link |
| TaskItemId | int? | no | optional link |
| SortOrder | int | yes | |

### 3.7 TaskDependency

DAG; cycle-protected; project-scoped.

| Field | Type | Required | Notes |
|---|---|---|---|
| Id | int | yes | |
| TaskItemId | int | yes | the dependent task |
| DependsOnTaskItemId | int | yes | the prerequisite task |
| ProjectId | int | yes | denormalised for cheap project queries |
| Type | enum | yes | `DependsOn | Blocks` |
| CreatedAt | DateTime | yes | |

### 3.8 TaskComment

Mixed system + user comments on a task.

| Field | Type | Required |
|---|---|---|
| Id, TaskItemId | int | yes |
| Text | string | yes |
| Author | string | yes |
| Type | enum | yes |
| CreatedAt | DateTime | yes |

`CommentType` values:
- **System types** (only the API/service layer may create): `Created`, `StatusChange`, `FeatureChange`, `Claimed`, `Released`, `Deleted` (and similar)
- **User-creatable types** (whitelisted): `Note`, `Decision`, `Handoff`, `Verification`

The whitelist is enforced server-side in `AllowedUserCommentTypes` (`ApiEndpoints.cs`).

### 3.9 ActivityEvent

Append-only audit log. **Survives entity deletion** (see R-7.3).

| Field | Type | Required | Notes |
|---|---|---|---|
| Id | int | yes | |
| ProjectId | int | yes | denormalised; **not a navigation property** — survives project delete |
| EntityType | string | yes | "Task", "Requirement", "Project", "Feature" — see R-5.5 about typing |
| EntityId | int | yes | |
| Actor | string? | no | free-text label of who did it |
| EventType | string | yes | "TaskCreated", "TaskStatusChanged", "TaskClaimed", etc. |
| BeforeJson | string? | no | structured snapshot of relevant fields before the change |
| AfterJson | string? | no | structured snapshot after |
| Message | string? | no | human-readable summary |
| OccurredAt | DateTime | yes | UTC |

---

## 4. Capabilities (the requirements)

Requirements are organised by **capability**. Every capability must be reachable from both REST and MCP. Status legend:

- ✅ implemented
- ⚠️ partially implemented (with caveat)
- ❌ not implemented (gap)

### C-1. Read everything

The agent can navigate and read every entity in the graph.

| # | Requirement | Status |
|---|-------------|--------|
| R-1.1 | List + get for every entity: project, feature, task, subtask, acceptance criterion, requirement, dependency, comment, activity event. | ✅ |
| R-1.2 | `GET /api/projects/{id}/context` returns the entire project graph in one denormalised JSON payload optimised for AI consumption: features (recursive), tasks, subtasks (recursive), criteria, comments, requirements, dependencies, recent activity. | ✅ |
| R-1.3 | Activity is queryable per project (`GET /api/projects/{id}/activity`) and per task (`GET /api/tasks/{id}/activity`), time-ordered, with structured `before`/`after` snapshots. | ✅ |
| R-1.4 | All list endpoints support `?limit=&offset=` paging. | ❌ Only `/activity` clamps `limit` to 500. Everything else is unbounded. |
| R-1.5 | Search endpoint: `GET /api/projects/{id}/search?q=...` returns matches across project descriptions, task descriptions, comments, requirements. | ❌ Not implemented. |
| R-1.6 | Per-task context endpoint: `GET /api/tasks/{id}/context` returns task + criteria + subtasks recursively + dependencies (resolved to names + status) + recent comments + related requirements + recent activity, in one call. | ⚠️ `GET /api/tasks/{id}` returns subtasks + criteria but not comments/deps/activity. Consolidate. |

**Acceptance criteria:**
- Every entity has a working `GET /api/...` route.
- `/context` and `/board` round-trip through `smoke-api.ps1` with assertions on returned shape.
- `next-tasks` returns priority-sorted results (see C-3).

### C-2. Write everything

The agent can author, modify, and remove every entity. Mutations preserve audit integrity.

| # | Requirement | Status |
|---|-------------|--------|
| R-2.1 | Every entity has full REST CRUD: list, get, create, update, delete. | ✅ 53 endpoints in `ApiEndpoints.cs`. |
| R-2.2 | Every REST capability has at least one MCP tool. | ❌ MCP exposes 11 of ~40 needed tools — see §6. |
| R-2.3 | Mutations through the UI go through the **same shared service layer** as REST mutations, so audit events, claim semantics, and validation are produced once and applied everywhere. | ❌ Issue #88 — UI components write directly to EF, bypassing audit. **Highest-priority refactor.** |
| R-2.4 | All entity mutations produce an `ActivityEvent` row. | ✅ for REST (9 emission sites in `ApiEndpoints.cs`); ❌ for UI (R-2.3). |
| R-2.5 | Project boundary integrity: a feature cannot have a parent feature in another project; a task cannot be reassigned across projects; a requirement cannot link to a feature/task in another project. | ❌ Issues #82, #83 — both still open. |
| R-2.6 | Cycle protection on feature trees, subtask trees, and dependency graphs. | ✅ `WouldCreateFeatureCycle`, `WouldCreateSubtaskCycle`, `WouldCreateTaskDependencyCycle` (`ApiEndpoints.cs:2014-2080`). |
| R-2.7 | Enum validation rejects undefined values: `Status: "99"` returns 422. UI int-to-enum casts also validate. | ❌ Issue #89 — 14 API call sites use `Enum.TryParse` (accepts numeric strings); UI does raw `(WorkStatus)int` casts. |

**Acceptance criteria:**
- Smoke suite covers create + update + delete for every entity (R-1.4 of `smoke-api.ps1` extension).
- Concurrent UI + API mutation produces one `ActivityEvent` row per logical change (regardless of source).
- Cross-project link attempts return 422.

### C-3. Direct agents to the next step, with full context

Given a project, the API tells an agent what to work on next, with reasoning and the context needed to act.

| # | Requirement | Status |
|---|-------------|--------|
| R-3.1 | `GET /api/projects/{id}/next-tasks?actor={name}&limit={n}` returns priority-sorted, dependency-aware next tasks. | ✅ |
| R-3.2 | Each recommendation includes a `reason` string ("Top because: P0 requirement linked, no open blockers, oldest unclaimed"). | ✅ `BuildNextTaskReason` (`ApiEndpoints.cs:2280`). |
| R-3.3 | Tasks blocked by incomplete dependencies are excluded from `next-tasks` results. | ✅ |
| R-3.4 | Per-task context endpoint (see R-1.6) returns the bundle an agent needs without further round-trips. | ⚠️ Partial. |
| R-3.5 | `next-tasks` accepts `actor` and de-prioritises tasks claimed by other actors. | ✅ |
| R-3.6 | Recommendation algorithm is documented in `docs/` so agents (and humans) can predict ranking. | ❌ Lives only in `BuildNextTaskReason` source. |

**Ranking rules (current):**
1. Tasks with incomplete acceptance criteria (the closest to done)
2. Tasks linked from open P0 requirements
3. Tasks with no open blockers
4. Oldest unclaimed (FIFO bias)

**Acceptance criteria:**
- `next-tasks` is non-empty for any project with ≥1 unblocked task.
- Reasons are non-empty strings tying back to specific ranking signals.
- Blocked tasks never appear at the top of the list.

### C-4. Add and update documentation

Every textual field is documentation. Agents update README, goals, descriptions, comments, and decisions.

| # | Requirement | Status |
|---|-------------|--------|
| R-4.1 | Project carries first-class documentation fields: `name`, `description`, `goals`, `readme`. The `readme` field is intended as the project's living documentation. | ✅ schema; ⚠️ no MCP tool to update README/goals yet. |
| R-4.2 | Markdown is rendered server-side via Markdig with HTML sanitisation. Renderable on any text field tagged `markdown` (project description/goals/readme, feature description, task description, comment text). | ❌ Markdig is referenced in `KanbanFlow.csproj:11` but never imported. README/description currently render as plaintext. |
| R-4.3 | Comments support typed authoring: `Note`, `Decision`, `Handoff`, `Verification` (user-creatable) and system types (`Created`, `StatusChange`, etc.). The whitelist enforces the user/system split. | ✅ `AllowedUserCommentTypes` (`ApiEndpoints.cs`). |
| R-4.4 | Acceptance criteria are first-class checklist items, not embedded in description text. | ✅ |
| R-4.5 | Activity log records both structured `BeforeJson`/`AfterJson` snapshots and a human-readable `Message`. Both are exposed to AI for summarisation. | ✅ |
| R-4.6 | Adding a `Decision` or `Handoff` comment is a single MCP tool call. | ✅ `add_task_comment` exists. |

**Acceptance criteria:**
- An agent can create a project, populate README + goals, add features and tasks, record a Decision comment, and link a Handoff to another agent — all via MCP, in <10 tool calls.

### C-5. Be self-describing

Agents discover the API and tool surface without reading source.

| # | Requirement | Status |
|---|-------------|--------|
| R-5.1 | API publishes an OpenAPI document at `/openapi.json` covering every endpoint, request body, and response shape. | ❌ Not wired (`Microsoft.AspNetCore.OpenApi` not added). |
| R-5.2 | Response types are strongly typed (records), not anonymous. | ❌ ~25 distinct anonymous shapes inline in `ApiEndpoints.cs`. |
| R-5.3 | Each MCP tool description explains purpose, parameters, and the typical follow-up after calling it. | ⚠️ Tool descriptions exist but are terse. |
| R-5.4 | This requirements doc + README list every endpoint, kept in sync with code. | ⚠️ README has the full table; the legacy v1 doc drifted out of sync. v2 (this doc) is fresh. Plan: regenerate from OpenAPI once R-5.1 lands. |
| R-5.5 | Errors use a uniform shape: `{ "message": string, "errors"?: Record<string, string[]>, "details"?: Record<string, unknown> }`. | ⚠️ Inconsistent — some endpoints add bonus fields ad-hoc. |
| R-5.6 | `EntityType` discriminator on `ActivityEvent` is constant-typed, not free-form string. | ❌ Currently string literals (`"Task"`, `"Requirement"`); typo-prone. Add `static class ActivityEntityTypes`. |

**Acceptance criteria:**
- An agent can fetch `/openapi.json`, parse it, and call any endpoint without source-code access.
- Error responses are deterministic enough to handle without per-endpoint special cases.

### C-6. Be operationally simple

Zero setup. Single SQLite file. Cross-platform. CI-tested.

| # | Requirement | Status |
|---|-------------|--------|
| R-6.1 | Zero setup: `dotnet run` produces a working app with seed data on .NET 10. | ✅ |
| R-6.2 | Single SQLite file; portable; no Docker, no migrations service. | ✅ |
| R-6.3 | Smoke suite runs in <60 seconds. | ✅ ~40s, asserts ~50 HTTP calls. |
| R-6.4 | Schema evolves via EF Core migrations, not hand-rolled `ALTER TABLE` patchers. | ❌ Issue #86 — `EnsureCreated()` + four `Ensure*` patchers in `Program.cs`, including a transactional FK-rebuild dance. |
| R-6.5 | A second smoke script (Node-based, cross-platform) lets non-Windows contributors verify the API. | ❌ Only PowerShell smoke exists. |
| R-6.6 | CI runs build + smoke on every PR. | ❌ No `.github/workflows/`. |
| R-6.7 | Dev URL is consistent across `launchSettings.json`, README, and MCP defaults. | ❌ Three URLs in use today (`:5148`, `:5123`, `:5043`). Standardise on `:5123`. |

**Acceptance criteria:**
- `git clone && dotnet run` works on a fresh machine.
- A non-Windows contributor can verify the API via Node smoke.

### C-7. Be honest about the audit trail (non-negotiable for AI accuracy)

The audit log is the single source of truth for "what changed when." This must hold without exceptions.

| # | Requirement | Status |
|---|-------------|--------|
| R-7.1 | The audit log is the single source of truth for "what changed when." No mutation path is allowed to skip it. | rule |
| R-7.2 | UI mutations and API mutations produce identical activity events. | ❌ Issue #88 — UI bypasses. Fixed by R-2.3 (service layer). |
| R-7.3 | The audit log survives entity deletion. The activity-read endpoint allows reading after the project no longer exists. | ⚠️ ✅ — `ActivityEvent` deliberately ignores the project FK (`AppDbContext.cs:64-71`). ❌ — `GET /api/projects/{id}/activity` hard-fails on missing project (issue #85, P2). |

**Acceptance criteria:**
- Smoke suite asserts: a status change via the UI produces an `ActivityEvent` with the same `EventType` and snapshot fields as a status change via the API.
- After deleting a project, its activity rows are still queryable (post-#85 fix).

---

## 5. Out of scope (deliberate exclusions)

These aren't oversights. They're explicit non-goals for v2:

| Excluded | Reason |
|---|---|
| Authentication / authorisation | Single-trust-boundary tool. Agent + owner on one machine. |
| Multi-tenancy / per-project ownership | One DB, one user. |
| HTTPS, security headers, rate limiting | **Open access by design** — trust the network (single-user local tool). No enforced loopback / CORS / source-IP restrictions; hardening deferred to #84. |
| Real-time human collaboration (multiple humans on the same board) | Single-user UX; auto-poll or manual refresh. |
| Notifications (email, Slack) | The agent is the integration. |
| External integrations (GitHub, Jira, Linear) | Possibly a later module; not v2. |
| Mobile-first UI | Desktop primary; existing responsive work is a courtesy, not a target. |
| LLM-driven planning features (`generate_task_outline` etc.) | The agent does this; the system records the outcome via existing CRUD. |

---

## 6. API + MCP surface

### 6.1 REST endpoints (53 total)

```
/api/projects                               GET (list), POST (create)
/api/projects/{id}                          GET, PUT, DELETE
/api/projects/{id}/context                  GET (full graph for AI)
/api/projects/{id}/board                    GET (tasks by status column)
/api/projects/{id}/next-tasks               GET (direction)
/api/projects/{id}/activity                 GET (project audit log)
/api/projects/{id}/features                 GET (list), POST (create)
/api/projects/{id}/tasks                    GET (flat task list)
/api/projects/{id}/requirements             GET (list), POST (create)
/api/features/{id}                          GET, PUT, DELETE
/api/features/{id}/tasks                    GET (list), POST (create)
/api/tasks/{id}                             GET, PUT, DELETE
/api/tasks/{id}/status                      PATCH
/api/tasks/{id}/feature                     PATCH (reassign)
/api/tasks/{id}/claim                       POST
/api/tasks/{id}/release                     POST
/api/tasks/{id}/activity                    GET
/api/tasks/{id}/subtasks                    GET (list), POST (create)
/api/tasks/{id}/acceptance-criteria         GET (list), POST (create)
/api/tasks/{id}/comments                    GET (list), POST (create)
/api/tasks/{id}/dependencies                GET (list), POST (create)
/api/subtasks/{id}                          GET, PUT, DELETE
/api/acceptance-criteria/{id}               GET, PUT, DELETE
/api/comments/{id}                          DELETE
/api/task-dependencies/{id}                 GET, DELETE
/api/requirements/{id}                      GET, PUT, DELETE
/api/requirements/{id}/assign               PATCH (assign feature/task)
/api/ideas                                  GET (list ?status= ?tag= ?q= ?limit= ?offset=), POST (create)
/api/ideas/{id}                             GET, PATCH, DELETE
/api/ideas/{id}/promote                     POST (promote idea to a project)
/api/ideas/{id}/activity                    GET (idea audit log)
```

### 6.2 MCP tools

**Implemented (11):** `list_projects`, `get_project`, `list_project_tasks`, `get_task`, `get_project_board`, `get_project_context`, `get_next_tasks`, `claim_task`, `release_task`, `update_task_status`, `add_task_comment`.

**To add (priority order):**

| # | Tool | Closes capability |
|---|------|-------------------|
| 1 | `update_project` | C-4 (the documentation backbone) |
| 2 | `create_project` | C-2 |
| 3 | `create_feature`, `update_feature`, `delete_feature`, `reparent_feature` | C-2 |
| 4 | `create_task`, `update_task`, `delete_task`, `reassign_task` | C-2 |
| 5 | `get_step_context` | C-3 (single-call bundle for an agent about to act) |
| 6 | Subtask CRUD (5 tools) | C-2 |
| 7 | Acceptance-criterion CRUD (5 tools) | C-2 |
| 8 | Requirement CRUD + assign (6 tools) | C-2 |
| 9 | Dependency CRUD (3 tools) | C-2 |
| 10 | Activity tools (`list_project_activity`, `list_task_activity`) | C-1 |
| 11 | `search_project` (waits on R-1.5) | C-1 |

Each tool forwards to the REST API — no duplicate logic in the MCP wrapper.

---

## 7. Definition of done (vision-complete)

An agent can do the following 10-step workflow end-to-end via MCP, in under 5 minutes, on a fresh DB:

1. `create_project` "Build feature X" with description + goals
2. `update_project_readme` to document context, links, prior art
3. `create_feature` × 3 (with markdown descriptions)
4. `create_requirement` × 5 (P0/P1/P2 with acceptance criteria)
5. `create_task` × 12 (assigned to features; with markdown descriptions)
6. `create_acceptance_criterion` × 30 (3 per task average)
7. `create_dependency` × 8 (real DAG)
8. `get_next_tasks` → reasoning explains the top recommendation
9. `claim_task` → `update_task_status` Todo → InProgress → `add_task_comment` Decision → `mark_acceptance_criterion_done` × 3 → `update_task_status` Done
10. `list_task_activity` returns 8+ events tracing the lifecycle

All 10 steps must succeed without the agent reading any source code — the OpenAPI spec and MCP tool descriptions are sufficient.

---

## 8. Gap list (prioritised)

The five concentrated gaps to vision-complete:

| # | Gap | Closes | Effort |
|---|-----|--------|--------|
| 1 | **Service layer extraction** so REST + UI share validation, audit, project-boundary checks. | R-2.3, R-2.4, R-2.5, R-7.2 (closes #82, #83, #88, half of #89) | 2-3 days |
| 2 | **Expand MCP tool surface** from 11 → ~40. | R-2.2 | 2-4 days |
| 3 | **OpenAPI generation + replace anonymous response types with records.** | R-5.1, R-5.2 | 1-2 days |
| 4 | **Wire up Markdig** for rendering descriptions / goals / README / comments. | R-4.2 | 0.5-1 day |
| 5 | **Adopt EF Core migrations** (replace `EnsureCreated` + manual patchers). | R-6.4 | 0.5 day |

Plus four integrity bugs that directly hurt AI accuracy:

| Bug | Issue | Fix vehicle |
|-----|-------|-------------|
| Cross-project context leak (requirement assign) | #82 | Service-layer extraction (gap #1) |
| Task feature reassignment crosses projects | #83 | Service-layer extraction |
| UI mutations leave audit log half-empty | #88 | Service-layer extraction |
| `Status: "99"` produces ghost tasks invisible to board | #89 | `TryParseStrict<T>` helper + remove UI raw int-to-enum casts |

Plus polish: standardise dev URL on `:5123`, add CI workflow, add Node-based smoke, add cross-platform contributor docs.

---

## 9. Historical reference

v1 (2026-04-12) of this document organised requirements P0/P1/P2 by entity (R-01 Project CRUD, R-02 Feature CRUD, ... R-21 Recursive Hierarchy). That structure shipped successfully — the entity-level work is implemented and the data model is stable.

v2 (this version) reorganises around capabilities because that's the lens the AI-first vision (README §"Vision") needs. The entity field specs from v1 are preserved in §3 above; the per-entity acceptance-criteria checklists are folded into the C-1..C-7 acceptance criteria.

The previous review documents in `docs/reviews/` remain authoritative for code-state-as-of date.
