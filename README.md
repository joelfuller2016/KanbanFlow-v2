# KanbanFlow v2

A super simple .NET 10 Blazor Server kanban board with project management. AI agents use the REST API to manage work. Humans use the UI.

## Quick Start

```bash
dotnet run
# Open http://localhost:5000
```

## Smoke Tests

Run the minimal API smoke suite from the repo root:

```powershell
./scripts/smoke-api.ps1
```

The script builds the app, starts it on a temporary fresh SQLite database, and checks the project list, project board, requirements, AI context, and next-task endpoints.

## What It Does

- **Project Outline** (left panel) — tree view of Project → Features → Tasks as draggable mini-cards
- **Kanban Board** (main area) — cards auto-placed by task status, drag-and-drop to change status
- **REST API** — full CRUD for AI agents to read and write project data
- **Zero Setup** — SQLite, no Docker, no accounts, no login

## Stack

.NET 10 | Blazor Server | C# | SQLite | Bootstrap 5.3 | Minimal APIs

## Docs

- [Requirements](docs/requirements.md) — full requirements with acceptance criteria
