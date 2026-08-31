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

// 5 MB. Rows measure 126–260 bytes in practice (a short read call with no arguments at the bottom
// of that range, a claim with an actor and a long cwd at the top), so the cap is roughly 20k–40k
// calls. A two-week trial will not come close, which makes rotation a backstop against a runaway
// loop rather than an expected event.
export const DEFAULT_MAX_BYTES = 5 * 1024 * 1024;

// How many archived generations to keep. `<log>.1` is the NEWEST archive and `<log>.5` the oldest;
// rotation shifts every generation up by one and the oldest falls off the end. Five generations at
// the default cap is 25 MB of history, which is far more than a two-week trial can produce.
export const MAX_ARCHIVE_GENERATIONS = 5;

// Every recorded string is truncated to this many characters. See buildRow.
export const MAX_FIELD_CHARS = 200;

// A rotation lock older than this is assumed to belong to a process that died holding it. Rotation
// takes single-digit milliseconds, so nothing legitimate is anywhere near this.
const STALE_LOCK_MS = 30_000;

// Values that mean "the operator turned this off", matched case-insensitively after trimming.
const DISABLED_VALUES = new Set(["off", "0", "false", "no", "none", "disabled"]);

// The ONLY argument fields that are ever written. An allowlist, not a denylist: a tool added
// later cannot leak a new argument into the log by default, which is the property that lets this
// stay on permanently without anyone re-auditing it. Task descriptions, comment bodies, search
// queries and memory observations are all arguments somewhere in this wrapper and none of them
// belong in a usage log.
//
// `projectId` and `taskId` are ids; `actor` is free text and is truncated.
const RECORDED_ID_FIELDS = ["projectId", "taskId"];
const RECORDED_TEXT_FIELDS = ["actor"];

// No id string a real call carries is anywhere near this long — safe integers top out at 16
// digits. The bound exists so a megabyte of digits is rejected before it is Number()ed, not
// because 32 is meaningful.
const MAX_ID_CHARS = 32;

/**
 * Coerce an id the way the wrapper itself does, or return undefined.
 *
 * THIS MUST AGREE WITH `coerceInt` IN mcp-server.mjs, which deliberately accepts a base-10 integer
 * STRING because LLM clients routinely send ids that way. Gating the log on
 * `Number.isSafeInteger` alone was a regression with real consequences: a call carrying
 * `taskId: "194"` SUCCEEDED and wrote the board, but the row recorded no taskId at all — so two
 * sessions that genuinely collided on task 194 read as one writer, and the trial scored ADOPT on
 * data that contained the collision it was looking for.
 *
 * Recording the coerced NUMBER, not the raw value, also means one task cannot appear as two
 * distinct ids in the report depending on how a client happened to spell it.
 */
function coercedId(value) {
  let number;
  if (typeof value === "number") {
    number = value;
  } else if (typeof value === "string" && value.length <= MAX_ID_CHARS && /^[+-]?\d+$/.test(value)) {
    // The same rejections as coerceInt: empty, whitespace-padded, decimal, hex, exponent — plus
    // booleans, arrays and objects, which Number() would silently coerce to something plausible.
    number = Number(value);
  } else {
    return undefined;
  }
  // isSafeInteger, not isInteger: "99999999999999999999" rounds to a nearby float that is still
  // an integer but a WRONG entity id.
  return Number.isSafeInteger(number) ? number : undefined;
}

/**
 * Resolve where the usage log is written, or null when recording is disabled.
 *
 * The default sits beside the database in ~/.kanbanflow/ and NOT in the repo, for the same
 * reason the database moved there (#354, #477): `*.db` and log files are gitignored, so a
 * routine `git clean -xdf` deletes them with no prompt. Losing the trial's only evidence to a
 * clean would not be noticed until the trial was being adjudicated.
 *
 * Throws on a relative path rather than resolving one. See the comment on that branch.
 */
export function resolveUsageLogPath(env = process.env, homedir = os.homedir()) {
  const configured = env.KANBANFLOW_USAGE_LOG;
  if (configured === undefined) return path.join(homedir, ".kanbanflow", "mcp-usage.jsonl");

  const trimmed = String(configured).trim();
  if (trimmed.length === 0 || DISABLED_VALUES.has(trimmed.toLowerCase())) return null;

  // `~` is a shell convention, not a filesystem one, and node expands nothing. Left alone,
  // `~/logs/usage.jsonl` creates a directory LITERALLY NAMED `~` inside whatever directory the
  // client happened to launch the wrapper from — most often the repo being worked in.
  let value = trimmed;
  if (value === "~") value = homedir;
  else if (value.startsWith("~/") || value.startsWith("~\\")) value = path.join(homedir, value.slice(2));

  // A relative path resolves against the CLIENT's working directory, which differs per session BY
  // DESIGN — that difference is the whole of §11 signal 4. Resolving one would silently scatter
  // the trial's only evidence across as many files as there are working directories, and the
  // report would read one of them and call it the total. Refusing is the only honest option: this
  // is a configuration mistake, and it must be visible at the moment it is made.
  if (!path.isAbsolute(value)) {
    throw new Error(
      `KANBANFLOW_USAGE_LOG must be an absolute path or start with "~/" (got ${JSON.stringify(trimmed)}). ` +
      "A relative path resolves against each client's working directory, which would split the " +
      "usage log across sessions instead of accumulating it."
    );
  }
  return path.resolve(value);
}

