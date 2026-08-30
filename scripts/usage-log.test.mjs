// Tests for the MCP usage recorder (#477 §11). Real code, no stubs: every assertion calls the
// exported function under test, and the filesystem cases use a real temp directory rather than a
// mocked fs — the properties that matter here (does an append land, does an unwritable path stay
// silent) are properties of the actual filesystem calls.
//
// Run: node --test scripts/

import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { resolveUsageLogPath, buildRow, createUsageLogger, DEFAULT_MAX_BYTES } from "./usage-log.mjs";

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "kbf-usage-"));
const readRows = file =>
  fs.readFileSync(file, "utf8").split("\n").filter(l => l.trim()).map(l => JSON.parse(l));

// --- 1. Where the log goes ----------------------------------------------------------------

test("defaults beside the database in ~/.kanbanflow, never inside the repo", () => {
  const resolved = resolveUsageLogPath({}, "C:\\Users\\Someone");
  assert.equal(resolved, path.join("C:\\Users\\Someone", ".kanbanflow", "mcp-usage.jsonl"));
});

test("KANBANFLOW_USAGE_LOG overrides the location and is resolved to an absolute path", () => {
  const resolved = resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: "relative/log.jsonl" }, os.homedir());
  assert.equal(resolved, path.resolve("relative/log.jsonl"));
  assert.ok(path.isAbsolute(resolved));
});

test("recording can be switched off, and an empty value is off rather than a file named ''", () => {
  for (const value of ["off", "OFF", "0", "false", "no", "none", "disabled", "", "   "]) {
    assert.equal(resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: value }, os.homedir()), null,
      `expected ${JSON.stringify(value)} to disable recording`);
  }
});

// --- 2. The field allowlist ---------------------------------------------------------------

test("records the allowlisted argument fields", () => {
  const row = buildRow({
    tool: "claim_task", sessionId: "abcd1234", cwd: "C:\\work", outcome: "ok", ms: 12,
    args: { taskId: 42, projectId: 2, actor: "agent-a" },
    now: new Date("2026-08-30T22:00:00.000Z")
  });
  assert.equal(row.ts, "2026-08-30T22:00:00.000Z");
  assert.equal(row.session, "abcd1234");
  assert.equal(row.tool, "claim_task");
  assert.equal(row.cwd, "C:\\work");
  assert.equal(row.outcome, "ok");
  assert.equal(row.ms, 12);
  assert.equal(row.taskId, 42);
  assert.equal(row.projectId, 2);
  assert.equal(row.actor, "agent-a");
});

test("NEVER copies an argument outside the allowlist, however innocuous it looks", () => {
  const row = buildRow({
    tool: "create_task", sessionId: "s", cwd: "C:\\work", outcome: "ok", ms: 1,
    args: {
      taskId: 7,
      name: "Rotate the production signing key",
      description: "full issue body with customer names",
      body: "comment text",
      query: "search terms",
      observations: ["memory content"]
    }
  });
  assert.equal(row.taskId, 7, "the allowlisted field still comes through");
  for (const leaked of ["name", "description", "body", "query", "observations"]) {
    assert.equal(row[leaked], undefined, `${leaked} must never reach the usage log`);
  }
});

test("an allowlisted key holding an object is dropped, not smuggled through", () => {
  const row = buildRow({
    tool: "get_task", sessionId: "s", cwd: "C:\\work", outcome: "ok", ms: 1,
    args: { taskId: { nested: "payload" }, actor: ["a", "b"] }
  });
  assert.equal(row.taskId, undefined);
  assert.equal(row.actor, undefined);
});

// --- 3. The fields the signals depend on --------------------------------------------------

test("a status change records its target status as `to`", () => {
  const row = buildRow({ tool: "update_task_status", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1,
    args: { taskId: 5, status: "Done" } });
  assert.equal(row.to, "Done");
});

test("a DoD override is recorded as a flag, never as its reason text", () => {
  const withReason = buildRow({ tool: "update_task_status", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1,
    args: { taskId: 5, status: "Done", overrideReason: "shipped under a waiver signed by X" } });
  assert.equal(withReason.override, true);
  assert.equal(JSON.stringify(withReason).includes("waiver"), false, "reason text must not reach the log");

  const blank = buildRow({ tool: "update_task_status", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1,
    args: { taskId: 5, status: "Done", overrideReason: "   " } });
  assert.equal(blank.override, undefined, "whitespace-only is not an override");
});

