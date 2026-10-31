# Persistent idea and project workspaces

Implementation contract for [issue #579](https://github.com/joelfuller2016/KanbanFlow-v2/issues/579). Source baseline: `42f17b459f0bee0dce78da48d6a2234f75f6fda6`, October 31, 2026. The issue owns task completion. This guide records the concrete contracts and will be updated with observed usage as implementation proceeds.

## Identity, compatibility and saved content

A workspace has a stable key `idea:{positive-id}` or `project:{positive-id}`. These keys let the catalog include every existing idea and standalone project without creating dummy records or mutating on reads. An idea keeps its key after project association. A project has exactly one primary workspace, `project:{id}`; contributing idea workspaces retain their own notes and point at that project's accepted plan. The catalog lists each key once, with its kind and relationships explicit.

Legacy Idea and Project documents remain complete and separately accessible. A first mutation creates an additive `IdeaWorkspaceState` snapshot containing the original source content and title. Association must also atomically capture the target project's complete Description/Goals/Readme in `project:{id}` state, without replacing a prior snapshot, even when that project was never otherwise edited through a workspace. Missing/deleted source records are represented explicitly when a saved workspace exists; deleting a source cannot delete the saved workspace. Current canonical task status is read from Tasks, never copied into a component's editable status.

New persistence uses explicitly named `IdeaWorkspaceStates` and `IdeaWorkspaceReceipts` tables and the `KanbanFlow.Models.IdeaWorkspaces` / `KanbanFlow.Services.IdeaWorkspaces` namespaces. This avoids collision with unmerged PR #511's `Services.Workflows.WorkspaceService`, request/brief/plan tables and `/api/workflows` routes. Do not merge or cherry-pick that unrelated 145-file PR. Reuse its proven invariants and the current master's existing `TaskService.CreateInContextAsync` for atomic task publication. Existing idea tools and status enum ordinals stay compatible.

Each state stores a versioned typed document: components, independent placements, connectors, threaded comments, draft/accepted plan revisions and per-client checkpoints. Content is kept in full. Supported types are frame, note, markdown, research, image, question, decision, checklist, table, plan and task-reference. Components are content records; placements have their own ID, component ID, parent frame-placement ID, x/y/width/height, z-order and outline order. Child coordinates are parent-relative; moving a frame preserves child offsets, and UI rendering sums ancestor positions for world coordinates. Removing a placement leaves content intact. An explicit component removal retains its complete before snapshot in history. Parent cycles, missing targets and cross-workspace moves fail visibly.

## Mutation and read consistency

All UI, REST and MCP commands use one `IIdeaWorkspaceService`. New workspace writes require a nonempty operation ID, expected workspace revision and actor label. Shared project-plan acceptance/publication additionally requires the expected canonical project-plan revision: two different contributing idea revisions cannot authorize overwriting the same stale project plan. SQLite reserves the writer before reading current state. Every affected workspace/project-plan state, operation receipt and complete ActivityEvent before/after snapshot commits in one transaction. Stale revisions produce a conflict and no partial write. The identical operation/payload replay returns the prior result even if its expected revision is now old; the same operation ID with different content conflicts. The first successful mutation advances revision 0 to 1.

Caller/session checkpoints coexist. Saving or opening workspace B never overwrites A or another caller's checkpoint. A resume response includes current revision, checkpoints and a paged changes cursor. A checkpoint is a navigational aid, not the accepted plan. Acknowledged changes survive process restart without a final checkpoint.

Catalog pages use a stable snapshot cursor which identifies the complete ordered membership/filter generation. New inserts cannot cause gaps/duplicates in that traversal; invalid/expired cursors fail explicitly. Component and activity pages have explicit continuation and full detail reads; APIs never shorten authored text. Summary endpoints explicitly return metadata. Context returns original/current source documents, project identity/deep links, the complete current plan, checkpoints and component paging descriptors. Task-reference details use current domain task data.

## Proposed public surface

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

## Storage verification

IW-T02 storage/core is implemented. On October 31, 2026, Windows/PowerShell7.6.5/.NET SDK10.0.403, the focused `dotnet test tests/KanbanFlow.Tests/KanbanFlow.Tests.csproj --filter FullyQualifiedName~IdeaWorkspace` gate rebuilt current source and passed 51 tests, failed 0, skipped 0. Receipts are ignored under `_sandbox/579/storage-tests-gate.log` and `test-results/workspace-storage-gate.trx`. Independent review found no unresolved material storage issue.

The tests exercise real SQLite, file restart, two independent concurrent writers, a deterministic WAL read/write interleaving, audit and late-receipt abort triggers, exact long-content/whitespace retention, separate placements/relative frame movement, cycles and foreign targets, original-source preservation, per-client/session checkpoints and property-order-independent retries. Both typed and manually parsed unsupported input fields fail explicitly. This proves the core service; REST/MCP/UI behavior is verified in later tasks.
