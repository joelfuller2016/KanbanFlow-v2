// MCP tool-call usage recorder (#477 §11).
//
// WHY THIS EXISTS. #477 commits to adopting or reverting the whole MCP integration on four
// measured signals — sessions invoking `get_next_tasks`, concurrent-session collisions, tasks
// reaching Done through the Definition-of-Done gate, and whether the board is consulted from
// outside this repo. Nothing recorded any of them. `get_next_tasks` is a read, so it emits no
// ActivityEvent; a session's working directory was never written down anywhere at all. A kill
// criterion nothing can measure is not a kill criterion, and #477 §4.3 is itself an indictment of
// building things nobody checks the use of — so shipping the trial without its instrument would
// repeat the exact failure the issue was filed about.
//
// WHAT IT IS NOT. This is a local trial instrument, not telemetry and not an audit trail. The
// board's ActivityEvent rows remain the audit trail; they are authoritative for what CHANGED.
// This log answers the different question those rows structurally cannot: WHO ASKED, FROM WHERE,
// and WHETHER THE CALL WAS EVEN A WRITE. Nothing here is ever sent anywhere.

import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import crypto from "node:crypto";

// 5 MB ≈ 30k calls at the ~170-byte rows this writes. A two-week trial will not come close, so
// rotation is a backstop against a runaway loop rather than an expected event.
export const DEFAULT_MAX_BYTES = 5 * 1024 * 1024;

// Values that mean "the operator turned this off", matched case-insensitively after trimming.
const DISABLED_VALUES = new Set(["off", "0", "false", "no", "none", "disabled"]);

// The ONLY argument fields that are ever written. An allowlist, not a denylist: a tool added
// later cannot leak a new argument into the log by default, which is the property that lets this
// stay on permanently without anyone re-auditing it. Task descriptions, comment bodies, search
// queries and memory observations are all arguments somewhere in this wrapper and none of them
// belong in a usage log.
const RECORDED_ARG_FIELDS = ["projectId", "taskId", "actor"];

/**
 * Resolve where the usage log is written, or null when recording is disabled.
 *
 * The default sits beside the database in ~/.kanbanflow/ and NOT in the repo, for the same
 * reason the database moved there (#354, #477): `*.db` and log files are gitignored, so a
 * routine `git clean -xdf` deletes them with no prompt. Losing the trial's only evidence to a
 * clean would not be noticed until the trial was being adjudicated.
 */
export function resolveUsageLogPath(env = process.env, homedir = os.homedir()) {
  const configured = env.KANBANFLOW_USAGE_LOG;
  if (configured !== undefined) {
    const trimmed = String(configured).trim();
    if (trimmed.length === 0 || DISABLED_VALUES.has(trimmed.toLowerCase())) return null;
    return path.resolve(trimmed);
  }
  return path.join(homedir, ".kanbanflow", "mcp-usage.jsonl");
}

/**
 * Build one log row. Pure and deterministic given its inputs, so the field allowlist and the
 * error classification below are unit-testable without touching a filesystem or a clock.
 */
export function buildRow({ tool, sessionId, cwd, args = {}, outcome, ms, error, now = new Date() }) {
  const row = {
    ts: now.toISOString(),
    session: sessionId,
    tool,
    cwd,
    outcome,
    ms
  };

  for (const field of RECORDED_ARG_FIELDS) {
    const value = args?.[field];
    // Numbers and strings only. An object or array here would be a caller mistake, and copying
    // one in would defeat the point of the allowlist by smuggling a nested payload through a
    // permitted key.
    if (typeof value === "number" || typeof value === "string") row[field] = value;
  }

  // The target work status of a status change — needed to tell a Done transition (§11 signal 3)
  // from any other move. Named `to` rather than `status` so it can never be confused with the
  // HTTP status recorded on the error path below.
  if (typeof args?.status === "string") row.to = args.status;

  // Whether the Definition-of-Done gate (#428) was OVERRIDDEN rather than satisfied. A boolean,
  // never the reason text: the count is what §11 needs, and the reason is already recorded
  // verbatim in the audit trail as a Verification comment plus a TaskDoneOverridden event.
  if (typeof args?.overrideReason === "string" && args.overrideReason.trim().length > 0) {
    row.override = true;
  }

  if (error) {
    // restJson attaches the upstream HTTP status to error.data.status, which is what separates a
    // claim conflict (409) from a Definition-of-Done refusal (422) from a transport failure
    // (neither). Both of those codes are load-bearing for §11 signals 2 and 3, so record the
    // number rather than a message that could be reworded later.
    const httpStatus = error?.data?.status;
    if (typeof httpStatus === "number") row.http = httpStatus;
    if (typeof error.code === "number") row.code = error.code;
  }

  return row;
}

