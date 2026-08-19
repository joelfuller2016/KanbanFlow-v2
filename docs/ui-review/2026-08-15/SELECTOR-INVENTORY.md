# Phase 0 selector and interop inventory

- **Snapshot baseline:** `3245d16c6a937d051fff8e7e111d1639b6364f16`
- **Razor scope:** `src/KanbanFlow/Components/**/*.razor`
- **JavaScript scope:** `src/KanbanFlow/wwwroot/js/kanbanflow.js`
- **Purpose:** preserve automation and production identity while the #322 layout changes

This inventory freezes the existing identity surface before visual implementation begins. It
contains all 169 Razor source lines that mention `data-testid` across 17 files: 168 attribute
declarations plus the `EditableMarkdown` generated-ID contract comment. Generated IDs retain
their existing prefixes and suffixes even when the declaration is helper-backed. Later PRs may
add hooks; deleting or renaming anything below requires an explicit migration test and PR callout.

`EditableMarkdown` expands the caller prefixes `edit-item-description` and
`task-detail-description` into `{prefix}-read`, `{prefix}-edit-control`, and
`{prefix}-edit`. Those six runtime IDs are frozen even though the caller lines themselves do not
contain a literal `data-testid` attribute.

The snapshot can be regenerated from the stated baseline with:

```powershell
rg -n --glob '*.razor' 'data-testid' src/KanbanFlow/Components
rg -n 'querySelector|querySelectorAll|draggable:|invokeMethodAsync' src/KanbanFlow/wwwroot/js/kanbanflow.js
rg -n 'JS\.Invoke(?:Void)?Async|\[JSInvokable\]' src/KanbanFlow/Components --glob '*.razor'
```

## Automation consumers

`scripts/ui-e2e.mjs` consumes the board, outline, filter, task-detail, activity, Projects, Settings,
and Ideas families recorded below, including their parameterized IDs and the structural drag
selectors. `TestRunnerService` additionally consumes `settings-app-info`, `settings-tests`,
`settings-test-buttons`, `kanban-board`, `idea-card-{id}`, both Ideas New/Triaging column
families, every WorkStatus `board-column-{status}`/`-cards` family, and
`board-card-{taskId}`. A rename must migrate both browser and in-app test consumers.

## Production DOM and SortableJS contracts

| Contract | Current meaning |
| --- | --- |
| `.board-column-cards[data-status]` | A droppable board container. Its direct draggable roots have positive `data-task-id` values; an empty-state element stays outside it as a sibling. |
| `.board-column-cards-inert[data-status]` | A non-droppable card container. It must never be bound to SortableJS. |
| `.feature-tasks[data-feature-id]` | An outline task container. Expanded subtask siblings may coexist, but only direct roots carrying `data-task-id` are draggable. |
| `draggable: '[data-task-id]'` | SortableJS must ignore expanded non-task siblings. |
| `data-task-id`, `data-status`, `data-feature-id` | Identity values passed across the JavaScript/.NET boundary; rendered-child indices are not persistence identities. |
| `.kanban-column-terminal` | Marks a terminal column and exposes the accessible collapse toggle. |
| `.kanban-column-collapsed` | Marks the current collapsed state. Terminal columns are default-collapsed, but the user can expand/collapse them for the component session. |
| `.kanban-column-inert` | Marks a column that is neither a drag source nor a drop target. |
| `.kanban-column-empty` | Empty-state grid sibling, emitted only when the column is empty and expanded; never a child of the Sortable container. |
| `.project-shell`, `#project-outline`, `outline-open` | Existing outline accessibility synchronization inputs. Breakpoint behavior may be deliberately migrated with matching tests in the responsive-shell PR. |
| `[data-testid="task-detail-close-button"]` | Production Escape-key close target used by `initTaskDetailDialog`. |
| `[data-testid="board-card-{id}-title"]` | Production focus-restoration target used by `focusTaskCard`. |
| `[data-testid="kanban-board"], .kanban-board, .board-column-cards` | Production fallback focus target; `kanban-board` also participates in application CSS. |
| `[data-dialog-autofocus]`, `[data-kf-escape-discard]` | Dialog initial-focus and nested-editor Escape ownership markers. |

