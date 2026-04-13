# KanbanFlow v2 — Requirements Document

> **Version:** 1.0 | **Date:** 2026-04-12 | **Status:** Draft
>
> A simple .NET 10 Blazor Server app with C# and SQLite. AI agents use a REST API to manage project work. The UI shows a Project outline on the left and a Kanban board on the right, with cards auto-placed by task status.

---

## Tech Stack

| Layer | Choice | Rationale |
|---|---|---|
| Framework | .NET 10, Blazor Server | C# full-stack, no JS build step |
| Database | SQLite (EF Core) | Zero config, file-based, no Docker |
| CSS | Bootstrap 5.3 | Standard, well-documented, no build step |
| Icons | Bootstrap Icons or Font Awesome | Included via CDN |
| API | Minimal APIs | Lightweight, fast to build |
| DnD | SortableJS via JS interop | 8KB, proven in Ticky v1 |
| Markdown | Markdig | .NET markdown rendering |
| Auth | None (auto-login) | Single-user local tool |

---

## Data Model

```
Project (1)
  └── Feature (many)
        ├── Feature (many, recursive — sub-features via ParentFeatureId)
        └── Task (many)
              └── Subtask (many)
                    └── Subtask (many, recursive — sub-subtasks via ParentSubtaskId)

Board = virtual view of Tasks grouped by Status column
Card = visual representation of a Task on the Board
```

**Key relationship:** A Card on the Kanban board is NOT a separate entity — it IS the Task, rendered visually. Moving a card changes the Task's `Status` field. Clicking a card opens the detail slide-out panel (R-11).

---

## P0 — Must Have

### R-01: Project CRUD

**Description:** Users can create, view, edit, and delete Projects. A Project is the top-level container for all work.

**Entity fields:**
- `Id` (int, PK, auto-increment)
- `Name` (string, required, max 200 chars)
- `Description` (string, optional, markdown — stored as-is, rendered in detail panel (R-11) only; board cards and left nav mini-cards show plain truncated text)
- `Goals` (string, optional, markdown — stored as-is, rendered in detail panel (R-11) only)
- `CreatedAt` (DateTime)
- `UpdatedAt` (DateTime)

**UI:**
- Home page lists all Projects as cards
- "New Project" button opens a form (name, description, goals)
- Click a project card to open it (shows left nav + board)
- Edit/delete via context menu on project card

**API:**
- `GET /api/projects` — list all
- `GET /api/projects/{id}` — get one with features + task counts
- `POST /api/projects` — create (body: name, description, goals)
- `PUT /api/projects/{id}` — update
- `DELETE /api/projects/{id}` — delete (cascades features + tasks)

**Acceptance criteria:**
- [ ] Creating a project with a name succeeds; empty name returns 422
- [ ] Deleting a project removes all its features and tasks
- [ ] Project list shows task count per project

---

### R-02: Feature CRUD

**Description:** Features group related Tasks within a Project. They represent major work areas (e.g., "User Authentication", "Dashboard", "API Layer").

**Entity fields:**
- `Id` (int, PK)
- `ProjectId` (int, FK → Project, required)
- `Name` (string, required, max 200 chars)
- `Description` (string, optional)
- `SortOrder` (int — for ordering in the left nav)
- `CreatedAt` (DateTime)
- `UpdatedAt` (DateTime)

**UI:**
- Shown in the left nav under the Project name
- Expandable/collapsible — click to show/hide child Tasks
- Drag-and-drop to reorder features
- Add/edit/delete via inline controls

**API:**
- `GET /api/projects/{projectId}/features` — list features for a project
- `GET /api/features/{id}` — get a single feature with task counts
- `POST /api/projects/{projectId}/features` — create
- `PUT /api/features/{id}` — update
- `DELETE /api/features/{id}` — delete (cascades tasks)

**Acceptance criteria:**
- [ ] Features appear under their parent Project in the left nav
- [ ] Deleting a feature shows a confirmation dialog with task count before cascade deletion
- [ ] Features can be reordered via drag-and-drop
- [ ] Progress indicator shows X/Y tasks complete per feature

---

### R-03: Task CRUD

**Description:** Tasks are the atomic work items. Each Task has a Status that determines which Kanban column it appears in. Tasks belong to a Feature.

