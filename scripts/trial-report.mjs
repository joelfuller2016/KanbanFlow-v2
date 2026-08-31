#!/usr/bin/env node
// Adoption-trial report for #477 §11.
//
// Renders the issue's own four-signal table with MEASURED values, so the two-week trial can be
// adjudicated from evidence instead of from recollection. Two sources, each answering the half it
// can actually answer:
//
//   - the usage log (scripts/usage-log.mjs)  — who called what, from where. The only record of
//     reads, and the only record of a caller's working directory.
//   - the board's ActivityEvent rows         — what actually changed. Authoritative for Done
//     transitions and claim events; unaffected by anything this instrument does or misses.
//
// It reports what it measured and says plainly what it could not. A trial report that quietly
// scores an unmeasured signal as a pass would make the kill criterion decorative, which is the
// failure #477 §4.3 already documents once. The same reasoning runs the other way and is the
// reason UNMEASURED exists as a verdict: §11's consequence for REVERT is "close #477 as
// not-planned", so scoring a signal REVERT because its SOURCE was missing would close the issue on
// the strength of a stopped app. A missing source is not evidence of absence.
//
// Usage:
//   npm run trial:report
//   npm run trial:report -- --since 2026-08-30 --json
//   npm run trial:report -- --repo-root C:\Users\Owner\Documents\Claude\KanbanFlow-v2

import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const DEFAULT_API = "http://127.0.0.1:5123";
const ACTIVITY_PAGE = 500; // server-side maximum (ApiEndpoints.cs Math.Clamp(limit ?? 100, 1, 500))

// Node's `fetch` has NO default timeout, and neither does `execFileSync` without one. A server that
// accepts the TCP connection and then never answers — the app paged out but still LISTENing, which
// is what actually happened — hangs this process forever: no verdict, no exit code, and every
// UNMEASURED path bypassed, because nothing ever throws. A bounded wait turns that into the
// source-unavailable case the report already knows how to report honestly.
export const FETCH_TIMEOUT_MS = 10_000;
const GIT_TIMEOUT_MS = 5_000;

// Tools that mutate ONE IDENTIFIED TASK, used to decide whether two sessions wrote the same task.
// Every name here takes a `taskId` argument or mutates a child of one; only the `taskId`-bearing
// ones can actually be bucketed, because `RECORDED_ARG_FIELDS` in usage-log.mjs records
// projectId/taskId/actor and nothing else — a tool keyed by `subtaskId` or `acId` records no task
// to collide on. Curated deliberately: the log records tool names, not HTTP verbs, so write-ness
// cannot be derived from a row. A drift test asserts every name here still exists in the wrapper,
// so a rename goes red instead of silent.
export const WRITE_TOOLS = new Set([
  "claim_task",
  "release_task",
  "reassign_task",
  "update_task",
  "update_task_status",
  "delete_task",
  "add_task_comment",
  "create_subtask",
  "update_subtask",
  "mark_subtask_done",
  "delete_subtask",
  "create_acceptance_criterion",
  "update_acceptance_criterion",
  "mark_acceptance_criterion_done",
  "delete_acceptance_criterion",
  "create_dependency",
  "delete_dependency"
]);

// Every tool that CHANGES BOARD STATE, task-scoped or not. This is a strictly broader question
// than WRITE_TOOLS answers, and it needs its own set because it drives signal 2's VACUITY GUARD:
// "did at least two sessions genuinely write?" A session that created tasks, promoted an idea and
// wired dependencies plainly wrote, yet under the old set — which held only task-scoped tools —
// it counted as zero writing sessions, and the guard then read a real trial as vacuous. That is
// the opposite of the direction a missing name is supposed to fail in.
//
// The memory-graph writers (create_entities, add_observations, …) are deliberately EXCLUDED. They
// mutate the knowledge graph, not the task-bearing domain, so two sessions writing only memory
// create no contention over work — counting them would let the guard pass on activity that cannot
// produce the collision signal 2 is measuring.
export const MUTATING_TOOLS = new Set([
  ...WRITE_TOOLS,
  "create_task",
  "create_project",
  "update_project",
  "create_feature",
  "update_feature",
  "delete_feature",
  "create_idea",
  "update_idea",
  "promote_idea"
]);

// Events that prove a task was under a claim. `TaskClaimStolen` counts: a takeover means a claim
// existed and the TTL handed it on, which is #477 §5.2's GREEN path, not a failure.
const CLAIM_EVENTS = new Set(["TaskClaimed", "TaskClaimRenewed", "TaskClaimStolen"]);

// WHY WRITERS ARE NOT SEEDED FROM ACTIVITY EVENTS, deliberately, despite the asymmetry it leaves.
//
// Signal 2's merge is asymmetric: claims come from the log AND the activity trail (so they can only
// ever REMOVE an overlap), while writers come from the log alone (so an activity-only writer can
// never ADD one). A UI, REST or native-MCP writer colliding with a wrapper session is therefore
// invisible to this signal. That is a real limitation and it is stated in the caveat rather than
// papered over.
//
// Seeding writers from `ActivityEvent.Actor` was tried and REVERTED, because it makes the signal
// actively wrong instead of merely incomplete. `session` and `actor` are different namespaces:
// `session` is a per-process id the wrapper mints, `actor` is free text the CALLER sends and most
// write tools leave to the schema default "mcp-agent". So one ordinary session making two
// `update_task_status` calls was counted twice — once from its own log rows as `session:f891778b`
// and once from the audit events those very calls produced as `actor:mcp-agent` — and scored
// `1 uncoordinated overlap / REVERT` with `writingSessions: 1` in the same detail block. §11's
// consequence for REVERT is closing #477 as not-planned, so a signal that manufactures one out of a
// single session doing ordinary work is far worse than one that under-reports. Under-reporting a
// collision is survivable; inventing one is not.

// A trailing `Z` or a ±hh:mm / ±hhmm offset.
const ZONE_DESIGNATOR = /(?:Z|[+-]\d{2}:?\d{2})$/i;

/**
 * Read an API timestamp as the UTC instant it actually is.
 *
 * #337: the API stores `DateTime.UtcNow` but serializes with `Kind = Unspecified`, so every
 * timestamp goes out with NO zone designator — `"2026-08-29T21:30:00"` where the instant means
 * `"...Z"`. `Date.parse` reads that zone-less form as LOCAL time, which shifts every activity
 * event by the host's UTC offset. Against a usage log whose own `ts` is a true `toISOString()`
 * the two sources are then in different time zones, and an event either side of a `--since`
 * cutoff lands on the wrong side of it.
 *
 * Normalizing HERE, at the source boundary, is deliberate: the alternative — teaching the
 * comparison to cope — leaves wrong data flowing through everything downstream that ever sorts,
 * renders or pairs on a timestamp. `summarize` receives correct instants and needs to know
 * nothing about #337.
 */
export function asUtcIso(value) {
  if (typeof value !== "string") return value;
  const trimmed = value.trim();
  // Date-only values (`2026-08-30`) are ALREADY parsed as UTC by the ISO rules, and appending a
  // designator to one produces `2026-08-30Z`, which is not a valid ISO date at all — Date.parse
  // returns NaN. Only a date-TIME is missing its zone.
  if (!trimmed.includes("T")) return value;
  return ZONE_DESIGNATOR.test(trimmed) ? trimmed : `${trimmed}Z`;
}

/** One activity event with its timestamp normalized to a true UTC instant. */
export function normalizeEvent(event) {
  if (!event || typeof event !== "object") return event;
  return { ...event, createdAt: asUtcIso(event.createdAt) };
}

/**
 * A task id as ONE key, whether the caller sent `7` or `"7"`.
 *
 * `coerceInt` in mcp-server.mjs deliberately accepts a base-10 integer STRING because LLM clients
 * send ids that way. `buildRow` now applies the same coercion and records a NUMBER, so a current
 * log holds one form — but this stays as defence in depth for two reasons: archives written before
 * that change still carry the raw string, and the report must not silently mis-bucket them; and a
 * recorder that ever regresses here would otherwise fail silently, since two sessions writing task
 * 7 as `7` and `"7"` would land in different buckets and the collision simply would not be seen.
 * A missed collision is the one error this signal cannot afford, so the cheap guard stays.
 */