`_nextTaskId` skips siblings without a valid positive task identity. Both Sortable handlers put
the physically moved DOM node back before invoking .NET so Blazor remains the DOM owner.

## JavaScript interop inventory

| Entry point | Direction and consumers | Frozen behavior |
| --- | --- | --- |
| `KanbanFlow.initBoardDragDrop` | Razor to JS: `ProjectView`, `Ideas` | Rebind `.board-column-cards`; invoke identity-anchored `OnCardMoved`; no-op when a route emits no droppable container. |
| `KanbanFlow.initLeftNavDragDrop` | Razor to JS: `ProjectView` | Rebind `.feature-tasks`; invoke identity-anchored `OnTaskReordered`. |
| `KanbanFlow.destroySortables` | Razor to JS: `ProjectView`, `Ideas` | Destroy both board and outline Sortable instances. |
| `KanbanFlow.initProjectOutlineAccessibility` | Razor to JS: `ProjectView` | Synchronize mobile outline `aria-hidden` and `inert`; install media-query and class observers. |
| `KanbanFlow.updateProjectOutlineAccessibility` | Internal/public object helper | Apply the current outline accessibility state. |
| `KanbanFlow.destroyProjectOutlineAccessibility` | Razor to JS: `ProjectView` | Remove the observer and media-query listener. |
| `KanbanFlow.initTaskDetailDialog` | Razor to JS: `TaskDetailPanel` | Focus panel and make Escape activate `task-detail-close-button`, except in text-entry controls. |
| `KanbanFlow.focusTaskCard` | Razor to JS: `ProjectView` | Restore focus to `board-card-{id}-title`, falling back to the board. |
| `KanbanFlow.initMarkdownEditable` | Razor to JS: `EditableMarkdown` | Stop rendered link clicks from entering edit mode. |
| `KanbanFlow.initDialog` | Razor to JS: add/edit/idea/Ideas dialogs | Capture opener, focus preferred/first target, trap Tab, and route Escape to `CloseFromJs`. |
| `KanbanFlow.destroyDialog` | Razor to JS: add/edit/idea/Ideas dialogs | Remove the key handler and restore opener focus. |
| `KanbanFlow._nextTaskId` | Internal helper | Return the next positive sibling identity, or `0` for append. |
| `KanbanFlow._revertDraggedNode` | Internal helper | Return Sortable's moved node to Blazor's last rendered position before state changes. |
| `KanbanFlow._dialogFocusables` | Internal helper | Produce the visible focus sequence used by the dialog trap. |
| `eval` | Razor to JS: `TaskDetailPanel.ScrollToOutline` | Current task-root scroll query. It is frozen for compatibility here and should be coordinated with #167 before replacement. |

The exact JS-to-.NET methods are:

- `ProjectView.OnCardMoved(int taskId, string newStatus, int beforeTaskId)`
- `ProjectView.OnTaskReordered(int taskId, int newFeatureId, int beforeTaskId)`
- `Ideas.OnCardMoved(int ideaId, string newStatus, int beforeItemId)`
- `AddItemModal.CloseFromJs()`, `EditItemModal.CloseFromJs()`,
  `IdeaModal.CloseFromJs()`, and `Ideas.CloseFromJs()`

The three move/reorder callbacks keep their three-argument shapes and next-item identity semantics;
`0` means append. `CloseFromJs` remains the single Escape close path for modal dialogs.

## Shared `/ideas` boundary

`/ideas` is explicitly in the regression boundary because it consumes the shared `KanbanBoard`,
`KanbanColumn`, board Sortable initialization, and dialog helpers. The frozen Ideas surface
includes every declaration below in `Pages/Ideas.razor`, `Shared/IdeaCard.razor`, and
`Shared/IdeaModal.razor`, plus the shared board/column declarations. In particular, preserve:

- search, status, and tag filter hooks; announcer, load/move errors, and empty actions;
- `add-idea-New-button`, modal, delete, promote, and success-state hooks;
- every `idea-card-{id}-*` identity family, including Move to, promote, and project link;
- the `New`, `Triaging`, `Promoted`, `Archived`, and `Rejected` status set;
- Ideas' derived `Priority desc, CreatedAt desc, Id desc` order and deliberate disregard of
  `beforeItemId`, because Ideas have no persisted `SortOrder`;
