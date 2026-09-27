# Native in-process MCP server (prototype)

This documents the prototype that hosts an **MCP server inside the same ASP.NET Core
process** as the REST API and Blazor UI — proving we can eventually retire the Node.js
wrapper (`scripts/mcp-server.mjs`).

## What this prototype adds

- Package: **`ModelContextProtocol.AspNetCore` 1.3.0** (the official, Microsoft-co-maintained
  C# MCP SDK).
- `src/KanbanFlow/Mcp/KanbanMcpTools.cs` — a `[McpServerToolType]` class exposing
  **15 tools (7 read + 8 audit-aware write)**.
- `Program.cs` wiring:

  ```csharp
  builder.Services.AddMcpServer()
      .WithHttpTransport(options => options.Stateless = true)
      .WithToolsFromAssembly();
  // …
  app.MapMcp("/mcp");
  ```

  The native MCP endpoint is **open access** (no `RequireHost`): no auth by design,
  trust-the-network — same as the unrestricted REST API + open CORS. Loopback/CORS
  hardening is deferred to issue #84.

- The Node wrapper is **left in place** as the stdio fallback and the full 41-tool surface.

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
| `project_github_issue` | **`IExternalWorkItemService.ProjectInboundGitHubIssueAsync`** | atomic task/link/audit projection; same-project retry returns current card unchanged |
| `delete_task` | **`ITaskService.DeleteAsync`** | mutation (audit-aware) |
| `add_task_comment` | **`ITaskService.AddCommentAsync`** | mutation (audit-aware) |
| `claim_task` | **`ITaskService.ClaimAsync`** | mutation (audit-aware) |
| `release_task` | **`ITaskService.ReleaseAsync`** | mutation (audit-aware) |
| `list_ideas` | `IIdeaService` | read |
| `get_idea` | `IIdeaService` | read |
| `create_idea` | **`IIdeaService.CreateAsync`** | mutation (audit-aware) |
| `update_idea` | **`IIdeaService.UpdateAsync`** | mutation (audit-aware) |
| `promote_idea` | **`IIdeaService.PromoteAsync`** | mutation (audit-aware) |

> `update_idea` cannot set `status: Promoted` — that status is **derived**, written only by
> `promote_idea`/`PromoteAsync`, which alone also creates the linked project. `UpdateAsync` throws
> `ValidationException` (surfaced as an actionable tool error naming the promote path) if you try (#283).

The atomic import tool is exposed through both native and stdio MCP. Its [TC-507 contract](test-cases/507-atomic-github-projection/README.md) records full-content, conflict and replay checks. This does not close the older native `get_task` response's missing durable links/journal fields; that separate read-shape gap remains explicit during workflow integration.

### Service-layer extraction (done — closes the task/comment half of #88)

The write tools (`create_task`/`delete_task`/`add_task_comment`/`claim_task`/`release_task`)
go through `ITaskService`, whose write logic was lifted out of `ApiEndpoints.cs` so UI, REST,
and MCP all emit identical `ActivityEvent`s. This closes the task/comment half of the #88
audit-bypass bug — the Blazor UI no longer writes to EF directly for task/comment mutations.

These five write tools are mutation-capable and **unauthenticated by design**. `/mcp` is
**open access** (no `RequireHost`) — same trust-the-network posture as the unrestricted REST
API + open CORS. If you bind to a non-loopback `--urls`, they are reachable from the network;
that is intentional for the single-user local tool, with loopback/CORS hardening deferred to #84.

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
  - `POST /mcp` `tools/list` → all 15 tools with correct input schemas; write tools emit
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

> `/mcp` is **open access** — no `RequireHost`, so any host/origin works (match the `url` to your `--urls`). The endpoint is unauthenticated by design (trust-the-network; same posture as the REST API + open CORS). Loopback/CORS hardening is deferred to #84.

## Path to full coverage (next steps)

1. **Extract the service layer** (#88) — *done for tasks + comments*. Remaining: move
   feature/subtask/criterion/requirement/dependency write logic out of `ApiEndpoints.cs`
   into services (and add their MCP write tools).
2. **Grow the native tool surface** to match the Node wrapper (41 tools), curated — see below.
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

## Wrapper ↔ native deltas to close before the #231 cutover

#231 tracks retiring the Node stdio wrapper (`scripts/mcp-server.mjs`) in favour of the
in-process native `/mcp` server. **Until that cutover the Node wrapper remains the production
tool surface** — currently **50 tools** (11 read + 10 authoring + 5 subtask + 5 acceptance-criterion
+ 3 dependency + 2 activity + 5 idea + 9 knowledge-graph).

> Historical counts elsewhere are stale: this file's "41-tool surface" (above) and some issues'
> "36" predate the idea-inbox tools (#180, +5) and the knowledge-graph memory tools (#224, +9).
> The live count, verified from `scripts/mcp-server.mjs` (`node --check` + tool grep), is **50**.

The deltas below were catalogued while hardening the wrapper (#168). They are **recorded here
only** — do NOT add the missing tools or change these shapes in the wrapper; they are inputs to
the #231 native cutover.

### 1. Protocol-version negotiation (#3)

- **Wrapper:** negotiates against a 2-entry allowlist (`SUPPORTED_PROTOCOL_VERSIONS` =
  `2025-06-18`, `2024-11-05` — `mcp-server.mjs:25`, `negotiateProtocolVersion` at `:33-39`): it
  echoes the client's requested revision when that revision is in the list, else falls back to the
  newest one it supports. This is narrower than full negotiation — a client asking for a revision
  outside those two gets the fallback, not a match — but it is not the unconditional hardcode this
  section previously claimed. On the separate HTTP hop to the native server it sends a fixed
  `MCP-Protocol-Version: 2025-06-18` header (`nativeProtocolVersion`), deliberately pinned rather
  than negotiated — see the comment at `mcp-server.mjs:12-19` for why the two hops are versioned
  independently.
- **Native:** the `ModelContextProtocol.AspNetCore` SDK performs the standard MCP version
  negotiation during the initialize handshake rather than pinning one revision.
- **Cutover impact:** narrower than previously stated — most clients already see a negotiated
  match from the wrapper today. Only a client requesting a revision outside the 2-entry allowlist
  would see a different (SDK-negotiated) version after cutover — confirm target clients
  accept that.

### 2. Response-shape differences (#5)

- **`get_next_tasks`:**
  - *Wrapper* returns the REST payload verbatim — an **object**:
    `{ projectId, generatedAt, count, recommendations: [...], blockedTaskCount, blockedTasks: [...] }`.
  - *Native* returns a **flat array** of ranked recommendations (`IReadOnlyList<NextTask>`), with
    no `blockedTasks` envelope.
  - Both share the same ranker (#151) — identical canonical order, actor de-prioritisation, and
    1–50 limit clamp; only the envelope differs.
- **`get_project_context`:**
  - *Wrapper* returns the REST `/context` payload (deeply nested: metadata + summary counts +
    requirements + nested features → tasks → subtasks → comments).
  - *Native* returns a flatter `ProjectContext` (project docs + all features with `parentFeatureId`
    + a flat task list).
- **Cutover impact:** any client parsing `recommendations`/`blockedTasks`, or walking the deep
  context tree, must adapt to the native flat shapes (or the native tools must be reshaped to match).

### 3. REST capabilities with no wrapper tool (#7)

~26 REST endpoints have no corresponding wrapper tool (and, by extension, no native tool yet),
including:

- **Requirements** — full CRUD **plus** assignment to features/tasks (no `*_requirement` tools exist).
- **Features** — `GET /api/features/{id}` (fetch one) and list-features (the wrapper only
  creates/updates/deletes features).
- **Comments** — `DELETE /api/comments/{id}` (the wrapper can add a comment but not delete one).

**Decision:** the wrapper is intentionally a curated **workflow** surface, not 1:1 REST parity.
Do **not** backfill these ~26 tools into the wrapper. Close the gap (if desired) **natively** as
part of #231, hand-curating high-value tools rather than auto-generating one tool per endpoint
(see "Auto-generation options" above).

## References

- C# MCP SDK: <https://github.com/modelcontextprotocol/csharp-sdk> ·
  docs <https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html>
- mcp-builder skill: <https://github.com/anthropics/skills/tree/main/skills/mcp-builder>
- Kebechet.Api.ToMcp: <https://dev.to/kebechet/your-aspnet-api-already-speaks-mcp-it-just-doesnt-know-it-yet-2870>
- OpenAPI→MCP (Node fallback): <https://github.com/harsha-iiiv/openapi-mcp-generator>
