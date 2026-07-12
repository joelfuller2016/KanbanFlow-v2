# GitHub → KanbanFlow mapping (reference)

Full semantics for `portfolio-sync.mjs`. The short version lives in `SKILL.md`; read this when
you need to reason about *why* the sync did (or didn't) do something.

## Entity mapping

### Repo → Project

Matched by `Project.Name == <repo-short-name>` (the part after the `/`). Created with a
`description` pointing back at the GitHub repo.

`Project` has **no repo-link column** today (`Models/Project.cs` — `Id/Name/Description/Goals/
Readme/CreatedAt/UpdatedAt`), so name-matching *is* the link. Consequences:

- Renaming a project on the board detaches it from its repo; the next sync creates a fresh one.
- A hand-made project whose name happens to equal a repo short-name **will be adopted** by the
  sync. Run `--dry-run` first when syncing into a board that already has hand-made projects.

A durable `Project.RepoUrl` is tracked in #237. When it lands, switch the match to it and keep
name-matching only as a fallback.

### Milestone → Feature

Matched by `Feature.Name == <milestone title>` within the project. Issues with no milestone go
to a feature literally named `Unsorted`.

This is **structurally required**, not a nicety: tasks are created at
`POST /api/features/{featureId}/tasks`. A task cannot exist without a Feature parent. It is
also the only thing keeping a several-hundred-task board navigable — each project collapses to
~7 milestone buckets.

Features are created flat (no `parentFeatureId`). KanbanFlow supports recursive features; the
sync doesn't use that, because GitHub milestones aren't hierarchical.

### Issue → TaskItem

Matched by parsing the **`#<n> ` prefix** off the task title (`/^#(\d+)\s/`).

```text
Task title:  "#287 Add kanban-portfolio skill: reconcile GitHub -> board"
             ▲
             └── the idempotency key
```

This is the whole reason the sync needs no schema change. It is also fragile in one specific
way: **strip or edit that prefix and the task is orphaned** — the next sync won't recognise it
and will create a duplicate. Don't rename synced tasks by hand; edit the issue instead.

`TaskItem.IssueNumber` (#237) replaces this with a real column. Until then, treat the prefix as
data, not decoration.

## Field mapping

### Priority — GitHub owns it, always reconciled

| Label | `TaskPriority` |
|---|---|
| `P0` | `Critical` |
| `P1` | `High` |
| `P2` | `Medium` |
| `P3` | `Low` |
| *(none)* | `Medium` |

First matching label wins (case-insensitive). Priority is re-synced on every run — set it on
the issue, not the board.

### Status — GitHub advances it; the board may hold it

`WorkStatus` = `Backlog | Todo | InProgress | Review | Done`.

**On create**, status is derived from GitHub:

| GitHub state | Status |
|---|---|
| closed | `Done` |
| open + linked open PR | `Review` |
| open + has assignee | `InProgress` |
| open | `Backlog` |

**On update**, the rules are deliberately asymmetric — the sync may *advance* a task but never
drag it backwards:

| Condition | Action |
|---|---|
| issue closed, task ≠ `Done` | → `Done` |
| issue open, linked open PR, task ∈ {`Backlog`,`Todo`,`InProgress`} | → `Review` |
| issue open, task = `Done` | **drift** — report, don't touch |
| anything else | leave the board's status alone |

Why: you might drag a task to `InProgress` on the board before touching GitHub. A naive sync
would shove it back to `Backlog` on the next run, and you'd stop trusting the board. The board
is allowed to be *ahead* of GitHub; it is not allowed to contradict a closed issue.

### Linked PR detection

A PR is linked to an issue when its **body** contains a GitHub closing keyword:

```text
/\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b[:\s]*#(\d+)/gi
```

matching `Closes #12`, `fixed #34`, `Resolves: #56`. First PR to claim an issue wins. Only
**open** PRs are considered — a merged PR's issue is closed anyway, which the closed-issue rule
already covers.

This is body-only. It does **not** read GitHub's native "linked issues" field (the
`closingIssuesReferences` GraphQL edge), so a PR linked purely through the UI sidebar is missed.
Acceptable: the convention in these repos is `Closes #N` in the body, enforced by the PR gate.

### Description — a sync block plus an excerpt

```markdown
<!-- kanbanflow-sync -->
**Issue:** https://github.com/owner/repo/issues/287
**State:** OPEN · **Milestone:** M6: AI Memory & Ops Hub · **Labels:** ai-agent, enhancement
**Open PR:** none
**Synced:** 2026-07-12T14:02:11.000Z
<!-- /kanbanflow-sync -->

<first 1500 chars of the issue body>
```

The block is machine-readable and human-visible. The body is truncated at 1500 chars — 400
tasks × full issue bodies is a lot of payload, and the canonical text is one click away.

## Drift

Drift is a disagreement the sync **refuses to resolve on its own**. It reports and moves on.

| Drift | Meaning |
|---|---|
| board says `Done`, issue is still open | you finished it but never closed the issue — *or* you closed the wrong task |
| task's issue no longer appears in the open set | the issue was closed or deleted; the task is now stranded |

Deletion is never automatic. The audit trail is sacred, and a stranded task is evidence of
something — losing it silently is worse than showing it.

## What the sync does not do

- **Does not write to GitHub.** Ever. Not a label, not a comment, not a status.
- **Does not touch SQLite/EF.** All writes go through REST so `ActivityEvent` stays complete.
- **Does not create acceptance criteria, subtasks, or dependencies.** Those are authored by
  humans and agents doing real work (`kanban-project-ops`), not derived from an issue.
- **Does not sync closed issues** unless `--include-closed`.
- **Does not use the native MCP server.** It can't: the 24 native tools include `list_projects`
  and `get_project_context` but **no `create_project`** — an agent on `/mcp` cannot onboard a
  project. Tracked as a parity gap in #231. Until then the sync uses REST
  (`POST /api/projects`), which has always been able to.
