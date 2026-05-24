## 🔧 Implementation plan

Add `tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj` (xUnit, `net10.0`) referencing `src/KanbanFlow`, and a `KanbanFlow.slnx` so `dotnet test` discovers both projects.

**Test infra:** use **real SQLite** — an in-memory connection kept open for the test's lifetime + `EnsureCreated()` — *not* the EF InMemory provider, so tests exercise the real provider, relationships, and cascade behavior defined in `AppDbContext`.

**Phase 1 — service layer (this PR).** `TaskServiceTests` against a real `AppDbContext`, covering the two implemented `ITaskService` methods:
- `ChangeStatusAsync`: status persists; `CompletedAt` is set on `→ Done` and cleared on `Done → other`; idempotent for same-status; writes a `CommentType.StatusChange` `TaskComment` **and** a `TaskStatusChanged` `ActivityEvent`; `actor: null` ⇒ comment author `"System"`; throws `TaskNotFoundException` for a missing id.
- `ToggleDoneAsync`: `Todo → Done` cascade-completes subtasks; `Done → Todo` cascade-uncompletes them; writes comment + event; throws for a missing id.
- Audit assertions explicitly verify the #88 guarantee ("every mutation is audited").

**Phase 2 — API integration (same PR if green, else fast-follow).** `WebApplicationFactory<Program>` + a temp SQLite file: smoke `/api/projects/{id}/context`, `/api/projects/{id}/next-tasks` ordering, and the `/openapi.json` shape.

All tests exercise real code paths (no mock-only/trivially-true assertions). Verified locally with `dotnet test` before the PR is opened. No GitHub Actions (per repo policy).