test("an error records the HTTP status that distinguishes a collision from a gate refusal", () => {
  const conflict = buildRow({ tool: "claim_task", sessionId: "s", cwd: "C:\\w", outcome: "error", ms: 3,
    args: { taskId: 9 }, error: { code: -32000, data: { status: 409 } } });
  assert.equal(conflict.http, 409);
  assert.equal(conflict.code, -32000);

  const gate = buildRow({ tool: "update_task_status", sessionId: "s", cwd: "C:\\w", outcome: "error", ms: 3,
    args: { taskId: 9, status: "Done" }, error: { code: -32000, data: { status: 422 } } });
  assert.equal(gate.http, 422);

  const transport = buildRow({ tool: "list_projects", sessionId: "s", cwd: "C:\\w", outcome: "error", ms: 3,
    args: {}, error: { code: -32000, message: "REST request failed" } });
  assert.equal(transport.http, undefined, "a transport failure has no HTTP status to record");
});

// --- 4. The recorder against a real filesystem --------------------------------------------

test("writes exactly one parseable JSONL row per call", () => {
  const dir = tmp();
  const logPath = path.join(dir, "nested", "usage.jsonl");
  const logger = createUsageLogger({ logPath, sessionId: "sess0001", cwd: "C:\\elsewhere" });

  logger.record({ tool: "get_next_tasks", args: { projectId: 2 }, outcome: "ok", ms: 5 });
  logger.record({ tool: "claim_task", args: { taskId: 3, actor: "a" }, outcome: "ok", ms: 6 });

  const rows = readRows(logPath);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].tool, "get_next_tasks");
  assert.equal(rows[0].session, "sess0001");
  assert.equal(rows[0].cwd, "C:\\elsewhere");
  assert.equal(rows[1].taskId, 3);
});

test("a logging failure NEVER propagates — the tool call must survive a broken log", () => {
  const dir = tmp();
  // A FILE where the log's parent directory should be: mkdir fails ENOTDIR, append fails too.
  const blocker = path.join(dir, "not-a-directory");
  fs.writeFileSync(blocker, "x");
  const logPath = path.join(blocker, "usage.jsonl");

  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w" });
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
  assert.equal(fs.existsSync(logPath), false);
});

test("a null path disables recording without touching the filesystem", () => {
  const logger = createUsageLogger({ logPath: null });
  assert.equal(logger.enabled, false);
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
});

test("rotates once past the size cap instead of growing without bound", () => {
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  // Small cap so a handful of real rows crosses it; the production cap is 5 MB.
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: 300 });

  for (let i = 0; i < 12; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });

  assert.ok(fs.existsSync(`${logPath}.1`), "the previous generation is kept as .1");
  assert.ok(fs.statSync(logPath).size <= 300 + 200, "the live log stays near the cap");
  assert.ok(readRows(logPath).length >= 1, "and remains parseable after rotation");
});

test("the production cap is 5 MB", () => {
  assert.equal(DEFAULT_MAX_BYTES, 5 * 1024 * 1024);
});

// --- 5. Test harnesses must not contaminate the trial -------------------------------------

test("the MCP test harnesses spawn the wrapper with recording OFF", () => {
  // smoke-mcp.mjs calls get_next_tasks, claim_task and update_task_status for real. If a smoke
  // run were recorded it would present as a session that consulted the board, inflating the very
  // §11 signals the trial adjudicates on — the project's own test suite counted as evidence of
  // its own adoption. Verified by running it too (a smoke run with KANBANFLOW_USAGE_LOG pointed
  // at a temp path leaves no file), but asserted here so removing the line goes red.
  const dir = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
  for (const harness of ["smoke-mcp.mjs", "framing-ab.mjs"]) {
    const source = fs.readFileSync(path.join(dir, harness), "utf8");
    assert.match(source, /KANBANFLOW_USAGE_LOG:\s*"off"/,
      `${harness} drives the wrapper for real and must spawn it with usage recording off`);
  }
});
