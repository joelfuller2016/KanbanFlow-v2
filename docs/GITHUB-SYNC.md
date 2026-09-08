# GitHub Sync — KanbanFlow *is* GitHub, visualised

GitHub is the source of truth. The KanbanFlow board is the visualisation: the same issues,
milestones, labels and review conversation, arranged so a human (or an agent) can see the
whole portfolio at once and ask "what next?".

One script drives the whole lifecycle: **`scripts/portfolio-sync.mjs`**.

```bash
node scripts/portfolio-sync.mjs --help
```

| Command | Direction | Writes to GitHub? | What it does |
|---|---|---|---|
| `sync` (default) | GitHub → board | **No** | Mirror issues, milestones, labels and comments onto the board. |
| `onboard <owner/repo>` | GitHub → board | **No** | Register a new repo: create/link its Project durably, then backfill everything. |
| `restore-content <owner/repo>` | GitHub → existing descriptions | **No** | Recover incomplete legacy imports for one explicit repo, preserving differing board text and unrelated task fields. |
| `push-issue <owner/repo>` | board → GitHub | **No** — *preview only* | Print the exact issue each unsynced task **would** become. Refuses to write; see [Why `push-issue` does not write](#why-push-issue-does-not-write). |
| `status` | both | **No** | Health check. Exit `0` clean · `1` drift · `2` incomplete/error. |

> **This script never writes to GitHub.** Not "writes carefully" — *never*. `gh` invocations are
> checked against a whitelist of four read verbs (`issue list`, `pr list`, `label list`, `api`
> without any write flag) before they run, so a future edit that adds `gh issue create` fails
> immediately rather than at review time. Every write this tool performs goes to the KanbanFlow
> REST API. See [Write surface](#write-surface).

---

## The mapping

| GitHub | KanbanFlow | Identity key (how a re-run recognises it) |
|---|---|---|
| repo | Project | `<!-- github: owner/repo -->` in `Project.Description`; falls back to the repo's short name |
| milestone | Feature | the milestone title, matched by Feature name within the project |
| *(no milestone)* | Feature `Unsorted` | the literal name `Unsorted` |
| issue | TaskItem | the `#<n> ` prefix on `TaskItem.Name` |
| issue labels | TaskItem.Priority | highest severity label wins; default `Medium` |
| issue body | TaskItem.Description | provenance block + **complete exact source body**, including Unicode and Markdown whitespace |
| issue comment | TaskComment | `<!-- gh:<node-id> -->` stamped into `TaskComment.Text` |
| PR conversation comment | TaskComment (`Comment`) | same node-id marker |
| PR review `APPROVED` | TaskComment (`Verification`) | same node-id marker |
| PR review `CHANGES_REQUESTED` | TaskComment (`Blocked`) | same node-id marker |
| inline `file:line` review comment | TaskComment (`Comment`) | same node-id marker |

Nothing above needs a schema change — every identity key is a marker embedded in a field the
board already has. That is why the sync can be added to an existing board without a migration.

### Label → priority

Priority labels are a zoo across repos, so several vocabularies are recognised — and the match
is anchored, so a topical label that merely *contains* a severity word (`critical-path`,
`size/medium`) is not mistaken for a priority.

| Label form | Result |
|---|---|
| `P0` / `P0-critical` / `p1-high` / `p2 medium` / `p3` | Critical / High / Medium / Low |
| `priority:high`, `priority: medium`, `priority-critical` | that severity |
| `high-priority`, `critical-priority` | that severity |
| exactly `critical` / `high` / `medium` / `low` | that severity |
| anything else | ignored |

No priority label anywhere on the issue → **Medium**.

---

## Field ownership — what actually happens

This table describes the code, not the aspiration. Read the status row carefully.

| Field | Owner | Behaviour on every `sync` |
|---|---|---|
| Task **name** | GitHub | Always reconciled to `#<n> <issue title>`. A board-side rename is overwritten. |
| Task **priority** | GitHub | Always reconciled from the issue's labels. A board-side change is overwritten. |
| Task **description** | GitHub source plus retained board edits | Complete text at creation. Identifiable legacy incomplete imports are repaired conservatively; differing old board text is retained. Later edits to already complete imports are *not* re-mirrored (#495). |
| Task **feature** | GitHub | Follows the issue's milestone. A milestone change **moves** the task. |
| Task **status** | **split — see below** | |
| Task **comments** | GitHub | Complete, additive copies. An old incomplete copy permits one full replacement copy; the prior comment remains in history. Complete copies deduplicate by node id. |
| Subtasks, acceptance criteria, dependencies, claims | **the board** | Never touched by the sync. |

### Status is not symmetric, and manual pinning does not exist yet

| Situation | What the sync does |
|---|---|
| Issue **closed**, task not `Done` | **Forces** the task to `Done` — *even if a human set that status by hand.* |
| Issue open, linked open PR, task in `Backlog`/`Todo`/`InProgress` | Advances to `Review`. |
| Issue open, linked open PR, task already `Review` or `Done` | Left alone. |
| Issue open, task is `Done` | Left alone, and **reported as drift**. |
| Issue open, no linked PR | Left alone. |

So the frequently-stated invariant *"the sync never overrides a hand-set status"* is **only
half true today**. The advance-to-`Review` path honours it. The close path does not: closing an
issue on GitHub forces the task to `Done` regardless of what anyone set on the board.

This is not currently fixable in the script. `TaskItem` has no manual-pin field (no
`StatusOverride`, no `StatusManagedBy`), so there is nothing for the sync to check before
forcing. Adding one is tracked separately. **Until then, treat a hand-set status on a task
whose issue may close as temporary.**

---

## Drift, pending, unsynced — three different things

Conflating these is how a sync earns a reputation for fighting its owner.

| Term | Meaning | Does `sync` fix it? |
|---|---|---|
| **pending** | The board is simply behind GitHub — a new issue, a closed issue, a changed title or label. | **Yes**, on the next run. Not a problem. |
| **unsynced** | A board task with no `#<n> ` prefix: work drafted on the board that has no GitHub issue. | No — it is a `push-issue` candidate. Not a problem. |
| **drift** | A disagreement the sync deliberately **will not** resolve. | **No.** A human decides. |

There are exactly two kinds of drift, and `sync` and `status` report them with identical
wording (they share the message builders, so the two commands cannot disagree):

1. **Board says `Done`, issue is still open.** Someone marked the task complete while the issue
   lives on. The sync refuses to guess which one is right.
2. **Task references an issue that does not exist on GitHub.** Deleted, transferred, or the
   wrong number was typed. The task is **never** auto-deleted — the audit trail is sacred.

Link problems also count as drift, because the mirror silently forks while looking healthy:
two projects carrying the same repo marker, or several unlinked projects sharing a name.

---

## `status` exit codes

```bash
node scripts/portfolio-sync.mjs status && echo "safe to act"
```

| Exit | Meaning |
|---|---|
| `0` | Clean **and complete** — everything was read, and nothing disagrees. |
| `1` | Confirmed drift. With `--strict`, also pending work, unsynced tasks, or a non-durable link. |
| `2` | **Incomplete or error** — a repo could not be read, or a fetch hit its item bound. |

Exit `2` dominates. "I could not see everything" is a different answer from "I looked and it is
clean", and a drift count computed over a partial read is worse than no count at all — a
truncated read of a 43-issue repo reports *38 phantom orphans*, which is why saturating the
bound is a hard failure and not a warning. Raise `KANBANFLOW_GH_PAGE_LIMIT` (default 2000) if a
repo ever legitimately exceeds it.

> ### ⚠️ "last sync" is scoped to `--actor`, and the default is `portfolio-sync`
>
> `status` reports the last sync by finding the most recent activity event whose `actor` equals
> the `--actor` it was given. That means **`status --actor X` only sees syncs that ran under
> `--actor X`.**
>
> This bites when a scheduled sync is labelled for the audit trail. Run the nightly job as
> `sync --actor nightly-sync`, then check it with a bare `status`, and the answer is
> *"never — no activity by this actor"* even though a sync finished minutes ago. Nothing is
> wrong with the board; the two commands are simply looking at different labels.
>
> ```bash
> node scripts/portfolio-sync.mjs sync   --actor nightly-sync
> node scripts/portfolio-sync.mjs status --actor nightly-sync   # must match, or "last sync" reads never
> ```
>
> The drift and count fields are **not** actor-scoped — only the "last sync" field is. An exit
> code of `0` is still trustworthy under a mismatched actor; it is the recency that goes blank.

---

## Write surface

**On GitHub: nothing.** Enforced structurally, not by convention.

**On the KanbanFlow board**, every mutation goes through the REST API — never SQLite or EF
directly — so the `ActivityEvent` audit trail stays complete and the sync's writes are
indistinguishable from a human's in the history.

| Endpoint | When | Notes |
|---|---|---|
| `POST /api/projects` | a repo has no project | description carries the `<!-- github: … -->` marker |
| `PUT /api/projects/{id}` | `onboard` adopting a name-matched project | sends **only** `name` + `description`. This PUT is a true partial update (#203), so `Goals` and `Readme` are preserved *by omission* — sending every field back would be the riskier move |
| `POST /api/projects/{id}/features` | a milestone has no feature | |
| `POST /api/features/{id}/tasks` | an issue has no task | |
| `PUT /api/tasks/{id}` | title or priority is behind | **`name` is mandatory** — a priority-only body 422s (`ApiEndpoints.cs:1451`), so `name` always goes on the wire even when unchanged. Do **not** generalise from the projects PUT; the two behave differently |
| `PATCH /api/tasks/{id}/status` | an issue closed since the last sync | |
| `PATCH /api/tasks/{id}/feature` | an issue's milestone changed | feature is not settable through the task PUT; reassignment is its own verb |
| `POST /api/tasks/{id}/comments` | a GitHub comment is not mirrored yet | deduped by node id |

**Never, on either side:** delete anything. Not a task, not a feature, not a project, not a
comment, not an issue.

---

## Why `push-issue` does not write

`push-issue` computes the complete issue — title, milestone, labels, body — and prints it. Then
it stops. `--allow-write` / `--push` are recognised only so that asking for the write gets this
explanation instead of "unknown option".

Creating the issue and recording its number on the board are **two operations that cannot be
made one**. The board's only identity for a mirrored issue is the `#<n> ` title prefix, and
that prefix cannot exist until the GitHub create has already succeeded. Everything that can
happen inside that window produces a duplicate issue — and under additive-only rules, nothing
here is permitted to close it:

- **Crash between the two.** The retry sees an unprefixed task and creates a second issue.
- **Two concurrent runs.** Both read the same unprefixed task; both create. `PUT /api/tasks/{id}`
  has no ETag or version check, so there is nothing to lose the race on.
- **A `sync` landing mid-window.** It sees an issue with no task and creates a *second* board
  task for the issue that was just pushed.
- **Stamping the title races a human** editing that same title in the UI. Last writer wins,
  silently.
- **A title that already contains `#123`** is ambiguous — legacy link, literal text, or a
  cross-repo reference. The prefix parser cannot tell them apart.

Doing this safely needs a pending-operation id persisted through REST **before** the GitHub
call and matched back from the issue body on restart, plus a durable link field that is not
`Name`. That is schema work, tracked separately.

Until then the preview is genuinely useful: it is the exact issue body to paste, and it tells
you what would be lost. In particular it warns when **no label in the target repo maps to the
task's priority**, because a `High` task pushed to a repo with no priority labels comes back
from the very next `sync` as `Medium`.

Milestones are **attached, never created**. A task under a feature with no matching milestone
on the repo is previewed unfiled, and says so.

---

## Runbook: onboarding a new repo

```bash
# 0. KanbanFlow must be running.
export KANBANFLOW_API_URL=http://127.0.0.1:5148

# 1. Look before you leap — writes nothing.
node scripts/portfolio-sync.mjs onboard joelfuller2016/new-repo --dry-run

# 2. Do it. Creates the Project (with the durable link marker), the Features from milestones,
#    the Tasks from issues, and mirrors the comment history.
node scripts/portfolio-sync.mjs onboard joelfuller2016/new-repo

# 3. Prove it converged. Should print "created: 0" everywhere.
node scripts/portfolio-sync.mjs onboard joelfuller2016/new-repo

# 4. Health check. Exit 0 = clean and complete.
node scripts/portfolio-sync.mjs status --repos joelfuller2016/new-repo

# 5. Add it to the standing rotation.
export KANBANFLOW_PORTFOLIO_REPOS="…existing…,joelfuller2016/new-repo"
```

`onboard` is safe to re-run at any time. It is also the **upgrade path for a project that was
matched by name** before the link marker existed: it stamps the marker in place, leaving
`Goals` and `Readme` untouched.

From then on the repo rides along with the portfolio sweep:

```bash
node scripts/portfolio-sync.mjs           # sync every repo in the rotation
node scripts/portfolio-sync.mjs status    # gate on it
```

### Onboarding is not side-effect-free

Projects and features are matched by **display name**. Two consequences worth knowing before
you point `onboard` at a board that already has content:

- If a project already contains a feature literally named `Unsorted`, onboarding **adopts it**
  and files every un-milestoned issue there — potentially hundreds of tasks landing in a
  human's existing feature.
- Renaming a milestone on GitHub creates a **new** feature and **moves** every affected task
  into it, out from under whoever was looking at the old one.

Neither is fixed here; the grouping model is tracked separately (#289). Onboard into a fresh
project, or check the existing feature names first.

---

## Complete content and legacy recovery

For board owners and agents, the invariant is complete saved text. The old importer replaced
the remainder of long issue bodies with a generated cutoff footer. That was actual data loss,
not a folded display. Current imports keep every source character, including Markdown
indentation, trailing spaces, newlines and Unicode. The provenance header records
`Content: complete` and a SHA-256 of the exact UTF-8 source body. It never replaces the body.

```mermaid
sequenceDiagram
    participant G as GitHub (read only)
    participant R as Restore command
    participant A as Existing task REST API
    participant D as Existing SQLite and audit
    participant H as Human or MCP client
    R->>A: Read existing project and incomplete descriptions
    R->>G: Read complete issue sources
    R->>R: Resolve every candidate; retain differing prior text
    R->>A: Re-read each task before changing it
    R->>A: Save description with actor
    A->>D: Commit description and full before/after audit together
    R->>A: Read back and compare exact full text
    H->>A: Open same task, edit or read through MCP
    A->>D: Read complete text and history
```

1. **Rebuild and verify the app that will receive the repair.** Run the repository gate and
   the [TC-509 full-content checks](test-cases/509-unified-workflow/README.md). Expect actual
   large browser input, save and reopening to preserve text. Task descriptions/comments and
   the card editor preserve nonblank authored text; blank descriptions still clear the field.
   Do not run a restoration against an older service that trims source whitespace.
2. **Back up the existing database consistently.** Use SQLite's backup API and place the copy
   in this repository's ignored `_sandbox` folder. Record source, time and process identity.
   Expect a readable snapshot without changing any live rows. Keep it for comparison and
   recovery; do not replace a live database while the application is running.
3. **Preview one explicit repository.** Run
   `node scripts/portfolio-sync.mjs restore-content owner/repo --dry-run --actor Codex`.
   Expect task IDs, old/new character counts and any retained prior text; no writes occur.
   Ambiguous project links, foreign provenance, missing source or a saturated GitHub page
   bound fail before writes. A provenance-free legacy stub additionally requires a durable
   matching project marker and issue-number title. Resolve the stated problem and preview again.
4. **Apply that scoped recovery.** Remove `--dry-run` from the same command. Each existing
   description is re-read, repaired through the audited task API and checked by exact
   read-back. The command does not run ordinary status/title/priority/milestone sync and
   does not create or delete tasks. If a card differs from the prepared version, recovery
   refuses that write. Already verified repairs remain; re-read and retry after resolving
   the reported change. The ordinary task API has no atomic compare-and-swap token, so the
   fresh-read guard cannot exclude an edit in the brief interval before the PUT.
5. **Verify saved content and unrelated data.** Compare all source hashes, prior text,
   task identities/fields and audit before/after values against the backup. If retained board
   text differs from GitHub, it appears in a labeled section after the complete current source;
   a human can reconcile it later. Keep both until that decision is recorded.
6. **Repeat the command.** Expect zero repairs and no new audit entries. Complete descriptions
   are not overwritten, even when their source literally quotes the old footer. General
   refresh of later changes to complete GitHub imports remains a separate concern (#495).

Comment recovery stays additive: a genuinely incomplete legacy comment permits one complete
copy with the same GitHub node identity; the old comment remains. The next sync detects the
complete copy and does not duplicate it. An already complete legacy comment that literally
quotes the old footer is compared with its source and is not duplicated.

Transport limits are not content budgets. The editor uses a finite 4 MiB inbound SignalR
message allowance and an 8 MiB inbound transport buffer. Import/storage code never clips a value to fit it. A failed or oversized
operation must not be reported as a successful partial save. Very large future sources may
need a streaming editor transport; do not bring back excerpts as a workaround. Browser
textareas normalize line endings to LF; API/MCP imports preserve source CRLF exactly.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `KANBANFLOW_API_URL` | `http://127.0.0.1:5123` | the board's REST API |
| `KANBANFLOW_PORTFOLIO_REPOS` | the six focus repos | comma-separated rotation for `sync` / `status` |
| `KANBANFLOW_MCP_TIMEOUT_MS` | `15000` | REST timeout |
| `KANBANFLOW_GH_PAGE_LIMIT` | `2000` | per-fetch item bound; saturating it is exit `2` |

`KANBANFLOW_BODY_EXCERPT_LIMIT` is obsolete and ignored with a warning: it cannot shorten content.
`--max-comment-chars` is rejected with exit `2`; remove it from old invocations.

Requires `gh` authenticated with read access to the repos in the rotation.

### npm scripts

```bash
npm run gh:sync        # mirror the whole rotation
npm run gh:sync:dry    # …preview it
npm run gh:status      # health check (exit 0/1/2)
npm run gh:onboard -- joelfuller2016/new-repo
npm run gh:push:preview -- joelfuller2016/new-repo --all   # preview only — never writes to GitHub
```

---

## No CI

This repo runs **no GitHub Actions** ([`docs/ci-without-actions.md`](./ci-without-actions.md)).
Run the sync from your machine or an external scheduler. `status`'s exit codes exist precisely
so an external scheduler or an agent can gate on it without a workflow file.
