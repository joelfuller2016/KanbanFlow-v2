// Tests for the MCP usage recorder (#477 §11). Real code, no stubs: every assertion calls the
// exported function under test, and the filesystem cases use a real temp directory rather than a
// mocked fs — the properties that matter here (does an append land, does an unwritable path stay
// silent, does a concurrent rotation destroy the archive) are properties of the actual filesystem
// calls, and one of them is a property of actual concurrent PROCESSES.
//
// Run: node --test scripts/

import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { spawn } from "node:child_process";

import {
  resolveUsageLogPath,
  buildRow,
  createUsageLogger,
  DEFAULT_MAX_BYTES,
  MAX_ARCHIVE_GENERATIONS,
  MAX_FIELD_CHARS
} from "./usage-log.mjs";

// fileURLToPath, never `new URL(...).pathname`: the pathname is PERCENT-ENCODED, so a checkout
// under "C:\My Repos\..." yields "/C:/My%20Repos/..." and every fs call built from it throws
// ENOENT. The sibling test file already got this right; this one did not.
const here = path.dirname(fileURLToPath(import.meta.url));
// A file:// URL, not a bare path: an ESM import of "C:\..." fails with ERR_UNSUPPORTED_ESM_URL_SCHEME.
const usageLogModuleUrl = pathToFileURL(path.join(here, "usage-log.mjs")).href;

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "kbf-usage-"));
const readRows = file =>
  fs.readFileSync(file, "utf8").split("\n").filter(l => l.trim()).map(l => JSON.parse(l));
const readRowsIfPresent = file => (fs.existsSync(file) ? readRows(file) : []);

/** Every row still on disk: the live file plus every surviving archive generation. */
function readAllGenerations(logPath) {
  const rows = readRowsIfPresent(logPath);
  for (let i = 1; i <= MAX_ARCHIVE_GENERATIONS; i++) rows.push(...readRowsIfPresent(`${logPath}.${i}`));
  return rows;
}

// --- 1. Where the log goes ----------------------------------------------------------------

test("defaults beside the database in ~/.kanbanflow, never inside the repo", () => {
  const resolved = resolveUsageLogPath({}, "C:\\Users\\Someone");
  assert.equal(resolved, path.join("C:\\Users\\Someone", ".kanbanflow", "mcp-usage.jsonl"));
});

test("KANBANFLOW_USAGE_LOG overrides the location when it is absolute", () => {
  const absolute = path.join(os.tmpdir(), "kbf-explicit", "log.jsonl");
  const resolved = resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: absolute }, os.homedir());
  assert.equal(resolved, path.resolve(absolute));
  assert.ok(path.isAbsolute(resolved));
});

test("a leading ~/ expands against the home directory instead of creating a folder named '~'", () => {
  const home = path.join(os.tmpdir(), "kbf-fake-home");
  assert.equal(
    resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: "~/logs/usage.jsonl" }, home),
    path.join(home, "logs", "usage.jsonl")
  );
  assert.equal(
    resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: "~\\logs\\usage.jsonl" }, home),
    path.join(home, "logs\\usage.jsonl")
  );
  assert.equal(resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: "~" }, home), home);
});

test("a RELATIVE path is refused, not silently resolved against the client's cwd", () => {
  // Each client starts the wrapper in its own working directory — that difference IS §11 signal 4.
  // Resolving a relative path would therefore write a DIFFERENT file per session and the report
  // would read one of them and call it the total, with nothing anywhere saying so.
  assert.throws(
    () => resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: "relative/log.jsonl" }, os.homedir()),
    /absolute path/i
  );
  assert.throws(
    () => resolveUsageLogPath({ KANBANFLOW_USAGE_LOG: "./log.jsonl" }, os.homedir()),
    /KANBANFLOW_USAGE_LOG/
  );
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

// --- 3. Every recorded value is BOUNDED ----------------------------------------------------

