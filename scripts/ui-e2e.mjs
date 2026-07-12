#!/usr/bin/env node
// Local interactive UI end-to-end tests for KanbanFlow v2 using Playwright (chromium).
//
// LOCAL ONLY — no CI. Self-contained: builds the app, boots an isolated instance
// (scratch port + throwaway SQLite DB), drives the real browser, then tears down.
//
//   npm run test:ui            # build + run
//   SKIP_BUILD=1 npm run test:ui
//
// The kanban board uses SortableJS drag-and-drop (initBoardDragDrop in
// wwwroot/js/kanbanflow.js): dragging a card between the `.board-column-cards`
// containers fires onEnd -> OnCardMoved(taskId, newStatus), which updates the
// task's status. These tests drive a real drag (mouse events) AND the done-checkbox
// path — both re-group the card into the destination column.

import { chromium } from "playwright";
import { spawn, execSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { mkdtempSync, rmSync } from "node:fs";
import path from "node:path";
import os from "node:os";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "..");
const csproj = path.join(repoRoot, "src", "KanbanFlow", "KanbanFlow.csproj");
const dll = path.join(repoRoot, "src", "KanbanFlow", "bin", "Debug", "net10.0", "KanbanFlow.dll");
const port = Number(process.env.UI_E2E_PORT || 5198);
const baseUrl = `http://127.0.0.1:${port}`;

const results = [];
let appProc = null;
let tempDbDir = null;