export function normalizeTaskId(value) {
  if (typeof value === "number") return Number.isSafeInteger(value) ? value : null;
  if (typeof value !== "string") return null;
  // The SAME shape check the wrapper applies (`coerceInt`, mcp-server.mjs) and the recorder
  // applies (`buildRow`, usage-log.mjs) — a base-10 integer string and nothing else.
  //
  // A bare `Number()` was wrong in both directions. It invents ids out of values that are not
  // ids at all (`Number("")` and `Number(null)` are 0, `Number(true)` is 1), and it ACCEPTS
  // three shapes the wrapper explicitly rejects before coercion: `"0x10"` became 16, `"1e3"`
  // became 1000, and `" 3 "` became 3. Bucketing on those means this report can resolve a task
  // id that the board never accepted, so two rows could be merged into a collision that never
  // happened — or a real one split apart. Mirroring the producer's grammar exactly is the whole
  // point of the function; approximating it reintroduces the bug it exists to prevent.
  if (!/^[+-]?\d+$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

/**
 * Parse a JSONL usage log tolerantly.
 *
 * Concurrent sessions append to one file, so a torn line is possible however unlikely. Counting
 * malformed lines and carrying on is right: dying would lose the whole trial to one bad byte, and
 * silently dropping them would make the counts quietly wrong. The count is printed.
 */
export function parseRows(text) {
  const rows = [];
  let malformed = 0;
  for (const line of String(text).split("\n")) {
    const trimmed = line.trim();
    if (trimmed.length === 0) continue;
    try {
      const row = JSON.parse(trimmed);
      // `ts` is required, not optional, and required to be a REAL INSTANT rather than merely a
      // string. Every consumer below sorts, windows or compares on it — `a.first.localeCompare(...)`
      // in signal 1 throws on `undefined` — so a row admitted without one is a row that kills the
      // whole trial later, from a line this function claims to have tolerated.
      //
      // A string that will not parse (`"ts":"hello"`) is worse than a missing one, because it fails
      // DIFFERENTLY depending on an unrelated flag: with no `--since` every comparison against it is
      // a string comparison and the row is counted, while with a `--since` the NaN comparison drops
      // it silently — and it can become the reported window start either way. Tolerating a malformed
      // line means COUNTING it, not passing it on, so both shapes land in `malformed`.
      const usable =
        row && typeof row === "object" &&
        typeof row.tool === "string" &&
        typeof row.ts === "string" &&
        Number.isFinite(Date.parse(row.ts));
      if (usable) rows.push(row);
      else malformed++;
    } catch {
      malformed++;
    }
  }
  return { rows, malformed };
}

/**
 * Read EVERY generation of the usage log, oldest first.
 *
 * THE ARCHIVE CONTRACT (agreed with the recorder): the live file is `<logPath>`; archives are
 * `<logPath>.1`, `<logPath>.2`, … where a HIGHER number is OLDER. So generations are read from the
 * highest number down to `.1`, then the live file last, and the concatenation comes out
 * oldest-first.
 *
 * Reading only the live file — the previous behavior — meant a single rotation silently deleted
 * evidence from the adjudication: the recorder moves the file aside, and the report then scores a
 * trial from whatever fraction happened to land after the rename. Missing archive numbers are
 * normal (an unrenameable generation is skipped by the recorder), so the directory is scanned
 * rather than walked until a gap.
 */
// A generation that is DISCOVERED but cannot be READ is reported, never swallowed. Skipping it
// quietly left `generations > 0`, which `main()` reads as "the log is available" — so a collision
// spanning a rotation scored REVERT with both generations readable and PENDING (overall MIXED) when
// one of them was unreadable, with no warning and no non-zero exit. `discovered` and `unreadable`
// are what let the caller tell "I read the whole log" from "I read some of it".
export function readLogGenerations(logPath, { fs: fsImpl = fs } = {}) {
  if (typeof logPath !== "string" || logPath.length === 0) {
    return { text: "", files: [], generations: 0, discovered: 0, unreadable: [] };
  }

  const dir = path.dirname(logPath);
  const base = path.basename(logPath);
  const archives = [];
  let entries = [];
  try {
    entries = fsImpl.readdirSync(dir);
  } catch {
    entries = []; // No directory yet: the first run, or logging pointed somewhere that does not exist.
  }
  for (const entry of entries) {
    if (!entry.startsWith(`${base}.`)) continue;
    const suffix = entry.slice(base.length + 1);
    if (!/^\d+$/.test(suffix)) continue; // `.jsonl.1.bak` and friends are not generations.
    archives.push({ generation: Number(suffix), file: path.join(dir, entry) });
  }
  // Descending generation number == ascending age-to-recency, because higher is older.
  archives.sort((a, b) => b.generation - a.generation);

  const candidates = archives.map(a => a.file);
  let liveExists = false;
  try {
    liveExists = fsImpl.existsSync(logPath);
  } catch {
    liveExists = false;
  }
  if (liveExists) candidates.push(logPath); // The live file is the NEWEST, so it goes last.

  const chunks = [];
  const files = [];
  const unreadable = [];
  for (const file of candidates) {
    try {
      chunks.push(fsImpl.readFileSync(file, "utf8"));
      files.push(file);
    } catch {
      // A generation that vanished between the scan and the read (the recorder rotating underneath
      // us), or one replaced by a directory, or one whose permissions changed. NOT fatal — but NOT
      // invisible either: it is named in `unreadable`, printed in the header, and makes the log
      // source unavailable, because a report built on part of the log is a report that can score a
      // collision as absent.
      unreadable.push(file);
    }
  }
  // Joined with a newline so a torn tail in one generation stays torn — counted as one malformed
  // line — instead of fusing with the first line of the next and destroying that one too.
  return { text: chunks.join("\n"), files, generations: files.length, discovered: candidates.length, unreadable };
}

/**
 * Drop rows that appear in more than one generation.
 *
 * The recorder's rotation is a rename, but a rename can fail (a file held open on Windows) and the
 * documented fallback is COPY-then-TRUNCATE — which leaves the copied rows in `<log>.1` AND, for
 * the window between the copy and the truncate, in the live file. Concatenating generations then
 * counts those calls twice: `calls` per session, `refusedClaims` and `gateRefusals` all inflate,
 * and every one of those numbers is read as evidence.
 *
 * Keyed on (ts, session, tool). `ts` is millisecond-resolution and every recorded call is an HTTP
 * round trip, so one session cannot legitimately produce two rows with all three equal; a
 * byte-identical duplicate from a copy-truncate always does. First occurrence wins, so the
 * oldest-first ordering of the concatenation is preserved.
 */
export function dedupeRows(rows) {
  const seen = new Set();
  const out = [];
  for (const row of rows) {
    const key = `${row?.ts}${row?.session}${row?.tool}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(row);
  }
  return out;
}

// A drive-letter root (C:\...) or a UNC root (\\server\share).
const WINDOWS_PATH = /^(?:[A-Za-z]:[\\/]|\\\\)/;

/**
 * Pick the path semantics the INPUTS are written in, not the ones the host happens to run.
 *
 * The rows in this log are written on Windows, but the report is a Node script that may be run
 * anywhere — and POSIX semantics applied to a Windows path get signal 4 WRONG, not merely
 * imprecise. `path.posix.relative` treats `C:\Repo` and `C:\repo\scripts` as unrelated (it is
 * case-sensitive and does not know `\` is a separator), and does the same for a UNC path, so a
 * session that never left the repo would be counted as evidence the board was consulted from
 * outside it — the exact claim signal 4 exists to establish.
 */
// Returns win32 or posix EXPLICITLY — never the host default. Falling back to `path` would make
// this function's behavior depend on where it runs, which is the whole defect being fixed, and it
// would also be untestable on Windows (there `path === path.win32`, so a host-default bug and a
// correct win32 choice are indistinguishable).
export function pathFlavor(...candidates) {
  return candidates.some(value => typeof value === "string" && WINDOWS_PATH.test(value)) ? path.win32 : path.posix;
}

/** True when `cwd` is not inside `repoRoot`. */
export function isOutsideRepo(cwd, repoRoot) {
  // An unrecorded cwd is NOT evidence of outside use, so it reads as inside. Signal 4's adopt
  // condition is "yes", so this direction under-counts the adopt case and can never manufacture it.
  if (typeof cwd !== "string" || cwd.length === 0) return false;
  if (typeof repoRoot !== "string" || repoRoot.length === 0) return false;
  // Mixed flavors (a Windows cwd against a POSIX repo root) resolve as OUTSIDE, which is the
  // honest answer: those two paths are on different machines.
  const p = pathFlavor(cwd, repoRoot);
  const rel = p.relative(p.resolve(repoRoot), p.resolve(cwd));
  if (rel === "") return false;
  // `startsWith("..")` catches a sibling; `isAbsolute(rel)` catches a DIFFERENT DRIVE, where
  // win32 `relative` cannot express a traversal and hands back the target verbatim ("D:\work").
  // Without that second clause a whole other volume reads as INSIDE the repo — silently hiding
  // exactly the outside-the-repo use signal 4 exists to find.
  return rel.startsWith("..") || p.isAbsolute(rel);
}

/** True when `cwd` is outside EVERY one of the repo roots (a worktree maps back to its main checkout). */
export function isOutsideAllRoots(cwd, roots) {
  const list = (Array.isArray(roots) ? roots : [roots]).filter(r => typeof r === "string" && r.length > 0);
  if (list.length === 0) return false; // No root to compare against measures nothing.
  return list.every(root => isOutsideRepo(cwd, root));
}

function gitOutput(args, cwd) {
  // `timeout` for the same reason the fetches carry one: an unbounded blocking call in a report
  // that has no other way to finish. `git rev-parse` on a network path, or against a repository
  // whose index lock is held, can sit indefinitely — and a hung git here hangs the whole run before
  // a single signal is computed. A timeout kills the child and throws, which this file already
  // treats as "git was unavailable".
  return execFileSync("git", args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "ignore"],
    timeout: GIT_TIMEOUT_MS
  }).trim();
}

/**
 * Resolve the repo root(s) signal 4 measures "outside" against — FROM GIT, not from this file.
 *
 * The old `resolve(dirname(import.meta.url), "..")` made the report's own location the answer, and
 * this repo uses linked worktrees routinely (`.worktrees/gsd-477-...`). Run from one, the report
 * called the MAIN checkout "outside the repo" and every ordinary in-repo session became evidence
 * the board was consulted from elsewhere — fabricating signal 4's adopt condition out of where the
 * script happened to be launched.
 *
 * `--show-toplevel` gives the working tree in hand; `--git-common-dir` points at the MAIN
 * repository's `.git`, whose parent is the main checkout. Both count as inside, so a worktree maps
 * back to its origin.
 */
export function resolveRepoRoots(override, { cwd = process.cwd(), git = gitOutput, fallbackRoot = null, exists = fs.existsSync } = {}) {
  if (typeof override === "string" && override.trim().length > 0) {
    const resolved = path.resolve(override.trim());
    // A directory that does not exist cannot be any session's repo root, so EVERY session would
    // read as "outside" it and signal 4 would score a confident ADOPT off a typo. Refusing is the
    // only honest answer: the operator asked for this root by name.
    if (!exists(resolved)) {
      throw new Error(`--repo-root ${JSON.stringify(override.trim())} does not exist (resolved to ${resolved}).`);
    }
    return { roots: [resolved], source: "override" };
  }
  // `roots` is hoisted and each git call has its OWN try, because they answer different questions
  // and either can fail alone. Sharing one try threw away a perfectly good `--show-toplevel` the
  // moment `--git-common-dir` failed (an older git, a corrupt worktree link) and dropped the whole
  // resolution to the fallback — which is the untrusted path that fabricates signal 4's adopt.
  const roots = [];
  try {
    const top = git(["rev-parse", "--show-toplevel"], cwd);
    if (top) roots.push(path.resolve(top));
  } catch {
    // Not a repository, or git is missing. The next question may still answer.
  }
  try {
    // In the main checkout this is a RELATIVE ".git", so it is resolved against cwd before its
    // parent is taken; in a linked worktree it is the absolute path to the main repo's .git.
    const common = git(["rev-parse", "--git-common-dir"], cwd);
    if (common) roots.push(path.dirname(path.resolve(cwd, common)));
  } catch {
    // Same: whatever `--show-toplevel` produced is still worth having.
  }
  const unique = [...new Set(roots)];
  if (unique.length > 0) return { roots: unique, source: "git" };
  return { roots: [path.resolve(fallbackRoot ?? cwd)], source: "fallback" };
}

/** The work status a TaskStatusChanged event landed on, or null when it cannot be read. */
export function statusAfter(event) {
  try {
    const after = JSON.parse(event.afterJson);
    return typeof after?.status === "string" ? after.status : null;
  } catch {
    // A status change whose payload will not parse is not counted as a Done transition. Under-
    // counting signal 3 delays an adopt verdict; over-counting would manufacture one.
    return null;
  }
}

// How far apart two ActivityEvent ids may be and still be from ONE SaveChangesAsync. TaskService
// adds the TaskDoneOverridden record and the TaskStatusChanged record to the same DbContext and
// saves once, so SQLite assigns them consecutive rowids inside one write transaction; 4 leaves room
// for another event joining that same save without admitting an unrelated later transition.
const OVERRIDE_ID_ADJACENCY = 4;

// The DOCUMENTED tolerance used when ids are unavailable. This IS a tolerance window and is called
// one: two records written in the same save are microseconds apart, and 1 s is far wider than any
// single save and far narrower than any two deliberate status changes of the same task.
const OVERRIDE_TIME_WINDOW_MS = 1_000;

const instantOf = value => {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
};

const eventIdOf = event => (Number.isSafeInteger(event?.id) ? event.id : null);

/**
 * Distance between a Done transition and a candidate override, or `null` when they cannot pair.
 *
 * WHY NOT A SHARED TIMESTAMP, which is what this used to do. The old key was `(entityId, exact
 * instant)` with a comment insisting it was "NOT a tolerance window: two different instants stay
 * different keys". That premise is false at the source. `ActivityEvent.CreatedAt` is a per-object
 * property initializer (`= DateTime.UtcNow`), so the two records are two SEPARATE clock reads, not
 * one shared value — measured on this machine, 20,000 of 20,000 consecutive reads were distinct.
 * The pairing only ever worked because `Date.parse` truncates to milliseconds, i.e. because of an
 * accidental ~1 ms tolerance the comment denied. A pair 13 µs apart that straddles a millisecond
 * boundary un-pairs, and five of those flip signal 3 from PENDING(4) to ADOPT(5) — an over-count,
 * in the direction that manufactures an adopt.
 *
 * The id is the honest join: both rows come from one `SaveChangesAsync`, the endpoint already
 * returns `id`, and ids are assigned in write order. The time window is the fallback for a caller
 * whose events carry no id, and it is stated as a tolerance rather than pretended away.
 */
function overrideDistance(done, override) {
  const doneTask = normalizeTaskId(done?.entityId) ?? done?.entityId;
  const overrideTask = normalizeTaskId(override?.entityId) ?? override?.entityId;
  if (doneTask !== overrideTask) return null;

  const doneId = eventIdOf(done);
  const overrideId = eventIdOf(override);
  if (doneId !== null && overrideId !== null) {
    const distance = Math.abs(doneId - overrideId);
    return distance <= OVERRIDE_ID_ADJACENCY ? distance : null;
  }

  const doneAt = instantOf(done?.createdAt);
  const overrideAt = instantOf(override?.createdAt);
  if (doneAt === null || overrideAt === null) return null;
  const gap = Math.abs(doneAt - overrideAt);
  return gap <= OVERRIDE_TIME_WINDOW_MS ? gap : null;
}

/**
 * Match each Done transition to at most one override, and each override to at most one Done.
 *
 * A MATCHING, not a broadcast: a task overridden once and later completed cleanly must have exactly
 * one of its two transitions disqualified. Nearest-first, so the override pairs with the Done it
 * was actually written beside rather than with whichever one is enumerated first.
 *
 * Returns `{ paired: Map<doneEvent, overrideEvent>, unpaired: overrideEvent[] }`.
 */
function pairOverrides(doneEvents, overrideEvents) {
  const paired = new Map();
  const claimed = new Set();
  const ordered = [...doneEvents].sort((a, b) => (eventIdOf(a) ?? 0) - (eventIdOf(b) ?? 0));
  for (const done of ordered) {
    let best = null;
    let bestDistance = Infinity;
    for (const override of overrideEvents) {
      if (claimed.has(override)) continue;
      const distance = overrideDistance(done, override);
      if (distance === null || distance >= bestDistance) continue;
      best = override;
      bestDistance = distance;
    }
    if (best !== null) {
      claimed.add(best);
      paired.set(done, best);
    }
  }
  return { paired, unpaired: overrideEvents.filter(o => !claimed.has(o)) };
}

// ── The identity seam between the two sources ──────────────────────────────────────────────────
//
// A usage-log row identifies its caller by `session`: one stdio wrapper process, an id minted by
// `createUsageLogger` from crypto.randomBytes. An ActivityEvent identifies its caller by `actor`:
// free text the caller CHOSE and sent as an argument (`claim_task` requires it; the server trims it
// and compares it case-insensitively). They are DIFFERENT NAMESPACES and neither is derivable from
// the other, so nothing here may quietly assume a session id equals an actor string.
//
// The bridge is the one place both appear together: `usage-log.mjs` allowlists `actor` as a
// recorded argument, so a log row carries the session AND the actor that call sent. Those rows are
// the only evidence connecting the namespaces and are used as exactly that — an actor with no such
// row stays an actor and is reported as an out-of-band participant, never folded into a session.
const SESSION_PREFIX = "session:";
const ACTOR_PREFIX = "actor:";

/** Normalize an actor string the way the server does: trimmed, case-insensitive. */
export function normalizeActor(value) {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed.toLowerCase();
}

export function buildIdentityBridge(rows = []) {
  const sessionToActors = new Map();
  const actorToSessions = new Map();
  for (const row of rows) {
    const actor = normalizeActor(row?.actor);
    if (actor === null) continue;
    const session = typeof row?.session === "string" && row.session.length > 0 ? row.session : null;
    if (session === null) continue;
    if (!sessionToActors.has(session)) sessionToActors.set(session, new Set());
    sessionToActors.get(session).add(actor);
    if (!actorToSessions.has(actor)) actorToSessions.set(actor, new Set());
    actorToSessions.get(actor).add(session);
  }
  return { sessionToActors, actorToSessions };
}

/** Every identity key that is known to be the SAME participant as `key`. */
function aliasesOf(key, bridge) {
  const all = new Set([key]);
  if (key.startsWith(SESSION_PREFIX)) {
    for (const actor of bridge.sessionToActors.get(key.slice(SESSION_PREFIX.length)) ?? []) {
      all.add(ACTOR_PREFIX + actor);
    }
  } else if (key.startsWith(ACTOR_PREFIX)) {
    for (const session of bridge.actorToSessions.get(key.slice(ACTOR_PREFIX.length)) ?? []) {
      all.add(SESSION_PREFIX + session);
    }
  }
  return all;
}

/**
 * Compute the four §11 signals. Pure — every input is passed in, so the whole adjudication is
 * unit-testable against fixtures with no app, no clock and no filesystem.
 *
 * `sources` carries whether each input could be READ AT ALL, which is a different fact from what
 * it contained. Without it an empty array is indistinguishable from a stopped app, and the report
 * scores REVERT on four signals it never measured.
 */
export function summarize({
  rows = [],
  events = [],
  repoRoot,
  repoRoots,
  repoRootSource = null,
  since = null,
  // `truncated` means HARMFULLY truncated — a full page whose oldest event is still inside the
  // window, so events from inside the window were dropped. `truncatedCovered` is the other kind:
  // a full page that reaches back past the cutoff, where everything dropped is older than the
  // trial. `fetchActivity` makes that distinction, because it is the only place that sees a page
  // as a page. See the note there for why gating on truncation alone was a one-way door.
  truncated = [],
  truncatedCovered = [],
  sources = {}
} = {}) {
  // An unparseable cutoff must NOT degrade to "exclude everything". NaN comparisons are all false,
  // so a typo'd date would silently drop every row and every event, and the report would then
  // score four empty signals and hand back a REVERT-shaped verdict for a trial it never read.
  // A wrong answer delivered confidently is the one outcome this whole report exists to prevent.
  const cutoff = since === null || since === undefined ? null : Date.parse(since);
  if (cutoff !== null && !Number.isFinite(cutoff)) {
    throw new Error(`summarize: since=${JSON.stringify(since)} is not a parseable date.`);
  }
  const inWindow = ts => {
    if (cutoff === null) return true;
    const at = Date.parse(ts);
    // Inclusive at the cutoff: `--since 2026-08-30` means the trial started AT that instant, so an
    // event landing exactly on it is in the trial.
    return Number.isFinite(at) && at >= cutoff;
  };

  const roots = (Array.isArray(repoRoots) ? repoRoots : repoRoot === undefined || repoRoot === null ? [] : [repoRoot])
    .filter(r => typeof r === "string" && r.length > 0);

  const allEvents = events.map(normalizeEvent);

  // Availability, not emptiness. Default true so a caller that passes fixtures directly (every
  // unit test below) is unaffected; `main()` sets them from what it could actually read.
  const logAvailable = sources.log !== false;
  const activityAvailable = sources.activity !== false;
  const availability = {
    log: {
      ok: logAvailable,
      why: "the usage log could not be read (recording is disabled, no log file exists yet, or a discovered generation was unreadable), so the record of tool calls this report needs is incomplete"
    },
    activity: {
      ok: activityAvailable,
      why: "the board's activity trail could not be read (the app is not answering, it did not answer within the timeout, or it returned something that is not a list of events)"
    },
    activityComplete: {
      ok: truncated.length === 0,
      why:
        `the activity trail was TRUNCATED at ${ACTIVITY_PAGE} events for project(s) ${truncated.join(", ")} ` +
        "and the OLDEST event it returned is still inside the trial window, so events from inside the " +
        "window were dropped. The endpoint returns NEWEST FIRST, so a TaskDoneOverridden can be lost " +
        "while the Done it qualifies survives, which scores an overridden task as having passed the " +
        "gate. That is an OVER-count, so this is not a lower bound"
    },
    // Root PROVENANCE, which is a different fact from having a root. Signal 4 asks "was the board
    // consulted from OUTSIDE the repo", and the answer is only as good as the boundary: with a
    // FALLBACK root, every session in the main checkout reads as outside when the report runs from
    // a worktree — demonstrated by toggling PATH alone, same log, verdict flips REVERT -> ADOPT.
    // With NO root at all nothing can be outside anything, and the signal scored REVERT: the one
    // outcome this file's header says a missing source must never produce.
    repoRoot: {
      ok: roots.length > 0 && repoRootSource !== "fallback",
      why:
        roots.length === 0
          ? "no repo root was supplied, so there is no boundary to be outside OF — every session reads as inside and the signal would score REVERT while measuring nothing"
          : "the repo root came from a FALLBACK (git could not be run, or this is not a git repository), so it is the parent of this script's own directory rather than the repository git would name. Run from a linked worktree, that root is the WORKTREE and every ordinary session in the main checkout reads as outside it — fabricating this signal's adopt condition out of where the script was launched. Pass --repo-root to measure it"
    }
  };

  /** Replace a signal's verdict with UNMEASURED when a source it depends on was not available. */
  const gate = (signal, requires) => {
    const missing = requires.filter(key => !availability[key].ok);
    if (missing.length === 0) return { ...signal, requires };
    return {
      ...signal,
      requires,
      verdict: "UNMEASURED",
      caveat:
        `UNMEASURED — ${missing.map(key => availability[key].why).join("; ")}. A missing source is not ` +
        "evidence of absence, so this signal is NOT scored REVERT: §11's consequence for REVERT is " +
        "closing #477 as not-planned, and a stopped app must never be able to trigger that.\n\n" +
        signal.caveat
    };
  };

  // A row without a USABLE `ts` is dropped here, not merely by `parseRows`. `summarize` is exported
  // and pure and this file's comments invite direct callers, so it must not depend on its caller
  // having sanitized the input: handed a ts-less row, signal 1's `a.first.localeCompare(b.first)`
  // threw a TypeError in five of the six possible orderings, surviving only when the bad row
  // happened to sort first — a crash that a single-ordering test would pass by luck.
  const windowRows = rows.filter(r => typeof r?.ts === "string" && Number.isFinite(Date.parse(r.ts)) && inWindow(r.ts));
  // Normalized HERE TOO, at this function's own input boundary — not inside `inWindow`.
  //
  // `fetchActivity` already normalizes what IT fetches, and that remains the primary fix. But
  // `summarize` is exported and pure, so a caller can hand it raw API events directly and never
  // pass through the fetch path at all — a second instrument, a REPL, or a test. That caller would
  // silently get every event shifted by the host's UTC offset (#337), which is precisely the
  // defect. `asUtcIso` is idempotent, so applying it on both boundaries costs nothing and closes
  // the path that skips the first one. The COMPARISON is still untouched: it sees correct data
  // instead of compensating for wrong data.
  //
  // Rows are deliberately NOT normalized: `buildRow` writes `now.toISOString()`, which is always
  // zoned, so a zone-less `ts` would mean a broken recorder — something to surface, not paper over.
  const windowEvents = allEvents.filter(e => inWindow(e.createdAt));

  // ── Signal 1: sessions invoking get_next_tasks ────────────────────────────────────────────
  const nextTaskSessions = new Map();
  for (const row of windowRows) {
    // `outcome === "ok"`, like signals 2 and 3 already required. A get_next_tasks that never
    // reached the board — a dead port, code -32000, no http status — is not a session consulting
    // the board; it is a session that tried. Counting it made a live run read 4 against an adopt
    // threshold of >= 3, manufacturing the ADOPT from calls that returned nothing.
    if (row.tool !== "get_next_tasks" || row.outcome !== "ok") continue;
    // EVERY distinct cwd, not just the first row's. The caveat below tells the reader to adjudicate
    // "unprompted" on exactly this field, and a session that started in KanbanFlow-v2 and then
    // called the board from three other projects rendered as the first one alone — the reader was
    // being pointed at a field that had already discarded the evidence.
    const entry = nextTaskSessions.get(row.session) ?? { session: row.session, cwds: [], calls: 0, first: row.ts, last: row.ts };
    entry.calls++;
    if (typeof row.cwd === "string" && row.cwd.length > 0 && !entry.cwds.includes(row.cwd)) entry.cwds.push(row.cwd);
    if (row.ts < entry.first) entry.first = row.ts;
    if (row.ts > entry.last) entry.last = row.ts;
    nextTaskSessions.set(row.session, entry);
  }
  const signal1 = gate({
    id: 1,
    // NOT "unprompted". The name is the half of this report an adjudicator pastes into #477, and
    // the old one asserted in the table the very property the caveat below retracts — "unprompted"
    // sat in the row while "UNPROMPTED-NESS IS NOT MEASURED" sat in prose several screens down.
    // The table must not claim what the instrument cannot see.
    name: "Distinct sessions invoking get_next_tasks",
    adopt: ">= 3",
    revert: "0",
    value: nextTaskSessions.size,
    verdict: nextTaskSessions.size >= 3 ? "ADOPT" : nextTaskSessions.size === 0 ? "REVERT" : "PENDING",
    detail: [...nextTaskSessions.values()].sort((a, b) => a.first.localeCompare(b.first)),
    // Stated on every run, not buried in docs: this is the one threshold the instrument cannot
    // fully decide, and the number above will read as a verdict unless the gap is named beside it.
    caveat:
      "UNPROMPTED-NESS IS NOT MEASURED. This counts distinct sessions that called get_next_tasks; " +
      "nothing here knows whether the user asked for it. Adjudicate against the per-session rows " +
      "below — a session whose cwd is another project is strong evidence of organic use; a session " +
      "rooted in KanbanFlow-v2 during a task about KanbanFlow-v2 is not. Every distinct cwd a " +
      "session called from is listed, oldest call first, because a session can move."
  }, ["log"]);

  // ── Signal 2: UNCOORDINATED overlaps between concurrent sessions ──────────────────────────
  // What this counts, and why it changed. #477 §4.1's actual incident was an UNDETECTED collision:
  // two sessions worked the same branch and neither learned of the other until git refused a push.
  // §5.2 draws the opposite case — a TTL takeover of a crashed agent's stale claim — as the GREEN
  // success path. Counting refused (409) and stolen claims as "collisions" therefore scored the
  // system's successes as failures and its actual failure mode as a pass, in both directions at
  // once: one TTL takeover forced REVERT, while two sessions writing one task with no coordination
  // between them scored a clean ADOPT.
  //
  // So the measured value is UNCOORDINATED OVERLAP: two or more distinct sessions writing the same
  // task where NEITHER held a claim on it. Refusals and takeovers are still counted and reported —
  // as collisions the system PREVENTED — but they do not drive the verdict.
  //
  // AND WHAT "COORDINATED" MEANS, which is the thing this got wrong. A flat set of "tasks that were
  // ever claimed by anyone" answers a question nobody asked. It let all of these score 0/ADOPT:
  //   - A claims and writes task 7, B writes task 7 and never claims — #477 §4.1's actual incident;
  //   - A and B collide with no claim, and an UNRELATED session claims task 7 afterwards;
  //   - a single TaskClaimStolen on task 7 anywhere in the window;
  //   - B is REFUSED the claim (409) and writes task 7 anyway — which was additionally counted
  //     under detail.prevented as a collision the system PREVENTED, when the write plainly was not.
  // Coordination is a property of a (task, participant) PAIR, so that is what is keyed here: an
  // overlap is coordinated only when EVERY writing participant is a HOLDER of that task.
  const bridge = buildIdentityBridge(windowRows);

  // Claim HOLDERS per task, from both sources. Log rows contribute their session AND, when the call
  // carried one, its actor; events contribute their actor. `aliasesOf` reconciles the two.
  const holdersByTask = new Map();
  const addTo = (map, taskId, key) => {
    if (!map.has(taskId)) map.set(taskId, new Set());
    map.get(taskId).add(key);
  };
  for (const row of windowRows) {
    // `outcome !== "ok"` is what keeps a REFUSED claim from marking anyone a holder. A 409 means the
    // board said no; treating it as a claim is how a refused session's later write read as coordinated.
    if (row.tool !== "claim_task" || row.outcome !== "ok") continue;
    const taskId = normalizeTaskId(row.taskId);
    if (taskId === null) continue;
    addTo(holdersByTask, taskId, SESSION_PREFIX + String(row.session));
    const actor = normalizeActor(row.actor);
    if (actor !== null) addTo(holdersByTask, taskId, ACTOR_PREFIX + actor);
  }
  let unattributedClaimEvents = 0;
  for (const event of windowEvents) {
    if (!CLAIM_EVENTS.has(event.eventType)) continue;
    const taskId = normalizeTaskId(event.entityId);
    if (taskId === null) continue;
    const actor = normalizeActor(event.actor);
    // An event with no actor names no holder. It is counted and reported rather than allowed to
    // vouch for everybody — "somebody held this once" is exactly the claim that was wrong before.
    if (actor === null) {
      unattributedClaimEvents++;
      continue;
    }
    addTo(holdersByTask, taskId, ACTOR_PREFIX + actor);
  }
  const isHeldBy = (taskId, key) => {
    const holders = holdersByTask.get(taskId);
    if (holders === undefined) return false;
    for (const alias of aliasesOf(key, bridge)) if (holders.has(alias)) return true;
    return false;
  };

  // WRITERS COME FROM THE LOG ONLY — see the note on CLAIM_EVENTS above for why the activity trail
  // is deliberately not allowed to add one. `session` is the only stable per-participant identity
  // in play; an `actor` string is a label the caller chose and several sessions share it by default.
  const writersByTask = new Map();
  for (const row of windowRows) {
    if (!WRITE_TOOLS.has(row.tool) || row.outcome !== "ok") continue;
    // Normalized, so `7` and `"7"` are ONE task. mcp-server's coerceInt accepts a base-10 integer
    // string and buildRow records the raw argument, so both forms genuinely occur.
    const taskId = normalizeTaskId(row.taskId);
    if (taskId === null) continue;
    addTo(writersByTask, taskId, SESSION_PREFIX + String(row.session));
  }

  const refusalRows = windowRows.filter(r => r.tool === "claim_task" && r.outcome === "error" && r.http === 409);
  const stolenClaims = windowEvents.filter(e => e.eventType === "TaskClaimStolen");
  // A refusal is only evidence the system PREVENTED something if the refused participant then
  // stayed off the task. One that was told no and wrote anyway is the opposite: a collision the
  // system failed to prevent. (A refusal followed by a successful retry leaves the session a
  // holder, so it counts as prevented — the board did its job and the handoff completed.)
  const refusedThenWroteByTask = new Map();
  let preventedRefusals = 0;
  for (const row of refusalRows) {
    const taskId = normalizeTaskId(row.taskId);
    const key = SESSION_PREFIX + String(row.session);
    const wroteAnyway = taskId !== null && (writersByTask.get(taskId)?.has(key) ?? false);
    if (wroteAnyway && !isHeldBy(taskId, key)) {
      if (!refusedThenWroteByTask.has(taskId)) refusedThenWroteByTask.set(taskId, []);
      refusedThenWroteByTask.get(taskId).push(key);
    } else {
      preventedRefusals++;
    }
  }

  const collisions = [];
  for (const [taskId, writerSet] of [...writersByTask.entries()].sort((a, b) => a[0] - b[0])) {
    const writers = [...writerSet];
    const unheld = writers.filter(w => !isHeldBy(taskId, w));
    const refusedThenWrote = refusedThenWroteByTask.get(taskId) ?? [];
    const isOverlap = writers.length > 1;
    if (!isOverlap && refusedThenWrote.length === 0) continue;
    const record = {
      taskId,
      writers,
      holders: [...(holdersByTask.get(taskId) ?? [])],
      unheld,
      refusedThenWrote,
      // COORDINATED means every writer held it. Anything else is the undetected collision.
      coordinated: (!isOverlap || unheld.length === 0) && refusedThenWrote.length === 0
    };
    collisions.push(record);
  }
  const uncoordinated = collisions.filter(c => !c.coordinated);
  const coordinated = collisions.filter(c => c.coordinated);

  // The VACUITY GUARD stays log-derived on purpose: it asks whether the MCP integration under trial
  // was used enough for a collision to have been possible, and an out-of-band writer is not evidence
  // of that. Counting UI writers here would let a trial in which the wrapper was never used pass the
  // signal — the ADOPT direction, which is the one that must never be widened by accident. They are
  // reported separately, and they can still ADD an overlap above, which is where they matter.
  const writingSessions = new Set(windowRows.filter(r => MUTATING_TOOLS.has(r.tool) && r.outcome === "ok").map(r => r.session));
  const signal2 = gate({
    id: 2,
    name: "Uncoordinated concurrent-session overlaps after wiring",
    adopt: "0",
    revert: ">= 1",
    value: uncoordinated.length,
    // VACUITY GUARD, applied to EVERY branch and not just the zero-overlap one.
    //
    // Zero overlaps among fewer than two writing sessions measures absence of USE, not absence of
    // collision, and scoring that as an adopt would let a completely unused board pass the signal it
    // exists to test. But the guard has to run the other way too: fewer than two writing sessions
    // means a collision was not POSSIBLE, so a non-zero overlap count there is by definition an
    // artefact of this instrument, not a finding — and §11's consequence for REVERT is closing #477
    // as not-planned. When the two disagree, the count is what is wrong.
    verdict: writingSessions.size < 2 ? "PENDING" : uncoordinated.length > 0 ? "REVERT" : "ADOPT",
    detail: {
      uncoordinatedOverlaps: uncoordinated,
      coordinatedOverlaps: coordinated,
      // Collisions the system PREVENTED — the board refused a second claim and the refused
      // participant stayed off the task, or a TTL handed a crashed agent's work on. Reported
      // because they are evidence the primitive worked.
      prevented: { refusedClaims: preventedRefusals, stolenClaims: stolenClaims.length },
      // And the ones it did NOT prevent: refused, then wrote anyway. Never in `prevented`.
      refusedThenWrote: [...refusedThenWroteByTask.entries()].map(([taskId, who]) => ({ taskId, writers: who })),
      claimHolders: [...holdersByTask.entries()]
        .sort((a, b) => a[0] - b[0])
        .map(([taskId, holders]) => ({ taskId, holders: [...holders] })),
      unattributedClaimEvents,
      writingSessions: writingSessions.size
    },
    caveat:
      (writingSessions.size >= 2
        ? "Counts tasks where the writers did NOT all hold a claim on that task — the undetected " +
          "collision of #477 §4.1. Coordination is keyed per (task, participant): an overlap is " +
          "coordinated only when EVERY writing participant appears as a HOLDER of that task, so a " +
          "claim by an unrelated session, or one on a different task, excuses nothing. "
        : `VACUOUS: only ${writingSessions.size} session(s) ever wrote through the wrapper, so a ` +
          "collision was not possible. Neither the presence NOR the absence of an overlap is " +
          "measurable from that, so this is PENDING whatever the count above says. ") +
      "IDENTITIES ARE BRIDGED, NOT ASSUMED EQUAL: the usage log names a caller by `session` (a " +
      "per-process id this wrapper mints) and an ActivityEvent names one by `actor` (free text the " +
      "caller sends, which most write tools leave at the default \"mcp-agent\"). The only evidence " +
      "linking the two is a log row carrying both, because `actor` is a recorded argument — those " +
      "rows are the bridge, and an actor with no such row is left as an actor rather than guessed " +
      "at. An event with NO actor names nobody: it is counted under " +
      "detail.unattributedClaimEvents and vouches for no one, so an anonymously recorded UI claim " +
      "cannot make an overlap read as coordinated. TWO SESSIONS SHARING ONE ACTOR LABEL are " +
      "indistinguishable here — as they are to the board itself, which treats a same-actor claim as " +
      "a renewal — so an actor-keyed holder excuses every session known to have used that label. " +
      "KNOWN LIMITATION, stated rather than papered over: WRITERS ARE COUNTED FROM THE USAGE LOG " +
      "ONLY. Claims come from both sources and can only REMOVE an overlap; a writer that left an " +
      "ActivityEvent and no log row — the UI, REST, or a native MCP server — can never ADD one, so " +
      "a collision between such a writer and a wrapper session is INVISIBLE to this signal. That is " +
      "deliberate: seeding writers from `actor` counted one ordinary session twice, once from its " +
      "own rows and once from the audit events those same calls produced, and scored REVERT on a " +
      "board where a single session was doing ordinary work. Under-reporting is survivable; " +
      "manufacturing the verdict that closes #477 is not. " +
      "Refused (409) and stolen claims are NOT counted as uncoordinated: per §5.2 a refusal and a " +
      "TTL takeover are the coordination primitive WORKING, and they are reported under " +
      "detail.prevented. A refusal followed by a write to that same task by the refused participant " +
      "is the exception — the write was demonstrably not prevented, so it is reported under " +
      "detail.refusedThenWrote and it DOES drive the verdict."
  }, ["log", "activity", "activityComplete"]);

  // ── Signal 3: tasks reaching Done through the DoD gate ────────────────────────────────────
  // "Through the gate" is the operative phrase: a Done reached by OVERRIDING the gate (#428) did
  // not pass it. Those are counted separately, and the refusals are counted too — a gate that
  // never refuses anything is not being tested either.
  //
  // Pairing is at TRANSITION granularity, not task granularity. TaskService writes the status
  // change and the TaskDoneOverridden record inside ONE SaveChangesAsync, so the two share an
  // instant (ApiEndpoints.cs says so explicitly where it adds an Id tiebreak for exactly that
  // reason). Excluding the whole TASK instead disqualified it forever — a task overridden once and
  // later completed cleanly through the gate never counted again — and made the exclusion depend
  // on the window, since an override before the cutoff excluded nothing at all.
  const overrideEvents = windowEvents.filter(e => e.eventType === "TaskDoneOverridden");
  const doneEvents = windowEvents.filter(event => {
    if (event.eventType !== "TaskStatusChanged" || event.entityType !== "Task") return false;
    // `=== "Done"` and nothing looser. A predicate that merely separates Done from Review would
    // also admit InProgress and Todo, and signal 3 would count ordinary board movement as
    // completion — five tasks dragged to InProgress would read as five tasks through the gate.
    return statusAfter(event) === "Done";
  });
  const { paired, unpaired } = pairOverrides(doneEvents, overrideEvents);
  const throughGateSet = new Set();
  const doneWithOverride = new Set();
  for (const event of doneEvents) {
    const taskId = normalizeTaskId(event.entityId) ?? event.entityId;
    if (paired.has(event)) doneWithOverride.add(taskId);
    else throughGateSet.add(taskId);
  }
  const throughGate = [...throughGateSet];
  const overriddenTasks = new Set(overrideEvents.map(e => normalizeTaskId(e.entityId) ?? e.entityId));
  // The two task lists CAN legitimately intersect — a task overridden once and later completed
  // cleanly is in both — so the intersection is named rather than left for the reader to notice and
  // read as a contradiction. `unpairedOverrides` is the other reason it can happen, and the one
  // that means the pairing failed rather than the task genuinely being completed twice.
  const overriddenAndAlsoThroughGate = [...overriddenTasks].filter(id => throughGateSet.has(id));
  const gateRefusals = windowRows.filter(r => r.tool === "update_task_status" && r.outcome === "error" && r.http === 422);
  const signal3 = gate({
    id: 3,
    name: "Tasks reaching Done through the DoD gate",
    adopt: ">= 5",
    revert: "0",
    value: throughGate.length,
    // VACUITY GUARD. Dones with no refusal evidence may all have had zero acceptance criteria,
    // so they do not establish that the Definition-of-Done gate had anything to check.
    verdict: throughGate.length === 0 ? "REVERT" : throughGate.length >= 5 && gateRefusals.length > 0 ? "ADOPT" : "PENDING",
    detail: {
      throughGate,
      doneWithOverride: [...doneWithOverride],
      overriddenTasks: [...overriddenTasks],
      // Named explicitly so `throughGate` and `overriddenTasks` cannot be read as contradicting
      // each other when a task appears in both.
      overriddenAndAlsoThroughGate,
      unpairedOverrides: unpaired.map(e => ({ taskId: normalizeTaskId(e.entityId) ?? e.entityId, id: e.id ?? null, createdAt: e.createdAt })),
      gateRefusals: gateRefusals.length
    },
    caveat:
      // The VACUITY GUARD above suppresses ADOPT; this says so in the prose, because a reader who
      // sees ">= 5" met and PENDING rendered next to it will otherwise assume the report is broken.
      // The two zero-refusal cases are NOT the same fact and are worded apart: the gate genuinely
      // never refused, versus no refusal could be observed at all.
      (gateRefusals.length > 0
        ? ""
        : availability.log.ok
          ? "VACUOUS: the gate never refused a Done transition. Completed tasks may have had zero " +
            "acceptance criteria, so this is PENDING rather than ADOPT. "
          : "VACUOUS: no gate refusal could be OBSERVED because the usage log was unavailable, so the " +
            "gate is not shown to have been exercised and this is PENDING rather than ADOPT. ") +
      "Counted from the board's own ActivityEvent rows, not from this log, so a Done reached " +
      "through the UI or REST counts too. A Done that OVERRODE the gate is excluded — it did not " +
      "pass it — but only THAT transition is: the same task completing cleanly later still counts, " +
      "which is why detail.overriddenTasks and detail.throughGate can share a task; the ones that " +
      "do are listed under detail.overriddenAndAlsoThroughGate. " +
      "OVERRIDE PAIRING IS ON ActivityEvent.Id, NOT ON THE TIMESTAMP: the two records come from one " +
      `SaveChangesAsync, so their ids are adjacent (within ${OVERRIDE_ID_ADJACENCY}), while their ` +
      "CreatedAt values are two SEPARATE DateTime.UtcNow reads and are essentially never equal — " +
      "the old exact-instant key only ever worked because Date.parse truncates to milliseconds, and " +
      "a pair straddling a millisecond boundary un-paired and OVER-counted this signal. Each " +
      "override is matched to at most one Done, nearest id first. When an event carries no id the " +
      `fallback is an explicit ±${OVERRIDE_TIME_WINDOW_MS} ms tolerance window — stated as a ` +
      "tolerance, not as exactness. An override with no Done to pair with is reported under " +
      "detail.unpairedOverrides rather than silently disqualifying nothing." +
      (availability.log.ok ? "" : " detail.gateRefusals reads 0 because the usage log was unavailable, not because the gate never refused.")
  }, ["activity", "activityComplete"]);

  // ── Signal 4: board consulted from outside KanbanFlow-v2/ ─────────────────────────────────
  const outsideSessions = new Map();
  for (const row of windowRows) {
    // Successful calls only, for signal 1's reason: a call that never reached the board is not the
    // board being consulted from anywhere. Live, one dead-port session supplied 2 of the 5 calls
    // behind this signal's ADOPT.
    if (row.outcome !== "ok") continue;
    if (!isOutsideAllRoots(row.cwd, roots)) continue;
    // All distinct cwds, for the same reason signal 1 collects them: this list is the evidence the
    // reader adjudicates on, and one session can call from several directories.
    const entry = outsideSessions.get(row.session) ?? { session: row.session, cwds: [], calls: 0 };
    entry.calls++;
    if (typeof row.cwd === "string" && row.cwd.length > 0 && !entry.cwds.includes(row.cwd)) entry.cwds.push(row.cwd);
    outsideSessions.set(row.session, entry);
  }
  const signal4 = gate({
    id: 4,
    name: "Board consulted for work outside KanbanFlow-v2/",
    adopt: "yes",
    revert: "no",
    value: outsideSessions.size > 0 ? "yes" : "no",
    verdict: outsideSessions.size > 0 ? "ADOPT" : "REVERT",
    detail: [...outsideSessions.values()],
    caveat:
      `Measured against repo root(s) ${roots.join(", ") || "(none supplied — nothing can be outside)"}.` +
      (repoRootSource === "git"
        ? " Resolved from git (--show-toplevel plus the parent of --git-common-dir), so a linked worktree maps back to the main checkout and does not read as outside."
        : repoRootSource === "override"
          ? " Supplied explicitly with --repo-root, and checked to exist."
          : repoRootSource === "fallback"
            ? " GIT WAS UNAVAILABLE, so this fell back to the PARENT of the directory containing this " +
              "script (scripts/../, not scripts/ itself). If the report is running from a worktree that " +
              "root is the WORKTREE, and ordinary in-repo sessions wrongly read as outside — which is " +
              "why this signal is UNMEASURED rather than scored. Pass --repo-root to measure it."
            : "")
  }, ["log", "repoRoot"]);

  const signals = [signal1, signal2, signal3, signal4];
  const verdicts = signals.map(s => s.verdict);
  let overall;
  if (verdicts.includes("UNMEASURED")) {
    // Neither adopt nor revert can be concluded from a source that was never read. Reported as a
    // distinct outcome rather than folded into MIXED, because MIXED means "the signals disagree"
    // and this means "we did not measure" — and only one of those is fixed by running it again.
    overall = {
      verdict: "INCOMPLETE",
      reason:
        "UNMEASURED: " +
        signals.filter(s => s.verdict === "UNMEASURED").map(s => `#${s.id}`).join(", ") +
        " could not be measured because a source was unavailable or truncated, so neither ADOPT nor " +
        "REVERT can be concluded. Restore the source and re-run before adjudicating §11."
    };
  } else if (verdicts.every(v => v === "ADOPT")) {
    overall = { verdict: "ADOPT", reason: "All four signals met their adopt threshold." };
  } else if (verdicts.every(v => v === "REVERT")) {
    overall = { verdict: "REVERT", reason: "All four signals met their revert threshold. Per §11: remove the mcpServers entry and close #477 as not-planned." };
  } else {
    // #477 §11 gives a threshold per signal but never says how to aggregate a split. Inventing a
    // rule here would put words in the issue's mouth on the one decision it deliberately left to
    // a person, so the split is reported and handed back.
    overall = {
      verdict: "MIXED",
      reason:
        "Signals disagree: " +
        signals.map(s => `#${s.id} ${s.verdict}`).join(", ") +
        ". §11 sets a threshold per signal but no aggregation rule for a split, so this one is yours to call."
    };
  }

  return {
    window: {
      since,
      rows: windowRows.length,
      events: windowEvents.length,
      first: windowRows.length ? windowRows.reduce((a, r) => (r.ts < a ? r.ts : a), windowRows[0].ts) : null,
      last: windowRows.length ? windowRows.reduce((a, r) => (r.ts > a ? r.ts : a), windowRows[0].ts) : null,
      truncatedProjects: truncated,
      // Split out, because "a page was full" and "the measurement is threatened" are different
      // facts and only the second one may stop the trial being adjudicated.
      truncatedCovered
    },
    sources: {
      log: logAvailable,
      activity: activityAvailable,
      activityTruncated: truncated.length > 0,
      repoRootTrusted: availability.repoRoot.ok
    },
    repoRoots: roots,
    repoRootSource,
    signals,
    overall
  };
}

export function renderReport(summary, { logPath, malformed = 0, generations = null, logFiles = [], discovered = null, unreadable = [] } = {}) {
  const out = [];
  out.push("# KanbanFlow adoption trial — #477 §11");
  out.push("");
  out.push(`Log:      ${logPath ?? "(none)"}`);
  // Generations, stated on every run: a rotation moves evidence into `<log>.1` and reading only
  // the live file would drop it silently. The count is how an operator sees that it was not — and
  // "N of M" plus the unreadable names is how they see a generation that was found but not read,
  // which used to be swallowed by an empty catch while the log still reported itself available.
  if (generations !== null) {
    out.push(
      `          ${generations} of ${discovered ?? generations} generation(s) read` +
      (logFiles.length > 0 ? ` (oldest first): ${logFiles.map(f => path.basename(f)).join(", ")}` : "") +
      (unreadable.length > 0 ? ` — ${unreadable.length} UNREADABLE: ${unreadable.map(f => path.basename(f)).join(", ")}` : "")
    );
  }
  out.push(
    `Roots:    ${(summary.repoRoots ?? []).join(", ") || "(none)"}` +
    (summary.repoRootSource ? `  [${summary.repoRootSource}]` : "") +
    (summary.sources?.repoRootTrusted === false ? "  NOT TRUSTED — signal 4 is UNMEASURED" : "")
  );
  out.push(`Sources:  usage log ${summary.sources?.log ? "available" : "UNAVAILABLE"}, activity trail ${summary.sources?.activity ? "available" : "UNAVAILABLE"}`);
  out.push(`Window:   ${summary.window.since ?? "all recorded time"}  (${summary.window.first ?? "-"} .. ${summary.window.last ?? "-"})`);
  out.push(`Measured: ${summary.window.rows} tool calls, ${summary.window.events} activity events` + (malformed ? `, ${malformed} unparseable log line(s)` : ""));
  const harmful = summary.window.truncatedProjects ?? [];
  const covered = summary.window.truncatedCovered ?? [];
  if (harmful.length > 0) {
    // The direction matters and the old wording had it backwards. The endpoint orders NEWEST
    // FIRST, so a full page means the OLDEST events fell off — and an override can be dropped
    // while the Done it qualifies survives, which OVER-counts signal 3. "Lower bound" was false in
    // precisely the direction that manufactures an adopt.
    out.push(
      `WARNING:  activity truncated at ${ACTIVITY_PAGE} events for project(s) ${harmful.join(", ")} — ` +
      "the endpoint returns NEWEST FIRST, so the OLDEST events were dropped, and the oldest event it DID " +
      "return is still inside the window (or there is no --since to compare against), so events from " +
      "inside the window are missing. This is NOT a lower bound: a TaskDoneOverridden can be lost while " +
      "its Done survives, scoring an overridden task as having passed the gate. Signals 2 and 3 are " +
      "UNMEASURED rather than scored. Narrow the window with --since to clear this."
    );
  }
  if (covered.length > 0) {
    // Truncation that cannot have cost the measurement anything: the page reaches back past the
    // cutoff, so everything it dropped is older than the trial. Reported, not gated — gating here
    // is a one-way door, since a project's lifetime event count only ever grows.
    out.push(
      `NOTE:     activity truncated at ${ACTIVITY_PAGE} events for project(s) ${covered.join(", ")}, but the ` +
      "oldest event returned predates --since, so the page covers the whole window and everything dropped " +
      "is outside the trial. Not gated."
    );
  }
  out.push("");
  out.push("| # | Signal | Adopt | Revert | Measured | Verdict |");
  out.push("|---|--------|-------|--------|----------|---------|");
  for (const s of summary.signals) {
    // An UNMEASURED signal prints no number. The value is still computed — from whatever fraction
    // of a source was readable — and printing it beside a verdict that disowns it reads at a glance
    // as a measurement ("1" next to UNMEASURED looks like one collision). The row an adjudicator
    // pastes into #477 must not say something the verdict retracts.
    const measured = s.verdict === "UNMEASURED" ? "—" : s.value;
    out.push(`| ${s.id} | ${s.name} | ${s.adopt} | ${s.revert} | ${measured} | ${s.verdict} |`);
  }
  out.push("");
  out.push(`OVERALL: ${summary.overall.verdict} — ${summary.overall.reason}`);
  out.push("");
  out.push("## What each number rests on");
  for (const s of summary.signals) {
    out.push("");
    out.push(`### ${s.id}. ${s.name} — ${s.verdict}`);
    out.push(s.caveat);
    out.push("```json");
    out.push(JSON.stringify(s.detail, null, 1));
    out.push("```");
  }
  return out.join("\n");
}

/**
 * The board ANSWERED, with an error status — as opposed to not answering at all.
 *
 * A distinct type, not just distinct prose, because the two cases mean different things to an
 * operator: a 500 says the app is up and its activity endpoint is broken, while a refused port or
 * a stall says the app is not there. Both end as `sources.activity = false` and UNMEASURED
 * signals, so nothing downstream branches on it, but a caller that wants to tell them apart can.
 */
export class ActivityHttpError extends Error {}

async function getJson(fetchImpl, url, timeoutMs = FETCH_TIMEOUT_MS) {
  let response;
  try {
    // EVERY fetch carries a deadline. Node's fetch has no default timeout, so a server that
    // completes the TCP handshake and then never answers leaves this awaiting forever: no verdict,
    // no exit code, and none of the UNMEASURED paths below ever reached, because nothing throws.
    // That is not hypothetical — it happened when the app had been paged out but was still
    // LISTENing. A deadline converts an indefinite hang into the source-unavailable case, which the
    // caller already turns into UNMEASURED signals and a non-zero exit.
    response = await fetchImpl(url, { signal: AbortSignal.timeout(timeoutMs) });
  } catch (error) {
    if (error?.name === "TimeoutError" || error?.name === "AbortError") {
      throw new Error(`GET ${url} did not answer within ${timeoutMs} ms — treating the source as unavailable.`);
    }
    throw error;
  }
  // `response.ok` is checked, not assumed. `fetch` resolves happily on a 500 or a 404, and
  // `.json()` on an error body either throws or yields an object — both of which used to land as
  // "no events", which then scored signal 3 REVERT. An error is UNAVAILABLE, not empty.
  if (!response || response.ok !== true) {
    // statusText is included when the response carries one — "HTTP 500" alone makes an operator go
    // and look it up — but it is optional, so it is appended rather than interpolated blindly: a
    // bare `${response.statusText}` renders the word "undefined" into the report on a fetch
    // implementation that omits it.
    const statusText = typeof response?.statusText === "string" ? response.statusText.trim() : "";
    throw new ActivityHttpError(
      `GET ${url} returned HTTP ${response?.status ?? "(no response)"}${statusText ? ` ${statusText}` : ""}.`
    );
  }
  return response.json();
}

/**
 * Read the board's activity trail, or fail loudly.
 *
 * Every failure path throws rather than returning a short list: the caller turns a throw into
 * `sources.activity = false`, which makes the activity-derived signals UNMEASURED. Silently
 * skipping a non-array body — the previous behavior — produced an empty `events` array
 * indistinguishable from a board on which nothing happened.
 */
export async function fetchActivity(apiBase, { fetchImpl = fetch, timeoutMs = FETCH_TIMEOUT_MS, since = null } = {}) {
  const projectsUrl = `${apiBase}/api/projects`;
  const projects = await getJson(fetchImpl, projectsUrl, timeoutMs);
  if (!Array.isArray(projects)) {
    throw new Error(`GET ${projectsUrl} did not return a list of projects.`);
  }
  // The window cutoff is needed HERE, not downstream, because truncation is a property of a PAGE
  // and this is the only place a page exists as such. Deciding it later, from the flat event list,
  // is guesswork about which project a full page belonged to.
  const cutoff = since === null || since === undefined ? null : Date.parse(since);

  const events = [];
  const truncated = [];
  const truncatedCovered = [];
  for (const project of projects) {
    const url = `${apiBase}/api/projects/${project.id}/activity?limit=${ACTIVITY_PAGE}`;
    const page = await getJson(fetchImpl, url, timeoutMs);
    if (!Array.isArray(page)) {
      throw new Error(`GET ${url} did not return a list of events.`);
    }
    // Normalized HERE, at the boundary: #337 emits zone-less timestamps that Date.parse would
    // otherwise read as local time, shifting every event by the host's UTC offset.
    const normalized = page.map(normalizeEvent);
    for (const event of normalized) events.push(event);
    // The endpoint caps at 500 with no continuation token or offset parameter, so a full page means
    // events were dropped and there is no clean way to fetch the rest.
    //
    // BUT A FULL PAGE IS NOT AUTOMATICALLY A PROBLEM, and treating it as one is a ONE-WAY DOOR: a
    // project's lifetime event count only grows, so the moment any single project crosses 500 the
    // trial could never be adjudicated again — on a board working exactly as intended. The endpoint
    // returns NEWEST FIRST, so what a full page drops is the OLDEST events, and those can only cost
    // the measurement anything if they fell INSIDE the window. The page reaches back to its own
    // oldest returned event: if that already predates `--since`, the page covers the whole window
    // and everything dropped is out of scope. With no `--since` there is no floor to compare
    // against, so a full page stays harmful. Narrowing the window is then a real remedy rather than
    // a flag that waves the gate through.
    if (page.length !== ACTIVITY_PAGE) continue;
    const instants = normalized.map(e => Date.parse(e?.createdAt)).filter(Number.isFinite);
    const oldest = instants.length > 0 ? Math.min(...instants) : null;
    // No cutoff, or nothing datable to compare: assume harmful. Assuming coverage that cannot be
    // demonstrated is the direction that scores a signal the report never measured.
    if (cutoff === null || !Number.isFinite(cutoff) || oldest === null || oldest > cutoff) truncated.push(project.id);
    else truncatedCovered.push(project.id);
  }
  return { events, truncated, truncatedCovered };
}

/**
 * Parse the CLI options strictly.
 *
 * `--since` takes a value, and an option that consumes its value with an unchecked `argv[++i]`
 * is a defect this repo has already been bitten by once: #468 is `portfolio-sync` swallowing
 * `--dry-run` as another option's value and then performing the real sync. The same shape here is
 * quieter and worse — `--since --json` would take "--json" as the cutoff, `--since` at the end of
 * the line would be ignored entirely, and a typo'd date would exclude every row. All three end in
 * a confidently rendered table that adjudicates a two-week trial on nothing.
 */
export function parseArgs(args) {
  const parsed = { since: null, json: false, repoRoot: null };
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === "--json") {
      parsed.json = true;
      continue;
    }
    if (arg === "--since") {
      const value = args[i + 1];
      if (value === undefined) {
        throw new Error("--since needs a date, e.g. --since 2026-08-30 (it was the last argument).");
      }
      if (value.startsWith("--")) {
        throw new Error(`--since needs a date, but the next argument was the option ${value}.`);
      }
      if (!Number.isFinite(Date.parse(value))) {
        throw new Error(
          `--since got ${JSON.stringify(value)}, which is not a date. Refusing rather than ` +
          "excluding every row and reporting an empty trial."
        );
      }
      parsed.since = value;
      i++;
      continue;
    }
    if (arg === "--repo-root") {
      const value = args[i + 1];
      if (value === undefined) {
        throw new Error("--repo-root needs a directory (it was the last argument).");
      }
      if (value.startsWith("--")) {
        throw new Error(`--repo-root needs a directory, but the next argument was the option ${value}.`);
      }
      parsed.repoRoot = value;
      i++;
      continue;
    }
    throw new Error(`Unknown option ${JSON.stringify(arg)}. Usage: trial-report [--since <date>] [--repo-root <dir>] [--json]`);
  }
  return parsed;
}

