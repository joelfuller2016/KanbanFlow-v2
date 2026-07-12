---
name: kanban-portfolio
description: >-
  Cross-project status over the KanbanFlow board — answer "what am I working on, everywhere,
  and where does it stand?" Reconciles GitHub issues/PRs (the source of truth) into the board,
  then reports a short standup: in flight, blocked, stalled, next up, and board-vs-GitHub drift.
  Use when asked for project status, a standup, "what's in flight", "what am I forgetting",
  "what should I work on next" across more than one repo, or when the board looks empty/stale.
  For working a single task inside one project, use kanban-project-ops instead.
---

# KanbanFlow — Portfolio Operations

`kanban-project-ops` answers *"what do I do next **in this project**?"*. This skill answers
the question one level up: **"what am I working on across everything, and what's its status?"**

It exists because a board with nothing on it cannot answer that. KanbanFlow is a knowledge
graph, but it is not self-populating — the work actually lives in GitHub issues and PRs. So
the portfolio loop is **reconcile first, then report**.

## The loop

```text
1. sync      GitHub ──▶ board    reconcile reality into the graph (idempotent)
2. standup   board  ──▶ you      one short report: in flight · stalled · next up · drift
3. drill     pick a project ──▶ hand off to `kanban-project-ops` to actually do the work
```

```bash
node scripts/portfolio-sync.mjs --dry-run   # see the plan, write nothing
node scripts/portfolio-sync.mjs             # reconcile
node scripts/portfolio-standup.mjs          # the report
```

Both scripts need the app running. Start it on the port they expect:

```bash
dotnet run --project src/KanbanFlow/KanbanFlow.csproj --urls http://127.0.0.1:5123
```

Config: `KANBANFLOW_API_URL` (default `http://127.0.0.1:5123`), `KANBANFLOW_PORTFOLIO_REPOS`
(comma-separated `owner/repo`; defaults to the six focus repos).

## The one rule that governs everything

**GitHub is the source of truth. The board is its projection.** The sync is strictly one-way
(GitHub → board) and **never writes to GitHub**. If the board and GitHub disagree, GitHub is
right and the disagreement is *reported as drift* — it is not silently resolved.

Corollaries you must not violate:

- **Never write to SQLite or EF directly.** Every mutation goes through REST/MCP so the
  `ActivityEvent` audit trail stays complete. The audit trail is sacred.
- **Never auto-delete a task** whose issue disappeared. Report it; let a human decide.
- **Never stomp a hand-set status.** If you moved a task to `InProgress` on the board, the
  sync leaves it there. It only *advances* status on authoritative GitHub evidence (issue
  closed → `Done`; linked PR opened → `Review`). Anything else is drift, not a correction.
  A sync that fights its owner gets turned off.

## How GitHub maps onto the graph

| GitHub | KanbanFlow | Matched by |
|---|---|---|
| Repo | **Project** | `Name == <repo>` |
| Milestone | **Feature** | `Name == <milestone>`; no milestone → `Unsorted` |
| Issue | **TaskItem** | the `#<n> ` prefix on the task title |
| Label `P0`/`P1`/`P2`/`P3` | **Priority** `Critical`/`High`/`Medium`/`Low` | default `Medium` |
| Issue state + linked PR | **Status** | open → `Backlog` · assigned → `InProgress` · linked open PR → `Review` · closed → `Done` |

Two things about this are load-bearing, not cosmetic:

- **Milestone → Feature is required.** Tasks are created at `POST /api/features/{featureId}/tasks`
  — a task *must* have a Feature parent. Milestones are also what keeps a 400-task board
  readable: each project's tasks group under ~7 milestone buckets.
- **The `#<n> ` title prefix is the idempotency key.** It is how a re-sync recognises a task
  it already created, and it needs no schema change. (A durable `TaskItem.IssueNumber` column
  is tracked in #237; until it lands, don't strip the prefix — you'll orphan the task.)

Full mapping semantics, drift rules, and the `kanbanflow-sync` description block:
see `reference/mapping.md`.

## Reading the standup

The board can hold hundreds of tasks; the report stays ~25 lines. It does that by rolling each
project into one row and letting KanbanFlow's own direction engine (`/next-tasks`) pick what
matters — **don't re-derive priority yourself, and don't page through the board to summarise it.**

- **NEXT UP** — the direction engine's top pick per project (unblocked, unclaimed, ranked).
- **IN FLIGHT** — open PRs, oldest first. A non-draft PR with no review after 3 days is `STALE`.
- **STALLED** — tasks parked in `InProgress` for >14 days. This is the "what am I forgetting" list.
- **drift** (from `sync`) — board and GitHub disagree. Read these; they're where the lie is.

## When the answer is "the board is empty"

That is a real and common state, and it is not a bug in the report — it means the sync has never
run, or has not run since the work moved. Say so plainly and run the sync; do not present an
empty board as "nothing to do."

## Gotchas

- **Run the app first.** Both scripts fail fast with a clear message if REST is unreachable.
- **`gh` must be authenticated.** The sync shells out to `gh issue list` / `gh pr list`.
- **`--dry-run` before any first sync into a board that already has hand-made projects** — a repo
  whose name collides with an existing project name will adopt that project, not create one.
- **Closed issues are skipped by default.** The board mirrors *open* work. `--include-closed`
  backfills them as `Done` if you want the history.
- **Don't run the sync against the live board to test it.** Use a scratch port and a throwaway
  DB: `KANBANFLOW_DB_PATH=$TEMP/x.db dotnet run … --urls http://127.0.0.1:5199`.

## Handing off

Once the standup tells you *which* project to work, stop using this skill and switch to
**`kanban-project-ops`**: `get_next_tasks` → `get_step_context` → `claim_task` → do the work →
`add_task_comment` → `update_task_status`. This skill gets you to the right project; that one
gets the work done.