- `Promoted` as a non-drag/inert column; `Archived` and `Rejected` as terminal, default-collapsed
  but user-toggleable rails except when status-focused; and only `New` exposing the empty-column
  create action;
- non-droppable column behavior: a column that cannot accept drops emits no
  `.board-column-cards` container;
- stable identity keys on both `KanbanCard` and `IdeaCard`; and
- keyboard/touch Move to behavior and live announcements alongside pointer drag behavior.

The task board separately preserves `WorkStatus` order, identity-anchored moves, and persisted
`SortOrder`. Task-board-specific visual rules must be scoped so they do not alter the distinct
Ideas ordering or interaction contracts.

## Static-asset and style sentinels

`Components/App.razor` resolves Bootstrap, `app.css`, `KanbanFlow.styles.css`, SortableJS,
`js/kanbanflow.js`, and Blazor through fingerprint-aware `@Assets[...]` references, while
`Program.cs` calls `MapStaticAssets()`. Phase 0A must prove those responses and stylesheet
readability. The root custom property `--kf-board-drop-floor: 6rem` is the initial computed-style
sentinel; this evidence PR does not assert it as the permanent visual value.

## Complete `data-testid` source snapshot

### `src/KanbanFlow/Components/Layout/NavMenu.razor`

```razor
L0009: <a class="btn btn-sm btn-outline-light d-inline-flex align-items-center ms-2" href="/ideas" title="Ideas" aria-label="Ideas inbox" data-testid="nav-ideas">
L0016: <a class="btn btn-sm btn-outline-light d-inline-flex align-items-center" href="/settings" title="Settings" aria-label="Settings" data-testid="nav-settings">
```

### `src/KanbanFlow/Components/Pages/Home.razor`

```razor
L0047: <div class="project-grid" data-testid="project-grid">
L0051: data-testid="project-card" data-project-id="@p.Id">
L0083: <div class="kf-card-progress" data-testid="project-progress">
L0086: <span class="kf-card-progress-count" data-testid="project-progress-count">
L0101: <div class="kf-card-breakdown" data-testid="project-progress-breakdown">
L0113: data-testid="project-next-task"
L0138: data-testid="project-last-activity"
L0145: <span class="kf-claim-chip" data-testid="project-claim" @key="claim.Actor">
L0158: <span class="kf-claim-chip kf-claim-more" data-testid="project-claim-more">@p.HiddenClaimLabel</span>
```

### `src/KanbanFlow/Components/Pages/Ideas.razor`

```razor
L0011: <button class="btn btn-primary" @onclick="OpenCreateModal" data-testid="new-idea-button">
L0022: <div class="alert alert-success alert-dismissible fade show d-flex align-items-center" role="alert" aria-live="polite" data-testid="idea-promote-success">
L0025: <a class="alert-link ms-1" href="@($"/project/{promoteSuccess.ProjectId}")" data-testid="idea-promote-success-link">Open new project →</a>
L0035: <div class="alert alert-danger alert-dismissible fade show" role="alert" aria-live="assertive" aria-atomic="true" data-testid="idea-load-error">
L0059: <div class="alert alert-danger alert-dismissible fade show" role="alert" aria-live="assertive" aria-atomic="true" data-testid="idea-move-error">
L0070: data-testid="idea-search" @bind="searchFilter" @bind:event="oninput" @bind:after="DebouncedReload" />
L0074: <select id="idea-status-filter" class="form-select" data-testid="idea-status-filter"
L0086: data-testid="idea-tag-filter" @bind="tagFilter" @bind:event="oninput" @bind:after="DebouncedReload" />
L0094: <div class="visually-hidden" aria-live="polite" aria-atomic="true" data-testid="ideas-board-announcer">@announcement</div>
L0111: <div class="text-center py-5" data-testid="ideas-empty-state">
L0121: <button class="btn btn-outline-primary" @onclick="ClearFilters" data-testid="ideas-clear-filters">Clear filters</button>
L0127: <button class="btn btn-primary" @onclick="OpenCreateModal" data-testid="ideas-empty-create">Create your first idea</button>
L0173: data-testid="add-idea-New-button">
L0208: <div class="modal fade show d-block" tabindex="-1" role="dialog" aria-modal="true" aria-labelledby="delete-idea-modal-title" style="background-color: rgba(0,0,0,0.5);" data-testid="idea-delete-modal">
L0225: <button type="button" class="btn btn-danger" @onclick="ConfirmDelete" disabled="@isDeleting" data-testid="idea-delete-confirm">
L0249: <div class="modal fade show d-block" tabindex="-1" role="dialog" aria-modal="true" aria-labelledby="promote-idea-modal-title" style="background-color: rgba(0,0,0,0.5);" data-testid="idea-promote-modal">
L0279: <button type="button" class="btn btn-primary" @onclick="ConfirmPromote" disabled="@isPromoting" data-testid="idea-promote-confirm">
```

