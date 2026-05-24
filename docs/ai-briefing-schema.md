# AI Orientation Briefing — schema (Phase 0, issue #173)

> **Status: design / read-only.** No entities, migrations, or endpoints ship in this phase.
> This is the reviewable artifact that locks the `get_project_briefing` shape (and the
> Milestone addition it depends on) **before** any migration — design from evidence, not theory.
> Defaults shown for the [open questions](#open-questions) can change without reworking the build.

## Purpose

Give an AI (or human) **full situational awareness of a project in one call**: intent → process →
what's in progress → what's next → what changed. It layers on the existing strong primitives
(`get_project_context`, the dependency-aware `get_next_tasks` with per-task *reasons*, claiming,
typed comments) rather than replacing them.

## Design principles

1. **One layered entry-point** — intent, process, now, next, progress, recent — mirroring how you'd brief a human at standup.
2. **Explicit semantics over inference** — ship the workflow definition; don't make the agent guess what `Review` means or when a task is "ready".
3. **Bounded** — top-N per section + counts; never dump the whole graph. A `depth` knob and a `since` cursor keep it cheap on repeat visits.
4. **A reason on every recommendation** — generalize the `get_next_tasks` reason field across `next`, `blocked`, and `stalled`.
5. **Summaries with stable-ID drill-down** — the agent calls `get_task` only for what it will actually work.

## Structural addition: Milestone (the "when" dimension)

Orthogonal to the Feature tree (`Feature` = *what*, `Milestone` = *when*). A task keeps its feature
and gains an optional milestone.

```
Milestone
  Id          int
  ProjectId   int
  Name        string
  Goal        string?
  SortOrder   int
  Status      Planned | Active | Done     // exactly one Active per project
  Target      DateTime?                    // optional due date
TaskItem (added)
  MilestoneId int?                          // nullable; backfilled to a default "Backlog" milestone
  Estimate    string?                       // optional S|M|L or points (capacity/burndown) — see open Q
```

`Blocked` / `Ready` / `Stalled` are **derived flags, NOT new `WorkStatus` values** (keep status linear Backlog→Done).

## `get_project_briefing` — response schema

> **Pseudocode notation:** the block below uses `int`/`str`/`num?` type hints and `| null`
> union syntax for readability. This is intentional schema pseudocode, not valid JSON/JSONC.
> The real implementation will be TypeScript-style types or JSON Schema once the endpoint ships.

```jsonc
{
  "project":   { "id": int, "name": str, "goals": str?, "requirementsTotal": int, "requirementsSatisfied": int },
  "workflow":  {
    "states": ["Backlog","Todo","InProgress","Review","Done"],
    "transitions": [["Backlog","Todo"],["Todo","InProgress"],["InProgress","Review"],["Review","Done"],["Review","InProgress"],["InProgress","Todo"],["Todo","Backlog"]],  // incl. pause (InProgress→Todo) + demote (Todo→Backlog)
    "definitionOfReady": "Has acceptance criteria, a milestone, identified deps, non-empty description.",
    "definitionOfDone":  "All acceptance criteria checked + a Verification comment recorded."
  },
  "activeMilestone": { "id": int, "name": str, "goal": str?, "tasksDone": int, "tasksTotal": int, "target": str? } | null,
  "now":   [ { "taskId": int, "name": str, "claimedBy": str?, "claimedAgoHours": num?, "latestNote": { "type": str, "text": str }?, "stalled": bool } ],
  "next":  [ { "taskId": int, "name": str, "priority": str, "estimate": str?, "milestone": str?, "ready": bool, "reason": str } ],
  "blocked": [ { "taskId": int, "name": str, "blockedBy": [int], "reason": str } ],
  "progress": {
    "byMilestone": [ { "name": str, "done": int, "total": int } ],
    "byFeature":   [ { "name": str, "done": int, "total": int } ],
    "wip": { "inProgress": int, "wipLimit": int?, "overLimitActors": [str] }  // wipLimit (not "limit") to avoid clashing with the pagination param
  },
  "recent": [ { "at": str, "actor": str, "event": str, "taskId": int?, "summary": str } ],
  "cursor": str   // opaque; pass back as `since` next call to get only the delta
}
```

### Parameters

| Param | Default | Meaning |
|-------|---------|---------|
| `projectId` | — | required |
| `actor` | null | de-prioritize tasks claimed by others; scope "since my last activity" |
| `since` | null | cursor/timestamp from a prior `cursor` → return only deltas in `recent` |
| `depth` | `summary` | `summary` (top-N + counts) or `full` (larger caps) |
| `limit` | 5 | cap for `now` / `next` / `blocked` lists (clamped 1–50 for the native MCP tool; the Node MCP wrapper may apply a lower cap — see `scripts/mcp-server.mjs`) |

## "Next" precedence (the heart of the feature — must be deterministic)

Ordering, after **excluding** any task with an unmet hard dependency (a blocker not `Done`):

1. **Finish before you start** (Kanban "Stop Starting, Start Finishing"): the requesting actor's own
   `InProgress`/claimed **Ready** tasks rank first — to drain WIP rather than fan out.
2. Prefer **Ready** tasks (Definition-of-Ready met) over not-ready.
3. Prefer the **Active milestone**, then milestone `SortOrder`.
4. **Priority** Critical → Low.
5. De-prioritize tasks claimed by *other* (non-stale) actors; tie-break oldest `Id`.

> **Flow vs. priority:** finishing in-flight work is preferred (step 1), but a `Critical` unblocked task
> should still surface above a `Low`/`Medium` in-progress one — a `Critical` new item may pre-empt step 1.
> The `reason` string always states which rule won, so the choice is auditable.

Derivations: **Blocked** = has an unmet dependency OR an open `Blocked` comment. **Stalled** =
`InProgress` + claimed + no activity in N hours (claim past its TTL → reclaimable). **Ready** = DoR met.

## Example payload (representative; hand-built from the real model)

```json
{
  "project": { "id": 1, "name": "KanbanFlow v2", "goals": "AI-first kanban that can run its own dev loop", "requirementsTotal": 12, "requirementsSatisfied": 7 },
  "workflow": { "states": ["Backlog","Todo","InProgress","Review","Done"], "definitionOfReady": "AC + milestone + deps + description", "definitionOfDone": "AC all checked + Verification event" },
  "activeMilestone": { "id": 4, "name": "M3 — Briefing + sequencing", "goal": "Make the process AI-legible", "tasksDone": 3, "tasksTotal": 9, "target": "2026-06-15" },
  "now": [
    { "taskId": 142, "name": "Add get_project_briefing tool", "claimedBy": "agent-opus", "claimedAgoHours": 1.5, "latestNote": { "type": "Handoff", "text": "schema validated; wiring MCP next" }, "stalled": false },
    { "taskId": 139, "name": "Milestone EF migration", "claimedBy": "agent-sonnet", "claimedAgoHours": 26.0, "latestNote": { "type": "Blocked", "text": "waiting on schema sign-off" }, "stalled": true }
  ],
  "next": [
    { "taskId": 145, "name": "Requirement↔Task links", "priority": "High", "estimate": "M", "milestone": "M3 — Briefing + sequencing", "ready": true, "reason": "Top because: High priority, Ready, in active milestone, no open blockers, unclaimed." },
    { "taskId": 150, "name": "WIP-limit surfacing", "priority": "Medium", "estimate": "S", "milestone": "M3 — Briefing + sequencing", "ready": false, "reason": "After: Medium priority, active milestone, but NOT ready (no acceptance criteria yet)." }
  ],
  "blocked": [ { "taskId": 151, "name": "Burndown chart", "blockedBy": [145], "reason": "Depends on #145 (Requirement↔Task links), not Done." } ],
  "progress": { "byMilestone": [ { "name": "M3 — Briefing + sequencing", "done": 3, "total": 9 } ], "byFeature": [ { "name": "MCP surface", "done": 5, "total": 8 } ], "wip": { "inProgress": 2, "wipLimit": 3, "overLimitActors": [] } },
  "recent": [ { "at": "2026-05-24T07:40:00Z", "actor": "agent-opus", "event": "TaskClaimed", "taskId": 142, "summary": "agent-opus claimed #142" } ],
  "cursor": "evt_20260524T0740Z_8842"
}
```

## Token budget

`summary` depth targets a small, predictable size: top-`limit` items per list + integer rollups, not the
full task graph. `recent` is delta-scoped by `since`. Large projects never blow the context; the agent
expands specific tasks via `get_task`.

## Maps to the phased plan (#173)

- **Phase 1** ships `Milestone` + `TaskItem.MilestoneId`/`Estimate` + Requirement↔Task (migration + backfill).
- **Phase 2** ships the `workflow` block + Ready/Blocked/Stalled derivation.
- **Phase 3** ships rollups + recent-activity (read `ActivityEvent`) in services/REST.
- **Phase 4** ships `get_project_briefing` to exactly this schema, makes `get_next_tasks` honor the precedence above, adds `get_recent_activity`.

## Open questions

Defaults used here (override anytime): term = **Milestone**; milestones attach to **tasks**; workflow =
**global default first**; recency = **since actor's last activity**; UI reordering = **API-first**;
`get_project_briefing` **wraps** `get_project_context` (one canonical big read).
