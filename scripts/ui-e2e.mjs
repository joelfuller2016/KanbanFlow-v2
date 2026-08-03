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
// containers fires onEnd -> OnCardMoved(taskId, newStatus, beforeTaskId), which updates the
// task's status and identity-anchored order. These tests drive a real drag (mouse events) AND
// the done-checkbox path — both re-group the card into the destination column.

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

// Playwright rejections that mean "the thing a teardown would clean up no longer exists", so the
// teardown is moot and must not mask the failure that triggered it. Deliberately narrow: any other
// rejection from a teardown is a real bug and is rethrown.
const IS_CONTEXT_GONE = /execution context was destroyed|target (page|closed)|target page, context or browser has been closed|cannot find context with specified id|frame was detached|browser has been closed|has been closed/i;

// Blazor Server renders static HTML first, then connects an interactive circuit a
// moment later. A click that lands before the circuit is live is a no-op, so we retry
// the click until the expected element appears.
//
// #312 — CHECK BEFORE YOU CLICK. This used to say "for idempotent open actions", and then
// SIX of its NINE call sites passed it a Bootstrap dropdown TOGGLE (`idea-card-menu-*`),
// which is the exact opposite: every click FLIPS it. The retry then fought itself —
//
//   attempt 1: click the ⋮   -> the menu opens, but the SignalR round-trip has not rendered
//   waitFor(2500ms)          -> TIMES OUT (the DOM has not caught up on a starved circuit)
//   attempt 2: click the ⋮   -> CLOSES the menu it just opened
//   ...and the caller's next line, a click on an item INSIDE that menu, races the close and
//      dies on the 15s default: `locator.click: Timeout 15000ms exceeded`.
//
// Load does not cause this. Load widens the window. The race is here, in the helper: it
// re-invokes `opener()` unconditionally, even when the thing it was asked to open is already
// open. One `isVisible` check makes a toggle safe and leaves the idempotent callers untouched
// (their target is not visible on entry, so they click exactly as they always did).
async function clickUntil(opener, expected, attempts = 6) {
  for (let i = 0; i < attempts; i++) {
    if (await expected.isVisible().catch(() => false)) return;
    try { await opener(); } catch { /* target not ready yet */ }
    try { await expected.waitFor({ state: "visible", timeout: 2500 }); return; }
    catch { await sleep(500); }
  }
  if (await expected.isVisible().catch(() => false)) return;
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

// Same real-mouse path as dragCardTo, but release near the top edge of a specific
// card so SortableJS places the dragged card immediately BEFORE that identity.
// This is the user gesture whose persisted meaning #307 protects.
async function dragCardBefore(page, cardSelector, beforeCardSelector) {
  const card = page.locator(cardSelector);
  const beforeCard = page.locator(beforeCardSelector);
  await beforeCard.scrollIntoViewIfNeeded();
  await card.scrollIntoViewIfNeeded();
  const cb = await card.boundingBox();
  const ab = await beforeCard.boundingBox();
  if (!cb || !ab) throw new Error("card or anchor card not visible for drag");
  const sx = cb.x + cb.width / 2, sy = cb.y + cb.height / 2;
  const tx = ab.x + ab.width / 2, ty = ab.y + Math.min(6, ab.height / 5);
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

async function waitForFeatureTaskOrder(featureId, expectedIds, timeoutMs = 6000) {
  const expected = expectedIds.join(",");
  const deadline = Date.now() + timeoutMs;
  let lastTasks = [];
  do {
    lastTasks = await api("GET", `/api/features/${featureId}/tasks`);
    if (lastTasks.map((task) => task.id).join(",") === expected) return lastTasks;
    await sleep(200);
  } while (Date.now() < deadline);
  throw new Error(`expected feature task order [${expected}], got [${lastTasks.map((task) => task.id)}]`);
}

async function waitForProjectBoardOrder(projectId, status, expectedIds, timeoutMs = 6000) {
  const expected = expectedIds.join(",");
  const deadline = Date.now() + timeoutMs;
  let lastTasks = [];
  do {
    const board = await api("GET", `/api/projects/${projectId}/board`);
    lastTasks = board[status] ?? [];
    if (lastTasks.map((task) => task.id).join(",") === expected) return lastTasks;
    await sleep(200);
  } while (Date.now() < deadline);
  throw new Error(`expected ${status} board order [${expected}], got [${lastTasks.map((task) => task.id)}]`);
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
//   /project/{id} effective rank, raw rank, Id asc        (ProjectView.OrderTasksInColumn)
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
const effectiveTaskRank = (task) => Number.isInteger(task.sortOrder)
  ? task.sortOrder + (task.id / 2147483648)
  : task.sortOrder;
const taskTruth = (tasks) => groupBy(tasks, (t) => t.status, (a, b) =>
  effectiveTaskRank(a) - effectiveTaskRank(b) || a.sortOrder - b.sortOrder || a.id - b.id);

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

// ── Dialog focus-trap harness (#300) ─────────────────────────────────────────────────────────────
// A modal's aria-modal="true" is a PROMISE that nothing behind it is reachable. The mouse keeps that
// promise for free (the backdrop swallows the clicks); the keyboard only keeps it if something traps
// Tab. Nothing in a screenshot, a unit test or the C# compiler can tell the two apart, so read it
// off the live DOM: where focus is, where the dialog's own focus order puts it, and how many
// focusables sit OUTSIDE the dialog — i.e. how much of the page a broken trap could leak onto.
//
// The selector is KanbanFlow._dialogFocusables' own (kanbanflow.js:222), deliberately: a trap judged
// against a different definition of "focusable" than the one it implements is judging a different
// dialog. Same for the visibility filter — getClientRects(), not offsetParent, which is null for the
// position:fixed .modal and would wrongly report an empty dialog.
const DIALOG_FOCUSABLE =
  'a[href]:not([tabindex="-1"]), button:not([disabled]):not([tabindex="-1"]), ' +
  'input:not([disabled]):not([tabindex="-1"]), select:not([disabled]):not([tabindex="-1"]), ' +
  'textarea:not([disabled]):not([tabindex="-1"]), [tabindex]:not([tabindex="-1"])';

async function probeDialogFocus(page, modalSelector) {
  return page.evaluate(({ sel, focusable }) => {
    const visible = (el) => el.getClientRects().length > 0;
    const modal = document.querySelector(sel);
    const active = document.activeElement;
    // Name whatever holds focus, so a failure says WHICH control the keyboard escaped to.
    const describe = (el) => {
      if (!el) return "null (focus is nowhere)";
      const testid = el.getAttribute?.("data-testid");
      const label = (el.getAttribute?.("aria-label") || el.textContent || "")
        .replace(/\s+/g, " ").trim().slice(0, 40);
      return `<${el.tagName.toLowerCase()}${testid ? ` data-testid="${testid}"` : ""}> ${label}`.trim();
    };
    const inDialog = modal ? [...modal.querySelectorAll(focusable)].filter(visible) : [];
    const outside = [...document.querySelectorAll(focusable)]
      .filter((el) => visible(el) && !(modal && modal.contains(el)));
    return {
      present: !!modal,
      inside: !!modal && !!active && modal.contains(active),
      index: inDialog.indexOf(active),   // position in the dialog's focus order; -1 = not one of them
      count: inDialog.length,
      escapes: outside.length,           // where a broken trap lands
      active: describe(active),
      activeTestid: active?.getAttribute?.("data-testid") ?? null,
    };
  }, { sel: modalSelector, focusable: DIALOG_FOCUSABLE });
}

// Park focus on the dialog's FIRST focusable. The Shift+Tab wrap has to be asked from a known place:
// pressing it from wherever the previous assertion happened to leave focus tests nothing in
// particular, and the trap's autofocus target is not what is under test here.
async function focusFirstInDialog(page, modalSelector) {
  await page.evaluate(({ sel, focusable }) => {
    const modal = document.querySelector(sel);
    const first = [...(modal?.querySelectorAll(focusable) ?? [])]
      .filter((el) => el.getClientRects().length > 0)[0];
    first?.focus();
  }, { sel: modalSelector, focusable: DIALOG_FOCUSABLE });
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

  // #312 — the health budget is a WALL-CLOCK BET ON A MACHINE WE DO NOT CONTROL, and the old one
  // (40 x 500ms = 20s) was losing it. Measured on a loaded box: the app came healthy at 20.0s
  // against a 20.0s budget — a coin flip that reported "App did not become healthy in time" for a
  // tree that was perfectly fine, with no stderr to explain it (the app had not crashed; it was
  // merely slow). A cold .NET start has to JIT, open SQLite, and run every Ensure* schema patcher.
  //
  // Raising the ceiling hides NOTHING. This is a readiness POLL, not a race: it returns the instant
  // the app answers, so an idle run is not one millisecond slower. It only stops a busy machine from
  // failing a green tree — which is the whole point, because a gate that only works when the box is
  // quiet is a gate nobody can trust. (Contrast the click timeouts, which must NOT be widened: there
  // the bigger number would hide a real race. See clickUntil above.)
  const startedAt = Date.now();
  const healthDeadline = startedAt + 90_000;

  // What the LAST probe actually saw. "Never answered" and "answered, but said it is not ok" are
  // completely different bugs — a hang or a pinned CPU vs. an app that is up and deliberately
  // reporting unhealthy — and a message that conflates them sends the next person hunting the wrong
  // one. So record which it was, and say so. (Copilot, PR #313.)
  let lastProbe = "never got a response (connection refused, or the probe timed out)";

  while (Date.now() < healthDeadline) {
    if (startupError) throw new Error(startupError);
    try {
      // Bound the request, or the deadline above is a lie. Node's fetch (undici) defaults
      // `headersTimeout` to 300s, so a server that ACCEPTS the connection and then never answers —
      // a deadlock, a locked DB, a pathological JIT — parks this loop for five minutes: the
      // deadline is only tested BETWEEN iterations, and `startupError` (set by the process `exit`
      // handler) never gets read either, so a crashed app is not noticed. (Gemini, PR #313.)
      //
      // 5s, not 2s. The abort must sit comfortably ABOVE any plausible healthy-but-slow response,
      // because aborting a good one and retrying forever is precisely the bug this PR exists to
      // kill: measured, /api/admin/health took 812ms under a pinned CPU, and the FIRST call also
      // pays for JIT. Aborting early is otherwise cheap — the next poll is 250ms away.
      const r = await fetch(`${baseUrl}/api/admin/health`, { signal: AbortSignal.timeout(5000) });
      const body = await r.text();
      let status = null;
      try { status = JSON.parse(body)?.status; } catch { /* not JSON — record it below */ }

      if (r.ok && status === "ok") {
        started = true;
        log(`App is up. (${((Date.now() - startedAt) / 1000).toFixed(1)}s)`);
        return;
      }
      lastProbe = `answered HTTP ${r.status}, body: ${body.slice(0, 160)}`;
    } catch (e) {
      lastProbe = `never got a response (${e.name}: ${e.message})`;
    }
    await sleep(250);
  }

  throw new Error(
    `App did not report healthy within 90s (waited ${((Date.now() - startedAt) / 1000).toFixed(1)}s). ` +
    `It did not crash.\n` +
    `  Last probe of /api/admin/health: ${lastProbe}\n` +
    `  If it never answered, the app never came up — on a heavily loaded machine that is the ` +
    `likeliest cause, so check CPU before suspecting the code.\n` +
    `  If it answered, the app IS up and is deliberately reporting unhealthy — read the body above.` +
    (stderrTail ? `\n--- stderr ---\n${stderrTail}` : ""));
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

    // ── Regression guard (#312): clickUntil must not re-click an opener it has already opened ──
    // Why this is not a browser scenario. The bug needs `waitFor` to MISS while the click ACTUALLY
    // WORKED — a starved SignalR circuit, which is exactly the non-determinism the bug is made of.
    // Staging that in a live browser means racing the same coin flip the bug is, and a guard that
    // only fails when the machine happens to be busy is not a guard. So the REAL clickUntil is
    // driven with a doubled locator whose first wait misses on purpose. The function under test is
    // the real one; only its inputs are doubled. Deterministic, and it fails for the right reason.
    //
    // It cost a full false bisect to find: two runs failed on "Deleting an idea removes its card"
    // while a CONTROL RUN AT THE SAME COMMIT PASSED — which reads exactly like a regression in the
    // code under test. It was not. It was this.
    await test("clickUntil does not re-click a toggle it has already opened (#312)", async () => {
      let isOpen = false;        // the ⋮ menu: every click FLIPS it
      let clicks = 0;
      let firstWaitMisses = true;

      const opener = async () => { clicks++; isOpen = !isOpen; };
      const expected = {
        isVisible: async () => isOpen,
        waitFor: async () => {
          // The render lands, but not before the 2500ms window — the whole bug in one line.
          if (firstWaitMisses) { firstWaitMisses = false; throw new Error("Timeout 2500ms exceeded"); }
          if (!isOpen) throw new Error("not visible");
        },
      };

      await clickUntil(opener, expected);

      assert(clicks === 1,
        `clickUntil clicked the toggle ${clicks}x. It must click ONCE: the first waitFor missed ` +
        `(slow render) even though the click had already worked, so the old code flipped the ⋮ ` +
        `back SHUT and then open again. In a real browser that is a menu flickering closed under ` +
        `the caller, whose very next line clicks an item inside it — hence the random ` +
        `"locator.click: Timeout 15000ms exceeded". Check the expected element BEFORE re-clicking.`);
      assert(isOpen,
        "clickUntil returned with the menu CLOSED — the caller is about to click into nothing.");
    });

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
    // ProjectView orders cards through the canonical effective/raw-rank/Id rule, and that sort lives
    // ONLY in the Razor: ProjectView builds `allTasks` from an unordered
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

    // ── Regression guard (#307): rendered indices are not database indices ──────────────
    // KanbanBoard accepts a caller-supplied subset. #112 will use that for filtering, but
    // shipping the filter merely to prove this lower-level contract would couple two issues.
    // Instead, stage the exact DOM shape a filtered board renders: five persisted cards, only
    // two card nodes present while Sortable computes the drop. The one-shot revert wrapper puts
    // every node back before Blazor is invoked, preserving the #294 DOM-ownership invariant.
    const partialProj = await api("POST", "/api/projects", { name: `E2E Partial Drag ${stamp}` });
    const partialFeatureA = await api("POST", `/api/projects/${partialProj.id}/features`, { name: "E2E Partial Feature A" });
    const partialFeatureB = await api("POST", `/api/projects/${partialProj.id}/features`, { name: "E2E Partial Feature B" });
    // Normal CreateAsync ranks are feature-local, so these interleaved tasks persist as
    // A0/id1, B0/id2, A1/id3, B1/id4, A2/id5. The two duplicate-rank groups are the common
    // case that the effective legacy positions must separate without rewriting bystanders.
    const partialTasks = [
      await api("POST", `/api/features/${partialFeatureA.id}/tasks`, { name: `E2E Partial A0 ${stamp}`, status: "Todo", sortOrder: 0 }),
      await api("POST", `/api/features/${partialFeatureB.id}/tasks`, { name: `E2E Partial B0 ${stamp}`, status: "Todo", sortOrder: 0 }),
      await api("POST", `/api/features/${partialFeatureA.id}/tasks`, { name: `E2E Partial A1 ${stamp}`, status: "Todo", sortOrder: 1 }),
      await api("POST", `/api/features/${partialFeatureB.id}/tasks`, { name: `E2E Partial B1 ${stamp}`, status: "Todo", sortOrder: 1 }),
      await api("POST", `/api/features/${partialFeatureA.id}/tasks`, { name: `E2E Partial A2 ${stamp}`, status: "Todo", sortOrder: 2 }),
    ];
    const [hiddenBefore, visibleAnchor, hiddenMiddle, movedVisible, hiddenAfter] = partialTasks;

    await test("Filtered board drag is persisted relative to the visible task identity", async () => {
      await page.goto(`${baseUrl}/project/${partialProj.id}`);
      for (const task of partialTasks) {
        await page.locator(`[data-testid="board-card-${task.id}"]`).waitFor({ state: "visible" });
      }
      await waitForSortableArmed(page, "Todo");

      const beforeTasks = (await api("GET", `/api/projects/${partialProj.id}/board`)).Todo;
      const beforeIds = beforeTasks.map((task) => task.id);
      const beforeRanks = new Map(beforeTasks.map((task) => [task.id, task.sortOrder]));
      assert(beforeIds.join(",") === partialTasks.map((task) => task.id).join(","),
        `seed: expected dense persisted order [${partialTasks.map((task) => task.id)}], got [${beforeIds}]`);

      let bodyErr, unexpectedTeardownErr;
      try {
        await page.evaluate(({ visibleIds }) => {
          const column = document.querySelector('[data-testid="board-column-Todo-cards"]');
          if (!column) throw new Error("Todo card container not found");
          const originalChildren = Array.from(column.children);
          const originalRevert = window.KanbanFlow._revertDraggedNode;

          // Restore the complete Blazor-owned DOM synchronously after Sortable reports the partial
          // drop but before invokeMethodAsync starts the server round-trip.
          const patched = function (evt) {
            originalRevert.call(window.KanbanFlow, evt);
            for (const child of originalChildren) column.appendChild(child);
            window.KanbanFlow._revertDraggedNode = originalRevert;
            delete window.KanbanFlow._e2eRevertPatch;
            delete window.KanbanFlow._e2eOriginalRevert;
          };

          // Publish both halves so the finally block can undo the patch even when `onEnd` — and
          // therefore the self-restore above — never fires. Without it the helper stays patched
          // for the remainder of THIS document (the next drag test navigates, which tears down the
          // JS context and the patch with it), and until then the closure keeps every removed node
          // reachable and every later drop on this page runs the stale wrapper.
          window.KanbanFlow._e2eRevertPatch = patched;
          window.KanbanFlow._e2eOriginalRevert = originalRevert;
          window.KanbanFlow._revertDraggedNode = patched;

          for (const child of originalChildren) {
            const id = Number(child.getAttribute("data-task-id"));
            if (!visibleIds.includes(id)) child.remove();
          }
        }, { visibleIds: [visibleAnchor.id, movedVisible.id] });

        const renderedIds = await page.$$eval(
          '[data-testid="board-column-Todo-cards"] [data-task-id]',
          (els) => els.map((el) => Number(el.getAttribute("data-task-id"))),
        );
        assert(renderedIds.join(",") === [visibleAnchor.id, movedVisible.id].join(","),
          `staged board must render only the two visible cards, got [${renderedIds}]`);

        await dragCardBefore(page,
          `[data-testid="board-card-${movedVisible.id}"]`,
          `[data-testid="board-card-${visibleAnchor.id}"]`);
      } catch (err) {
        bodyErr = err;
      } finally {
        // Must be a real finally: the staged-board assert above and dragCardBefore both throw, and
        // the surrounding test() helper catches and continues to the next test — so a teardown
        // placed merely *after* the drag is skipped on exactly the failure paths that leave the
        // patch installed. Restore ONLY while our own patch is still the installed helper: if
        // `onEnd` already restored it, or anything else replaced it since, overwriting here would
        // reinstate a stale function. `?? {}` keeps this a no-op if the JS context is already gone.
        //
        // Best-effort by construction: `page.evaluate` REJECTS when the execution context is
        // destroyed by a navigation, or when the page/context/browser has been closed. A throw
        // inside a `finally` REPLACES the exception that put us here — so we store any unexpected
        // teardown error in `unexpectedTeardownErr` and surface it AFTER the finally block, where
        // it cannot mask the primary body failure. Context-gone errors are swallowed (teardown is
        // moot once the page is gone); everything else is a real bug and surfaces below.
        try {
          await page.evaluate(() => {
            const kf = window.KanbanFlow ?? {};
            if (kf._e2eRevertPatch && kf._revertDraggedNode === kf._e2eRevertPatch) {
              kf._revertDraggedNode = kf._e2eOriginalRevert;
            }
            delete kf._e2eRevertPatch;
            delete kf._e2eOriginalRevert;
          });
        } catch (err) {
          if (!IS_CONTEXT_GONE.test(err?.message ?? "")) unexpectedTeardownErr = err;
        }
      }
      // Surface errors outside the finally so neither masks the other.
      if (bodyErr) {
        if (unexpectedTeardownErr) {
          console.error(`[e2e teardown] additional error after body failure: ${unexpectedTeardownErr.message}`);
        }
        throw bodyErr;
      }
      if (unexpectedTeardownErr) throw unexpectedTeardownErr;

      const expectedIds = [hiddenBefore.id, movedVisible.id, visibleAnchor.id, hiddenMiddle.id, hiddenAfter.id];
      const afterTasks = await waitForProjectBoardOrder(partialProj.id, "Todo", expectedIds);
      const beforeBystanders = beforeIds.filter((id) => id !== movedVisible.id);
      const afterBystanders = afterTasks.map((task) => task.id).filter((id) => id !== movedVisible.id);
      assert(afterBystanders.join(",") === beforeBystanders.join(","),
        `non-dragged tasks changed relative order: before [${beforeBystanders}], after [${afterBystanders}]`);

      for (const task of afterTasks.filter((task) => task.id !== movedVisible.id)) {
        assert(task.sortOrder === beforeRanks.get(task.id),
          `hidden/bystander task ${task.id} SortOrder changed from ${beforeRanks.get(task.id)} to ${task.sortOrder}`);
      }
      const movedAfter = afterTasks.find((task) => task.id === movedVisible.id);
      assert(movedAfter?.sortOrder > 0 && movedAfter?.sortOrder < 1,
        `moved task should receive a fractional rank inside the legacy rank-0 interval, got ${movedAfter?.sortOrder}`);

      // A task response containing a fractional rank must be accepted by the public PUT surface;
      // otherwise GET -> PUT round trips would break after the first identity-anchored drag.
      const roundTripped = await api("PUT", `/api/tasks/${movedVisible.id}`, {
        name: movedAfter.name,
        description: movedAfter.description,
        status: movedAfter.status,
        priority: movedAfter.priority,
        assignee: movedAfter.assignee,
        sortOrder: movedAfter.sortOrder,
        actor: "ui-e2e",
      });
      assert(roundTripped.sortOrder === movedAfter.sortOrder,
        `fractional SortOrder did not survive GET -> PUT: ${roundTripped.sortOrder}`);
    });

    // The outline already has a live partial-DOM seam: expanded subtask cards are direct siblings
    // of task cards inside `.feature-tasks`, so evt.newIndex counts nodes the database task list
    // does not contain. Identity anchoring must scan past those non-task siblings.
    const outlineProj = await api("POST", "/api/projects", { name: `E2E Outline Anchor ${stamp}` });
    const outlineFeature = await api("POST", `/api/projects/${outlineProj.id}/features`, { name: "E2E Outline Feature" });
    const outlineA = await api("POST", `/api/features/${outlineFeature.id}/tasks`, { name: `Outline A ${stamp}`, status: "Todo", sortOrder: 0 });
    const outlineB = await api("POST", `/api/features/${outlineFeature.id}/tasks`, { name: `Outline B ${stamp}`, status: "Review", sortOrder: 1 });
    const outlineC = await api("POST", `/api/features/${outlineFeature.id}/tasks`, { name: `Outline C ${stamp}`, status: "Backlog", sortOrder: 2 });
    const outlineSubtask = await api("POST", `/api/tasks/${outlineA.id}/subtasks`, {
      text: `Expanded child ${stamp}`, completed: false, sortOrder: 0, parentSubtaskId: null,
    });

    await test("Outline drag ignores expanded subtask siblings and anchors to the next task", async () => {
      await page.goto(`${baseUrl}/project/${outlineProj.id}`);
      for (const task of [outlineA, outlineB, outlineC]) {
        await page.locator(`[data-testid="outline-task-${task.id}"]`).waitFor({ state: "visible" });
      }
      await page.locator(`[data-testid="outline-subtask-${outlineSubtask.id}"]`).waitFor({ state: "visible" });
      await page.waitForFunction((featureId) => {
        const container = document.querySelector(`[data-testid="outline-feature-${featureId}-tasks"]`);
        return container && window.Sortable && window.Sortable.get(container);
      }, outlineFeature.id);

      const directChildren = await page.$$eval(
        `[data-testid="outline-feature-${outlineFeature.id}-tasks"] > [data-testid]`,
        (els) => els.map((el) => el.getAttribute("data-testid")),
      );
      assert(directChildren.join(",") === [
        `outline-task-${outlineA.id}`,
        `outline-subtask-${outlineSubtask.id}`,
        `outline-task-${outlineB.id}`,
        `outline-task-${outlineC.id}`,
      ].join(","), `seed: expanded outline child sequence was [${directChildren}]`);

      const ranksBefore = new Map(
        (await api("GET", `/api/features/${outlineFeature.id}/tasks`))
          .map((task) => [task.id, task.sortOrder]),
      );

      await dragCardBefore(page,
        `[data-testid="outline-task-${outlineC.id}"]`,
        `[data-testid="outline-task-${outlineB.id}"]`);

      const beforeIds = [outlineA.id, outlineB.id, outlineC.id];
      const expectedIds = [outlineA.id, outlineC.id, outlineB.id];
      const afterTasks = await waitForFeatureTaskOrder(outlineFeature.id, expectedIds);
      const beforeBystanders = beforeIds.filter((id) => id !== outlineC.id);
      const afterBystanders = afterTasks.map((task) => task.id).filter((id) => id !== outlineC.id);
      assert(afterBystanders.join(",") === beforeBystanders.join(","),
        `outline bystanders changed relative order: before [${beforeBystanders}], after [${afterBystanders}]`);
      for (const task of afterTasks.filter((task) => task.id !== outlineC.id)) {
        assert(task.sortOrder === ranksBefore.get(task.id),
          `outline bystander ${task.id} SortOrder changed from ${ranksBefore.get(task.id)} to ${task.sortOrder}`);
      }
      const movedAfter = afterTasks.find((task) => task.id === outlineC.id);
      assert(movedAfter?.sortOrder === 0.5,
        `outline moved task should receive midpoint rank 0.5, got ${movedAfter?.sortOrder}`);
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
      //
      // ── Settling this drop has NO NATURAL SIGNAL, which is why it used to be a bare sleep(900).
      // Two facts conspire. kanbanflow.js reverts the dragged node SYNCHRONOUSLY inside onEnd,
      // *before* it calls into Blazor, so the DOM is already canonical the instant the mouse comes
      // up; and on a healthy board LoadIdeas' re-render is then a NO-OP DIFF that changes nothing.
      // So "poll until the order is canonical" is worse than the sleep, not better: it returns TRUE
      // immediately, BEFORE the round-trip lands, and would sail straight past a re-render that
      // corrupts the board — the exact bug this guard exists to catch. The sleep at least waited.
      //
      // So MANUFACTURE a signal. This idea is created out-of-band while the board is open, and the
      // page has no timer, no SignalR push and no polling — LoadIdeas runs ONLY when an interaction
      // asks it to. The open board therefore CANNOT know about this card until the drop's own reload
      // fetches it. Its appearance is proof that LoadIdeas ran AND that Blazor applied the resulting
      // render batch (batches are applied atomically, so New's cards are settled with it).
      //
      // It also kills the sleep's blind spot: if the drop never fired at all, OnCardMoved never runs,
      // LoadIdeas never runs, this card never appears — and the assertions below go RED instead of
      // passing on a board nobody actually dragged.
      const barrier = await mkIdea("Barrier", "Low");
      assert(await page.locator(`[data-testid="idea-card-${barrier.id}"]`).count() === 0,
        "precondition: the open board must not already know about the out-of-band barrier idea");

      await dragCardTo(page, `[data-testid="idea-card-${bottom.id}"]`,
        `[data-testid="board-column-New-cards"]`);

      // The reload has landed exactly when the card it fetched is on screen. On a CORRUPT board the
      // diff can wedge and never draw it — so don't die here with an opaque Playwright timeout; fall
      // through and let the assertions below say precisely what the board is drawing and why that is
      // wrong. Generous timeout: this waits on a real signal, so a slow box costs time, never a flake.
      await page.locator(`[data-testid="idea-card-${barrier.id}"]`)
        .waitFor({ state: "visible", timeout: 15000 })
        .catch(() => {});

      const settled = await readBoardDom(page, IDEA_COLUMNS);
      assertBoardTruthful(settled, ideaTruth(await api("GET", "/api/ideas?limit=500")),
        "after dropping an idea out of canonical order inside its own column");
      const canonical = [top.id, bottom.id, barrier.id];
      assert(settled.New.cardIds.join(",") === canonical.join(","),
        `an in-column drop must snap back to the canonical order [${canonical}] ` +
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

    // ── Regression guard (#293): an OPEN ⋮ menu must be CLICKABLE, not merely VISIBLE ─────────
    // app.css:604 — `.board-card:has(.dropdown-menu.show) { z-index: 3 }` — is ONE line, it is
    // load-bearing, and nothing held it down: revert it and `dotnet build`, `dotnet test` and this
    // very suite all stay green while Promote and Delete quietly stop working on every idea card
    // that has another card underneath it.
    //
    // The mechanism is stated in full beside the rule. In short: `.board-card:hover` lifts the card
    // with `transform: translateY(-1px)`, and a transform CREATES A STACKING CONTEXT. The pointer is
    // on the card — it must be; you just clicked that card's ⋮ — so the card becomes a stacking
    // context and the menu's `z-index: 1000` is scoped to the CARD instead of the board. The card
    // itself sits at `z-index: auto`, so the NEXT card (also auto, but later in DOM order) paints
    // over the whole subtree, menu included. The menu is not hidden and not clipped. It is BURIED.
    //
    // TWO things kept this bug alive, and the guard has to beat both:
    //
    //  1. VISIBILITY SEES NOTHING. A buried button is painted, has a box, has size, is not
    //     `display:none` and is not clipped — Playwright's visibility checks, and every assertion in
    //     this file above this one, call it perfectly fine. Only a HIT TEST (`elementFromPoint`) can
    //     say that the pixel under the cursor belongs to somebody else. So this asserts what a CLICK
    //     WOULD REACH, never what is drawn.
    //
    //  2. THE BUG EVAPORATES IF YOU LOOK AWAY. No hover => no transform => no stacking context =>
    //     the menu is on top and everything passes on definitively broken code. Moving the mouse off
    //     the card — or scrolling, which moves the pointer relative to the page — is enough to erase
    //     it. A first draft of this guard clicked the ⋮ and then scrolled, and reported GREEN against
    //     a deliberately sabotaged stylesheet. So: hover the card, open the menu with a click that
    //     lands INSIDE it, and hit-test from `page.evaluate` — which cannot move the pointer — with
    //     no scroll and no locator walk in between.
    //
    // The preconditions below are this test's "can it still fail?" proof. Each is a real way the
    // guard could rot into a green that means nothing: the pointer must still be ON the card at the
    // moment of the hit-test (P1), the hover-lift that creates the stacking context must still exist
    // (P2), the card must have a sibling BELOW it — that sibling is what does the burying (P3) — and
    // the menu must actually SPILL past the card's bottom edge, or there is nothing to bury (P4).
    await test("Ideas board: every item of an open ⋮ menu is hit-testable, not buried under the next card", async () => {
      const viewport = page.viewportSize();
      try {
        // Tall enough that the whole menu opens DOWNWARD and inside the viewport. Popper flips a
        // dropdown upward when it runs out of room below, which would put it over the cards ABOVE
        // the target — no sibling underneath it, no burial, vacuous pass. P4 catches a flip, but
        // it is better not to provoke one. Width is unchanged (>= 992px stays the desktop layout).
        await page.setViewportSize({ width: viewport?.width ?? 1280, height: 1000 });

        // Clean slate: the seed IS the board, so "first card in New" is exact.
        for (const i of await api("GET", "/api/ideas?limit=500")) {
          await api("DELETE", `/api/ideas/${i.id}`).catch(() => {});
        }
        // Distinct priorities fix New's order outright (`Priority desc`), so the target is the FIRST
        // card and the other two sit under it. The target carries NO body and NO tags on purpose: it
        // must be the SHORT card. A tall target would swallow its own menu, nothing would spill past
        // its bottom edge, no sibling could bury anything — and P4 would (correctly) call the seed
        // undiscriminating. The two cards below it are what the menu gets buried under.
        const mkIdea = (label, priority, body) => api("POST", "/api/ideas",
          { title: `E2E Menu ${label} ${stamp}`, priority, body });
        const target = await mkIdea("Target", "Critical", null);
        await mkIdea("Under", "High", "The card that paints over the open menu.");
        await mkIdea("Below", "Medium", "And the one under that.");

        await page.goto(`${baseUrl}/ideas`);
        const card = page.locator(`[data-testid="idea-card-${target.id}"]`);
        await card.waitFor({ state: "visible" });
        // Not needed by the dropdown (Bootstrap's own JS runs it), but it is this file's settled
        // "the board has finished standing itself up" signal: LoadIdeas' re-render, and the deferred
        // initBoardDragDrop behind it, are done — so nothing replaces the card DOM mid-hit-test.
        await waitForSortableArmed(page, "New");

        // ⚠️ FROM HERE THE POINTER MUST NOT LEAVE THE CARD. hover() scrolls the card into view and
        // parks the mouse on it; the ⋮ lives inside the card, so clicking it keeps `:hover` live
        // (hover matches every ancestor of the hovered node). No scrollIntoView, no boundingBox
        // walk, no goto, nothing else until the hit-test has run.
        await card.hover();
        const menu = page.locator(`[data-testid="idea-card-${target.id}"] .dropdown-menu.show`);
        await clickUntil(
          () => page.locator(`[data-testid="idea-card-menu-${target.id}"]`).click(),
          menu,
        );

        // ONE evaluate: it cannot move the mouse, so the card is still hovered, still transformed,
        // still a stacking context — the exact state a real user is in when they reach for Delete.
        const probe = await page.evaluate((id) => {
          const cardEl = document.querySelector(`[data-testid="idea-card-${id}"]`);
          if (!cardEl) return { error: `the card for idea ${id} is not in the DOM` };
          const menuEl = cardEl.querySelector(".dropdown-menu.show");
          const cardRect = cardEl.getBoundingClientRect();
          const style = getComputedStyle(cardEl);
          // The opening tag of whatever is actually painted at that point — the tell.
          const tagOf = (el) => {
            if (!el) return "null (nothing is painted there)";
            const attrs = [...el.attributes].map((a) => ` ${a.name}="${a.value}"`).join("");
            return `<${el.tagName.toLowerCase()}${attrs}>`.slice(0, 140);
          };
          const siblings = [...document.querySelectorAll('[data-testid="board-column-New-cards"] [data-task-id]')];
          const idx = siblings.indexOf(cardEl);
          return {
            hovered: cardEl.matches(":hover"),
            transform: style.transform,
            zIndex: style.zIndex,
            menuOpen: !!menuEl,
            cardsBelow: idx < 0 ? -1 : siblings.length - 1 - idx,
            // `.dropdown-item` is exactly the actionable items — the dividers and the "Move to"
            // header carry no such class and are not things a user can click.
            items: [...(menuEl?.querySelectorAll(".dropdown-item") ?? [])].map((el) => {
              const r = el.getBoundingClientRect();
              const cx = r.left + r.width / 2;
              const cy = r.top + r.height / 2;
              const hit = document.elementFromPoint(cx, cy);
              return {
                testid: el.getAttribute("data-testid"),
                label: (el.textContent ?? "").trim(),
                // Does this item spill past the card, into the sibling's box? (P4)
                spillsPastCard: cy > cardRect.bottom,
                inViewport: cx >= 0 && cx <= window.innerWidth && cy >= 0 && cy <= window.innerHeight,
                // THE question: would a click here land on this item, or on somebody else's markup?
                reachable: !!hit && el.contains(hit),
                hit: tagOf(hit),
              };
            }),
          };
        }, target.id);

        assert(!probe.error, probe.error ?? "");
        assert(probe.menuOpen, "the ⋮ menu never opened — there is nothing to hit-test");

        // ── P1-P4: the preconditions that make a green here MEAN something ──────────────────────
        assert(probe.hovered,
          "PRECONDITION: the pointer is no longer over the card at the moment of the hit-test. " +
          "No hover => no transform => no stacking context => the menu is on top and this test " +
          "passes on BROKEN code. Something above moved the mouse or scrolled the page.");
        assert(probe.transform !== "none",
          `PRECONDITION: the hovered card has no transform (computed \`${probe.transform}\`), so it ` +
          "is not a stacking context and the bug this guard exists for cannot occur. The hover-lift " +
          "(.board-card:hover { transform: translateY(-1px) }) is what creates it — if that is gone, " +
          "app.css:604 may be dead code, and this guard must be re-derived rather than left green.");
        assert(probe.cardsBelow >= 1,
          `seed is not discriminating: the card under test has ${probe.cardsBelow} card(s) below it in ` +
          "New. The burying element IS the next card — with none, nothing can paint over the menu.");
        const spill = probe.items.filter((i) => i.spillsPastCard);
        assert(spill.length > 0,
          "seed is not discriminating: no menu item extends past the card's bottom edge, so the menu " +
          "sits entirely inside its own card and the sibling below has nothing to bury. (Either the " +
          "target card grew a body/tags and is now too tall, the menu lost items, or Popper flipped " +
          "the menu upward for want of room below it.)");

        // ── P5: the items whose burial IS the bug must actually be on the menu ──────────────────
        const rendered = probe.items.map((i) => i.testid);
        for (const required of [
          `idea-card-move-${target.id}-Rejected`,
          `idea-card-promote-${target.id}`,
          `idea-card-delete-${target.id}`,
        ]) {
          assert(rendered.includes(required),
            `the open menu does not render ${required} — it renders [${rendered}]. These are the ` +
            "three items the bug buried; a menu without them cannot demonstrate anything.");
        }

        // ── P6: elementFromPoint is viewport-relative, so an off-screen centre returns null for a ──
        //    reason that has nothing to do with burial. Say so, rather than blaming the z-index.
        const offscreen = probe.items.filter((i) => !i.inViewport);
        assert(offscreen.length === 0,
          `PRECONDITION: ${offscreen.map((i) => i.label).join(", ")} lie outside the viewport, where ` +
          "elementFromPoint can see nothing at all. Give the page more height.");

        // ── THE ASSERTION. Not "is it visible" — "would a click get there". ─────────────────────
        const buried = probe.items.filter((i) => !i.reachable);
        assert(buried.length === 0,
          `${buried.length} of ${probe.items.length} items in the open ⋮ menu are BURIED — they are ` +
          `painted, but a click at their centre lands on somebody else's markup (the card owning the ` +
          `open menu is painting at z-index: ${probe.zIndex}):\n` +
          buried.map((i) => `    BURIED  ${i.label.padEnd(10)} [${i.testid}]  elementFromPoint = ${i.hit}`).join("\n") +
          "\n  This is what app.css:604 (.board-card:has(.dropdown-menu.show) { z-index: 3 }) prevents.");
      } finally {
        if (viewport) await page.setViewportSize(viewport);
      }
    });

    // ── Regression guard (#300): the confirms are REAL dialogs, not painted-on ones ───────────────
    // Both hand-rolled confirms have advertised role="dialog" + aria-modal="true" since the day they
    // were written — a promise that nothing behind them is reachable — while Tab walked straight out
    // of an open confirm and onto the board underneath, and closing one dropped focus on the floor.
    // A mouse user could never tell; a keyboard user is driving controls hidden behind a modal.
    // Nothing else in this suite, in xUnit, or in the compiler can see any of that: the modal LOOKS
    // right, so the only way to hold the wiring in place is to press the keys.
    //
    // Three questions, each of which the unwired code answers wrongly:
    //   1. does Tab stay inside the dialog, however many times it is pressed;
    //   2. does Shift+Tab from the FIRST focusable wrap round to the LAST, or reverse out;
    //   3. once the dialog closes, does focus return to the control that opened it.
    // (3) is asked of the PROMOTE confirm on purpose. Its opener is a plain visible button on the
    // card, which is still there afterwards — so "focus came back" is a question with a legible
    // answer. The delete confirm opens from a ⋮ item that Bootstrap display:none's on click, and
    // "focus returned to a hidden element" would mean nothing whichever way it came out. Same
    // destroyDialog code path either way; ask it where the answer can be read.
    await test("Ideas board: the confirm dialogs trap Tab and hand focus back to the opener", async () => {
      // Clean slate: one idea, so "the card" is unambiguous.
      for (const i of await api("GET", "/api/ideas?limit=500")) {
        await api("DELETE", `/api/ideas/${i.id}`).catch(() => {});
      }
      const idea = await api("POST", "/api/ideas", { title: `E2E Trap ${stamp}`, priority: "High" });

      await page.goto(`${baseUrl}/ideas`);
      await page.locator(`[data-testid="idea-card-${idea.id}"]`).waitFor({ state: "visible" });
      // This file's settled "the board has finished standing itself up" signal: the circuit is live,
      // so a click reaches the server and the modal's OnAfterRenderAsync (which arms the trap) runs.
      await waitForSortableArmed(page, "New");

      // ── 1. The DELETE confirm: Tab cannot leave it ───────────────────────────────────────────
      const deleteModal = '[data-testid="idea-delete-modal"]';
      await clickUntil(
        () => page.locator(`[data-testid="idea-card-menu-${idea.id}"]`).click(),
        page.locator(`[data-testid="idea-card-delete-${idea.id}"]`),
      );
      await page.locator(`[data-testid="idea-card-delete-${idea.id}"]`).click();
      await page.locator(deleteModal).waitFor({ state: "visible" });

      const opened = await probeDialogFocus(page, deleteModal);

      // ── PRECONDITIONS: what makes a green here MEAN something ────────────────────────────────
      assert(opened.count >= 3,
        `the delete confirm exposes ${opened.count} focusable control(s). A cycle needs at least three ` +
        "(✕, Cancel, Delete) — with fewer, wrapping and not-wrapping look identical and this guard is " +
        "vacuous.");
      assert(opened.escapes > 0,
        "PRECONDITION: nothing OUTSIDE the open confirm is focusable, so Tab has nowhere to escape to " +
        "and even an untrapped dialog would hold focus by accident. This guard would pass on broken " +
        "code. (The board, its filter bar and its cards are supposed to be sitting right behind it.)");
      assert(opened.inside,
        `the confirm opened without taking focus at all — focus is on ${opened.active}. Nothing below ` +
        "this line is being tested from inside the dialog.");

      // Press Tab MORE times than the dialog has focusables: a "trap" that merely lets you walk to
      // the last control and out would survive any shorter walk.
      const presses = opened.count + 2;
      for (let i = 1; i <= presses; i++) {
        await page.keyboard.press("Tab");
        const p = await probeDialogFocus(page, deleteModal);
        assert(p.inside,
          `Tab #${i} of ${presses} walked OUT of the open delete confirm and onto ${p.active}. The ` +
          'dialog claims aria-modal="true" and its backdrop swallows every click, so a keyboard user ' +
          "is now operating a control they cannot see, behind a modal they cannot leave. " +
          "(KanbanFlow.initDialog is the Tab cycle — Ideas.razor must call it.)");
      }

      // Shift+Tab from the first control must WRAP to the last, not reverse out the front.
      await focusFirstInDialog(page, deleteModal);
      const parked = await probeDialogFocus(page, deleteModal);
      assert(parked.index === 0,
        `could not park focus on the confirm's first focusable — focus sits at index ${parked.index} ` +
        `(${parked.active}). The wrap below would be asked from the wrong place.`);

      await page.keyboard.press("Shift+Tab");
      const wrapped = await probeDialogFocus(page, deleteModal);
      assert(wrapped.inside,
        `Shift+Tab from the confirm's FIRST control reversed straight out of the dialog and onto ` +
        `${wrapped.active}. Backwards is a leak too — a forward-only trap is not a trap.`);
      assert(wrapped.index === opened.count - 1,
        `Shift+Tab from the first control must wrap to the LAST (index ${opened.count - 1}); it landed ` +
        `at index ${wrapped.index} (${wrapped.active}).`);

      // Esc still cancels (#296 AC4) — now via initDialog -> [JSInvokable] CloseFromJs, which REPLACED
      // the hand-rolled @onkeydown handler. Deleting that handler without wiring its replacement, or
      // wiring it without the StateHasChanged a [JSInvokable] does not get for free, lands here.
      await page.keyboard.press("Escape");
      await page.locator(deleteModal).waitFor({ state: "detached" });
      const survivor = await api("GET", `/api/ideas/${idea.id}`).catch(() => null);
      assert(survivor !== null, "Esc on the DELETE confirm must cancel it — the idea was deleted instead");

      // ── 2. The PROMOTE confirm: focus goes back where it came from ───────────────────────────
      const promoteModal = '[data-testid="idea-promote-modal"]';
      const openerTestid = `idea-card-promote-btn-${idea.id}`;
      await clickUntil(
        () => page.locator(`[data-testid="${openerTestid}"]`).click(),
        page.locator(promoteModal),
      );

      const promoteOpen = await probeDialogFocus(page, promoteModal);
      // Load-bearing precondition. Clicking the opener FOCUSED it, so "focus is on the opener after
      // close" is also true of a dialog that never took focus in the first place. Focus has to be
      // shown leaving the opener before its return can mean anything.
      assert(promoteOpen.inside,
        `the promote confirm opened without taking focus — it is still on ${promoteOpen.active}, so ` +
        "the focus-restore assertion below could not tell a working restore from a dialog that never " +
        "moved focus at all.");
      assert(promoteOpen.count >= 3,
        `the promote confirm exposes only ${promoteOpen.count} focusable control(s)`);

      await page.keyboard.press("Escape");
      await page.locator(promoteModal).waitFor({ state: "detached" });

      const restored = await probeDialogFocus(page, promoteModal);
      assert(restored.activeTestid === openerTestid,
        "closing the promote confirm dropped focus on the floor instead of handing it back to the " +
        `button that opened it — focus is now on ${restored.active}. A keyboard user who cancels a ` +
        "confirm is dumped at the top of the document and has to walk the entire board again to get " +
        "back to the card they were on. (KanbanFlow.destroyDialog is the focus restore, and it only " +
        "runs while the dialog's node still EXISTS — these confirms are @if-rendered, so a teardown " +
        "deferred to after the close-render is handed a null element and silently does nothing.)");

      const notPromoted = await api("GET", `/api/ideas/${idea.id}`);
      assert(notPromoted.status === "New",
        `Esc on the PROMOTE confirm must cancel it — the idea is now ${notPromoted.status}`);
    });

    // ── Scenario: the activity timeline's fold survives a refresh of the SAME task (#328) ──
    //
    // ActivityTimeline re-reads its audit events when TaskId OR RefreshToken changes, and it used
    // to reset `expanded` for BOTH — so posting a comment (which bumps RefreshToken; see
    // TaskDetailPanel.AddCommentAsync) collapsed the history the reader had just opened, right
    // underneath them. `eventLimit` was correctly guarded on "different task only" and `expanded`
    // was not; the two are the same invariant.
    //
    // This is a browser test on purpose. The bug lived in the WIRING — which state resets sit
    // inside which guard in OnParametersSet — so a unit test on an extracted helper would have
    // gone green against the broken component. There is no bUnit in this repo by choice.
    await test("Timeline: posting a comment does not re-collapse an expanded history (#328)", async () => {
      const timelineTask = await api("POST", `/api/features/${feature.id}/tasks`,
        { name: `E2E timeline fold ${stamp}` });

      // CollapseAfter is 10 in TaskDetailPanel, so this has to clear 10 rows to produce a fold.
      //
      // The expected row count is 13, and every term matters:
      //   12  comments. Each ALSO writes a paired TaskCommentAdded event, which the timeline
      //       de-duplicates away — so this contributes 12 rows, not 24. Asserted, not assumed:
      //       if de-duplication broke, the toggle below would read "Show all 25".
      //  + 1  the task's own creation. TaskService writes a TaskCreated event AND a Created
      //       comment for it; those de-duplicate to a single row too (the event wins, it carries
      //       the snapshot).
      const seeded = 12;
      const expectedRows = seeded + 1;
      for (let i = 1; i <= seeded; i++) {
        await api("POST", `/api/tasks/${timelineTask.id}/comments`,
          { text: `seeded comment ${i}`, author: "seed", type: "Note" });
      }

      await page.goto(`${baseUrl}/project/${proj.id}`);
      await clickUntil(
        () => page.locator(`[data-testid="board-card-${timelineTask.id}-title"]`).click(),
        page.locator('[data-testid="task-detail-panel"]'),
      );

      const entries = page.locator('[data-testid="activity-timeline-entry"]');
      const toggle = page.locator('[data-testid="activity-timeline-toggle"]');

      // Folded: exactly CollapseAfter rows, with a toggle offering the rest.
      await toggle.waitFor({ state: "visible" });

      // …then settle on the MERGED total before asserting on it.
      //
      // The theory this guards against (raised in review): TaskDetailPanel awaits its comment
      // load, but ActivityTimeline's event load is fire-and-forget, so there should be an
      // intermediate render with all 12 comments and `events` still empty — nothing
      // de-duplicated, 12 rows, which already exceeds CollapseAfter, so the toggle would be
      // visible in that state reading "Show all 12". `folded === 10` is true there too and
      // cannot separate the two states.
      //
      // MEASURED, and it did not reproduce. Sampling the toggle label every 5ms from before the
      // panel opens, across 12 runs — 6 warm, and 6 with a deliberate `await Task.Delay(1500)`
      // injected into LoadEventsAsync (confirmed to have reached the built DLL, not just the
      // source) — the first state ever present in the DOM was "Show all 13 | rows=10", at +0ms.
      // A 1500ms window would have been trivial to catch, so whatever Blazor is batching, the
      // un-merged render does not reach the browser here.
      //
      // The wait is kept regardless: it is a no-op in the state that was actually measured, and
      // one predicate evaluation is cheaper than diagnosing a load-dependent flake later. It is
      // NOT evidence of a bug that was observed — do not cite it as one.
      //
      // The timeout is swallowed on purpose: if the merge is genuinely broken the assertion
      // below reports WHAT it saw, which beats an opaque Playwright timeout.
      await page.waitForFunction(
        (n) => document.querySelector('[data-testid="activity-timeline-toggle"]')
          ?.innerText.includes(`Show all ${n}`) ?? false,
        expectedRows, { timeout: 10000 },
      ).catch(() => { /* fall through to the diagnostic assertion */ });

      const folded = await entries.count();
      assert(folded === 10, `expected the timeline folded to 10 rows, saw ${folded}`);
      assert(
        (await toggle.innerText()).includes(`Show all ${expectedRows}`),
        `expected "Show all ${expectedRows}" — ${seeded} comments must merge with their paired ` +
        `TaskCommentAdded events into ${seeded} rows, not ${seeded * 2}. Saw "${await toggle.innerText()}"`,
      );

      // Expand.
      await toggle.click();
      await page.waitForFunction(
        (n) => document.querySelectorAll('[data-testid="activity-timeline-entry"]').length === n,
        expectedRows, { timeout: 10000 },
      );

      // Now post a comment THROUGH THE UI. This is the RefreshToken bump — the same task, not a
      // different one.
      await page.locator('[data-testid="task-comment-input"]').fill(`typed while expanded ${stamp}`);
      await page.locator('[data-testid="task-comment-add-button"]').click();

      // The new row must appear (proves the refresh actually happened — without this the
      // assertion below could pass simply because nothing re-rendered at all)…
      await page.waitForFunction(
        (s) => [...document.querySelectorAll('[data-testid="activity-timeline-entry"]')]
          .some((e) => e.textContent.includes(`typed while expanded ${s}`)),
        stamp, { timeout: 10000 },
      );

      // …and the history must still be OPEN. On the broken build this is 10.
      const after = await entries.count();
      assert(
        after === expectedRows + 1,
        `history re-collapsed after a same-task refresh: expected ${expectedRows + 1} rows, saw ${after}`,
      );
      assert(
        (await toggle.innerText()).includes("Show fewer"),
        `toggle reverted to the folded label after a refresh: "${await toggle.innerText()}"`,
      );

      await api("DELETE", `/api/tasks/${timelineTask.id}`).catch(() => {});
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
    await api("DELETE", `/api/projects/${partialProj.id}`).catch(() => {});
    await api("DELETE", `/api/projects/${outlineProj.id}`).catch(() => {});
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
