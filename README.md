# KanbanFlow

> ## KanbanFlow is GitHub, visualized.
>
> **GitHub issues are the substrate for the work the world can see** — the items, their state,
> and their conversation. KanbanFlow **projects** them into a richer graph and adds what an
> issue tracker has no place for: dependencies, acceptance criteria, subtasks, agent claims,
> and memory.
>
> It is a **projection, not a mirror.** GitHub owns some fields; the board owns others; a few
> are derived. Which is which is spelled out in
> [Who owns what](#who-owns-what) — read that table before you trust either side.

| | |
|---|---|
| **Stack** | .NET 10 · Blazor Server · SQLite (EF Core 10) · Minimal APIs · Node 18+ for the MCP wrapper |
| **Surfaces** | 6 UI routes · 71 REST endpoints · 28 native MCP tools · 50 Node-wrapper MCP tools |
| **Sync** | GitHub → board projection, plus one explicit additive board → GitHub issue-create path. Preview is the default; live create requires `--allow-write --task <id>` and a durable journal. No edit/close/delete path — see [Who owns what](#who-owns-what) |
| **Tests** | 1,079 xUnit · 56 in-app integration · 52 Playwright e2e · 274 script unit · 2 smoke suites — [all local, no CI](#deliberate-non-features) |
| **Runs at** | `http://127.0.0.1:5123` — localhost only, no auth, no Docker, no cloud |
| **Log book** | [`docs/BACKLOG.md`](docs/BACKLOG.md) — the authoritative record of what happened |
| **Deep detail** | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |

Every count above was re-derived from the code for this revision. The commands that produced
them are in [Surfaces, verified](#surfaces-verified) — run them yourself; do not trust the
table alone.

---

## Contents

- [The thesis](#the-thesis)
- [How the sync works](#how-the-sync-works)
- [GitHub → KanbanFlow: the mapping](#github--kanbanflow-the-mapping)
- [Who owns what](#who-owns-what)
- [Known omissions](#known-omissions)
- [The task lifecycle](#the-task-lifecycle)
- [Runbook](#runbook) — [start](#0-start-the-app) · [onboard a repo](#1-onboard-a-new-repo) · [create an issue](#2-create-a-new-issue) · [sync](#3-run-a-sync) · [status & drift](#4-check-status-and-drift) · [log book](#5-read-the-log-book)
- [Surfaces, verified](#surfaces-verified)
- [What is audited — and what is not](#what-is-audited--and-what-is-not)
- [Deliberate non-features](#deliberate-non-features)
- [Install & run](#install--run)
- [Tests](#tests)
- [Docs](#docs)

---

## The thesis

GitHub already holds the work: the issues, the milestones, the review conversations, the
closes. What it does not hold is a **picture**. `gh issue list` across six repos is 400 lines
of text with no shape — you cannot see what is in flight, what is blocked, or what to do next.
GitHub Projects gets closer, but it is a board *for humans clicking buttons*; an agent cannot
ask it "what should I work on, and why".

KanbanFlow does not ask you to move your work. It **projects** GitHub into a project knowledge
graph and gives that graph three surfaces:

1. **A board** — drag-and-drop kanban, project outline, and an ideas inbox. A filter bar over
   the board narrows it as you type — free-text over name and description, plus priority,
   assignee, claimed-by and hide-done, AND-combined and carried in the query string
   (`?q=&priority=&assignee=&claimedBy=&hideDone=`), so a filtered view survives a reload and
   pastes into chat as a link. Filtering never reorders anything: a drag under an active filter
   anchors on the next *visible* card's identity and the server resolves that against the full
   persisted column. For you.
2. **A REST API** — 71 endpoints over the same graph. For scripts.
3. **An MCP server** — the same capabilities as first-class agent tools, plus a direction
   engine that answers *"what next, and why"* with a human-readable reason. For agents.

The projection is deliberately **lossy in one direction and additive in the other**. GitHub
supplies the work items and their conversation; the board supplies the structure an issue
tracker cannot express — a cycle-guarded dependency DAG, acceptance-criteria checklists,
recursive subtasks, agent claims (`ClaimedBy` / `ClaimedAt`), ordering (`SortOrder`),
requirements, and a memory knowledge graph. **None of that has a GitHub representation.**
Editing an issue will never change a dependency edge or tick an acceptance criterion.

Three properties make it safe to point at a real portfolio:

- **GitHub is upstream.** Normal `sync`, `status`, `onboard`, and `backfill-links` only read
  GitHub. `push-issue` previews by default; `--allow-write --task <id>` exposes exactly one
  additive issue create backed by a persisted operation and durable identity. It cannot edit,
  close, delete, or comment on GitHub.
- **It advances your board, and overrides it in exactly one case.** Drag a task to
  `InProgress` and the sync leaves it there — *unless the issue is closed*, in which case the
  task is forced to `Done` regardless of where you put it
  (`syncTasks()` in `portfolio-sync.mjs`). The opposite disagreement — board `Done`, issue still
  open — is reported as **drift** and never touched (`driftDoneButOpen`). That asymmetry is the whole rule; the
  older "never overrides your status" phrasing was half true and has been corrected here.
- **It is idempotent and fail-closed.** Active issue links use immutable GitHub repository and
  issue node ids. Legacy `#<n> ` names are a fallback that an ordinary `sync` promotes to a
  durable link — and strips — as soon as the task's `kanbanflow-sync` block agrees or is
  absent; a block that disagrees is reported as drift instead. Interrupted
  creates reconcile an exact operation marker; ambiguity never automatically authorizes another
  create. Issue
  transfers are detected globally by node: same-project metadata refreshes, while cross-project
  ownership reports actionable drift and never moves or duplicates a task automatically.

---

## How the sync works

```mermaid
flowchart LR
    subgraph GH["GitHub — upstream"]
        direction TB
        REPO["repo"]
        MS["milestone"]
        ISS["issue"]
        IC["issue comment"]
        PR["open PR + reviews<br/>+ inline file:line comments"]
    end

    subgraph ENGINE["sync engine — scripts/portfolio-sync.mjs"]
        direction TB
        READ["gh CLI, read-only<br/>issues · PRs · comments"]
        MATCH["match by durable identity first<br/>repo node · issue node<br/>legacy #n fallback"]
        PLAN["plan writes<br/>--dry-run prints, mutates nothing"]
        READ --> MATCH --> PLAN
    end

    subgraph KF["KanbanFlow graph — projected from GitHub"]
        direction TB
        PRJ["Project"]
        FEAT["Feature"]
        TASK["TaskItem"]
        TC["TaskComment"]
        AE[["ActivityEvent<br/>append-only"]]
    end

    subgraph OWN["board-owned — NO GitHub source, never round-trips"]
        direction TB
        DEP["TaskDependency<br/>cycle-guarded DAG"]
        AC["AcceptanceCriterion"]
        SUB["Subtask (recursive)"]
        REQ["Requirement"]
        CLAIM["ClaimedBy · ClaimedAt · SortOrder"]
        MEM["Memory knowledge graph"]
    end

    subgraph OUT["surfaces over the same graph"]
        direction TB
        UI["Blazor UI<br/>/ · /project/{id} · /ideas · /memory"]
        REST["REST · 71 endpoints"]
        MCP["MCP · 28 native / 50 wrapper"]
    end

    REPO --> READ
    MS --> READ
    ISS --> READ
    IC --> READ
    PR --> READ

    PLAN -->|"POST /api/projects"| PRJ
    PLAN -->|"POST /api/projects/{id}/features"| FEAT
    PLAN -->|"POST /api/features/{id}/tasks<br/>PUT name+priority · PATCH status · PATCH feature"| TASK
    PLAN -->|"POST /api/tasks/{id}/comments"| TC
    TASK -.->|"task writes only"| AE

    TASK --- OWN
    PRJ --> UI
    TASK --> UI
    TC --> UI
    AE --> UI
    OWN --> UI

    DRIFT["drift report<br/>disagreements the sync refuses to resolve"]
    PLAN --> DRIFT
    XBACK["explicit additive create only<br/>prepare · dispatch · create · finalize"]
    KF -.-> XBACK
```

Every sync write goes over **REST**, never straight to SQLite — so the mutations the service
layer owns land in the audit trail like any other. (What that does and does not cover is
[spelled out below](#what-is-audited--and-what-is-not).)

The right-hand box is the part that makes this a projection rather than a mirror: those
entities are created by you and by agents doing real work, live only on the board, and are
invisible to GitHub.

The sync also **cannot** run over the native MCP server: the 28 native tools include
`list_projects` and `get_project_context` but **no `create_project`**, so an agent on `/mcp`
cannot onboard a repo. That parity gap is [#231](https://github.com/joelfuller2016/KanbanFlow-v2/issues/231).

---

## GitHub → KanbanFlow: the mapping

| GitHub concept | KanbanFlow entity | Idempotency key | Direction |
|---|---|---|---|
| Repo | `Project` | immutable `<!-- github-node: R_… -->` marker in `Project.Description`; legacy path marker and short-name match are upgrade-only fallbacks | GitHub → board |
| Milestone | `Feature` | `Feature.Name` == milestone title, within the project; issues with no milestone go to a feature literally named `Unsorted` | GitHub → board |
| Issue | `TaskItem` | Active `ExternalWorkItemLink` keyed by immutable repository node id + issue node id; legacy **`#<n> `** prefix only when no structured state exists | GitHub → board; explicit additive create board → GitHub |
| Issue labels (`P0`…`P3`, `priority:high`, `critical`, …) | `TaskItem.Priority` | — reconciled every run | GitHub → board (GitHub always wins) |
| Issue state + linked open PR | `TaskItem.Status` | — | GitHub → board, **advance-only** (see below) |
| Issue body | `TaskItem.Description` | `<!-- kanbanflow-sync -->` block + first 1500 chars | GitHub → board |
| Issue comment | `TaskComment` (`Comment`) | GitHub node id stamped as `<!-- gh:ID -->` | GitHub → board |
| PR conversation comment | `TaskComment` (`Comment`) | same `<!-- gh:ID -->` marker | GitHub → board |
| PR review, `APPROVED` | `TaskComment` (**`Verification`**) | same `<!-- gh:ID -->` marker | GitHub → board |
| PR review, `CHANGES_REQUESTED` | `TaskComment` (**`Blocked`**) | same `<!-- gh:ID -->` marker | GitHub → board |
| PR inline `file:line` review comment | `TaskComment` (`Comment`) | same `<!-- gh:ID -->` marker | GitHub → board |
| *(every task mutation the sync makes)* | `ActivityEvent` | — append-only | board-internal |
| Board-only task | GitHub issue | durable `ExternalWriteOperation` UUID embedded as `<!-- kanbanflow-push:… -->`, then finalized to an Active link | board → GitHub, explicit additive create only |

**Status is deliberately asymmetric — and one row does override you.** On create it is derived
from GitHub (`closed` → `Done`; open + linked open PR → `Review`; open + assignee →
`InProgress`; else `Backlog`). On update:

| Condition | Action | Overrides a hand-set status? |
|---|---|---|
| issue closed, task ≠ `Done` | → `Done` | **Yes.** Whatever column you put it in, a closed issue forces `Done` (`syncTasks()` in `portfolio-sync.mjs`) |
| issue open, linked open PR, task ∈ {`Backlog`, `Todo`, `InProgress`} | → `Review` | Partly — it advances those three, and will not touch `Review` or `Done` |
| issue open, task = `Done` | **drift** — reported, not touched | No |
| task's issue is in neither the open nor the closed set | **drift** — deleted or transferred; never auto-deleted | No |
| anything else | leave the board alone | No |

> The frequently-repeated line *"the sync advances status but never overrides yours"* is **half
> true**. Row 1 is a genuine override. If you deliberately park a task in `Todo` while its issue
> is closed, the next sync will move it to `Done` and keep doing so.

**Legacy and protocol markers are data, not decoration:**

- Active linked tasks have plain names and survive arbitrary task/issue renames. On a task that
  has not yet been promoted or backfilled, stripping the legacy `#<n> ` prefix can still orphan
  the fallback.
- Edit a mirrored comment's text and you strip its `<!-- gh:ID -->` marker — the next sync
  re-adds it as a duplicate. The marker is hidden where the body renders as Markdown
  (`MarkdownRenderer` sanitises HTML comments away) but **plainly visible in raw reads**:
  `GET /api/tasks/{id}/comments`, a `curl`, and any agent pulling the comment over MCP all
  see it.

**An ordinary `sync` is the upgrade path — `backfill-links` is not a required first step.** When
a legacy `#<n> ` prefix matches an issue in the repo being synced, the sync registers the durable
link and strips the prefix in the same run, provided the task's `kanbanflow-sync` block agrees
about the repo and the issue number, or carries no `**Issue:**` URL at all
(`legacyPromotionDisposition` in `portfolio-sync.mjs`). An upgraded board therefore heals itself
on the first sync, with no operator action. Promotion is **refused** — the prefix kept,
the task reported as drift — when that block names a different repo or a different issue number,
when its provenance is malformed, or when the issue record carries no immutable node id. Full
case list: [`docs/GITHUB-SYNC.md`](docs/GITHUB-SYNC.md#legacy-prefix--durable-link).

`backfill-links owner/repo` is the **repair and audit** tool for what auto-promotion will not
touch. Run it with `--dry-run` to review those candidates. Only an authoritative issue URL inside
a sync block can proceed to immutable-node verification. A different repository
path is treated as metadata drift and must resolve to the same immutable issue node before the link
is activated; title-only, duplicate, and malformed candidates are persisted as quarantined for
human review.
Reruns leave quarantines in place until a human promotes one through the verified link PUT. A
quarantine with no correlatable issue number blocks projection of otherwise-unmatched issues for
that project instead of allowing a duplicate. Because a live `backfill-links` **quarantines** a
title-only `#<n> ` task that an ordinary sync would have promoted, run `sync` first and reach for
`backfill-links` afterwards, for the residue.

Full semantics, including truncation warnings, comment sizing, and every drift case:
[`docs/GITHUB-SYNC.md`](docs/GITHUB-SYNC.md) and
[`.claude/skills/kanban-portfolio/reference/mapping.md`](.claude/skills/kanban-portfolio/reference/mapping.md).

---

## Who owns what

The single most useful table here. Before you edit a field, know which side wins.

| Field | Owner | What that means in practice |
|---|---|---|
| Task **title** | **GitHub content; not identity** | Reconciled every run (`syncTasks()`). Active linked tasks show the plain issue title and identity survives renames. Legacy fallback tasks keep `#<n> ` only until the first sync that promotes them |
| Task **priority** | **GitHub** | Reconciled every run (`syncTasks()`) from `P0`…`P3` / `priority:high` / bare severity labels. Setting it on the board is undone on the next sync |
| Task **status** | **Shared, GitHub-dominant** | You may move it freely, **except** that a closed issue forces `Done` (`syncTasks()`). Board `Done` + open issue is reported as drift, not corrected |
| Task **feature** (milestone) | **GitHub** | An issue's milestone change moves the task via `PATCH /api/tasks/{id}/feature` (`syncTasks()`) |
| Task **description** | **GitHub, once** | Written from the issue body **only at creation**, truncated to 1,500 chars. It is **never refreshed** — the update patch carries only name, priority, status |
| **Comments** | **GitHub, append-only** | Issue comments, PR conversation, PR reviews, and inline `file:line` comments are appended, deduped by node id. Board-authored comments are never pushed |
| **`ActivityEvent`** | **Derived** | Written by the service layer as a side effect of task writes. Never read from or sent to GitHub |
| `TaskDependency` (DAG) | **Board** | No GitHub representation. Authored by you and by agents |
| `AcceptanceCriterion` | **Board** | No GitHub representation |
| `Subtask` (recursive) | **Board** | No GitHub representation |
| `Requirement` | **Board** | No GitHub representation |
| `ClaimedBy` / `ClaimedAt` | **Board** | Agent coordination primitive. GitHub assignees do **not** populate it |
| `SortOrder` | **Board** | Your manual ordering within a column. Survives sync |
| Memory graph (`MemoryNode` / relations / observations) | **Board** | Entirely local |
| `Idea` inbox | **Board** | Pre-project capture; promotion creates a Project, never a repo |

The read-only **Decision & Memory Map** at `/memory` visualizes that board-owned graph without
copying it into another store. It defaults to the same Agent / `knowledge-graph` namespace used by
the native MCP memory tools, classifies the tools' free-form `entityType` tags into readable lanes,
and provides both a relationship map and an accessible list/detail view. The UI only reads records
deliberately stored in the graph; it does not request, infer, or reconstruct model-hidden reasoning.
Capture clients must never write private scratchpads or secrets into this open local store. The read
service returns at most 900 source nodes; the UI then bounds one view to 80 nodes, 320 unique directed
edges, and 50 observations per selected record, with explicit warnings whenever a view is incomplete.

**Derived, not owned by either side:** task status *on create* (computed from issue state +
linked PR + assignee presence), the `Feature` named `Unsorted` (synthesised for issues with no
milestone), the `<!-- kanbanflow-sync -->` description block, and `isBlocked` /
`openBlockerCount` (computed from the board-owned dependency DAG at read time).

---

## Known omissions

What the sync **does not** do. Each of these is verified behaviour, not speculation — an agent
that assumes otherwise will be wrong.

| Omission | Detail |
|---|---|
| **Issue body is never refreshed** | Written once at task creation, truncated at `BODY_EXCERPT_LIMIT = 1500` (`portfolio-sync.mjs`). Edit an issue body and the board keeps the original excerpt forever |
| **Closed issues are fetched as numbers only** | Without `--include-closed` the sync pulls only issue *numbers* for closed issues (`fetchClosedIssues(repo, full)`), enough to converge a task to `Done` — their bodies and comments are not mirrored |
| **Assignees are not synchronised** | GitHub assignees are read once, only to infer `InProgress` on create (`deriveStatus()`). `TaskItem.Assignee` is never populated from GitHub |
| **Labels collapse to one priority** | The first matching severity label becomes `TaskItem.Priority`. Every other label survives only as text inside the description block — there is no label entity, and no filtering by label on the board |
| **Comment edits, deletions, and reactions are never reconciled** | Dedupe is by GitHub node id; an id already seen is skipped forever. An edited GitHub comment keeps its original mirrored text, and a deleted one stays on the board |
| **PR comments on a PR that closes no issue are dropped** | With no `Closes #n` there is no task to attach them to. They are counted and reported, never silently discarded |
| **Inline review comments cost one API call per PR** | Fetched via `gh api repos/{repo}/pulls/{n}/comments`. A failure is reported and sets a non-zero exit code — the board is explicitly flagged incomplete rather than looking whole |
| **Fetches are capped per list** | `GH_PAGE_LIMIT` defaults to `2000` (`KANBANFLOW_GH_PAGE_LIMIT` overrides it). Hitting the cap is a hard failure, not a warning — a count derived from a partial read is worse than no count |
| **No general board → GitHub editing** | The only outbound path creates one issue after `--allow-write --task`. Titles/bodies/status/comments of existing issues are never written back; no issue is closed or deleted |
| **Board-owned structure never round-trips** | Dependencies, acceptance criteria, subtasks, requirements, claims, ordering, and memory have no GitHub equivalent. See [Who owns what](#who-owns-what) |
| **Milestones are flattened** | Features are created flat; GitHub milestones are not hierarchical, so the recursive-feature capability is unused by the sync |
| **Project ↔ repo link has no dedicated column** | There is still no `RepoUrl` column ([#237](https://github.com/joelfuller2016/KanbanFlow-v2/issues/237)), but the link is durable anyway: immutable `<!-- github-node: R_… -->` is primary and survives rename/transfer; mutable `<!-- github: owner/repo -->` is refreshed display/compatibility metadata. Short-name matching is an upgrade-only fallback, and `onboard` stamps both markers |

---

## The task lifecycle

A card's column *is* its status. Five things can change it — dragging, the done checkbox, REST,
MCP, and the next sync after the issue closes. The first four are yours; the fifth can
overrule them, in the one case noted below.

```mermaid
stateDiagram-v2
    direction LR

    [*] --> Backlog : issue opened / synced
    Backlog --> Todo : you triage it
    Todo --> InProgress : claim_task / assignee on GitHub
    InProgress --> Review : PR opened with "Closes #n"
    Review --> Done : issue closed on GitHub
    InProgress --> Done : you tick it off

    Review --> InProgress : changes requested
    Done --> Todo : reopened

    note left of Backlog
        Sync creates it here unless GitHub
        already says otherwise.
    end note

    note right of Review
        The sync ADVANCES Backlog/Todo/InProgress
        to Review when a PR says "Closes #n",
        and FORCES Done when the issue closes -
        overriding whatever column you chose.
        It never drags a task backwards.
    end note

    note right of Done
        Board says Done but the issue is
        still open? That is DRIFT: reported,
        never silently corrected.
    end note
```

A task with an unfinished dependency is **blocked**: excluded from `next-tasks`
recommendations and flagged `isBlocked` / `openBlockerCount` in step context. Those
dependencies are **board-owned** — GitHub has no equivalent, so blocking is something the
board knows and the issue tracker cannot. The direction engine ranks what is left by priority,
then status, then oldest-updated, and hands back a human-readable `reason` per pick.

---

## Runbook

Everything below was executed as written on Windows 11 / PowerShell 7 with Git Bash available.
Commands are portable except where noted; `smoke-api.ps1` and `verify.ps1` are PowerShell.

### 0. Start the app

```powershell
$env:KANBANFLOW_DB_PATH = "$HOME\.kanbanflow\kanbanflow.db"
dotnet run --project src/KanbanFlow/KanbanFlow.csproj --urls http://127.0.0.1:5123
```

SQLite creates itself on first run with seed data. No setup, no Docker, no accounts.

> **Use `--urls http://127.0.0.1:5123`.** The launch profile defaults to `:5148`, but the MCP
> wrapper, `smoke-mcp.mjs`, `portfolio-sync.mjs`, and `portfolio-standup.mjs` all default to
> `:5123`. Standardising the profile on `:5123` is on the roadmap; until then pass the flag or
> set `KANBANFLOW_API_URL` to match.

Confirm it is up:

```bash
curl -s http://127.0.0.1:5123/api/admin/health
# {"status":"ok","app":"KanbanFlow","startedAtUtc":"2026-08-02T22:26:22.1897449Z",
#  "checkedAtUtc":"2026-08-02T22:26:30.7731684Z","runtime":".NET 10.0.10",
#  "openApiUrl":"/openapi.json",
#  "db":{"path":"C:\\Users\\you\\.kanbanflow\\kanbanflow.db","exists":true,"source":"env",
#        "existedAtStartup":true,"createdAtUtc":"2026-06-01T09:12:44.1Z",
#        "lastWriteUtc":"2026-08-02T22:19:03.7Z","sizeBytes":39124992,
#        "shadowedPaths":[],"projects":1,"status":"ok","error":null,
#        "reasons":[],"elapsedMs":1.1}}
```

`status` is `ok` only when the app opened the database, read from it, found a board that is not
empty, did not create that board itself, saw no trace of that board having been copied into place
just before startup, and saw no second `kanbanflow.db` shadowing it — otherwise `degraded`, or
`error` if it could not read at all. It is **always HTTP 200**, so the reason is readable rather
than guessed.

If you started the app and it is serving a board you do not recognise, this is where to look:

```text
"status":"degraded", "reasons":["last-write-precedes-creation","created-moments-before-startup"]
```

That means the file was **copied into place moments before the app started** — a board written
before it was created cannot be one you have been using. The usual cause is the launcher failing to
find your real database (`$HOME` resolves differently under Task Scheduler than in your shell) and
restoring the committed seed instead. Compare `db.sizeBytes` with the size of
`seed/kanbanflow.seed.db`: a match is **consistent with** the seed rather than proof of it —
SQLite reuses freed pages instead of growing the file, so a board that has been written to can
still report the seed's size to the byte. Pin the right file with `-DbPath` and restart.

**A fresh install reports `degraded` too, and that is expected.** The first start of a new
deployment restores the seed, so the board genuinely was copied into place moments before the app
ran and both reasons fire — correctly. With `-NoSeed` nothing is copied at all, yet a restart
within 15 minutes still reports `created-moments-before-startup`, because a board that new is not
distinguishable from a restored one. Neither is a fault. The first reason clears on a restart after
the board has been written; the second clears on a restart more than 15 minutes after the file was
created. Confirm `db.path` is the file you meant before you go looking for a cause.

These signals use the **file facts captured at startup**. A write to the wrong board moves its
last-write time past its creation time on disk, but `last-write-precedes-creation` remains in the
running process's health response until a restart captures the changed file. A write does nothing to
`created-moments-before-startup`, which reads the creation time and a write never touches that; that
one lasts until the next start more than 15 minutes after the file was created, whether or not the
board has been written to. So a wrongly-restored board can keep reporting `degraded` after you have
been using it, and — the direction that costs you — a board written to and then restarted a day later
reports a clean `ok` with both signals gone. `db.path` is the durable answer: compare it with the
file you meant, which is the check no code can make for you (#498).

The shadow-database list is also captured at startup: removing a reported shadow file does not
clear its warning until a restart. Each health request reads the current project count and reports
whether the selected database currently exists, alongside those recorded startup facts.

**Which database it opened.** The app names the file it is using, on every boot:

```text
info: KanbanFlow[0]
      [db] kanbanflow.db -> C:\...\src\KanbanFlow\kanbanflow.db (source=probe, existing=true)
```

`source` says how the file was chosen — `env` (`KANBANFLOW_DB_PATH` was set), `probe` (an existing
`kanbanflow.db` was found) or `default` (none existed anywhere, so this is a new database). The same line
appears on the Settings page. Resolution order, with `KANBANFLOW_DB_PATH` unset: the directory the
app was launched from, then the content root, then the application's own directory — the first one
that already holds a `kanbanflow.db` wins, and if nothing does, one is created beside the
application. That last step is what makes the location independent of where you started the app
from; if a second `kanbanflow.db` exists at another candidate, a warning names it rather than
silently opening an empty board (#354).

Custom database location — this override wins over every candidate above:

```powershell
$env:KANBANFLOW_DB_PATH = "C:\path\to\my-projects.db"
dotnet run --project src/KanbanFlow/KanbanFlow.csproj --urls http://127.0.0.1:5123
```

### 1. Onboard a new repo

Onboarding *is* the first sync. There is no separate "create project" step — point the sync at
a repo and it creates the `Project`, the milestone `Feature`s, and every issue as a `TaskItem`.

Requires an authenticated [`gh` CLI](https://cli.github.com/) (`gh auth status` to check).

```bash
# 1. See exactly what would be written. Mutates nothing.
node scripts/portfolio-sync.mjs --dry-run --repos joelfuller2016/KanbanFlow-v2

# 2. Happy with the plan? Run it for real.
node scripts/portfolio-sync.mjs --repos joelfuller2016/KanbanFlow-v2
```

**`onboard` is the explicit version of step 2's first half.** It resolves (or creates) the
`Project` for a repo and stamps immutable `<!-- github-node: R_… -->` plus the refreshable
`<!-- github: owner/repo -->` marker into
`Project.Description` — including onto a legacy project that was previously linked by name
alone. It is safe to re-run:

```bash
node scripts/portfolio-sync.mjs onboard joelfuller2016/KanbanFlow-v2
```

To onboard several at once, or to make a repo set the default for every later run:

```bash
node scripts/portfolio-sync.mjs --repos owner/alpha,owner/beta,owner/gamma
```

```powershell
$env:KANBANFLOW_PORTFOLIO_REPOS = "owner/alpha,owner/beta"
node scripts/portfolio-sync.mjs
```

> **Run `--dry-run` first on a board that already has hand-made projects.** A project whose
> name happens to equal a repo's short name **will be adopted** by the sync — short-name
> matching is the fallback the durable marker does not replace, because a project that predates
> the marker has nothing else to match on. Once adopted (or onboarded) it carries a marker and
> the name stops mattering: rename it freely. [#237](https://github.com/joelfuller2016/KanbanFlow-v2/issues/237)
> would replace the marker with a real column, but the link is no longer waiting on it.

### 2. Create a new issue

GitHub remains the substrate. You can create there directly:

```bash
gh issue create --repo joelfuller2016/KanbanFlow-v2 \
  --title "Board should show the linked PR on a task card" \
  --body  "Objective / Requirements / Goals (AC) / Plan — see docs/issue-quality-checklist.md" \
  --label "P2,enhancement" \
  --milestone "M5: Docs & Process"
```

Then bring it onto the board:

```bash
node scripts/portfolio-sync.mjs --repos joelfuller2016/KanbanFlow-v2
```

The new issue arrives as a linked `TaskItem` with a plain title, filed under the milestone's
`Feature` (or `Unsorted`), with priority from the `P2` label and status `Backlog`.

Repo issue templates live in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE); what a
well-formed issue contains is in [`docs/issue-quality-checklist.md`](docs/issue-quality-checklist.md).

Or preview one board-only task, then opt into one additive create:

```bash
node scripts/portfolio-sync.mjs push-issue joelfuller2016/KanbanFlow-v2 --task 123
node scripts/portfolio-sync.mjs push-issue joelfuller2016/KanbanFlow-v2 --allow-write --task 123
```

There is no GitHub issue-create idempotency key. KanbanFlow persists `Prepared` then
`Dispatched`, embeds an exact operation marker, and finalizes the immutable link. If interrupted,
use the operation UUID printed by the failure:

```bash
node scripts/portfolio-sync.mjs push-issue joelfuller2016/KanbanFlow-v2 \
  --allow-write --task 123 --resume 00000000-0000-4000-8000-000000000000
```

`Dispatched`/`NeedsReview` recovery performs a complete paginated marker scan and never
auto-creates. Authorization uses raw occurrences: duplicate marker copies, a marker mixed with
another operation marker, copies across issues, or an incomplete read all fail closed.

For a persistent zero-occurrence `NeedsReview`, an operator may record (not execute) one manual
retry decision. The UUID must be repeated and an explicit actor/reason are mandatory:

```bash
node scripts/portfolio-sync.mjs push-issue joelfuller2016/KanbanFlow-v2 \
  --allow-write --task 123 --resume 00000000-0000-4000-8000-000000000000 \
  --authorize-ambiguous-retry 00000000-0000-4000-8000-000000000000 \
  --actor operator-name --resolution-reason "Reviewed the zero-marker audit"
```

That invocation reads GitHub exhaustively but exits without mutating it or invoking issue create.
A separate normal `--resume` first claims and timestamps the next journal attempt, then scans every
issue again: one late marker finalizes, ambiguity blocks, and only a post-Resume zero scan permits
an evidence-bound dispatch of the same frozen create. The raw
counter includes malformed target tokens and PR records. This is an audited residual-risk escape hatch, not exactly-once proof—GitHub has no
issue-create idempotency key and a negative-read race remains. See
[`docs/GITHUB-SYNC.md`](docs/GITHUB-SYNC.md#safe-push-issue-preview-live-create-and-recovery).

### 3. Run a sync

```bash
# Default repo set (or $KANBANFLOW_PORTFOLIO_REPOS), full comment mirroring.
node scripts/portfolio-sync.mjs

# Also mirror closed issues as Done tasks.
node scripts/portfolio-sync.mjs --include-closed

# Skip comment mirroring — much faster on repos with heavy bot review threads.
node scripts/portfolio-sync.mjs --no-comments

# Cap each mirrored comment (a single CodeRabbit review has measured 110,574 chars).
node scripts/portfolio-sync.mjs --max-comment-chars 4000

# Label the audit trail so you can tell this run apart later.
node scripts/portfolio-sync.mjs --actor nightly-sync

node scripts/portfolio-sync.mjs --help
```

The run prints a plan line per write, then a summary. Onboarding this repo from an empty
board produced:

```text
── summary ──
projects created: 1
features created: 8
tasks created:    100
tasks updated:    0
tasks moved:      0
tasks unchanged:  0
comments mirrored: 404
comments existing:   0

  ! biggest comment: 110,574 chars from coderabbitai on #149.
    Mirrored whole. Use --max-comment-chars N to cap it (agents reading this task over MCP pay for it).
```

**A second consecutive run creates nothing** — that is the idempotency contract. Immediately
re-running the same command gave:

```text
── summary ──
projects created: 0
features created: 0
tasks created:    0
tasks updated:    0
tasks moved:      0
tasks unchanged:  100
comments mirrored:   0
comments existing: 404
```

If a re-run keeps creating tasks, inspect the durable link/quarantine rows first; only legacy
tasks without structured state depend on the `#<n> ` fallback.

### 4. Check status and drift

Three questions, three commands.

**"Is it safe to act on this board right now?"** — `status`. This is the dedicated health check:
read-only, and it answers with an **exit code** so a script or an agent can gate on it rather
than parsing prose.

```bash
node scripts/portfolio-sync.mjs status && echo "safe to act"
node scripts/portfolio-sync.mjs status --repos joelfuller2016/KanbanFlow-v2
```

| Exit | Meaning |
|---|---|
| `0` | Clean **and complete** — everything was read, and nothing disagrees |
| `1` | Confirmed drift. With `--strict`, also pending work, unsynced tasks, or a non-durable link |
| `2` | **Incomplete or error** — a repo could not be read, or a fetch hit its item bound |

Exit `2` dominates `1`: "I could not see everything" is a different answer from "I looked and it
is clean", and a drift count computed over a partial read is worse than no count at all. See
[`docs/GITHUB-SYNC.md`](docs/GITHUB-SYNC.md) for the full contract — including the fact that
`status --actor <name>` reports "last sync" only for syncs run under that **same** `--actor`.

**"Is the board still in agreement with GitHub?"** — the drift block at the end of any sync,
including a dry run:

```bash
node scripts/portfolio-sync.mjs --dry-run
```

```text
── drift (2) — board and GitHub disagree ──
  ! owner/repo#144: board says Done, issue is still open — https://github.com/owner/repo/issues/144
  ! owner/repo: task "#98 Old thing" has no matching issue #98 on GitHub (deleted or transferred?)
```

Drift is never auto-resolved. Close the issue, or move the card — the sync will not choose for
you, and it never deletes a task (the audit trail is sacred).

**"What is actually going on across everything?"** — the standup. Read-only; writes nothing,
anywhere. It rolls each project to one row, asks the direction engine what matters, and
overlays live GitHub state (open PRs, PRs stale without review >3 days, tasks parked
`InProgress` >14 days):

```bash
node scripts/portfolio-standup.mjs
node scripts/portfolio-standup.mjs --repos joelfuller2016/ZeroRouter
node scripts/portfolio-standup.mjs --help
```

Or ask an agent, over MCP, per project:

```text
get_next_tasks(projectId: 1, limit: 5)   → ranked picks, each with a reason
get_step_context(taskId: 42)             → everything needed to act on one task
```

### 5. Read the log book

**[`docs/BACKLOG.md`](docs/BACKLOG.md) is the authoritative log book** — the durable, ordered
record of what was decided and what happened. Read it before starting work; it answers "why is
this the way it is" faster than any query.

The live equivalents, when you need the current state rather than the history:

```bash
# Every recorded mutation on a project, newest first (limit clamped to 500).
curl -s "http://127.0.0.1:5123/api/projects/1/activity?limit=50"

# One task's history.
curl -s "http://127.0.0.1:5123/api/tasks/42/activity"

# One task's full context: description, criteria, subtasks, dependencies,
# comments (including every mirrored GitHub comment), and recent activity.
curl -s "http://127.0.0.1:5123/api/tasks/42/context"
```

In the browser, open a project at `/project/{id}` and click any card — the detail panel carries
the same comment stream and history.

---

## Surfaces, verified

[Issue #277](https://github.com/joelfuller2016/KanbanFlow-v2/issues/277) exists because this
README under-reported its own surface. Each number below was re-derived for this revision by
the command beside it — not copied from the previous README, and not copied from the issue.

| Surface | Count | Command that produced it |
|---|---:|---|
| REST endpoints (app) | 66 | `grep -cE '\.Map(Get\|Post\|Put\|Patch\|Delete)\(' src/KanbanFlow/ApiEndpoints.cs` |
| REST endpoints (admin) | 5 | `grep -cE '\.Map(Get\|Post\|Put\|Patch\|Delete)\(' src/KanbanFlow/AdminEndpoints.cs` |
| **REST endpoints total** | **71** | *sum of the two above* |
| Native MCP tools (`POST /mcp`) | 28 | `grep -c '\[McpServerTool(' src/KanbanFlow/Mcp/KanbanMcpTools.cs` |
| Node-wrapper MCP tools | 50 | `tools/list` over stdio against `scripts/mcp-server.mjs` — [exact invocation](docs/ARCHITECTURE.md#mcp-surface-28-native--50-wrapper) |
| Blazor routes | 6 | `grep -rn '@page' src/KanbanFlow/Components/ \| sort -u` → `/`, `/Error`, `/ideas`, `/memory`, `/project/{Id:int}`, `/settings` |
| Razor components | 28 | `find src/KanbanFlow/Components -name '*.razor' \| wc -l` |
| xUnit tests | 1,079 | `dotnet test tests/KanbanFlow.Tests` |
| In-app integration tests | 56 | `grep -c 'results.Add(await RunTest' src/KanbanFlow/Services/TestRunnerService.cs` (28 API + 22 service + 6 UI) |
| Playwright e2e scenarios | 52 | `npm run test:ui` |
| Script unit tests (`scripts/*.mjs`) | 274 | `npm run test:scripts` |

Corrections this revision made to the previous README: **53 → 60** endpoints previously (both memory
endpoints and `GET /api/tasks/{id}/context` were missing), then **60 → 71** for the #336
external-identity/write-journal routes;
**41 → 50** wrapper tools; native tool count stated for the first time (**28**); in-app suite
**40 → 56**; and `GET /api/subtasks/{id}`, `GET /api/acceptance-criteria/{id}`,
`GET /api/requirements/{id}` were listed as existing — **they do not exist**. The route map in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#rest-surface-71-endpoints) now marks them.

Also corrected: `#82`, `#83`, `#85`, `#88`, and `#89` were all cited as open/broken. All five
are **closed**, and the code enforces what they asked for.

---

## What is audited — and what is not

Every mutation *should* write an immutable `ActivityEvent`. **It does not yet.** The previous
README called this "non-negotiable ✅ for REST"; that was not true, and an agent that trusts a
partial history draws wrong conclusions. Here is the real coverage:

| Entity | Create | Update | Delete | Writes `ActivityEvent`? |
|---|:--:|:--:|:--:|---|
| `TaskItem` (incl. status, claim, release, reassign) | ✅ | ✅ | ✅ | **Yes** — via `ITaskService`, in one `SaveChanges` |
| `TaskComment` (add) | ✅ | — | ❌ | **Yes** on add via `ITaskService`; **no** on `DELETE /api/comments/{id}` |
| `Idea` (incl. promote) | ✅ | ✅ | ✅ | **Yes** — via `IIdeaService` |
| `Project` | ✅ | ✅ | ✅ | **Delete only.** Create and update write raw EF |
| `Requirement` | ✅ | ✅ | ✅ | **Assign only** (`PATCH /assign`). Create, update, delete write raw EF |
| `Feature` | ✅ | ✅ | ✅ | **No** |
| `Subtask` | ✅ | ✅ | ✅ | **No** |
| `AcceptanceCriterion` | ✅ | ✅ | ✅ | **No** |
| `TaskDependency` | ✅ | — | ✅ | **No** |

The mechanical evidence — the entire REST layer has exactly one inline audit call site:

```bash
grep -n 'AddActivityEvent(' src/KanbanFlow/ApiEndpoints.cs
# 2247:  AddActivityEvent(..., "RequirementAssigned", ...)   <- the only call
# 2364:  private static void AddActivityEvent(...)           <- the helper itself
```

Closing this is [#149](https://github.com/joelfuller2016/KanbanFlow-v2/issues/149) (extend
`ActivityEvent` to all entities). Two related issues are **already closed** and should not be
cited as gaps: [#88](https://github.com/joelfuller2016/KanbanFlow-v2/issues/88) routed UI
mutations through the shared service layer, and
[#85](https://github.com/joelfuller2016/KanbanFlow-v2/issues/85) made the audit log survive
entity deletion. [#302](https://github.com/joelfuller2016/KanbanFlow-v2/issues/302) tracks the
REST edge layer re-declaring policy the service layer already owns.

**What you can rely on today:** task and idea history is complete and identical across UI,
REST, and MCP, and it survives deletion. **What you cannot:** the shape of the graph around a
task — its features, subtasks, criteria, and dependencies — has no history at all.

---

## Deliberate non-features

These are settled charter decisions, not backlog items. Proposals to add them have been
filed and rejected; please read the linked issue before filing another.

| Not here | Why |
|---|---|
| **No CI / no GitHub Actions** | Single-trust-boundary, local-first tool. The gate is [`scripts/verify.ps1`](scripts/verify.ps1), run before every push, plus externally-scheduled [`scripts/pr-ci.ps1`](scripts/pr-ci.ps1) and [`scripts/master-health.ps1`](scripts/master-health.ps1) — same build + tests, no workflow file. See [`docs/ci-without-actions.md`](docs/ci-without-actions.md). |
| **No authentication or authorisation** | One machine, one owner, one agent. There is no second principal to authorise against. |
| **No Docker** | One SQLite file and `dotnet run`. A container adds a layer and removes nothing. |
| **No HTTPS, CORS lock, loopback pin, or rate limiting** | **Open access by design** — trust the network. [#169](https://github.com/joelfuller2016/KanbanFlow-v2/issues/169) removed the last host lock; [#84](https://github.com/joelfuller2016/KanbanFlow-v2/issues/84) and [#161](https://github.com/joelfuller2016/KanbanFlow-v2/issues/161) are the rejected hardening proposals. |
| **No multi-tenancy** | One database, one user. Per-project ownership scoping is not a goal. |
| **No real-time multi-human collaboration** | Single-user UX. Refresh on demand. |
| **No notifications** | The agent is the integration. |
| **No destructive/broad GitHub writes** | The sole outbound mutation is one explicitly gated additive issue create. There is no edit, close, delete, comment, bulk-create, or implicit retry path; generic `gh()` is structurally read-only. |
| **No mobile-first UI** | Desktop primary; existing responsive work is a courtesy, not a target. |

If you need something in the right-hand column, this is not the tool.

---

## Install & run

```powershell
git clone https://github.com/joelfuller2016/KanbanFlow-v2.git
cd KanbanFlow-v2
dotnet run --project src/KanbanFlow/KanbanFlow.csproj --urls http://127.0.0.1:5123
# open http://127.0.0.1:5123
```

Prerequisites: **.NET 10 SDK**. For the sync and MCP wrapper: **Node 18+** and an authenticated
**`gh` CLI**. For the browser e2e suite: `npm install` then `npx playwright install chromium`.

Client-side libraries (Bootstrap, SortableJS) are vendored under `wwwroot/lib` and pinned in
`libman.json`:

```powershell
dotnet tool install -g Microsoft.Web.LibraryManager.Cli   # once
libman restore                                            # fetch the pinned versions
libman update bootstrap                                   # bump one
```

Optional: KanbanFlow can talk to an OpenAI-compatible LLM endpoint and an embedding endpoint
(both **fail-soft** — unreachable means a typed error result, never a 500). Configuration and
self-test endpoints: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#optional-llm--embedding-configuration).

### Claude Code skills

Two skills ship in `.claude/skills/`. Pick by scope.

| Skill | Answers | Use when |
|---|---|---|
| [`kanban-project-ops`](.claude/skills/kanban-project-ops/SKILL.md) | *"What do I do next **in this project**, and how do I record it?"* | Working a task: `get_next_tasks` → `get_step_context` → `claim_task` → do it → `add_task_comment` → `update_task_status`. |
| [`kanban-portfolio`](.claude/skills/kanban-portfolio/SKILL.md) | *"What am I working on **across everything**?"* | Status, standups, "what's in flight", "what am I forgetting" — across more than one repo. Or when the board looks empty. |

---

## Tests

All local. There is no CI — [on purpose](#deliberate-non-features).

```powershell
./scripts/verify.ps1                    # the gate: restore + build (warnings=errors) + test + format
```

```bash
dotnet test tests/KanbanFlow.Tests      # 1,079 xUnit tests
```

```powershell
./scripts/smoke-api.ps1                 # boots its own app on a free loopback port,
                                        # ~75 HTTP calls including negative paths
```

```bash
node ./scripts/smoke-mcp.mjs 1          # MCP tool surface — needs the app running on :5123
npm run test:mcp                        # stdio framing A/B + the MCP smoke suite
npm run test:scripts                    # node:test units for scripts/*.mjs — the shared
                                        # priority model behind portfolio-sync and the
                                        # log book (#360). No app, no network, no DB.
npm run test:ui                         # Playwright e2e — builds, boots an isolated instance
                                        # (fixed port 5198 + throwaway DB), drives chromium, tears down
```

> **`test:ui` binds a fixed port (`5198`).** Two concurrent runs — two agents, or you and an
> agent — share that port but get *separate* throwaway databases, so one run's API calls land on
> the other's app and fail as `Feature <n> not found` rather than as a port clash. Override with
> `UI_E2E_PORT=<free port> npm run test:ui` when anything else might be running the suite.

```bash
# The in-app integration suite, from inside the live app.
# Also available as "Run all" at /settings.
curl -X POST "http://127.0.0.1:5123/api/admin/run-tests?suite=all"   # suite=api|ui|service|all
```

Build and tests are the hard gates in `verify.ps1`. The **format check is advisory** — it
prints a warning and lets the run continue, pending a one-time `dotnet format` cleanup of the
existing tree.

On a clean build + test run, `verify.ps1` exits **0** even when the formatter prints its advisory
warning. It exits **1** on a hard restore/build/test failure. Keep the advisory in the run record;
do not misreport it as a product-test failure or ignore it as nonexistent drift.

Feature-level test contracts and immutable execution records live in
[`docs/test-cases/`](docs/test-cases/). They complement the executable tests; they do not replace
them.

---

## Docs

| Doc | What it holds |
|---|---|
| [`docs/BACKLOG.md`](docs/BACKLOG.md) | **The log book.** Authoritative record of what happened and why. Start here. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Entity model, write path, full REST route map, MCP inventory, capability matrix, known gaps |
| [`docs/GITHUB-SYNC.md`](docs/GITHUB-SYNC.md) | Full sync semantics, drift cases, comment mirroring |
| [`.claude/skills/kanban-portfolio/reference/mapping.md`](.claude/skills/kanban-portfolio/reference/mapping.md) | GitHub → board mapping in full, with the reasoning |
| [`docs/ci-without-actions.md`](docs/ci-without-actions.md) | How the quality gate runs with no workflow file |
| [`docs/issue-quality-checklist.md`](docs/issue-quality-checklist.md) | What a well-formed issue contains |
| [`docs/test-cases/`](docs/test-cases/) | Feature-level test contracts, traceability, and commit-pinned execution records |
| [`docs/native-mcp-notes.md`](docs/native-mcp-notes.md) | Native in-process MCP implementation notes |
| [`docs/ai-briefing-schema.md`](docs/ai-briefing-schema.md) | The agent briefing payload shape |
| [`docs/requirements.md`](docs/requirements.md) | Original v1 requirements — partly superseded by `ARCHITECTURE.md` |
| [`docs/reviews/`](docs/reviews) | Historical review notes |

> `docs/BACKLOG.md` and `docs/GITHUB-SYNC.md` are authored on sibling branches and land with
> them; on a branch that has only this README the two links will 404 until those merge.

---

## License & contributing

Licensed under the [MIT License](LICENSE).

Single-developer project. Issues use the templates under
[`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE), PRs follow
[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md), and
`./scripts/verify.ps1` must pass before pushing. If you fork it, the data model is small enough
to skim in an afternoon — start with `src/KanbanFlow/Models/`, then `Data/AppDbContext.cs`,
then `ApiEndpoints.cs`.
