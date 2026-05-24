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

    // ── Scenario E: Settings -> Run all shows an all-passed result table ──
    await test("Settings 'Run all' reports an all-passed result", async () => {
      await page.goto(`${baseUrl}/settings`);
      await page.getByRole("button", { name: "Run all" }).click();
      await page.locator('[data-testid="settings-test-results"]').waitFor({ state: "visible" });
      // green summary badge present, and no failing rows
      await page.locator(".badge.text-bg-success").first().waitFor({ state: "visible" });
      const failedRows = await page.locator("tr.table-danger").count();
      assert(failedRows === 0, `expected 0 failed rows in the UI, found ${failedRows}`);
    });

    // ── Cleanup of API-created data (best effort) ──
    await api("DELETE", `/api/projects/${proj.id}`).catch(() => {});
    // delete the modal-created project too
    const all = await api("GET", "/api/projects");
    const modalProj = all.find((p) => p.name === modalProjectName);
    if (modalProj) await api("DELETE", `/api/projects/${modalProj.id}`).catch(() => {});
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
