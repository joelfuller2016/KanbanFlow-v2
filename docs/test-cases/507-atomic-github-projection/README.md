# TC-507: atomic inbound GitHub projection

This guide helps maintainers and human reviewers verify [issue #507](https://github.com/joelfuller2016/KanbanFlow-v2/issues/507) in the existing KanbanFlow board. It depends on the durable identity model from [PR #506](https://github.com/joelfuller2016/KanbanFlow-v2/pull/506). The [GitHub sync operating contract](../../GITHUB-SYNC.md) owns field ownership, legacy promotion and external-write recovery. The review cycle is tracked in [issue #515](https://github.com/joelfuller2016/KanbanFlow-v2/issues/515).

## Purpose and architecture

An inbound GitHub issue must become one existing board card, its durable identity row and its audit trail. A lost response or a concurrent sync must not create an orphan card. Replaying the same identity must preserve human edits already made to that card.

Previously the sync script committed three requests: create a task with a temporary issue-number prefix, register its link, then rename it to the plain title. The link's uniqueness constraint protected only the second request. A competing process could leave behind a task whose link insert was refused.

```mermaid
sequenceDiagram
    participant A as Sync process A
    participant B as Sync process B
    participant API as Old board API
    participant DB as SQLite
    A->>API: Create task
    API->>DB: Commit task A
    B->>API: Create task
    API->>DB: Commit task B
    A->>API: Register immutable issue link
    API->>DB: Commit link to task A
    B->>API: Register same immutable issue link
    DB-->>B: Conflict; task B already remains
```

The new `POST /api/features/{featureId}/external-work-item-projections/github` accepts the task fields and a GitHub identity from a verified provider read. The service owns one immediate SQLite transaction across all writes. It does not perform a GitHub write or verify a new identity through a separate GitHub network request; the importing caller supplies that provider evidence. Existing database identity and canonical URL checks remain authoritative.

```mermaid
sequenceDiagram
    participant Caller as Sync / REST / MCP
    participant Service as ExternalWorkItemService
    participant Core as TaskService shared creation
    participant DB as SQLite
    Caller->>Service: Task fields + verified identity
    Service->>Service: Validate fields and canonical URL
    Service->>DB: BEGIN IMMEDIATE
    Service->>DB: Resolve feature/project; recheck global issue node
    alt Exact active identity already in same project
        Service->>DB: Commit without writes
        Service-->>Caller: Existing task/link; created=false
    else Ownership, number, repository or quarantine conflict
        Service->>DB: Roll back; zero saves
        Service-->>Caller: Structured 409 or 422
    else Identity is available
        Service->>Core: CreateInContextAsync in caller transaction
        Core->>DB: Task + Created comment + TaskCreated event
        Service->>DB: Active link + ExternalWorkItemLinked event
        Service->>DB: Commit once
        Service-->>Caller: Task/link; created=true
    end
```

Every context uses the same existing SQLite database. `BEGIN IMMEDIATE` happens before the ownership reads, so independent app/sync processes serialize through SQLite. The existing unique indexes remain a final constraint. The shared task creation core preserves defaults, finite append ordering, completion timestamps, actor attribution, the Created comment and the original TaskCreated snapshot. Descriptions are retained exactly, including meaningful leading/trailing Markdown whitespace. A failure while saving the link rolls back earlier task and audit saves.

## Human-visible behavior and transport contract

The card, outline, detail panel and editor keep the existing UI. New imports start with the plain GitHub title and immediately have their durable issue badge. Humans can rename or move the card through existing controls. An identical projection retry returns those current fields without renaming, moving, editing, or adding another creation audit. Normal later sync reconciliation remains governed by the separate field-ownership policy; this change does not introduce a permanent manual field pin.

Both native MCP and the stdio wrapper expose `project_github_issue` with the same task/identity fields and the same `task`, `link`, `created` result. Native calls the shared service; stdio calls the compound REST endpoint. No legacy create/register sequence remains for a new verified inbound issue in `portfolio-sync.mjs`. Legacy task promotion still uses explicit verified registration.

| Situation | Result | Recovery |
|---|---|---|
| First valid projection | HTTP 201, Location `/api/tasks/{id}`, `created:true` | Open the existing board card. |
| Same active identity in the same project | HTTP 200, same IDs, `created:false`; zero writes | Continue with the current card fields. A lost response is safe to replay. |
| Immutable node belongs to another project | 409 `external-node-cross-project`; zero saves | Review ownership and explicitly reconcile; do not create a replacement card. |
| Repository issue number belongs to another node | 409 `external-number-already-linked`; zero saves | Investigate conflicting provider evidence. |
| Same node reports another number | 409 `external-node-number-mismatch`; zero saves | Reconcile the verified link through the existing transfer/refresh path first. |
| Same node reports another repository node, owner/name or URL | 422; zero saves | Verify a rename/transfer and refresh the existing link explicitly before retrying projection. |
| A matching repository/number has unresolved quarantine | 409 `quarantined-link-exists`; zero saves | Resolve the human review item before projection. |
| Missing feature | 404; zero saves | Refresh the project/feature context. |
| Invalid identity/task fields | 422; zero saves | Correct the named field. Literal JSON `null` is 400. |
| Failure after task creation, before link commit | Entire transaction rolls back | Retry the same identity after the persistence failure is resolved. |

## Implementation checklist

- [x] Add the projection request/result contracts and `IExternalWorkItemService` operation.
- [x] Reuse the existing task creation/audit/default implementation inside a caller-owned transaction.
- [x] Recheck global immutable identity and repository-number ownership inside an immediate transaction.
- [x] Preserve exact content, existing human edits and quarantine decisions.
- [x] Commit task, Created comment, TaskCreated event, link and ExternalWorkItemLinked event together.
- [x] Add the REST endpoint, dedicated response DTOs, documented status codes and nullable-body guard.
- [x] Expose the compound operation through native and stdio MCP.
- [x] Replace new inbound sync's create/register/rename sequence with one atomic call; preserve full conflict details and continue other issues on 409/422.
- [x] Add deterministic independent-connection concurrency and injected rollback/retry tests.
- [x] Add endpoint no-save conflict tests, native service tests, actual stdio/HTTP adapter tests and a same-board browser check.
- [x] Add repeatable [HTTP examples](../../../atomic-projection.http), [transport smoke](../../../scripts/atomic-projection-smoke.mjs) and [browser fixture](../../../scripts/atomic-projection.browser.js).
- [x] Record the final combined-branch API result: every original HTTP/data assertion passed against the owned foreground published app; source/check hashes and provider budget are recorded.
- [ ] Record actual human usability feedback before claiming human acceptance.

## Repeatable validation

1. Inspect the current commit and clean/dirty state. Run `pwsh -NoProfile -File scripts/verify.ps1`; expect a fresh warnings-as-errors build and all .NET tests passing. Run `npm run test:scripts`; expect Node's script suite, including the real stdio adapter checks, to pass. Fix the first failing gate before using prior artifacts.
2. Review [ExternalWorkItemProjectionTests](../../../tests/KanbanFlow.Tests/ExternalWorkItemProjectionTests.cs). The concurrency fixture uses independent file-backed SQLite connections and coordinates the second transaction attempt while the first is paused before task save. A watchdog bounds failures. The link-save interceptor proves a task ID was assigned before failure, then fresh reads prove no task/link/comment/audit survived and retry produces exactly one projection.
3. Publish current source into an ignored folder. Start a disposable app with an explicit database path and a free loopback port. Run `scripts/atomic-projection-smoke.mjs` with `KANBANFLOW_API_URL` pointing to it. Expect exact full-content reads and audit JSON through REST, native MCP and stdio; 201/200/409/422/400 status checks; cleanup of only the labeled fixture projects. No provider write is made.
4. Open the disposable app in a named Playwright CLI session. Run `scripts/atomic-projection.browser.js`. Expect one card after two concurrent HTTP calls, the same badge in card/detail, a saved human title change, and that title plus full description preserved after retry and reload. The script deletes only its fixture project in `finally`.
5. Run the existing `scripts/smoke-mcp.mjs`, `scripts/framing-ab.mjs all`, `scripts/ui-e2e.mjs` and broad API smoke under their documented isolated-app prerequisites. Keep the optional provider budget compatible with the API harness's five-second client timeout through an approved local invocation. Record failures and approval rejections rather than replacing them with a generic pass.
6. Stop only owned app/browser processes after checking their identities. Keep full logs, databases and snapshots in the owning checkout's ignored folder. Record the source commit, actual results, environment and remaining limits in the run record.

## Boundaries still outside this change

This operation does not merge or delete historical duplicates, alter outbound issue-write recovery, or normalize the two GitHub fetch record shapes tracked in [#514](https://github.com/joelfuller2016/KanbanFlow-v2/issues/514). The current native `get_task` response still lacks durable links/journal fields; adding the compound tool does not close that separate read-shape parity gap. The parent integration owns that work and #511's complete-content recovery.

## Runs

- [2026-09-09 integrated published-app source `f6b83bb52240`](runs/2026-09-09-f6b83bb52240.md)

- [2026-09-08 integrated source `e912072205bc`](runs/2026-09-08-e912072205bc.md)