### `src/KanbanFlow/Components/Pages/ProjectView.razor`

```razor
L0071: data-testid="toggle-project-outline-button"
L0091: <button class="btn btn-sm btn-outline-primary" data-testid="ai-tools-toggle" aria-expanded="@ToAriaExpanded(aiControlsExpanded)" aria-controls="ai-tools-controls" @onclick="ToggleAiControls">
L0104: <button class="btn btn-sm btn-outline-primary" data-testid="ai-project-context-button" @onclick="ShowProjectContextAsync">
L0107: <button class="btn btn-sm btn-outline-primary" data-testid="ai-next-tasks-button" @onclick="ShowNextTasksAsync">
L0110: <button class="btn btn-sm btn-outline-secondary" data-testid="ai-generate-tasks-button" disabled title="Coming soon: create tasks from project context">
L0113: <button class="btn btn-sm btn-outline-secondary" data-testid="ai-assign-button" disabled title="Coming soon: claim and assign selected work to an AI agent">
L0120: <div class="card mt-3 shadow-sm" data-testid="ai-action-panel">
L0178: <div class="list-group" data-testid="ai-next-tasks-list">
L0203: <section class="requirements-panel border-bottom bg-light px-3 py-2" data-testid="requirements-panel" aria-labelledby="requirements-heading">
L0209: <span class="badge text-bg-secondary" data-testid="requirements-count">@GetOrderedRequirements().Count</span>
L0210: <button class="btn btn-sm btn-outline-secondary" data-testid="requirements-toggle" aria-expanded="@ToAriaExpanded(requirementsExpanded)" aria-controls="requirements-content" @onclick="ToggleRequirements">
L0224: <div class="text-muted small border rounded bg-white p-3 mt-2" data-testid="requirements-empty-state">
L0240: <article class="requirement-row border rounded bg-white p-2" data-testid="@($"requirement-row-{requirement.Id}")">
L0243: <span class="@GetRequirementPriorityBadgeClass(requirement.Priority)" data-testid="@($"requirement-priority-{requirement.Id}")">@requirement.Priority</span>
L0244: <span class="@GetRequirementStatusBadgeClass(requirement.Status)" data-testid="@($"requirement-status-{requirement.Id}")">@GetRequirementStatusDisplayName(requirement.Status)</span>
L0279: aria-live="assertive" aria-atomic="true" data-testid="task-move-error">
L0321: data-testid="add-task-@status-button">
L0346: data-testid="@($"board-column-{status}-no-match")">
```

### `src/KanbanFlow/Components/Pages/Settings.razor`

```razor
L0017: <div class="card" data-testid="settings-app-info">
L0065: <div class="card" data-testid="settings-tests">
L0087: <div class="d-flex flex-wrap gap-2 mb-3" data-testid="settings-test-buttons">
L0119: <div class="table-responsive" data-testid="settings-test-results">
```

### `src/KanbanFlow/Components/Shared/ActivityTimeline.razor`

```razor
L0024: <div class="kf-timeline-wrap" data-testid="activity-timeline">
L0032: data-testid="@($"activity-filter-{chip.Key}")"
L0045: <div class="alert alert-warning py-1 px-2 small mb-2" role="alert" data-testid="activity-timeline-degraded">
L0052: <div class="text-muted small fst-italic py-2" data-testid="activity-timeline-empty">
L0058: <ol class="kf-timeline" data-testid="activity-timeline-list">
L0070: data-testid="activity-timeline-entry"
L0097: <ul class="kf-timeline-changes" data-testid="activity-timeline-changes">
L0100: <li data-testid="activity-timeline-change">
L0131: data-testid="activity-timeline-toggle"
L0143: <div class="kf-timeline-boundary" data-testid="activity-timeline-boundary">
L0146: data-testid="activity-timeline-load-older"
```

