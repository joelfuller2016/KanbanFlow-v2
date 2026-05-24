# Native in-process MCP server (prototype)

This documents the prototype that hosts an **MCP server inside the same ASP.NET Core
process** as the REST API and Blazor UI — proving we can eventually retire the Node.js
wrapper (`scripts/mcp-server.mjs`).

## What this prototype adds

- Package: **`ModelContextProtocol.AspNetCore` 1.3.0** (the official, Microsoft-co-maintained
  C# MCP SDK).
- `src/KanbanFlow/Mcp/KanbanMcpTools.cs` — a `[McpServerToolType]` class exposing
  **10 tools (5 read + 5 audit-aware write)**.
- `Program.cs` wiring:

  ```csharp
  builder.Services.AddMcpServer()
      .WithHttpTransport(options => options.Stateless = true)
      .WithToolsFromAssembly();
  // …
  app.MapMcp("/mcp");
  ```

- The Node wrapper is **left in place** as the stdio fallback and the full 36-tool surface.

## Why in-process beats the Node wrapper

The Node wrapper forwards every JSON-RPC call to the REST API over HTTP. The native tools
call the **service layer / EF Core directly via DI** — no extra network hop, one process,
one deployable, and mutations reuse the exact audit-aware code path the UI/REST use.

| Tool | Backing | Kind |
|------|---------|------|
| `list_projects` | `IDbContextFactory<AppDbContext>` | read |
| `get_project_context` | `IDbContextFactory<AppDbContext>` | read |
| `get_next_tasks` | `IDbContextFactory<AppDbContext>` | read (direction) |
| `get_task` | `IDbContextFactory<AppDbContext>` | read |
| `update_task_status` | **`ITaskService.ChangeStatusAsync`** | mutation (audit-aware) |
| `create_task` | **`ITaskService.CreateAsync`** | mutation (audit-aware) |
| `delete_task` | **`ITaskService.DeleteAsync`** | mutation (audit-aware) |
| `add_task_comment` | **`ITaskService.AddCommentAsync`** | mutation (audit-aware) |
| `claim_task` | **`ITaskService.ClaimAsync`** | mutation (audit-aware) |
| `release_task` | **`ITaskService.ReleaseAsync`** | mutation (audit-aware) |

### Service-layer extraction (done — closes the task/comment half of #88)

The write tools (`create_task`/`delete_task`/`add_task_comment`/`claim_task`/`release_task`)
go through `ITaskService`, whose write logic was lifted out of `ApiEndpoints.cs` so UI, REST,
and MCP all emit identical `ActivityEvent`s. This closes the task/comment half of the #88
audit-bypass bug — the Blazor UI no longer writes to EF directly for task/comment mutations.
Remaining (phase 2): feature / requirement / subtask / acceptance-criterion / dependency
write extraction.

## Design decisions

- **Stateless Streamable HTTP** (`Stateless = true`): simplest for local agent clients — no
  session handshake required. Flip to stateful later if server-initiated messages are needed.
- **Reads via the DbContext factory; the mutation via `ITaskService`.** Reads need no audit;
  the status change must produce a `TaskStatusChanged` activity event + `StatusChange` comment,
  so it goes through the service.
- **EF-safe shaping:** queries materialise (`ToListAsync`) before mapping, because EF Core
  can't reliably translate `enum.ToString()` inside a projection.
- **mcp-builder discipline:** snake_case tool names, concise "what it does + what's next"
  descriptions, and actionable errors (e.g. invalid status lists the valid set).

## Verification (performed on this branch)

- `dotnet build` → **0 warnings, 0 errors**.
- App started on `http://127.0.0.1:5123` (verified on a scratch port + throwaway DB):
  - `POST /mcp` `tools/list` → all 10 tools with correct input schemas; write tools emit
    `TaskCreated`/`TaskCommentAdded`/`TaskClaimed`/`TaskStatusChanged`/`TaskDeleted` activity
    events attributed to the actor (verified e2e); in-app service tests 17/17.
  - `tools/call list_projects` → real seed data (`Sample Project`, 4 features / 5 tasks).
  - `tools/call update_task_status {taskId:1, status:"InProgress", actor:"verify"}` →
    succeeded, and `GET /api/tasks/1/activity` showed a `TaskStatusChanged` event with
    `actor:"verify"` and before/after snapshots — i.e. the audit trail was written.
  - Invalid status → `isError:true` with the valid-values list.

## Connecting a client

```jsonc
{
  "mcpServers": {
    "kanbanflow-native": {
      "type": "http",
      "url": "http://127.0.0.1:5123/mcp"
    }
  }
}
```
Start the app first: `dotnet run --project src/KanbanFlow/KanbanFlow.csproj --urls http://127.0.0.1:5123`.

## Path to full coverage (next steps)

1. **Extract the service layer** (#88) — *done for tasks + comments*. Remaining: move
   feature/subtask/criterion/requirement/dependency write logic out of `ApiEndpoints.cs`
   into services (and add their MCP write tools).
2. **Grow the native tool surface** to match the Node wrapper (36 tools), curated — see below.
3. **Retire the Node wrapper** once parity + clients are migrated; keep stdio via a thin
   console host if any client still needs it.

### Auto-generation options (evaluated, not adopted)

We already publish OpenAPI at `/openapi/v1.json`, so generating tools from it is tempting.

- **`Kebechet.Api.ToMcp`** — a C# **source generator** that turns existing ASP.NET endpoints
  into MCP tools at compile time, in the same process. Exactly our scenario and worth watching.
  *Caveat:* young/low-maturity NuGet — pin and vet before depending on it.
- **`harsha-iiiv/openapi-mcp-generator`** (Node, ~590★) — the fallback if we stayed on Node;
  generates Zod-schema tools from an OpenAPI doc.

**Recommendation:** do **not** auto-generate one tool per endpoint. Anthropic's mcp-builder
and Speakeasy both warn this yields a noisy, low-value surface (46 raw CRUD tools). Hand-curate
the high-value **workflow** tools (`get_next_tasks`, `get_step_context`, `move`/`update_task_status`
with WIP checks, etc.) natively; consider a generator only to backfill breadth, or as scaffolding
to hand-edit.

## References

- C# MCP SDK: <https://github.com/modelcontextprotocol/csharp-sdk> ·
  docs <https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html>
- mcp-builder skill: <https://github.com/anthropics/skills/tree/main/skills/mcp-builder>
- Kebechet.Api.ToMcp: <https://dev.to/kebechet/your-aspnet-api-already-speaks-mcp-it-just-doesnt-know-it-yet-2870>
- OpenAPI→MCP (Node fallback): <https://github.com/harsha-iiiv/openapi-mcp-generator>