test("an enormous actor cannot write an enormous row", () => {
  // Measured against the unbounded version: one call with a 2 MB `actor` wrote a 2,097,287-byte
  // row, so roughly three of them filled the whole 5 MB cap — and the error path records the raw
  // arguments of a call the SERVER HAS JUST REJECTED, so nothing upstream had validated them.
  const row = buildRow({
    tool: "claim_task", sessionId: "s", cwd: "C:\\w", outcome: "error", ms: 1,
    args: { taskId: 1, actor: "A".repeat(2 * 1024 * 1024) },
    error: { code: -32000, data: { status: 409 } }
  });
  assert.equal(row.actor.length, MAX_FIELD_CHARS);
  assert.ok(Buffer.byteLength(JSON.stringify(row)) < 1024,
    `a row built from a 2 MB argument is ${Buffer.byteLength(JSON.stringify(row))} bytes`);
});

test("every recorded string is bounded, not just the arguments", () => {
  const long = "x".repeat(5000);
  const row = buildRow({
    tool: long, sessionId: long, cwd: long, outcome: long, ms: 1,
    args: { actor: long, status: long }
  });
  for (const field of ["tool", "session", "cwd", "outcome", "actor", "to"]) {
    assert.equal(row[field].length, MAX_FIELD_CHARS, `${field} must be truncated`);
  }
});

test("an id sent as a STRING is recorded, as the number the wrapper coerced it to", () => {
  // A live end-to-end run caught this as a regression from the row-hardening work. `coerceInt` in
  // mcp-server.mjs deliberately accepts "194" — LLM clients routinely send ids that way — so the
  // call SUCCEEDS and the board is really written. Gating the log on Number.isSafeInteger alone
  // therefore recorded no taskId at all, and two sessions that genuinely collided on task 194 read
  // as a single writer: the trial scored ADOPT on data containing the collision it was hunting.
  const row = buildRow({
    tool: "claim_task", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1,
    args: { taskId: "194", projectId: "2" }
  });
  assert.equal(row.taskId, 194, "a base-10 integer string is coerced, not dropped");
  assert.equal(row.projectId, 2);
  assert.equal(typeof row.taskId, "number",
    "and recorded as a NUMBER, so one task cannot bucket as two ids in the report");
  assert.equal(buildRow({
    tool: "claim_task", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1, args: { taskId: "-7" }
  }).taskId, -7, "a signed integer string too, matching coerceInt");
});

test("ids the WRAPPER would have rejected are not recorded either", () => {
  // The log must agree with what mcp-server.mjs actually accepted: everything below throws
  // invalid-params there, so none of it can describe a call that touched the board.
  const rejected = {
    "a huge numeric string": "9".repeat(500),
    "a decimal string": "1.5",
    "a whitespace-padded string": " 3 ",
    "a hex string": "0x10",
    "an exponent string": "1e3",
    "an empty string": "",
    "a boolean": true,
    "an array": [5],
    "an object": { id: 5 },
    "a fraction": 1.5,
    "NaN": Number.NaN,
    "beyond the safe-integer range": Number.MAX_SAFE_INTEGER + 2
  };
  for (const [label, value] of Object.entries(rejected)) {
    const row = buildRow({
      tool: "get_task", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1, args: { taskId: value }
    });
    assert.equal(row.taskId, undefined, `${label} must not be recorded as an id`);
  }

  const good = buildRow({
    tool: "get_task", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: 1,
    args: { taskId: 42, projectId: 7 }
  });
  assert.equal(good.taskId, 42);
  assert.equal(good.projectId, 7);
});

test("a non-finite duration is omitted rather than serialised as null", () => {
  const row = buildRow({ tool: "t", sessionId: "s", cwd: "C:\\w", outcome: "ok", ms: Number.NaN });
  assert.equal(row.ms, undefined);
  assert.equal(JSON.stringify(row).includes("null"), false);
});

// --- 4. The fields the signals depend on --------------------------------------------------

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

// --- 5. The recorder against a real filesystem --------------------------------------------

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
  assert.equal(logger.enabled, true);
  assert.equal(logger.failures, 0);
});

test("a logging failure NEVER propagates — the tool call must survive a broken log", () => {
  const dir = tmp();
  // A FILE where the log's parent directory should be: mkdir fails ENOTDIR, append fails too.
  const blocker = path.join(dir, "not-a-directory");
  fs.writeFileSync(blocker, "x");
  const logPath = path.join(blocker, "usage.jsonl");

  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", warn: () => {} });
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
  assert.equal(fs.existsSync(logPath), false);
});

