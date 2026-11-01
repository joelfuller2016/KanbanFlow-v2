# Project memory ownership

KanbanFlow owns project tasks, decisions, requirements and project knowledge.
The native `get_project_context` tool at the server's HTTP `/mcp` endpoint is the
selected entry point for authoritative project memory. It returns current
project memory with the project docs, features and tasks. It excludes retired
versions, other projects and personal/agent/session namespaces.
Each page contains up to 200 complete nodes, ordered by `CreatedAt` descending,
then `Id` descending. Native JSON property names use camelCase. When
`memoryTruncated` is true, call `get_project_context` again with the same
`projectId` and `memoryCursor` set to the returned
`memoryNextCursor`. Append the returned `memory` entries and repeat until
`memoryTruncated` is false. The final page omits `memoryNextCursor`; clients
should also accept a null cursor. Omit `memoryCursor` to restart from the
newest page. Treat cursor strings as opaque and project-specific.

Continuation uses creation time and ID, so deleting the previous boundary node
or inserting newer nodes does not shift later pages. Each call reads current
records, not a frozen snapshot: retirement removes a node from subsequent reads,
and concurrent backdated inserts may appear later. Reload from the first page
before a new task to pick up newly created versions. Project docs, features and
tasks are included on every call.

The Node stdio wrapper forwards the REST `/api/projects/{id}/context` shape.
Its transport and response differ from the native tool; see the
[native MCP contract notes](native-mcp-notes.md#2-response-shape-differences-5).
Configure the agent's `kanban` source to use the native HTTP `/mcp` endpoint
for the memory pages described here.

| Data | Owner | Other components |
| --- | --- | --- |
| Project tasks and decisions | KanbanFlow project services and memory | ToolGateway retrieves them through API/MCP before planning |
| Personal preferences | The selected personal memory MCP server | Keep these separate from project decisions |
| Learned prompts, evaluation history and reusable skills | ToolGateway SelfImprover | Reference project decisions used as evidence; do not become their owner |
| Embeddings and short-lived context caches | Derived data | Rebuild or invalidate after source changes |

## Identity and synchronization

An identity is the tuple `(configured KanbanFlow server, Reference)`, for example
`(my-kanban-server, Project:3)` or `(my-kanban-server, MemoryNode:42)`. The server
identity comes from the trusted connector configuration, not a forwarded Host
header or the returned content. Numeric IDs are not globally unique across
installations. Keep IDs and provenance with every cache entry.

MemoryNode IDs identify versions. An update retires the old row and creates a
new one; follow `SupersededById` when resolving an old reference, or reload current
project context. Do not infer identity from a mutable title or a text match.

The synchronization direction is KanbanFlow to a disposable agent context/cache.
Reload before each project task. Mutations go back to KanbanFlow's existing
audit-aware API/MCP write tools, then invalidate the cached context. There is no
bidirectional replication to SelfImprover or a generic MCP memory graph. Personal
preferences and learned skills remain in their own namespaces.

## Agent use

Connect the KanbanFlow MCP server as the `kanban` source in ToolGateway's canonical
tool catalog. Offer `kanban__list_projects`, `kanban__get_project_context` and
`kanban__get_task` for planning. Call `get_project_context` with the selected
project ID, use its current memory and preserve `authority`, `reference`, `source`
and `validFrom` with the answer's supporting context. Request write tools only
when the task calls for a project change.

This extends the existing project-context tool and adds a partial index for
current-memory paging. The EF model creates the index for new databases; the
idempotent startup patch adds it to existing databases. There is no additional
memory service or project synchronization loop. Scope/version tests use the
actual SQLite schema and MCP method; query-plan tests check both page queries.
An HTTP client test discovers the native tool and follows its cursor while
checking complete Markdown content and provenance on both pages.
