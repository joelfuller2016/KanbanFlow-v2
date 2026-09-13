# Project memory ownership

KanbanFlow owns project tasks, decisions, requirements and project knowledge.
The existing `get_project_context` MCP tool is the entry point for agents. It now
returns current project memory with the project docs, features and tasks. It
excludes retired versions, other projects and personal/agent/session namespaces.
The newest 200 nodes are returned; `MemoryTruncated` tells the caller to continue
through scoped memory retrieval rather than assume the project has no more facts.

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
project ID, use its current memory and preserve `Authority`, `Reference`, `Source`
and `ValidFrom` with the answer's supporting context. Request write tools only
when the task calls for a project change.

This extends the existing project-context tool. It does not add a competing
memory service, a database migration or a second project synchronization loop.
The scope/version tests use the actual SQLite schema and MCP method.