**Entity fields:**
- `Id` (int, PK)
- `FeatureId` (int, FK → Feature, required)
- `Name` (string, required, max 500 chars)
- `Description` (string, optional, markdown — stored as-is, rendered in detail panel (R-11) only; board cards and left nav mini-cards show plain truncated text)
- `Status` (enum: Backlog, Todo, InProgress, Review, Done)
- `Priority` (enum: Low, Medium, High, Critical)
- `Assignee` (string, optional — free text, not linked to users)
- `SortOrder` (int — ordering within the same status column)
- `CreatedAt` (DateTime)
- `UpdatedAt` (DateTime)
- `CompletedAt` (DateTime, nullable — set when Status → Done)

**UI:**
- Shown as mini-cards in the left nav under their Feature
- Mini-card shows: name, priority badge, status dot
- Click to open detail panel (slide-out or modal)
- Drag mini-cards in the left nav to reorder within a feature

**API:**
- `GET /api/features/{featureId}/tasks` — list tasks for a feature
- `GET /api/projects/{projectId}/tasks` — list ALL tasks for a project (flat)
- `GET /api/tasks/{id}` — get one with subtasks
- `POST /api/features/{featureId}/tasks` — create
- `PUT /api/tasks/{id}` — update (including status change)
- `DELETE /api/tasks/{id}` — delete
- `PATCH /api/tasks/{id}/status` — change status only (used by board drag-and-drop)
- `PATCH /api/tasks/{id}/feature` — reassign task to a different Feature (body: `{ featureId: int }`)

Tasks can be reassigned to a different Feature via drag-and-drop in the left nav or via the Feature dropdown in the detail panel.

If Status is omitted in POST body, defaults to Backlog.

**Acceptance criteria:**
- [ ] Creating a task with Status=Backlog places it in the Backlog column
- [ ] Changing a task's status via API moves it on the board
- [ ] Tasks with Status=Done set CompletedAt automatically
- [ ] Priority badges render correctly (color-coded)
- [ ] Tasks appear under their Feature in the left nav

---

### R-04: Kanban Board (Auto-Generated from Status)

**Description:** The board is a virtual view — not a separate entity. Each column maps to a Task Status. Cards appear in the column matching their current status. There's no separate "Board" or "Column" entity to manage.

**Columns (fixed, derived from Status enum):**

| Column | Status Value | Color Hint |
|---|---|---|
| Backlog | `Backlog` | Gray |
| To Do | `Todo` | Blue |
| In Progress | `InProgress` | Yellow/Orange |
| Review | `Review` | Purple |
| Done | `Done` | Green |

**UI:**
- Board fills the main content area (right side of the layout)
- Each column shows a header with name + task count
- Cards show: task name, priority badge, feature name (small text), assignee
- Drag-and-drop a card between columns changes the Task's Status
- Click a card to open the detail slide-out panel (R-11)
- "New Task" button at the bottom of each column (creates task with that status)

A filter dropdown in the board header allows filtering by Feature ("All" by default). Selecting a Feature shows only that feature's tasks on the board.

Board auto-polls every 30 seconds for changes. A manual refresh button in the board header forces immediate sync. No SignalR.

**Acceptance criteria:**
- [ ] Board shows exactly 5 columns matching the Status enum
- [ ] Moving a card updates the Task.Status in the database
- [ ] Board reflects task changes made via the API (auto-polls every 30s)
- [ ] Manual refresh button forces immediate sync
- [ ] Empty columns show a helpful empty state message
- [ ] Card count badge on each column header
- [ ] Feature filter dropdown filters board cards by Feature

---

### R-05: Drag-and-Drop on Board

**Description:** Dragging a card from one column to another updates the Task's Status field in the database.

**Behavior:**
- Drag within a column → reorder (update SortOrder)
- Drag between columns → change Status + place at drop position
- Visual feedback during drag (card lifts, drop zone highlights)
- Optimistic update — UI moves immediately, API call in background
- On API failure — revert the card to its original position + show error toast

**Acceptance criteria:**
- [ ] Drag from "Todo" to "InProgress" sets Task.Status = InProgress
- [ ] SortOrder updates correctly when dropping between existing cards
- [ ] The left nav mini-card status dot updates to match
- [ ] API failure reverts the visual state

---

### R-06: Card ↔ Project Outline Link

**Description:** Clicking a card on the board opens the detail slide-out panel (R-11). The detail panel includes a "Show in outline" button that highlights the task in the left nav. Clicking a mini-card in the left nav also opens the detail panel and highlights the corresponding card on the board.

