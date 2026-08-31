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

let mcpRequestId = 1000;
async function nativeMcpTool(name, args) {
  const requestId = ++mcpRequestId;
  const res = await fetch(`${baseUrl}/mcp`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      accept: "application/json, text/event-stream",
      "mcp-protocol-version": "2025-06-18",
    },
    body: JSON.stringify({
      jsonrpc: "2.0",
      id: requestId,
      method: "tools/call",
      params: { name, arguments: args },
    }),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`MCP ${name} -> ${res.status}: ${text.slice(0, 300)}`);
  const contentType = res.headers.get("content-type") || "";
  let messages;
  if (contentType.includes("application/json")) {
    messages = [JSON.parse(text)];
  } else if (contentType.includes("text/event-stream")) {
    messages = text.split(/\r?\n/)
      .filter((line) => line.startsWith("data:"))
      .map((line) => JSON.parse(line.slice(5).trim()));
  } else {
    throw new Error(`MCP ${name} returned unsupported content type '${contentType}': ${text.slice(0, 300)}`);
  }
  const message = messages.find((candidate) => candidate.id === requestId);
  if (!message) throw new Error(`MCP ${name} returned no response for id ${requestId}: ${text.slice(0, 300)}`);
  if (message.error) throw new Error(`MCP ${name} failed: ${JSON.stringify(message.error)}`);
  const toolError = message.result?.content?.find((item) => item.type === "text" && item.text)?.text;
  if (message.result?.isError) throw new Error(`MCP ${name} failed: ${toolError || "unknown tool error"}`);
  return message.result;
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