test("logger construction NEVER propagates a failure while resolving its defaults", () => {
  const options = {};
  Object.defineProperty(options, "logPath", {
    get() { throw new Error("simulated homedir failure"); }
  });
  let logger;
  assert.doesNotThrow(() => { logger = createUsageLogger(options); });
  assert.equal(logger.enabled, false);
  assert.ok(logger.lastError, "the construction failure is still reported, not swallowed silently");
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
});

test("a broken log reports itself broken instead of looking like an unused board", () => {
  // Reproduced by pointing KANBANFLOW_USAGE_LOG at a DIRECTORY: record() returned perfectly
  // happily, `enabled` stayed true, nothing was ever written and no signal existed anywhere that
  // the instrument was dead. For a two-week trial adjudicated on "did anyone use the board", a
  // silent instrument and an unused board are the same picture.
  const dir = tmp();
  const logPath = path.join(dir, "a-directory-not-a-file");
  fs.mkdirSync(logPath);

  const warnings = [];
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", warn: m => warnings.push(m) });

  assert.equal(logger.enabled, false, "a log that cannot be written is not enabled");
  assert.ok(logger.failures > 0, "the failure is counted");
  assert.ok(logger.lastError, "and the reason is kept");

  for (let i = 0; i < 25; i++) logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 });

  assert.equal(warnings.length, 1,
    `exactly one stderr line per session, got ${warnings.length} — this process speaks JSON-RPC ` +
    "on stdout and its stderr is the client's log");
  assert.equal(logger.enabled, false);
});

test("a TRANSIENT failure does not disable recording for the rest of the session", () => {
  // `ready = true` used to be set BEFORE the try in init(), so one mkdir failure — an antivirus
  // holding the directory, a home drive still mounting — permanently disabled recording even after
  // the obstruction cleared, for a session that could last all day.
  const dir = tmp();
  const blocker = path.join(dir, "obstruction");
  fs.writeFileSync(blocker, "x");
  const logPath = path.join(blocker, "usage.jsonl");

  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", warn: () => {} });
  logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 });
  assert.equal(logger.enabled, false, "broken while the obstruction is there");

  fs.unlinkSync(blocker);   // the obstruction clears
  logger.record({ tool: "get_next_tasks", args: { projectId: 2 }, outcome: "ok", ms: 1 });

  assert.equal(logger.enabled, true, "recording resumes once the path is writable again");
  const rows = readRows(logPath);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].tool, "get_next_tasks");
});

test("a null path disables recording without touching the filesystem", () => {
  const logger = createUsageLogger({ logPath: null });
  assert.equal(logger.enabled, false);
  assert.doesNotThrow(() => logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 }));
});

// --- 6. Rotation ---------------------------------------------------------------------------

test("rotates once past the size cap instead of growing without bound", () => {
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  // Small cap so a handful of real rows crosses it; the production cap is 5 MB.
  const cap = 500;
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: cap });

  for (let i = 0; i < 12; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });

  assert.ok(fs.existsSync(`${logPath}.1`), "the previous generation is kept as .1");
  assert.ok(fs.statSync(logPath).size <= cap,
    `live log is ${fs.statSync(logPath).size} bytes against a ${cap}-byte cap`);
  assert.ok(readRows(logPath).length >= 1, "and remains parseable after rotation");
  assert.equal(readAllGenerations(logPath).length, 12, "and no row was dropped along the way");
});

test("the production cap is 5 MB", () => {
  assert.equal(DEFAULT_MAX_BYTES, 5 * 1024 * 1024);
});