/**
 * Create a recorder bound to one wrapper process.
 *
 * One stdio wrapper process is one client session, so a per-process id is exactly the session
 * identity §11 signal 1 counts ("sessions invoking get_next_tasks", not calls).
 *
 * record() NEVER THROWS. That is the single most important property in this file: an instrument
 * that can break the thing it measures is worse than no instrument, and a full disk or a
 * read-only home directory must degrade to "no measurement" and never to "the board stopped
 * answering". Every filesystem call below is individually guarded.
 */
export function createUsageLogger({
  logPath = resolveUsageLogPath(),
  sessionId = crypto.randomBytes(4).toString("hex"),
  cwd = process.cwd(),
  maxBytes = DEFAULT_MAX_BYTES,
  now = () => new Date()
} = {}) {
  const enabled = Boolean(logPath);
  let bytesOnDisk = 0;
  let ready = false;

  // Size is read ONCE at construction and tracked in-process from there, rather than stat()ing
  // before every append. A stat per tool call would be a syscall on the hot path to protect
  // against an event that a two-week trial will not reach. Concurrent sessions each track their
  // own contribution, so the tracked figure under-counts when several run at once — which errs
  // toward rotating late, never toward rotating a file another process is mid-append to.
  function init() {
    if (ready || !enabled) return;
    ready = true;
    try {
      fs.mkdirSync(path.dirname(logPath), { recursive: true });
      bytesOnDisk = fs.statSync(logPath).size;
    } catch {
      // A missing file is the normal first-run case and leaves bytesOnDisk at 0. A genuinely
      // unwritable directory surfaces on the first append instead, where it is also swallowed.
      bytesOnDisk = 0;
    }
  }

  // The size at which the next rotation is attempted. Normally just maxBytes, but it is pushed out
  // after a FAILED rotation so a permanently unrenameable log does not attempt a rename on every
  // single call for the rest of the session.
  let rotateAt = maxBytes;

  function rotate() {
    try {
      // rename over an existing destination is fine — Node uses MOVEFILE_REPLACE_EXISTING on
      // Windows, verified: a second rotation overwrites `.1` rather than failing EEXIST. What it
      // genuinely cannot do is replace a destination another process holds OPEN, which throws
      // EPERM — and concurrent sessions sharing this log is the normal case, not an exotic one.
      fs.renameSync(logPath, `${logPath}.1`);
      bytesOnDisk = 0;
      rotateAt = maxBytes;
    } catch {
      // Keep appending. An oversized log is a far smaller problem than a failed measurement, and
      // the report reads whatever length it finds. Back the threshold off by another full cap so
      // the retry is periodic rather than per-call.
      rotateAt = bytesOnDisk + maxBytes;
    }
  }

  return {
    enabled,
    sessionId,
    logPath,

    record(entry) {
      if (!enabled) return;
      try {
        init();
        const row = buildRow({ ...entry, sessionId, cwd, now: now() });
        // One row per line, and deliberately short. Concurrent sessions append to the same file
        // with O_APPEND, where a single small write is not interleaved in practice; a row that
        // grew to several KB would make that guarantee much weaker. The report tolerates a torn
        // line regardless and counts it rather than dying on it.
        const line = `${JSON.stringify(row)}\n`;
        // BYTES, not string length, and the same figure for both the check and the accounting.
        // `line.length` counts UTF-16 code units, so a cwd or actor holding any non-ASCII
        // character makes the check undercount what the accounting then adds — the cap drifts
        // upward a little on every such row and the file quietly overshoots it.
        const bytes = Buffer.byteLength(line);
        if (bytesOnDisk + bytes > rotateAt) rotate();
        fs.appendFileSync(logPath, line);
        bytesOnDisk += bytes;
      } catch {
        // Deliberately silent, and deliberately not stderr either: this wrapper speaks JSON-RPC
        // over stdio and its stderr is the client's log. A per-call warning about a broken log
        // would be noise in exactly the situation where the operator is already having a bad day.
      }
    }
  };
}