// The API serialises stored-UTC DateTimes with Kind=Unspecified, i.e. with NO trailing "Z" — and
// `new Date("2026-07-12T17:19:00")` reads a zoneless string as LOCAL. Pin the zone before
// comparing, or a clock assertion quietly compares local against local and can never fail.
// (#337 owns the wire format itself; this is the reader side.)
function utcIso(value) {
  const s = String(value);
  return /(Z|[+-]\d{2}:?\d{2})$/.test(s) ? s : `${s}Z`;
}

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

    // ── Decision & Memory Map (#496): real REST writes -> real Blazor projection ──
    // The graph service and MCP relation path have real-SQLite coverage in MemoryServiceTests and
    // McpKnowledgeGraphToolsTests. This scenario owns the missing UI boundary: records written through
    // the public API must appear in the interactive map, classify by both enum and free-form tags,
    // remain usable as a keyboard-native list, and stay reachable in a narrow viewport. The database
    // is the disposable DB created by startApp, so these records need no production cleanup.
    await test("Decision map renders explicit agent records with filters and list fallback (#496)", async () => {
      const scopeKey = `ui-decision-map-${stamp}`;
      const context = await api("POST", "/api/memory", {
        content: `Context ${stamp}: the graph backend already exists`,
        type: "Semantic",
        scope: "Agent",
        scopeKey,
        tags: "context",
        source: "ui-e2e",
      });
      const decision = await api("POST", "/api/memory", {
        content: `Decision ${stamp}: reuse the existing MCP graph`,
        type: "Decision",
        scope: "Agent",
        scopeKey,
        tags: "OPEN_QUESTION, chosen",
        source: "ui-e2e",
        confidence: 0.9,
      });
      const evidence = await api("POST", "/api/memory", {
        content: `Evidence ${stamp}: ReadGraphAsync is already shipped`,
        type: "Semantic",
        scope: "Agent",
        scopeKey,
        tags: "EVIDENCE",
        source: "ui-e2e",
      });
      await nativeMcpTool("create_relations", {
        scopeKey,
        actor: "ui-e2e",
        relations: [
          { from: context.content, to: decision.content, relationType: "frames" },
          { from: evidence.content, to: decision.content, relationType: "supports" },
        ],
      });

      await page.goto(`${baseUrl}/memory`);
      await page.locator('[data-testid="memory-map-namespace"]').waitFor({ state: "visible" });
      await page.locator('[data-testid="memory-map-namespace"]').fill(scopeKey);
      await clickUntil(
        () => page.getByRole("button", { name: "Load namespace" }).click(),
        page.locator(`[data-testid="decision-map-node-${decision.id}"]`),
      );
      assert(new URL(page.url()).searchParams.get("scopeKey") === scopeKey,
        `loaded namespace was not preserved in the navigable URL: ${page.url()}`);

      await page.locator(`[data-testid="decision-map-node-${context.id}"]`).waitFor({ state: "visible" });
      await page.locator(`[data-testid="decision-map-node-${evidence.id}"]`).waitFor({ state: "visible" });
      assert(await page.locator('[data-testid^="decision-edge-"]').count() === 2,
        "real MCP relations did not render as two directed map edges");
      assert(await page.locator('[data-testid^="decision-edge-"][marker-end="url(#decision-map-arrow)"]').count() === 2,
        "decision map relationships did not retain their directed arrow markers");
      assert(await page.locator(".lane-decision .node-kind").filter({ hasText: "Decisions" }).count() >= 1,
        "MemoryType.Decision did not render in the Decisions lane");
      assert(await page.locator(".lane-evidence .node-kind").filter({ hasText: "Evidence" }).count() >= 1,
        "case-insensitive EVIDENCE tag did not render in the Evidence lane");
      await page.locator(`[data-testid="decision-map-node-${decision.id}"]`).click();
      await page.locator('[data-testid="decision-node-detail"]').getByText("0.9", { exact: true }).waitFor({ state: "visible" });
      const relationshipText = await page.locator('[data-testid="decision-node-detail"] .relationship-list').innerText();
      assert(relationshipText.includes(context.content) && relationshipText.includes("frames"),
        `incoming context relationship was not readable: ${relationshipText}`);
      assert(relationshipText.includes(evidence.content) && relationshipText.includes("supports"),
        `incoming evidence relationship was not readable: ${relationshipText}`);

      const search = page.locator('[data-testid="memory-map-search"]');
      await search.fill("ReadGraphAsync");
      await page.locator(`[data-testid="decision-map-node-${evidence.id}"]`).waitFor({ state: "visible" });
      await page.locator(`[data-testid="decision-map-node-${decision.id}"]`).waitFor({ state: "detached" });
      assert(await page.locator(`[data-testid="decision-map-node-${decision.id}"]`).count() === 0,
        "search filter left a non-matching decision node visible");
      await search.fill("");

      await page.locator('[data-testid="decision-graph-list-toggle"]').click();
      await page.locator('[data-testid="decision-graph-list"]').waitFor({ state: "visible" });
      const listToggle = page.locator('[data-testid="decision-graph-list-toggle"]');
      const contextListNode = page.locator(`[data-testid="decision-list-node-${context.id}"]`);
      await listToggle.focus();
      await page.keyboard.press("Tab");
      const keyboardFocus = await contextListNode.evaluate((el) => {
        const style = getComputedStyle(el);
        return {
          active: el === document.activeElement,
          boxShadow: style.boxShadow,
          outlineStyle: style.outlineStyle,
          outlineWidth: style.outlineWidth,
        };
      });
      assert(keyboardFocus.active,
        `Tab did not reach the first list record: ${JSON.stringify(keyboardFocus)}`);
      assert(keyboardFocus.boxShadow !== "none" || (keyboardFocus.outlineStyle !== "none" && keyboardFocus.outlineWidth !== "0px"),
        `keyboard focus had no visible treatment: ${JSON.stringify(keyboardFocus)}`);
      await contextListNode.press("Enter");
      await page.locator('[data-testid="decision-node-detail"] .lead').getByText(context.content, { exact: true }).waitFor({ state: "visible" });
      assert((await contextListNode.getAttribute("aria-pressed"))?.toLowerCase() === "true",
        "Enter did not select the focused list record");

      await page.locator('[data-testid="decision-graph-map-toggle"]').click();
      const savedViewport = page.viewportSize();
      try {
        await page.setViewportSize({ width: 390, height: 844 });
        const overflow = await page.locator('[data-testid="decision-graph-map"]').evaluate((el) => {
          el.scrollLeft = 100;
          return {
            client: el.clientWidth,
            clientHeight: el.clientHeight,
            scroll: el.scrollWidth,
            scrollHeight: el.scrollHeight,
            maxHeight: getComputedStyle(el).maxHeight,
            moved: el.scrollLeft,
            visible: el.getBoundingClientRect().width > 0,
          };
        });
        assert(overflow.visible && overflow.scroll > overflow.client && overflow.moved > 0,
          `narrow map is clipped without a scroll region: ${JSON.stringify(overflow)}`);
        assert(overflow.maxHeight !== "none",
          `map has no viewport-bounded vertical scroll region: ${JSON.stringify(overflow)}`);
      } finally {
        if (savedViewport) await page.setViewportSize(savedViewport);
      }

      await page.locator('[data-testid="nav-memory"]').waitFor({ state: "visible" });
      await page.locator('[data-testid="memory-map-privacy-note"]').getByText("Explicit records only.", { exact: true })
        .waitFor({ state: "visible" });
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

    // ── #112: the board filter bar ───────────────────────────────────────────────────────────
    // The scenario above STAGED a partial board by deleting DOM nodes. This one is the real thing:
    // ProjectView narrows its own list and hands the board a genuine subset, so these scenarios are
    // what fail if `Items="allTasks"` is ever restored at the call site.
    //
    // THE SEED SHAPE IS THE TEST. Two hidden cards, and their POSITIONS are what give the drag
    // scenario below its discriminating power (see the long note there). One hidden card in the
    // middle is NOT enough: with persisted A,B,C*,D,E and rendered A,B,D,E, dragging E before B
    // lands A,E,B,C,D under an identity anchor AND under a rendered-index anchor — the two readings
    // agree whenever every hidden card sits AFTER the insertion point, so such a seed passes either
    // way and proves nothing. Adding X* early is what pulls them apart. (Reviewer, PR #362.)
    //
    //   persisted:  A(0)  X*(1)  B(2)  C*(3)  D(4)  E(5)      * = hidden by `q=Keep`
    //   rendered:   A     B      D     E
    //
    // Assignees and the claim are load-bearing too, not decoration: they are what lets the wiring
    // scenario prove the assignee box writes `assignee=` and the claimed-by box writes `claimedBy=`.
    const filterProj = await api("POST", "/api/projects", { name: `E2E Filter ${stamp}` });
    const filterFeature = await api("POST", `/api/projects/${filterProj.id}/features`, { name: "E2E Filter Feature" });
    const filterClaimant = `e2e-agent-${stamp}`;
    const filterTasks = [];
    for (const [label, priority, status, assignee] of [
      ["Keep A", "High", "Todo", "alice"],
      ["Skip X", "Low", "Todo", "alice"],
      ["Keep B", "Low", "Todo", "bob"],
      ["Skip C", "High", "Todo", "alice"],
      ["Keep D", "Low", "Todo", "bob"],
      ["Keep E", "High", "Todo", "carol"],
    ]) {
      filterTasks.push(await api("POST", `/api/features/${filterFeature.id}/tasks`, {
        name: `E2E Filter ${label} ${stamp}`,
        description: label.startsWith("Keep") ? "matching description" : "excluded description",
        priority,
        status,
        assignee,
        sortOrder: filterTasks.length,
      }));
    }
    const [keepA, skipX, keepB, skipC, keepD, keepE] = filterTasks;
    // A second status, so "≥2 statuses" is real and the Done/hide-done clause has something to bite.
    const filterDone = await api("POST", `/api/features/${filterFeature.id}/tasks`, {
      name: `E2E Filter Keep F ${stamp}`, description: "matching description",
      priority: "High", status: "Done", sortOrder: 10, assignee: "carol",
    });
    // Exactly ONE claimed task, so `claimedBy` discriminates down to a single card.
    await api("POST", `/api/tasks/${keepA.id}/claim`, { actor: filterClaimant });

    const filterAllIds = [...filterTasks, filterDone].map((task) => task.id);
    const filterKeepIds = [keepA.id, keepB.id, keepD.id, keepE.id, filterDone.id];
    const filterAssigneeAliceIds = [keepA.id, skipX.id, skipC.id];
    const sortedIds = (ids) => ids.slice().sort((a, b) => a - b).join(",");

    const filterUrl = (query = "") => `${baseUrl}/project/${filterProj.id}${query}`;
    const renderedBoardIds = (page) => page.$$eval(
      '[data-testid="kanban-board"] [data-task-id]',
      (els) => els.map((el) => Number(el.getAttribute("data-task-id"))),
    );
    // Per column: the count badge (computed by KanbanColumn from the MODEL it was handed) beside the
    // ids actually rendered (put there by the Blazor DIFF). Under a filter those two can disagree
    // only if the page filtered the render but not the list, which is exactly the bug worth naming.
    const readBadges = (page) => page.evaluate((statuses) => statuses.map((status) => {
      const column = document.querySelector(`[data-testid="board-column-${status}"]`);
      const badge = column?.querySelector("h3 .badge");
      return {
        status,
        badge: Number((badge?.textContent ?? "").replace(/[^0-9]/g, "")),
        cards: column?.querySelectorAll("[data-task-id]").length ?? -1,
      };
    }), TASK_COLUMNS);

    await test("Board filter narrows the board from the URL and survives a reload", async () => {
      // Deep link, not a click: this is simultaneously the "a shared link works" case and the
      // "filter state round-trips through the query string" case.
      await page.goto(filterUrl("?q=Keep"));
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      for (const task of [keepA, keepB, keepD, keepE]) {
        await page.locator(`[data-testid="board-card-${task.id}"]`).waitFor({ state: "visible" });
      }

      const shown = await renderedBoardIds(page);
      assert(sortedIds(shown) === sortedIds(filterKeepIds),
        `filtered board must render only matching cards — expected [${filterKeepIds}], got [${shown}]`);
      for (const hidden of [skipX, skipC]) {
        assert(!shown.includes(hidden.id), `the non-matching card ${hidden.id} is still rendered`);
      }

      const badges = await readBadges(page);
      assert(badges.every((row) => row.badge === row.cards),
        `each column's count badge must equal its rendered card count: ${JSON.stringify(badges)}`);
      // …and the badges must sum to the FILTERED total, not the project total. Without this the
      // per-column equality above also holds for a board that rendered nothing at all.
      const badgeTotal = badges.reduce((sum, row) => sum + row.badge, 0);
      assert(badgeTotal === filterKeepIds.length,
        `count badges sum to ${badgeTotal}, expected ${filterKeepIds.length} (of ${filterAllIds.length} total)`);

      // The summary line is the only place the user is told how much they are NOT seeing.
      const summary = (await page.locator('[data-testid="board-filter-summary"]').textContent()).trim();
      assert(summary === `Showing ${filterKeepIds.length} of ${filterAllIds.length} tasks`,
        `filter summary reads "${summary}", expected "Showing ${filterKeepIds.length} of ${filterAllIds.length} tasks"`);

      // The search box itself must be repopulated too — a board that is filtered while the control
      // reads empty tells the user their view is unfiltered, which is worse than not filtering.
      assert(await page.locator('[data-testid="board-filter-search"]').inputValue() === "Keep",
        "the search box did not restore its value from the URL");

      await page.reload();
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      await page.locator(`[data-testid="board-card-${keepA.id}"]`).waitFor({ state: "visible" });
      const afterReload = await renderedBoardIds(page);
      assert(sortedIds(afterReload) === sortedIds(filterKeepIds),
        `the filter did not survive a reload — expected [${filterKeepIds}], got [${afterReload}]`);
    });

    await test("Board filter clauses combine, and Clear resets both the board and the URL", async () => {
      await page.goto(filterUrl());
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      await page.locator(`[data-testid="board-card-${skipC.id}"]`).waitFor({ state: "visible" });
      // A rendered card only proves the STATIC prerender landed. Every control below is
      // `@oninput`/`@onchange`, so a keystroke sent before the interactive circuit is live is
      // swallowed and the test reports "the filter does nothing" — see clickUntil's note on the
      // same race. Sortable is bound from OnAfterRenderAsync, i.e. strictly after the circuit is
      // live, so it is the cheapest honest readiness signal the page exposes.
      await waitForSortableArmed(page, "Todo");

      // Type into the real control (not the URL) so the oninput -> filter -> URL path is exercised.
      // Settle on each control's own query key before touching the next one: three writes to the
      // same URL in flight at once is a race the test would report as "the priority chip does
      // nothing", which is a lie about which half is broken.
      const waitForQuery = async (key, value) => {
        try {
          await page.waitForFunction(
            ({ k, v }) => new URL(window.location.href).searchParams.get(k) === v,
            { k: key, v: value }, { timeout: 10000 });
        } catch {
          throw new Error(`the "${key}" control never reached the URL — expected ${key}=${value}, URL is ${page.url()}`);
        }
      };

      const waitForRendered = async (expectedIds, what) => {
        try {
          await page.waitForFunction(
            (ids) => {
              const rendered = [...document.querySelectorAll('[data-testid="kanban-board"] [data-task-id]')]
                .map((el) => Number(el.getAttribute("data-task-id"))).sort((a, b) => a - b);
              return rendered.join(",") === ids.join(",");
            },
            expectedIds.slice().sort((a, b) => a - b),
            { timeout: 10000 },
          );
        } catch {
          assert(false, `${what} — expected [${expectedIds}], got [${await renderedBoardIds(page)}]`);
        }
      };

      await page.locator('[data-testid="board-filter-search"]').fill("Keep");
      await waitForQuery("q", "Keep");
      // Priority chips are btn-check inputs; the LABEL is the hit target, as it is for a user.
      await page.locator('[data-testid="board-filter-priority-High"]').click();
      await waitForQuery("priority", "High");
      await page.locator('[data-testid="board-filter-hide-done"]').check();
      await waitForQuery("hideDone", "true");
      // Keep A and Keep E are the only High + non-Done + "Keep" tasks.
      await waitForRendered([keepA.id, keepE.id], "search + priority + hide-done must AND together");

      // Fourth clause, and the one that proves the ASSIGNEE box is wired to the assignee field:
      // alice narrows {A, E} to {A} (E is carol's). Swapping this handler for the claimed-by one in
      // ProjectView compiles and passes every other assertion in this file — the URL check below is
      // what refuses it, because a miswired box writes its neighbour's query key.
      await page.locator('[data-testid="board-filter-assignee"]').fill("alice");
      await waitForQuery("assignee", "alice");
      await waitForRendered([keepA.id], "adding assignee=alice must narrow the AND further");
      assert(!page.url().includes("claimedBy="),
        `the assignee box wrote the claimed-by query key: ${page.url()}`);

      const url = new URL(page.url());
      assert(url.searchParams.get("q") === "Keep", `URL lost the search term: ${page.url()}`);
      assert(url.searchParams.get("priority") === "High", `URL lost the priority chip: ${page.url()}`);
      assert(url.searchParams.get("hideDone") === "true", `URL lost hide-done: ${page.url()}`);

      // Every column is empty except Backlog/Todo — those must say "no tasks MATCH", never the
      // "no tasks in Review yet, add one" placeholder, which under a filter is a lie. Asserted
      // through the dedicated testid, so the filtered placeholder is identified structurally rather
      // than by matching prose that a copy edit would silently break.
      const noMatchTestids = await page.evaluate(() =>
        [...document.querySelectorAll('[data-testid$="-no-match"]')].map((el) => el.getAttribute("data-testid")));
      const emptyStates = await page.evaluate(() =>
        [...document.querySelectorAll(".kanban-column-empty")].map((el) => el.textContent.trim()));
      assert(emptyStates.length > 0, "expected at least one empty column while filtered");
      assert(noMatchTestids.length === emptyStates.length,
        `every empty column must use the filtered placeholder — ${noMatchTestids.length} of ${emptyStates.length} did: ${JSON.stringify(emptyStates)}`);
      assert(emptyStates.every((text) => text.includes("No tasks match these filters")),
        `a filtered-out column showed the wrong empty state: ${JSON.stringify(emptyStates)}`);

      await page.locator('[data-testid="board-filter-clear"]').click();
      await page.locator(`[data-testid="board-card-${skipC.id}"]`).waitFor({ state: "visible" });
      const cleared = await renderedBoardIds(page);
      assert(cleared.length === filterAllIds.length,
        `Clear must restore every card — expected ${filterAllIds.length}, got ${cleared.length}`);
      // The URL write is debounced (ProjectView.UrlSyncDebounce), so poll rather than read once.
      await page.waitForFunction(() => !window.location.href.includes("?"), null, { timeout: 10000 })
        .catch(() => assert(false, `Clear must empty the query string too, got ${page.url()}`));
      for (const box of ["search", "assignee", "claimed-by"]) {
        assert(await page.locator(`[data-testid="board-filter-${box}"]`).inputValue() === "",
          `Clear left the ${box} box populated`);
      }
    });

    // Found by review (CodeRabbit, PR #362) and REPRODUCED before it was fixed: with the search box
    // rendering `value="@Filter.Search"`, typing "KeepAlphaBetaGamma" with no inter-key delay left
    // "KepAlphaBetaGmma" in the box — two characters silently eaten — and a caret placed mid-word
    // jumped to the end. On a Server circuit the keystroke round-trips to the server, and the
    // re-render it triggers carries a value that is already stale by the time it lands; the diff
    // writes it back over the newer characters. The fix is that BoardFilterBar renders its text
    // boxes from LOCAL fields that typing never touches (see the warning at the top of that file).
    //
    // Nothing else in this suite could catch it: every other filter assertion uses `fill()`, which
    // sets the value in ONE event and so never races a re-render. This is the only scenario that
    // types character by character, which is the only way a human uses the control.
    await test("Fast typing in the filter box keeps every character and holds the caret", async () => {
      await page.goto(filterUrl());
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      await waitForSortableArmed(page, "Todo");

      const box = page.locator('[data-testid="board-filter-search"]');
      await box.click();
      await page.keyboard.type("KeepAlphaBetaGamma", { delay: 0 });
      // The filter is debounced into the URL, so wait for the write rather than reading immediately.
      await page.waitForFunction(() => window.location.href.includes("q="), null, { timeout: 10000 });
      await page.waitForTimeout(600);
      const burst = await box.inputValue();
      assert(burst === "KeepAlphaBetaGamma",
        `fast typing lost characters: box reads "${burst}", expected "KeepAlphaBetaGamma"`);

      // Now type into the MIDDLE. A control that rewrites `value` on re-render snaps the caret to
      // the end, so this would produce "abcdefXYZ" with selectionStart 9 instead of "abcXYZdef"/6.
      await box.fill("abcdef");
      await page.waitForTimeout(600);
      await page.evaluate(() => {
        const el = document.querySelector('[data-testid="board-filter-search"]');
        el.focus();
        el.setSelectionRange(3, 3);
      });
      await page.keyboard.type("XYZ", { delay: 0 });
      await page.waitForTimeout(600);
      const caret = await page.evaluate(() => {
        const el = document.querySelector('[data-testid="board-filter-search"]');
        return { value: el.value, selectionStart: el.selectionStart };
      });
      assert(caret.value === "abcXYZdef" && caret.selectionStart === 6,
        `typing mid-word moved the caret or reordered the text: value="${caret.value}" ` +
        `selectionStart=${caret.selectionStart}, expected "abcXYZdef"/6`);

      // Clear must still be able to empty a box the bar otherwise never rewrites — the whole point
      // of the reset token. Without it, "Clear" would leave the typed text sitting in the input.
      await page.locator('[data-testid="board-filter-clear"]').click();
      await page.waitForFunction(
        () => document.querySelector('[data-testid="board-filter-search"]').value === "",
        null, { timeout: 10000 })
        .catch(async () => assert(false,
          `Clear did not empty the search box — it still reads "${await box.inputValue()}"`));
    });

    // S2 (reviewer, PR #362): assignee and claimed-by are two near-identical string clauses over two
    // different columns, and the predicate-level guard for confusing them
    // (TaskFilterTests.ClaimedByFilter_DoesNotReadTheAssigneeField) says nothing about the WIRING.
    // `OnClaimedByChanged="OnAssigneeChanged"` in ProjectView compiles. This is the test that refuses it.
    await test("The claimed-by box filters by claim, not by assignee", async () => {
      await page.goto(filterUrl());
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      await waitForSortableArmed(page, "Todo");

      await page.locator('[data-testid="board-filter-claimed-by"]').fill(filterClaimant);
      await page.waitForFunction(
        (v) => new URL(window.location.href).searchParams.get("claimedBy") === v,
        filterClaimant, { timeout: 10000 })
        .catch(() => assert(false, `the claimed-by box never wrote claimedBy= to the URL: ${page.url()}`));
      assert(!page.url().includes("assignee="),
        `the claimed-by box wrote the assignee query key: ${page.url()}`);

      // Only Keep A is claimed, and it is assigned to alice — who also owns Skip X and Skip C. So a
      // claimed-by clause reading the ASSIGNEE column would render three cards here, not one.
      await page.waitForFunction((id) => {
        const rendered = [...document.querySelectorAll('[data-testid="kanban-board"] [data-task-id]')]
          .map((el) => Number(el.getAttribute("data-task-id")));
        return rendered.length === 1 && rendered[0] === id;
      }, keepA.id, { timeout: 10000 })
        .catch(async () => assert(false,
          `claimedBy=${filterClaimant} must render only the claimed card ${keepA.id} — got [${await renderedBoardIds(page)}] ` +
          `(alice's three cards are [${filterAssigneeAliceIds}], which is what an assignee-reading clause would show)`));

      // And the same state as a shared link.
      await page.goto(filterUrl(`?claimedBy=${encodeURIComponent(filterClaimant)}`));
      await page.locator(`[data-testid="board-card-${keepA.id}"]`).waitFor({ state: "visible" });
      const viaLink = await renderedBoardIds(page);
      assert(viaLink.join(",") === String(keepA.id),
        `?claimedBy= deep link must render only [${keepA.id}], got [${viaLink}]`);
    });

    // AC 5, and the only place filtering can CORRUPT data rather than merely mis-display it.
    //
    //   persisted:  A(0)  X*(1)  B(2)  C*(3)  D(4)  E(5)     * hidden by `q=Keep`
    //   rendered:   A     B      D     E
    //
    // Drag E onto B's top edge: "put E before B". The third OnCardMoved argument is the next
    // RENDERED card's identity — B — and MoveOnBoardAsync resolves it against the complete persisted
    // column, so E lands immediately before B, i.e. AFTER the hidden X:
    //
    //   identity anchor  ->  A,  X,  E,  B,  C,  D     <- the correct answer, asserted below
    //   rendered index   ->  A,  E,  X,  B,  C,  D     <- what "insert at visible index 1" produces
    //
    // Those two orders DIFFER, which is the entire point of the seed, and the test asserts the first
    // and explicitly refuses the second. The naive seed — one hidden card, in the middle — cannot do
    // this: with A,B,C*,D,E both readings yield A,E,B,C,D, because they only diverge when a hidden
    // card sits BEFORE the insertion point. A guard that passes under the bug it names is worse than
    // no guard, so X exists purely to move one hidden card in front of the anchor. (Reviewer, PR #362.)
    await test("Dragging under an active filter persists the correct position across a hidden card", async () => {
      await page.goto(filterUrl("?q=Keep"));
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      for (const task of [keepA, keepB, keepD, keepE]) {
        await page.locator(`[data-testid="board-card-${task.id}"]`).waitFor({ state: "visible" });
      }
      await waitForSortableArmed(page, "Todo");

      const seedOrder = [keepA.id, skipX.id, keepB.id, skipC.id, keepD.id, keepE.id];
      const beforeTasks = (await api("GET", `/api/projects/${filterProj.id}/board`)).Todo;
      const beforeIds = beforeTasks.map((task) => task.id);
      const beforeRanks = new Map(beforeTasks.map((task) => [task.id, task.sortOrder]));
      assert(beforeIds.join(",") === seedOrder.join(","),
        `seed: expected persisted Todo order [${seedOrder}], got [${beforeIds}]`);

      // Load-bearing precondition: both hidden cards must actually be absent from the DOM, or the
      // drag below is an ordinary unfiltered drag and proves nothing about filtering.
      const renderedInColumn = await page.$$eval(
        '[data-testid="board-column-Todo-cards"] [data-task-id]',
        (els) => els.map((el) => Number(el.getAttribute("data-task-id"))),
      );
      const expectedRendered = [keepA.id, keepB.id, keepD.id, keepE.id];
      assert(renderedInColumn.join(",") === expectedRendered.join(","),
        `the filtered Todo column must render exactly [A,B,D,E] = [${expectedRendered}], got [${renderedInColumn}]`);

      await dragCardBefore(page,
        `[data-testid="board-card-${keepE.id}"]`,
        `[data-testid="board-card-${keepB.id}"]`);

      const expectedOrder = [keepA.id, skipX.id, keepE.id, keepB.id, skipC.id, keepD.id];
      const renderedIndexOrder = [keepA.id, keepE.id, skipX.id, keepB.id, skipC.id, keepD.id];
      const afterTasks = await waitForProjectBoardOrder(filterProj.id, "Todo", expectedOrder);
      const afterIds = afterTasks.map((task) => task.id);
      // Say the wrong answer out loud. waitForProjectBoardOrder already failed if the order is not
      // `expectedOrder`, but naming the rendered-index order here is what stops a future edit from
      // quietly weakening the seed back into the shape where the two coincide.
      assert(afterIds.join(",") !== renderedIndexOrder.join(","),
        `the drag anchored on the RENDERED INDEX, not the persisted identity: got [${afterIds}]`);

      for (const task of afterTasks.filter((task) => task.id !== keepE.id)) {
        assert(task.sortOrder === beforeRanks.get(task.id),
          `bystander ${task.id} SortOrder changed from ${beforeRanks.get(task.id)} to ${task.sortOrder}`);
      }
      // The moved card must take a rank strictly between the HIDDEN X and B — proof the server
      // placed it against persisted neighbours, one of which the browser never rendered.
      const movedAfter = afterTasks.find((task) => task.id === keepE.id);
      assert(movedAfter.sortOrder > beforeRanks.get(skipX.id) && movedAfter.sortOrder < beforeRanks.get(keepB.id),
        `moved card should rank between hidden X (${beforeRanks.get(skipX.id)}) and B ` +
        `(${beforeRanks.get(keepB.id)}), got ${movedAfter.sortOrder}`);

      // And the human's own confirmation: clear the filter and read the order off the board.
      await page.locator('[data-testid="board-filter-clear"]').click();
      await page.locator(`[data-testid="board-card-${skipC.id}"]`).waitFor({ state: "visible" });
      await page.waitForFunction((ids) => {
        const rendered = [...document.querySelectorAll('[data-testid="board-column-Todo-cards"] [data-task-id]')]
          .map((el) => Number(el.getAttribute("data-task-id")));
        return rendered.join(",") === ids.join(",");
      }, expectedOrder, { timeout: 10000 });
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

    // ── Regression guards (#322 phase 3): the two visible board defects ───────────────────────
    // Both are LAYOUT bugs, so both are asserted on measured geometry in a real browser. Neither
    // can be caught server-side, and neither shows up in a DOM/text assertion: the markup was
    // always correct, it was the boxes that were wrong.
    const visProj = await api("POST", "/api/projects", { name: `E2E Visual ${stamp}` });
    // A deliberately long feature name: the ONLY input that exercises the ellipsis branch.
    const visLongFeature = await api("POST", `/api/projects/${visProj.id}/features`,
      { name: "Authentication & Session Lifecycle Management" });
    const visShortFeature = await api("POST", `/api/projects/${visProj.id}/features`,
      { name: "Dashboard" });

    await test("Board card: a feature label is never shaved mid-glyph, and a long one really ellipsises (#322)", async () => {
      // The defect this guards is SILENT. `.board-card-meta-label` used to be sized to exactly its
      // own text (measured: box 54.797px === text 54.797px), so:
      //   * the italic final glyph's INK leaned past the clip edge and was shaved — "Dashboard"
      //     rendered as "Dashboarc", which reads as a different WORD, not as a truncation; and
      //   * `text-overflow: ellipsis` could never fire, because nothing overflowed (scrollWidth
      //     === clientWidth), so there was no "…" to signal the loss.
      // Note what is NOT asserted: `scrollWidth > clientWidth` on a SHORT label. That is the
      // condition the old markup already satisfied only for long names, and asserting it for short
      // ones would demand the bug. The load-bearing measurement is the final glyph's HEADROOM
      // against the clip edge, which is the thing the rasteriser actually consumes.
      await api("POST", `/api/features/${visShortFeature.id}/tasks`,
        { name: "Short label card", status: "Todo", priority: "Medium" });
      await api("POST", `/api/features/${visLongFeature.id}/tasks`,
        { name: "Long label card", status: "Todo", priority: "Critical" });
      // AC4 names TWO viewports (1440x900 and 390x844) and the suite's default is 1280x720, so a
      // single-viewport run silently drops half the criterion — and the halves genuinely differ:
      // at the 180px column floor "Dashboard" DOES overflow and renders "Dashbo…", so the
      // "renders the whole word" claim is width-dependent and has to be checked where it is made.
      const restoreVis = page.viewportSize();
      try {
        for (const [vw, vh] of [[1440, 900], [390, 844]]) {
          await page.setViewportSize({ width: vw, height: vh });
          await page.goto(`${baseUrl}/project/${visProj.id}`);
          await page.locator('[data-testid="board-column-Todo-cards"] .board-card-meta-label').first()
            .waitFor({ state: "visible" });

          // Query and measure in one browser task. `$$eval` first materialises an Element[] handle
          // over the Playwright protocol and evaluates its callback afterward; InteractiveServer
          // hydration can replace the prerendered card nodes in that gap, leaving detached elements
          // whose computed styles are empty and whose geometry serialises as NaN. (PR #497.)
          const labels = await page.evaluate(() =>
            [...document.querySelectorAll(".board-card-meta-label")].map((el) => {
            // Locate the text node EXPLICITLY rather than assuming `firstChild` is one. If the label's
            // markup ever gains a wrapper (e.g. the text moves inside a <span> when CardMeta is
            // extracted in a later phase), `firstChild` becomes an Element and `Range#setStart` throws
            // IndexSizeError — the suite would then die with an opaque DOM exception instead of saying
            // what actually broke. Offsets are taken from the TEXT NODE's own length, not
            // `el.textContent.length`, which counts every descendant and would overrun a partial node.
            // (Copilot, PR #335.)
            const node = [...el.childNodes].find((n) => n.nodeType === Node.TEXT_NODE && n.nodeValue.trim() !== "");
            if (!node) {
              return { structureError: `no direct text node under .board-card-meta-label (found: ` +
                `${[...el.childNodes].map((n) => n.nodeName).join(",") || "nothing"}) — the guard needs updating` };
            }
            const text = node.nodeValue;
            // The FINAL glyph's own box, versus the box that clips it.
            const last = document.createRange();
            last.setStart(node, text.length - 1);
            last.setEnd(node, text.length);
            const cs = getComputedStyle(el);
            const r = el.getBoundingClientRect();
            // overflow:hidden clips at the PADDING box, so the trailing padding is real headroom.
            const clipRight = r.right - parseFloat(cs.borderRightWidth);
            const badge = el.parentElement.querySelector(".badge").getBoundingClientRect();
            return {
              text: text.trim(),
              headroom: clipRight - last.getBoundingClientRect().right,
              overflows: el.scrollWidth > el.clientWidth,
              gutterToBadge: r.left - badge.right,
              // Resolve the DECLARED side bearing and the root size, so the assertions below can be
              // stated against what the stylesheet says rather than against a copied-out constant.
              bearingPx: parseFloat(cs.paddingInlineEnd),
              fontSizePx: parseFloat(cs.fontSize),
              fontStyle: cs.fontStyle,
              rootPx: parseFloat(getComputedStyle(document.documentElement).fontSize),
              // The independently MEASURED constraint the bearing exists to clear: how far this
              // italic font's final glyph paints PAST its own advance width. Canvas reports both
              // (`actualBoundingBoxRight` is ink, `width` is advance), so the floor is derived from
              // the font actually rendering on screen rather than copied from the design spec.
              // Measured here: 0.771px for "Dashboard" at italic 11.2px Helvetica Neue, 1.400px for
              // a "Wy" ending. This is exactly the quantity `Range`/`scrollWidth` cannot see, which
              // is why the original bug reported "nothing is overflowing" while shaving a glyph.
              inkOverhangPx: (() => {
                const probe = document.createElement("canvas").getContext("2d");
                probe.font = `${cs.fontStyle} ${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
                const m = probe.measureText(text);
                // NOT clamped to >= 0. The SAME string upright measures -0.2289px, and that
                // negative value is the only tell that the page is not in the state this guard
                // thinks it is; clamping would erase it. The precondition below rejects it.
                return m.actualBoundingBoxRight - m.width;
              })(),
            };
          }));
          assert(labels.length >= 2, `expected both seeded cards to render a feature label, saw ${labels.length}`);
          // Surface a markup-shape change as a readable failure, not an IndexSizeError. (Copilot.)
          const broken = labels.filter((l) => l.structureError);
          assert(broken.length === 0, `label geometry could not be measured: ${broken[0]?.structureError}`);

          const short = labels.find((l) => l.text === "Dashboard");
          const long = labels.find((l) => l.text.startsWith("Authentication & Session"));
          assert(short, `short-label card not rendered; saw ${JSON.stringify(labels.map((l) => l.text))}`);
          assert(long, `long-label card not rendered; saw ${JSON.stringify(labels.map((l) => l.text))}`);

          // (a) A label that FITS must clear its clip edge by the bearing the stylesheet DECLARES,
          //     and (b) that declared bearing must clear the ink overhang actually MEASURED for this
          //     font. Two assertions, because either one alone is worthless here:
          //
          //   * (a) alone is a TAUTOLOGY. For a non-overlapping right-aligned label,
          //     `clipRight - lastGlyph.right` is arithmetically EQUAL to `padding-inline-end` — so
          //     comparing headroom against `bearingPx` compares the padding with itself. Verified,
          //     not assumed: written that way, weakening the token to `.02em` leaves the suite 27/27
          //     GREEN with the glyph shaved again. (CodeRabbit proposed exactly this; it removes the
          //     literal and the guard's power together.)
          //   * (b) alone would not notice CSS failing to apply the padding at all.
          //
          // Neither hard-codes `0.15`. The old form did, which is the same guard-the-exemplar bug
          // this file fixes twice elsewhere (`0.25 * rootPx` for the gutter, `--kf-board-drop-floor`
          // read at runtime for the floor) — CodeRabbit was right that the literal had to go; it is
          // the REPLACEMENT that has to be an independently measured quantity, not the declaration.
          // PRECONDITION for (b): the label must actually be rendering ITALIC. Measured, this
          // font overhangs +0.7711px italic but -0.2289px UPRIGHT for the same string -- so if the
          // page loses `fst-italic` (Bootstrap's, not ours), the overhang goes negative and (b)
          // degrades to `0 >= -0.2289` => true, passing on a completely unstyled page.
          //
          // Not hypothetical: it is the shape of the .NET 10 MapStaticAssets trap, where a
          // stylesheet can return 200 with a body that parses to zero rules. Lose app.css ALONE
          // and Bootstrap still supplies `fst-italic`, so the overhang stays +0.7711 and (b) bites
          // correctly (`0 >= 0.7711` is false). Lose EVERY stylesheet and it does not. The suite
          // still goes red either way -- `--kf-board-drop-floor` is then unresolved and the
          // drop-floor guard's `Math.abs(NaN - x) <= 1` fails unconditionally -- but it would go
          // red for the wrong reason while this assertion sat blind. (Reviewer, PR #335.)
          assert(short.inkOverhangPx > 0,
            `the feature label measures ${short.inkOverhangPx.toFixed(4)}px of ink past its advance ` +
            `width at ${vw}x${vh} — non-positive means it is not rendering italic (computed ` +
            `font-style: ${short.fontStyle}), so the page is not in the state this guard measures. ` +
            `Suspect a stylesheet that loaded but parsed to zero rules; rebuild --no-incremental.`);
          assert(short.headroom >= short.bearingPx - 0.05,
            `feature label "${short.text}" clears its clip edge by ${short.headroom.toFixed(3)}px at ${vw}x${vh}, ` +
            `but the stylesheet declares a ${short.bearingPx.toFixed(3)}px side bearing — the padding is not being applied`);
          assert(short.bearingPx >= short.inkOverhangPx,
            `the declared italic side bearing is ${short.bearingPx.toFixed(3)}px at ${vw}x${vh}, below the ` +
            `${short.inkOverhangPx.toFixed(3)}px of ink this font paints past its advance width for ` +
            `"${short.text}" — the final glyph will be shaved`);
          // (b) A label that does NOT fit must genuinely overflow, so the ellipsis renders.
          assert(long.overflows,
            `long feature label "${long.text}" does not overflow its box at ${vw}x${vh}, so text-overflow:ellipsis cannot fire`);
          // (c) Neither may collide with the priority badge (the badge is flex-shrink-0 for this).
          // The gutter is Bootstrap's `ms-1` = 0.25rem. It is a MARGIN, so it is fixed against
          // viewport — but it is REM, so it is not fixed against the root font size: a 12px root
          // ("Small" in Chrome) makes it 3.00px and a hard-coded `>= 3.5` would false-RED on a user
          // setting rather than on a regression. Derived from the measured root instead.
          //
          // Scope note, so this is not miscounted among the load-bearing guards: two in-flow flex
          // siblings in DOM order cannot overlap, and this measures 4px on master, 4px with the fix,
          // and 4-5px with the label rules reverted. It only catches `ms-1` being deleted from the
          // markup. Kept for that, claimed as no more than that.
          for (const l of labels) {
            const expectedGutter = 0.25 * l.rootPx;
            assert(l.gutterToBadge >= expectedGutter - 0.5,
              `feature label "${l.text}" leaves ${l.gutterToBadge.toFixed(2)}px before its priority badge ` +
              `at ${vw}x${vh}; the ms-1 gutter at a ${l.rootPx}px root should be ${expectedGutter.toFixed(2)}px`);
          }
        }
      } finally {
        // try/finally, not a bare restore: an assertion thrown mid-loop would otherwise leave the
        // viewport at 390x844 for the ~17 tests that follow, turning one real failure into a
        // screenful of spurious ones that bury it.
        if (restoreVis) await page.setViewportSize(restoreVis);
      }
    });

    await test("Board column: a 1-card column does not paint a dead region below its card (#322)", async () => {
      // Every column used to stretch to the board's full height, so a column holding one 68px card
      // painted ~545px of empty `bg-body-secondary` under it and the board read as EMPTY while work
      // existed in it. Asserted against the epic's own AC5 threshold (200px), with a wide margin:
      // the fix lands at ~36px, which is the drop floor doing its job, not residual dead space.
      const deadProj = await api("POST", "/api/projects", { name: `E2E Dead ${stamp}` });
      const deadFeature = await api("POST", `/api/projects/${deadProj.id}/features`, { name: "Dead Space Feature" });
      await api("POST", `/api/features/${deadFeature.id}/tasks`, { name: "Only card", status: "Todo" });
      await page.goto(`${baseUrl}/project/${deadProj.id}`);
      await page.locator('[data-testid="board-column-Todo-cards"] .board-card').waitFor({ state: "visible" });

      const geom = await page.evaluate(() => {
        const col = document.querySelector('[data-testid="board-column-Todo"]');
        const cards = [...col.querySelectorAll(".board-card")];
        const last = cards[cards.length - 1].getBoundingClientRect();
        const empty = document.querySelector('[data-testid="board-column-Review"]');
        const emptyCards = document.querySelector('[data-testid="board-column-Review-cards"]');
        // Resolve the DECLARED floor so the assertion below compares the rendered box against the
        // token, instead of against a number copied out of it that can silently drift.
        const rootPx = parseFloat(getComputedStyle(document.documentElement).fontSize);
        const declared = getComputedStyle(document.documentElement)
          .getPropertyValue("--kf-board-drop-floor").trim();
        return {
          cardCount: cards.length,
          deadBelowLastCard: col.getBoundingClientRect().bottom - last.bottom,
          columnH: col.getBoundingClientRect().height,
          boardH: document.querySelector('[data-testid="kanban-board"]').getBoundingClientRect().height,
          emptyColumnH: empty.getBoundingClientRect().height,
          emptyDropTargetH: emptyCards.getBoundingClientRect().height,
          declaredFloorPx: declared.endsWith("rem") ? parseFloat(declared) * rootPx : parseFloat(declared),
          unusedBoardHeightBelowColumn:
            document.querySelector('[data-testid="kanban-board"]').getBoundingClientRect().bottom
            - col.getBoundingClientRect().bottom,
          cardStridePx: (() => {
            const c = cards[0];
            return c.getBoundingClientRect().height + parseFloat(getComputedStyle(c).marginBottom);
          })(),
        };
      });
      assert(geom.cardCount === 1, `guard needs exactly one card to be meaningful, saw ${geom.cardCount}`);
      assert(geom.deadBelowLastCard < 200,
        `a 1-card column paints ${geom.deadBelowLastCard.toFixed(0)}px of dead space below its last card (limit 200px)`);
      // The column must genuinely be shorter than the board, not merely under the threshold.
      //
      // `columnH < boardH` was a TAUTOLOGY as seeded: under `align-self: stretch` a 1-card column
      // measures 470.6 against a 486.6 board, and the 16px difference is just the board's own
      // `p-2`, so it could not fail in any configuration. Stated instead as the property the fix
      // actually creates — a 1-card column leaves most of the board's height UNUSED — which is
      // ~8px (the padding) under stretch and hundreds of px with the fix.
      assert(geom.unusedBoardHeightBelowColumn > 100,
        `a 1-card column leaves only ${geom.unusedBoardHeightBelowColumn.toFixed(0)}px of board height ` +
        `unused below it (column ${geom.columnH.toFixed(0)}px / board ${geom.boardH.toFixed(0)}px) — it is still stretching`);
      // …and the cure must not be worse: an EMPTY column has to stay a real drop target (#290).
      // Two separate holes, because a single fixed threshold closes neither. (CodeRabbit, PR #335.)
      //   (a) the RENDERED floor must match the DECLARED token — catches CSS breaking the floor;
      assert(Math.abs(geom.emptyDropTargetH - geom.declaredFloorPx) <= 1,
        `an empty column's drop target renders ${geom.emptyDropTargetH.toFixed(0)}px but ` +
        `--kf-board-drop-floor declares ${geom.declaredFloorPx.toFixed(0)}px`);
      //   (b) the DECLARED token must still clear one card's stride — catches the token itself being
      //       quietly shrunk below the constraint it was derived from, which (a) alone would let
      //       through with both numbers agreeing on a uselessly small target.
      assert(geom.declaredFloorPx >= geom.cardStridePx,
        `--kf-board-drop-floor is ${geom.declaredFloorPx.toFixed(0)}px, below one card's stride of ` +
        `${geom.cardStridePx.toFixed(0)}px — an empty column no longer offers room for the card it asks for`);
    });

    await test("Board column: the empty-state placeholder never paints outside its column (#322)", async () => {
      // The half of the dead-space fix that had NO coverage, and which shipped a real defect.
      //
      // The placeholder's natural height is a function of COLUMN WIDTH x status-name length: 76px
      // at a 214px column, 97px at 182px, and 118px at the 180px `min-width` floor, where both the
      // "In Progress" heading and the placeholder text wrap hardest. While every column stretched
      // to ~470px that variation was invisible. Content-height columns exposed it: at 1024x768 the
      // white dashed box hung 14px BELOW the grey column background (column bottom 401.75,
      // placeholder bottom 415.75), because the placeholder was an `inset: 0` absolutely-positioned
      // overlay and so could never push the column taller, only spill out of it.
      //
      // This runs at 1024x768 ON PURPOSE. It is GREEN at 1280x720 and 1440x900 — the default
      // viewport the other guards use — so a fixed-viewport suite cannot see it at all.
      //
      // Paired with a dead-space assertion at the SAME viewport, because containment is trivially
      // satisfiable by making every column taller again — which would trade this defect for the
      // one the whole PR exists to remove.
      const phProj = await api("POST", "/api/projects", { name: `E2E Placeholder ${stamp}` });
      const phFeature = await api("POST", `/api/projects/${phProj.id}/features`, { name: "Placeholder Feature" });
      await api("POST", `/api/features/${phFeature.id}/tasks`, { name: "Only card", status: "Todo" });

      const restore = page.viewportSize();
      await page.setViewportSize({ width: 1024, height: 768 });
      try {
        await page.goto(`${baseUrl}/project/${phProj.id}`);
        await page.locator('[data-testid="board-column-Todo-cards"] .board-card').waitFor({ state: "visible" });

        const g = await page.evaluate(() => {
          const rows = [];
          for (const col of document.querySelectorAll(".kanban-column")) {
            const ph = col.querySelector(".kanban-column-empty");
            if (!ph || !ph.firstElementChild) continue;
            const cr = col.getBoundingClientRect();
            rows.push({
              status: col.getAttribute("data-testid").replace("board-column-", ""),
              columnWidth: cr.width,
              columnBottom: cr.bottom,
              // the PAINTED box, not the inset:0 wrapper — the wrapper can never report a spill
              placeholderBottom: ph.firstElementChild.getBoundingClientRect().bottom,
            });
          }
          const todo = document.querySelector('[data-testid="board-column-Todo"]');
          const cards = [...todo.querySelectorAll(".board-card")];
          return {
            rows,
            deadBelowLastCard: todo.getBoundingClientRect().bottom - cards[cards.length - 1].getBoundingClientRect().bottom,
          };
        });

        assert(g.rows.length > 0, "no empty column rendered a placeholder — the guard has nothing to measure");
        for (const r of g.rows) {
          assert(r.placeholderBottom <= r.columnBottom + 0.5,
            `the "${r.status}" empty-state placeholder paints ` +
            `${(r.placeholderBottom - r.columnBottom).toFixed(2)}px below its column at a ` +
            `${r.columnWidth.toFixed(0)}px column width (1024x768)`);
        }
        // …and containment must not have been bought by re-growing the columns.
        assert(g.deadBelowLastCard < 200,
          `containment came at the cost of ${g.deadBelowLastCard.toFixed(0)}px of dead space below a ` +
          `1-card column at 1024x768 — the defect this PR removes has been traded, not fixed`);
      } finally {
        if (restore) await page.setViewportSize(restore);
      }
    });

    await test("Board column: a column taller than the board scrolls itself, not the board (#322)", async () => {
      // The other unguarded half. `.kanban-column { max-height: 100% }` caps a long column and
      // `.kanban-column-body { min-height: 0 }` is the release valve that lets
      // `.board-column-cards`'s `overflow-auto` actually engage. Every other guard seeds <= 2 cards,
      // so NEITHER was exercised: reverting `max-height` (column grows to 1038px) or the
      // `min-height` (cards box 993px inside a 651px column) brings back the board's second
      // vertical scrollbar with every other assertion still green.
      const tallProj = await api("POST", "/api/projects", { name: `E2E Tall ${stamp}` });
      const tallFeature = await api("POST", `/api/projects/${tallProj.id}/features`, { name: "Tall Feature" });
      for (let i = 0; i < 14; i++) {
        await api("POST", `/api/features/${tallFeature.id}/tasks`,
          { name: `Tall probe ${i} with a deliberately long wrapping title`, status: "Todo", sortOrder: i * 10 });
      }
      await page.goto(`${baseUrl}/project/${tallProj.id}`);
      await page.locator('[data-testid="board-column-Todo-cards"] .board-card').first().waitFor({ state: "visible" });

      const g = await page.evaluate(() => {
        const board = document.querySelector('[data-testid="kanban-board"]');
        const col = document.querySelector('[data-testid="board-column-Todo"]');
        const cards = document.querySelector('[data-testid="board-column-Todo-cards"]');
        const before = cards.scrollTop;
        cards.scrollTop = 99999;
        const maxScroll = cards.scrollTop;
        cards.scrollTop = before;
        return {
          cardCount: col.querySelectorAll(".board-card").length,
          columnBottom: col.getBoundingClientRect().bottom,
          boardBottom: board.getBoundingClientRect().bottom,
          cardsClientH: cards.clientHeight,
          cardsScrollH: cards.scrollHeight,
          maxInternalScroll: maxScroll,
          boardScrollH: board.scrollHeight,
          boardClientH: board.clientHeight,
        };
      });

      assert(g.cardCount >= 10, `guard needs a genuinely tall column, saw ${g.cardCount} cards`);
      // the cap: the column may not grow past the board
      assert(g.columnBottom <= g.boardBottom + 1,
        `a ${g.cardCount}-card column overflows the board by ${(g.columnBottom - g.boardBottom).toFixed(0)}px — ` +
        `max-height is not capping it`);
      // the release valve: the cards box must actually be scrollable, not merely overflowing
      assert(g.cardsScrollH > g.cardsClientH,
        `the cards container is ${g.cardsClientH}px for ${g.cardsScrollH}px of cards — it is not bounded, so it cannot scroll`);
      assert(g.maxInternalScroll > 0,
        `the cards container reports ${g.maxInternalScroll}px of scroll range — overflow-auto never engaged`);
      // and the whole point: the board itself must NOT gain a second vertical scrollbar
      assert(g.boardScrollH <= g.boardClientH + 1,
        `the board scrolls vertically (${g.boardScrollH}px in ${g.boardClientH}px) — the column overflow escaped to it`);
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
    // The empty-state placeholder had ZERO coverage before #322 phase 3 — `kanban-column-empty`,
    // `empty-column-*` and `add-idea-New-button` appeared nowhere in this suite — which is exactly
    // why both #290's overlay contract and the placeholder's own geometry could break silently.
    await test("Ideas board: an empty column's add affordance is contained, hit-testable, and opens the modal (#322/#290)", async () => {
      const parked = await api("POST", "/api/ideas", { title: `E2E Empty ${stamp}` });
      await api("PATCH", `/api/ideas/${parked.id}`, { status: "Triaging" });   // vacate New
      await page.goto(`${baseUrl}/ideas`);
      await page.locator('[data-testid="add-idea-New-button"]').waitFor({ state: "visible" });

      const g = await page.evaluate(() => {
        const col = document.querySelector('[data-testid="board-column-New"]');
        const ph = col.querySelector(".kanban-column-empty");
        const btn = document.querySelector('[data-testid="add-idea-New-button"]');
        const cr = col.getBoundingClientRect(), br = btn.getBoundingClientRect();
        return {
          // #290: the placeholder must be a SIBLING of the sortable container, never a DOM child —
          // a child with no data-task-id makes kanbanflow.js parseInt(undefined) => NaN on drag.
          placeholderIsChildOfSortable: !!ph.closest(".board-column-cards"),
          sortableNonCardChildren: [...col.querySelector(".board-column-cards").children]
            .filter((n) => !n.classList.contains("board-card")).length,
          placeholderPointerEvents: getComputedStyle(ph).pointerEvents,
          buttonPointerEvents: getComputedStyle(btn).pointerEvents,
          // the placeholder must not spill its column (the #322 phase 3 blocker)
          overhang: ph.firstElementChild.getBoundingClientRect().bottom - cr.bottom,
          btnH: br.height, btnW: br.width,
          // is the button the topmost thing at its own centre? (paint order / click swallowing)
          topmostAtCentre: (() => {
            const el = document.elementFromPoint(br.left + br.width / 2, br.top + br.height / 2);
            return el === btn || btn.contains(el);
          })(),
        };
      });

      assert(!g.placeholderIsChildOfSortable, "#290 broken: the placeholder is now a DOM child of .board-column-cards");
      assert(g.sortableNonCardChildren === 0,
        `#290 broken: the SortableJS container has ${g.sortableNonCardChildren} non-card child element(s)`);
      assert(g.placeholderPointerEvents === "none",
        `#290 broken: the placeholder is ${g.placeholderPointerEvents}, so a drop aimed at an empty column cannot fall through`);
      assert(g.buttonPointerEvents === "auto",
        `the add-idea button re-enables pointer events (pe-auto); it reports ${g.buttonPointerEvents}`);
      assert(g.overhang <= 0.5,
        `the /ideas empty placeholder paints ${g.overhang.toFixed(2)}px below its column`);
      assert(g.btnH >= 24 && g.btnW >= 24,
        `the add-idea target is ${g.btnW.toFixed(0)}x${g.btnH.toFixed(0)}px, under WCAG 2.5.8's 24x24`);
      assert(g.topmostAtCentre,
        "something paints above the add-idea button at its own centre — the click would be swallowed");

      // …and it must actually WORK, not merely be positioned correctly.
      await page.locator('[data-testid="add-idea-New-button"]').click();
      await page.locator('[data-testid="idea-modal"]').waitFor({ state: "visible" });
      await page.locator('[data-testid="idea-cancel-button"]').click();
      await page.locator('[data-testid="idea-modal"]').waitFor({ state: "hidden" });
    });

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

    // TaskDetailPanel makes the same aria-modal promise as the confirms above and deliberately
    // shares their initDialog/destroyDialog wiring. Keep it in this harness: a separate test with
    // a different definition of focus containment could let the shared behavior silently diverge.
    await test("Task detail panel traps Tab, Escape closes it, and focus returns to its card (#483)", async () => {
      const trapTask = await api("POST", `/api/features/${feature.id}/tasks`, {
        name: `E2E task detail trap ${stamp}`, status: "Todo",
      });
      const panel = '[data-testid="task-detail-panel"]';
      const card = `[data-testid="board-card-${trapTask.id}"]`;
      const openerTestid = `board-card-${trapTask.id}-title`;

      await page.goto(`${baseUrl}/project/${proj.id}`);
      await page.locator(card).waitFor({ state: "visible" });
      await page.locator(`[data-testid="${openerTestid}"]`).click();
      await page.locator(panel).waitFor({ state: "visible" });

      const opened = await probeDialogFocus(page, panel);
      assert(opened.count >= 2,
        `the task detail panel exposes only ${opened.count} focusable control(s), so the Tab cycle is vacuous`);
      assert(opened.escapes > 0,
        "PRECONDITION: no focusable controls exist outside the task detail panel, so an untrapped " +
        "aria-modal panel would pass this guard by accident");
      assert(opened.inside,
        `the task detail panel opened without taking focus — focus is on ${opened.active}`);

      let escapedStops = 0;
      const escapedOnto = [];
      const presses = 30;
      for (let i = 1; i <= presses; i++) {
        await page.keyboard.press("Tab");
        const p = await probeDialogFocus(page, panel);
        if (!p.inside) {
          escapedStops++;
          escapedOnto.push(`Tab #${i}: ${p.active}`);
        }
      }
      assert(escapedStops === 0,
        `${escapedStops} of ${presses} Tab stops escaped the aria-modal task detail panel: ` +
        escapedOnto.join("; "));

      await page.keyboard.press("Escape");
      await page.locator(panel).waitFor({ state: "hidden" });
      const restored = await probeDialogFocus(page, panel);
      assert(restored.activeTestid === openerTestid,
        `closing the task detail panel did not return focus to ${card}; focus is on ${restored.active}`);

      await api("DELETE", `/api/tasks/${trapTask.id}`).catch(() => {});
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

    // ══ Scenario H: the Home project card (#281) ═══════════════════════════════════════════
    //
    // The card is the only place a human sees a project's progress, what to work on next, and
    // whether an agent is holding anything. Every number on it is SERVER-computed, so each
    // assertion below compares the RENDERED PIXEL against the API that produced it — never
    // against a constant the test also derived. A card that agreed with a hard-coded "2/4" while
    // the endpoint said something else would be exactly the drift this issue exists to end.
    const cardProj = await api("POST", "/api/projects", { name: `E2E Card ${stamp}` });
    const cardFeature = await api("POST", `/api/projects/${cardProj.id}/features`, { name: "Card Feature" });
    const cardTasks = {
      doneA: await api("POST", `/api/features/${cardFeature.id}/tasks`, { name: `card done A ${stamp}`, status: "Done" }),
      doneB: await api("POST", `/api/features/${cardFeature.id}/tasks`, { name: `card done B ${stamp}`, status: "Done" }),
      low: await api("POST", `/api/features/${cardFeature.id}/tasks`, { name: `card low ${stamp}`, status: "Todo", priority: "Low" }),
      critical: await api("POST", `/api/features/${cardFeature.id}/tasks`, { name: `card critical ${stamp}`, status: "Todo", priority: "Critical" }),
    };

    // A second project that nothing has ever happened to — the calm-empty case (AC 5).
    const calmProj = await api("POST", "/api/projects", { name: `E2E Calm ${stamp}` });

    const cardFor = (projectId) => page.locator(`[data-testid="project-card"][data-project-id="${projectId}"]`);

    await test("Home card shows the server's progress, not a count derived in the page", async () => {
      await page.goto(`${baseUrl}/`);
      const card = cardFor(cardProj.id);
      await card.waitFor({ state: "visible" });

      const listed = (await api("GET", "/api/projects")).find((p) => p.id === cardProj.id);
      assert(listed, "the project is missing from GET /api/projects");
      assert(listed.progress, "GET /api/projects returned no progress object");

      const rendered = (await card.locator('[data-testid="project-progress-count"]').innerText()).trim();
      assert(
        rendered === `${listed.progress.completed}/${listed.progress.total} done`,
        `card shows "${rendered}" but the API says ` +
        `"${listed.progress.completed}/${listed.progress.total} done"`,
      );

      // The number must be the WHOLE project, not the tasks the page happens to hold. Four were
      // seeded and two are finished, so a card counting only open work would read "2/2".
      assert(
        rendered === "2/4 done",
        `expected the card to count finished tasks too ("2/4 done"), got "${rendered}"`,
      );

      // The bar's width is an inline style built from progress.percentComplete. A culture that
      // formats 50 as "50,0" would emit `width:50,0%`, which CSS drops — the bar would silently
      // render empty while the text beside it still read 2/4.
      const width = await card.locator('[data-testid="project-progress"] .progress-bar')
        .evaluate((el) => el.style.width);
      assert(width === `${listed.progress.percentComplete}%`,
        `progress bar width is "${width}", expected "${listed.progress.percentComplete}%"`);
    });

    await test("Home card's next task is the ranker's #1, identical to the next-tasks endpoint", async () => {
      await page.goto(`${baseUrl}/`);
      const chip = cardFor(cardProj.id).locator('[data-testid="project-next-task"]');
      await chip.waitFor({ state: "visible" });

      // The same question, asked of the endpoint agents use. #151/#188 are two prior incidents of
      // two surfaces answering it differently; this is the third surface.
      const ranked = await api("GET", `/api/projects/${cardProj.id}/next-tasks?limit=5`);
      const top = ranked.recommendations?.[0] ?? ranked.Recommendations?.[0];
      assert(top, `next-tasks returned no recommendations: ${JSON.stringify(ranked).slice(0, 200)}`);

      const chipTaskId = Number(await chip.getAttribute("data-task-id"));
      assert(chipTaskId === top.id,
        `card recommends task ${chipTaskId}, next-tasks recommends ${top.id}`);

      // …and it is NOT simply the first Todo. The Low-priority task was created first; the ranker
      // sorts by priority before id, so a "first open task" implementation would show the wrong one.
      assert(chipTaskId === cardTasks.critical.id,
        `expected the Critical task ${cardTasks.critical.id}, got ${chipTaskId}`);
      assert((await chip.innerText()).includes(cardTasks.critical.name),
        `the chip does not name the recommended task: "${await chip.innerText()}"`);
    });

    await test("Home card's next-task chip opens that task on the project page", async () => {
      await page.goto(`${baseUrl}/`);
      const chip = cardFor(cardProj.id).locator('[data-testid="project-next-task"]');
      await chip.waitFor({ state: "visible" });
      await chip.click();

      await page.waitForURL(new RegExp(`/project/${cardProj.id}\\?task=${cardTasks.critical.id}$`));
      // The panel must actually be open ON THAT TASK — a link that merely lands on the project is
      // not "links straight to it", and a panel open on the wrong task is worse than none.
      const title = page.locator('[data-testid="task-detail-panel"] #task-detail-title');
      await title.waitFor({ state: "visible" });
      assert((await title.innerText()).trim() === cardTasks.critical.name,
        `detail panel opened on "${(await title.innerText()).trim()}", expected "${cardTasks.critical.name}"`);

      // …and the deep link must SURVIVE using the page. SyncFilterToUrlAsync rebuilds the whole
      // query string from the filter, so before the merge it landed on this PR it silently
      // dropped `task=` on the first keystroke — after which the link is unshareable AND
      // unrecoverable, because DeepLinkTaskId is [SupplyParameterFromQuery] and the value is gone
      // from the component too. Typing into the filter is the cheapest way to make the page write
      // the URL; the debounce is 250ms, so waitForURL does the waiting.
      await page.locator('[data-testid="board-filter-bar"]').waitFor({ state: "visible" });
      await page.locator('[data-testid="board-filter-search"]').fill("card");
      await page.waitForURL((url) => url.searchParams.get("q") === "card", { timeout: 10000 });
      const afterFilter = new URL(page.url());
      assert(afterFilter.searchParams.get("task") === String(cardTasks.critical.id),
        `filtering stripped the deep link: expected task=${cardTasks.critical.id}, got "${page.url()}"`);
      assert(afterFilter.searchParams.get("q") === "card",
        `merging the deep link back in lost the filter itself: "${page.url()}"`);
    });

    await test("Home card shows a claim once an agent claims a task", async () => {
      // The LOW task, deliberately, not the Critical one. TaskRanking treats every claimed task as
      // claimed-by-another when `actor` is null, and the Home summary passes null — so claiming the
      // Critical task would sink it below the unclaimed Low one and quietly invert the ranking the
      // two scenarios above assert. They pass today only because they run first. Claiming the task
      // the ranker does NOT recommend removes that ordering dependency entirely.
      await api("POST", `/api/tasks/${cardTasks.low.id}/claim`, { actor: `e2e-agent-${stamp}` });

      await page.goto(`${baseUrl}/`);
      const claim = cardFor(cardProj.id).locator('[data-testid="project-claim"]');
      await claim.first().waitFor({ state: "visible" });
      assert((await claim.first().innerText()).includes(`e2e-agent-${stamp}`),
        `claim chip does not name the claimant: "${await claim.first().innerText()}"`);

      // …and the recommendation must be UNCHANGED by the claim, which is the property that makes
      // the ordering independent rather than merely lucky.
      const chipTaskId = Number(await cardFor(cardProj.id)
        .locator('[data-testid="project-next-task"]').getAttribute("data-task-id"));
      assert(chipTaskId === cardTasks.critical.id,
        `claiming the Low task moved the recommendation to ${chipTaskId}; ` +
        `it must still be the Critical task ${cardTasks.critical.id}`);

      // Recency is rendered relative with the absolute instant on hover — one clock (#306).
      const when = cardFor(cardProj.id).locator('[data-testid="project-last-activity"]');
      await when.waitFor({ state: "visible" });
      const tooltip = await when.getAttribute("title");
      assert(/UTC:/.test(tooltip || ""),
        `last-activity tooltip must carry the absolute UTC instant, got "${tooltip}"`);
      assert((await when.getAttribute("datetime") || "").length > 0,
        "last-activity <time> must carry a machine-readable datetime attribute");

      // ── The absolute instant must be reachable WITHOUT a mouse ──
      // `title` fires on hover only and `datetime` is never announced, so on the pre-fix build a
      // keyboard or screen-reader user saw "2m ago" and had no route to the exact time — while
      // `cursor: help` and the dotted underline advertised that there was more to reach. Both
      // assertions below fail on that build: `title` alone satisfies neither.
      const reach = await when.evaluate((el) => ({
        tabindex: el.getAttribute("tabindex"),
        text: el.textContent,
        // The hidden half must be hidden to the EYE, not to the accessibility tree: a
        // `display:none`/`hidden` implementation would read to nobody at all.
        hiddenWidth: el.querySelector(".visually-hidden")?.getBoundingClientRect().width ?? -1,
      }));
      assert(reach.tabindex === "0",
        `the <time> must be focusable so the absolute instant is in the keyboard's reach, ` +
        `tabindex="${reach.tabindex}"`);
      assert(reach.text.includes(tooltip),
        `the accessible text must carry the absolute instant "${tooltip}", got "${reach.text}"`);
      assert(reach.hiddenWidth >= 0 && reach.hiddenWidth <= 1,
        `the absolute instant must be visually hidden, not rendered — measured ${reach.hiddenWidth}px wide`);

      // …and the focus stop must be VISIBLE. A silent tab stop is worse than no tab stop.
      // The Tab press is not decoration: `:focus-visible` is a HEURISTIC on how focus arrived, and
      // a programmatic .focus() after a mouse interaction does not match it. Pressing a key first
      // puts the browser in keyboard-interaction mode so the subsequent focus is the keyboard case
      // this assertion is about.
      await page.keyboard.press("Tab");
      await when.focus();
      const focusRing = await when.evaluate((el) => {
        const cs = getComputedStyle(el);
        return {
          matches: el.matches(":focus-visible"),
          width: cs.outlineWidth,
          style: cs.outlineStyle,
          offset: cs.outlineOffset,
        };
      });
      assert(focusRing.matches,
        "the <time> did not match :focus-visible after keyboard focus — the tab stop is unreachable-looking");
      // ⚠️ `outline-width > 0` ALONE CANNOT FAIL HERE, and asserting only that is how this guard
      // was written first. Chromium's UA stylesheet already draws a ring on any :focus-visible
      // element, so deleting `.kf-card-when:focus-visible` outright still measured a positive
      // width — a green test over a stylesheet that had lost the rule (measured: the mutation ran
      // and the scenario passed). `outline-style` is what separates them: the UA ring computes to
      // the keyword `auto`, and only an authored rule computes to `solid`. The offset is asserted
      // too, because the ring has to clear this element's own dotted underline to read as focus
      // rather than as more decoration.
      assert(focusRing.style === "solid",
        ".kf-card-when:focus-visible must declare the app's own ring; computed outline-style is " +
        `"${focusRing.style}" — "auto" means only the browser default is left`);
      assert(parseFloat(focusRing.width) > 0,
        `.kf-card-when:focus-visible must paint an outline, computed outline-width "${focusRing.width}"`);
      assert(parseFloat(focusRing.offset) > 0,
        `the focus ring must be offset clear of the dotted underline, computed "${focusRing.offset}"`);
    });

    await test("Home grid uses the canvas at 1440 and stays single-column at 390 (#281 AC 6)", async () => {
      // AC 6 — "the grid uses the available canvas; the one-card-in-an-ocean layout is fixed" —
      // was the only acceptance criterion with no automated coverage: every other scenario in this
      // block runs at the suite's default 1280x720, so the layout was proven by hand once and then
      // guarded by nothing. Same viewport-loop + try/finally shape as the #322 feature-label guard
      // above; the restore is NOT optional, because an assertion thrown mid-loop would otherwise
      // leave the viewport at 390x844 for every scenario that follows.
      //
      // ⚠️ WHAT THIS DELIBERATELY DOES **NOT** ASSERT: auto-fit vs auto-fill.
      // `.project-grid` uses `auto-fit` specifically so a LONE project's empty tracks collapse.
      // The two functions are indistinguishable once the cards outnumber the tracks — and by the
      // time this runs the suite has created a dozen projects, so the first row is always full and
      // both keywords produce identical geometry. Asserting it here would be a test that cannot
      // fail. Isolating a single-project Home would mean deleting every other scenario's fixtures,
      // which is a worse trade than naming the gap. What IS asserted below is the defect AC 6
      // actually describes — dead canvas — and each assertion is failable by a real mutation:
      //   * perRow >= 2       ← `grid-template-columns: 1fr` (the pre-#281 single-column layout)
      //   * row spans grid    ← any `.project-card` max-width narrower than the computed track
      //   * perRow === 1 @390 ← a track floor that lets two cards pack on a phone
      //   * no overflow @390  ← `minmax(19rem, …)` without the `min(100%, …)` clamp
      const restoreGrid = page.viewportSize();
      try {
        for (const [vw, vh, single] of [[1440, 900, false], [390, 844, true]]) {
          await page.setViewportSize({ width: vw, height: vh });
          await page.goto(`${baseUrl}/`);
          await cardFor(cardProj.id).waitFor({ state: "visible" });

          const g = await page.evaluate(() => {
            const grid = document.querySelector('[data-testid="project-grid"]');
            const cards = [...grid.querySelectorAll('[data-testid="project-card"]')];
            const boxes = cards.map((c) => c.getBoundingClientRect());
            const firstTop = Math.round(boxes[0].top);
            const firstRow = boxes.filter((b) => Math.round(b.top) === firstTop);
            return {
              gridWidth: grid.getBoundingClientRect().width,
              cardCount: boxes.length,
              perRow: firstRow.length,
              cardWidth: boxes[0].width,
              // How much of the grid's width the first row actually covers, edge to edge.
              rowSpan: firstRow[firstRow.length - 1].right - firstRow[0].left,
              docScrollWidth: document.documentElement.scrollWidth,
              innerWidth: window.innerWidth,
            };
          });

          // The span check below is only meaningful over a SATURATED row, so state the
          // precondition instead of letting a short row pass as "dead canvas".
          assert(g.cardCount > g.perRow,
            `guard needs a full first row: ${g.cardCount} cards, ${g.perRow} in the first row`);

          // Nothing may spill sideways at either width — the AC 7 measurement, now automated.
          assert(g.docScrollWidth <= g.innerWidth,
            `at ${vw}x${vh} the page scrolls horizontally: ` +
            `document ${g.docScrollWidth}px vs viewport ${g.innerWidth}px`);

          if (single) {
            assert(g.perRow === 1,
              `at ${vw}x${vh} the grid packs ${g.perRow} cards per row; a phone must be single-column`);
            assert(g.cardWidth <= vw,
              `at ${vw}x${vh} a card is ${g.cardWidth.toFixed(0)}px wide, wider than the viewport`);
          } else {
            assert(g.perRow >= 2,
              `at ${vw}x${vh} the grid draws ${g.perRow} card(s) across ${g.gridWidth.toFixed(0)}px ` +
              "— that is the one-card-in-an-ocean layout AC 6 removes");
            // Every track is `1fr` and every card is `width: 100%` of its track, so a full row
            // must reach both edges of the grid. It stops short the moment `.project-card`'s cap
            // falls below the computed track width — the "narrow card marooned in a wide
            // container" half of the same defect, which perRow alone cannot see.
            assert(Math.abs(g.rowSpan - g.gridWidth) <= 1.5,
              `at ${vw}x${vh} a full row spans ${g.rowSpan.toFixed(1)}px of a ` +
              `${g.gridWidth.toFixed(1)}px grid — ${(g.gridWidth - g.rowSpan).toFixed(1)}px of dead canvas`);
          }
        }
      } finally {
        if (restoreGrid) await page.setViewportSize(restoreGrid);
      }
    });

    await test("The next-task priority chip keeps its fixed ink in both themes (#281)", async () => {
      // `.kf-next-task-prio` paints `var(--bs-white)` on a `--kf-prio-*` background. Both sides are
      // declared once on `:root` and never re-declared under `[data-bs-theme=dark]`, which is the
      // whole reason the chip is readable in dark mode — and it is also why a typo in the variable
      // name is INVISIBLE in review: an unresolvable `var()` is invalid at computed-value time, so
      // `color` falls back to the inherited body colour and the chip silently turns grey-on-red.
      // Measured in both themes, because "it looked fine" was only ever checked in one.
      // The selector is resolved and measured INSIDE one page.evaluate, not through a Locator
      // handle. On this Server circuit the static SSR pass renders the card and the interactive
      // pass then REPLACES those nodes, so a handle resolved a round-trip earlier can be detached
      // by the time it is measured — and `getComputedStyle` on a detached node returns every
      // property as the empty string, which reads as "the token did not resolve" rather than as a
      // stale handle. Measured: that is exactly what a Locator-based version of this reported.
      const chipSelector = (projectId) =>
        `[data-testid="project-card"][data-project-id="${projectId}"] ` +
        '[data-testid="project-next-task"] .kf-next-task-prio';

      for (const theme of ["light", "dark"]) {
        await page.goto(`${baseUrl}/`);
        // Wait for a node that is BOTH present and live — a non-empty computed colour is the
        // cheapest proof the element is still attached and the stylesheet has applied.
        await page.waitForFunction(
          (sel) => {
            const el = document.querySelector(sel);
            return !!el && getComputedStyle(el).color !== "";
          },
          chipSelector(cardProj.id),
          { timeout: 10000 },
        );

        const ink = await page.evaluate(({ sel, t }) => {
          document.documentElement.setAttribute("data-bs-theme", t);
          const el = document.querySelector(sel);
          const cs = getComputedStyle(el);
          return { color: cs.color, background: cs.backgroundColor, priority: el.dataset.priority };
        }, { sel: chipSelector(cardProj.id), t: theme });

        assert(ink.priority === "Critical",
          `expected the Critical chip for this measurement, got "${ink.priority}"`);
        assert(ink.color === "rgb(255, 255, 255)",
          `[${theme}] the Critical chip's ink resolved to "${ink.color}", not white — ` +
          "the colour token did not resolve");
        assert(ink.background === "rgb(220, 53, 69)",
          `[${theme}] the Critical chip's background resolved to "${ink.background}", not #dc3545`);
      }
    });

    await test("A project with no activity renders calm — no placeholder chips", async () => {
      await page.goto(`${baseUrl}/`);
      const card = cardFor(calmProj.id);
      await card.waitFor({ state: "visible" });

      for (const testid of ["project-progress", "project-next-task", "project-claim", "project-last-activity"]) {
        assert(await card.locator(`[data-testid="${testid}"]`).count() === 0,
          `an untouched project rendered "${testid}" — AC 5 requires the card to stay calm`);
      }
      // …but it is still a real card with a way in, not a blank tile. `exact` matters: the ⋮
      // menu's accessible name starts with "Open project actions for …", so a loose match here
      // resolves to two buttons and fails on strict mode rather than on the product.
      await card.getByRole("button", { name: "Open", exact: true }).waitFor({ state: "visible" });
    });

    // ── Scenario: reopening the outline-sidebar edit modal re-reads the audit trail (#374) ──
    //
    // EditItemModal is never unmounted — ProjectView leaves it in the tree with its root hidden,
    // and closing does not clear EditTaskId. ActivityTimeline re-queries on a TaskId OR
    // RefreshToken change, so without the explicit `activityRefreshToken++` on a fresh open
    // (EditItemModal.razor:367) a reopen shows the events cached on the FIRST open.
    //
    // The symptom is #328's own bug, narrowed to one surface: the COMMENT written by a change
    // reloads (the modal re-reads comments itself), while the matching ACTIVITY row does not. So
    // the screen shows a task whose history half-updated.
    //
    // ⚠️ THE SCOPING BELOW IS LOAD-BEARING — DO NOT SIMPLIFY IT TO A TEXT MATCH. ⚠️
    // A PATCH to status writes BOTH a system comment ("Status changed from Review to Done") and a
    // TaskStatusChanged event. The comment reloads on every open whether or not the fix is
    // present, so any assertion over the timeline's innerText passes against the broken build.
    // This assertion scopes to rows whose `data-kind` is the ActivityEvent's EventType — comment
    // rows carry `Comment:{CommentType}` instead (see TimelineEntry.Kind) — and reads the
    // rendered per-change cells rather than the row's flattened text.
    //
    // That distinction is measured, not asserted on faith: the scenario computes the naive
    // text signal too and REPORTS it in the failure message, so a RED run states in its own
    // output that the loose variant was still satisfied. See #374 AC2.
    await test("Timeline: reopening the edit modal re-reads the audit trail, it does not serve a cached one (#374)", async () => {
      const reopenTask = await api("POST", `/api/features/${feature.id}/tasks`,
        { name: `E2E reopen ${stamp}`, status: "Todo", priority: "Low" });

      // History that exists BEFORE the first open, so the first open has something to cache.
      await api("PATCH", `/api/tasks/${reopenTask.id}/status`, { status: "InProgress", actor: "e2e-bot" });
      await api("PUT", `/api/tasks/${reopenTask.id}`,
        { name: `E2E reopen ${stamp}`, priority: "High", assignee: "claude", actor: "e2e-bot" });
      await api("PATCH", `/api/tasks/${reopenTask.id}/status`, { status: "Review", actor: "e2e-bot" });

      const modalTimeline = page.locator('[data-testid="activity-timeline"]');
      const openModal = async () => {
        await clickUntil(
          () => page.locator(`[data-testid="outline-task-${reopenTask.id}"]`).click(),
          modalTimeline,
        );
      };

      // Reads one signal per row, keeping ACTIVITY rows and COMMENT rows separable.
      const readRows = () => page.evaluate(() =>
        [...document.querySelectorAll('[data-testid="activity-timeline-entry"]')].map((row) => ({
          kind: row.getAttribute("data-kind"),
          isActivity: !(row.getAttribute("data-kind") ?? "").startsWith("Comment:"),
          text: row.innerText,
          when: row.querySelector("time")?.getAttribute("datetime") ?? null,
          tooltip: row.querySelector("time")?.getAttribute("title") ?? null,
          changes: [...row.querySelectorAll('[data-testid="activity-timeline-change"]')].map((c) => ({
            label: c.querySelector(".kf-change-label")?.textContent?.trim() ?? "",
            before: c.querySelector(".kf-change-before")?.textContent?.trim() ?? "",
            after: c.querySelector(".kf-change-after")?.textContent?.trim() ?? "",
          })),
        })));

      const consoleErrors = [];
      const onConsole = (m) => { if (m.type() === "error") consoleErrors.push(m.text()); };
      page.on("console", onConsole);

      try {
        await page.goto(`${baseUrl}/project/${proj.id}`);
        await page.waitForLoadState("networkidle").catch(() => {});

        // ── First open: caches whatever the trail holds now ──
        await openModal();
        await page.waitForFunction(
          () => [...document.querySelectorAll('[data-testid="activity-timeline-entry"]')]
            .some((r) => !(r.getAttribute("data-kind") ?? "").startsWith("Comment:")),
          null, { timeout: 10000 },
        );

        const first = await readRows();

        // #374 AC3 — a PER-ROW diff assertion. A regex over the whole timeline can take "Priority"
        // from one row and "Low"/"High" from its neighbours and pass while no single row renders a
        // readable change at all, so every cell below is read from ONE row's own change element.
        const cellsOf = (rows) => rows.flatMap((r) => r.changes.map((c) => ({ ...c, kind: r.kind })));
        const firstCells = cellsOf(first);
        for (const [label, before, after] of [
          ["Status", "Todo", "InProgress"],
          ["Priority", "Low", "High"],
        ]) {
          assert(
            firstCells.some((c) => c.label.startsWith(label) && c.before === before && c.after === after),
            `no single row renders "${label}: ${before} → ${after}". Cells: ` +
            JSON.stringify(firstCells).slice(0, 700),
          );
        }

        // #374 AC5 — the machine-readable instant must be an unambiguous UTC one. Stored
        // DateTimes arrive Kind=Unspecified, and ToString("O") on those emits no trailing Z, so a
        // <time datetime> would silently mean LOCAL to every consumer. #337 owns the wire format;
        // this only asserts the rendered attribute.
        const stamps = first.map((r) => r.when);
        assert(stamps.length > 0 && stamps.every((s) => /(Z|[+-]\d{2}:?\d{2})$/.test(s ?? "")),
          `a timeline row's <time datetime> carries no zone designator: ${JSON.stringify(stamps)}`);

        // #374 AC4 — the tooltip's INSTANT, not the presence of a "UTC:" label. A tooltip reading
        // "UTC: 2026-07-12T17:19:00" with no designator wears the label while meaning local.
        const apiTask = await api("GET", `/api/tasks/${reopenTask.id}`);
        const updatedIso = utcIso(apiTask.updatedAt);
        const tooltip = first[0].tooltip ?? "";
        const instant = tooltip.split("UTC:")[1]?.trim().replace(/\)$/, "") ?? "";
        assert(/(Z|[+-]\d{2}:?\d{2})$/.test(instant),
          `the tooltip is labelled UTC but its value "${instant}" has no zone designator. ` +
          `Full title: ${JSON.stringify(tooltip)}`);
        assert(Math.abs(new Date(instant) - new Date(updatedIso)) < 5 * 60 * 1000,
          `the tooltip instant ${instant} is not the instant the API reports (${updatedIso}).`);

        // ── Close, mutate while closed, reopen ──
        await page.locator('[data-testid="edit-item-cancel-button"]').click();
        await modalTimeline.waitFor({ state: "hidden" });

        await api("PATCH", `/api/tasks/${reopenTask.id}/status`, { status: "Done", actor: "e2e-reopen" });

        await openModal();
        // Settle: the comment written by that PATCH always arrives, so waiting on the ACTIVITY row
        // is the only wait that distinguishes the two builds. Swallowed so the assertion below
        // reports what it saw rather than an opaque Playwright timeout.
        await page.waitForFunction(
          () => [...document.querySelectorAll('[data-testid="activity-timeline-entry"]')]
            .some((r) => !(r.getAttribute("data-kind") ?? "").startsWith("Comment:")
              && [...r.querySelectorAll('[data-testid="activity-timeline-change"]')]
                .some((c) => c.querySelector(".kf-change-after")?.textContent?.trim() === "Done")),
          null, { timeout: 10000 },
        ).catch(() => { /* fall through to the diagnostic assertion */ });

        const second = await readRows();

        // The LOOSE signal, computed only so a failure can report it (#374 AC2). This is what a
        // naive innerText assertion would see — and it is true on the BROKEN build too, because
        // the system comment reloads regardless.
        const looseTextMatched = second.some((r) => /Review[\s\S]{0,60}Done/.test(r.text))
          || /Review[\s\S]{0,60}Done/.test(second.map((r) => r.text).join("\n"));

        // The REAL assertion: an ActivityEvent row rendering the change recorded while closed.
        const activityShowsDone = second.some((r) => r.isActivity
          && r.changes.some((c) => c.label.startsWith("Status") && c.after === "Done"));

        assert(activityShowsDone,
          "reopening the outline-sidebar edit modal served a STALE activity trail: no " +
          '[data-kind] ActivityEvent row renders "Status: … → Done", recorded while the modal ' +
          "was closed. The modal is never unmounted, so without the explicit refresh-token bump " +
          "on a fresh open the component keeps the events it cached on the first open.\n" +
          `  loose innerText variant matched: ${looseTextMatched}  <-- #374 AC2: when this is ` +
          "true while the assertion above fails, that IS the demonstration that a text-scoped " +
          "guard is fake-green here — the system comment written by the same PATCH reloads " +
          "whether or not the fix is present.\n" +
          `  rows: ${JSON.stringify(second.map((r) => ({ kind: r.kind, changes: r.changes })))
            .slice(0, 900)}`);

        assert(consoleErrors.length === 0,
          `the timeline logged ${consoleErrors.length} console error(s): ${consoleErrors.slice(0, 3).join(" | ")}`);
      } finally {
        page.off("console", onConsole);
        await page.locator('[data-testid="edit-item-cancel-button"]').click().catch(() => {});
        await api("DELETE", `/api/tasks/${reopenTask.id}`).catch(() => {});
      }
    });

    // ── #357: the task description READS as rendered markdown and EDITS as raw source ──────────
    //
    // These are the behavioural regression tests for #357. They fail on master: before the fix the
    // description was an unconditional <textarea>, so `task-detail-description-read` does not exist
    // and the first waitFor times out. (Mutation-proved: reverting only the TaskDetailPanel markup
    // takes exactly these cases red and leaves the other 35 green.)
    //
    // Template-faithful fixture — this is the exact line sequence descriptionFor() emits at
    // scripts/portfolio-sync.mjs:456-473, not an approximation of it. Every mirrored task on the
    // real board starts with these six lines, and the whole point of the issue is what a HUMAN sees
    // when they do.
    const syncIssueUrl = "https://github.com/joelfuller2016/KanbanFlow-v2/issues/357";
    const syncBlock = [
      "<!-- kanbanflow-sync -->",
      `**Issue:** ${syncIssueUrl}`,
      "**State:** OPEN · **Milestone:** M4: UI / UX Polish · **Labels:** bug, ui",
      "**Open PR:** none",
      "**Synced:** 2026-08-02T21:14:07.482Z",
      "<!-- /kanbanflow-sync -->",
      "",
    ].join("\n");

    // The description save is a blur -> SignalR round-trip -> ITaskService write, so poll the API
    // rather than read once. Same reasoning as waitForApiStatus above.
    async function waitForApiDescription(taskId, predicate, what, timeoutMs = 8000) {
      const deadline = Date.now() + timeoutMs;
      let last;
      do {
        last = (await api("GET", `/api/tasks/${taskId}`)).description;
        if (predicate(last ?? "")) return last ?? "";
        await sleep(200);
      } while (Date.now() < deadline);
      throw new Error(`stored description never ${what}; last value started: ${JSON.stringify((last ?? "").slice(0, 120))}`);
    }

    // Counts what actually reached the DOM inside the read region — elements, attributes and
    // comment NODES. Asserting on innerText alone cannot see an <img> or a surviving handler, and
    // asserting on innerHTML alone cannot tell "not rendered" from "rendered inert".
    const READ_REGION_PROBE = (el) => {
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_COMMENT);
      const comments = [];
      while (walker.nextNode()) comments.push(walker.currentNode.data ?? "");
      return {
        text: el.innerText,
        comments,
        strong: el.querySelectorAll("strong").length,
        scripts: el.querySelectorAll("script").length,
        hrefs: [...el.querySelectorAll("a")].map((a) => a.getAttribute("href")),
        handlerAttrs: [...el.querySelectorAll("*")]
          .flatMap((n) => [...n.attributes].map((a) => a.name))
          .filter((n) => n.toLowerCase().startsWith("on")),
      };
    };

    const syncedTask = await api("POST", `/api/features/${feature.id}/tasks`, {
      name: `E2E synced description ${stamp}`,
      status: "Todo",
      description: `${syncBlock}\n## Summary\n\nA real **body** with a [named link](https://example.com/docs).`,
    });

    await test("Task description renders markdown in read mode, one click opens the raw source (#357)", async () => {
      await page.goto(`${baseUrl}/project/${proj.id}`);
      await clickUntil(
        () => page.locator(`[data-testid="board-card-${syncedTask.id}-title"]`).click(),
        page.locator('[data-testid="task-detail-panel"]'),
      );

      const read = page.locator('[data-testid="task-detail-description-read"]');
      await read.waitFor({ state: "visible" });
      const probe = await read.evaluate(READ_REGION_PROBE);

      // The defect, stated as an assertion: none of this may be visible to a human.
      assert(!probe.text.includes("<!--"), `an HTML comment is visible as text: ${JSON.stringify(probe.text.slice(0, 120))}`);
      assert(!probe.text.includes("kanbanflow-sync"), "the sync marker is visible as text");
      assert(!probe.text.includes("**Issue:**"), "raw ** markers are visible as text");
      // Stronger than "invisible": the sync markers are not in the DOM even as comment nodes.
      // Blazor itself emits harmless `<!--!-->` framework markers around component fragments, so
      // asserting zero comments would test framework internals rather than the sanitiser.
      assert(!probe.comments.some((c) => c.includes("kanbanflow-sync")),
        `a sync marker survived as a comment node: ${JSON.stringify(probe.comments)}`);

      // Non-vacuity: an empty read region would satisfy every assertion above. The block's
      // metadata must have RENDERED, with the issue URL as a working link (AC 4).
      assert(probe.strong >= 4, `expected the ** labels to render as <strong>, saw ${probe.strong}`);
      assert(probe.hrefs.includes(syncIssueUrl), `expected the issue URL to autolink; hrefs = ${JSON.stringify(probe.hrefs)}`);
      assert(probe.hrefs.includes("https://example.com/docs"), `expected the body's named link; hrefs = ${JSON.stringify(probe.hrefs)}`);

      // ONE click into edit, with the caret already in the textarea. A second click would be the
      // roadblock the issue forbids.
      await read.click();
      const edit = page.locator('[data-testid="task-detail-description-edit"]');
      await edit.waitFor({ state: "visible" });
      const focused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(focused === "task-detail-description-edit",
        `after one click the textarea must hold the caret; document.activeElement was ${JSON.stringify(focused)}`);

      // Edit mode shows the RAW source, markers included — that is what makes the round trip
      // below possible at all.
      const raw = await edit.inputValue();
      assert(raw.includes("<!-- kanbanflow-sync -->"), "edit mode must show the raw sync block");
      assert(raw.includes("**Issue:**"), "edit mode must show the raw markdown source");

      // Round trip: type at the end of the raw source, blur, and require the marker block to
      // survive in STORAGE byte-for-byte. Stripping at the storage boundary would silently break
      // the sync's idempotency anchor (#336) and this is the assertion that would catch it.
      const typedSuffix = `\n\nEdited by e2e ${stamp}.`;
      const appended = `${raw}${typedSuffix}`;
      await edit.focus();
      await page.keyboard.press("Control+End");
      await page.keyboard.type(typedSuffix, { delay: 0 });
      assert(await edit.inputValue() === appended,
        "rapid description typing lost or reordered characters before blur");

      // Force an unrelated ProjectView re-render without moving focus. TaskDetailPanel receives
      // its parameters again when the board filter changes; that must not reseed this live edit
      // buffer from the task's last persisted Description.
      // Must match the task under test: a non-matching term filters its card off the board,
      // and the buffer assertion below would then test how the panel reacts to losing its
      // own card — a different question from the one this guard asks.
      const unrelatedFilter = `E2E synced description ${stamp}`;
      await page.locator('[data-testid="board-filter-search"]').evaluate((el, value) => {
        el.value = value;
        el.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: value }));
      }, unrelatedFilter);
      await page.waitForFunction((value) => new URL(window.location.href).searchParams.get("q") === value,
        unrelatedFilter, { timeout: 10000 });
      await page.locator(`[data-testid="board-card-${syncedTask.id}"]`).waitFor({ state: "visible" });
      assert(await edit.inputValue() === appended,
        "an unrelated parent re-render overwrote the unsaved description buffer");

      await page.keyboard.press("Tab"); // blur -> SaveDescriptionAsync -> back to read mode

      await read.waitFor({ state: "visible" });
      const stored = await waitForApiDescription(
        syncedTask.id, (d) => d.includes(`Edited by e2e ${stamp}.`), "picked up the edit");
      assert(stored.includes("<!-- kanbanflow-sync -->"), "the sync block's opening marker was lost on save");
      assert(stored.includes("<!-- /kanbanflow-sync -->"), "the sync block's closing marker was lost on save");
      assert(stored.startsWith(syncBlock), "the stored sync block is no longer byte-identical to what the sync wrote");
      assert(stored.includes(`**Issue:** ${syncIssueUrl}`), "the issue link inside the block was mangled on save");
    });

    await test("Task description: Tab reaches read mode, Enter/Space edit, and no-op blur writes nothing (#357)", async () => {
      await page.goto(`${baseUrl}/project/${proj.id}`);
      await clickUntil(
        () => page.locator(`[data-testid="board-card-${syncedTask.id}-title"]`).click(),
        page.locator('[data-testid="task-detail-panel"]'),
      );

      const read = page.locator('[data-testid="task-detail-description-read"]');
      await read.waitFor({ state: "visible" });
      const editControl = page.locator('[data-testid="task-detail-description-edit-control"]');
      await editControl.waitFor({ state: "visible" });

      // Accessibility-tree guard: the edit action is a real sibling button, not a focusable
      // generic wrapper and not a button role containing the Markdown's interactive links.
      const readSemantics = await read.evaluate((el) => ({
        tabIndex: el.tabIndex,
        role: el.getAttribute("role"),
        ariaLabel: el.getAttribute("aria-label"),
      }));
      assert(readSemantics.tabIndex < 0 && readSemantics.role === null && readSemantics.ariaLabel === null,
        `rendered Markdown still masquerades as an interactive control: ${JSON.stringify(readSemantics)}`);
      assert(await editControl.evaluate((el) => el.tagName === "BUTTON" && el.type === "button"),
        "description edit control is not a native type=button");
      assert(await page.locator('[data-testid="task-detail-panel"]')
        .getByRole("button", { name: "Edit Description", exact: true }).count() === 1,
        "description edit action is missing from the accessibility tree");
      const tabIndex = await editControl.evaluate((el) => el.tabIndex);
      assert(tabIndex === 0, `edit control must be in the tab order; tabIndex was ${tabIndex}`);

      const before = await api("GET", `/api/tasks/${syncedTask.id}`);

      await editControl.focus();
      await page.keyboard.press("Enter");
      const edit = page.locator('[data-testid="task-detail-description-edit"]');
      await edit.waitFor({ state: "visible" });
      const focused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(focused === "task-detail-description-edit",
        `Enter must open the editor with the caret in it; document.activeElement was ${JSON.stringify(focused)}`);

      // Blur without typing. The unchanged-guard in SaveDescriptionAsync must still swallow this:
      // no write, so no ActivityEvent and no UpdatedAt bump. AC 3.
      await page.keyboard.press("Tab");
      await read.waitFor({ state: "visible" });
      await sleep(600); // give a write, if one were going to happen, time to land

      const after = await api("GET", `/api/tasks/${syncedTask.id}`);
      assert(after.updatedAt === before.updatedAt,
        `a no-op description blur wrote to the task: updatedAt ${before.updatedAt} -> ${after.updatedAt}`);
      assert(after.description === before.description, "a no-op description blur changed the stored description");

      // AC 7 names both activation keys. Exercise Space independently after Enter returned the
      // field to read mode, and require the same one-step focus handoff.
      await editControl.focus();
      await page.keyboard.press("Space");
      await edit.waitFor({ state: "visible" });
      const spaceFocused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(spaceFocused === "task-detail-description-edit",
        `Space must open the editor with the caret in it; document.activeElement was ${JSON.stringify(spaceFocused)}`);

      // Escape is the keyboard discard path: restore the session's seed, do not write, and return
      // focus to the read surface instead of stranding it when the textarea disappears.
      const escapedSuffix = `\n\nDiscarded by Escape ${stamp}.`;
      await edit.focus();
      await page.keyboard.press("Control+End");
      await page.keyboard.type(escapedSuffix, { delay: 0 });
      assert((await edit.inputValue()).endsWith(escapedSuffix), "Escape fixture never reached the textarea");
      await page.keyboard.press("Escape");
      await read.waitFor({ state: "visible" });
      const escapeFocused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(escapeFocused === "task-detail-description-edit-control",
        `Escape did not return focus to read mode; document.activeElement was ${JSON.stringify(escapeFocused)}`);
      await sleep(600);
      const afterEscape = await api("GET", `/api/tasks/${syncedTask.id}`);
      assert(afterEscape.updatedAt === before.updatedAt, "Escape wrote an audit-visible task update");
      assert(afterEscape.description === before.description, "Escape persisted the discarded description draft");
      assert(!(await read.innerText()).includes(`Discarded by Escape ${stamp}.`),
        "Escape left the discarded draft in read mode");

      // Rendered links remain independent of the sibling edit button and the content box's
      // pointer shortcut. Space must do nothing; Enter must activate the link without letting
      // its synthetic click bubble to the content box's BeginEdit handler.
      const link = read.locator(`a[href="${syncIssueUrl}"]`).first();

      // Space first: it does not activate the link, so the task page is still loaded when
      // the assertion runs. Pressing Enter BEFORE Space would navigate to the external href;
      // any assertion on the post-navigation page passes trivially (no editor there either).
      await link.focus();
      await page.keyboard.press("Space");
      await sleep(300);
      assert(await page.locator('[data-testid="task-detail-description-edit"]').count() === 0,
        "Space on a rendered link bubbled to the wrapper and opened the description editor");

      // Prove Enter still performs the link's native activation without letting this test leave
      // the app. If the keydown guard regresses, the wrapper enters edit mode before this click.
      await link.evaluate((el) => {
        el.dataset.e2eActivated = "false";
        el.addEventListener("click", (event) => {
          event.preventDefault();
          el.dataset.e2eActivated = "true";
        }, { once: true });
      });
      await page.keyboard.press("Enter");
      await page.waitForFunction((href) =>
        document.querySelector(`a[href="${href}"]`)?.dataset.e2eActivated === "true", syncIssueUrl);
      await sleep(600); // let a regressed Blazor click bubble complete before the negative check
      assert(await page.locator('[data-testid="task-detail-description-edit"]').count() === 0,
        "Enter on a rendered link bubbled to the wrapper and opened the description editor");
      assert(await link.getAttribute("data-e2e-activated") === "true",
        "Enter on a rendered link did not preserve native link activation");
    });

    const emptyDescriptionTask = await api("POST", `/api/features/${feature.id}/tasks`, {
      name: `E2E empty description ${stamp}`,
      status: "Todo",
    });

    await test("An empty task description renders a clickable placeholder (#357)", async () => {
      await page.goto(`${baseUrl}/project/${proj.id}`);
      await clickUntil(
        () => page.locator(`[data-testid="board-card-${emptyDescriptionTask.id}-title"]`).click(),
        page.locator('[data-testid="task-detail-panel"]'),
      );

      const read = page.locator('[data-testid="task-detail-description-read"]');
      await read.waitFor({ state: "visible" });
      assert((await read.innerText()).includes("Add a description..."), "empty read mode did not show its placeholder");

      await read.click();
      const edit = page.locator('[data-testid="task-detail-description-edit"]');
      await edit.waitFor({ state: "visible" });
      assert(await edit.inputValue() === "", "empty description editor did not start empty");
      const focused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(focused === "task-detail-description-edit", "empty placeholder did not enter edit mode in one click");
      await page.keyboard.press("Tab");
    });

    // The provenance test. A task description is a GitHub ISSUE BODY (portfolio-sync writes it), so
    // this payload is what an outside contributor can put on a synced repo — and #357 is the change
    // that turns those bytes into live DOM. Before it they were inert for one accidental reason:
    // they never left a <textarea>.
    const layoutProbeText = `Layout containment ${stamp}`;
    const xssTask = await api("POST", `/api/features/${feature.id}/tasks`, {
      name: `E2E hostile description ${stamp}`,
      status: "Todo",
      description: [
        syncBlock,
        "<img src=x onerror=alert(1)>",
        "",
        "<script>alert(1)</script>",
        "",
        "[click me](javascript:alert(1))",
        "",
        "<a href=\"https://x.example\" onclick=\"steal()\">x</a>",
        "",
        `<p style="position:fixed;inset:0;top:0;right:0;bottom:0;left:0;width:100vw;height:100vh;min-width:100vw;min-height:100vh;z-index:999999;transform:scale(2);display:block;overflow:visible;pointer-events:auto;background-color:red">${layoutProbeText}</p>`,
        "",
        "This part is **bold** and must survive.",
      ].join("\n"),
    });

    await test("A hostile issue body in a description is inert and panel-contained (#357/#381)", async () => {
      // Arm a real dialog listener: Playwright auto-dismisses dialogs when nobody is listening, so
      // "no alert appeared" proves nothing unless something is watching for one.
      const dialogs = [];
      const onDialog = async (d) => { dialogs.push(d.message()); await d.dismiss().catch(() => {}); };
      const pageErrors = [];
      const onPageError = (e) => pageErrors.push(e.message);
      page.on("dialog", onDialog);
      page.on("pageerror", onPageError);

      try {
        await page.goto(`${baseUrl}/project/${proj.id}`);
        await clickUntil(
          () => page.locator(`[data-testid="board-card-${xssTask.id}-title"]`).click(),
          page.locator('[data-testid="task-detail-panel"]'),
        );

        const read = page.locator('[data-testid="task-detail-description-read"]');
        await read.waitFor({ state: "visible" });
        const probe = await read.evaluate(READ_REGION_PROBE);
        await sleep(500); // an onerror/onload handler would have fired well inside this

        assert(probe.scripts === 0, `a <script> element reached the DOM (${probe.scripts})`);
        assert(probe.handlerAttrs.length === 0,
          `event-handler attributes survived sanitisation: ${JSON.stringify(probe.handlerAttrs)}`);
        assert(!probe.hrefs.some((h) => (h ?? "").toLowerCase().startsWith("javascript:")),
          `a javascript: href survived: ${JSON.stringify(probe.hrefs)}`);
        assert(dialogs.length === 0, `alert() fired: ${JSON.stringify(dialogs)}`);
        assert(pageErrors.length === 0, `the page threw while rendering hostile markup: ${JSON.stringify(pageErrors)}`);

        // CSS is part of the sanitizer boundary too. Stored Markdown must not position a child
        // against the viewport or size it beyond the task panel that owns the description.
        const layoutProbe = read.getByText(layoutProbeText, { exact: true });
        await layoutProbe.waitFor({ state: "visible" });
        const geometry = await layoutProbe.evaluate((el) => {
          const probeRect = el.getBoundingClientRect();
          const panelRect = el.closest('[data-testid="task-detail-panel"]').getBoundingClientRect();
          const cornerHit = document.elementFromPoint(8, 8);
          return {
            position: getComputedStyle(el).position,
            ownsViewportCorner: cornerHit === el || el.contains(cornerHit),
            probe: { left: probeRect.left, top: probeRect.top, right: probeRect.right, bottom: probeRect.bottom },
            panel: { left: panelRect.left, top: panelRect.top, right: panelRect.right, bottom: panelRect.bottom },
          };
        });
        assert(geometry.position !== "fixed", `hostile CSS retained position:fixed: ${JSON.stringify(geometry)}`);
        assert(!geometry.ownsViewportCorner,
          `hostile Markdown intercepted a viewport hit-test outside its task panel: ${JSON.stringify(geometry)}`);
        assert(geometry.probe.left >= geometry.panel.left - 1
          && geometry.probe.right <= geometry.panel.right + 1
          && geometry.probe.top >= geometry.panel.top - 1
          && geometry.probe.bottom <= geometry.panel.bottom + 1,
        `hostile Markdown escaped the task panel: ${JSON.stringify(geometry)}`);

        // Non-vacuity: this must not pass because nothing rendered.
        assert(probe.strong >= 4, `benign markdown stopped rendering alongside the payload (strong=${probe.strong})`);
        assert(probe.text.includes("bold"), "the benign body text is missing — the payload check would be vacuous");
      } finally {
        page.off("dialog", onDialog);
        page.off("pageerror", onPageError);
      }
    });

    await test("Edit Task modal renders the description too, from the same shared component (#357)", async () => {
      await page.goto(`${baseUrl}/project/${proj.id}`);
      await clickUntil(
        () => page.locator(`[data-testid="outline-task-${syncedTask.id}"]`).click(),
        page.locator('[data-testid="edit-item-modal"] [data-testid="edit-item-description-read"]'),
      );

      const read = page.locator('[data-testid="edit-item-description-read"]');
      const probe = await read.evaluate(READ_REGION_PROBE);
      assert(!probe.text.includes("<!--"), "the modal still shows an HTML comment as text");
      assert(!probe.comments.some((c) => c.includes("kanbanflow-sync")),
        `a sync marker survived as a modal comment node: ${JSON.stringify(probe.comments)}`);
      assert(probe.hrefs.includes(syncIssueUrl), `the modal did not autolink the issue URL; hrefs = ${JSON.stringify(probe.hrefs)}`);

      // Identical interaction to the panel: one click, caret in the raw source.
      await read.click();
      const edit = page.locator('[data-testid="edit-item-description-edit"]');
      await edit.waitFor({ state: "visible" });
      const focused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(focused === "edit-item-description-edit",
        `the modal must behave like the panel; document.activeElement was ${JSON.stringify(focused)}`);
      const raw = await edit.inputValue();
      assert(raw.includes("<!-- kanbanflow-sync -->"), "the modal's edit mode must show the raw sync block");

      // The modal owns persistence through Save Changes, so moving focus must not collapse its
      // textarea back to read mode. That would be a surprising state change while the form is open.
      await page.locator('#edit-item-name').focus();
      assert(await edit.isVisible(), "modal description left edit mode on blur before Save/Cancel");

      // The dialog's native Escape handler runs before Blazor's delegated textarea handler. The
      // field must own this key: discard only its draft, keep the modal open, and restore focus.
      const modalEscapeSuffix = `\n\nDiscarded by modal Escape ${stamp}.`;
      await edit.focus();
      await page.keyboard.press("Control+End");
      await page.keyboard.type(modalEscapeSuffix, { delay: 0 });
      await page.keyboard.press("Escape");
      await read.waitFor({ state: "visible" });
      assert(await page.locator('[data-testid="edit-item-modal"]').isVisible(),
        "Escape in the description editor closed the entire edit modal");
      const modalEscapeFocused = await page.evaluate(() => document.activeElement?.getAttribute("data-testid"));
      assert(modalEscapeFocused === "edit-item-description-edit-control",
        `modal Escape did not return focus to description read mode; active element was ${JSON.stringify(modalEscapeFocused)}`);
      assert(!(await read.innerText()).includes(`Discarded by modal Escape ${stamp}.`),
        "modal Escape left the discarded description draft in read mode");

      // Cancel must DISCARD the buffer, and reopening must land in READ mode showing the original
      // text — that reopen is what descriptionResetToken exists for.
      await read.click();
      await edit.waitFor({ state: "visible" });
      await edit.focus();
      await page.keyboard.press("Control+End");
      await page.keyboard.type(`\n\nDiscarded ${stamp}.`, { delay: 0 });
      await page.locator('[data-testid="edit-item-cancel-button"]').click();
      // Negative assertion: a regression's write would arrive after an immediate read. Give
      // it time to land before declaring that none did — same pattern as the positive legs.
      await page.locator('[data-testid="edit-item-modal"]').waitFor({ state: "hidden" });
      await sleep(600);
      const afterCancel = await api("GET", `/api/tasks/${syncedTask.id}`);
      assert(!afterCancel.description.includes(`Discarded ${stamp}.`),
        "Cancel persisted a buffered modal description edit");

      await clickUntil(
        () => page.locator(`[data-testid="outline-task-${syncedTask.id}"]`).click(),
        page.locator('[data-testid="edit-item-modal"] [data-testid="edit-item-description-read"]'),
      );
      assert(await page.locator('[data-testid="edit-item-description-edit"]').count() === 0,
        "reopening the modal inherited the previous session's edit mode");
      assert(!(await read.evaluate((el) => el.innerText)).includes(`Discarded ${stamp}.`),
        "the discarded buffer survived into the reopened modal");

      // Save Changes must persist a buffered edit through the modal's save flow.
      await read.click();
      await edit.waitFor({ state: "visible" });
      // The component focuses the textarea from OnAfterRenderAsync, so visibility is not proof
      // the caret has landed. Park it explicitly, exactly as the first edit leg above does.
      await edit.focus();
      await page.keyboard.press("Control+End");
      await page.keyboard.type(`\n\nSaved from modal ${stamp}.`, { delay: 0 });
      await page.locator('[data-testid="edit-item-save-button"]').click();
      const saved = await waitForApiDescription(
        syncedTask.id, (d) => d.includes(`Saved from modal ${stamp}.`), "picked up the modal save");
      assert(saved.startsWith(syncBlock), "the modal save mangled the sync block");
    });

    await api("DELETE", `/api/tasks/${syncedTask.id}`).catch(() => {});
    await api("DELETE", `/api/tasks/${xssTask.id}`).catch(() => {});
    await api("DELETE", `/api/tasks/${emptyDescriptionTask.id}`).catch(() => {});

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
    await api("DELETE", `/api/projects/${filterProj.id}`).catch(() => {});
    await api("DELETE", `/api/projects/${outlineProj.id}`).catch(() => {});
    await api("DELETE", `/api/projects/${cardProj.id}`).catch(() => {});
    await api("DELETE", `/api/projects/${calmProj.id}`).catch(() => {});
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
