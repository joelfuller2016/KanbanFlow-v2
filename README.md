# KanbanFlow v2

A super simple .NET 10 Blazor Server kanban board with project management. AI agents use the REST API to manage work. Humans use the UI.

## Quick Start

Run from the repo root with the checked-in launch profile:

```powershell
dotnet run --project src/KanbanFlow/KanbanFlow.csproj
# Open the HTTP URL printed by dotnet run, for example http://localhost:5043
```

For a fixed local URL that is convenient for UI testing:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/KanbanFlow/KanbanFlow.csproj --urls http://127.0.0.1:5123
# Open http://127.0.0.1:5123
```

Avoid running the local UI with `--no-launch-profile` unless you also set `ASPNETCORE_ENVIRONMENT=Development`. Without the Development environment, Blazor static web assets such as `_framework/blazor.web.js` may not be served from an unpackaged local build.

## Smoke Tests

Run the minimal API smoke suite from the repo root:

```powershell
./scripts/smoke-api.ps1
```

The script builds the app, starts it on a temporary fresh SQLite database, and checks the project list, project board, requirements, AI context, and next-task endpoints.

## MCP Server

Start the web app, then run the minimal MCP wrapper from the repo root:

```powershell
$env:KANBANFLOW_API_URL = "http://127.0.0.1:5123"
node ./scripts/mcp-server.mjs
```

The server exposes **36 REST-backed MCP tools** so AI agents can read, author, and direct project work end-to-end:

**Projects (5):** `list_projects`, `get_project`, `get_project_context`, `create_project`, `update_project`

**Features (4):** `list_features`, `get_feature` (via `get_project`), `create_feature`, `update_feature`, `delete_feature`

**Tasks (8):** `list_project_tasks`, `get_task`, `get_step_context`, `create_task`, `update_task`, `delete_task`, `update_task_status`, `reassign_task`

**Subtasks (5):** `list_subtasks`, `create_subtask`, `update_subtask`, `mark_subtask_done`, `delete_subtask`

**Acceptance criteria (5):** `list_acceptance_criteria`, `create_acceptance_criterion`, `update_acceptance_criterion`, `mark_acceptance_criterion_done`, `delete_acceptance_criterion`

**Dependencies (3):** `list_dependencies`, `create_dependency`, `delete_dependency`

**Comments + claim + activity (6):** `add_task_comment`, `claim_task`, `release_task`, `get_project_board`, `get_next_tasks`, `list_project_activity`, `list_task_activity`

The full machine-readable list is at `tools/list` (JSON-RPC) when the server is running. The OpenAPI document at `/openapi/v1.json` describes the underlying REST surface.

With the web app already running, smoke-test the wrapper with:

```powershell
node ./scripts/smoke-mcp.mjs 1
```

## What It Does

- **Project Outline** (left panel) — tree view of Project → Features → Tasks as draggable mini-cards
- **Kanban Board** (main area) — cards auto-placed by task status, drag-and-drop to change status
- **REST API** — full CRUD for AI agents to read and write project data
- **OpenAPI Spec** — machine-readable API description (R-5.1) at `/openapi.json` (redirects to `/openapi/v1.json`); response shapes are loose pending the records refactor (R-5.2)
- **Zero Setup** — SQLite, no Docker, no accounts, no login

## Stack

.NET 10 | Blazor Server | C# | SQLite | Bootstrap 5.3 | Minimal APIs

## Docs

- [Requirements](docs/requirements.md) — full requirements with acceptance criteria