test("the cap counts BYTES, so a non-ASCII cwd cannot overshoot it", () => {
  // Reported by Copilot on PR #485. The check used line.length (UTF-16 code units) while the
  // accounting added Buffer.byteLength, so every row holding a multi-byte character let the live
  // file drift further past the cap.
  //
  // The exact invariant matters here, and getting it wrong makes the test a decoration. Rotation
  // happens BEFORE the append that would breach the cap and re-stats the file under the rotation
  // lock, so WITHIN ONE PROCESS the live file never exceeds maxBytes at all. (Across concurrent
  // processes it can: each tracks only its own contribution, so a second writer's bytes are
  // invisible until someone attempts a rotation and re-stats. The cap is a single-process
  // invariant and a multi-process approximation — the file-comment in usage-log.mjs says the same.)
  //
  // So the assertion is `size <= cap`, flat, for this single-process case. A first version allowed
  // `cap + oneRow` of slack and stayed green with the bug reinstated — the slack was larger than
  // the entire defect.
  //
  // Each CJK character below is 1 UTF-16 unit but 3 UTF-8 bytes, so the undercount is ~120 bytes
  // per row — far more than enough to breach a flat cap.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const cap = 600;
  const cwd = `C:\\${"項目".repeat(30)}`;
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd, maxBytes: cap });

  for (let i = 0; i < 30; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });

  const size = fs.statSync(logPath).size;
  assert.ok(size <= cap,
    `live log is ${size} bytes against a ${cap}-byte cap; rotation happens before the breaching ` +
    "append, so the check and the accounting must use the same unit");
});

test("a log that is ALREADY oversized when the session starts rotates on the first record", () => {
  // init() reads the existing size, and nothing tested that it did anything with it. A wrapper
  // attaching to a log left oversized by a previous session must rotate immediately, not append
  // to it for another whole session.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const preexisting = `${JSON.stringify({ ts: "old", tool: "from-a-previous-session" })}\n`.repeat(20);
  fs.writeFileSync(logPath, preexisting);

  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: 300 });
  assert.ok(fs.statSync(logPath).size > 300, "precondition: the log starts over the cap");

  logger.record({ tool: "list_projects", args: { projectId: 1 }, outcome: "ok", ms: 1 });

  assert.equal(readRows(logPath).length, 1, "the live log holds only the new row");
  assert.equal(fs.readFileSync(`${logPath}.1`, "utf8"), preexisting,
    "and the previous session's rows were archived intact, not appended to or discarded");
});

test("rotation SHIFTS generations so an older archive is never overwritten", () => {
  // The old implementation renamed straight onto `.1` and relied on the rename replacing whatever
  // was there. The second rotation therefore destroyed the first one's evidence, in a single
  // process, with no concurrency needed at all.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  // Cap chosen so 40 rows span four rotations and still fit inside the generation bound: this
  // test is about NOT overwriting an archive, so nothing may age out legitimately either.
  const cap = 1000;
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: cap });

  const total = 40;
  for (let i = 0; i < total; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });

  assert.ok(fs.existsSync(`${logPath}.2`), "a second rotation must create .2, not reuse .1");
  const ids = readAllGenerations(logPath).map(r => r.projectId).sort((a, b) => a - b);
  assert.deepEqual(ids, Array.from({ length: total }, (_, i) => i),
    "every row written is still on disk somewhere across the generations");

  // .1 is the NEWEST archive and higher numbers are older: that ordering is the contract the
  // report and any operator reading the directory depend on.
  const newest = readRows(`${logPath}.1`).map(r => r.projectId);
  const older = readRows(`${logPath}.2`).map(r => r.projectId);
  assert.ok(Math.min(...newest) > Math.max(...older), ".1 must hold newer rows than .2");
});

test("history is bounded, and rotation keeps working once the bound is reached", () => {
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const cap = 200;
  const total = 200;
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: cap });

  for (let i = 0; i < total; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });

  assert.ok(fs.existsSync(`${logPath}.${MAX_ARCHIVE_GENERATIONS}`), "the bound is actually reached");
  assert.equal(fs.existsSync(`${logPath}.${MAX_ARCHIVE_GENERATIONS + 1}`), false,
    "and nothing is kept beyond it");

  // Reaching the bound must DROP THE OLDEST GENERATION, not stop rotating. A version that simply
  // refused to rotate once every slot was taken also satisfied the two assertions above while
  // letting the live log grow without limit.
  assert.ok(fs.statSync(logPath).size <= cap,
    `the live log is ${fs.statSync(logPath).size} bytes against a ${cap}-byte cap — rotation stopped`);
  const newest = readRows(`${logPath}.1`).map(r => r.projectId);
  assert.ok(Math.max(...newest) > total - 20,
    ".1 must hold the most RECENT rows; the oldest generation is the one that falls off the end");
});