/**
 * Truncate a recorded string, or drop it if it is not a string at all.
 *
 * BOUNDING EVERY STRING IS THE POINT. `actor` is allowlisted free text with no schema behind it,
 * and the error path records the arguments of a call the server has just REJECTED — so the log
 * happily wrote whatever the caller sent. Measured: one call with a 2 MB `actor` wrote a
 * 2,097,287-byte row, and about three of those fill the 5 MB cap. Worse, a single oversized frame
 * could rotate the log twice in a row and annihilate 24,098 rows of real evidence.
 *
 * 200 characters is deliberately generous for the fields that carry meaning (a session id, a tool
 * name, an actor, a working directory prefix) and still bounds a row to a few KB even when every
 * character is a 3-byte one.
 */
function boundedString(value) {
  if (typeof value !== "string") return undefined;
  return value.length > MAX_FIELD_CHARS ? value.slice(0, MAX_FIELD_CHARS) : value;
}

/**
 * Build one log row. Pure and deterministic given its inputs, so the field allowlist and the
 * error classification below are unit-testable without touching a filesystem or a clock.
 */
export function buildRow({ tool, sessionId, cwd, args = {}, outcome, ms, error, now = new Date() }) {
  const row = {
    ts: now.toISOString(),
    session: boundedString(sessionId),
    tool: boundedString(tool),
    cwd: boundedString(cwd),
    outcome: boundedString(outcome)
  };
  // A duration is a number or it is nothing. NaN and Infinity would both serialise to `null` and
  // read as a recorded zero-ish value in the report.
  if (Number.isFinite(ms)) row.ms = ms;

  for (const field of RECORDED_ID_FIELDS) {
    // Exactly what the wrapper accepted, normalised to a number. Anything the wrapper would have
    // rejected with invalid-params is dropped rather than written — including a huge numeric
    // string, which is one of the ways an unbounded row got written.
    const value = coercedId(args?.[field]);
    if (value !== undefined) row[field] = value;
  }
  for (const field of RECORDED_TEXT_FIELDS) {
    // Strings only, truncated. An object or array here would be a caller mistake, and copying one
    // in would defeat the point of the allowlist by smuggling a nested payload through a
    // permitted key.
    const value = boundedString(args?.[field]);
    if (value !== undefined) row[field] = value;
  }

  // The target work status of a status change — needed to tell a Done transition (§11 signal 3)
  // from any other move. Named `to` rather than `status` so it can never be confused with the
  // HTTP status recorded on the error path below.
  const to = boundedString(args?.status);
  if (to !== undefined) row.to = to;

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
    if (Number.isSafeInteger(httpStatus)) row.http = httpStatus;
    if (Number.isSafeInteger(error.code)) row.code = error.code;
  }

  return row;
}