**Behavior:**
- Board card click → opens the detail slide-out panel (R-11)
- Detail panel "Show in outline" button → left nav scrolls to the Task, expands parent Feature, highlights the mini-card briefly (smooth scroll + 2-second highlight animation)
- Left nav mini-card click → opens the detail panel + board scrolls to the card's column, highlights the card briefly (smooth scroll + 2-second highlight animation)

**Acceptance criteria:**
- [ ] Clicking a board card opens the detail panel
- [ ] "Show in outline" button in detail panel highlights and scrolls to the task in the left nav
- [ ] Clicking a left nav mini-card opens the detail panel and highlights the card on the board
- [ ] Parent Feature expands automatically if collapsed

---

### R-07: Left Nav — Project Outline Panel

**Description:** A persistent left panel showing the Project hierarchy: Project → Features → Tasks as a tree with mini-cards.

**Layout:**
- Fixed width (300px default, resizable)
- Scrollable independently from the board
- Collapsible (toggle button to hide/show)

**Content:**
- Project name + description at the top
- Features as collapsible sections
- Tasks as mini-cards under each Feature
- Mini-card shows: name (truncated), priority badge (small colored dot), status indicator (colored dot matching column color)
- Drag-and-drop mini-cards to reorder within a Feature
- "Add Feature" button at the bottom
- "Add Task" button under each Feature

**Acceptance criteria:**
- [ ] Left nav shows correct hierarchy for the selected project
- [ ] Mini-cards show priority and status visually
- [ ] Features are collapsible
- [ ] Drag-and-drop reorders tasks within a feature
- [ ] Adding a task from the left nav creates it with Status=Backlog

---

### R-08: REST API for AI Agents

**Description:** A complete JSON API that AI agents (Claude Code, Cursor, etc.) can use to read and modify project data. No authentication required (local trust model).

**Design principles:**
- All endpoints return JSON
- Standard HTTP status codes (200, 201, 204, 400, 404, 422)
- Error responses include a `message` field
- Supports `X-Request-Id` header for correlation

**Endpoint summary:**

| Method | Path | Description |
|---|---|---|
| GET | `/api/projects` | List all projects |
| GET | `/api/projects/{id}` | Get project with feature/task counts |
| POST | `/api/projects` | Create project |
| PUT | `/api/projects/{id}` | Update project |
| DELETE | `/api/projects/{id}` | Delete project + cascade |
| GET | `/api/projects/{id}/features` | List features |
| GET | `/api/features/{id}` | Get single feature with task counts |
| POST | `/api/projects/{id}/features` | Create feature |
| PUT | `/api/features/{id}` | Update feature |
| DELETE | `/api/features/{id}` | Delete feature + cascade |
| GET | `/api/features/{id}/tasks` | List tasks in feature |
| GET | `/api/projects/{id}/tasks` | List ALL tasks in project |
| GET | `/api/projects/{id}/board` | Get board view (tasks grouped by status) |
| GET | `/api/tasks/{id}` | Get task with subtasks |
| POST | `/api/features/{id}/tasks` | Create task |
| PUT | `/api/tasks/{id}` | Update task |
| DELETE | `/api/tasks/{id}` | Delete task |
| PATCH | `/api/tasks/{id}/status` | Change status only |
| PATCH | `/api/tasks/{id}/feature` | Reassign task to different feature |
| GET | `/api/tasks/{id}/subtasks` | List subtasks for a task |
| POST | `/api/tasks/{id}/subtasks` | Create subtask |
| PUT | `/api/subtasks/{id}` | Update subtask |
| DELETE | `/api/subtasks/{id}` | Delete subtask |

**Pagination:** Not required. All list endpoints return full results.

**Concurrency:** Last-write-wins. No optimistic locking for P0.

**CORS:** Enabled for all origins (no auth, local tool).

### Validation Rules

- Name: required (1+ char trimmed)
- Description: max 10,000 chars
- Status and Priority must be valid enum values
- Unknown JSON fields are ignored

### Error Response Schema

All 400/422 responses use this shape:

```json
{
  "message": "string",
  "errors": {
    "fieldName": ["error message 1", "error message 2"]
  }
}
```

The `errors` field is optional and only present for validation errors.