test("an EMPTY live file is never rotated over a real archive", () => {
  // A single frame far larger than the whole cap used to rotate the log twice in a row and wipe
  // out the archive to make room for it — measured at 24,098 rows of real evidence annihilated by
  // one 8 MB call. Overshooting the cap by one row is the much cheaper mistake.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const archive = `${JSON.stringify({ ts: "old", tool: "precious" })}\n`;
  fs.writeFileSync(`${logPath}.1`, archive);

  // Cap far below one row, so the very first record on an empty live file wants to rotate.
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: 20 });
  logger.record({ tool: "list_projects", args: { projectId: 1 }, outcome: "ok", ms: 1 });

  assert.equal(fs.readFileSync(`${logPath}.1`, "utf8"), archive, "the archive is untouched");
  assert.equal(fs.existsSync(`${logPath}.2`), false, "and nothing was shifted to make room");
  assert.equal(readRows(logPath).length, 1, "the oversized row is simply written");
});

test("a rotation that cannot happen keeps recording and does not retry on every single call", () => {
  // Every archive slot occupied by a DIRECTORY: nothing can be unlinked, nothing can be shifted,
  // and no free generation exists — so rotation is genuinely impossible rather than merely
  // contended. Recording must continue, no archive may be destroyed, and the retry must be
  // periodic (about one per cap) rather than one attempt per row.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  for (let i = 1; i <= MAX_ARCHIVE_GENERATIONS; i++) fs.mkdirSync(`${logPath}.${i}`);

  const cap = 1000;
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: cap });

  // Each rotation attempt starts by opening the lock file, so counting those counts attempts.
  const lockPath = `${logPath}.lock`;
  let attempts = 0;
  const realOpen = fs.openSync;
  fs.openSync = (file, ...rest) => {
    if (file === lockPath) attempts++;
    return realOpen.call(fs, file, ...rest);
  };
  const rows = 40;
  try {
    for (let i = 0; i < rows; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });
  } finally {
    fs.openSync = realOpen;
  }

  assert.equal(readRows(logPath).length, rows, "every call is still recorded despite the failed rotation");
  const written = fs.statSync(logPath).size;
  const capsSpanned = Math.ceil(written / cap);
  assert.ok(attempts <= capsSpanned + 1,
    `expected about one attempt per cap (${capsSpanned} caps of data), got ${attempts} for ${rows} rows`);
  for (let i = 1; i <= MAX_ARCHIVE_GENERATIONS; i++) {
    assert.ok(fs.statSync(`${logPath}.${i}`).isDirectory(), `.${i} must be left exactly as it was`);
  }
  assert.equal(fs.existsSync(lockPath), false, "and the rotation lock is always released");
});

test("a stale rotation lock left by a dead process does not disable rotation forever", () => {
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const lockPath = `${logPath}.lock`;
  fs.writeFileSync(lockPath, "");
  const longAgo = new Date(Date.now() - 10 * 60 * 1000);
  fs.utimesSync(lockPath, longAgo, longAgo);

  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: 300 });
  for (let i = 0; i < 12; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });

  assert.ok(fs.existsSync(`${logPath}.1`), "rotation happened despite the abandoned lock");
  assert.equal(fs.existsSync(lockPath), false, "and the stale lock was cleaned up");
});

test("a rotation lock held by someone else is respected, not stomped", () => {
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const lockPath = `${logPath}.lock`;
  const cap = 300;
  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\\w", maxBytes: cap });

  // A FRESH lock: another process is mid-rotation right now, so this one must not rotate as well.
  // Two processes renaming the live file at the same moment is precisely how a rotation lands on
  // top of an archive the other has just written.
  fs.writeFileSync(lockPath, "");

  for (let i = 0; i < 12; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });
  assert.equal(fs.existsSync(`${logPath}.1`), false, "no rotation may happen while the lock is held");
  assert.equal(readRows(logPath).length, 12, "but every row is still recorded");

  fs.unlinkSync(lockPath);   // the other process finishes
  for (let i = 12; i < 36; i++) logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });
  assert.ok(fs.existsSync(`${logPath}.1`), "and rotation resumes once the lock is free");
});