### `src/KanbanFlow/Components/Shared/AddItemModal.razor`

```razor
L0007: <div class="modal fade @(IsOpen ? "show" : "")" tabindex="-1" data-testid="add-item-modal"
L0014: <button type="button" class="btn-close" @onclick="Close" aria-label="Close add item modal" data-testid="add-item-close-button"></button>
L0017: <p class="small text-muted mb-3" data-testid="add-item-context-help">@GetContextHelpText()</p>
L0032: <label class="btn btn-outline-primary btn-sm" for="add-item-type-feature" data-testid="add-item-feature-toggle">Feature</label>
L0036: <label class="btn btn-outline-primary btn-sm" for="add-item-type-task" data-testid="add-item-task-toggle">Task</label>
L0107: <button type="button" class="btn btn-secondary btn-sm" @onclick="Close" data-testid="add-item-cancel-button">Cancel</button>
L0109: data-testid="add-item-create-button"
```

### `src/KanbanFlow/Components/Shared/BoardFilterBar.razor`

```razor
L0037: data-testid="board-filter-bar"
L0047: data-testid="board-filter-search"
L0059: data-testid="board-filter-assignee"
L0074: data-testid="board-filter-claimed-by"
L0109: data-testid="@($"board-filter-priority-{priority}")">@priority</label>
L0118: data-testid="board-filter-hide-done"
L0129: <span class="text-muted small" aria-live="polite" aria-atomic="true" data-testid="board-filter-summary">
L0148: data-testid="board-filter-clear"
```

### `src/KanbanFlow/Components/Shared/EditableMarkdown.razor`

```razor
L0059: data-testid="@editTestId"
L0077: data-testid="@readControlTestId"
L0084: data-testid="@readTestId"
L0110: /// Prefix for the three data-testid hooks: <c>{TestId}-read</c>,
```

### `src/KanbanFlow/Components/Shared/EditItemModal.razor`

```razor
L0008: <div class="modal fade @(IsOpen ? "show" : "")" tabindex="-1" data-testid="edit-item-modal"
L0015: <button type="button" class="btn-close" @onclick="Close" aria-label="Close edit item modal" data-testid="edit-item-close-button"></button>
L0076: <div class="mb-3 d-flex gap-2 flex-wrap" data-testid="edit-item-github-links">
L0249: <button type="button" class="btn btn-secondary btn-sm" @onclick="Close" data-testid="edit-item-cancel-button">Cancel</button>
L0251: data-testid="edit-item-save-button"
```

### `src/KanbanFlow/Components/Shared/IdeaCard.razor`

```razor
L0016: data-testid="idea-card-@Idea.Id"
L0020: <h5 class="card-title mb-0" data-testid="idea-card-title-@Idea.Id">@Idea.Title</h5>
L0024: aria-label="Open idea actions for @Idea.Title" data-testid="idea-card-menu-@Idea.Id">
L0031: <button type="button" class="dropdown-item" @onclick="() => OnEdit.InvokeAsync(Idea)" data-testid="idea-card-edit-@Idea.Id">Edit</button>
L0051: data-testid="idea-card-move-@(Idea.Id)-@target">@target</button>
L0063: <button type="button" class="dropdown-item" @onclick="() => OnPromote.InvokeAsync(Idea)" data-testid="idea-card-promote-@Idea.Id">Promote</button>
L0069: <button type="button" class="dropdown-item text-danger" @onclick="() => OnDelete.InvokeAsync(Idea)" data-testid="idea-card-delete-@Idea.Id">Delete</button>
L0086: <span class="badge @IdeaBadge.PriorityClass(priority)" data-testid="idea-card-priority-@Idea.Id">@priority</span>
L0096: data-testid="idea-card-plan-@Idea.Id"
L0124: <a class="d-inline-flex align-items-center mt-2 small" href="@($"/project/{projectId}")" data-testid="idea-card-project-link-@Idea.Id">
L0142: data-testid="idea-card-promote-btn-@Idea.Id">
```

