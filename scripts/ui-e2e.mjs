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
      // success banner with an "Open new project" link appears, and the card now shows Promoted
      await page.locator('[data-testid="idea-promote-success-link"]').waitFor({ state: "visible" });
      await page.locator(`[data-testid="idea-card-status-${createdIdea.id}"]`)
        .getByText("Promoted", { exact: true }).waitFor({ state: "visible" });
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