function log(msg) { process.stdout.write(msg + "\n"); }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(method, p, body) {
  const res = await fetch(`${baseUrl}${p}`, {
    method,
    headers: body ? { "content-type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  // Check status before parsing: error responses may be HTML/plaintext and would
  // otherwise make JSON.parse throw, masking the real status/body.
  if (!res.ok) throw new Error(`${method} ${p} -> ${res.status}: ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}

// Status persistence after a drag is async (SortableJS moves the DOM immediately,
// then OnCardMoved round-trips to the server), so poll the API rather than read once.
async function waitForApiStatus(taskId, expected, timeoutMs = 6000) {
  const deadline = Date.now() + timeoutMs;
  let last;
  do {
    last = (await api("GET", `/api/tasks/${taskId}`)).status;
    if (last === expected) return;
    await sleep(200);
  } while (Date.now() < deadline);
  throw new Error(`expected status ${expected}, got ${last}`);
}

// Status persistence after an idea drag, same reasoning as waitForApiStatus above.
async function waitForIdeaStatus(ideaId, expected, timeoutMs = 8000) {
  const deadline = Date.now() + timeoutMs;
  let last;
  do {
    last = (await api("GET", `/api/ideas/${ideaId}`)).status;
    if (last === expected) return;
    await sleep(200);
  } while (Date.now() < deadline);
  throw new Error(`expected idea status ${expected}, got ${last}`);
}

async function test(name, fn) {
  const started = Date.now();
  try {
    await fn();
    results.push({ name, ok: true, ms: Date.now() - started });
    log(`  PASS  ${name}  (${Date.now() - started}ms)`);
  } catch (err) {
    results.push({ name, ok: false, ms: Date.now() - started, error: err.message });
    log(`  FAIL  ${name}  -> ${err.message}`);
  }
}

function assert(cond, msg) { if (!cond) throw new Error(msg); }

// Blazor Server renders static HTML first, then connects an interactive circuit a
// moment later. A click that lands before the circuit is live is a no-op, so for
// idempotent "open" actions we retry the click until the expected element appears.
async function clickUntil(opener, expected, attempts = 6) {
  for (let i = 0; i < attempts; i++) {
    try { await opener(); } catch { /* target not ready yet */ }
    try { await expected.waitFor({ state: "visible", timeout: 2500 }); return; }
    catch { await sleep(500); }
  }
  await opener();
  await expected.waitFor({ state: "visible", timeout: 5000 });
}

// SortableJS responds to real mouse movement (not HTML5 DnD), so we press on the
// card and move toward the destination column in small steps before releasing.
async function dragCardTo(page, cardSelector, targetColumnSelector) {
  const card = page.locator(cardSelector);
  const target = page.locator(targetColumnSelector);
  await card.scrollIntoViewIfNeeded();
  const cb = await card.boundingBox();
  const tb = await target.boundingBox();
  if (!cb || !tb) throw new Error("card or target column not visible for drag");
  const sx = cb.x + cb.width / 2, sy = cb.y + cb.height / 2;
  const tx = tb.x + tb.width / 2, ty = tb.y + Math.min(tb.height / 2, 40);
  await page.mouse.move(sx, sy);
  await page.mouse.down();
  for (let i = 1; i <= 12; i++) {
    await page.mouse.move(sx + ((tx - sx) * i) / 12, sy + ((ty - sy) * i) / 12, { steps: 2 });
    await page.waitForTimeout(25);
  }
  await page.mouse.move(tx, ty);
  await page.waitForTimeout(60);
  await page.mouse.up();
}

// ── Board render-truth harness (#294) ────────────────────────────────────────────────
// Reads what a board is actually DRAWING: per column, the count badge (which the component
// computes from the MODEL) and the ids of the card roots rendered beside it (which the Blazor
// DIFF puts there). Only a card root carries data-task-id, on both boards.
const IDEA_COLUMNS = ["New", "Triaging", "Promoted", "Archived", "Rejected"];
const TASK_COLUMNS = ["Backlog", "Todo", "InProgress", "Review", "Done"];

async function readBoardDom(page, statuses) {
  return page.evaluate((sts) => {
    const board = {};
    for (const s of sts) {
      const col = document.querySelector(`[data-testid="board-column-${s}"]`);
      if (!col) continue;
      board[s] = {
        badge: parseInt(col.querySelector("h3 .badge")?.textContent ?? "", 10),
        // A collapsed terminal rail renders NO cards by design (KanbanColumn: `@if (!Collapsed)`),
        // so "badge === cards drawn" is only a claim about an expanded column.
        collapsed: col.classList.contains("kanban-column-collapsed"),
        cardIds: Array.from(
          document.querySelectorAll(`[data-testid="board-column-${s}-cards"] [data-task-id]`),
          (el) => Number(el.getAttribute("data-task-id")),
        ),
      };
    }
    return board;
  }, statuses);
}

// The two boards' canonical column orders, restated from the source so the DOM is checked
// against the SERVER's truth and not against itself:
//   /ideas        Priority desc, CreatedAt desc, Id desc  (Ideas.OrderIdeas — an Idea has no SortOrder)
//   /project/{id} SortOrder asc, Id asc                   (ProjectView.OrderTasksInColumn)
// Both end in a UNIQUE key, so both comparisons are TOTAL and this "truth" is a single order rather
// than a set of acceptable ones. Ideas.OrderIdeas grew its `Id desc` for exactly that reason (#300):
// CreatedAt defaults to UtcNow, two ideas made in the same tick at the same priority tie on both of
// the other keys, and a guard that compares the DOM against an ambiguous truth FLAPS instead of
// failing. Mirror of the C# — change one, change the other.
const PRIORITY_RANK = { Critical: 3, High: 2, Medium: 1, Low: 0 };
function groupBy(items, statusOf, compare) {
  const out = {};
  for (const item of items) (out[statusOf(item)] ??= []).push(item);
  for (const key of Object.keys(out)) out[key] = out[key].sort(compare).map((i) => i.id);
  return out;
}
const ideaTruth = (ideas) => groupBy(ideas, (i) => i.status, (a, b) =>
  (PRIORITY_RANK[b.priority] ?? -1) - (PRIORITY_RANK[a.priority] ?? -1) ||
  new Date(b.createdAt) - new Date(a.createdAt) ||
  b.id - a.id);
const taskTruth = (tasks) => groupBy(tasks, (t) => t.status, (a, b) =>
  a.sortOrder - b.sortOrder || a.id - b.id);

// The assertion #294 would have failed on. The badge is rendered from the model; the cards are
// put there by the render diff. A column whose badge contradicts the cards drawn beside it is a
// board disagreeing with ITSELF on screen — no server call, no error, nothing else can see it.
function assertBoardTruthful(dom, truth, ctx) {
  for (const [status, col] of Object.entries(dom)) {
    const expected = truth[status] ?? [];
    assert(col.badge === expected.length,
      `${ctx}: ${status} badge says ${col.badge}, the API holds ${expected.length} [${expected}]`);
    if (col.collapsed) {
      assert(col.cardIds.length === 0,
        `${ctx}: collapsed rail ${status} drew ${col.cardIds.length} card(s) [${col.cardIds}]`);
      continue;
    }
    assert(col.badge === col.cardIds.length,
      `${ctx}: ${status} counts ${col.badge} but DRAWS ${col.cardIds.length} card(s) [${col.cardIds}] ` +
      `— the column's own badge contradicts the cards rendered beside it`);
    assert(col.cardIds.join(",") === expected.join(","),
      `${ctx}: ${status} draws [${col.cardIds}] but the API holds [${expected}]`);
  }
}

// Which cards changed column between two DOM snapshots, as "id: From -> To".
function domColumnMoves(before, after, statuses) {
  const moves = [];
  for (const to of statuses) {
    for (const id of after[to]?.cardIds ?? []) {
      const from = statuses.find((s) => before[s]?.cardIds.includes(id));
      if (from && from !== to) moves.push(`${id}: ${from} -> ${to}`);
    }
  }
  return moves;
}

// Terminal rails (Archived/Rejected on /ideas) start collapsed and draw no cards, so open them
// before snapshotting — otherwise "badge === cards drawn" has nothing to say about them. The
// retry loop doubles as a wait for the interactive circuit: the toggle is an @onclick, so a
// click that lands on the SSR HTML is a no-op (same handoff clickUntil guards against).
async function expandColumn(page, status) {
  const col = page.locator(`[data-testid="board-column-${status}"]`);
  const isCollapsed = () => col.evaluate((e) => e.classList.contains("kanban-column-collapsed"));
  for (let i = 0; i < 8; i++) {
    if (!(await isCollapsed())) return;
    try { await page.locator(`[data-testid="board-column-${status}-collapse-toggle"]`).click({ timeout: 2000 }); }
    catch { /* circuit not live yet */ }
    await sleep(400);
  }
  assert(!(await isCollapsed()), `the ${status} rail never expanded`);
}

// A drag fired before SortableJS is bound is a silent no-op: the card follows the cursor (the
// browser's own text-drag) and nothing happens. Binding is deferred — OnAfterRenderAsync waits for
// the interactive circuit, then 100ms, then calls initBoardDragDrop — so a test that navigates and
// drags immediately is racing it. `Sortable.get(el)` is the framework's own "is this container
// armed?" question, so ask that rather than sleeping and hoping.
async function waitForSortableArmed(page, status) {
  await page.waitForFunction((s) => {
    const el = document.querySelector(`[data-testid="board-column-${s}-cards"]`);
    return !!el && !!window.Sortable && window.Sortable.get(el) != null;
  }, status, { timeout: 15000 });
}

async function startApp() {
  if (process.env.SKIP_BUILD !== "1") {
    log("Building app (worktree)...");
    execSync(`dotnet build "${csproj}" -c Debug --nologo`, { stdio: "inherit" });
  }
  tempDbDir = mkdtempSync(path.join(os.tmpdir(), "kf-uie2e-"));
  const dbPath = path.join(tempDbDir, "ui-e2e.db");
  log(`Starting app on ${baseUrl} (db ${dbPath})...`);

  // Fail fast on startup problems: capture stderr and surface spawn/early-exit
  // errors immediately, instead of falling through to the generic health timeout.
  let started = false;
  let startupError = null;
  let stderrTail = "";
  appProc = spawn("dotnet", [dll, "--urls", baseUrl], {
    env: { ...process.env, KANBANFLOW_DB_PATH: dbPath, ASPNETCORE_ENVIRONMENT: "Development" },
    stdio: ["ignore", "ignore", "pipe"],
  });
  appProc.on("error", (e) => { startupError = `failed to spawn dotnet: ${e.message}`; });
  appProc.stderr?.on("data", (d) => { stderrTail = (stderrTail + d.toString()).slice(-800); });
  appProc.on("exit", (code, signal) => {
    if (!started) {
      startupError = `app exited before becoming healthy (code=${code}, signal=${signal})` +
        (stderrTail ? `\n--- stderr ---\n${stderrTail}` : "");
    }
  });

  for (let i = 0; i < 40; i++) {
    if (startupError) throw new Error(startupError);
    try {
      const r = await fetch(`${baseUrl}/api/admin/health`);
      if (r.ok && (await r.json()).status === "ok") { started = true; log("App is up."); return; }
    } catch { /* not ready */ }
    await sleep(500);
  }
  throw new Error("App did not become healthy in time" + (stderrTail ? `\n--- stderr ---\n${stderrTail}` : ""));
}

function stopApp() {
  try {
    if (appProc?.pid) {
      if (process.platform === "win32") {
        try { execSync(`taskkill /pid ${appProc.pid} /T /F`, { stdio: "ignore" }); } catch { /* ignore */ }
      } else {
        appProc.kill("SIGKILL");
      }
    }
  } catch { /* ignore */ }
  try { if (tempDbDir) rmSync(tempDbDir, { recursive: true, force: true }); } catch { /* ignore */ }
}

async function main() {
  await startApp();
  const browser = await chromium.launch();
  const page = await browser.newPage();
  page.setDefaultTimeout(15000);

  try {
    const stamp = Date.now();

    // ── Scenario A: create a project through the Home "New Project" modal ──
    const modalProjectName = `E2E Modal ${stamp}`;
    await test("New Project modal creates a project", async () => {
      await page.goto(`${baseUrl}/`);
      await page.waitForLoadState("networkidle").catch(() => {});
      await clickUntil(
        () => page.getByRole("button", { name: "New Project" }).first().click(),
        page.locator("#project-name"),
      );
      await page.locator("#project-name").fill(modalProjectName);
      await page.getByRole("button", { name: "Save" }).click();
      // card with the new name appears in the list
      await page.getByText(modalProjectName, { exact: true }).waitFor({ state: "visible" });
    });

    // ── Setup for board scenarios: project + feature via API (a board task needs a feature) ──
    const proj = await api("POST", "/api/projects", { name: `E2E Board ${stamp}` });
    const feature = await api("POST", `/api/projects/${proj.id}/features`, { name: "E2E Feature" });

    // ── Scenario B: project board renders all five status columns ──
    await test("Project board renders all 5 status columns", async () => {
      await page.goto(`${baseUrl}/project/${proj.id}`);
      for (const status of ["Backlog", "Todo", "InProgress", "Review", "Done"]) {
        await page.locator(`[data-testid="board-column-${status}"]`).waitFor({ state: "visible" });
      }
    });

    // ── Scenario C: add a task to the Todo column via the Add-item modal ──
    const taskName = `E2E Task ${stamp}`;
    await test("Add-task modal creates a card in the Todo column", async () => {
      await clickUntil(
        () => page.locator('[data-testid="add-task-Todo-button"]').click(),
        page.locator("#add-item-name"),
      );
      await page.locator("#add-item-name").fill(taskName);
      await page.locator('[data-testid="add-item-create-button"]').click();
      // the card shows up in the Todo column's card container
      await page.locator(`[data-testid="board-column-Todo-cards"]`).getByText(taskName, { exact: true })
        .waitFor({ state: "visible" });
    });

    // resolve the new task id via the API so we can target its card precisely
    const tasks = await api("GET", `/api/features/${feature.id}/tasks`);
    const created = tasks.find((t) => t.name === taskName);
    assert(created, "created task not found via API");

    // ── Scenario D: drag a card between columns (SortableJS) updates its status ──
    await test("Dragging a card from Todo to In Progress updates its status", async () => {
      await page.locator(`[data-testid="board-column-Todo-cards"] [data-testid="board-card-${created.id}"]`)
        .waitFor({ state: "visible" });
      await dragCardTo(page,
        `[data-testid="board-card-${created.id}"]`,
        `[data-testid="board-column-InProgress-cards"]`);
      // card re-groups under In Progress, and the API reflects the new status
      await page.locator(`[data-testid="board-column-InProgress-cards"] [data-testid="board-card-${created.id}"]`)
        .waitFor({ state: "visible" });
      await waitForApiStatus(created.id, "InProgress");
    });

    // ── Scenario E: toggling the done checkbox auto-places the card into Done ──
    await test("Toggling done moves the card to the Done column", async () => {
      await page.locator(`[data-testid="board-card-${created.id}-done-checkbox"]`).click();
      await page.locator(`[data-testid="board-column-Done-cards"] [data-testid="board-card-${created.id}"]`)
        .waitFor({ state: "visible" });
      await waitForApiStatus(created.id, "Done");
    });

    // ── Regression guard: cards inside a column render in SortOrder sequence ──
    // KanbanColumn.razor orders its cards `OrderBy(t => t.SortOrder).ThenBy(t => t.Id)`, and that
    // sort lives ONLY in the Razor: ProjectView builds `allTasks` from an unordered
    // `Features.SelectMany(f => f.Tasks)` (no OrderBy on the Include), so nothing downstream
    // re-imposes it. Every other board scenario above puts a single card in a column, where a
    // scrambled sort is invisible. Seed THREE cards whose SortOrder order matches neither their
    // Id order nor their creation order, then assert the DOM *sequence* (not membership), so
    // dropping or reversing that OrderBy fails loudly instead of silently scrambling every board.
    const orderProj = await api("POST", "/api/projects", { name: `E2E Order ${stamp}` });
    const orderFeature = await api("POST", `/api/projects/${orderProj.id}/features`, { name: "E2E Order Feature" });

    // One feature, so SortOrder (which is per-feature) is unambiguous within the column.
    // Created Third -> First -> Second on purpose: Ids ascend with creation, SortOrder does not.
    const orderCards = [];
    for (const [label, sortOrder] of [["Third", 30], ["First", 10], ["Second", 20]]) {
      orderCards.push(await api("POST", `/api/features/${orderFeature.id}/tasks`, {
        name: `E2E Order ${label} ${stamp}`,
        status: "Todo",
        sortOrder,
      }));
    }
    const [third, first, second] = orderCards;
    const expectedCardIds = [first.id, second.id, third.id]; // SortOrder 10, 20, 30

    await test("Todo column renders its cards in SortOrder sequence", async () => {
      // Guard the seed itself: if these three sequences ever coincide the test proves nothing.
      // Ids ascend in creation order (third, first, second), so the four candidate orders are
      // all distinct — SortOrder asc [first,second,third] (correct), Id asc / creation order
      // [third,first,second] (sort dropped), SortOrder desc [third,second,first] (sort reversed).
      assert(
        third.id < first.id && first.id < second.id,
        `seed is not discriminating: ids ${third.id}, ${first.id}, ${second.id} must ascend in creation order`,
      );
      await page.goto(`${baseUrl}/project/${orderProj.id}`);
      // $$eval does not auto-wait, so wait for every card to render before reading the sequence.
      for (const id of expectedCardIds) {
        await page.locator(`[data-testid="board-column-Todo-cards"] [data-testid="board-card-${id}"]`)
          .waitFor({ state: "visible" });
      }
      // Only the card ROOT carries data-task-id — its checkbox and title children reuse the
      // `board-card-<id>-*` testid prefix, so `[data-task-id]` selects exactly one node per card.
      const domIds = await page.$$eval(
        '[data-testid="board-column-Todo-cards"] [data-task-id]',
        (els) => els.map((e) => Number(e.getAttribute("data-task-id"))),
      );
      assert(
        domIds.join(",") === expectedCardIds.join(","),
        `cards must render in SortOrder order — expected [${expectedCardIds}], got [${domIds}]`,
      );
    });

    // ── Regression guard: columns render left-to-right in WorkStatus order ──
    // KanbanBoard.razor lays the columns out by iterating `Enum.GetValues<WorkStatus>()`. The
    // per-column `waitFor(visible)` loops above are order-independent and pass for ANY permutation,
    // so assert the DOM sequence here — a reordered board is otherwise invisible to this suite.
    await test("Board renders its columns in WorkStatus order, left to right", async () => {
      const expectedColumns = ["Backlog", "Todo", "InProgress", "Review", "Done"]; // WorkStatus enum order
      await page.goto(`${baseUrl}/project/${orderProj.id}`);
      for (const status of expectedColumns) {
        await page.locator(`[data-testid="board-column-${status}"]`).waitFor({ state: "visible" });
      }
      // Direct children only: each column's card container reuses the `board-column-<status>-`
      // testid prefix but sits a level deeper, so `>` keeps this to the five column roots.
      const domColumns = await page.$$eval(
        '[data-testid="kanban-board"] > [data-testid^="board-column-"]',
        (els) => els.map((e) => e.getAttribute("data-testid").replace("board-column-", "")),
      );
      assert(
        domColumns.join(",") === expectedColumns.join(","),
        `columns must render in WorkStatus order — expected [${expectedColumns}], got [${domColumns}]`,
      );
    });

    // ── Regression guard (#294): a drag must not corrupt the RENDER ───────────────────────────
    // SortableJS physically relocates the dragged card's <div> BEFORE Blazor is told anything.
    // Blazor's renderer keeps its own logical-children array per `.board-column-cards` container
    // and never re-reads the DOM, so after a drop its model and the real DOM disagree. Worse,
    // KanbanColumn draws its cards as `@foreach (var item in Items) { @CardTemplate(item) }`,
    // which compiles to `AddContent(seq, CardTemplate(item))` — one REGION per card, all sharing
    // ONE sequence number — so the diff pairs cards POSITIONALLY. It therefore hands card N+1's
    // parameters to the component instance that owns card N's DOM node… which SortableJS has
    // already moved into a different column. The board then draws cards in columns they are not
    // in, while the database is perfectly correct and a refresh heals it — so every server-side
    // test, and every drag test that only checks "the card I dragged arrived", stays green.
    //
    // The seed is what makes this catch it: the dragged card must NOT be the last card in its
    // column. Dragging the LAST card is self-healing (the diff's trailing removal happens to
    // delete exactly the node Sortable moved), which is why every drag scenario above — each of
    // which drags the only card in its column — has never seen this.
    //
    // Asserted: (a) exactly one card changes column in the DOM, (b) every column's count badge
    // equals the number of cards actually drawn beside it, (c) the DOM equals the API.
    const dragProj = await api("POST", "/api/projects", { name: `E2E Drag ${stamp}` });
    const dragFeature = await api("POST", `/api/projects/${dragProj.id}/features`, { name: "E2E Drag Feature" });
    await test("Project board: dragging ONE card moves exactly one card, and every badge matches its cards", async () => {
      const mkTask = (label, sortOrder, status) => api("POST", `/api/features/${dragFeature.id}/tasks`,
        { name: `E2E Drag ${label} ${stamp}`, status, sortOrder });
      const t1 = await mkTask("Todo A", 10, "Todo");
      const t2 = await mkTask("Todo B", 20, "Todo"); // the one we drag — deliberately NOT last
      const t3 = await mkTask("Todo C", 30, "Todo");
      await mkTask("Parked", 10, "InProgress");      // a bystander the drag must not disturb

      await page.goto(`${baseUrl}/project/${dragProj.id}`);
      for (const t of [t1, t2, t3]) {
        await page.locator(`[data-testid="board-column-Todo-cards"] [data-testid="board-card-${t.id}"]`)
          .waitFor({ state: "visible" });
      }

      await waitForSortableArmed(page, "Todo");
      await waitForSortableArmed(page, "InProgress");

      const before = await readBoardDom(page, TASK_COLUMNS);
      assert(before.Todo.cardIds.join(",") === [t1.id, t2.id, t3.id].join(","),
        `seed: Todo must draw [${[t1.id, t2.id, t3.id]}], got [${before.Todo.cardIds}]`);
      assert(before.Todo.cardIds.indexOf(t2.id) < before.Todo.cardIds.length - 1,
        "seed is not discriminating: the dragged card must not be the LAST card in its column");

      await dragCardTo(page, `[data-testid="board-card-${t2.id}"]`,
        `[data-testid="board-column-InProgress-cards"]`);
      await waitForApiStatus(t2.id, "InProgress");
      await page.waitForTimeout(600); // let the re-render land

      const after = await readBoardDom(page, TASK_COLUMNS);
      const truth = taskTruth(await api("GET", `/api/features/${dragFeature.id}/tasks`));
      assertBoardTruthful(after, truth, "after dragging one task Todo -> InProgress");

      const moves = domColumnMoves(before, after, TASK_COLUMNS);
      assert(moves.length === 1 && moves[0] === `${t2.id}: Todo -> InProgress`,
        `exactly ONE card may change column — the DOM moved [${moves.join("; ") || "nothing"}]`);
    });

    // ── Scenario F: Ideas inbox — create / edit / filter / promote / delete ──
    // The harness boots a fresh scratch DB each run (mkdtempSync), so the ideas table
    // starts empty: assert the empty state first, then drive the full lifecycle through
    // the real Blazor UI (IdeaModal + IdeaCard + the confirm modals).
    await test("Ideas inbox shows the empty state on a fresh DB", async () => {
      await page.goto(`${baseUrl}/ideas`);
      await page.waitForLoadState("networkidle").catch(() => {});
      await page.locator('[data-testid="ideas-empty-state"]').waitFor({ state: "visible" });
    });

    const ideaTitle = `E2E Idea ${stamp}`;
    const ideaTag = `e2etag${stamp}`;
    await test("New-idea modal creates a card", async () => {
      await clickUntil(
        () => page.locator('[data-testid="new-idea-button"]').click(),
        page.locator("#idea-title"),
      );
      await page.locator("#idea-title").fill(ideaTitle);
      await page.locator("#idea-body").fill("captured via e2e");
      await page.locator("#idea-tags").fill(ideaTag);
      await page.locator('[data-testid="idea-priority-select"]').selectOption(String(2)); // High
      await page.locator('[data-testid="idea-save-button"]').click();
      // the new card appears in the grid with that title
      await page.getByText(ideaTitle, { exact: true }).waitFor({ state: "visible" });
    });

    // Resolve the created idea's id via the REST surface so we can target its card precisely.
    const ideasAfterCreate = await api("GET", `/api/ideas?q=${encodeURIComponent(ideaTitle)}&limit=500`);
    const createdIdea = ideasAfterCreate.find((i) => i.title === ideaTitle);
    assert(createdIdea, "created idea not found via API");

    const ideaTitleEdited = `${ideaTitle} (edited)`;
    await test("Editing an idea updates its card title", async () => {
      await clickUntil(
        () => page.locator(`[data-testid="idea-card-menu-${createdIdea.id}"]`).click(),
        page.locator(`[data-testid="idea-card-edit-${createdIdea.id}"]`),
      );
      await page.locator(`[data-testid="idea-card-edit-${createdIdea.id}"]`).click();
      await page.locator("#idea-title").waitFor({ state: "visible" });
      await page.locator("#idea-title").fill(ideaTitleEdited);
      await page.locator('[data-testid="idea-status-select"]').selectOption(String(1)); // Triaging
      await page.locator('[data-testid="idea-save-button"]').click();
      await page.getByText(ideaTitleEdited, { exact: true }).waitFor({ state: "visible" });
    });

    await test("Tag filter narrows the list to the matching idea", async () => {
      // A second idea with a different tag must be filtered OUT by our unique tag.
      const other = await api("POST", "/api/ideas", { title: `E2E Other ${stamp}`, tags: "unrelated" });
      await page.goto(`${baseUrl}/ideas`);
      const ours = page.locator(`[data-testid="idea-card-${createdIdea.id}"]`);
      const unrelated = page.locator(`[data-testid="idea-card-${other.id}"]`);
      const tagInput = page.locator('[data-testid="idea-tag-filter"]');
      await ours.waitFor({ state: "visible" });
      await unrelated.waitFor({ state: "visible" });
      // The tag filter runs on the interactive Blazor circuit, which connects a moment
      // after the SSR HTML loads; a fill that lands before it is live is a no-op (the same
      // SSR->interactive handoff clickUntil guards against). Re-apply until it takes effect.
      for (let i = 0; i < 6; i++) {
        await tagInput.fill("");
        await tagInput.fill(ideaTag);
        try { await unrelated.waitFor({ state: "detached", timeout: 2500 }); break; }
        catch { await sleep(500); }
      }
      // our idea remains; the unrelated one is filtered away
      await ours.waitFor({ state: "visible" });
      await unrelated.waitFor({ state: "detached" });
      await api("DELETE", `/api/ideas/${other.id}`).catch(() => {});
      // clear the filter for subsequent steps
      await tagInput.fill("");
      await ours.waitFor({ state: "visible" });
    });

    await test("Promoting an idea flips it to Promoted with a project link", async () => {
      await clickUntil(
        () => page.locator(`[data-testid="idea-card-menu-${createdIdea.id}"]`).click(),
        page.locator(`[data-testid="idea-card-promote-${createdIdea.id}"]`),
      );
      await page.locator(`[data-testid="idea-card-promote-${createdIdea.id}"]`).click();
      await page.locator('[data-testid="idea-promote-confirm"]').waitFor({ state: "visible" });
      await page.locator('[data-testid="idea-promote-confirm"]').click();
      // success banner with an "Open new project" link appears, and the card has MOVED to the
      // Promoted column. #280 turned /ideas into a status board: the column IS the status, so the
      // card no longer carries a status badge (two grey pills meaning different things). Asserting
      // the column instead of a label is strictly stronger — it tests the promote pipeline rather
      // than a string.
      await page.locator('[data-testid="idea-promote-success-link"]').waitFor({ state: "visible" });
      await page.locator(
        `[data-testid="board-column-Promoted-cards"] [data-testid="idea-card-${createdIdea.id}"]`,
      ).waitFor({ state: "visible" });
    });

    // ── Regression guard: an ALREADY-PROMOTED idea must still be editable ──
    // #283 has two halves. The service half (IdeaService.UpdateAsync rejects Status=Promoted) is
    // covered by xUnit. The UI half is IdeaModal's `SelectedStatus`, which sends null instead of
    // Promoted on save — and it is the ONLY thing stopping the new guard from making every promoted
    // idea permanently uneditable: the modal used to send `Status: (IdeaStatus)status`
    // unconditionally, so editing a promoted idea's TITLE would now throw ValidationException and
    // block the whole save. Nothing tested that half (there is no bUnit, and the edit above happens
    // BEFORE the promote), so `SelectedStatus` could be reverted with the suite still fully green.
    // Sequenced here, between promote and delete, and deliberately BEFORE the project-link scenario
    // — that one deletes the promoted project, which would leave PromotedProjectId dangling and make
    // the "promotion undisturbed" assertion below meaningless.
    const ideaTitlePromotedEdit = `${ideaTitle} (edited after promote)`;
    await test("An already-promoted idea can still be edited, promotion intact", async () => {
      const before = await api("GET", `/api/ideas/${createdIdea.id}`);
      assert(before.status === "Promoted", `precondition: expected Promoted, got ${before.status}`);
      assert(before.promotedProjectId > 0, "precondition: promoted idea has no promotedProjectId");

      // IdeaCard offers Edit for a promoted idea (only Promote is hidden), so re-open the modal on it.
      await clickUntil(
        () => page.locator(`[data-testid="idea-card-menu-${createdIdea.id}"]`).click(),
        page.locator(`[data-testid="idea-card-edit-${createdIdea.id}"]`),
      );
      await page.locator(`[data-testid="idea-card-edit-${createdIdea.id}"]`).click();
      await page.locator("#idea-title").waitFor({ state: "visible" });
      // Change a field that has NOTHING to do with status — the status select is left untouched,
      // still showing (disabled) Promoted, exactly as a real user would leave it.
      await page.locator("#idea-title").fill(ideaTitlePromotedEdit);
      await page.locator('[data-testid="idea-save-button"]').click();

      // SaveIdea() awaits UpdateAsync BEFORE OnClose, so the modal closes only if the save actually
      // succeeded. If the modal sends Promoted back, UpdateAsync (#283) throws ValidationException,
      // SaveIdea catches it and the modal stays open with the message inline — quote it in the failure.
      try {
        await page.locator('[data-testid="idea-modal"]').waitFor({ state: "hidden", timeout: 8000 });
      } catch {
        const inline = await page.locator('[data-testid="idea-modal"] [role="alert"]').first()
          .textContent().catch(() => null);
        throw new Error("saving a promoted idea was rejected — the modal stayed open: " +
          `${(inline ?? "(no inline error rendered)").trim()}`);
      }
      await page.getByText(ideaTitlePromotedEdit, { exact: true }).waitFor({ state: "visible" });

      // Trust the DB, not the DOM: the edit must have persisted AND left the promotion untouched.
      const after = await api("GET", `/api/ideas/${createdIdea.id}`);
      assert(after.title === ideaTitlePromotedEdit,
        `title did not persist — expected "${ideaTitlePromotedEdit}", got "${after.title}"`);
      assert(after.status === "Promoted", `the edit disturbed the promotion — status is now ${after.status}`);
      assert(after.promotedProjectId === before.promotedProjectId,
        `the edit disturbed the promotion link — promotedProjectId ${before.promotedProjectId} -> ${after.promotedProjectId}`);
    });

    await test("The promoted idea's project link opens a real board", async () => {
      // confirm the promote actually created a project, and its board route renders
      const promoted = await api("GET", `/api/ideas/${createdIdea.id}`);
      assert(promoted.status === "Promoted", `expected Promoted, got ${promoted.status}`);
      assert(promoted.promotedProjectId > 0, "promoted idea has no project id");
      await page.goto(`${baseUrl}/project/${promoted.promotedProjectId}`);
      for (const status of ["Backlog", "Todo", "InProgress", "Review", "Done"]) {
        await page.locator(`[data-testid="board-column-${status}"]`).waitFor({ state: "visible" });
      }
      await api("DELETE", `/api/projects/${promoted.promotedProjectId}`).catch(() => {});
    });

    await test("Deleting an idea removes its card", async () => {
      await page.goto(`${baseUrl}/ideas`);
      await clickUntil(
        () => page.locator(`[data-testid="idea-card-menu-${createdIdea.id}"]`).click(),
        page.locator(`[data-testid="idea-card-delete-${createdIdea.id}"]`),
      );
      await page.locator(`[data-testid="idea-card-delete-${createdIdea.id}"]`).click();
      await page.locator('[data-testid="idea-delete-confirm"]').waitFor({ state: "visible" });
      await page.locator('[data-testid="idea-delete-confirm"]').click();
      await page.locator(`[data-testid="idea-card-${createdIdea.id}"]`).waitFor({ state: "detached" });
    });

    // ── Regression guard (#294), ideas board ──────────────────────────────────────────────────
    // The same guard as the project board's above, on the board where the UX review actually saw
    // it: drag ONE idea into Archived and two cards move. See that comment for the mechanism.
    // The ideas board is the harder case — a moved idea can land ANYWHERE in the destination's
    // canonical order (`Priority desc, CreatedAt desc`), not just at the end — so the seed makes
    // the dragged card sort FIRST in Archived, ahead of the bystander already sitting there.
    await test("Ideas board: dragging ONE card moves exactly one card, and every badge matches its cards", async () => {
      // Clean slate: the seed IS the board, so every assertion below is exact.
      for (const i of await api("GET", "/api/ideas?limit=500")) {
        await api("DELETE", `/api/ideas/${i.id}`).catch(() => {});
      }
      // Distinct priorities all round: OrderIdeas is `Priority desc, CreatedAt desc`, so priority
      // alone fixes every column's order and the assertions never race the clock.
      const mkIdea = (label, priority) => api("POST", "/api/ideas", { title: `E2E Drag ${label} ${stamp}`, priority });
      const top = await mkIdea("Top", "Critical");
      const mid = await mkIdea("Mid", "High");    // the one we drag — deliberately NOT last
      const bottom = await mkIdea("Bottom", "Medium");
      const parked = await mkIdea("Parked", "Low"); // a bystander in the destination column
      await api("PATCH", `/api/ideas/${parked.id}`, { status: "Archived" });

      await page.goto(`${baseUrl}/ideas`);
      await page.locator(`[data-testid="idea-card-${top.id}"]`).waitFor({ state: "visible" });
      // Archived/Rejected are terminal rails and start collapsed, drawing no cards at all. Open
      // them so "badge === cards drawn" is a claim about EVERY column on the board.
      await expandColumn(page, "Archived");
      await expandColumn(page, "Rejected");
      await waitForSortableArmed(page, "New");
      await waitForSortableArmed(page, "Archived");

      const before = await readBoardDom(page, IDEA_COLUMNS);
      assert(before.New.cardIds.join(",") === [top.id, mid.id, bottom.id].join(","),
        `seed: New must draw [${[top.id, mid.id, bottom.id]}], got [${before.New.cardIds}]`);
      assert(before.New.cardIds.indexOf(mid.id) < before.New.cardIds.length - 1,
        "seed is not discriminating: the dragged card must not be the LAST card in its column");

      await dragCardTo(page, `[data-testid="idea-card-${mid.id}"]`,
        `[data-testid="board-column-Archived-cards"]`);
      await waitForIdeaStatus(mid.id, "Archived");
      await page.waitForTimeout(600); // let the re-render land

      const after = await readBoardDom(page, IDEA_COLUMNS);
      const truth = ideaTruth(await api("GET", "/api/ideas?limit=500"));
      // The moved idea (High) must sort AHEAD of the bystander (Low) — i.e. NOT at the end.
      assert(truth.Archived.join(",") === [mid.id, parked.id].join(","),
        `precondition: Archived's canonical order must be [${[mid.id, parked.id]}], API says [${truth.Archived}]`);
      assertBoardTruthful(after, truth, "after dragging one idea New -> Archived");

      const moves = domColumnMoves(before, after, IDEA_COLUMNS);
      assert(moves.length === 1 && moves[0] === `${mid.id}: New -> Archived`,
        `exactly ONE card may change column — the DOM moved [${moves.join("; ") || "nothing"}]`);

      // ── The same bug, in-column. An Idea has NO SortOrder: a column's order is DERIVED
      // (`Priority desc, CreatedAt desc`), so dropping a card at a position the server cannot
      // represent must snap back. Ideas.ApplyStatusAsync says so in as many words — "the reload
      // snaps the card back to its canonical slot, so the board teaches its own rule instead of
      // quietly drifting" — and that claim is only TRUE if Blazor still owns the DOM it is
      // re-rendering. When SortableJS keeps the node it moved, the re-render is a no-op (same
      // items, same order => zero diff edits) and the drifted order simply STAYS on screen, a
      // lie the server never agreed to, until something forces a refresh.
      await dragCardTo(page, `[data-testid="idea-card-${bottom.id}"]`,
        `[data-testid="board-column-New-cards"]`);
      await page.waitForTimeout(900); // no status change to poll — this drop is a pure re-render

      const settled = await readBoardDom(page, IDEA_COLUMNS);
      assertBoardTruthful(settled, ideaTruth(await api("GET", "/api/ideas?limit=500")),
        "after dropping an idea out of canonical order inside its own column");
      assert(settled.New.cardIds.join(",") === [top.id, bottom.id].join(","),
        `an in-column drop must snap back to the canonical order [${[top.id, bottom.id]}] ` +
        `(Priority desc) — the board is still drawing [${settled.New.cardIds}]`);
    });

    // ── Regression guard (#300): a REFUSED move must not fail SILENTLY ────────────────────────
    // ApplyStatusAsync's error path ends in `await LoadIdeas()` — that reload IS the snap-back —
    // and LoadIdeas clears `loadError` on every successful load. So the move error it had just set
    // was wiped by the very reload that returned the card to its real column: the card jumped home
    // and the board said NOTHING about why. A silent snap-back is precisely the "the UI lies to
    // you" failure this board was rebuilt to kill, so it gets a test that can see it.
    //
    // The refusal is REAL, not injected. IdeaService has exactly ONE status rule — the Promoted
    // boundary may not be crossed (#283 in, #285 out) — and ApplyStatusAsync's own pre-check reads
    // the board's IN-MEMORY copy of the idea. Promote out-of-band (a second tab, the MCP server's
    // promote_idea, an agent on the API) and that copy is stale: the pre-check waves the drag
    // through, the service loads the idea fresh, and refuses. That is a race this app can genuinely
    // lose — three writers share one DB — and it is the only way a drag can be refused at all.
    await test("Ideas board: a move the service refuses says WHY, and does not take effect", async () => {
      for (const i of await api("GET", "/api/ideas?limit=500")) {
        await api("DELETE", `/api/ideas/${i.id}`).catch(() => {});
      }
      const stale = await api("POST", "/api/ideas", { title: `E2E Refused ${stamp}`, priority: "Critical" });

      await page.goto(`${baseUrl}/ideas`);
      await page.locator(`[data-testid="idea-card-${stale.id}"]`).waitFor({ state: "visible" });
      await waitForSortableArmed(page, "New");

      // Out-of-band promote. The open board never hears about it, and keeps drawing the card in New.
      const promoted = await api("POST", `/api/ideas/${stale.id}/promote`, { actor: "e2e" });
      assert(promoted.projectId > 0, `promote did not create a project: ${JSON.stringify(promoted)}`);

      // Drag the now-stale card New -> Triaging. The service refuses: it is really Promoted.
      await dragCardTo(page, `[data-testid="idea-card-${stale.id}"]`,
        `[data-testid="board-column-Triaging-cards"]`);

      // 1. The user must be TOLD. THIS is the assertion that is RED before the fix, and it is
      //    deliberately testid-AGNOSTIC: the question is "does the board say ANYTHING at all?",
      //    and pinning it to one testid would let the answer come back "no, but the right box was
      //    missing" — a weaker claim than the bug deserves. Pre-fix NO danger alert paints at all:
      //    ApplyStatusAsync sets `loadError`, then its own snap-back reload nulls it on the way out.
      const anyError = page.locator('.alert-danger[role="alert"]');
      try {
        await anyError.first().waitFor({ state: "visible", timeout: 8000 });
      } catch {
        throw new Error("a refused move told the user NOTHING — the card snapped back and no " +
          "page-level error ever appeared (no visible .alert-danger[role=alert] on /ideas)");
      }
      const msg = ((await anyError.first().textContent()) ?? "").replace(/\s+/g, " ").trim();

      // 2. ...and told WHY, in the service's own actionable words — never a bare "move failed".
      assert(/promot/i.test(msg) && /Triaging/.test(msg),
        `the refusal must name the reason AND the target it refused — the banner reads "${msg}"`);

      // 3. It must ride the MOVE channel, not the load channel. A move error parked in `loadError`
      //    IS the bug: LoadIdeas owns that field and clears it (`loadError = null`) on every
      //    successful load — including the snap-back reload the error path itself awaits.
      await page.locator('[data-testid="idea-move-error"]').waitFor({ state: "visible", timeout: 3000 });

      // 4. The gesture must not have taken effect. The card belongs in Promoted — where it REALLY
      //    is — and never in Triaging, where the user dropped it. (This half already passed: the
      //    reload snaps the card back. It is the EXPLANATION that was missing, which is exactly
      //    what makes the bug so easy to miss — the board looks right and lies by omission.)
      await page.locator(`[data-testid="board-column-Promoted-cards"] [data-task-id="${stale.id}"]`)
        .waitFor({ state: "visible", timeout: 8000 });
      const settled = await readBoardDom(page, IDEA_COLUMNS);
      assert(!settled.Triaging.cardIds.includes(stale.id),
        `the refused card is still sitting in Triaging [${settled.Triaging.cardIds}] — the drop was never undone`);
      assertBoardTruthful(settled, ideaTruth(await api("GET", "/api/ideas?limit=500")), "after a refused move");

      await api("DELETE", `/api/projects/${promoted.projectId}`).catch(() => {});
      await api("DELETE", `/api/ideas/${stale.id}`).catch(() => {});
    });

    // ── Scenario E: Settings -> Run all shows an all-passed result table ──
    await test("Settings 'Run all' reports an all-passed result", async () => {
      await page.goto(`${baseUrl}/settings`);
      await page.getByRole("button", { name: "Run all" }).click();
      await page.locator('[data-testid="settings-test-results"]').waitFor({ state: "visible" });
      // green summary badge present, and no failing rows
      await page.locator(".badge.text-bg-success").first().waitFor({ state: "visible" });
      // Count rows inside the results table (not the header summary badge). Passed rows carry
      // no marker class; only failed rows get `table-danger`, so passed = total - failed.
      const resultsTable = page.locator('[data-testid="settings-test-results"]');
      const totalRows = await resultsTable.locator("tbody tr").count();
      // Scope failed rows to the SAME tbody as totalRows so passedRows can never go negative if a
      // non-body element ever carries table-danger (#168 review).
      const failedRows = await resultsTable.locator("tbody tr.table-danger").count();
      const passedRows = totalRows - failedRows;
      assert(failedRows === 0, `expected 0 failed rows in the UI, found ${failedRows}`);
      // Guard against a vacuous pass: a table that rendered zero rows also has zero failures.
      assert(passedRows > 0, `expected at least one passed row in the UI, found ${passedRows} of ${totalRows}`);
    });

    // ── Cleanup of API-created data (best effort) ──
    await api("DELETE", `/api/projects/${proj.id}`).catch(() => {});
    await api("DELETE", `/api/projects/${orderProj.id}`).catch(() => {});
    await api("DELETE", `/api/projects/${dragProj.id}`).catch(() => {});
    // delete the modal-created project too
    const all = await api("GET", "/api/projects");
    const modalProj = all.find((p) => p.name === modalProjectName);
    if (modalProj) await api("DELETE", `/api/projects/${modalProj.id}`).catch(() => {});
    // sweep any ideas this run created (in case a mid-flow step failed before the delete step)
    const leftoverIdeas = await api("GET", `/api/ideas?q=E2E&limit=500`).catch(() => []);
    for (const i of leftoverIdeas.filter((i) => i.title.includes(String(stamp)))) {
      await api("DELETE", `/api/ideas/${i.id}`).catch(() => {});
    }
  } finally {
    await browser.close();
  }
}

main()
  .then(() => {
    const failed = results.filter((r) => !r.ok);
    log(`\nUI e2e: ${results.length - failed.length}/${results.length} passed`);
    stopApp();
    process.exit(failed.length === 0 ? 0 : 1);
  })
  .catch((err) => {
    log(`\nUI e2e harness error: ${err.stack || err.message}`);
    stopApp();
    process.exit(1);
  });