test("a recorder with a stale byte count does not rotate a file someone else just rotated", () => {
  // The tracked size is this process's own contribution and nothing else's, so it is stale the
  // moment a second session touches the log. Acting on it without re-reading the file under the
  // lock is what turns a rotation into data loss: this recorder believes the live file is full
  // when in fact it was archived a millisecond ago and holds one row.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const cap = 2000;
  const logger = createUsageLogger({ logPath, sessionId: "sess-a", cwd: "C:\\w", maxBytes: cap });

  // Fill to just under the cap without tripping a rotation, whatever a row happens to measure.
  let filled = 0;
  while (fs.statSync(logPath).size < cap - 300) {
    logger.record({ tool: "list_projects", args: { projectId: filled }, outcome: "ok", ms: 1 });
    filled++;
  }
  assert.equal(fs.existsSync(`${logPath}.1`), false, "precondition: the fill itself must not rotate");

  // Another session rotates, exactly as this module does: live becomes .1, a new live file starts.
  fs.renameSync(logPath, `${logPath}.1`);
  fs.writeFileSync(logPath, `${JSON.stringify({ ts: "x", session: "sess-b", tool: "list_projects" })}\n`);

  // A row big enough that this recorder's stale count says "rotate" — the actor is truncated to
  // MAX_FIELD_CHARS, so the row stays bounded while still being several times a normal one.
  logger.record({ tool: "list_projects", args: { projectId: 999, actor: "A".repeat(400) }, outcome: "ok", ms: 1 });

  assert.equal(readRows(`${logPath}.1`).length, filled,
    "the archive the other session just created must not be shifted away or overwritten");
  assert.equal(fs.existsSync(`${logPath}.2`), false, "and nothing was shifted needlessly");
  assert.equal(readRows(logPath).length, 2, "the new row is simply appended to the new live file");
});

test("two recorders sharing one log cannot destroy each other's archive", () => {
  // THE DEFECT, deterministically. Each recorder tracks its OWN byte count, so recorder B's
  // accounting is blind to everything A has written. With the old rotation — rename straight onto
  // `.1`, no lock, no re-stat — B renamed a freshly created live file over the archive A had just
  // preserved. Two in-process recorders reproduce that exact mechanism without spawning anything.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  // Sized so FOUR rotations happen (two recorders interleaved archive about 2*cap per rotation)
  // while the whole run still fits inside the generation bound: an overwrite therefore shows up as
  // lost rows rather than as rows that legitimately aged out.
  const cap = 2000;
  const a = createUsageLogger({ logPath, sessionId: "sess-a", cwd: "C:\\w", maxBytes: cap });
  const b = createUsageLogger({ logPath, sessionId: "sess-b", cwd: "C:\\w", maxBytes: cap });

  const perSession = 70;
  for (let i = 0; i < perSession; i++) {
    a.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });
    b.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });
  }

  const rows = readAllGenerations(logPath);
  const seen = new Set(rows.map(r => `${r.session}#${r.projectId}`));
  assert.equal(seen.size, perSession * 2,
    `expected all ${perSession * 2} rows to survive, found ${seen.size}`);
  for (const session of ["sess-a", "sess-b"]) {
    assert.equal(rows.filter(r => r.session === session).length, perSession,
      `session ${session} lost rows to a concurrent rotation`);
  }
});

