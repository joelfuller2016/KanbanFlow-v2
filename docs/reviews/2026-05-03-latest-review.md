# KanbanFlow v2 Latest Review

Reviewed latest `origin/master` at commit `afdc2a0` (`fix: add task detail dialog accessibility`) on 2026-05-03.

## Verification

- `dotnet build src\KanbanFlow\KanbanFlow.csproj --nologo` passed with 0 warnings and 0 errors.
- `./scripts/smoke-api.ps1` passed against a fresh temporary SQLite database.
- Review was performed in `C:\Users\joelf\KanbanFlow-v2-latest-review` to avoid disturbing the dirty local `KanbanFlow-v2` branch.

## GitHub Issues Created

- [#82 P0: Enforce project scoping for requirement feature/task links](https://github.com/joelfuller2016/KanbanFlow-v2/issues/82)
- [#83 P0: Prevent task reassignment across project boundaries](https://github.com/joelfuller2016/KanbanFlow-v2/issues/83)
- [#84 P1: Restrict CORS or document local-only API assumptions](https://github.com/joelfuller2016/KanbanFlow-v2/issues/84)
- [#85 P2: Keep deleted project activity accessible or document retention semantics](https://github.com/joelfuller2016/KanbanFlow-v2/issues/85)
- [#86 P2: Replace manual schema patching with EF migrations](https://github.com/joelfuller2016/KanbanFlow-v2/issues/86)

## Review Findings

### Project Boundary Validation

Requirement create, update, and assignment endpoints validate that referenced features or tasks exist, but not that they belong to the same project as the requirement. This can contaminate project context and board data across projects. Task reassignment has a similar issue: `PATCH /api/tasks/{id}/feature` can move a task into a feature from another project, leaving dependencies, requirements, and activity history inconsistent.

### API Exposure Assumptions

The app currently allows any CORS origin, method, and header while exposing unauthenticated mutation endpoints. That may be acceptable for a loopback-only development tool, but should be explicitly documented or restricted before any shared-network use.

### Audit Trail Semantics

Activity rows are intentionally retained without a project foreign key, but project activity lookup requires the project to still exist. If retained audit history should be user-visible after deletion, the API needs a deletion-safe history endpoint.

### Schema Evolution

Startup uses `EnsureCreated()` plus manual table and column patch helpers. This is workable for a prototype, but EF Core migrations would be safer for long-lived SQLite databases as the schema grows.