function defaultWarn(message) {
  try {
    process.stderr.write(`${message}\n`);
  } catch {
    // Even the complaint about a broken log must not be able to break a tool call.
  }
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
 *
 * It must also never degrade SILENTLY. `enabled` reflects whether recording is actually working,
 * not merely whether it was asked for, and `failures` / `lastError` say why it is not — because a
 * broken instrument that reports itself healthy is indistinguishable from an unused board, and the
 * trial would be adjudicated on the difference.
 */
export function createUsageLogger(options = {}) {
  // CONSTRUCTION obeys the same contract as record(): a logging failure cannot break a tool call,
  // and that includes a failure thrown while merely READING the options — a hostile or exotic
  // options object, a crypto/process.cwd() that throws under a locked-down runtime. Everything
  // that can throw while resolving the configuration lives inside this guard, and a throw here
  // yields the same inert-but-well-shaped logger the rest of the file returns when disabled.
  let sessionId;
  let cwd;
  let maxBytes;
  let now;
  let warn;
  let logPathProvided;
  let providedLogPath = null;
  try {
    ({
      sessionId = crypto.randomBytes(4).toString("hex"),
      cwd = process.cwd(),
      maxBytes = DEFAULT_MAX_BYTES,
      now = () => new Date(),
      warn = defaultWarn
    } = options);
    logPathProvided = Object.prototype.hasOwnProperty.call(options, "logPath");
    if (logPathProvided) providedLogPath = options.logPath;
  } catch (error) {
    const reason = String(error?.message ?? error);
    // Still not silent: the disabled logger carries the reason, and one stderr line is emitted
    // through the same guarded sink noteFailure() would have used.
    defaultWarn(`kanbanflow: usage recording could not be configured (${reason}); it is disabled for this session.`);
    return { enabled: false, failures: 1, lastError: reason, sessionId: null, logPath: null, record() {} };
  }

  let logPath = null;
  let failures = 0;
  let lastError = null;
  let ready = false;
  let broken = false;
  let bytesOnDisk = 0;
  // The size at which the next rotation is attempted. Normally just maxBytes, but it is pushed out
  // after a FAILED rotation so a permanently unrotatable log does not attempt one on every single
  // call for the rest of the session.
  let rotateAt = maxBytes;

  function noteFailure(error) {
    failures += 1;
    lastError = String(error?.message ?? error);
    // ONE line, on the FIRST failure only. This wrapper speaks JSON-RPC over stdio and its stderr
    // is the client's log: a per-call warning would be a flood in exactly the situation where the
    // operator is already having a bad day. But total silence was worse — it is what let a log
    // pointed at a directory look identical to a board nobody used.
    if (failures === 1) {
      try {
        warn(`kanbanflow: usage recording is failing (${lastError}); this is reported once per session.`);
      } catch {
        // A broken warn sink must not break the tool call either.
      }
    }
  }

  if (logPathProvided) {
    logPath = providedLogPath;
  } else {
    try {
      logPath = resolveUsageLogPath();
    } catch (error) {
      // A misconfigured KANBANFLOW_USAGE_LOG must not take the wrapper down with it, but it must
      // not pass unnoticed either.
      logPath = null;
      broken = true;
      noteFailure(error);
    }
  }

  const configured = Boolean(logPath);
  const lockPath = configured ? `${logPath}.lock` : null;

  /**
   * Prepare the directory and prove the path is writable, once.
   *
   * `ready` is set INSIDE the try, after success. It used to be set before it, so a single
   * transient mkdir failure — an antivirus holding the directory, a home drive still mounting —
   * disabled recording for the whole session even after the obstruction cleared. Now a failed
   * init leaves `ready` false and the next record() tries again; the extra syscalls only happen
   * while the log is broken, where they are the cheapest thing in the process.
   *
   * The open-for-append probe creates the file when missing and fails when the directory is not
   * writable or the file is read-only. It is NOT sufficient on its own: measured on Windows,
   * `fs.openSync(<a directory>, "a")` SUCCEEDS and so does `fs.appendFileSync(<a directory>, "")`
   * — only a non-empty write throws EISDIR. So the stat below asserts the path is really a file.
   * That is the exact case that reproduced the silent-instrument bug: KANBANFLOW_USAGE_LOG
   * pointing at a directory, record() returning happily, and nothing ever written.
   */
  function init() {
    if (ready || !configured) return;
    try {
      fs.mkdirSync(path.dirname(logPath), { recursive: true });
      fs.closeSync(fs.openSync(logPath, "a"));
      const stat = fs.statSync(logPath);
      if (!stat.isFile()) throw new Error(`${logPath} is not a file`);
      bytesOnDisk = stat.size;
      ready = true;
      broken = false;
    } catch (error) {
      broken = true;
      noteFailure(error);
    }
  }

  function backOff() {
    rotateAt = bytesOnDisk + maxBytes;
  }

  /**
   * Take the cross-process rotation lock, or return null when someone else holds it.
   *
   * ROTATION IS THE ONLY PART OF THIS FILE THAT NEEDS COORDINATION. Appending is safe: measured
   * with six concurrent processes and rotation switched off, zero rows were lost and no line was
   * torn, because a single small O_APPEND write is atomic in practice. The same six processes WITH
   * the old unsynchronised rotation lost 93.7% of rows and erased one session entirely — each
   * process tracked its own byte count, so process B renamed a freshly created live file over the
   * `.1` that process A had just preserved.
   */
  function acquireRotationLock() {
    try {
      return fs.openSync(lockPath, "wx");
    } catch (error) {
      if (error?.code !== "EEXIST") return null;
    }
    // A lock left behind by a process that died mid-rotation would otherwise disable rotation for
    // good, and the live log would grow without bound with nothing saying why.
    try {
      if (Date.now() - fs.statSync(lockPath).mtimeMs < STALE_LOCK_MS) return null;
      fs.unlinkSync(lockPath);
      return fs.openSync(lockPath, "wx");
    } catch {
      return null;
    }
  }

  function releaseRotationLock(fd) {
    if (fd === null) return;
    try { fs.closeSync(fd); } catch { /* nothing useful to do */ }
    try { fs.unlinkSync(lockPath); } catch { /* nothing useful to do */ }
  }

  /** The lowest generation number not already taken, or null when all of them are. */
  function firstFreeArchive() {
    for (let i = 1; i <= MAX_ARCHIVE_GENERATIONS; i++) {
      const candidate = `${logPath}.${i}`;
      if (!fs.existsSync(candidate)) return candidate;
    }
    return null;
  }

  /**
   * Shift `.1`→`.2`, `.2`→`.3`, … so `.1` becomes free for the live file.
   *
   * A rename is NEVER allowed to land on an existing archive: the old code renamed straight onto
   * `.1` and relied on MOVEFILE_REPLACE_EXISTING, which is exactly how a second rotation destroyed
   * the first one's evidence. The single deliberate deletion is the oldest generation falling off
   * the end of the bound, which is the price of keeping the history bounded at all. Anything that
   * cannot be moved is left where it is and firstFreeArchive() routes around it.
   */
  function shiftArchives() {
    const oldest = `${logPath}.${MAX_ARCHIVE_GENERATIONS}`;
    try {
      if (fs.existsSync(oldest)) fs.unlinkSync(oldest);
    } catch { /* leave it; the loop below will refuse to overwrite it */ }

    for (let i = MAX_ARCHIVE_GENERATIONS - 1; i >= 1; i--) {
      const from = `${logPath}.${i}`;
      const to = `${logPath}.${i + 1}`;
      try {
        if (fs.existsSync(from) && !fs.existsSync(to)) fs.renameSync(from, to);
      } catch { /* leave it where it is */ }
    }
  }

  function rotate(pendingBytes) {
    let lock = null;
    try {
      lock = acquireRotationLock();
      if (lock === null) {
        // Someone else is rotating right now. Appending to whatever file exists is correct and
        // safe; backing off means this process tries again a cap later rather than fighting.
        backOff();
        return;
      }

      // RE-STAT UNDER THE LOCK. The tracked figure is this process's own contribution and nothing
      // else's, so it is stale the moment a second session appends — and acting on a stale figure
      // is what turned rotation into data loss.
      const liveSize = fs.statSync(logPath).size;
      bytesOnDisk = liveSize;

      // Refuse to rotate an EMPTY live file. Without this, one row larger than the whole cap
      // rotates immediately, then rotates again on the next row, and two generations of real
      // evidence are gone to make room for a single oversized frame. An oversized row overshooting
      // the cap is a far smaller problem than an archive that no longer exists.
      if (liveSize === 0) {
        rotateAt = maxBytes;
        return;
      }
      // Another process rotated between the check and this lock: the pending row fits after all.
      if (liveSize + pendingBytes <= maxBytes) {
        rotateAt = maxBytes;
        return;
      }

      shiftArchives();
      const target = firstFreeArchive();
      if (target === null) {
        backOff();
        return;
      }
      fs.renameSync(logPath, target);
      bytesOnDisk = 0;
      rotateAt = maxBytes;
    } catch {
      // Keep appending. An oversized log is a far smaller problem than a failed measurement, and
      // the report reads whatever length it finds.
      backOff();
    } finally {
      releaseRotationLock(lock);
    }
  }

  // Probe eagerly, so `enabled` means something before the first tool call rather than only after
  // one has already been lost.
  init();

  return {
    get enabled() { return configured && !broken; },
    get failures() { return failures; },
    get lastError() { return lastError; },
    sessionId,
    logPath,

    record(entry) {
      if (!configured) return;
      try {
        init();
        if (!ready) return;
        const row = buildRow({ ...entry, sessionId, cwd, now: now() });
        // One row per line, and deliberately short. Concurrent sessions append to the same file
        // with O_APPEND, where a single small write is not interleaved in practice; a row that
        // grew to several KB would make that guarantee much weaker, which is the other reason
        // every string above is bounded. The report tolerates a torn line regardless.
        const line = `${JSON.stringify(row)}\n`;
        // BYTES, not string length, and the same figure for both the check and the accounting.
        // `line.length` counts UTF-16 code units, so a cwd or actor holding any non-ASCII
        // character makes the check undercount what the accounting then adds — the cap drifts
        // upward a little on every such row and the file quietly overshoots it.
        const bytes = Buffer.byteLength(line);
        if (bytesOnDisk + bytes > rotateAt) rotate(bytes);
        fs.appendFileSync(logPath, line);
        bytesOnDisk += bytes;
      } catch (error) {
        noteFailure(error);
      }
    }
  };
}