test("four REAL concurrent processes lose nothing", { timeout: 60_000 }, async () => {
  // The measured defect: six real processes against the old rotation lost 93.7% of rows and erased
  // one session entirely, while the same six with rotation switched off lost nothing and tore no
  // line — appending was never the problem, rotation was. This is that experiment, shrunk to what
  // a test suite can afford, and it is the only case here that exercises a lock held across
  // process boundaries rather than within one.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  const childPath = path.join(dir, "child.mjs");
  const processes = 4;
  const perProcess = 150;
  // Same sizing rule as the two-recorder case: four interleaved writers archive about 4*cap per
  // rotation, so this cap buys two to four real rotations and still leaves a spare generation.
  // Measured over a dozen runs: 2–4 generations used, never 5, and never a lost row. Overwriting
  // `.1` the way the old code did would retain only the live file plus one archive — under half.
  const cap = 8000;
  const spinNs = 50_000n;   // a beat between rows, so the four really do overlap rather than queue

  fs.writeFileSync(childPath, `
import { createUsageLogger } from ${JSON.stringify(usageLogModuleUrl)};
const [logPath, sessionId, count, maxBytes, startAt, spinNs] = process.argv.slice(2);
const logger = createUsageLogger({
  logPath, sessionId, cwd: "C:\\\\w", maxBytes: Number(maxBytes), warn: () => {}
});
while (Date.now() < Number(startAt)) { /* line the processes up so they really overlap */ }
for (let i = 0; i < Number(count); i++) {
  logger.record({ tool: "list_projects", args: { projectId: i }, outcome: "ok", ms: 1 });
  const until = process.hrtime.bigint() + BigInt(spinNs);
  while (process.hrtime.bigint() < until) { /* keep all four in the same stretch of time */ }
}
`);

  const startAt = Date.now() + 1500;
  await Promise.all(Array.from({ length: processes }, (_, i) => new Promise((resolve, reject) => {
    const child = spawn(process.execPath,
      [childPath, logPath, `sess-${i}`, String(perProcess), String(cap), String(startAt), String(spinNs)],
      { stdio: ["ignore", "ignore", "pipe"] });
    let stderr = "";
    child.stderr.on("data", chunk => { stderr += chunk; });
    child.on("error", reject);
    child.on("exit", code => (code === 0 ? resolve() : reject(new Error(`child ${i} exited ${code}: ${stderr}`))));
  })));

  const rows = readAllGenerations(logPath);
  const seen = new Set(rows.map(r => `${r.session}#${r.projectId}`));
  assert.equal(seen.size, processes * perProcess,
    `${processes * perProcess - seen.size} of ${processes * perProcess} rows were lost to concurrent rotation`);
  for (let i = 0; i < processes; i++) {
    assert.equal(rows.filter(r => r.session === `sess-${i}`).length, perProcess,
      `session sess-${i} lost rows`);
  }
  assert.equal(fs.existsSync(`${logPath}.lock`), false, "the rotation lock is released by every process");
});

// --- 7. Test harnesses must not contaminate the trial -------------------------------------

test("the MCP test harnesses spawn the wrapper with recording OFF", () => {
  // smoke-mcp.mjs calls get_next_tasks, claim_task and update_task_status for real. If a smoke
  // run were recorded it would present as a session that consulted the board, inflating the very
  // §11 signals the trial adjudicates on — the project's own test suite counted as evidence of
  // its own adoption. Verified by running it too (a smoke run with KANBANFLOW_USAGE_LOG pointed
  // at a temp path leaves no file), but asserted here so removing the line goes red.
  for (const harness of ["smoke-mcp.mjs", "framing-ab.mjs"]) {
    const source = fs.readFileSync(path.join(here, harness), "utf8");
    assert.match(source, /KANBANFLOW_USAGE_LOG:\s*"off"/,
      `${harness} drives the wrapper for real and must spawn it with usage recording off`);
  }
});

// --- Review findings on PR #504 ------------------------------------------------------------

test("rotation takes a FREE archive slot instead of destroying the oldest", () => {
  // Reported on PR #504. shiftArchives() unlinked the last generation unconditionally, so with a
  // gap left by an earlier failed rename, `.5` was deleted while `.3` sat empty and would have
  // taken the archive without displacing anything. Bounded history is the goal; gratuitous loss
  // is not.
  const dir = tmp();
  const logPath = path.join(dir, "usage.jsonl");
  fs.writeFileSync(logPath, `${JSON.stringify({ ts: "2026-09-01T10:00:00.000Z", session: "live", tool: "list_projects", cwd: "C:\w", outcome: "ok", ms: 1 })}\n`);
  fs.writeFileSync(`${logPath}.1`, "gen1\n");
  fs.writeFileSync(`${logPath}.2`, "gen2\n");
  fs.writeFileSync(`${logPath}.5`, "OLDEST-MUST-SURVIVE\n");   // gap: .3 and .4 are free

  const logger = createUsageLogger({ logPath, sessionId: "s", cwd: "C:\w", maxBytes: 1 });
  logger.record({ tool: "list_projects", args: {}, outcome: "ok", ms: 1 });

  assert.ok(fs.existsSync(`${logPath}.5`), "the oldest generation must not be dropped while a slot is free");
  assert.equal(fs.readFileSync(`${logPath}.5`, "utf8").trim(), "OLDEST-MUST-SURVIVE");
});