**Acceptance criteria:**
- [ ] All endpoints return proper JSON with correct status codes
- [ ] `GET /api/projects/{id}/board` returns tasks grouped by status column
- [ ] Creating a task via API makes it appear on the board immediately (on refresh)
- [ ] Status change via `PATCH` is reflected on the board

---

### R-09: SQLite Database

**Description:** The app uses SQLite via EF Core. Database file is created automatically on first run. No setup, no Docker, no connection strings to configure.

**Details:**
- Database file: `kanbanflow.db` in the app directory
- EF Core with `Microsoft.EntityFrameworkCore.Sqlite`
- Code-first migrations (or `EnsureCreated` for simplicity)
- Default URL: `http://localhost:5000`
- Seed data:
  - Project: "Sample Project"
  - Feature 1: "User Management" (3 tasks)
    - "Login page" — Status: Done
    - "Registration form" — Status: InProgress
    - "Password reset" — Status: Todo
  - Feature 2: "Dashboard" (2 tasks)
    - "KPI cards" — Status: Backlog
    - "Activity feed" — Status: Backlog

**Acceptance criteria:**
- [ ] `dotnet run` creates the database automatically if it doesn't exist
- [ ] Sample project with tasks appears on first run
- [ ] No configuration needed — works out of the box

---

### R-10: Auto-Login

**Description:** No user accounts, no login page. The app is a single-user local tool. It starts and you're immediately using it.

**Details:**
- No ASP.NET Identity
- No authentication middleware
- No login/register pages
- App loads directly to the home page (project list)

**Acceptance criteria:**
- [ ] Opening the app URL shows the home page immediately
- [ ] No login redirect
- [ ] No user-related UI elements (no "Log out", no avatar)

---

## P1 — Should Have

### R-11: Task Detail Panel

**Description:** Clicking a task (mini-card or board card) opens a slide-out panel showing full task details with inline editing. Changes auto-save with 500ms debounce. No explicit Save button.

**Panel contents:**
- Task name (editable inline)
- Status dropdown
- Priority dropdown
- Feature dropdown (reassign task to a different Feature)
- Assignee text field
- Description (markdown editor or textarea — rendered as markdown via Markdig)
- Subtask list (R-13)
- Feature breadcrumb (Project > Feature > Task)
- Created/Updated timestamps
- "Show in outline" button (highlights the task in the left nav, see R-06)
- "Delete" button with confirmation

**Acceptance criteria:**
- [ ] Panel slides in from the right
- [ ] All fields are editable inline
- [ ] Changes auto-save with 500ms debounce (no explicit Save button)
- [ ] Close via X button or clicking outside

---

### R-12: Priority Badges

**Description:** Tasks show a colored priority badge on both board cards and left nav mini-cards.

| Priority | Color | Badge Text |
|---|---|---|
| Critical | Red | `Critical` |
| High | Orange | `High` |
| Medium | Yellow | `Medium` |
| Low | Gray | `Low` |

**Acceptance criteria:**
- [ ] Badge visible on board cards
- [ ] Small dot or badge on left nav mini-cards
- [ ] Correct colors for each priority level

---

### R-13: Subtasks

**Description:** Tasks can have a checkbox list of subtasks.

**Entity fields:**
- `Id` (int, PK)
- `TaskId` (int, FK → Task)
- `Text` (string, required)
- `Completed` (bool)
- `SortOrder` (int)
- `CreatedAt` (DateTime)

**UI:**
- Checkbox list in the task detail panel
- Add new subtask via text input + Enter
- Toggle completion via checkbox
- Delete via X button
- Drag to reorder
- Progress shown on board card: "3/5" checkmark count

**API:**
- `GET /api/tasks/{id}/subtasks`
- `POST /api/tasks/{id}/subtasks`
- `PUT /api/subtasks/{id}`
- `DELETE /api/subtasks/{id}`

**Acceptance criteria:**
- [ ] Subtasks render as checkboxes
- [ ] Checking a subtask saves immediately
- [ ] Board card shows subtask progress (e.g., "2/4")

---

### R-14: Feature Progress Indicator

**Description:** Each Feature in the left nav shows a progress bar or fraction showing how many tasks are Done.

**Display:** `3/7 tasks` with a small progress bar

**Acceptance criteria:**
- [ ] Progress updates when a task status changes to Done
- [ ] Shows 0/N for features with no completed tasks
- [ ] Shows checkmark or "Complete" when all tasks are Done

