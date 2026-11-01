# Persistent idea and project workspaces

Implementation contract for [issue #579](https://github.com/joelfuller2016/KanbanFlow-v2/issues/579). Source baseline: `42f17b459f0bee0dce78da48d6a2234f75f6fda6`, October 31, 2026. The issue owns task completion. This guide records the concrete contracts and will be updated with observed usage as implementation proceeds.

For the human workflow and actual application screenshots, see [An idea becomes a working project](idea-workspaces-storyboard.md). The examples below describe the same persisted records used by the UI, API and both MCP transports.

## Identity, compatibility and saved content

A workspace has a stable key `idea:{positive-id}` or `project:{positive-id}`. These keys let the catalog include every existing idea and standalone project without creating dummy records or mutating on reads. An idea keeps its key after project association. A project has exactly one primary workspace, `project:{id}`; contributing idea workspaces retain their own notes and point at that project's accepted plan. The catalog lists each key once, with its kind and relationships explicit.

Legacy Idea and Project documents remain complete and separately accessible. A first mutation creates an additive `IdeaWorkspaceState` snapshot containing the original source content and title. Association must also atomically capture the target project's complete Description/Goals/Readme in `project:{id}` state, without replacing a prior snapshot, even when that project was never otherwise edited through a workspace. Missing/deleted source records are represented explicitly when a saved workspace exists; deleting a source cannot delete the saved workspace. Current canonical task status is read from Tasks, never copied into a component's editable status.

New persistence uses explicitly named `IdeaWorkspaceStates` and `IdeaWorkspaceReceipts` tables and the `KanbanFlow.Models.IdeaWorkspaces` / `KanbanFlow.Services.IdeaWorkspaces` namespaces. This avoids collision with unmerged PR #511's `Services.Workflows.WorkspaceService`, request/brief/plan tables and `/api/workflows` routes. Do not merge or cherry-pick that unrelated 145-file PR. Reuse its proven invariants and the current master's existing `TaskService.CreateInContextAsync` for atomic task publication. Existing idea tools and status enum ordinals stay compatible.

Each state stores a versioned typed document: components, independent placements, connectors, threaded comments, draft/accepted plan revisions and per-client checkpoints. Content is kept in full. Supported types are frame, note, markdown, research, image, question, decision, checklist, table, plan and task-reference. Components are content records; placements have their own ID, component ID, parent frame-placement ID, x/y/width/height, z-order and outline order. Child coordinates are parent-relative; moving a frame preserves child offsets, and UI rendering sums ancestor positions for world coordinates. Removing a placement leaves content intact. An explicit component removal retains its complete before snapshot in history. Parent cycles, missing targets and cross-workspace moves fail visibly.

## Mutation and read consistency

All UI, REST and MCP commands use one `IIdeaWorkspaceService`. New workspace writes require a nonempty operation ID, expected workspace revision and actor label. Shared project-plan acceptance/publication additionally requires the expected canonical project-plan revision: two different contributing idea revisions cannot authorize overwriting the same stale project plan. SQLite reserves the writer before reading current state. Every affected workspace/project-plan state, operation receipt and complete ActivityEvent before/after snapshot commits in one transaction. Stale revisions produce a conflict and no partial write. The identical operation/payload replay returns the prior result even if its expected revision is now old; the same operation ID with different content conflicts. The first successful mutation advances revision 0 to 1.

Caller/session checkpoints coexist. Saving or opening workspace B never overwrites A or another caller's checkpoint. A resume response includes current revision, checkpoints and a paged changes cursor. A checkpoint is a navigational aid, not the accepted plan. Acknowledged changes survive process restart without a final checkpoint.

Catalog pages use a stable snapshot cursor which identifies the complete ordered membership/filter generation. New inserts cannot cause gaps/duplicates in that traversal; invalid/expired cursors fail explicitly. Component and activity pages have explicit continuation and full detail reads; APIs never shorten authored text. Summary endpoints explicitly return metadata. Context returns original/current source documents, project identity/deep links, the complete current plan, checkpoints and component paging descriptors. Task-reference details use current domain task data.

## Public surface

REST lives under `/api/workspaces`; capability discovery is `/api/workspace-capabilities`. Read operations: catalog, context, components/detail and changes. Writes: create/open a source-backed workspace, component/content operations, atomic placement batches, comments, checkpoints, plan proposals/acceptance, project association and publication preview/apply. Every result exposes revision and successful operation identity. Both native MCP and the stdio wrapper expose these same operations and equivalent domain errors.

The whiteboard is at `/ideas/workspace/{kind}/{id}`. Ideas gains an explicit gallery/board choice, and linked projects provide `/project/{id}` Kanban navigation. The whiteboard renders editable frames and typed cards, a full-content inspector, discussion, plan outline, resume switcher, and layout controls that also work by keyboard. Optional drag/resize updates persisted placements through the same command service. The existing task board remains available throughout owner usability review.

## Contract walkthroughs

1. Existing Idea 12: catalog returns `idea:12`, context reads its complete Body/Plan without a save. Adding a card creates state/revision/audit atomically. Restart reads the same card.
2. Existing Project 8 with no Idea: catalog returns `project:8`, context reads Description/Goals/Readme and Kanban URL; no Idea is created.
3. Idea 12 and Idea 13 linked to Project 8: keys remain distinct, notes/checkpoints stay separate, accepted delivery plan is read from `project:8`, and published plan-item/task mappings prevent duplicate tasks.
4. Link an otherwise untouched Project 8, then remove it: saved idea/project documents and history remain because association captured the target first. The context marks the missing project; publication fails rather than recreating it. Explicit relinking preserves all authored versions and audit.
5. Ideas 12/13 both read Project 8 plan revision 5: A accepts revision 6; B's acceptance using expected project-plan revision 5 fails even if B's own idea revision is still current. A's plan and both workspaces' prior content survive.

## Verification boundaries

Implementation checks use real SQLite, current builds, isolated databases and actual REST/native/stdio clients. Browser checks prove UI/API agreement and keyboard/narrow-screen behavior. Automated checks do not supply owner usability feedback. No publishing, merging, access-policy change or live-board migration follows merely from this contract.

**Verification correction, November 1, 2026 UTC:** the repair checkpoint on source [`1d1cc40d204daad29705998e7c74c2b1e9eea3f8`](https://github.com/joelfuller2016/KanbanFlow-v2/commit/1d1cc40d204daad29705998e7c74c2b1e9eea3f8) passed 1,686 .NET tests and 374 script tests, with zero failures/skips and zero build warnings/errors; `scripts/verify.ps1` exited 0. Four actual browser suites passed on that rebuilt application: reviewed-target refusal and explicit new-preview recovery, existing-feature recovery, saved-text/stale-control races, and 16 light/dark normal/hover outline-button measurements. The earlier counts and broader scenario totals below are historical receipts, not reruns on this source. Complete source and command receipts remain in [JOURNAL.md](../JOURNAL.md); owner usability acceptance is still pending.

The earlier integration gate on source `c485b3af47c76bbd517e1f58e75c418c4ef9b7cd` passed 1,660 .NET tests, 0 failed/skipped, with zero build warnings/errors and advisory formatter drift. The 374-test script receipt applied because all script/package files matched that tested checkpoint. Nineteen selected-detail cases cover controlled active/stale failures, disposal and out-of-order same-component successes/failures; six ordering counterexamples failed with request-sequence equality disabled. Two actual browser checks at that checkpoint verified the named, focusable canvas region and A/B/A selection plus reload preserving an exact 52,049-character dirty buffer. The actual inventory again measured REST 92/5/97 and native/stdio 53/75 on DLL `CBC0D34755FD6DA48D939C6D13330080262D8D1EB23995BD61A4736B0406EE07`. All owned apps/browsers were stopped and application renderer/CircuitHost/failure matches were zero. The copied-criteria regression verifies Copy/ItemChanged/Propose state transitions; it does not independently exercise the Razor input handler.

The complete core-workflow checkpoint passed 1,640 .NET tests and 374 script tests, with a warnings-as-errors build reporting zero warnings/errors. Formatter completed with advisory drift; no formatting-clean claim is made. On that DLL (`0C49C65DB981A11099BA73CAF9C301B7E6582D58951F7F26E8338D6F2011593A`), 41 browser behavior checks passed: 12 convergence, 11 delivery and 18 whiteboard. The unchanged convergence assertion first exposed a feature-dropdown selection mismatch and then passed after stable option keys. Whiteboard used viewport captures with every fixture, behavior assertion and full-content comparison unchanged. Two original full-page screenshot attempts exceeded their 30-second budget; one bounded full-page probe succeeded in 42.466 seconds. These capture diagnostics remain distinct from behavior results. The actual inventory helper on the same DLL measured 92 application / 5 admin / 97 total REST operations, 53 native MCP tools and 75 stdio tools. All owned apps and browsers were stopped and application renderer/CircuitHost/failure matches were zero.

Earlier checkpoints remain historical evidence: 60 combined workspace browser scenarios and a 55-member stable gallery traversal; eight canvas module-failure/recovery checks plus 18 whiteboard checks; and 27 polling/ARIA/legacy-receipt, restoration and follow-up checks. Recovery tests block a module import or throw after real controller setup, then verify full saves, two unsaved buffers, cleanup, remount retry and real keyboard movement. Eight recovery-picker light/dark normal/hover samples had minimum contrast 4.69:1; this is narrow sample coverage. Actual REST/native/stdio reads, writes, nine publication groups and process-restart receipts remain retained. Detailed receipts, source hashes and failed-check corrections are in [JOURNAL.md](../JOURNAL.md).

## Storage verification

IW-T02 storage/core is implemented. On October 31, 2026, Windows/PowerShell7.6.5/.NET SDK10.0.403, the focused `dotnet test tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj --filter FullyQualifiedName~IdeaWorkspace` gate rebuilt current source and passed 51 tests, failed 0, skipped 0. Receipts are ignored under `_sandbox/579/storage-tests-gate.log` and `test-results/workspace-storage-gate.trx`. Independent review found no unresolved material storage issue.

The tests exercise real SQLite, file restart, two independent concurrent writers, a deterministic WAL read/write interleaving, audit and late-receipt abort triggers, exact long-content/whitespace retention, separate placements/relative frame movement, cycles and foreign targets, original-source preservation, per-client/session checkpoints and property-order-independent retries. Both typed and manually parsed unsupported input fields fail explicitly. This proves the core service; REST/MCP/UI behavior is verified in later tasks.

## Read surfaces and verification

IW-T03 adds six read operations to both MCP transports: `get_workspace_capabilities`, `list_workspaces`, `get_workspace_context`, `list_workspace_components`, `get_workspace_component`, `get_workspace_changes`. REST exposes `/api/workspace-capabilities`, `/api/workspaces` and the workspace context/component/change collections. Catalog and component snapshots expire after ten minutes, with a bounded cache of 64; invalid/mismatched/expired cursors fail explicitly. Complete history uses an ascending, fixed activity watermark. This history is module workspace activity; current legacy source/task records are read fresh through context/detail, not claimed as whole-app audit parity.

The rebuilt service gate passed 71 tests (51 storage plus 20 query cases), failed 0, skipped 0. `node --test scripts/workspace-mcp.test.mjs` passed five actual stdio/HTTP adapter checks. `scripts/workspace-read-smoke.mjs` then passed against the actual disposable app through REST, native MCP and stdio MCP: six read tools, cross-transport cursor continuation across nine pages, insertion-stable membership, archived/rejected/standalone-project discovery, exact 140,047-character source/detail, current context/deep links and error codes. Native SDK omits optional null properties; domain comparisons normalize that wire difference and still compare all non-null values and authored text exactly.

Run the smoke only with explicit `KANBANFLOW_API_URL` pointing at disposable data; it creates labeled fixture ideas/projects and does not clean history. Runtime evidence is in ignored `_sandbox/579/read-smoke.log`, `workspace-adapter-tests.log` and `query-tests.log`. Actual discovery measured 35 native and 57 wrapper tools, and OpenAPI exposed 78 API operations at this checkpoint.

Launch lesson: automatic review rejected a detached background launch. A managed foreground app was permitted; Windows rejected fixed port 5141 with socket10013, while an OS-assigned loopback port worked without changing any permissions or access policy. Use `--urls http://127.0.0.1:0`, the isolated database path and Development static assets; read the actual address from startup output and keep the process under session control. Never use this as authorization to change firewall/branch/authentication settings.

## Creation and write surfaces

Capture a new idea with `POST /api/workspaces` or `create_workspace`, providing an operationId, actor and title; optional body/plan/tags preserve complete text, including empty strings and Markdown whitespace. To open an existing project's primary workspace, provide projectId instead of the source-edit fields. This creates no dummy Idea and never replaces the project's current or original documents. Creation receipts have a separate global scope, so an identical retry returns the original workspace and revision even after subsequent edits or source deletion.

`POST /api/workspaces/{workspaceId}/commands` accepts operationId, expectedRevision, action, payload and actor. Native and stdio MCP expose `workspace_command` and twelve discoverable aliases for component/placement/connector operations, comments, checkpoints and plan proposal/acceptance. Payload is a JSON object. Capability discovery describes each component type's create/edit/server-owned fields, independent placement properties, required envelope fields and per-action payload fields. Unknown requested fields fail visibly.

REST also exposes typed collection routes: component create/update/delete; placement add/delete; layout-operations for atomic move/resize/group/order batches; connectors; comments; checkpoints; plan-revisions and acceptance. REST can take the actor from `X-KanbanFlow-Actor` or the body, defaulting to `api`. MCP defaults to `mcp-agent`; use the same explicit actor and exact envelope for cross-transport replay. A stale expectedRevision fails without changing content, audit or receipt. An identical replay returns its original result with replayed=true; it never overwrites later edits.

Component IDs can contain Unicode, slashes and literal percent sequences. Use `GET /api/workspaces/{workspaceId}/component?componentId={query-encoded-id}` for arbitrary identifiers; the stdio adapter uses this form. The older `/components/{componentId}` detail route remains compatible for path-safe IDs. Generic command payloads address all valid component/placement/connector IDs without path-decoding ambiguity. An actual transport probe caught ASP.NET preserving `%2F` in path values; the query form fixes the mismatch without double-decoding a literal `%2F` in the stored identifier.

The T04 warnings-as-errors rebuilt gate passed 115 real-SQLite cases and eight wrapper/HTTP adapter checks. Actual `workspace-write-smoke.mjs` passed REST/native/stdio creation and command replay, full 145,022-character source and 132,058-character edited component, relative-frame layout, invalid-batch rollback, stale conflicts, strict identifiers, two-client A/B/A resume, draft acceptance, typed collection routes and existing-project capture. Read-smoke regression also passed. Independent review found unsupported top-level stdio arguments could be silently discarded; both MCP transports now check workspace keys against their advertised schemas before dispatch. After an actual app restart, `workspace-restart-smoke.mjs` recovered edits, both client checkpoints, accepted plan and capture/command receipts with no replay overwrite. Live discovery measured 49 native and 71 wrapper tools. These scripts require an explicit disposable API URL; restart verification also requires the fixture workspace ID from write-smoke.

## Gallery, whiteboard and resume — T05 verified locally

Open `/ideas` for the mixed gallery. Search spans developed workspace content as well as source text; kind/status filters and Load more retain the same catalog snapshot. `/ideas/board` preserves the previous status board. Each card opens `/ideas/workspace/{kind}/{id}`, with a full-content inspector, canvas/outline, source documents, discussion/replies, connections, versioned plan and Resume panel. The switcher traverses the complete mixed catalog.

Select a card title, or select its content in Outline. Add a supported component, save its complete content, and use Canvas placement to set parent frame, relative X/Y, dimensions, reading order and layer. Drag a title to move, its corner to resize, or focus a title and use arrows to move and Shift+arrows to resize. Frame descendants move together. Negative coordinates have a translated canvas origin; frame layers cannot hide their own children. Removing an appearance leaves its shared content. Explicit component removal retains the full before snapshot in module history.

Browser drafts are separate from persisted workspace checkpoints. Local/session storage supplies independent browser and tab identities. Each workspace retains its own unsaved buffers and immutable pending operation. Storage failure keeps the current buffer visible, pauses writes and exposes an explicit retry; every send/retry first saves the exact envelope. A source key and route generation protect callbacks across A/B/A navigation. Refresh and periodic checks disclose external saves. Load latest preserves dirty drafts; Compare and explicit Reapply select the new revision before Save. The Resume panel stores objective, focus, completed changes, questions, next actions and acknowledged activity per caller/session.

Returning to a workspace or reloading refreshes clean editor and layout buffers from the current saved revision. Dirty buffers keep their complete text, layout and original revision for comparison. A removed selection is cleared when its buffer is clean; an unsaved buffer for a removed card remains available for the explicit keep-as-new action. Resume lists removed focus selections so they can be explicitly unchecked before saving other changes.

The **Unsaved cards removed from the workspace** list appears with both Canvas and Outline. It includes removed cards whose unsaved text is no longer selected. Select a retained draft to inspect its complete content, then use **Keep draft as a new card**. If another unsaved new card already exists, recovery pauses and retains both buffers. Use **Add component** to return to the existing new card, finish saving it, then recover the removed card.

**Add component** opens a separate new-card draft even when another client supplied an unusual saved card ID. Existing browser drafts and pending retry commands retain their complete content while the editor separates saved cards from the unsaved card.

If the canvas controls cannot load, a warning leaves the editor, **Outline**, and precise layout fields available. Continue editing there, or switch to Outline and back to Canvas to retry loading drag and resize controls. Saved content and browser drafts remain available throughout that recovery.

Each live document reserves its checkpoint session with a Web Lock when available, so copied or duplicated tabs cannot replace one another's checkpoint. Ordinary reload can reuse the outgoing document's released session. On browsers or plain HTTP LAN origins without Web Locks, each new document receives a fresh session; navigation within that document retains it. Reload still restores the complete browser draft and lists prior durable checkpoints, while subsequent checkpoint saves use the new session. UUID generation falls back to cryptographically random bytes when `randomUUID` is unavailable. This follows the [Web Locks interface](https://www.w3.org/TR/web-locks/#navigator-mixins) and [Web Cryptography interface](https://www.w3.org/TR/webcrypto/#crypto-interface), whose secure-context requirements differ; no access-policy change is required.

Malformed or incompatible browser drafts stay intact until an explicit recovery choice. Recovery first copies the entire raw entry, including any pending command, to a unique browser backup and verifies an exact readback before clearing the active entry. The recovery UI retains the complete raw text for inspection and copying. If backup storage fails, the active entry remains intact. These backups have the same tab-session retention as browser drafts; copy the text elsewhere before closing the tab if it must be kept longer. Copying a saved plan over an unsaved plan also requires an explicit replace-or-cancel choice.

Text typed while stored-draft loading is blocked stays in the current buffer and is retained through explicit recovery; it is separate from the preserved original raw entry. Duplicate plan-item IDs and incompatible/null sections are rejected before rendering. Script-import failure, cancellation and circuit disconnection are handled during cleanup so an explicit storage retry can create a fresh interop wrapper.

If initialization failed before a valid stored draft was read, **Retry browser draft storage** does not replace it with newer in-memory typing. The workspace remains paused and requires the explicit preservation action, which verifies a complete backup of the original draft and any pending operation before continuing with the current typing. A draft already restored in this browser view keeps its normal retry and A/B/A behavior.

Selection and refresh before initialization succeeds are navigation, so they do not replace a real stored draft. Authored edits are retained separately until restoration or explicit preservation succeeds. Typing after preservation continues in the newer active draft while browser storage is reinitialized.

After another caller changes an idea's project association, loading or restoring the workspace clears old project-item and existing-feature IDs. Proposed project/feature names and all other drafts remain intact. No items from the new project are selected automatically: review and select them before previewing publication. Events arriving from the previous project's controls cannot put its hidden IDs back into the current selection.

If a save or refresh completes before an older background read, the workspace ignores that older result. Retrying a saved operation clears a draft only when its recorded send snapshot still matches the complete current buffer. Older pending operations without that comparison snapshot retain the current buffer for review. Canvas and Outline selection buttons expose their selected state to assistive technology, including separate appearances of one card.

At the earlier whiteboard checkpoint, the warnings-as-errors rebuild and focused gate passed 119 cases. Named Playwright CLI run `ideas579final` passed all 18 UI scenarios, including 56,014-character complete content, actual rendered Markdown/source/comments/plan, pointer/keyboard and 390px operation, relative groups/negative coordinates/layers, independent appearances, full draft conflict/reapply, actual GUID checkpoints, A/B/A/reload, standalone projects and the previous board. Five additional storage-failure scenarios passed with four controlled failures and two real receipt replays. Gallery proof traversed 55 members across two pages without gaps/duplicates while a late insertion was excluded from the active snapshot. The UI-authored content and layout matched REST/native/stdio exactly; a separate native MCP edit of 51,818 characters became visible through UI Refresh. Actual human usability feedback remains pending before adoption.

Repeatable browser scripts are [gallery](../scripts/workspace-gallery-smoke.browser.mjs), [whiteboard](../scripts/workspace-ui-smoke.browser.mjs), [storage recovery](../scripts/workspace-ui-recovery.browser.mjs), [restoration and independent tabs](../scripts/workspace-ui-restoration.browser.mjs), [project delivery](../scripts/workspace-delivery.browser.mjs), [late-review recovery/relink cases](../scripts/workspace-ui-followup.browser.mjs), [draft identity and exact checkpoints](../scripts/workspace-ui-identity.browser.mjs), [canvas initialization recovery](../scripts/workspace-ui-canvas-recovery.browser.mjs), [delayed-render races](../scripts/workspace-ui-races.browser.mjs), [feature recovery](../scripts/workspace-ui-feature-recovery.browser.mjs), [cache/receipt convergence](../scripts/workspace-ui-convergence.browser.mjs), [automatic polling](../scripts/workspace-ui-polling.browser.mjs), [reviewed publication target](../scripts/workspace-ui-preview-target.browser.mjs) and [button contrast](../scripts/workspace-ui-button-contrast.browser.mjs) under `scripts`. Run from ignored scratch using a named Playwright CLI session pointed at disposable data. They retain labeled fixtures and screenshots. In this installed CLI, `.fill` of a 51,774-character plain textarea emitted 5,506 input events. Long fixtures therefore simulate one paste through the actual textarea/input handler; ordinary short-field and keyboard checks remain native. Tests wait for the exact target workspace identity and ready state, with bounded waits. Failure injection controls only browser storage, the actual module boundary or delivery of real server messages; persistence, audit and receipts remain real. The race script checks sequential typing under delayed renders, stale card/appearance controls, keyboard focus when switching appearances and actual warning contrast in both themes. The feature-recovery script discovers replacement choices before preview, reproduces the missing historical target, then publishes the new task into the chosen existing feature through the UI.

## Plan and task identity — T06 implemented locally

Association keeps legacy Idea linkage and workspace linkage coherent in one transaction. `link_project` names an existing target and never copies idea narrative into that project's Description/Goals/Readme. Replacing a link requires the explicit expected current project ID. `create_project` creates a project from the complete idea documents while preserving its original workspace and terminal legacy promotion semantics. Both commands save the target project's full original documents before acknowledging the association. The same target capture occurs when a legacy promoted idea receives its first workspace edit.

The linked project's workspace owns the accepted delivery plan. Acceptance checks the source workspace revision and an explicit expected project-plan revision under the same reserved SQLite writer. Initially the canonical token is the project workspace revision; an unrelated project canvas change can conservatively require a refresh. A contribution expands the accepted plan, keeping earlier items and version history. Omitting an item does not retire its task.

Publication identity is independent of revision history: target project ID, originating workspace ID and originating item ID identify a mapping to one real Task/Feature. A server-generated canonical item ID separates two ideas that both call an item `step-1`. Accepted items retain their validated provenance; a draft copied from the canonical plan carries that provenance into the next version, while new items acquire the current source's provenance. Caller-supplied task IDs are never mapping authority. Dependencies are remapped from proposal-local item IDs to canonical IDs. Explicit relinking cannot reuse old-project task mappings in the new target.

Preview performs no writes. Publication can select a subset of accepted items; required predecessors must already be published or included. It creates only new Tasks, criteria and dependency edges, using `TaskService.CreateInContextAsync` inside the same transaction as workspace states, mappings, audit and receipt. Existing mapped tasks, completed criteria, human edits and board ordering remain unchanged. A changed published specification, missing/foreign target, forged provenance, stale canonical revision or late insert failure fails visibly and rolls back. Deleted targets are never silently recreated. This bridge performs no GitHub publication.

Pre-T06 accepted project plans retain their existing item IDs and history. Reads derive legacy provenance `(projectId, project:N, storedItemId, storedItemId)` in memory; the next audited write can persist that enrichment. New contributions receive server-generated canonical IDs. Two proposal entries that resolve to one canonical identity fail with `duplicate_canonical_identity` rather than dropping an entry.

Successful module commands also observe complete legacy source documents. `document.sourceSnapshots` contains `{id,source,actor,capturedAt}` versions when the observed source differs from the latest capture, or from the earliest original when no capture exists. A linked target is observed in the same transaction; an existing target state retains its earliest `originalSource` and adds a new full version when current Description/Goals/Readme differ. Linked-idea context exposes those versions as `projectSourceSnapshots`; the project workspace retains them in its own document. The Source documents panel labels the actor as **Captured by**, without claiming authorship. Reads, preview and identical receipt replay append no versions. Edits outside this module made after the last capture remain recoverable only while the source still exists; the module does not claim to archive unobserved external changes.

At the T06 checkpoint, the local build passed 178 focused workspace cases, failed 0, skipped 0 (warnings as errors). Actual REST/native/stdio delivery checks passed nine groups, and the delivery browser run passed all 11 steps, including full captured source documents after deletion. The JavaScript script suite passed 339 cases. The complete verification script exited 0 with a clean build and all 1,437 .NET tests passing; its formatter check reported advisory drift. The subsequent full legacy UI regression passed 56 of 56 scenarios with source-aware readiness and focus waits. Discovery at this checkpoint measured 97 REST operations (92 application and five admin), 53 native MCP tools and 75 stdio tools, with eight page routes and 49 Razor components.

After an actual app stop/start against the same disposable SQLite database, fresh native/stdio clients and REST recovered the canonical accepted plan, provenance, task identity, human-edited Done status/content/completed criteria, two caller checkpoints and captured source history. Identical publication replays through all three transports returned the original result without changing current state, history or task count. Capture and component-command receipts also replayed unchanged. The earlier component/checkpoint restart script passed again with a complete 132,058-character component. Repeat this using [workspace-delivery-restart-smoke.mjs](../scripts/workspace-delivery-restart-smoke.mjs), running its prepare and verify phases as separate processes with the app restart between them. All databases and receipts belong in ignored `_sandbox/`; do not point these fixture-writing scripts at a live board. These are automated local results; owner usability review remains pending before adoption.

## Using the workspace from the UI or an agent

This walkthrough is for a person developing ideas and for an MCP/API client that needs to return to the same work later. Use `/ideas` to discover cards, `/ideas/workspace/{kind}/{id}` to work, and **Project and Kanban** to associate a project and review publication. **Plan** can copy the accepted canonical plan into a new draft while retaining item provenance. The Kanban header links back to the primary project workspace with `?view=plan`. **Read current task status** opens a fresh read from Tasks; status editing stays in the Kanban.

The native HTTP MCP server is at the application's `/mcp` route. The stdio wrapper uses the same domain operations through REST. Discover capabilities before composing payloads. Tool names below are available through both MCP transports; REST path parameters identify the workspace instead of putting `workspaceId` in its body.

| Operation | MCP tool | REST |
|---|---|---|
| Capabilities | `get_workspace_capabilities` | `GET /api/workspace-capabilities` |
| Complete catalog | `list_workspaces` | `GET /api/workspaces` |
| Full context | `get_workspace_context` | `GET /api/workspaces/{id}/context` |
| Component summaries/detail | `list_workspace_components`, `get_workspace_component` | `GET /api/workspaces/{id}/components`, `GET /api/workspaces/{id}/component?componentId=...` |
| Module history | `get_workspace_changes` | `GET /api/workspaces/{id}/changes` |
| Capture/open | `create_workspace` | `POST /api/workspaces` |
| Generic mutation | `workspace_command` | `POST /api/workspaces/{id}/commands` |
| Associate/create project | `link_workspace_project`, `create_workspace_project` | `POST /api/workspaces/{id}/project-association`, `POST /api/workspaces/{id}/project` |
| Read-only delivery preview | `preview_workspace_publication` | `POST /api/workspaces/{id}/publication-preview` |
| Publish reviewed tasks | `publish_workspace_plan` | `POST /api/workspaces/{id}/publication` |

For `workspace_command`, send `workspaceId`, `operationId`, `expectedRevision`, `action`, `payload` and `actor`. Mutation aliases use those arguments without `action`. The REST generic command body omits `workspaceId`; REST convenience routes flatten their action fields alongside `operationId`, `expectedRevision` and `actor`. Preview is a read: it takes the two revision fields and optional selection/feature fields, with no operation ID or actor in its body.

### 1. Discover every page, then retrieve full content

The following PowerShell 7 helpers use the application's actual local address supplied by `KANBANFLOW_API_URL`. The write examples create idea/project/task records in that chosen instance.

```powershell
$workspaceApi = $env:KANBANFLOW_API_URL
if (-not $workspaceApi) { throw 'Set KANBANFLOW_API_URL to the actual local app address.' }
$workspaceApi = $workspaceApi.TrimEnd('/')
function Invoke-WorkspaceApi([string]$Method, [string]$Path, [object]$Body = $null) {
    $call = @{ Method = $Method; Uri = "$workspaceApi$Path" }
    if ($null -ne $Body) {
        $call.ContentType = 'application/json; charset=utf-8'
        $call.Body = ConvertTo-Json -InputObject $Body -Depth 50
    }
    Invoke-RestMethod @call
}
function New-WorkspaceCommand([object]$Context, [string]$Action, [object]$Payload) {
    @{ operationId = [guid]::NewGuid().ToString('N'); expectedRevision = $Context.revision
       action = $Action; payload = $Payload; actor = 'guide-client' }
}
$catalog = @()
$cursor = $null
do {
    $path = '/api/workspaces?limit=50'
    if ($cursor) { $path += '&cursor=' + [uri]::EscapeDataString($cursor) }
    $page = Invoke-WorkspaceApi GET $path
    $catalog += $page.items
    $cursor = $page.nextCursor
} while ($cursor)
```

Keep `kind`, `status`, `q` and an explicit `limit` unchanged when continuing a filtered traversal. Follow `nextCursor` until null; `total` is the snapshot's membership. Cursors expire after ten minutes and can become unavailable after eviction or restart; use the actionable error to restart discovery. A catalog or component summary deliberately omits content. Get the workspace context and each required full component detail; URL-encode arbitrary component IDs in the detail query. Never treat one page or a summary as the complete idea.

### 2. Capture an idea or open an existing project

```powershell
$capture = @{ operationId = [guid]::NewGuid().ToString('N'); actor = 'guide-client'
    title = 'Garden sensory bench'; body = "# Concept`n`n  Keep this complete Markdown.  `n"
    plan = "## Starting plan`n`n- Explore materials`n- Test a prototype`n" }
$created = Invoke-WorkspaceApi POST '/api/workspaces' $capture
$key = $created.workspaceId
$context = Invoke-WorkspaceApi GET "/api/workspaces/$key/context"
```

Retain the exact `$capture` for an uncertain retry. To open an existing project, use a separate creation request containing `operationId`, `actor` and `projectId` only; it returns `project:N` and creates no dummy idea. It preserves existing workspace content. `body` and `plan` keep null, empty and authored whitespace semantics; supply complete strings. Existing legacy sources without saved state can be read at revision 0 without creating anything.

### 3. Compose content and arrange independent placements

```powershell
$request = New-WorkspaceCommand $context 'create_component' @{
    component = @{ id = 'materials-note'; type = 'research'; title = 'Material research'
        content = "## Findings`n`n- Compare outdoor durability`n- Keep the final requirement`n"
        url = 'https://example.org/materials' }
    placement = @{ id = 'materials-appearance'; x = 40; y = 60
        width = 360; height = 240; zIndex = 10; order = 6 }
}
$saved = Invoke-WorkspaceApi POST "/api/workspaces/$key/commands" $request
$context = Invoke-WorkspaceApi GET "/api/workspaces/$key/context"
$request = New-WorkspaceCommand $context 'move_components' @{
    placements = @(@{ placementId = 'materials-appearance'; parentId = 'place-frame-research'
        x = 20; y = 60; width = 360; height = 240; zIndex = 1; order = 5 })
}
$saved = Invoke-WorkspaceApi POST "/api/workspaces/$key/commands" $request
```

The parent above is the Research **placement** in a freshly initialized workspace. For an existing layout, resolve the actual frame placement from context first. Component IDs and placement IDs are independent. Child X/Y are parent-relative; moving a frame keeps the child's offsets. One `move_components` payload can move, resize, reparent and reorder several placements atomically. Omitted layout fields stay unchanged; `parentId:null` moves an appearance to the canvas root. `add_placement` reuses content; `remove_placement` preserves it. A task-reference component supplies a real `taskId` in the linked project, with current status read from Tasks.

### 4. Save a checkpoint, switch ideas and resume

Refresh context after each successful command. Use `save_checkpoint` with stable, caller-owned `clientId`/`sessionId` and a payload such as:

```json
{
  "clientId": "guide-agent", "sessionId": "session-01",
  "objective": "Choose a weatherproof material",
  "focusedComponentIds": ["materials-note"],
  "completedChanges": ["Captured the research card"],
  "openQuestions": ["Which coating survives winter?"],
  "nextActions": ["Compare two suppliers", "Draft the prototype criteria"],
  "lastAcknowledgedActivityId": 0
}
```

Zero acknowledges no prior activity. After processing changes, use the actual acknowledged position. Save B's checkpoint against B's key and component IDs; reopening A returns A's document and checkpoints. Browser buffers also remain separate per workspace and tab session. Agents manage their own stable caller IDs rather than sharing the browser's storage keys.

To catch up, call `get_workspace_changes` or `/changes` with `afterActivityId` from the checkpoint. Process all pages through `nextCursor`, preserving the initial position/page size; acknowledge the returned `watermark` only after processing that batch. History is ascending and includes complete before/after JSON. It covers module workspace activity; refresh context/current task reads for external legacy edits.

### 5. Link a project, propose and accept shared work

Use `link_project` with `{projectId: N}` from the catalog. For replacement, include the exact `expectedCurrentProjectId` from context. Alternatively, an unpromoted, unlinked idea can use `create_project` with `{name: "Garden bench delivery"}`. Its accepted standalone plan, if any, is carried into the new canonical project plan. Both actions retain the idea key and update the legacy association atomically.

Read fresh context before composing a proposal. The `propose_plan` payload contains a title, the canonical baseline and local item IDs:

```json
{
  "title": "Bench prototype plan",
  "expectedProjectPlanRevision": 1,
  "items": [
    { "id": "step-1", "title": "Choose materials", "description": "Compare complete supplier specifications.",
      "acceptanceCriteria": ["Record durability and cost"], "dependsOn": [] },
    { "id": "step-2", "title": "Build the prototype", "description": "Use the selected material.",
      "acceptanceCriteria": ["Demonstrate the prototype"], "dependsOn": ["step-1"] }
  ]
}
```

Replace the illustrative canonical token `1` with the reviewed `context.projectPlanRevision`, and use `context.revision` in the mutation envelope. The server assigns the draft revision ID. Retrieve it from the operation's `ActivityEvent.AfterJson` document, or from refreshed `document.planRevisions` when its revision still matches the result. `result.activityId` identifies the exact source event; `/changes?afterActivityId={activityId-1}&limit=1` retrieves it even if another writer subsequently adds a draft.

Accept using `accept_plan` with `{planRevisionId,expectedProjectPlanRevision}` and the current source revision. A project workspace's own draft save advances its canonical token; source snapshots and unrelated project edits can also advance it. Review the current shared plan before adopting a newer token. The UI disables stale acceptance and offers an explicit reviewed-current action. A linked idea's stale proposal is never silently restamped.

To expand an existing plan, copy `context.acceptedPlan.items`, retaining each existing item's `provenance`; assign new local IDs and omit provenance/publication IDs for genuinely new items. Dependencies in the proposal refer to its item IDs. The server remaps them to canonical IDs and keeps prior accepted items that were omitted. Do not invent `publishedTaskId` or `publishedFeatureId`; supplied IDs are checked against the durable mapping. The top-level proposal accepts only `title`, `items` and `expectedProjectPlanRevision`; revision identity, status and audit fields are server-owned. Dependency validation uses one iterative graph traversal, preserving every authored item without recursive stack growth.

Relinking to a different project requires new provenance for new work and cannot reuse the old target's mappings. Task-reference cards must also resolve to tasks in the proposed target. Otherwise association returns 409 `project_link_task_conflict` with the incompatible card IDs and no saved effect. Review each card and explicitly convert it to a note with `update_component` using `type:"note"` and `taskId:null`, preserving its title/content, or choose a compatible target before retrying. Removed component discussions retain their history and accept replies with the original parent and matching historical anchor; new top-level comments still require a current component.

### 6. Preview, review and explicitly publish

```powershell
$context = Invoke-WorkspaceApi GET "/api/workspaces/$key/context"
$previewBody = @{ expectedRevision = $context.revision
    expectedProjectPlanRevision = $context.projectPlanRevision
    itemIds = @($context.acceptedPlan.items | ForEach-Object { $_.id }) }
$preview = Invoke-WorkspaceApi POST "/api/workspaces/$key/publication-preview" $previewBody
# Review full Items, NewTaskCount, ExistingTaskCount, dependencies and feature selection.
$request = @{ operationId = [guid]::NewGuid().ToString('N'); actor = 'guide-client'
    expectedRevision = $preview.revision; action = 'publish_plan'
    payload = @{ expectedProjectPlanRevision = $preview.projectPlanRevision
        itemIds = $previewBody.itemIds } }
$published = Invoke-WorkspaceApi POST "/api/workspaces/$key/commands" $request
$published.publication.items
```

Use canonical IDs from `acceptedPlan.items`, including unpublished prerequisites, or omit `itemIds` for all. Preview performs no saves and includes full descriptions/criteria, existing live tasks and available feature choices. Optional `featureId` selects an existing project feature; optional `featureName` creates a new one for new tasks; do not provide both. With neither, the first publication uses the plan title and later publications reuse the durable feature. If selection or feature changes, preview again and publish the same reviewed fields/tokens. Existing mapped tasks are preserved even when a later operation selects them again.

A human can move a published task to another feature in the same project and remove its now-empty original feature. Preview follows the live task's current feature/project and retains the original mapping as history. If no historical publication feature still exists, existing tasks remain inspectable and publishable; adding new tasks requires an explicit current feature or new feature name. Missing tasks and tasks moved to another project still fail visibly.

The Project and Kanban panel loads existing feature choices independently of publication preview. Use **Reload feature choices** to refresh those choices or retry a failed read, then select a replacement feature and preview again. A failed preview does not require creating a temporary feature just to discover the current choices.

`publication.items` returns canonical/source identity, real `taskId`/`featureId` and `created`. Read `GET /api/tasks/{taskId}` or the native `get_task` tool for current task data; the workspace's task inspector also reads fresh. Update status in the existing Kanban UI, then refresh the inspector. Acceptance and publication never treat a copied status on an idea card as authority.

### 7. Handle conflicts and uncertain outcomes without losing intent

Keep the exact operation ID, actor, expected revision, action and payload until the result is known. A transport interruption calls for replaying that same envelope; a committed receipt returns `replayed:true` without applying the action again. Do not generate a new operation ID merely because its response was lost. Creation uses its separate global receipt scope and follows the same rule.

A known `revision_conflict` or `project_plan_revision_conflict` calls for loading current context, comparing the complete saved content with the retained draft, and explicitly choosing a revised command. A different payload or actor with an already committed operation ID conflicts. Use a new operation ID for the new reviewed intent. `missing_publication_dependency`, `forged_provenance`, `duplicate_canonical_identity`, `published_specification_changed` and `publication_target_missing` identify issues to resolve before retrying; a deleted task/feature/project is not silently recreated. REST errors expose `code`, `message` and `status`; MCP preserves the domain code/status in its error response.

Source contracts and payload descriptors are implemented in [IIdeaWorkspaceService](../src/KanbanFlow/Services/IdeaWorkspaces/IIdeaWorkspaceService.cs), [delivery DTOs](../src/KanbanFlow/Services/IdeaWorkspaces/IdeaWorkspaceDelivery.cs), [capabilities](../src/KanbanFlow/Services/IdeaWorkspaces/IdeaWorkspaceService.Capabilities.cs), [REST routes](../src/KanbanFlow/IdeaWorkspaceEndpoints.Writes.cs) and the [MCP delivery tools](../src/KanbanFlow/Mcp/IdeaWorkspaceMcpTools.Delivery.cs). Runtime receipts and the final source checkpoint belong in [the work journal](../JOURNAL.md).

The catalog accepts `kind:"all"` as the unfiltered form advertised by both MCP transports. The current picker loads the complete catalog, and content search scans complete saved workspace documents. Very large catalogs may need an indexed search and paged picker in later work; no result is silently dropped to hide that cost. Audit deliberately retains complete before/after workspace documents, so storage grows with both document size and mutation count. No automatic compaction or retention deletion is implemented. A future storage policy must preserve authored versions, receipts and rollback evidence before changing that contract.

The guide's four literal PowerShell blocks were subsequently executed unchanged against the isolated local app, with the intervening checkpoint/project/propose/accept steps and exact source-activity draft lookup. The run passed all 56 assertions: complete 217-member catalog traversal, full strings including an 86,064-character B source, relative placement, A/B/A checkpoint isolation, read-only preview, real tasks/criteria, exact receipt replay and stable task IDs under a later publication operation. The ignored executable receipt is `_sandbox/579/guide-example.ps1`; it produced two real tasks in the disposable instance. This verifies the examples without supplying human adoption feedback.

### Current master integration check — November 1, 2026 UTC

After incorporating the merged project-memory change `f1be4156dd716551adc45878e6049699fd679b74`, the required gate passed **1,721 .NET tests** with zero failures/skips and zero build warnings/errors; all **374 script tests** passed. The feature-recovery and reviewed-publication-target browser flows passed again against that rebuilt application. The feature-lifetime and placement-revision regressions passed **20/20**; their controlled baseline exposed two failures with eighteen controls passing. The earlier 1,686-case and browser/color checkpoints above remain tied to their recorded source; their measurements are not relabeled as reruns. Human walkthrough/usability acceptance remains pending.