async function main(argv) {
  const { since, json: asJson, repoRoot: repoRootOverride } = parseArgs(argv.slice(2));
  const apiBase = (process.env.KANBANFLOW_API_URL || DEFAULT_API).replace(/\/$/, "");

  const { resolveUsageLogPath } = await import("./usage-log.mjs");
  const logPath = resolveUsageLogPath();
  const here = path.dirname(fileURLToPath(import.meta.url));
  const { roots: repoRoots, source: repoRootSource } = resolveRepoRoots(repoRootOverride, {
    cwd: here,
    fallbackRoot: path.resolve(here, "..")
  });

  const { text, files, generations, discovered, unreadable } = readLogGenerations(logPath);
  // A PARTIALLY read log is not an available log. A collision spanning a rotation reads as absent
  // when the generation holding half of it could not be opened, and `generations > 0` used to call
  // that "available" — scoring the signal from a fraction of the evidence with no warning.
  const logAvailable = Boolean(logPath) && generations > 0 && unreadable.length === 0;
  if (!logPath) console.error("NOTE: usage logging is disabled (KANBANFLOW_USAGE_LOG) — signals 1, 2 and 4 CANNOT BE MEASURED.\n");
  else if (generations === 0) console.error(`NOTE: no usage log at ${logPath} yet — no MCP tool call has been recorded. Signals 1, 2 and 4 CANNOT BE MEASURED.\n`);
  else if (unreadable.length > 0) {
    console.error(
      `NOTE: ${generations} of ${discovered} usage-log generation(s) could be read; ` +
      `${unreadable.length} UNREADABLE (${unreadable.map(f => path.basename(f)).join(", ")}) — ` +
      "the log is incomplete, so signals 1, 2 and 4 CANNOT BE MEASURED.\n"
    );
  }

  const { rows: parsedRows, malformed } = parseRows(text);
  // De-duplicated across generations: rotation is a rename, but its documented fallback is
  // copy-then-truncate, which can leave the same rows in two generations at once.
  const rows = dedupeRows(parsedRows);
  // A log that was READ but of which NOTHING PARSED is functionally a missing source, and the rule
  // at the top of this file says a missing source must never produce the verdict that closes #477.
  // Two torn lines and no good ones used to render `0 tool calls, 2 unparseable log line(s)` and
  // then score signals 1 and 4 REVERT off it. An EMPTY log is a different fact and stays available:
  // the recorder was working and recorded nothing, which is real evidence.
  const nothingParsed = rows.length === 0 && malformed > 0;
  if (nothingParsed) {
    console.error(`NOTE: every one of the ${malformed} line(s) in ${logPath} is unparseable — the log is corrupt, so signals 1, 2 and 4 CANNOT BE MEASURED.\n`);
  }

  let events = [];
  let truncated = [];
  let truncatedCovered = [];
  let activityAvailable = true;
  try {
    ({ events, truncated, truncatedCovered } = await fetchActivity(apiBase, { since }));
  } catch (error) {
    activityAvailable = false;
    // Deliberately NOT "KanbanFlow is not answering": an ActivityHttpError means it answered and
    // said no. `error.message` already carries which of the two it was (HTTP status + statusText,
    // versus a stall or a refused connection), so the prose stays true for both.
    console.error(`NOTE: the board's activity trail is unreadable at ${apiBase} (${error.message}) — signals 2 and 3 CANNOT BE MEASURED.\n`);
  }

  const summary = summarize({
    rows, events, repoRoots, repoRootSource, since, truncated, truncatedCovered,
    sources: { log: logAvailable && !nothingParsed, activity: activityAvailable }
  });
  console.log(asJson
    ? JSON.stringify(summary, null, 2)
    : renderReport(summary, { logPath, malformed, generations, logFiles: files, discovered, unreadable }));

  // Exit non-zero when the trial could not be adjudicated. A caller that treats exit 0 as "the
  // verdict is in" must not get one from a run that measured nothing.
  if (summary.overall.verdict === "INCOMPLETE") process.exitCode = 1;
}

// Only run when invoked directly, so the pure functions above can be imported by tests.
if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main(process.argv).catch(error => {
    // Message, not stack: every error this can throw is a usage error the operator must read and
    // act on, and a stack trace buries it. Exit 2 matches the launcher's convention.
    console.error(error?.message ?? error);
    process.exit(2);
  });
}