---

### R-15: Filter/Search

**Description:** A search bar at the top of the left nav that filters tasks by name. Optional filters for status and priority.

**Acceptance criteria:**
- [ ] Typing in search filters tasks in real-time (client-side)
- [ ] Matching tasks remain visible; non-matching hide
- [ ] Clear button resets the filter
- [ ] Search also filters board cards

---

### R-16: Board Column WIP Limits

**Description:** Columns can have optional WIP (Work In Progress) limits. When the limit is reached, the column header shows a warning and adding more cards is blocked.

WIP limits are stored in `appsettings.json` as a dictionary of status to max count. Default: unlimited for all columns.

**Acceptance criteria:**
- [ ] WIP limit configurable per column (default: unlimited)
- [ ] Column header shows "3/5" when limit is set
- [ ] Dragging a card into a full column shows a warning and reverts

---

## P2 — Nice to Have

### R-17: Multiple Board Views

**Description:** Different filtered views of the same tasks — e.g., "My Tasks", "Critical Only", "This Sprint".

---

### R-18: Comments/Notes on Tasks

**Description:** Free-text comments attached to tasks with timestamps.

---

### R-19: Activity Log

**Description:** Chronological log of all changes — task created, status changed, etc.

---

### R-20: Dark Mode

**Description:** Toggle between light and dark Bootstrap themes.

---

### R-21: Recursive Hierarchy (Nested Features & Subtasks)

**Description:** Features can contain child features (sub-features), and subtasks can contain child subtasks (sub-subtasks), to arbitrary depth. This enables breaking down complex work into an outline-style hierarchy displayed in the left nav and detail panel.

**Data model changes:**
- `Feature.ParentFeatureId` (int?, FK → Feature, nullable) — null means top-level
- `Feature.ChildFeatures` — navigation property to child features
- `Subtask.ParentSubtaskId` (int?, FK → Subtask, nullable) — null means top-level
- `Subtask.ChildSubtasks` — navigation property to child subtasks
- Both use `DeleteBehavior.Restrict` (must delete children first to prevent orphans)

**UI — Left Nav:**
- Features render recursively as an indented tree (16px indent per level)
- Each feature shows "+ Add Task" and "+ Sub-feature" buttons when expanded
- Task counts aggregate recursively (parent shows sum of own + all descendant tasks)
- Progress bar reflects aggregate done/total across the entire subtree

**UI — Detail Panel:**
- Subtasks render recursively as an indented checkbox tree (20px indent per level)
- Each subtask has a `+` button to add a child sub-item inline
- Subtask count badge (e.g., "0/4") includes all descendants

**API:**
- `POST /api/projects/{id}/features` — accepts optional `parentFeatureId` in body
- `PUT /api/features/{id}` — can update `parentFeatureId` (set to 0 or null to make top-level)
- `POST /api/tasks/{id}/subtasks` — accepts optional `parentSubtaskId` in body
- `PUT /api/subtasks/{id}` — can update `parentSubtaskId`
- All GET responses include `parentFeatureId` / `parentSubtaskId` fields

**Acceptance criteria:**
- [ ] Creating a feature with `parentFeatureId` nests it under the parent in the left nav
- [ ] Creating a subtask with `parentSubtaskId` nests it under the parent in the detail panel
- [ ] Parent feature task counts aggregate across all descendant features
- [ ] Deleting a parent feature with children returns an error (Restrict, not Cascade)
- [ ] Sub-features are visually indented in the left nav
- [ ] Sub-subtasks are visually indented in the detail panel
- [ ] Seed data includes at least one sub-feature and one nested subtask

---

## Out of Scope

- User accounts / multi-user authentication
- Real-time collaboration (SignalR/WebSocket)
- GitHub issue sync
- LLM/AI planning features (generate_task_outline, etc.)
- MCP server
- Docker deployment
- Mobile-specific layouts

---

## Definition of Done

1. `dotnet run` starts the app with zero config on .NET 10
2. SQLite database created automatically with seed data
3. Home page shows project list
4. Selecting a project shows left nav (Project → Features → Tasks) + Kanban board
5. Dragging a card on the board updates the task status
6. Clicking a board card opens the detail panel; "Show in outline" highlights the task in the left nav
7. All API endpoints return correct JSON
8. Bootstrap CSS renders cleanly — no custom CSS hacks
9. No login page, no accounts — immediate access
