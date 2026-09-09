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
| `push-issue <owner/repo>` | board → GitHub | Preview: **No**. Explicit live mode: **one issue** | Preview by default. `--allow-write --task <id>` enables one additive create through the durable journal; `--resume <uuid>` reconciles an interrupted operation. |
| `backfill-links <owner/repo>` | GitHub → board identity | **No** | **Repair/audit, not a required migration step** — an ordinary `sync` promotes a legacy prefix by itself. Verifies authoritative URLs already stored in sync blocks, persists durable links, and quarantines ambiguous legacy candidates. |
| `status` | both | **No** | Health check. Exit `0` clean · `1` drift · `2` incomplete/error. |

> **The GitHub write surface is one additive primitive.** Generic `gh()` remains checked against
> the closed read-only whitelist (`issue list`, `pr list`, `label list`, `api` without write
> flags). A separate wrapper can run only `gh issue create --repo … --title … --body-file -`,
> optionally attaching the frozen milestone/label, and only after `--allow-write --task <id>`
> plus a durable board-side prepare/dispatch. There is no edit, close, delete, comment, or
> post-create relabel path. See [Write surface](#write-surface).

---

## The mapping

| GitHub | KanbanFlow | Identity key (how a re-run recognises it) |
|---|---|---|
| repo | Project | immutable `<!-- github-node: R_… -->` in `Project.Description`; legacy `<!-- github: owner/repo -->`, then short name, are upgrade-only fallbacks |
| milestone | Feature | the milestone title, matched by Feature name within the project |
| *(no milestone)* | Feature `Unsorted` | the literal name `Unsorted` |
| issue | TaskItem | Active `ExternalWorkItemLink`: immutable repository node id + issue node id; legacy `#<n> ` prefix only when no structured link exists |
| issue labels | TaskItem.Priority | highest severity label wins; default `Medium` |
| issue body | TaskItem.Description | provenance block + first 1500 chars of the body |
| issue comment | TaskComment | `<!-- gh:<node-id> -->` stamped into `TaskComment.Text` |
| PR conversation comment | TaskComment (`Comment`) | same node-id marker |
| PR review `APPROVED` | TaskComment (`Verification`) | same node-id marker |
| PR review `CHANGES_REQUESTED` | TaskComment (`Blocked`) | same node-id marker |
| inline `file:line` review comment | TaskComment (`Comment`) | same node-id marker |

Newly mirrored and board-originated issues receive structured links. Existing boards keep
working because prefix matching remains a fallback — and an ordinary `sync` upgrades that
fallback by itself.

### Legacy prefix → durable link

An ordinary `sync` promotes a legacy `#<n> ` prefix match to a durable link, with no operator
action. `backfill-links` is **no longer the mandatory migration step**; it is the repair and
audit tool for the cases promotion deliberately refuses.

The precondition is the point. A prefix match knows only a repo-local *number* and the repo
being synced — exactly the mutable identity durable links exist to stop trusting. So promotion
never contradicts the task's `kanbanflow-sync` block, which is authoritative
(`legacyPromotionDisposition`, `scripts/portfolio-sync.mjs`).

**Promotes** — registers the link *and* strips the prefix in the same run — when all hold:

| Condition | Why |
|---|---|
| the task is a `legacy` match: no active *and* no quarantined GitHub link for this repo, not linked to another repo, `#<n> ` parses, and no other task in the project claims `<n>` | structured state always beats title parsing |
| the issue record's `node_id ?? id` is a non-numeric string | a REST-shaped record puts a *number* there; persisting it as `externalNodeId` would corrupt the identity field (`promotableIssueNodeId`) |
| the description has **no** `**Issue:**` URL in a sync block **or** exactly one canonical URL naming this repo (case-insensitively) *and* this issue number | the ordinary pre-#336 task has no block and must be allowed; a block that agrees adds nothing to refuse |

`--dry-run` plans both the link and the rename and writes neither.

**Refuses, and reports drift** — the entire task is left untouched, and the sync continues with other issues:

| Case | Drift names |
|---|---|
| the sync block names a **different repository** | `backfill-links owner/repo` |
| the sync block names a **different issue number** | `backfill-links owner/repo` |
| **malformed provenance**: more than one `kanbanflow-sync` block, more than one `**Issue:**` line inside it, or an `**Issue:**` value that is not a canonical GitHub issue URL | `backfill-links owner/repo` — backfill owns that taxonomy and quarantines it properly |
| the issue record carries **no immutable node id** | refuses rather than persisting a number as identity |
| the board **rejects the link registration** (a competing claim, arbitrated server-side) | `backfill-links owner/repo`; the prefix stays the task's identity |

A refusal also prevents comment imports, feature movement, description/title/priority changes,
and closing the task. A matching integer does not authorize changing a task whose source identity
was rejected. Failure details and quarantine explanations retain their complete text and Markdown
whitespace in the journal/link and applicable audit event; they are never silently shortened.

The issue badge uses the actual description as a display fallback only when there is no durable
GitHub row. A quarantined or invalid row suppresses that fallback across card, detail and editor.
The source description remains accessible. See [TC-336](test-cases/336-durable-github-links/README.md)
for the repeatable review and browser checks.

**Never reached by promotion at all** — these fall to `backfill-links`, or to the human
verified-link PUT:

- the task's issue is **closed** and `--include-closed` is off, so it is not in the fetched set —
  `backfill-links`;
- the prefix was **already stripped** before a link existed — only the sync block can identify
  it, so `backfill-links`;
- the task already carries a **quarantined** link candidate, for which the sync refuses all
  automation — and so does a `backfill-links` rerun. Only the verified link PUT clears it;
- two tasks claim the same `#<n> `, which is not drift but a hard stop: `buildTaskIdentityIndex`
  throws `refusing an ambiguous sync`, the rotation loop reports `ERROR on owner/repo` and exits
  `1`, and the remaining repos still run. `backfill-links` is unaffected — it never builds that
  index and never keys on the prefix, so it classifies each task from its own sync block
  (`planBackfill`). It quarantines a *duplicate* only when two blocks name the same issue.

**Order matters.** A live `backfill-links` quarantines a title-only `#<n> ` task as having "no
authoritative kanbanflow-sync issue URL" — the very case an ordinary sync promotes — and a
quarantined task is then refused by every later sync. Run `sync` first; run `backfill-links` for
what it leaves behind.

An explicit verified link PUT is the human-resolution path that promotes a quarantine to Active;
backfill reruns deliberately leave the quarantine untouched. If malformed provenance cannot be
correlated to one issue number, unmatched issue projection fails closed across that project until
the quarantine is resolved.

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
| Task **name** | GitHub | Active linked tasks use the plain issue title; a legacy fallback task keeps `#<n> <issue title>` only until the sync that promotes it (see [Legacy prefix → durable link](#legacy-prefix--durable-link)). Identity never depends on a linked task's editable name. |
| Task **priority** | GitHub | Always reconciled from the issue's labels. A board-side change is overwritten. |
| Task **description** | GitHub | Written **once, at creation**. Later issue-body edits are *not* re-mirrored. |
| Task **feature** | GitHub | Follows the issue's milestone. A milestone change **moves** the task. |
| Task **status** | **split — see below** | |
| Task **comments** | GitHub | Additive only. Never edited or deleted. |
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
| **unsynced** | A board task with neither an Active GitHub link nor a legacy `#<n> ` fallback. | No — it is a `push-issue` candidate. Not a problem. |
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

**On GitHub:** one additive issue create, and only after the explicit live gate and durable
journal transition. The generic GitHub helper remains read-only. There is no update/delete path.

**On the KanbanFlow board**, every mutation goes through the REST API — never SQLite or EF
directly — so the `ActivityEvent` audit trail stays complete and the sync's writes are
indistinguishable from a human's in the history.

| Endpoint | When | Notes |
|---|---|---|
| `POST /api/projects` | a repo has no project | description carries immutable `<!-- github-node: … -->` plus the refreshable `<!-- github: … -->` display marker |
| `PUT /api/projects/{id}` | `onboard` adopting a name-matched project | sends **only** `name` + `description`. This PUT is a true partial update (#203), so `Goals` and `Readme` are preserved *by omission* — sending every field back would be the riskier move |
| `POST /api/projects/{id}/features` | a milestone has no feature | |
| `POST /api/features/{id}/tasks` | an issue has no task | |
| `PUT /api/tasks/{id}` | title or priority is behind | **`name` is mandatory** — a priority-only body 422s (`ApiEndpoints.cs:1451`), so `name` always goes on the wire even when unchanged. Do **not** generalise from the projects PUT; the two behave differently |
| `PATCH /api/tasks/{id}/status` | an issue closed since the last sync | |
| `PATCH /api/tasks/{id}/feature` | an issue's milestone changed | feature is not settable through the task PUT; reassignment is its own verb |
| `POST /api/tasks/{id}/comments` | a GitHub comment is not mirrored yet | deduped by node id |
| `POST /api/tasks/{id}/external-write-operations` | before a live GitHub create | freezes the exact request and server-generated `kanbanflow-push` marker |
| `POST /api/external-write-operations/{uuid}/dispatch` | immediately before GitHub contact | closes the unsafe pre-call window |
| `POST /api/external-write-operations/{uuid}/finalize` | after an exact issue result or marker recovery | sends current repository node/owner/name with the issue result; the service pins the immutable node, validates the URL against the current path, then atomically completes the operation and inserts the Active link |
| `POST /api/external-write-operations/{uuid}/authorize-ambiguous-retry` | operator accepts residual risk after an exhaustive raw zero-marker scan | records actor/reason and `ManualRetryAuthorized`; exits without a GitHub write and requires a separate resume |
| `PUT /api/tasks/{id}/external-work-item-links/github` | verified sync/backfill identity | idempotent registration; database uniqueness is authoritative |
| `GET /api/external-work-item-links/github/by-node?externalNodeId=…` | before fallback matching/task creation | global immutable-node owner lookup; 404 means unlinked |
| `POST /api/tasks/{id}/external-work-item-quarantines/github` | ambiguous legacy candidate | persists the reason instead of guessing |

**Never, on either side:** delete anything. Not a task, not a feature, not a project, not a
comment, not an issue.

---

## Safe `push-issue`: preview, live create, and recovery

Preview remains the default and performs **zero writes on either side**:

```bash
node scripts/portfolio-sync.mjs push-issue owner/repo --task 123
node scripts/portfolio-sync.mjs push-issue owner/repo --all
```

Live mode is deliberately single-task and explicit:

```bash
node scripts/portfolio-sync.mjs push-issue owner/repo --allow-write --task 123
```

The sequence is `prepare → dispatch → gh issue create → verify → finalize`. `prepare` freezes
the title/body/milestone/label and appends exactly
`<!-- kanbanflow-push:<operation-uuid> -->`; `dispatch` is persisted before GitHub contact;
`finalize` sends the current repository node id, owner, and name with the issue result. The service
requires the immutable node to match the frozen operation, validates the URL against the supplied
current path (so a rename survives but a different-repository URL is rejected), then atomically
stores the issue node id, number, URL, and immutable repository identity.

GitHub does **not** offer an idempotency key for issue creation. The body marker is therefore a
recovery anchor, not a provider-side exactly-once guarantee. If a process dies after dispatch,
resume it explicitly:

```bash
node scripts/portfolio-sync.mjs push-issue owner/repo \
  --allow-write --task 123 --resume 00000000-0000-4000-8000-000000000000
```

For `Dispatched` or `NeedsReview`, resume never creates automatically. Indexed GitHub search is
used only as a hint; the script then performs a complete paginated REST scan. One clean issue
record containing one target marker and no other operation marker finalizes. The stronger raw
counter scans every REST record including PRs and recognises the target token even when its HTML
comment/whitespace is malformed. A raw target marker copied twice, mixed with another operation
marker, or present across records is ambiguous even when the clean-match filter returns zero. Zero raw occurrences, ambiguity, or an incomplete read
stay `NeedsReview` and fail closed. A normal `sync` applies the same rule before it may create any
board task, so a sync landing between GitHub create and finalize maps the issue back to the
intended task.

GitHub supplies no atomic negative-read/idempotency primitive. When an operator has investigated
a persistent zero-result `NeedsReview`, the only liveness escape hatch is deliberately two-step:

```bash
# Step 1: fresh exhaustive scan, then record the operator decision and EXIT. No GitHub create.
node scripts/portfolio-sync.mjs push-issue owner/repo \
  --allow-write --task 123 --resume 00000000-0000-4000-8000-000000000000 \
  --authorize-ambiguous-retry 00000000-0000-4000-8000-000000000000 \
  --actor operator-name --resolution-reason "Reviewed remote audit and accepted retry risk"

# Step 2: a separate invocation claims the attempt, then scans before evidence-bound dispatch/create.
node scripts/portfolio-sync.mjs push-issue owner/repo \
  --allow-write --task 123 --resume 00000000-0000-4000-8000-000000000000
```

The repeated UUID, explicit actor, reason, attempt count, scan time, and zero raw occurrence count
are checked and audited. The second invocation calls Resume first; Resume preserves
`ManualRetryAuthorized`, claims the lease, increments the attempt, and records `LastAttemptAt`.
Only then does the script scan. One late issue finalizes without creating and any
ambiguous/incomplete result blocks. After a post-Resume zero scan, dispatch accepts the operation
only with the same UUID, post-Resume attempt count, a scan timestamp at or after `LastAttemptAt`,
clean count zero, and raw count zero. It then sends the same frozen
body/marker. There is still an unavoidable residual race between that negative read and GitHub
create; manual authorization records acceptance of that risk rather than claiming exactly-once
proof. A crash after Resume and scan but before dispatch cannot erase the gate—the next invocation
must claim a new attempt and scan again. `Prepared` with prior `DispatchedAt` is only a defensive compatibility state: a late
marker may proof-finalize it, but it is not the normal manual-retry path and it cannot dispatch a
replacement. A first-ever Prepared operation cannot use that exception.

A second concurrent prepare loses at the database boundary before GitHub is contacted. Every
post-dispatch process/CLI/transport failure remains ambiguous; the script never classifies one as
definitively pre-create and never compensates by closing or deleting an issue.

Issue transfers use the issue node ID as canonical identity. Before fallback matching or task
creation, sync checks the global node lookup. If the linked task already belongs to the current
project, the verified PUT refreshes repository node/owner/name, number, and URL and audits
`ExternalWorkItemTransferred`. If it belongs to another project, sync reports the source project
and task, then makes zero mutations and creates nothing; moving a task across projects is an
explicit human decision. Nonterminal write operations remain pinned to their frozen repository.

Milestones and priority labels are attached during the create when their exact names already
exist. They are never created by this command.

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

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `KANBANFLOW_API_URL` | `http://127.0.0.1:5123` | the board's REST API |
| `KANBANFLOW_PORTFOLIO_REPOS` | the six focus repos | comma-separated rotation for `sync` / `status` |
| `KANBANFLOW_MCP_TIMEOUT_MS` | `15000` | REST timeout |
| `KANBANFLOW_GH_PAGE_LIMIT` | `2000` | per-fetch item bound; saturating it is exit `2` |
| `KANBANFLOW_EXTERNAL_WRITE_LEASE_SECONDS` | `300` | durable write lease; must be a positive integer. The real-GitHub harness sets `15` on its scratch instance so the post-Resume paginated scan has headroom, and polls the recorded expiry after intentional crashes |

Requires `gh` authenticated with read access to the repos in the rotation.

### npm scripts

```bash
npm run gh:sync        # mirror the whole rotation
npm run gh:sync:dry    # …preview it
npm run gh:status      # health check (exit 0/1/2)
npm run gh:onboard -- joelfuller2016/new-repo
npm run gh:push:preview -- joelfuller2016/new-repo --all   # preview only — never writes to GitHub
npm run gh:backfill -- joelfuller2016/new-repo --dry-run   # repair/audit; sync already auto-promotes
```

---

## Opt-in real-GitHub failure harness

[`scripts/verify-336-github.ps1`](../scripts/verify-336-github.ps1) is pinned to the private
`joelfuller2016/test-mcp-github-repo-20250527` fixture repository and its immutable node id. It
refuses to run without `-AllowGitHubWrites`, rechecks privacy/issues/admin access, starts a fresh
scratch SQLite database on a random port, and exercises:

- a command-level preview that leaves operation, activity, link, task, and GitHub issue state unchanged;
- post-dispatch/pre-create process death, fail-closed normal recovery, audited manual authorization,
  a malformed raw-token denial fixture, an evidence-bearing post-Resume scan/create, and a
  crash-after-Resume-and-scan delayed-marker subcase that proves the next invocation claims a new
  attempt and rescans/finalizes without duplication (PR-token denial is deterministic Node coverage;
  the harness intentionally does not create retained branches/commits merely to manufacture a PR);

1. process death after GitHub create and before finalize;
2. two live pushes released together from a pre-prepare barrier for one task;
3. `sync` inside the create/finalize window, including an unchanged project task-id set;
4. task-only, issue-only, and both-side renames;
5. duplicate-link rejection plus focused real-SQLite tests;
6. title-only legacy quarantine, idempotent rerun, and suppression of later sync/live-push writes.

```powershell
pwsh ./scripts/verify-336-github.ps1 -AllowGitHubWrites
```

This command creates and renames real fixture issues and adds one audit-summary comment. It
**never closes or deletes them**. The scratch database, process logs, barrier signals, and
`result.json` are preserved in the printed temporary run directory, even on failure.

---

## No CI

This repo runs **no GitHub Actions** ([`docs/ci-without-actions.md`](./ci-without-actions.md)).
Run the sync from your machine or an external scheduler. `status`'s exit codes exist precisely
so an external scheduler or an agent can gate on it without a workflow file.