### `src/KanbanFlow/Components/Shared/IdeaModal.razor`

```razor
L0010: <div class="modal fade @(IsOpen ? "show" : "")" tabindex="-1" data-testid="idea-modal"
L0017: <button type="button" class="btn-close" @onclick="Close" aria-label="Close idea dialog" data-testid="idea-close-button"></button>
L0032: data-dialog-autofocus="true" data-testid="idea-title-input"
L0037: <div class="text-danger small mt-1" data-testid="idea-title-validation">Title is required.</div>
L0045: data-testid="idea-body-input" @bind="body"></textarea>
L0053: data-testid="idea-plan-input" @bind="plan"></textarea>
L0061: data-testid="idea-tags-input" @bind="tags" />
L0067: <select id="idea-priority" class="form-select form-select-sm" data-testid="idea-priority-select" @bind="priorityRaw">
L0094: <select id="idea-status" class="form-select form-select-sm" data-testid="idea-status-select"
L0111: <div class="form-text small" data-testid="idea-status-promoted-hint">
L0120: <button type="button" class="btn btn-secondary btn-sm" @onclick="Close" data-testid="idea-cancel-button">Cancel</button>
L0122: data-testid="idea-save-button" disabled="@(isSaving || string.IsNullOrWhiteSpace(title))">
```

### `src/KanbanFlow/Components/Shared/KanbanBoard.razor`

```razor
L0036: <p id="kanban-board-guidance" class="small text-muted mb-0" data-testid="kanban-board-guidance">@Guidance</p>
L0041: data-testid="kanban-board"
```

### `src/KanbanFlow/Components/Shared/KanbanCard.razor`

```razor
L0001: <div class="card mb-2 board-card @(HasIncompleteBlockers ? "board-card-blocked" : null)" data-testid="board-card-@Task.Id" data-task-id="@Task.Id"
L0012: data-testid="board-card-@Task.Id-done-checkbox" />
L0019: data-testid="board-card-@Task.Id-title">@Task.Name</button>
L0025: data-testid="board-card-@Task.Id-blocked-badge">
```

### `src/KanbanFlow/Components/Shared/KanbanColumn.razor`

```razor
L0014: <section class="@ColumnCssClass" data-testid="board-column-@Status" aria-labelledby="@HeadingId">
L0052: data-testid="board-column-@(Status)-collapse-toggle">
L0078: <p class="small text-body-secondary mb-2" data-testid="board-column-@(Status)-inert-hint">@InertHint</p>
L0090: data-testid="@CardsId">
L0126: data-testid="empty-column-@Status">
```

### `src/KanbanFlow/Components/Shared/ProjectOutline.razor`

```razor
L0004: <div id="project-outline" class="project-outline border-end bg-light p-3" data-testid="project-outline">
L0012: data-testid="back-to-projects-button">
L0023: data-testid="edit-project-button">
L0034: data-testid="close-project-outline-button">
L0045: data-testid="add-project-item-button">
L0108: data-testid="outline-feature-@feature.Id" @onclick="() => OnOpenEditFeature.InvokeAsync(feature)">
L0116: aria-label="Toggle @feature.Name" data-testid="outline-feature-@feature.Id-toggle"
L0146: data-testid="outline-feature-@feature.Id-add-button">
L0170: <div class="feature-tasks" data-testid="outline-feature-@feature.Id-tasks" data-feature-id="@feature.Id">
L0179: data-testid="outline-task-@task.Id" data-task-id="@task.Id"
L0188: aria-label="Toggle subtasks for @task.Name" data-testid="outline-task-@task.Id-toggle"
L0207: data-testid="outline-task-@task.Id-done-checkbox" />
L0221: data-testid="outline-task-@task.Id-add-button">
L0255: data-testid="outline-subtask-@subtask.Id" @onclick="() => OnOpenEditTask.InvokeAsync(parentTask)">
L0277: data-testid="outline-subtask-@subtask.Id-done-checkbox" />
L0290: data-testid="outline-subtask-@subtask.Id-add-button">
```

