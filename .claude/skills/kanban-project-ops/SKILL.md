---
name: kanban-project-ops
description: >-
  Read, navigate, and update KanbanFlow v2 projects — the AI-first project
  documentation & direction system — through its REST API or MCP server. Use this
  when working inside a KanbanFlow v2 repo, when asked "what should I work on next?",
  or when creating/reading/updating projects, features, tasks, subtasks, acceptance
  criteria, dependencies, requirements, comments, or activity via KanbanFlow's API/MCP.
---

# KanbanFlow v2 — Project Operations

KanbanFlow v2 is not a kanban app with an API bolted on. It is a **project knowledge
graph** that an agent can read, write, and be *directed by*. The kanban board is one
view; the same graph is exposed over **REST** and **MCP** for agents. Every change —
human or agent — produces an immutable **activity event**. Treat the audit trail as
sacred: never bypass it.

## When to use this skill

- You're working in a repo whose tasks live in KanbanFlow v2.
- You need to know **what to work on next**, with full context.
- You're creating or updating any entity (project, feature, task, subtask, acceptance
  criterion, requirement, dependency, comment) programmatically.

> **Working across more than one project?** This skill operates *inside* a single project. For
> cross-project status — a standup, "what's in flight everywhere", "what am I forgetting" — or
> when the board is empty and needs reconciling from GitHub, use the **`kanban-portfolio`**
> skill instead. It gets you to the right project; this one gets the work done.

## Who owns what (do not blur this)

**GitHub is the system of record for issues and PRs.** The board does not mirror them and
must never be read as a replacement for `gh`. The board owns the execution state that
GitHub structurally cannot hold:

| Owned by GitHub | Owned by the board |
|---|---|
| Issues, PRs, review threads, labels, milestones | **Claims** — who is on it, right now |
| The permanent public record | **Dependencies** and next-task ranking |
| Anything another human reads | **Acceptance criteria** and the Definition-of-Done gate |
| | **Ordering** within a column |
| | The **memory graph** |

If you need issue state, ask GitHub. If you need to know what to pick up and whether it is
already taken, ask the board. Filing an issue is always `gh`, never a board write.

See `docs/ARCHITECTURE.md` § "Who owns what" in the KanbanFlow-v2 repo.

## Two interfaces (same graph)

| Interface | Where | Best for |
|-----------|-------|----------|
| **REST + OpenAPI** | `http://127.0.0.1:5123` · spec at `/openapi/v1.json` | scripts, curl, discovering the full surface |
| **MCP (native)** | `POST http://127.0.0.1:5123/mcp` (Streamable HTTP, stateless) | agents connected to the in-process server |
| **MCP (Node wrapper)** | `scripts/mcp-server.mjs` over stdio | Claude Desktop / clients that prefer stdio |

> **URL convention:** start the app with `--urls http://127.0.0.1:5123` (the launch
> profile default is `:5148`, but the MCP wrapper and smoke tests assume `:5123`).
> Local-loopback only, **no auth** — single trust boundary (agent + owner, one machine).

**Source of truth for the live tool/endpoint list** — don't guess:
- MCP: send `tools/list` (JSON-RPC) to see exactly which tools the server exposes.
- REST: `GET /openapi/v1.json`.

## The core loop: "what's next?" → do it → record it

This is the workflow KanbanFlow is built for. Follow it on every unit of work.

```text
1. get_next_tasks(projectId, actor=<you>)   → priority-sorted, unblocked, with a `reason`
2. get_step_context(taskId)  (or get_task)  → one bundle: description, acceptance
                                               criteria, subtasks, dependencies, comments
3. claim_task(taskId, actor=<you>)          → signal you're working it (avoids collisions)
4. … do the work …
5. add_task_comment(taskId, type, text)     → record a Decision / Handoff / Verification
6. update_task_status(taskId, status)       → Backlog→Todo→InProgress→Review→Done
7. release_task(taskId)  (if handing off)   → let another actor pick it up
```

`get_next_tasks` already excludes tasks blocked by incomplete dependencies and
de-prioritises tasks claimed by *other* actors, and every recommendation carries a
human-readable `reason` ("Top because: High priority, no open blockers, unclaimed").
Trust it; don't re-derive priority yourself.

## Conventions that matter

- **Always pass an `actor` label** on mutations (your agent name). It is recorded as the
  comment author and on the activity event. Omitting it falls back to `mcp-agent`/`System`.
- **Comment types carry intent** — pick the right one:
  - `Note` — general observation.
  - `Decision` — a choice you made and *why* (future agents read these).
  - `Handoff` — context for whoever picks this up next.
  - `Verification` — evidence the work satisfies acceptance criteria.
  - (`StatusChange`, `Created`, etc. are system-generated — don't author these.)
- **Statuses** (enum): `Backlog`, `Todo`, `InProgress`, `Review`, `Done`. Case-insensitive
  on input; invalid values are rejected with the valid set listed.
- **Priorities** (enum): `Low`, `Medium`, `High`, `Critical`.
- **Documentation is first-class.** A project's `readme`, `goals`, and `description`, plus
  every entity's text fields, are living docs — read them for context, keep them current.
- **Acceptance criteria are checklist items**, not prose buried in a description. Mark them
  done as you satisfy them (`mark_acceptance_criterion_done`).
- **Project boundaries are enforced**: a task can't be reassigned across projects; a
  requirement can't link to a feature/task in another project. Don't fight it.

## Entity model (what you can read/write)

```text
Project ─ readme/goals/description (markdown docs), Requirements (P0/P1/P2)
  └─ Feature (recursive: features contain features)
       └─ TaskItem ─ description, Status, Priority, claim
            ├─ AcceptanceCriterion (checklist)
            ├─ Subtask (recursive)
            ├─ TaskComment (Note/Decision/Handoff/Verification + system)
            └─ TaskDependency (DAG; cycle-protected)
ActivityEvent ─ append-only audit log; survives entity deletion
```

## Canonical capabilities (names as exposed by the MCP server)

Read: `list_projects`, `get_project`, `get_project_context` (whole-graph bundle),
`list_project_tasks`, `get_task`, `get_step_context`, `get_project_board`,
`get_next_tasks`, `list_subtasks`, `list_acceptance_criteria`, `list_dependencies`,
`list_project_activity`, `list_task_activity`.

Write: `create_project`/`update_project`, `create_feature`/`update_feature`/`delete_feature`,
`create_task`/`update_task`/`delete_task`, `update_task_status`, `reassign_task`,
`claim_task`/`release_task`, subtask + acceptance-criterion + dependency CRUD,
`add_task_comment`.

> The **native in-process server** currently exposes a curated subset
> (`list_projects`, `get_project_context`, `get_next_tasks`, `get_task`,
> `update_task_status`); the **Node wrapper** exposes the full surface. Always confirm
> with `tools/list` / `/openapi/v1.json` rather than assuming a tool exists.

## Gotchas

- **No paging caps yet on most list endpoints** — for very large projects, prefer
  `get_project_context` once over many small reads, and expect big payloads.
- **Run the app before connecting.** MCP/REST need the Kestrel host up on `:5123`.
- **Don't write directly to the SQLite file or EF entities** to "save a step" — that
  bypasses the audit trail. Go through the API/MCP every time.
- **Smoke tests** live at `scripts/smoke-api.ps1` and `scripts/smoke-mcp.mjs`; run them
  after changes to confirm the surface still behaves.