### `src/KanbanFlow/Components/Shared/TaskDetailPanel.razor`

```razor
L0007: data-testid="task-detail-panel" @ref="panelRoot"
L0032: <button type="button" class="btn-close" @onclick="ClosePanel" aria-label="Close task detail panel" data-testid="task-detail-close-button"></button>
L0042: @onclick="ScrollToOutline" data-testid="task-detail-show-in-outline-button">
L0078: <section class="mb-3" data-testid="task-detail-blockers-section" aria-labelledby="task-detail-blockers-heading">
L0083: <span class="ms-2 badge @(HasIncompleteBlockers ? "text-bg-danger" : "text-bg-secondary") small" data-testid="task-detail-blockers-count">@Task.Dependencies.Count</span>
L0089: <div class="text-muted small border rounded p-2" data-testid="task-detail-blockers-empty-state">
L0095: <div class="list-group list-group-flush border rounded" data-testid="task-detail-blockers-list">
L0103: <div class="list-group-item py-2 @(blockerDone ? null : "task-blocker-incomplete")" data-testid="task-detail-blocker-@dependency.DependsOnTaskItemId">
L0106: <span class="@GetStatusBadgeClass(blockerStatus)" data-testid="task-detail-blocker-@dependency.DependsOnTaskItemId-status">@GetStatusDisplayName(blockerStatus)</span>
L0154: <section class="mb-3 pt-3 border-top" data-testid="task-acceptance-criteria-section" aria-labelledby="task-acceptance-criteria-heading">
L0163: <span class="ms-2 badge bg-secondary small" data-testid="task-acceptance-criteria-count">@completedCriteriaCount/@totalCriteriaCount</span>
L0169: <div class="text-muted small border rounded p-2 mb-2" data-testid="task-acceptance-criteria-empty-state">
L0175: <div class="list-group list-group-flush mb-2" data-testid="task-acceptance-criteria-list">
L0178: <div class="list-group-item px-0 py-1 border-0 d-flex align-items-start gap-2" data-testid="@($"task-acceptance-criterion-{criterion.Id}")">
L0181: data-testid="@($"task-acceptance-criterion-toggle-{criterion.Id}")"
L0186: data-testid="@($"task-acceptance-criterion-delete-{criterion.Id}")"
L0200: data-testid="task-acceptance-criterion-input"
L0205: aria-label="Add acceptance criterion to @Task.Name" data-testid="task-acceptance-criterion-add-button"
L0255: data-testid="subtask-error" role="alert">
L0269: aria-label="Add subtask to @Task.Name" data-testid="task-detail-add-subtask-button"
L0291: <section class="mb-4 pt-3 border-top" data-testid="task-comments-section" aria-labelledby="task-comments-heading">
L0298: <span class="ms-2 badge bg-secondary small" data-testid="task-comments-count"
L0305: <div class="d-flex align-items-center small text-muted" data-testid="task-comments-loading">
L0316: <div class="alert alert-danger py-2 small" data-testid="task-comments-error">We couldn't load comments right now. Please try reopening the task.</div>
L0320: <div class="text-muted small border rounded p-2 mb-2" data-testid="task-comments-empty-state">
L0328: <div class="alert alert-warning py-2 mb-2" data-testid="latest-handoff-comment">
L0330: <span class="badge text-bg-warning" data-testid="latest-handoff-comment-badge">Latest Handoff</span>
L0334: <div data-testid="latest-handoff-comment-text">
L0354: data-testid="task-comment-input"
L0358: <select class="form-select form-select-sm w-auto" aria-label="Comment type" data-testid="task-comment-type-select" @bind="newCommentType">
L0364: <button class="btn btn-sm btn-primary" data-testid="task-comment-add-button"
L0399: <button class="btn btn-danger btn-sm" @onclick="ConfirmDeleteAsync" data-testid="task-detail-confirm-delete-button">Yes, delete</button>
L0400: <button class="btn btn-outline-secondary btn-sm" @onclick="() => confirmingDelete = false" data-testid="task-detail-cancel-delete-button">Cancel</button>
L0405: <button class="btn btn-outline-danger btn-sm" @onclick="() => confirmingDelete = true" data-testid="task-detail-delete-button">
```
