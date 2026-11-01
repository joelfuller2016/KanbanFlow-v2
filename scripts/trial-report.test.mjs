// Tests for the #477 §11 adoption-trial report. Real code, no stubs: `summarize` is pure, so the
// fixtures below are the exact row and ActivityEvent shapes the recorder and the API actually
// emit — a usage row from usage-log.mjs `buildRow`, and an activity event from
// GET /api/projects/{id}/activity.
//
// The activity fixtures deliberately carry a ZONE-LESS `createdAt` ("2026-09-01T10:00:00"),
// because that is literally what the API sends (#337). Anything that must see a true UTC instant
// is fed through `fetchActivity`/`normalizeEvent`, which is where the boundary fix lives.
//
// Run: node --test scripts/

import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import {
  parseRows, isOutsideRepo, isOutsideAllRoots, statusAfter, summarize, renderReport, parseArgs,
  pathFlavor, asUtcIso, normalizeEvent, normalizeTaskId, readLogGenerations, resolveRepoRoots,
  fetchActivity, ActivityHttpError, dedupeRows, buildIdentityBridge, aliasesOf, normalizeActor,
  WRITE_TOOLS, MUTATING_TOOLS, normalizeRemoteUrl, withConfirmedCloneRoots, gitOutput, isLocalPathRemote,
  scrubGitEnv, createGitRunner,
  selectRowsInWindow, selectCloneCandidates
} from "./trial-report.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = "C:\\Users\\Owner\\Documents\\Claude\\KanbanFlow-v2";

// This file's own worktree HEAD must be EXACTLY unchanged after this run. Several tests below
// `git init`/`clone`/`commit` inside throwaway os.tmpdir() directories, and this run may itself be
// invoked with a leaked GIT_DIR from whatever launched it — if either of those ever bled into this
// checkout instead of staying confined to the tmpdir being built, the test at the end of this file
// (searching for "this worktree's own HEAD is unchanged") is the one place that would catch it,
// rather than a stray commit or index change surfacing silently in `git status` later.
//
// A plain top-level constant, not a `before`/`after` hook: measured directly, node:test's top-level
// `after()` in THIS file (153 tests, several async, several spawning real child processes) silently
// dropped an assertion failure inside it — no ✖, no altered `fail` count, exit 0 — even though the
// hook body demonstrably ran (confirmed with a temporary console.error) and computed mismatched
// values. An isolated single-test file reproduced the hook mechanism correctly, so the failure mode
// is specific to this file's scale/shape, not proof the mechanism is reliable here. A plain
// module-load-time constant, checked in an ordinary `test(...)` at the end of the file, uses
// exactly the assertion path already proven trustworthy by every other test in this file.
//
// Read through the scrubbed `gitOutput`, like every other git call in this file. A raw
// `execFileSync("git", …)` inherits a leaked GIT_DIR and snapshots THAT repository's HEAD, at load
// and again at the end, so the guard would watch the wrong repository and pass. A read that fails
// (git missing, or this file outside any checkout) leaves no snapshot; the last test then skips,
// instead of the import failing and taking every test in the file down with it.
let HEAD_AT_MODULE_LOAD = null;
try {
  HEAD_AT_MODULE_LOAD = gitOutput(["rev-parse", "HEAD"], HERE);
} catch {
  // No snapshot: see the last test.
}
const row = (over = {}) => ({
  ts: "2026-09-01T10:00:00.000Z", session: "s1", tool: "get_next_tasks",
  cwd: REPO, outcome: "ok", ms: 5, ...over
});
const statusEvent = (entityId, status, over = {}) => ({
  id: 1, projectId: 2, entityType: "Task", entityId, actor: "a",
  eventType: "TaskStatusChanged", afterJson: JSON.stringify({ id: entityId, status }),
  createdAt: "2026-09-01T10:00:00", ...over
});
const overrideEvent = (entityId, over = {}) => ({
  id: 2, projectId: 2, entityType: "Task", entityId, actor: "a",
  eventType: "TaskDoneOverridden", createdAt: "2026-09-01T10:00:00", ...over
});
const find = (summary, id) => summary.signals.find(s => s.id === id);
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "kbf-trial-"));
// git reports physical paths (`--show-toplevel` resolves a symlink or junction anywhere in the path),
// and path.resolve does not. Real-git tests compare a git-reported path with a fixture path through
// this, on BOTH sides, so a temp directory reached through a link still compares equal.
const real = p => fs.realpathSync(p);

// A `fetch` stand-in. Real shape: `.ok`, `.status`, `.json()` — the three things the production
// code now checks, because a 500 with a JSON error body used to read as "no events".
const okResponse = body => async () => ({ ok: true, status: 200, json: async () => body });
const routed = table => async url => {
  const hit = Object.entries(table).find(([fragment]) => url.includes(fragment));
  if (!hit) throw new Error(`no fixture for ${url}`);
  return hit[1];
};

// --- 1. Reading the log -------------------------------------------------------------------

test("a torn line is counted, not fatal, and does not drop the good rows around it", () => {
  const text = [
    JSON.stringify(row()),
    '{"ts":"2026-09-01T10:00:01.000Z","tool":"claim_ta',   // torn by a concurrent append
    "",
    JSON.stringify(row({ tool: "claim_task" }))
  ].join("\n");
  const { rows, malformed } = parseRows(text);
  assert.equal(rows.length, 2);
  assert.equal(malformed, 1);
});

test("valid JSON that is not a usage row still counts as malformed", () => {
  const { rows, malformed } = parseRows('{"unrelated":true}\n[1,2,3]\n');
  assert.equal(rows.length, 0);
  assert.equal(malformed, 2);
});

test("a row with a tool but NO ts is malformed, not admitted — it used to kill the whole trial", () => {
  // The defect this pins: `parseRows` claimed to tolerate a malformed line, then admitted a row
  // with no `ts`, and signal 1's `a.first.localeCompare(b.first)` threw on `undefined` several
  // hundred lines later. One bad line lost the entire two-week adjudication, from the function
  // whose whole purpose is that it cannot.
  const text = [JSON.stringify({ tool: "get_next_tasks", session: "a", cwd: REPO }), JSON.stringify(row())].join("\n");
  const { rows, malformed } = parseRows(text);
  assert.equal(malformed, 1, "the ts-less row must be COUNTED as malformed");
  assert.equal(rows.length, 1);
  assert.doesNotThrow(() => summarize({ rows, repoRoot: REPO }),
    "and what survives must be safe to summarize — this is the throw the old code reached");
  // The surviving row is still MEASURED, not merely survived: dropping the bad line must not cost
  // the good one, which is how a "tolerant" parser quietly turns a real trial into an empty one.
  assert.equal(find(summarize({ rows, repoRoot: REPO }), 1).value, 1);
});

test("a ts that is a STRING but not a DATE is malformed too — it used to count or vanish depending on --since", () => {
  // `{"ts":"hello"}` passed the old `typeof row.ts === "string"` check. With no --since every
  // comparison against it was a string comparison and the row COUNTED; with a --since the NaN
  // comparison dropped it silently. Same log, two different totals, nothing flagged — and a junk
  // `ts` could also become the reported window start, since `window.first` is a string reduce.
  const text = [
    JSON.stringify(row({ ts: "hello", session: "junk" })),
    JSON.stringify(row({ ts: "2026-09-01T10:00:00.000Z", session: "good" }))
  ].join("\n");
  const { rows, malformed } = parseRows(text);
  assert.deepEqual(rows.map(r => r.session), ["good"]);
  assert.equal(malformed, 1, "an unparseable ts is a malformed line, and the counter must say so");

  // And the flag-dependence itself: both windows now agree, because the row never got in.
  assert.equal(summarize({ rows, repoRoot: REPO }).window.rows, 1);
  assert.equal(summarize({ rows, repoRoot: REPO, since: "2026-08-01" }).window.rows, 1);
});

test("summarize itself rejects a ts-less row, in EVERY position — it is exported and cannot trust its caller", () => {
  // The production path is safe because `parseRows` filters first, but `summarize` is exported,
  // pure, and this file's comments invite direct callers. Handed a ts-less row it threw
  // `TypeError: Cannot read properties of undefined (reading 'localeCompare')` — in five of the six
  // orderings. It survived only when the bad row happened to sort FIRST, so a single-ordering test
  // would have passed by luck. All six are exercised.
  const bad = { tool: "get_next_tasks", session: "no-ts", cwd: REPO, outcome: "ok" };
  const good = [row({ session: "a", ts: "2026-09-01T10:00:00.000Z" }), row({ session: "b", ts: "2026-09-01T11:00:00.000Z" })];
  const orderings = [
    [bad, good[0], good[1]], [good[0], bad, good[1]], [good[0], good[1], bad],
    [bad, good[1], good[0]], [good[1], bad, good[0]], [good[1], good[0], bad]
  ];
  for (const rows of orderings) {
    const summary = assert.doesNotThrow(() => summarize({ rows, repoRoot: REPO })) ?? summarize({ rows, repoRoot: REPO });
    assert.equal(summary.window.rows, 2, "the ts-less row is dropped, the good ones survive");
    assert.deepEqual(find(summary, 1).detail.map(d => d.session), ["a", "b"]);
  }
  // A string-but-unparseable ts is the same case, and would sort as a plain string without this.
  assert.equal(summarize({ rows: [{ ...bad, ts: "hello" }, ...good], repoRoot: REPO }).window.rows, 2);
});

// --- 2. Every generation of the log, not just the live file --------------------------------

test("all rotated generations are read, oldest first — a rotation must not delete evidence", () => {
  // THE ARCHIVE CONTRACT: live file is <log>, archives are <log>.1, <log>.2, … where a HIGHER
  // number is OLDER. Reading only the live file meant one rotation silently removed rows from the
  // adjudication.
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(`${log}.2`, `${JSON.stringify(row({ session: "oldest", ts: "2026-09-01T01:00:00.000Z" }))}\n`);
  fs.writeFileSync(`${log}.1`, `${JSON.stringify(row({ session: "middle", ts: "2026-09-01T02:00:00.000Z" }))}\n`);
  fs.writeFileSync(log, `${JSON.stringify(row({ session: "newest", ts: "2026-09-01T03:00:00.000Z" }))}\n`);
  fs.writeFileSync(`${log}.1.bak`, "{\"not\":\"a generation\"}\n");

  const { text, files, generations } = readLogGenerations(log);
  assert.equal(generations, 3, "two archives plus the live file");
  assert.deepEqual(files.map(f => path.basename(f)), ["mcp-usage.jsonl.2", "mcp-usage.jsonl.1", "mcp-usage.jsonl"],
    "highest archive number is the OLDEST, so it is read first; the live file is read last");
  assert.deepEqual(parseRows(text).rows.map(r => r.session), ["oldest", "middle", "newest"]);
  assert.equal(find(summarize({ rows: parseRows(text).rows, repoRoot: REPO }), 1).value, 3,
    "all three sessions reach the signal — reading only the live file would have scored 1");
  fs.rmSync(dir, { recursive: true, force: true });
});

test("a gap in the archive numbering is not a stopping point, and a non-numeric suffix is not a generation", () => {
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(`${log}.3`, `${JSON.stringify(row({ session: "g3" }))}\n`);   // .2 is missing
  fs.writeFileSync(`${log}.1`, `${JSON.stringify(row({ session: "g1" }))}\n`);
  fs.writeFileSync(`${log}.old`, `${JSON.stringify(row({ session: "ignored" }))}\n`);
  const { text, generations } = readLogGenerations(log);
  assert.equal(generations, 2, "the live file does not exist; .3 and .1 do, and the gap at .2 stops nothing");
  assert.deepEqual(parseRows(text).rows.map(r => r.session), ["g3", "g1"]);
  fs.rmSync(dir, { recursive: true, force: true });
});

test("no log at all reads as zero generations rather than throwing", () => {
  const dir = tmp();
  assert.deepEqual(readLogGenerations(path.join(dir, "nothing.jsonl")), { text: "", files: [], generations: 0, discovered: 0, unreadable: [] });
  assert.deepEqual(readLogGenerations(null), { text: "", files: [], generations: 0, discovered: 0, unreadable: [] });
  fs.rmSync(dir, { recursive: true, force: true });
});

test("a DISCOVERED but UNREADABLE generation is named, counted, and makes the log unavailable", () => {
  // The defect: the reader's `catch {}` swallowed a generation it could not open, and `generations
  // > 0` still reported the log "available". A collision spanning a rotation then scored REVERT
  // with both generations readable and PENDING (overall MIXED) with one of them gone — no warning,
  // no non-zero exit, and the difference invisible in the output.
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(log, `${JSON.stringify(row({ session: "live" }))}\n`);
  fs.mkdirSync(`${log}.1`);   // a DIRECTORY where a generation should be: discovered, unreadable

  const { generations, discovered, unreadable, files } = readLogGenerations(log);
  assert.equal(discovered, 2, "both the archive and the live file were DISCOVERED");
  assert.equal(generations, 1, "only the live file could be READ");
  assert.deepEqual(unreadable.map(f => path.basename(f)), ["mcp-usage.jsonl.1"]);
  assert.deepEqual(files.map(f => path.basename(f)), ["mcp-usage.jsonl"]);

  const text = renderReport(summarize({ rows: [row()], repoRoot: REPO }), { logPath: log, generations, discovered, unreadable });
  assert.match(text, /1 of 2 generation\(s\) read/);
  assert.match(text, /1 UNREADABLE: mcp-usage\.jsonl\.1/);

  // End to end: a partially read log is NOT an available log. This is the assignment that used to
  // read `generations > 0` and call it available while half the evidence was missing.
  const result = spawnSync(process.execPath, [path.join(HERE, "trial-report.mjs")], {
    encoding: "utf8",
    env: { ...process.env, KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" }
  });
  assert.match(result.stdout, /usage log UNAVAILABLE/, result.stdout + result.stderr);
  assert.match(result.stdout, /1 of 2 generation\(s\) read/);
  assert.match(result.stdout, /1 UNREADABLE/);
  assert.match(result.stderr, /UNREADABLE \(mcp-usage\.jsonl\.1\)/);
  assert.equal(result.status, 1, "and it must not exit 0 with the trial half-read");
  fs.rmSync(dir, { recursive: true, force: true });
});

test("a copy-truncate rotation must not double-count the same call in two generations", () => {
  // Rotation is a rename, but a rename can fail on Windows and the documented fallback is
  // copy-then-truncate — which leaves the copied rows in `<log>.1` AND in the live file. Every
  // count built from rows inflates: `calls` per session, refusedClaims, gateRefusals.
  const duplicated = [
    row({ session: "a", ts: "2026-09-01T01:00:00.000Z" }),
    row({ session: "a", tool: "claim_task", taskId: 1, ts: "2026-09-01T01:00:01.000Z", outcome: "error", http: 409 }),
    row({ session: "a", tool: "update_task_status", taskId: 1, ts: "2026-09-01T01:00:02.000Z", outcome: "error", http: 422 })
  ];
  const doubled = [...duplicated, ...duplicated];
  assert.equal(doubled.length, 6);

  const deduped = dedupeRows(doubled);
  assert.equal(deduped.length, 3, "the second copy of each row is dropped");
  assert.deepEqual(deduped.map(r => r.tool), duplicated.map(r => r.tool), "first occurrence wins, so oldest-first order survives");

  const before = summarize({ rows: doubled, repoRoot: REPO });
  const after = summarize({ rows: deduped, repoRoot: REPO });
  assert.equal(find(before, 1).detail[0].calls, 2, "the un-deduped log double-counts the call");
  assert.equal(find(after, 1).detail[0].calls, 1);
  assert.equal(find(after, 2).detail.prevented.refusedClaims, 1);
  assert.equal(find(after, 3).detail.gateRefusals, 1);

  // A distinct call is NOT a duplicate: same session and tool, different instant.
  assert.equal(dedupeRows([row({ ts: "2026-09-01T01:00:00.000Z" }), row({ ts: "2026-09-01T01:00:00.001Z" })]).length, 2);
  // Nor is the same instant and tool from a DIFFERENT session.
  assert.equal(dedupeRows([row({ session: "a" }), row({ session: "b" })]).length, 2);
});

test("dedupeRows keys a CALL, not a bucket — same millisecond, different task, both survive", () => {
  // Reported on PR #504, finding 2. The key was `ts + session + tool` — no taskId, and no
  // delimiter — on the premise that every recorded call is an HTTP round trip so one session
  // cannot produce two rows in one millisecond. The wrapper's LOCAL validation (requiredInt,
  // assertEnum) throws before any HTTP hop, so it can and does. Two writes on two tasks then
  // collapsed into one and a task silently lost a writer, which erases a real collision — the one
  // error signal 2 cannot afford.
  const at = "2026-09-01T01:00:00.000Z";
  const sameMs = [
    row({ session: "a", tool: "update_task_status", taskId: 1, ts: at, outcome: "error", http: 422 }),
    row({ session: "a", tool: "update_task_status", taskId: 2, ts: at, outcome: "error", http: 422 })
  ];
  assert.equal(dedupeRows(sameMs).length, 2, "two tasks are two calls, however close together");
  assert.equal(find(summarize({ rows: dedupeRows(sameMs), repoRoot: REPO }), 3).detail.gateRefusals, 2,
    "gateRefusals must not under-count because two refusals shared a millisecond");

  // The consequence that actually decides a verdict: the dropped row was another session's write
  // on a DIFFERENT task, so the collision on that task disappeared entirely.
  const collision = [
    row({ session: "a", tool: "update_task", taskId: 1, ts: at }),
    row({ session: "a", tool: "update_task", taskId: 2, ts: at }),
    row({ session: "b", tool: "update_task", taskId: 2, ts: "2026-09-01T01:00:05.000Z" })
  ];
  const s2 = find(summarize({ rows: dedupeRows(collision), repoRoot: REPO }), 2);
  assert.equal(s2.value, 1, "task 2 still has two writers");
  assert.equal(s2.detail.uncoordinatedOverlaps[0].taskId, 2);

  // projectId is in the key too, for tools that carry no taskId at all.
  assert.equal(dedupeRows([
    row({ tool: "create_task", projectId: 1, ts: at }),
    row({ tool: "create_task", projectId: 2, ts: at })
  ]).length, 2);

  // And the key is ENCODED, not concatenated: `session:"a" + tool:"bc"` and `session:"ab" +
  // tool:"c"` produced the same string when the fields were simply glued together.
  assert.equal(dedupeRows([row({ session: "a", tool: "bc", ts: at }), row({ session: "ab", tool: "c", ts: at })]).length, 2);
  // A byte-identical duplicate — the copy-truncate case this function exists for — is still dropped.
  assert.equal(dedupeRows([row({ session: "a", tool: "update_task", taskId: 1, ts: at }), row({ session: "a", tool: "update_task", taskId: 1, ts: at })]).length, 1);
});

test("a COMMENT on a task somebody else holds is coordination, not a collision", () => {
  // Reported on PR #504, finding 3. `add_task_comment` sat in WRITE_TOOLS, so "A claims task 7 and
  // works it, B leaves a handoff note on it" scored 1 uncoordinated overlap / REVERT — and §11's
  // consequence for REVERT is closing #477 as not-planned. §4.1's incident is an UNDETECTED
  // collision; a comment is the opposite of undetected, and it changes neither the task nor any of
  // its structural children. Every other name in the set does.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "b", tool: "add_task_comment", taskId: 7, ts: "2026-09-01T11:00:00.000Z" })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0, "leaving a note on somebody else's claimed task is not §4.1's collision");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps, []);
  assert.notEqual(s2.verdict, "REVERT");

  // But commenting IS board activity, so it still counts towards the vacuity guard — the guard asks
  // whether the wrapper was used enough for a collision to have been possible, and it plainly was.
  assert.equal(s2.detail.writingSessions, 2);
  assert.equal(s2.verdict, "ADOPT");
  assert.equal(WRITE_TOOLS.has("add_task_comment"), false, "the collision set");
  assert.equal(MUTATING_TOOLS.has("add_task_comment"), true, "the board-activity set");

  // And the same session commenting alongside a REAL write still collides, so removing the name
  // from the collision set did not make a genuine overlap invisible.
  const alsoWrote = [...rows, row({ session: "b", tool: "update_task", taskId: 7, ts: "2026-09-01T11:01:00.000Z" })];
  assert.equal(find(summarize({ rows: alsoWrote, repoRoot: REPO }), 2).value, 1);
});

test("a generation whose last line is torn does not corrupt the first line of the next", () => {
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(`${log}.1`, `${JSON.stringify(row({ session: "a" }))}\n{"ts":"2026-09-01T10:00:00.000Z","to`); // no trailing newline
  fs.writeFileSync(log, `${JSON.stringify(row({ session: "b" }))}\n`);
  const { rows, malformed } = parseRows(readLogGenerations(log).text);
  assert.deepEqual(rows.map(r => r.session), ["a", "b"], "the good row after the tear must survive");
  assert.equal(malformed, 1);
  fs.rmSync(dir, { recursive: true, force: true });
});

test("the header states how many generations were read", () => {
  const summary = summarize({ rows: [row()], repoRoot: REPO });
  const text = renderReport(summary, { logPath: "C:\\log.jsonl", generations: 3, logFiles: ["C:\\log.jsonl.2", "C:\\log.jsonl.1", "C:\\log.jsonl"] });
  assert.match(text, /3 generation\(s\) read/);
  assert.match(text, /log\.jsonl\.2, log\.jsonl\.1, log\.jsonl/);
});

// --- 3. Timestamps: the API sends no zone designator (#337) --------------------------------

test("a zone-less API timestamp is read as UTC, and an already-zoned one is left alone", () => {
  assert.equal(asUtcIso("2026-08-29T21:30:00"), "2026-08-29T21:30:00Z");
  assert.equal(asUtcIso("2026-08-29T21:30:00.123"), "2026-08-29T21:30:00.123Z");
  assert.equal(asUtcIso("2026-08-29T21:30:00Z"), "2026-08-29T21:30:00Z", "must not double-stamp");
  assert.equal(asUtcIso("2026-08-29T21:30:00+02:00"), "2026-08-29T21:30:00+02:00", "an explicit offset is already unambiguous");
  assert.equal(asUtcIso("2026-08-29"), "2026-08-29",
    "a date-only value is ALREADY parsed as UTC; appending Z makes it unparseable (Date.parse -> NaN)");
  assert.ok(Number.isFinite(Date.parse(asUtcIso("2026-08-29"))));
  assert.equal(asUtcIso(undefined), undefined);
});

test("fetchActivity normalizes createdAt at the SOURCE boundary, so summarize never sees a naive instant", async () => {
  const { events } = await fetchActivity("http://x", {
    fetchImpl: routed({
      // `/activity` first: `routed` takes the FIRST fragment that matches, and an activity URL
      // contains `/api/projects` too.
      "/activity": { ok: true, status: 200, json: async () => [statusEvent(1, "Done", { createdAt: "2026-08-29T21:30:00" })] },
      "/api/projects": { ok: true, status: 200, json: async () => [{ id: 1 }] }
    })
  });
  // THE HOST-INDEPENDENT ASSERTION, and the reason it is written on the string rather than on the
  // verdict: whether a naive parse changes the answer depends on the host's UTC offset, so a
  // verdict assertion cannot go red on a machine running at UTC+0. The emitted string can, on any
  // host, the moment the boundary stops normalizing.
  assert.equal(events[0].createdAt, "2026-08-29T21:30:00Z");
  assert.equal(Date.parse(events[0].createdAt), Date.parse("2026-08-29T21:30:00Z"));
  assert.equal(normalizeEvent(statusEvent(1, "Done")).createdAt, "2026-09-01T10:00:00Z");
});

test("the API's real 7-fractional-digit, zone-less shape is handled", () => {
  // What the live API actually emits: "2026-08-30T23:37:44.9731683" — seven fractional digits and
  // no designator. More digits than the ES spec defines, so the regex must anchor on the ZONE, not
  // on a fixed millisecond width.
  assert.equal(asUtcIso("2026-08-30T23:37:44.9731683"), "2026-08-30T23:37:44.9731683Z");
  assert.equal(Date.parse("2026-08-30T23:37:44.9731683Z"), Date.parse("2026-08-30T23:37:44.973Z"));
  assert.ok(Number.isFinite(Date.parse(asUtcIso("2026-08-30T23:37:44.9731683"))));
});

test("summarize normalizes zone-less events ITSELF — it is exported, so it cannot rely on the fetch path", () => {
  // `fetchActivity` normalizes what it fetches, but `summarize` is pure and exported: a caller can
  // hand it raw API events and never pass through the fetch path at all. Without normalization on
  // this boundary too, `Date.parse` reads the zone-less string as LOCAL and every event shifts by
  // the host's UTC offset — five pre-trial events then hit signal 3's adopt threshold exactly.
  const events = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done", { createdAt: "2026-08-29T21:30:00" }));
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO, since: "2026-08-30", sources: { log: true, activity: true } }), 3);
  assert.equal(s3.value, 0, "the true instant is 2026-08-29T21:30:00Z, which is BEFORE the cutoff");
  assert.equal(s3.verdict, "REVERT");

  // The same events one minute the OTHER side of the cutoff must still be admitted, so the
  // assertion above cannot be satisfied by simply dropping everything.
  //
  // The pair is also what makes this test go red on ANY host with a non-zero UTC offset, which one
  // assertion alone could not: unnormalized, a host WEST of UTC shifts the first set forward past
  // the cutoff and fails assertion one, while a host EAST of UTC shifts the second set back behind
  // it and fails assertion two. (At exactly UTC+0 there is no skew to detect at all; the `asUtcIso`
  // string assertions above are what guard the normalization there.)
  const after = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done", { createdAt: "2026-08-30T00:01:00" }));
  assert.equal(find(summarize({ rows: [], events: after, repoRoot: REPO, since: "2026-08-30" }), 3).value, 5);
});

test("five Done events just BEFORE the cutoff score 0/REVERT, not 5/ADOPT", async () => {
  // The reproduction from the defect report: five Done events whose true instant is
  // 2026-08-29T21:30:00Z, against `--since 2026-08-30`. Read as local time on a host west of UTC
  // they shift forward past the cutoff and score a full ADOPT for a trial that had not started.
  const page = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done", { createdAt: "2026-08-29T21:30:00" }));
  const { events } = await fetchActivity("http://x", {
    fetchImpl: routed({ "/activity": { ok: true, status: 200, json: async () => page }, "/api/projects": { ok: true, status: 200, json: async () => [{ id: 1 }] } })
  });
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO, since: "2026-08-30" }), 3);
  assert.equal(s3.value, 0);
  assert.equal(s3.verdict, "REVERT");
});

// --- 4. Inside vs outside the repo --------------------------------------------------------

test("cwd inside the repo, including the repo root itself, is not outside", () => {
  assert.equal(isOutsideRepo(REPO, REPO), false);
  assert.equal(isOutsideRepo(`${REPO}\\scripts`, REPO), false);
  assert.equal(isOutsideRepo(REPO.toLowerCase(), REPO), false, "Windows paths compare case-insensitively");
});

test("cwd in another project is outside", () => {
  assert.equal(isOutsideRepo("C:\\Users\\Owner\\Documents\\Claude\\other-repo", REPO), true);
  assert.equal(isOutsideRepo("C:\\Users\\Owner", REPO), true);
});

test("a sibling directory whose name merely starts with the repo name is outside", () => {
  assert.equal(isOutsideRepo(`${REPO}-scratch`, REPO), true,
    "a prefix match is not containment — this is why path.relative is used, not startsWith");
});

test("a cwd on a DIFFERENT DRIVE is outside, and an unrecorded cwd is not", () => {
  // win32 `relative` cannot express a traversal across volumes, so it returns the target verbatim
  // ("D:\\work") — a string that does NOT start with "..". Only the `isAbsolute(rel)` clause
  // catches it. Drop that clause and a whole other volume reads as INSIDE the repo, silently
  // hiding exactly the outside-the-repo use signal 4 exists to find.
  assert.equal(isOutsideRepo("D:\\work", REPO), true);
  assert.equal(isOutsideRepo("D:\\work\\project\\src", REPO), true);

  // An unrecorded cwd reads as INSIDE deliberately: signal 4's adopt condition is "yes", so this
  // direction can only under-count the adopt case and can never manufacture it.
  assert.equal(isOutsideRepo(undefined, REPO), false);
  assert.equal(isOutsideRepo("", REPO), false);
  assert.equal(isOutsideRepo(REPO, undefined), false, "no root to compare against measures nothing");
});

// --- 5. Repo root comes from git, not from where this script happens to live ----------------

const fakeGit = table => args => {
  const key = args.join(" ");
  if (!(key in table)) throw new Error(`unexpected: git ${key}`);
  return table[key];
};

test("a linked worktree maps back to the MAIN checkout, which must not read as outside the repo", () => {
  // The defect: `resolve(dirname(import.meta.url), "..")` made the report's own directory the
  // root. Run from `.worktrees/gsd-477-…` — which this repo does routinely — every ordinary
  // session in the main checkout counted as "outside the repo", fabricating signal 4's adopt.
  const { roots, source } = resolveRepoRoots(null, {
    cwd: "C:\\repo\\.worktrees\\wt",
    git: fakeGit({
      "rev-parse --show-toplevel": "C:/repo/.worktrees/wt",
      "rev-parse --git-common-dir": "C:/repo/.git"
    })
  });
  assert.equal(source, "git");
  assert.deepEqual(roots, [path.resolve("C:/repo/.worktrees/wt"), path.resolve("C:/repo")]);

  assert.equal(isOutsideAllRoots("C:\\repo\\src", roots), false, "the MAIN checkout is inside");
  assert.equal(isOutsideAllRoots("C:\\repo\\.worktrees\\wt\\scripts", roots), false, "so is the worktree");
  assert.equal(isOutsideAllRoots("C:\\other-project", roots), true, "and a genuinely other project is still outside");

  const s4 = find(summarize({ rows: [row({ cwd: "C:\\repo\\src" })], repoRoots: roots, repoRootSource: "git" }), 4);
  assert.equal(s4.verdict, "REVERT", "an in-repo session must not be counted as evidence of outside use");
  assert.equal(s4.detail.length, 0);
});

test("--git-common-dir is resolved relative to cwd when git returns the bare '.git' of a main checkout", () => {
  const { roots } = resolveRepoRoots(null, {
    cwd: "C:\\repo",
    git: fakeGit({ "rev-parse --show-toplevel": "C:/repo", "rev-parse --git-common-dir": ".git" })
  });
  assert.deepEqual(roots, [path.resolve("C:/repo")], "the two roots collapse to one in a plain checkout");
});

test("--repo-root overrides git, and a missing git falls back LOUDLY", () => {
  const override = resolveRepoRoots("C:\\repo", {
    cwd: "C:\\elsewhere",
    git: () => { throw new Error("git must not be consulted"); },
    exists: () => true
  });
  assert.equal(override.source, "override");
  assert.deepEqual(override.roots, [path.resolve("C:\\repo")]);

  const fallback = resolveRepoRoots(null, {
    cwd: "C:\\repo\\.worktrees\\wt",
    git: () => { throw new Error("'git' is not recognized"); },
    fallbackRoot: "C:\\repo\\.worktrees\\wt"
  });
  assert.equal(fallback.source, "fallback");
  assert.deepEqual(fallback.roots, [path.resolve("C:\\repo\\.worktrees\\wt")]);
  // The fallback still has the old defect — it cannot not have it, there is nothing else to ask.
  // What it must not do is stay quiet about it, and what it must not do NOW is score a verdict.
  assert.equal(isOutsideAllRoots("C:\\repo\\src", fallback.roots), true);
  const s4 = find(summarize({ rows: [row()], repoRoots: fallback.roots, repoRootSource: "fallback" }), 4);
  assert.match(s4.caveat, /GIT WAS UNAVAILABLE/);
  assert.match(s4.caveat, /--repo-root/);
  assert.match(s4.caveat, /PARENT of the directory containing this script/,
    "the old wording said 'the directory containing this script', which is not what the fallback uses");
});

test("a FALLBACK repo root makes signal 4 UNMEASURED — a git failure must not flip the verdict", () => {
  // Demonstrated by toggling PATH alone: same log, same rows, verdict flips REVERT -> ADOPT,
  // because every session in the main checkout reads as "outside" a worktree-shaped fallback root.
  // The measurement is only as good as its boundary, so the boundary's PROVENANCE is part of
  // availability — not a footnote under a scored verdict.
  const rows = [row({ session: "a", cwd: "C:\\repo\\src" })];
  const fallbackRoots = [path.resolve("C:\\repo\\.worktrees\\wt")];

  const untrusted = summarize({ rows, repoRoots: fallbackRoots, repoRootSource: "fallback" });
  assert.equal(find(untrusted, 4).verdict, "UNMEASURED", "a fallback root cannot score this signal either way");
  assert.match(find(untrusted, 4).caveat, /^UNMEASURED —/);
  assert.equal(untrusted.sources.repoRootTrusted, false);
  assert.equal(untrusted.overall.verdict, "INCOMPLETE");

  // The SAME rows against a git-resolved root are scored, so the gate is about provenance and not
  // about the data.
  const trusted = summarize({ rows, repoRoots: [path.resolve("C:\\repo")], repoRootSource: "git" });
  assert.equal(find(trusted, 4).verdict, "REVERT");
  assert.equal(trusted.sources.repoRootTrusted, true);

  // An explicit --repo-root is trusted: the operator named the boundary.
  assert.equal(find(summarize({ rows, repoRoots: [path.resolve("C:\\repo")], repoRootSource: "override" }), 4).verdict, "REVERT");
});

test("an EMPTY root list makes signal 4 UNMEASURED, never REVERT", () => {
  // With no root, nothing can be outside anything, so the old code scored a confident REVERT —
  // the one outcome this file's header says a missing source must never produce, since §11's
  // consequence for REVERT is closing #477 as not-planned.
  const summary = summarize({ rows: [row()], repoRoots: [] });
  assert.equal(find(summary, 4).verdict, "UNMEASURED");
  assert.equal(summary.sources.repoRootTrusted, false);
  assert.match(find(summary, 4).caveat, /no boundary to be outside OF/);
  assert.notEqual(summary.overall.verdict, "REVERT");
});

test("--repo-root must EXIST — a typo'd root would make every session read as outside it", () => {
  // A directory that is not there cannot contain any session, so signal 4 would score a confident
  // ADOPT off a mistyped path.
  assert.throws(() => resolveRepoRoots("C:\\definitely-not-a-real-directory-477", { git: () => { throw new Error("unused"); } }),
    /does not exist/);
  // And the real one is accepted, so the check is not simply refusing everything.
  assert.equal(resolveRepoRoots(HERE, { git: () => { throw new Error("unused"); } }).source, "override");
});

test("--repo-root is resolved to an absolute path, exercised with a RELATIVE one", () => {
  // Asserted with a relative path on purpose: against an already-absolute path `path.resolve` is
  // the identity function, so the old assertion passed whether the call was there or not.
  const relative = "sub\u002Fdir";
  const { roots, source } = resolveRepoRoots(relative, { exists: () => true, git: () => { throw new Error("unused"); } });
  assert.equal(source, "override");
  assert.deepEqual(roots, [path.resolve(process.cwd(), relative)]);
  assert.ok(path.isAbsolute(roots[0]), "a relative --repo-root must not reach the comparison as-is");
  assert.notEqual(roots[0], relative);
  // Surrounding whitespace is trimmed before resolving, so `--repo-root " C:\\x "` is not a
  // directory literally named " C:\\x ".
  assert.deepEqual(resolveRepoRoots("  C:\\repo  ", { exists: () => true, git: () => { throw new Error("unused"); } }).roots,
    [path.resolve("C:\\repo")]);
});

test("a successful --show-toplevel survives a --git-common-dir that throws", () => {
  // Both calls used to share one try with `roots` declared inside it, so a failure of the SECOND
  // question discarded the answer to the first and dropped the whole resolution to the untrusted
  // fallback — the path that fabricates signal 4's adopt condition.
  const { roots, source } = resolveRepoRoots(null, {
    cwd: "C:\\repo",
    git: args => {
      if (args[1] === "--show-toplevel") return "C:/repo";
      throw new Error("fatal: unknown option --git-common-dir");
    },
    fallbackRoot: "C:\\somewhere\\else"
  });
  assert.equal(source, "git", "one failed question must not throw away the other's answer");
  assert.deepEqual(roots, [path.resolve("C:/repo")]);

  // And the reverse: --show-toplevel failing must not discard --git-common-dir.
  const other = resolveRepoRoots(null, {
    cwd: "C:\\repo",
    git: args => {
      if (args[1] === "--git-common-dir") return ".git";
      throw new Error("fatal: not a git repository");
    },
    fallbackRoot: "C:\\somewhere\\else"
  });
  assert.equal(other.source, "git");
  assert.deepEqual(other.roots, [path.resolve("C:\\repo")]);
});

test("resolveRepoRoots really can drive git — the injected fake is not the only thing that works", () => {
  // Guards the shape of the arguments the fake stands in for: if `--show-toplevel` or
  // `--git-common-dir` were misspelled, every test above would still pass on the fake.
  const real = resolveRepoRoots(null, { cwd: HERE });
  assert.equal(real.source, "git", "this test file lives in a git repo");
  assert.ok(real.roots.length >= 1);
  assert.equal(isOutsideAllRoots(HERE, real.roots), false, "the directory git was asked about is inside the repo git named");
});

// --- 5b. A separate CLONE of this repo (not a linked worktree) must not read as outside -----
//
// resolveRepoRoots's ancestry walk (section 5, above) maps a `git worktree add` back to its main
// checkout because git can SEE that link (`--git-common-dir`). A `git clone` — a second, fully
// independent repository, such as a review fleet's throwaway sandbox — has none: its own
// `--git-common-dir` points at ITS OWN `.git`, with zero relation to the source repo. Without the
// check below, that clone's cwd reads as "outside KanbanFlow-v2/" and pure code-review or test
// traffic gets counted as signal 4's adopt evidence. This really happened: a reviewer's own
// sandbox wrote rows with a throwaway-clone cwd into the real usage log.
//
// `fakeGit` above is keyed ONLY on `args.join(" ")` and ignores which directory git was asked
// about — reused here it would hand every cwd the SAME table entry regardless of the `cwd`
// argument, so a same-repo comparison could not help but match and the test would pass even with
// the comparison deleted. `fakeGitByCwd` is keyed on cwd first, so a wrong or missing cwd argument
// throws "unexpected", not a silently-reused fixture.
//
// A table written one URL per remote (`remote get-url <name>`) still answers the `--all` and
// `--push --all` reads a candidate now gets: with no key of their own, both fall back to the plain
// entry — exactly what git reports for a remote with one `url` and no `pushurl`, whose only URL is
// both where it fetches from and where it pushes to. A table that spells a `--all` or `--push --all`
// key out is answered from it, and a remote with no entry at all still throws, so an inconclusive
// read stays inconclusive.
const fakeGitByCwd = table => (args, cwd) => {
  const key = args.join(" ");
  const forCwd = table[cwd];
  const plain = key.replace(/^remote get-url (?:--push )?--all /, "remote get-url ");
  const hit = !forCwd ? null : key in forCwd ? key : plain in forCwd ? plain : null;
  if (hit === null) throw new Error(`unexpected: git -C ${cwd} ${key}`);
  return forCwd[hit];
};

test("a git clone of THIS repo is confirmed by its root commit and folded in as inside", () => {
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const cloneCwd = path.resolve("C:\\scratch\\clone");
  const otherCwd = path.resolve("C:\\other-project");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    // Root commit matches the main root's. Every remote is listed FIRST now (`git remote`, #5b) —
    // this cwd's listing is EMPTY (a real no-remote repo: `git remote` succeeds with no output, it
    // does not fail), so there is nothing to veto with, and the root-commit path is what confirms
    // here, exactly the case a no-remote local clone hits in practice.
    [cloneCwd]: {
      "remote": "",
      "rev-parse --show-toplevel": cloneCwd,
      "rev-list --max-parents=0 HEAD": "aaa111"
    },
    // A genuinely different repo: neither root commit nor remote matches. Must stay outside.
    [otherCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": otherCwd,
      "rev-list --max-parents=0 HEAD": "zzz999",
      "remote get-url origin": "https://github.com/someone/other.git"
    }
  });

  const { confirmed, probed, capped } = withConfirmedCloneRoots([cloneCwd, otherCwd], [mainRoot], { git });
  assert.equal(capped, false);
  assert.equal(probed, 2);
  assert.deepEqual([...confirmed.keys()], [cloneCwd], "only the confirmed clone is returned");
  assert.equal(confirmed.get(cloneCwd), cloneCwd);

  // End to end: folding the confirmed CWD into `cloneRoots` — the map's key, exactly as main() does,
  // never the toplevel it maps to — must remove exactly that session from signal 4's outside
  // evidence, and no others.
  const rows = [
    row({ session: "review-sandbox", cwd: cloneCwd }),
    row({ session: "genuinely-external", cwd: otherCwd })
  ];
  const s4 = find(summarize({
    rows, repoRoots: [mainRoot], repoRootSource: "git", cloneRoots: [...confirmed.keys()]
  }), 4);
  assert.equal(s4.detail.length, 1, "the confirmed clone must not count as outside evidence");
  assert.equal(s4.detail[0].session, "genuinely-external");
  assert.match(s4.caveat, /confirmed/i);
  assert.ok(quotedValues(s4.caveat).includes(cloneCwd), "the caveat names which cwd was reclassified");
});

test("a shared root commit does NOT confirm a fork or renamed derivative pushed to a different network remote", () => {
  // A fork keeps the original's history up to the point it diverged, so root commits still match —
  // but it is genuinely different work someone is doing elsewhere, not this repository. A remote
  // naming a DIFFERENT network project is real evidence a bare root-commit match would discard.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const forkCwd = path.resolve("C:\\scratch\\someone-elses-fork");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    [forkCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": forkCwd,
      "rev-list --max-parents=0 HEAD": "aaa111", // SAME root commit: shared history from before the fork
      "remote get-url origin": "https://github.com/someone-else/kanbanflow-v2-fork.git" // DIFFERENT project
    }
  });
  const { confirmed } = withConfirmedCloneRoots([forkCwd], [mainRoot], { git });
  assert.equal(confirmed.size, 0, "shared history does not make a fork the same project once it has its own network remote");
});

test("a root-commit match is still trusted when the candidate's remote is a LOCAL path, not a network project", () => {
  // `git clone C:\...\KanbanFlow-v2 kf` (this review fleet's own pattern) leaves the clone's remote
  // pointing at the source's local path, not at any network project — that contradicts nothing
  // about the shared root commit, so it must not be treated the same as a genuinely different fork.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const localCloneCwd = path.resolve("C:\\scratch\\local-clone");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    [localCloneCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": localCloneCwd,
      "rev-list --max-parents=0 HEAD": "aaa111", // shared history
      "remote get-url origin": "C:\\Users\\repo\\dev\\KanbanFlow-v2" // a LOCAL PATH, not a network remote
    }
  });
  const { confirmed } = withConfirmedCloneRoots([localCloneCwd], [mainRoot], { git });
  assert.equal(confirmed.size, 1, "a local-path remote does not contradict the shared root commit");
});

test("isLocalPathRemote tells a local path apart from a network remote, per git's own rule", () => {
  assert.equal(isLocalPathRemote("C:\\Users\\joel.fuller\\repo"), process.platform === "win32", "drive syntax is local only on Windows");
  // C:/path: on Windows this is a drive path; on POSIX Git parses it as SCP (host "C", absolute
  // path /Users/repo), which is a network remote.
  assert.equal(isLocalPathRemote("C:/Users/repo"), process.platform === "win32", "forward-slash Windows drive path is local only on Windows");
  if (process.platform !== "win32") {
    assert.equal(isLocalPathRemote("C:repo"), false, "on POSIX, single-letter SCP host is a network remote, not a drive letter");
    assert.equal(isLocalPathRemote("D:/path/repo"), false, "on POSIX, D:/path is SCP (host D, absolute path), not a drive path");
  }
  assert.equal(isLocalPathRemote("/home/joel/repo"), true, "absolute POSIX path");
  assert.equal(isLocalPathRemote("\\\\server\\share\\repo"), true, "UNC path");
  assert.equal(isLocalPathRemote("./relative/repo"), true);
  assert.equal(isLocalPathRemote("https://github.com/x/y.git"), false);
  assert.equal(isLocalPathRemote("ssh://git@github.com/x/y"), false);
  // file:// is git's own exception: it spells a path as a URL, but it is still a path, not a
  // network project — the earlier version of this function got this backwards (round-3 finding N1).
  assert.equal(isLocalPathRemote("file:///home/joel/repo"), true, "file:// names a local path");
  assert.equal(isLocalPathRemote("git@github.com:x/y.git"), false, "scp shorthand with userinfo");
  assert.equal(isLocalPathRemote("github.com:x/y.git"), false, "scp shorthand WITHOUT userinfo is still network");
  // The three shapes the earlier TLD-guessing rule got wrong (N1): an IPv4 host, a single-label
  // host, and an SSH config alias all lack a dotted letters-only suffix, but are still legitimate
  // scp hosts — none of them is a Windows drive letter, so none of them is local.
  assert.equal(isLocalPathRemote("git@192.168.1.10:alice/repo.git"), false, "an IPv4 host is still network");
  assert.equal(isLocalPathRemote("gitserver:alice/repo.git"), false, "a single-label host is still network");
  assert.equal(isLocalPathRemote("git@example-work:alice/repo.git"), false, "an SSH config alias is still network");
  assert.equal(isLocalPathRemote(null), false);
  assert.equal(isLocalPathRemote(""), false);
});

test("remote path classification follows both native Windows and POSIX Git parser rules", () => {
  // Git url.c:url_is_local_not_ssh uses the first '/' and its platform DOS-drive predicate.
  // On POSIX that predicate is false; backslashes are literal characters, not separators.
  // https://github.com/git/git/blob/v2.55.0/url.c#L128-L134
  for (const [url, windowsLocal, posixLocal] of [
    ["C:repo", true, false], ["C:/repo", true, false], ["C:\\repo", true, false],
    ["C:repo:other", false, false], ["C:re?po", false, false], ["C:CON", false, false], ["C:CON .txt", false, false], ["C:AUX .ext", false, false], ["C:LPT1 .ext", false, false],
    ["before\\slash:repo", false, false], ["before/slash:repo", true, true],
    ["user@C:repo", false, false], ["x::host/repo", false, false],
    ["/local/repo", true, true], ["file:///local/repo", true, true]
  ]) {
    assert.equal(isLocalPathRemote(url, { platform: "win32" }), windowsLocal, "Windows: " + url);
    assert.equal(isLocalPathRemote(url, { platform: "linux" }), posixLocal, "POSIX parser contract: " + url);
  }
  assert.equal(normalizeRemoteUrl("C:/repo", { platform: "linux" }),
    normalizeRemoteUrl("ssh://c/repo", { platform: "linux" }), "the POSIX SCP form has an absolute SSH path");
});

test("native Git diagnostics verify the host platform's drive and backslash classification without connecting", () => {
  for (const [url, validDrive] of [["C:repo", true], ["C:/repo", true], ["C:\\repo", true],
      ["C:repo:other", false], ["C:re?po", false], ["C:CON", false], ["C:CON .txt", false], ["C:AUX .ext", false], ["C:LPT1 .ext", false], ["before\\slash:repo", false]]) {
      // Git's CONNECT_DIAG_URL returns before opening the file/SSH transport.
      const diagnostic = gitOutput(["fetch-pack", "--diag-url", url], HERE);
      const local = process.platform === "win32" && validDrive;
      assert.match(diagnostic, new RegExp("^Diag: protocol=" + (local ? "file" : "ssh") + "$", "m"), url);
      assert.equal(isLocalPathRemote(url), local, "match this installed Git's parser for " + url);
  }
});

test("real configured single-letter remotes veto shared history under the POSIX parser contract", () => {
  const { a, b, cleanup } = buildCloneFixture();
  try {
    gitOutput(["remote", "add", "origin", "C:fork"], b);
    for (const [url, windowsConfirmed] of [["C:fork", true], ["C:/fork", true], ["C:\\fork", true], ["before\\slash:fork", false],
      ["C:fork:other", false], ["C:re?po", false], ["C:CON", false], ["C:CON .txt", false], ["C:AUX .ext", false], ["C:LPT1 .ext", false]]) {
      gitOutput(["remote", "set-url", "origin", url], b);
      assert.equal(gitOutput(["remote", "get-url", "origin"], b), url, "real Git stores the exact namespace being judged");
      assert.equal(withConfirmedCloneRoots([b], [a], { platform: "win32" }).confirmed.has(b), windowsConfirmed, "Windows contract: " + url);
      assert.equal(withConfirmedCloneRoots([b], [a], { platform: "linux" }).confirmed.has(b), false,
        "a POSIX SCP remote must veto shared history: " + url);
    }
  } finally { cleanup(); }
});

test("ambiguous Windows drive forms cannot prove a matching remote or shared-root clone", () => {
  const { a, b, c, cleanup } = buildCloneFixture();
  try {
    gitOutput(["remote", "add", "origin", "C:CON"], b);
    for (const url of ["C:CON", "C:CON .txt", "C:AUX .ext", "C:LPT1 .ext", "C:repo:other", "1:repo", "ä:repo"]) {
      for (const cwd of [a, b, c]) gitOutput(["remote", "set-url", "origin", url], cwd);
      assert.equal(withConfirmedCloneRoots([b, c], [a], { platform: "win32" }).confirmed.size, 0,
        "ambiguous Windows paths cannot contribute URL or root-commit proof: " + url);
      assert.deepEqual([...withConfirmedCloneRoots([b, c], [a], { platform: "linux" }).confirmed.keys()], [b, c],
        "the same explicit POSIX SSH namespace still supplies matching URL evidence: " + url);
    }
  } finally { cleanup(); }
});

test("normalizeRemoteUrl keeps unknown SCP, absolute SSH and HTTPS namespaces distinct", () => {
  // Git's SSH account and home-relative SCP path can select another repository. Only a known
  // hosting service's documented aliases justify dropping those distinctions.
  const https = normalizeRemoteUrl("https://example.invalid/org/repo.git");
  assert.notEqual(normalizeRemoteUrl("example.invalid:org/repo.git"), https);
  assert.notEqual(normalizeRemoteUrl("ssh://git@example.invalid:22/org/repo.git"), https);
  // A scheme must never be misparsed as a bare scp host — the naive fix for the line above.
  assert.notEqual(normalizeRemoteUrl("https://example.invalid/org/repo.git"), "https/" + "/example.invalid/org/repo");
  // ssh.github.com:443 (GitHub's over-443 SSH endpoint) is a DIFFERENT host from github.com, so it
  // must NOT normalize the same as a plain github.com remote — see the caveat's gap list.
  assert.notEqual(normalizeRemoteUrl("ssh://git@ssh.github.com:443/x/y.git"), normalizeRemoteUrl("https://github.com/x/y.git"));
  assert.notEqual(normalizeRemoteUrl("git@host::path"), normalizeRemoteUrl("ssh://git@host/:path"),
    "userinfo is SCP syntax with a home-relative :path, not an absolute SSH path");
});

test("a NON-default port is another service, not a spelling — only the scheme's default port folds away", () => {
  // The port used to be dropped from any `scheme://` URL, so https://host:8443/org/repo and
  // https://host/org/repo shared a key, and a checkout of the service on 8443 matched this
  // repository and was folded in as inside.
  const plain = normalizeRemoteUrl("https://example.invalid/org/repo.git");
  assert.notEqual(normalizeRemoteUrl("https://example.invalid:8443/org/repo.git"), plain, "8443 is not https's default port");
  const ssh = normalizeRemoteUrl("ssh://git@example.invalid/org/repo.git");
  assert.notEqual(normalizeRemoteUrl("ssh://git@example.invalid:2222/org/repo.git"), ssh, "2222 is not ssh's default port");
  // The controls: a scheme's own default port is the same endpoint, spelled out.
  assert.equal(normalizeRemoteUrl("https://example.invalid:443/org/repo.git"), plain, "443 is https's default");
  assert.equal(normalizeRemoteUrl("ssh://git@example.invalid:22/org/repo.git"), ssh, "22 is ssh's default");
  assert.equal(normalizeRemoteUrl("http://example.invalid:80/org/repo.git"), normalizeRemoteUrl("http://example.invalid/org/repo.git"));
  assert.equal(normalizeRemoteUrl("git://example.invalid:9418/org/repo.git"), normalizeRemoteUrl("git://example.invalid/org/repo.git"));

  // End to end: the clone check reads the port the same way.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const otherPort = path.resolve("C:\\scratch\\port-8443");
  const defaultPort = path.resolve("C:\\scratch\\port-443");
  const candidate = (cwd, url) => ({ "remote": "origin", "rev-parse --show-toplevel": cwd, "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": url });
  const git = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": "https://example.invalid/org/repo.git" },
    [otherPort]: candidate(otherPort, "https://example.invalid:8443/org/repo.git"),
    [defaultPort]: candidate(defaultPort, "https://example.invalid:443/org/repo.git")
  });
  const { confirmed } = withConfirmedCloneRoots([otherPort, defaultPort], [mainRoot], { git });
  assert.deepEqual([...confirmed.keys()], [defaultPort], "the :8443 checkout stays outside; the :443 one is this repository");
});

test("#5b: EVERY remote is checked, not only origin — a renamed origin plus a new network remote still vetoes", () => {
  // A derivative can rename `origin` to anything (here `upstream`, pointing back at the TRUE main
  // root) and add its OWN remote under a different name (`github`, a genuinely different project).
  // Checking only a fixed name ("origin") would see nothing here and wrongly fall through to
  // root-commit, which WOULD match (shared history) — exactly the false confirmation rule (A)
  // exists to prevent. This mirrors this repo's own real fixture, `derivNoOrigin`.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const derivCwd = path.resolve("C:\\scratch\\derivative-no-origin");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    [derivCwd]: {
      "remote": "github\nupstream", // two remotes, NEITHER named "origin"
      "rev-parse --show-toplevel": derivCwd,
      "rev-list --max-parents=0 HEAD": "aaa111", // shared history with main
      "remote get-url github": "https://github.com/alice/derivative.git", // a DIFFERENT project
      "remote get-url upstream": "C:\\real\\KanbanFlow-v2" // points at main itself, but doesn't matter
    }
  });
  const { confirmed } = withConfirmedCloneRoots([derivCwd], [mainRoot], { git });
  assert.equal(confirmed.size, 0, "the OTHER network remote (github) vetoes, even though upstream points at main and history matches");
});

test("a fork that ALSO carries this repo's URL (origin = the fork, upstream = this repo) is vetoed on the remote-match path too", () => {
  // The ordinary fork layout. `upstream` MATCHES this repository's own remote, and a match used to
  // be proof on its own, checked BEFORE the veto — so the veto only ever guarded the root-commit
  // path, and this checkout confirmed as a clone and was folded in as inside. It is a fork: other
  // work, pushed to a different project, which is exactly what signal 4 exists to count. The veto
  // now runs first and binds BOTH paths: a matching remote shows the checkout knows this
  // repository, not that it is it.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const forkCwd = path.resolve("C:\\scratch\\alice-fork");
  const plainCloneCwd = path.resolve("C:\\scratch\\plain-clone");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    [forkCwd]: {
      "remote": "origin\nupstream",
      "rev-parse --show-toplevel": forkCwd,
      "rev-list --max-parents=0 HEAD": "aaa111", // shared history, too — neither path may confirm it
      "remote get-url origin": "https://github.com/alice/kanbanflow-v2.git", // the fork: a DIFFERENT project
      "remote get-url upstream": "https://github.com/joel/kanbanflow-v2.git" // matches main exactly
    },
    // The control: a plain clone whose ONLY remote is this repository's own URL. Nothing mismatches,
    // so the veto has nothing to fire on, and the remote match alone must still confirm it — no
    // root-commit entry here, so the root-commit path cannot be what does.
    [plainCloneCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": plainCloneCwd,
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    }
  });
  const { confirmed } = withConfirmedCloneRoots([forkCwd, plainCloneCwd], [mainRoot], { git });
  assert.equal(confirmed.has(forkCwd), false, "a non-matching origin vetoes, even with a matching upstream and shared history");
  assert.deepEqual([...confirmed.keys()], [plainCloneCwd], "a clone whose only remote is this repo's own URL still confirms");
});

test("a fork kept as ONE remote is vetoed too — every fetch AND push URL of a remote is checked, not only the first", () => {
  // `git remote get-url <name>` answers with ONE url: the first fetch URL. A fork kept as a single
  // remote names this repository there and the fork somewhere else, so reading only that one URL
  // confirmed both of these as clones:
  //   triangular — fetches from this repository, pushes to the fork (`git remote set-url --push`);
  //   secondUrl  — this repository first, the fork added as a second url (`git remote set-url --add`).
  // The keys below are what real git answers for those two configurations.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const FORK = "https://github.com/alice/kanbanflow-v2.git";
  const triangularCwd = path.resolve("C:\\scratch\\triangular");
  const secondUrlCwd = path.resolve("C:\\scratch\\second-url");
  const plainCloneCwd = path.resolve("C:\\scratch\\plain-clone");
  const git = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN },
    [triangularCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": triangularCwd,
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": MAIN,
      "remote get-url --all origin": MAIN,
      "remote get-url --push --all origin": FORK
    },
    [secondUrlCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": secondUrlCwd,
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": MAIN,
      "remote get-url --all origin": `${MAIN}\n${FORK}`,
      "remote get-url --push --all origin": `${MAIN}\n${FORK}`
    },
    // The control: one url, no pushurl — its only URL is this repository's, fetch and push alike.
    [plainCloneCwd]: { "remote": "origin", "rev-parse --show-toplevel": plainCloneCwd, "remote get-url origin": MAIN }
  });
  const { confirmed } = withConfirmedCloneRoots([triangularCwd, secondUrlCwd, plainCloneCwd], [mainRoot], { git });
  assert.deepEqual([...confirmed.keys()], [plainCloneCwd],
    "the fork's URL vetoes wherever the remote carries it — as a push URL or a second URL — and the plain clone still confirms");
});

test("a match on a PUSH url or a later url is not proof — unrelated history that merely pushes here stays outside", () => {
  // The veto reads every url; a MATCH may rest only on a remote's fetch url (its first url, the one
  // git fetches from). Matching on every url would confirm both candidates below: their fetch url is
  // a local path, their history is unrelated to this repository's, and the only thing tying them to
  // it is where they push — the root-commit check is what used to keep them out, and still must.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const LOCAL = "C:\\some\\local\\path";
  const pushesHere = path.resolve("C:\\scratch\\unrelated-pushes-here");
  const secondUrlHere = path.resolve("C:\\scratch\\unrelated-second-url-here");
  const localCloneThatPushesHere = path.resolve("C:\\scratch\\local-clone-pushes-here");
  const git = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN },
    // Each table answers all three reads the way git does: plain `get-url` is the first url.
    [pushesHere]: {
      "remote": "origin", "rev-parse --show-toplevel": pushesHere, "rev-list --max-parents=0 HEAD": "zzz999",
      "remote get-url origin": LOCAL, "remote get-url --all origin": LOCAL, "remote get-url --push --all origin": MAIN
    },
    [secondUrlHere]: {
      "remote": "origin", "rev-parse --show-toplevel": secondUrlHere, "rev-list --max-parents=0 HEAD": "zzz999",
      "remote get-url origin": LOCAL, "remote get-url --all origin": `${LOCAL}\n${MAIN}`, "remote get-url --push --all origin": `${LOCAL}\n${MAIN}`
    },
    // The control: the same shape as `pushesHere`, but with THIS repository's history — the root-
    // commit path still confirms it, so the push url is neither proof nor an obstacle.
    [localCloneThatPushesHere]: {
      "remote": "origin", "rev-parse --show-toplevel": localCloneThatPushesHere, "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": LOCAL, "remote get-url --all origin": LOCAL, "remote get-url --push --all origin": MAIN
    }
  });
  const { confirmed } = withConfirmedCloneRoots([pushesHere, secondUrlHere, localCloneThatPushesHere], [mainRoot], { git });
  assert.deepEqual([...confirmed.keys()], [localCloneThatPushesHere],
    "only the candidate with this repository's history confirms; a match on where the others push is not proof");
});

test("the MAIN root is matched on its FETCH urls only — where main pushes, or mirrors to, is never proof of a clone", () => {
  // git fetches from a remote's FIRST url only; a pushurl, and any url after the first, are never
  // where main fetches from (a url after the first is pushed to only while no pushurl is set).
  // Counting those as main's identity would widen what a candidate can match: a checkout of main's
  // push target, or of a mirror it pushes to, would be confirmed as a clone of this repository,
  // which the fingerprint never accepted before. So main contributes its fetch urls alone, and a
  // candidate whose only URL is either of those stays outside.
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const PUSH_TARGET = "https://github.com/alice/kanbanflow-v2.git";
  const MIRROR = "https://mirror.example.invalid/joel/kanbanflow-v2.git";
  const mainPushes = path.resolve("C:\\real\\main-with-pushurl");
  const mainMirrors = path.resolve("C:\\real\\main-with-second-url");
  const pushTargetCheckout = path.resolve("C:\\scratch\\push-target-checkout");
  const mirrorCheckout = path.resolve("C:\\scratch\\mirror-checkout");
  const plainCloneCwd = path.resolve("C:\\scratch\\plain-clone");
  const candidate = (cwd, url) => ({ "remote": "origin", "rev-parse --show-toplevel": cwd, "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": url });
  const git = fakeGitByCwd({
    [mainPushes]: {
      "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": MAIN, "remote get-url --all origin": MAIN, "remote get-url --push --all origin": PUSH_TARGET
    },
    [mainMirrors]: {
      "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": MAIN, "remote get-url --all origin": `${MAIN}\n${MIRROR}`, "remote get-url --push --all origin": `${MAIN}\n${MIRROR}`
    },
    [pushTargetCheckout]: candidate(pushTargetCheckout, PUSH_TARGET),
    [mirrorCheckout]: candidate(mirrorCheckout, MIRROR),
    [plainCloneCwd]: candidate(plainCloneCwd, MAIN)
  });
  const pushed = withConfirmedCloneRoots([pushTargetCheckout, plainCloneCwd], [mainPushes], { git }).confirmed;
  assert.deepEqual([...pushed.keys()], [plainCloneCwd], "main's pushurl is not something a candidate can match");
  const mirrored = withConfirmedCloneRoots([mirrorCheckout, plainCloneCwd], [mainMirrors], { git }).confirmed;
  assert.deepEqual([...mirrored.keys()], [plainCloneCwd], "nor is a url after main's first, which git only pushes to");
});

test("#5c: an inconclusive remote check is NOT read as 'no remote' — it must not launder a fork past the veto", () => {
  // The reviewer's own M3b mutation: a `remote get-url` call that merely FAILS (a transient error,
  // not "no such remote") used to read as `rawRemote === null`, exactly like a genuine no-remote
  // clone, and root commit then confirmed a fork it should have vetoed. Fails closed now: the WHOLE
  // candidate is left unconfirmed, whether the failure happens on a fork-shaped repo or a
  // genuinely local one — "inconclusive" must mean inconclusive, not "assume local".
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const forkWithFlakyRemote = path.resolve("C:\\scratch\\fork-flaky-remote");
  const localWithFlakyRemote = path.resolve("C:\\scratch\\local-flaky-remote");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    [forkWithFlakyRemote]: {
      "remote": "origin", // the LISTING succeeds...
      "rev-parse --show-toplevel": forkWithFlakyRemote,
      "rev-list --max-parents=0 HEAD": "aaa111" // shared history — would confirm if this read as "no remote"
      // ...but "remote get-url origin" has NO entry, so the fake throws: the URL read fails.
    },
    [localWithFlakyRemote]: {
      "remote": "origin",
      "rev-parse --show-toplevel": localWithFlakyRemote,
      "rev-list --max-parents=0 HEAD": "aaa111"
      // Same shape: the listing succeeds, the URL read fails. A genuinely local clone must ALSO
      // stay unconfirmed here — inconclusive is inconclusive regardless of what it might have been.
    }
  });
  const { confirmed } = withConfirmedCloneRoots([forkWithFlakyRemote, localWithFlakyRemote], [mainRoot], { git });
  assert.equal(confirmed.size, 0, "a failed remote-URL read must leave BOTH candidates unconfirmed, not fall through to root commit");
});

test("a PUSH-url read that fails is inconclusive too — even when the fetch read answered with this repository's URL", () => {
  // A candidate is read twice per remote, fetch urls and push urls. Only the push read fails here, and
  // it is exactly the read that would have named a fork kept as one remote; reading its failure as
  // "no push url" would confirm on the fetch url alone. The fetch read failing on its own is the same.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const pushReadFails = path.resolve("C:\\scratch\\push-read-fails");
  const fetchReadFails = path.resolve("C:\\scratch\\fetch-read-fails");
  const git = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN },
    // No plain entry, so the fallback cannot answer: only the read spelled out here succeeds.
    [pushReadFails]: {
      "remote": "origin", "rev-parse --show-toplevel": pushReadFails, "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url --all origin": MAIN
    },
    [fetchReadFails]: {
      "remote": "origin", "rev-parse --show-toplevel": fetchReadFails, "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url --push --all origin": MAIN
    }
  });
  const { confirmed } = withConfirmedCloneRoots([pushReadFails, fetchReadFails], [mainRoot], { git });
  assert.equal(confirmed.size, 0, "either read failing leaves the candidate unconfirmed — inconclusive, never 'no url'");
});

// A fixture builder that makes several directories runs BEFORE its caller's `try` exists — the
// `cleanup` it returns is what that `finally` would call — so a step that throws part-way through
// construction must remove what it already made itself, or nothing ever will: every directory made
// so far used to leak into os.tmpdir() on each such failure. `build` receives `make` (a `tmp()` that
// records what it made) and returns the fixture's fields; `make` is handed back with `cleanup`, so a
// test can add a directory of its own INSIDE its try and have the same cleanup remove it.
function buildInto(build) {
  const dirs = [];
  const make = () => { const d = tmp(); dirs.push(d); return d; };
  const cleanup = () => { for (const d of dirs) fs.rmSync(d, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); };
  try {
    return { ...build(make), make, cleanup };
  } catch (error) {
    cleanup();
    throw error;
  }
}

// Twenty-four real repositories covering every remote-classification shape rule (A) addresses, built
// fresh in os.tmpdir() every run, never read from a machine-specific fixture directory: this is the
// same key an earlier version of this test read from one, self-contained now instead — this test
// creates every repo itself, needs no external fixture directory, and works identically in any
// future session.
function buildRuleAFixture() {
  return buildInto(make => {
    // Construct owned local repositories separately from the report's five-second read budget.
    const setupGit = (args, cwd) => {
      const child = spawnSync("git", args, {
        cwd, encoding: "utf8", timeout: 30_000, maxBuffer: 256 * 1024, windowsHide: true,
        env: { ...scrubGitEnv(process.env), GIT_CONFIG_NOSYSTEM: "1",
          GIT_CONFIG_SYSTEM: "/dev/null", GIT_CONFIG_GLOBAL: "/dev/null" }
      });
      if (child.error) throw child.error;
      assert.equal(child.status, 0, child.stderr);
    };
    const clone = (source, dir, options = []) => setupGit(["clone", "-q", ...options, source, dir], a);
    const cloneWithRemote = (remoteUrl, srcOrigin = "origin") => {
      const dir = make();
      clone(a, dir);
      if (remoteUrl !== undefined) setupGit(["remote", "set-url", srcOrigin, remoteUrl], dir);
      return dir;
    };

    const a = make();
    initRealRepo(a, "a.txt", setupGit);
    setupGit(["remote", "add", "origin", "https://example.invalid/org/repo.git"], a);
    // a's origin carries a SECOND url as well. git fetches from the first url only, so a is matched on
    // that first url alone; `mainSecondUrlOnly` below, whose only remote is this second url, must stay
    // outside, exactly as it did before every url was read.
    setupGit(["remote", "set-url", "--add", "origin", "https://mirror.example.invalid/org/repo.git"], a);

    // --- CONFIRMED: shares a's root commit, and either the remote is local or matches a's ---------
    const localClone = cloneWithRemote(); // origin left as-is: git clone sets it to a's own local path
    const fileUrlClone = make();
    clone(`file:///${a.replace(/\\/g, "/")}`, fileUrlClone); // needs N1
    // Its only remote is a's own URL, spelled exactly as a spells it: the veto that now binds the
    // remote-match path too has nothing to fire on here, and must leave this one confirmed.
    const sameRepoHttps = cloneWithRemote("https://example.invalid/org/repo.git");
    // A genuine clone that fetches from a's local path and PUSHES to a's own URL. Its fetch url names no
    // network project, so it takes the root-commit path — decided on its fetch urls, never on every
    // url — and a's history confirms it: the push url is neither proof nor an obstacle.
    const localClonePushesToA = cloneWithRemote();
    setupGit(["remote", "set-url", "--push", "origin", "https://example.invalid/org/repo.git"], localClonePushesToA);

    // --- OUTSIDE: shares a's root commit, but a network remote vetoes it (rule A) ------------------
    // Neither an unknown SSH account's home-relative SCP path nor its absolute SSH path proves
    // identity with the HTTPS repository, even when their path text and history happen to match.
    const unprovenScpNamespace = cloneWithRemote("example.invalid:org/repo.git");
    const unprovenSshNamespace = cloneWithRemote("ssh://git@example.invalid:22/org/repo.git");
    const forkHttps = cloneWithRemote("https://example.invalid/alice/repo.git");
    const forkIPv4 = cloneWithRemote("git@192.168.1.10:alice/repo.git"); // needs N1
    const forkSingleLabel = cloneWithRemote("gitserver:alice/repo.git"); // needs N1
    const forkSshAlias = cloneWithRemote("git@example-work:alice/repo.git"); // needs N1
    // The deliberately-accepted gap, locked in rather than left to chance: SAME org/repo path as a,
    // but reached via an SSH config alias this file cannot resolve to a real hostname. Rule (A) does
    // not try to resolve it — an unresolvable alias is still "a network remote that doesn't match",
    // so it still vetoes, and this genuinely-same-repository clone still reads as outside.
    const aliasSpelledSameRepo = cloneWithRemote("git@example-work:org/repo.git");
    const derivNoOrigin = make();
    clone(a, derivNoOrigin, ["--origin", "upstream"]); // origin renamed away
    setupGit(["remote", "add", "github", "https://example.invalid/alice/derivative.git"], derivNoOrigin);
    // The ordinary fork layout: origin is the fork, upstream is a's own URL. The upstream MATCHES,
    // and the non-matching origin still vetoes — on the remote-match path, not only the root-commit one.
    const forkWithUpstream = cloneWithRemote("https://example.invalid/alice/repo.git");
    setupGit(["remote", "add", "upstream", "https://example.invalid/org/repo.git"], forkWithUpstream);
    // A fork kept as ONE remote. A single `get-url` reads the first fetch URL only; that misses the
    // fork in triangular, secondUrl and pushInsteadOfFork, and catches it in the other two:
    //   triangular: fetches from a, pushes to the fork (`set-url --push`);
    //   secondUrl: a's URL first, the fork added as a second url (`set-url --add`), pushed to as well;
    //   reverseTriangular: fetches from the fork, pushes to a — caught by the fetch read, not the push;
    //   pushInsteadOfFork: one plain url, a's own, but `pushInsteadOf` rewrites every push to the
    //     fork — git expands that for a push read, so the veto sees the fork where git would push;
    //   insteadOfFork: one plain url, a's own, but `insteadOf` rewrites it to the fork for fetch and
    //     push alike — git expands that in every read, a plain one included, so every read sees the
    //     fork. Its raw `remote.origin.url` still names a: read around git's rewriting, it would
    //     confirm.
    const triangular = cloneWithRemote("https://example.invalid/org/repo.git");
    setupGit(["remote", "set-url", "--push", "origin", "https://example.invalid/alice/repo.git"], triangular);
    const secondUrl = cloneWithRemote("https://example.invalid/org/repo.git");
    setupGit(["remote", "set-url", "--add", "origin", "https://example.invalid/alice/repo.git"], secondUrl);
    const reverseTriangular = cloneWithRemote("https://example.invalid/alice/repo.git");
    setupGit(["remote", "set-url", "--push", "origin", "https://example.invalid/org/repo.git"], reverseTriangular);
    const pushInsteadOfFork = cloneWithRemote("https://example.invalid/org/repo.git");
    setupGit(["config", "url.https://example.invalid/alice/.pushInsteadOf", "https://example.invalid/org/"], pushInsteadOfFork);
    const insteadOfFork = cloneWithRemote("https://example.invalid/org/repo.git");
    setupGit(["config", "url.https://example.invalid/alice/.insteadOf", "https://example.invalid/org/"], insteadOfFork);
    // EVERY url, not only the ones git would contact — a policy choice, locked in on purpose: the fork
    // is a second url that git neither fetches from (it fetches from the first url only) nor pushes to
    // (the pushurl pins pushes to a), and it still vetoes. A fork named anywhere in a remote's
    // configuration reads as a fork, which fails closed.
    const secondUrlPushPinned = cloneWithRemote("https://example.invalid/org/repo.git");
    setupGit(["remote", "set-url", "--add", "origin", "https://example.invalid/alice/repo.git"], secondUrlPushPinned);
    setupGit(["remote", "set-url", "--push", "origin", "https://example.invalid/org/repo.git"], secondUrlPushPinned);
    // Its only remote is a's SECOND url, one a never fetches from. Main is matched on its first url
    // alone, so this is a different URL, and it vetoes — as it did when only one url was read.
    const mainSecondUrlOnly = cloneWithRemote("https://mirror.example.invalid/org/repo.git");

    // --- OUTSIDE: genuinely unrelated (its own history) -------------------------------------------
    const unrelated = make();
    initRealRepo(unrelated, "unrelated.txt", setupGit);
    setupGit(["remote", "add", "origin", "https://example.invalid/other/thing.git"], unrelated);
    // Its own history and NO remote at all: nothing to veto with and nothing to match, so the root-
    // commit comparison is the only thing keeping it out. Without it, forcing that comparison open
    // was caught by a single test elsewhere, and only incidentally.
    const unrelatedNoRemote = make();
    initRealRepo(unrelatedNoRemote, "unrelated-no-remote.txt", setupGit);
    // Its own history; fetches from a's local path and PUSHES to a's own URL. A match on where it pushes
    // is not proof, so it takes the root-commit path like any local-only checkout — and its history is
    // not a's. Matching on every url, instead of on fetch urls, confirms exactly this one.
    const unrelatedPushesToA = make();
    initRealRepo(unrelatedPushesToA, "unrelated-pushes-to-a.txt", setupGit);
    setupGit(["remote", "add", "origin", a], unrelatedPushesToA);
    setupGit(["remote", "set-url", "--push", "origin", "https://example.invalid/org/repo.git"], unrelatedPushesToA);

    return {
      a, localClone, fileUrlClone, sameRepoHttps, unprovenScpNamespace, unprovenSshNamespace, localClonePushesToA,
      forkHttps, forkIPv4, forkSingleLabel, forkSshAlias, aliasSpelledSameRepo, derivNoOrigin, forkWithUpstream,
      triangular, secondUrl, reverseTriangular, pushInsteadOfFork, insteadOfFork, secondUrlPushPinned,
      mainSecondUrlOnly, unrelated, unrelatedNoRemote, unrelatedPushesToA
    };
  });
}

test("rule (A), rebuilt fresh in os.tmpdir(): the exact confirm/outside partition across all remote shapes", () => {
  const fx = buildRuleAFixture();
  try {
    const confirmedKeys = [
      "localClone", "fileUrlClone", "sameRepoHttps", "localClonePushesToA"
    ];
    const outsideKeys = [
      "unprovenScpNamespace", "unprovenSshNamespace", "forkHttps", "forkIPv4", "forkSingleLabel", "forkSshAlias", "aliasSpelledSameRepo",
      "derivNoOrigin", "forkWithUpstream", "triangular", "secondUrl", "reverseTriangular",
      "pushInsteadOfFork", "insteadOfFork", "secondUrlPushPinned", "mainSecondUrlOnly", "unrelated",
      "unrelatedNoRemote", "unrelatedPushesToA"
    ];
    const candidates = [...confirmedKeys, ...outsideKeys].map(k => fx[k]);
    const { confirmed } = withConfirmedCloneRoots(candidates, [fx.a]);

    // Every misplaced key is collected BEFORE asserting, so one failing run names them all: a loop of
    // asserts stops at the first, and hides every other misclassification behind it.
    const misplaced = [
      ...confirmedKeys.filter(key => !confirmed.has(fx[key]))
        .map(key => `${key} must be CONFIRMED (shares a's history; remote is local or matches)`),
      ...outsideKeys.filter(key => confirmed.has(fx[key]))
        .map(key => `${key} must stay OUTSIDE (vetoed by a non-matching/unresolvable network remote, or genuinely unrelated)`)
    ];
    assert.deepEqual(misplaced, [], "the confirm/outside partition must be exact");
    assert.equal(confirmed.size, confirmedKeys.length, "no unexpected extra confirmations");
  } finally {
    fx.cleanup();
  }
});

test("a real fork (forkHttps) whose remote listing merely FAILS is not confirmed — #5c against real git", () => {
  // The reviewer's own suggestion: real git cannot easily be made to fail on just one call, so a
  // real fixture (genuine shared history, a genuine fork-shaped remote) is combined with an
  // injected `git` that fails ONLY the remote-related calls for that one cwd, delegating every
  // other call (rev-parse, rev-list, and the MAIN root's own remote fingerprinting) to real git.
  const fx = buildRuleAFixture();
  try {
    const flakyGit = (args, cwd) => {
      if (path.resolve(cwd) === path.resolve(fx.forkHttps) && args[0] === "remote") {
        throw new Error("simulated transient failure reading forkHttps's remotes");
      }
      return gitOutput(args, cwd);
    };
    const { confirmed } = withConfirmedCloneRoots([fx.forkHttps], [fx.a], { git: flakyGit });
    assert.equal(confirmed.size, 0, "an inconclusive remote read must not fall through to root-commit confirmation");
  } finally {
    fx.cleanup();
  }
});

test("a shallow clone (no shared root commit) is still confirmed via a normalized origin remote", () => {
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const shallowCwd = path.resolve("C:\\scratch\\kf-shallow");
  const git = fakeGitByCwd({
    [mainRoot]: {
      "remote": "origin",
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git"
    },
    [shallowCwd]: {
      "remote": "origin",
      "rev-parse --show-toplevel": shallowCwd,
      // A shallow clone's only "root" is its fetch boundary, not the true initial commit.
      "rev-list --max-parents=0 HEAD": "shallow-boundary-commit-not-the-real-root",
      // Same repo, different case / scheme / trailing .git — must still normalize equal.
      "remote get-url origin": "git@github.com:joel/KanbanFlow-v2.git"
    }
  });
  const { confirmed } = withConfirmedCloneRoots([shallowCwd], [mainRoot], { git });
  assert.deepEqual([...confirmed.keys()], [shallowCwd]);
});

test("normalizeRemoteUrl folds case, a trailing .git, and https/ssh/scp syntax to the same key", () => {
  const forms = [
    "https://github.com/Joel/KanbanFlow-v2.git",
    "https://github.com/joel/kanbanflow-v2",
    "git@github.com:joel/kanbanflow-v2.git",
    "ssh://git@github.com/joel/kanbanflow-v2/"
  ];
  const normalized = new Set(forms.map(normalizeRemoteUrl));
  assert.equal(normalized.size, 1, `all four forms must normalize identically, got ${JSON.stringify([...normalized])}`);
  assert.notEqual(normalizeRemoteUrl("https://github.com/joel/OTHER-repo.git"), [...normalized][0]);
  assert.equal(normalizeRemoteUrl(null), null);
  assert.equal(normalizeRemoteUrl(""), null);
  assert.equal(normalizeRemoteUrl(undefined), null);
});

test("normalizeRemoteUrl preserves complete payloads after record framing is removed", () => {
  const value = "https://example.invalid/org/repo.git";
  const plain = normalizeRemoteUrl(value);
  for (const payload of [value + " ", " " + value, value + "\n", value + "\r\n"]) {
    assert.notEqual(normalizeRemoteUrl(payload), plain, "all supplied characters belong to the URL payload");
  }
  // Native get-url assertions below verify that the runner removes only Git's terminal LF.
});

test("normalizeRemoteUrl preserves case-sensitive repository paths while normalizing network hosts", () => {
  const forms = [
    "git://EXAMPLE.invalid/Org/Main.git",
    "ssh://git@EXAMPLE.invalid/Org/Main.git",
    "EXAMPLE.invalid:Org/Main.git",
    "https://EXAMPLE.invalid/Org/Main.git"
  ];
  for (const form of forms) {
    assert.equal(normalizeRemoteUrl(form), normalizeRemoteUrl(form.replace("EXAMPLE", "example")), "host casing does not change identity");
    assert.notEqual(normalizeRemoteUrl(form), normalizeRemoteUrl(form.replace("Main", "main")),
      "a case-sensitive server can host both paths as different repositories");
    assert.notEqual(normalizeRemoteUrl(form), normalizeRemoteUrl(form.replace(".git", "")),
      "an unknown server can expose both suffixes as different repositories");
  }
  assert.notEqual(normalizeRemoteUrl("https://github.com:8443/Org/Main.git"),
    normalizeRemoteUrl("https://github.com:8443/Org/main.git"), "a non-default port denotes a different service");
});

test("remote URL identity preserves payload whitespace instead of trimming it into an alias", () => {
  for (const url of ["https://example.invalid/org/repo.git", "https://github.com/org/repo.git",
    "ssh://alice@host/repo.git", "alice@host:repo.git", "softp://host/repo", "x::host/repo"]) {
    for (const payload of [url + " ", url + "\t", url + "\r", url + "\n", " " + url, "\t" + url])
      assert.notEqual(normalizeRemoteUrl(payload), normalizeRemoteUrl(url), JSON.stringify(payload));
  }
  assert.notEqual(normalizeRemoteUrl(" "), normalizeRemoteUrl(""), "a space is payload, not an absent value");
  assert.equal(isLocalPathRemote(" https://host/repo"), false, "leading whitespace must not be removed before classifying a network-shaped value");
  assert.equal(isLocalPathRemote(" C:/repo", { platform: "win32" }), false,
    "a leading payload space must not be removed to manufacture a Windows drive prefix");
});

test("generic SSH equivalences preserve the account and effective command path", () => {
  for (const [left, right] of [
    ["alice@host:/repo.git", "ssh://alice@HOST:22/repo.git"],
    ["alice@host:~/repo", "ssh://alice@host/~/repo"],
    ["alice@host:/~/repo", "ssh://alice@host/~/repo"],
    ["alice@host:~bob/repo", "ssh://alice@host/~bob/repo"],
    ["host:/repo", "ssh://HOST/repo"],
    ["alice@host:/dir with space", "ssh://alice@host/dir%20with%20space"],
    ["alice@host:/repo", "ssh://%61lice@host/repo"],
    ["git@github.com:Owner/Repo.git", "https://github.com/owner/repo"],
    ["ssh://git@github.com:22/OWNER/REPO.git", "https://github.com/owner/repo"]
  ]) {
    assert.equal(normalizeRemoteUrl(left), normalizeRemoteUrl(right), `${left} and ${right} must identify the same supported namespace`);
  }
});

test("generic SSH account and path namespaces stay distinct", () => {
  for (const [left, right] of [
    ["alice@host:repo.git", "bob@host:repo.git"],
    ["ssh://alice@host/repo", "ssh://bob@host/repo"],
    ["Alice@host:repo", "alice@host:repo"],
    ["host:repo", "alice@host:repo"],
    ["alice@host:repo", "ssh://alice@host/repo"],
    ["alice@host:repo", "alice@host:~/repo"],
    ["alice@host:repo", "alice@host:repo.git"],
    ["alice@host:~bob/repo", "alice@host:~carol/repo"],
    ["alice@host:~/a%2Fb", "ssh://alice@host/~/a%2Fb"],
    ["ssh://alice@host:2222/repo", "ssh://alice@host:22/repo"],
    ["ssh://alice@host/repo", "https://host/repo"],
    ["bob@github.com:Owner/Repo.git", "git@github.com:Owner/Repo.git"],
    ["ssh://git@github.com/~/owner/repo", "https://github.com/owner/repo"]
  ]) {
    assert.notEqual(normalizeRemoteUrl(left), normalizeRemoteUrl(right), `${left} and ${right} can name different repositories`);
  }
});

test("normalizeRemoteUrl preserves unknown SSH accounts, path roots and named homes", () => {
  for (const [left, right] of [
    ["alice@host.example:repo.git", "bob@host.example:repo.git"],
    ["ssh://alice@host.example/repo.git", "ssh://bob@host.example/repo.git"],
    ["host.example:repo.git", "alice@host.example:repo.git"],
    ["alice@host.example:repo.git", "ssh://alice@host.example/repo.git"],
    ["alice@host.example:repo.git", "alice@host.example:/repo.git"],
    ["alice@host.example:~alice/repo.git", "alice@host.example:~bob/repo.git"],
    ["ssh://alice@host.example/~alice/repo.git", "ssh://alice@host.example/~bob/repo.git"],
    ["ssh://alice@github.com:2222/Org/Main.git", "ssh://bob@github.com:2222/Org/Main.git"]
  ]) {
    assert.notEqual(normalizeRemoteUrl(left), normalizeRemoteUrl(right), `${left} and ${right} name distinct namespaces`);
  }
});

test("normalizeRemoteUrl preserves unknown repository suffixes and transport identities", () => {
  for (const [left, right] of [
    ["https://host.example/Org/Main.git", "https://host.example/Org/Main"],
    ["alice@host.example:Org/Main.git", "alice@host.example:Org/Main"],
    ["ssh://alice@host.example/Org/Main.git", "https://host.example/Org/Main.git"],
    ["git://host.example/Org/Main.git", "https://host.example/Org/Main.git"],
    ["ssh://git@github.com:2222/Org/Main.git", "ssh://git@github.com:2222/Org/Main"]
  ]) assert.notEqual(normalizeRemoteUrl(left), normalizeRemoteUrl(right), `${left} and ${right} must not fold`);
});

for (const url of ["alice@github.com:Org/Main.git", "ssh://alice@github.com:22/Org/Main.git"]) {
  test(`normalizeRemoteUrl keeps a non-git GitHub SSH account distinct: ${url}`, () => {
    assert.notEqual(normalizeRemoteUrl(url), normalizeRemoteUrl("https://github.com/Org/Main.git"),
      "GitHub's documented SSH alias uses the git account");
  });
}

test("unrecognized remote schemes retain distinct nonempty identities", () => {
  const known = normalizeRemoteUrl("https://example.invalid/Org/Main.git");
  const custom = normalizeRemoteUrl("softp://example.invalid/Org/Main.git");
  assert.ok(custom, "an unknown network URL must still veto shared-root fallback");
  assert.notEqual(custom, known);
  assert.notEqual(custom, normalizeRemoteUrl("other://example.invalid/Org/Main.git"));
  assert.equal(normalizeRemoteUrl("SoFtP://example.invalid/Org/Main.git"), custom);
  assert.notEqual(normalizeRemoteUrl("softp://github.com/Org/Main.git"),
    normalizeRemoteUrl("softp://github.com/Org/main.git"), "an unfamiliar transport gets no GitHub path assumptions");
  // Git permits an alphanumeric first character, followed by alphanumerics or +.-.
  for (const prefix of ["custom", "1custom", "x", "1", "x+helper.-2"]) {
    const uri = `${prefix}://example.invalid/Org/Main.git`;
    const helper = `${prefix}::host/org/repo`;
    assert.equal(isLocalPathRemote(uri), false, prefix + ": explicit URI stays network");
    assert.equal(isLocalPathRemote(helper), false, prefix + ": explicit helper stays network");
    assert.equal(normalizeRemoteUrl(uri), uri, "unknown URI identity stays complete and nonempty");
    assert.equal(normalizeRemoteUrl(helper), helper, "unknown helper identity stays complete and nonempty");
    assert.notEqual(normalizeRemoteUrl(helper), normalizeRemoteUrl(`ssh://${prefix}/:host/org/repo`),
      "a helper must not become an SCP host alias");
  }
  assert.equal(isLocalPathRemote("C:\\work\\repo"), process.platform === "win32", "an actual Windows drive path is local on Windows");
});

test("confirmed cwd disclosure is printable literal data without changing membership", () => {
  const cwd = "/tmp/clone[`plan`](fake)\n## INJECTED VERDICT: ADOPT\n| forged |\r\t\u001b[2J\u0085\u2028\u2029\u202e\u{e0001}";
  const rows = [row({ cwd })];
  const cloneRoots = [cwd];
  const summary = summarize({ rows, repoRoots: ["/tmp/main"], repoRootSource: "git", cloneRoots });
  const signal = find(summary, 4);
  assert.equal(signal.detail.length, 0, "the exact recorded cwd remains confirmed and inside");
  assert.equal(rows[0].cwd, cwd);
  assert.equal(cloneRoots[0], cwd);
  assert.doesNotMatch(signal.caveat, /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u);
  const literal = quotedLiterals(signal.caveat).find(value => JSON.parse(value) === cwd);
  assert.ok(literal, "literal disclosure must decode to the original path");
  assert.doesNotMatch(literal, /[`*_\[\]<>|&~#!]/u);
  assert.doesNotMatch(renderReport(summary), /^## INJECTED VERDICT/m);
});

test("remote locality and clone veto follow each native platform's Git grammar", () => {
  const main = path.resolve("platform-main"), candidate = path.resolve("platform-candidate");
  const drive = ["C:repo", "C:/repo", "C:\\repo"];
  const local = ["./C:repo", "dir/part:repo"];
  const network = ["foo\\bar:repo", "git@C:repo", "C::repo"];
  const script = [
    `import { isLocalPathRemote, normalizeRemoteUrl, withConfirmedCloneRoots } from ${JSON.stringify(new URL("./trial-report.mjs", import.meta.url).href)};`,
    `const main = ${JSON.stringify(main)}, candidate = ${JSON.stringify(candidate)}, remotes = ${JSON.stringify([...drive, ...local, ...network])};`,
    'const native = process.platform, results = [];',
    'for (const platform of ["linux", "darwin", "win32"]) {',
    '  Object.defineProperty(process, "platform", { value: platform });',
    '  try { for (const remote of remotes) {',
    '    const git = (args, cwd) => {',
    '      if (args[0] === "rev-parse") return cwd;',
    '      if (args[0] === "rev-list") return "shared-root";',
    '      if (args.length === 1 && args[0] === "remote") return "origin";',
    '      if (args[0] === "remote" && args[1] === "get-url") return cwd === main ? "https://example.invalid/org/main.git" : remote;',
    '      throw new Error("unexpected Git command");',
    '    };',
    '    results.push({ platform, remote, local: isLocalPathRemote(remote), confirmed: withConfirmedCloneRoots([candidate], [main], { git }).confirmed.has(candidate),',
    '      driveUriAlias: normalizeRemoteUrl("C:/repo") === normalizeRemoteUrl("ssh://c/repo") });',
    '  } } finally { Object.defineProperty(process, "platform", { value: native }); }',
    '}',
    'console.log(JSON.stringify(results));'
  ].join("\n");
  const child = spawnSync(process.execPath, ["--input-type=module", "--eval", script], {
    encoding: "utf8", timeout: 20_000, windowsHide: true, env: scrubGitEnv(process.env)
  });
  assert.equal(child.status, 0, child.stderr);
  for (const result of JSON.parse(child.stdout)) {
    const expected = local.includes(result.remote) || drive.includes(result.remote) && result.platform === "win32";
    assert.equal(result.local, expected, `${result.platform}: ${result.remote}`);
    assert.equal(result.confirmed, expected, "a nonmatching network remote vetoes shared history: " + result.platform + ": " + result.remote);
    assert.equal(result.driveUriAlias, result.platform !== "win32", "single-letter POSIX hosts normalize as SSH");
  }
});

test("native Git backslash host spelling cannot bypass a shared-root clone veto", () => {
  const { a, b, cleanup } = buildCloneFixture();
  try {
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), true);
    gitOutput(["remote", "add", "origin", "foo\\bar:repo"], b);
    assert.equal(gitOutput(["remote", "get-url", "origin"], b), "foo\\bar:repo");
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false);
  } finally { cleanup(); }
});

test("clone confirmation FAILS CLOSED: a cwd git cannot examine stays counted as outside", () => {
  // No fixture at all for `goneCwd` — every call the fake receives for it throws, standing in for
  // "the directory no longer exists", "git is not installed", or any other inconclusive failure.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const goneCwd = path.resolve("C:\\scratch\\deleted-worktree");
  const git = fakeGitByCwd({ [mainRoot]: { "rev-list --max-parents=0 HEAD": "aaa111" } });

  const { confirmed, probed } = withConfirmedCloneRoots([goneCwd], [mainRoot], { git });
  assert.equal(confirmed.size, 0, "an inconclusive check must not confirm a clone");
  assert.equal(probed, 1);

  const s4 = find(summarize({ rows: [row({ cwd: goneCwd })], repoRoots: [mainRoot], repoRootSource: "git", cloneRoots: [] }), 4);
  assert.equal(s4.detail.length, 1, "unconfirmed stays outside, exactly as before this check existed");
});

test("clone confirmation is bounded — exactly the first 50 distinct cwds, in the order given, are probed", () => {
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const many = Array.from({ length: 60 }, (_, i) => path.resolve(`C:\\scratch\\wt${i}`));
  // Every candidate is confirmable (no remote at all, and this repository's root commit), so each one
  // left unconfirmed was left unprobed, not rejected: the confirmed set IS the probed set. Without a
  // `remote` answer every candidate was inconclusive, and the old assertions held for any cap below 60.
  const table = { [mainRoot]: { "remote": "", "rev-list --max-parents=0 HEAD": "aaa111" } };
  for (const c of many) table[c] = { "remote": "", "rev-parse --show-toplevel": c, "rev-list --max-parents=0 HEAD": "aaa111" };

  const { confirmed, probed, capped } = withConfirmedCloneRoots(many, [mainRoot], { git: fakeGitByCwd(table) });
  // 50 is MAX_CLONE_PROBES, written out so a changed cap fails here instead of passing silently.
  assert.equal(probed, 50);
  assert.equal(capped, true);
  // The FIRST 50 of the list: main() hands the candidates over newest first (selectCloneCandidates),
  // so the cap drops the oldest.
  assert.deepEqual([...confirmed.keys()], many.slice(0, 50));
});

test("a candidate listing MORE than 16 remotes is left inconclusive before any of its urls is read — 16 are still read", () => {
  // MAX_CLONE_PROBES caps directories, not the remotes behind one: a single logged cwd can name a
  // repository with any number of remotes, at two `get-url` reads each. Past 16, the candidate fails
  // closed from its `git remote` listing alone.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  // Every remote names this repository, so without the bound each candidate would confirm.
  const withRemotes = (cwd, n) => {
    const t = {
      "rev-parse --show-toplevel": cwd, "rev-list --max-parents=0 HEAD": "aaa111",
      "remote": Array.from({ length: n }, (_, i) => `r${i}`).join("\n")
    };
    for (let i = 0; i < n; i++) t[`remote get-url r${i}`] = MAIN;
    return t;
  };
  const seventeen = path.resolve("C:\\scratch\\seventeen-remotes");
  const sixteen = path.resolve("C:\\scratch\\sixteen-remotes");
  const fake = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN },
    [seventeen]: withRemotes(seventeen, 17),
    [sixteen]: withRemotes(sixteen, 16)
  });
  const urlReads = new Map();
  const git = (args, cwd) => {
    if (args[0] === "remote" && args[1] === "get-url") urlReads.set(cwd, (urlReads.get(cwd) ?? 0) + 1);
    return fake(args, cwd);
  };
  const result = withConfirmedCloneRoots([seventeen, sixteen], [mainRoot], { git });
  assert.deepEqual([...result.confirmed.keys()], [sixteen], "17 remotes: inconclusive, so outside; 16: read and confirmed");
  assert.equal(urlReads.get(seventeen) ?? 0, 0, "not one url of the 17-remote candidate was read");
  assert.equal(urlReads.get(sixteen), 32, "16 remotes, two reads each");
  assert.equal(result.tooManyRemotes, 1);
  assert.equal(result.probed, 2, "both were probed; the 17-remote one ended at its listing");
  const s4 = find(summarize({
    rows: [row({ session: "many", cwd: seventeen })], repoRoots: [mainRoot], repoRootSource: "git",
    cloneRoots: [...result.confirmed.keys()],
    cloneProbe: { ...result, confirmed: undefined, candidates: 2 }
  }), 4);
  assert.equal(s4.detail.length, 1, "the 17-remote candidate counts as outside");
  assert.match(s4.caveat, /1 checked cwd\(s\) listed more than 16 remotes/);
});

test("a remote with more than 64 urls is inconclusive, including a contradicting 65th fetch or push URL", () => {
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const candidate = path.resolve("C:\\scratch\\many-urls");
  const limit = Array(64).fill(MAIN).join("\n");
  for (const [label, fetch, push, accepted] of [
    ["64 aggregate boundary", Array(32).fill(MAIN).join("\n"), Array(32).fill(MAIN).join("\n"), true],
    ["empty records cannot disappear from the aggregate bound", "\n" + Array(32).fill(MAIN).join("\r\n\n") + "\n", Array(32).fill(MAIN).join("\n"), false],
    ["LF-only empty records also consume the allowance", "\n" + Array(32).fill(MAIN).join("\n\n") + "\n", Array(32).fill(MAIN).join("\n"), false],
    ["65 matching aggregate entries", Array(32).fill(MAIN).join("\n"), Array(33).fill(MAIN).join("\n"), false],
    ["65 matching fetch URLs", limit + "\n" + MAIN, MAIN, false],
    ["65th fetch URL contradicts", limit + "\nhttps://github.com/fork/project.git", MAIN, false],
    ["65th push URL contradicts", MAIN, limit + "\nhttps://github.com/fork/project.git", false]
  ]) {
    const git = fakeGitByCwd({
      [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN },
      [candidate]: { "rev-parse --show-toplevel": candidate, "rev-list --max-parents=0 HEAD": "aaa111",
        "remote": "origin", "remote get-url --all origin": fetch, "remote get-url --push --all origin": push }
    });
    const result = withConfirmedCloneRoots([candidate], [mainRoot], { git });
    assert.equal(result.confirmed.has(candidate), accepted, label);
    if (!accepted) assert.equal(result.tooManyUrls, 1, label + ": disclose the rejected read");
  }
});

test("URL overflow in a later remote discards an earlier matching fingerprint", () => {
  const main = path.resolve("C:\\real\\main");
  const candidate = path.resolve("C:\\scratch\\clone");
  const url = "https://github.com/owner/project.git";
  const git = fakeGitByCwd({
    [main]: { remote: "origin", "rev-list --max-parents=0 HEAD": "shared", "remote get-url origin": url },
    [candidate]: { "rev-parse --show-toplevel": candidate, remote: "origin\nlater",
      "remote get-url origin": url, "remote get-url later": Array(65).fill(url).join("\n") }
  });
  const result = withConfirmedCloneRoots([candidate], [main], { git });
  assert.equal(result.confirmed.size, 0, "the earlier matching origin cannot bypass incomplete later evidence");
  assert.equal(result.tooManyUrls, 1);
});

test("the reference fingerprint also refuses more than 16 remote names before reading their URLs", () => {
  const main = path.resolve("C:\\real\\main");
  const local = path.resolve("C:\\scratch\\local-clone");
  const network = path.resolve("C:\\scratch\\network-clone");
  const url = "https://github.com/owner/project.git";
  for (const count of [16, 17]) {
    const root = { remote: Array.from({ length: count }, (_, i) => "r" + i).join("\n"), "rev-list --max-parents=0 HEAD": "shared" };
    for (let i = 0; i < count; i++) root["remote get-url r" + i] = url;
    const fake = fakeGitByCwd({ [main]: root,
      [local]: { "rev-parse --show-toplevel": local, remote: "", "rev-list --max-parents=0 HEAD": "shared" },
      [network]: { "rev-parse --show-toplevel": network, remote: "origin", "remote get-url origin": url }
    });
    let rootUrlReads = 0;
    const result = withConfirmedCloneRoots([local, network], [main], { git: (args, cwd) => {
      if (cwd === main && args[1] === "get-url") rootUrlReads++;
      return fake(args, cwd);
    } });
    assert.deepEqual([...result.confirmed.keys()], count === 16 ? [local, network] : [], "a capped reference contributes no partial fingerprint");
    assert.equal(rootUrlReads, count === 16 ? 16 : 0);
    assert.equal(result.limitedMainRoots, count === 16 ? 0 : 1);
    assert.equal(result.tooManyRemotes, 0, "reference limits have their own disclosed counter");
  }
});

test("oversized Git output is rejected before parsing on either reference or candidate reads", () => {
  const main = path.resolve("C:\\real\\main");
  const candidate = path.resolve("C:\\scratch\\clone");
  const oversized = "aaa111\n".repeat(150_000);
  assert.ok(Buffer.byteLength(oversized) > 1024 * 1024);
  for (const overflowAt of [main, candidate]) {
    const git = fakeGitByCwd({
      [main]: { remote: "", "rev-list --max-parents=0 HEAD": overflowAt === main ? oversized : "aaa111" },
      [candidate]: { "rev-parse --show-toplevel": candidate, remote: "", "rev-list --max-parents=0 HEAD": overflowAt === candidate ? oversized : "aaa111" }
    });
    const result = withConfirmedCloneRoots([candidate], [main], { git });
    assert.equal(result.confirmed.size, 0, "partial or oversized history cannot confirm a clone");
    assert.equal(result.limitedMainRoots, overflowAt === main ? 1 : 0);
    assert.equal(result.outputTooLarge, overflowAt === candidate ? 1 : 0);
  }
});

test("real Git URL overflow rejects the whole candidate and is disclosed on both CLI surfaces", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const url = "https://example.invalid/org/repo.git";
  const config = path.join(b, ".git", "config");
  const initial = fs.readFileSync(config, "utf8");
  try {
    const setUrls = urls => fs.writeFileSync(config, initial + '\n[remote "origin"]\n' + urls.map(value => "\turl = " + value + "\n").join(""));
    setUrls(Array(32).fill(url));
    assert.equal(gitOutput(["remote", "get-url", "--all", "origin"], b).split(/\r?\n/).length, 32);
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), true, "32 fetch plus 32 push URLs fill the aggregate remote allowance");
    setUrls([...Array(64).fill(url), "https://example.invalid/fork/repo.git"]);
    assert.equal(gitOutput(["remote", "get-url", "--all", "origin"], b).split(/\r?\n/).length, 65);
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, "a real 65th contradiction cannot be dropped");
    const log = path.join(make(), "usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ cwd: b })) + "\n");
    const child = runCliJson(log, a);
    assert.equal(child.status, 1, child.stderr + child.stdout);
    const summary = JSON.parse(child.stdout);
    assert.equal(summary.sources.cloneProbe.tooManyUrls, 1);
    assert.match(child.stderr, /64 URL entries per remote/);
    assert.match(find(summary, 4).caveat, /64 URL entries per remote/);
    assert.doesNotMatch(child.stderr, /folded into signal 4/);
  } finally { cleanup(); }
});

test("real Git enforces the 256 KiB child-output bound exactly", () => {
  const { a, cleanup } = buildCloneFixture();
  try {
    const config = path.join(a, ".git", "config");
    const initial = fs.readFileSync(config, "utf8");
    fs.writeFileSync(config, initial + "\n[diagnostic]\n\toutput = " + "x".repeat(256 * 1024 - 1) + "\n");
    assert.equal(gitOutput(["config", "--get", "diagnostic.output"], a).length, 256 * 1024 - 1,
      "the value plus Git's final newline exactly fills the allowed output buffer");
    fs.writeFileSync(config, initial + "\n[diagnostic]\n\toutput = " + "x".repeat(256 * 1024 + 1) + "\n");
    assert.throws(() => gitOutput(["config", "--get", "diagnostic.output"], a), error => error.code === "ENOBUFS");
  } finally { cleanup(); }
});

test("a remote beyond 64 urls cannot confirm from a matching prefix or shared history", () => {
  // A discarded suffix can name another project. The whole fingerprint must be refused,
  // rather than silently dropping the 65th URL and trusting the matching prefix.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const candidate = path.resolve("C:\\scratch\\many-urls");
  const manyUrls = [...Array.from({ length: 64 }, () => MAIN), "https://github.com/other/unrelated.git"].join("\n");
  const git = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN },
    [candidate]: {
      "rev-parse --show-toplevel": candidate,
      "rev-list --max-parents=0 HEAD": "aaa111",
      "remote": "origin",
      "remote get-url --all origin": manyUrls,
      "remote get-url --push --all origin": manyUrls
    }
  });
  const { confirmed } = withConfirmedCloneRoots([candidate], [mainRoot], { git });
  assert.deepEqual([...confirmed.keys()], [], "neither the matching prefix nor shared history can confirm an incomplete fingerprint");
});

test("the probe pass has a 60 s budget — once it is spent the pass stops, the rest stay outside, and the caveat says so", () => {
  // A cwd on a hung mount makes each git call wait out GIT_TIMEOUT_MS, and fifty such cwds block the
  // report for minutes before a single signal is computed. The clock is injected, so the deadline
  // is exercised without waiting for it.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const MAIN = "https://github.com/joel/kanbanflow-v2.git";
  const cands = Array.from({ length: 10 }, (_, i) => path.resolve(`C:\\scratch\\slow${i}`));
  const table = { [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": MAIN } };
  for (const c of cands) table[c] = { "remote": "origin", "rev-parse --show-toplevel": c, "remote get-url origin": MAIN };
  // Main's fingerprint takes 3 calls and each candidate 4 (all confirm on path (a)). The clock stands
  // still for the first `after` calls — main and four candidates, by default — then reads `later`.
  const run = (later, after = 3 + 4 * 4) => {
    const fake = fakeGitByCwd(table);
    let calls = 0;
    const git = (args, cwd) => { calls++; return fake(args, cwd); };
    const now = () => (calls >= after ? later : 0);
    return { ...withConfirmedCloneRoots(cands, [mainRoot], { git, now }), calls };
  };
  const spent = run(60_001);
  assert.equal(spent.budgetSpent, true);
  assert.equal(spent.probed, 4);
  assert.deepEqual([...spent.confirmed.keys()], cands.slice(0, 4), "what was probed before the deadline still counts");
  assert.equal(spent.capped, true);
  assert.equal(spent.calls, 3 + 4 * 4, "no git call was made once the budget was spent");
  // Exactly on the deadline is still inside it, so the budget is 60 s, not less.
  const onTime = run(60_000);
  assert.equal(onTime.budgetSpent, false);
  assert.equal(onTime.probed, 10);
  // Part-way through a probe: the deadline passes after candidate 5's second call. The budget is
  // checked at every call, not only between candidates, so its third call is refused; the refusal
  // leaves candidate 5 inconclusive, so it confirms nothing, counts as unprobed, and no git call
  // follows it.
  const cut = run(60_001, 3 + 4 * 4 + 2);
  assert.equal(cut.calls, 3 + 4 * 4 + 2, "no call after the deadline, not even the rest of the probe it fell in");
  assert.equal(cut.probed, 4);
  assert.deepEqual([...cut.confirmed.keys()], cands.slice(0, 4), "the candidate cut short confirmed nothing");
  const s4 = find(summarize({
    rows: [row({ session: "late", cwd: cands[9] })], repoRoots: [mainRoot], repoRootSource: "git",
    cloneRoots: [...spent.confirmed.keys()],
    cloneProbe: { ...spent, confirmed: undefined, candidates: cands.length }
  }), 4);
  assert.match(s4.caveat, /CLONE CONFIRMATION CAPPED THIS RUN \(its 60 s time budget ran out\)/);
  assert.match(s4.caveat, /capped: 6 candidate\(s\) unprobed/);
  assert.equal(s4.detail.length, 1, "an unprobed candidate stays outside");
});

test("clone confirmation is skipped entirely with no roots — nothing to compare a clone against", () => {
  const { confirmed, probed, capped } = withConfirmedCloneRoots(["C:\\anything"], [], { git: () => { throw new Error("must not be called"); } });
  assert.equal(confirmed.size, 0);
  assert.equal(probed, 0);
  assert.equal(capped, false);
});

test("signal 4's git-sourced caveat always discloses the clone-confirmation limitation, even when none were found", () => {
  // The gap is standing, not conditional on this run having found one: a clone whose directory is
  // gone, whose git is unavailable, or that is shallow with a rewritten origin cannot be confirmed
  // and still reads as outside, so the reader must be told every run, not only the runs that hit it.
  const s4 = find(summarize({
    rows: [row()], repoRoots: [path.resolve("C:\\repo")], repoRootSource: "git", cloneRoots: []
  }), 4);
  assert.match(s4.caveat, /clone/i);
  assert.doesNotMatch(s4.caveat, /confirmed as/i, "nothing was confirmed this run, so it must not claim otherwise");
  // A no-remote clone DOES confirm (via root commit) — see withConfirmedCloneRoots — so listing "no
  // remote configured" as a reason confirmation could FAIL was false. The real remaining gaps named
  // instead: a call can exceed GIT_TIMEOUT_MS, and the probe cap can leave a stale candidate unprobed.
  assert.doesNotMatch(s4.caveat, /no remote configured/i);
  assert.match(s4.caveat, /GIT_TIMEOUT_MS/);
  assert.match(s4.caveat, /cap/i);
  // Round-3, rule (A): the residual this file's own veto rule accepts on purpose — a genuinely
  // same-repo clone reached via an alias, an IP literal, or another host-spelling this check
  // cannot resolve to a matching hostname — must be named, not silently assumed away.
  assert.match(s4.caveat, /alias/i);
  assert.match(s4.caveat, /ip literal|IP\b/);
});

test("signal 4's caveat and sources.cloneProbeCapped disclose a clone-probe cap hit, with counts", () => {
  const s = summarize({
    rows: [row()], repoRoots: [path.resolve("C:\\repo")], repoRootSource: "git",
    cloneRoots: [], cloneProbe: { probed: 50, capped: true, candidates: 73 }
  });
  assert.equal(s.sources.cloneProbeCapped, true);
  const s4 = find(s, 4);
  assert.match(s4.caveat, /CAPPED THIS RUN/);
  assert.match(s4.caveat, /50 of 73/);
});

test("sources.cloneProbeCapped is false, with no cap sentence, when nothing was capped (or nothing probed at all)", () => {
  const noProbe = summarize({ rows: [row()], repoRoots: [path.resolve("C:\\repo")], repoRootSource: "git" });
  assert.equal(noProbe.sources.cloneProbeCapped, false, "cloneProbe defaults to null: no probing ran, so nothing is capped");
  assert.doesNotMatch(find(noProbe, 4).caveat, /CAPPED THIS RUN/);

  const notCapped = summarize({
    rows: [row()], repoRoots: [path.resolve("C:\\repo")], repoRootSource: "git",
    cloneProbe: { probed: 3, capped: false, candidates: 3 }
  });
  assert.equal(notCapped.sources.cloneProbeCapped, false);
  assert.doesNotMatch(find(notCapped, 4).caveat, /CAPPED THIS RUN/);
});

test("an explicit --repo-root override gets the SAME clone-confirmation disclosure as a git-resolved root", () => {
  // main() runs the clone check whenever repoRootSource !== "fallback", which includes "override"
  // — an operator who named the root by hand still deserves to know a cwd was reclassified.
  const cwd = path.resolve("C:\\scratch\\confirmed-clone");
  const s4 = find(summarize({
    rows: [row({ cwd })], repoRoots: [path.resolve("C:\\repo")], repoRootSource: "override", cloneRoots: [cwd]
  }), 4);
  assert.equal(s4.detail.length, 0, "the confirmed clone must not count as outside under override sourcing either");
  assert.match(s4.caveat, /confirmed as separate clones/i);
  assert.ok(quotedValues(s4.caveat).includes(cwd));
});

// A shared fixture of four distinct REAL repositories, each confirmable (or not) by a DIFFERENT
// one of withConfirmedCloneRoots's checks — used by both the unit-level real-git test below and
// the CLI-level integration test in section 5e. Replaces an earlier real-git test that compared
// this worktree with itself: same config, same object store, so it could not have caught a broken
// fingerprint (measured: a broken rev-list alone still passed it).
//
//   a — the main root. Its own origin is a fake network URL (remote add/get-url are local metadata
//       only; nothing here ever makes a network call).
//   b — `git clone a b`, then `remote remove origin`: confirmable ONLY by root commit — no remote
//       at all, this review fleet's own `SCRATCH/kf` pattern once cloned and re-pointed.
//   c — its OWN unrelated init/commit, its OWN different network origin: confirmable by NEITHER
//       check. A genuinely different project.
//   d — its OWN unrelated init/commit (root commit does NOT match a's), but its origin is set to
//       a's OWN origin URL: confirmable ONLY by remote.
function buildCloneFixture() {
  return buildInto(make => {
    const a = make(); const b = make(); const c = make(); const d = make();
    initRealRepo(a, "a.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], a);

    gitOutput(["clone", "-q", a, b], a);
    gitOutput(["remote", "remove", "origin"], b);

    initRealRepo(c, "c.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/other/thing.git"], c);

    initRealRepo(d, "d.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], d);

    return { a, b, c, d };
  });
}

test("withConfirmedCloneRoots really can drive git — root-commit-only, remote-only, and a real rejection", () => {
  const { a, b, c, d, cleanup } = buildCloneFixture();
  try {
    const { confirmed, probed } = withConfirmedCloneRoots([b, c, d], [a]);
    assert.equal(probed, 3);
    assert.equal(real(confirmed.get(b)), real(b), "b: no remote at all, confirmed by shared root commit alone");
    assert.equal(real(confirmed.get(d)), real(d), "d: different root commit, confirmed by its matching remote alone");
    assert.equal(confirmed.has(c), false, "c: different root commit AND different remote — genuinely a different project");
    assert.equal(confirmed.size, 2);
  } finally {
    cleanup();
  }
});

test("real Git clone evidence does not collapse differently cased network repository paths", () => {
  const { a, b, d, cleanup } = buildCloneFixture();
  try {
    gitOutput(["remote", "set-url", "origin", "git://example.invalid/Org/Main.git"], a);
    gitOutput(["remote", "add", "origin", "git://example.invalid/Org/main.git"], b);
    gitOutput(["remote", "set-url", "origin", "git://EXAMPLE.invalid/Org/Main.git"], d);

    const { confirmed } = withConfirmedCloneRoots([b, d], [a]);

    assert.equal(confirmed.has(b), false, "a different network path contradicts even shared root history");
    assert.equal(confirmed.has(d), true, "host casing alone does not hide a matching remote");
  } finally {
    cleanup();
  }
});

test("real Git URL payload whitespace cannot confirm unrelated or shared-history candidates", () => {
  const { a, b, c, d, cleanup } = buildCloneFixture();
  try {
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], b);
    for (const [url, different] of [
      ["https://example.invalid/org/repo.git", "https://example.invalid/org/repo.git "],
      ["https://github.com/Org/Repo.git", "https://github.com/Org/Repo.git\t"],
      ["ssh://alice@host/repo.git", " ssh://alice@host/repo.git"]
    ]) {
      gitOutput(["remote", "set-url", "origin", url], a);
      for (const cwd of [b, d]) {
        gitOutput(["config", "remote.origin.url", different], cwd);
        assert.equal(gitOutput(["config", "--null", "--get-all", "remote.origin.url"], cwd), different + "\0",
          "NUL-framed native config proves the exact configured payload");
      }
      gitOutput(["remote", "set-url", "origin", url], c);
      const { confirmed } = withConfirmedCloneRoots([b, c, d], [a]);
      assert.equal(confirmed.has(b), false, "meaningful URL bytes veto shared-root fallback");
      assert.equal(confirmed.has(d), false, "trimming must not manufacture a match for unrelated history");
      assert.equal(confirmed.has(c), true, "the exact supported URL still confirms an independent repository");
      assert.equal(gitOutput(["remote", "get-url", "origin"], d), different,
        "the Git reader removes only its own record terminator");
    }
    const exactPayload = "https://example.invalid/org/repo.git ";
    for (const cwd of [a, c]) gitOutput(["config", "remote.origin.url", exactPayload], cwd);
    assert.equal(withConfirmedCloneRoots([c], [a]).confirmed.has(c), true,
      "identical whitespace-bearing values remain comparable without inventing an alias");
  } finally { cleanup(); }
});

test("real Git multiline URL framing cannot forge candidate or reference clone evidence", () => {
  const { a, b, c, d, cleanup } = buildCloneFixture();
  const url = "https://example.invalid/org/repo.git";
  try {
    gitOutput(["remote", "add", "origin", url], b);
    gitOutput(["config", "remote.origin.url", url + "\n" + url], d);
    assert.equal(gitOutput(["config", "--null", "--get-all", "remote.origin.url"], d), url + "\n" + url + "\0",
      "the two printed lines are one actual config value");
    const ordinary = withConfirmedCloneRoots([d], [a]);
    assert.equal(ordinary.confirmed.has(d), false, "one multiline URL is not two matching remote records");
    gitOutput(["config", "remote.origin.url", url], d);
    for (const [key, value] of [["remote.origin.pushurl", url + "\n" + url],
      ["url." + url + ".pushInsteadOf", "unused:\r\n"]]) {
      gitOutput(["config", key, value], d);
      assert.equal(withConfirmedCloneRoots([d], [a]).confirmed.has(d), false, key + " must not bypass native framing validation");
      gitOutput(["config", "--unset", key], d);
    }
    gitOutput(["config", "remote.origin.url", url + "\n" + url], b);
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, "ambiguous framing also vetoes shared history");

    gitOutput(["config", "remote.origin.url", url], d);
    gitOutput(["remote", "remove", "origin"], b);
    for (const ambiguous of [url + "\n" + url, url + "\n", url + "\r\n"]) {
      gitOutput(["config", "remote.origin.url", ambiguous], a);
      assert.equal(withConfirmedCloneRoots([b, d], [a]).confirmed.size, 0,
        "an ambiguously framed reference contributes neither URL nor root-history proof");
    }
    gitOutput(["config", "remote.origin.url", url], a);
    gitOutput(["config", "remote.origin.url", ""], b);
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, "an empty configured URL cannot fall back to shared history as absent evidence");
    gitOutput(["config", "remote.origin.url", "short:repo"], c);
    gitOutput(["config", "url." + url + ".insteadOf", "short:repo"], c);
    assert.equal(withConfirmedCloneRoots([c], [a]).confirmed.has(c), true, "ordinary repository-local rewriting remains native Git behavior");
    gitOutput(["config", "url." + url + ".insteadOf", "short:repo\n"], c);
    gitOutput(["config", "remote.origin.url", url], c);
    assert.equal(withConfirmedCloneRoots([c], [a]).confirmed.has(c), false, "rewrite controls with line breaks cannot authorize ambiguous framing");
  } finally { cleanup(); }
});

test("real Git malformed UTF8 URL bytes cannot alias a valid SSH path or reference history", () => {
  const { a, b, c, d, cleanup } = buildCloneFixture();
  try {
    const bases = new Map([a, b, c, d].map(cwd => {
      const bytes = fs.readFileSync(path.join(cwd, ".git", "config"));
      const remote = bytes.indexOf(Buffer.from('[remote "origin"]'));
      return [cwd, remote === -1 ? bytes : bytes.subarray(0, remote)];
    }));
    const writeUrlBytes = (cwd, bytes) => fs.writeFileSync(path.join(cwd, ".git", "config"),
      Buffer.concat([bases.get(cwd), Buffer.from('[remote "origin"]\n\turl = "'), bytes, Buffer.from('"\n')]));
    const prefix = Buffer.from("ssh://alice@host/repo-"), suffix = Buffer.from(".git");
    const valid = Buffer.concat([prefix, Buffer.from("\ufffd"), suffix]);
    const invalid = Buffer.concat([prefix, Buffer.from([0xff]), suffix]);
    for (const cwd of [a, c]) writeUrlBytes(cwd, valid);
    for (const cwd of [b, d]) writeUrlBytes(cwd, invalid);
    const raw = cwd => {
      const child = spawnSync("git", ["remote", "get-url", "origin"], {
        cwd, encoding: null, windowsHide: true, timeout: 5000,
        env: { ...scrubGitEnv(process.env), GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_SYSTEM: "/dev/null", GIT_CONFIG_GLOBAL: "/dev/null" }
      });
      assert.equal(child.status, 0, child.stderr?.toString());
      return child.stdout;
    };
    const expected = raw(a), different = raw(d);
    assert.notDeepEqual(different, expected, "native Git preserves the different SSH pathname bytes");
    assert.equal(different.toString("utf8"), expected.toString("utf8"), "permissive decoding is the concrete collision being prevented");
    const { confirmed } = withConfirmedCloneRoots([b, c, d], [a]);
    assert.equal(confirmed.has(b), false, "invalid URL bytes veto shared-root fallback");
    assert.equal(confirmed.has(d), false, "replacement decoding cannot manufacture an unrelated repository match");
    assert.equal(confirmed.has(c), true, "a valid U+FFFD pathname remains supported");
    assert.throws(() => gitOutput(["remote", "get-url", "origin"], d), error => error.code === "git_url_framing");
    writeUrlBytes(a, invalid);
    fs.writeFileSync(path.join(b, ".git", "config"), bases.get(b));
    assert.equal(withConfirmedCloneRoots([b, c], [a]).confirmed.size, 0, "an invalid reference also loses its shared-root proof");

    // Git also reads its legacy line-framed remote files, outside modern config metadata.
    // This reaches the URL output decoder itself with genuine native invalid bytes.
    for (const [name, url] of [["invalid", invalid], ["bom", Buffer.concat([Buffer.from("\ufeff"), valid])]]) {
      const file = path.join(b, ".git", "remotes", name);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, Buffer.concat([Buffer.from("URL: "), url, Buffer.from("\n")]));
    }
    assert.throws(() => gitOutput(["remote", "get-url", "invalid"], b), error => error.code === "git_url_framing");
    assert.equal(gitOutput(["remote", "get-url", "bom"], b), "\ufeff" + valid.toString("utf8"),
      "valid leading BOM bytes are payload, not an encoding marker to discard");
  } finally { cleanup(); }
});

test("real Git evidence keeps an unfamiliar transport outside despite shared history", () => {
  const { a, b, d, cleanup } = buildCloneFixture();
  try {
    gitOutput(["remote", "add", "origin", "softp://example.invalid/org/repo.git"], b);
    for (const [custom, known] of [
      ["softp://example.invalid/org/repo.git", "https://example.invalid/org/repo.git"],
      ["softp::host/org/repo", "ssh://softp/:host/org/repo"],
      ["x::host/org/repo", "ssh://x/:host/org/repo"],
      ["1x://host/org/repo", "ssh://1x///host/org/repo"],
      ["1::host/org/repo", "ssh://1/:host/org/repo"]
    ]) {
      gitOutput(["remote", "set-url", "origin", known], a);
      gitOutput(["remote", "set-url", "origin", custom], b);
      gitOutput(["remote", "set-url", "origin", known], d);
      const { confirmed } = withConfirmedCloneRoots([b, d], [a]);
      assert.equal(confirmed.has(b), false, "unknown transport must contradict the known Git identity");
      assert.equal(confirmed.has(d), true, "the matching known transport still confirms an unrelated root");
    }
  } finally {
    cleanup();
  }
});

test("real Git SSH identity rejects account and path collisions with independent and shared history", () => {
  const { a, b, c, d, cleanup } = buildCloneFixture();
  try {
    gitOutput(["remote", "add", "origin", "alice@host:repo.git"], b);
    for (const [main, different, alias] of [
      ["alice@host:repo.git", "bob@host:repo.git", "alice@HOST:repo.git"],
      ["alice@host:/repo", "bob@host:/repo", "ssh://alice@HOST/repo"],
      ["alice@host:repo", "alice@host:/repo", "alice@HOST:repo"],
      ["alice@host:repo", "alice@host:repo.git", "alice@HOST:repo"],
      ["alice@host:~/a%2Fb", "ssh://alice@host/~/a%2Fb", "ssh://alice@HOST/~/a%252Fb"],
      ["git@github.com:Org/Repo.git", "bob@github.com:Org/Repo.git", "https://github.com/org/repo"]
    ]) {
      for (const [cwd, remote] of [[a, main], [b, different], [c, alias], [d, different]])
        gitOutput(["remote", "set-url", "origin", remote], cwd);
      const { confirmed } = withConfirmedCloneRoots([b, c, d], [a]);
      assert.equal(confirmed.has(b), false, "a contradicting SSH namespace vetoes shared-root fallback");
      assert.equal(confirmed.has(d), false, "an unrelated history cannot confirm through an account/path collision");
      assert.equal(confirmed.has(c), true, "the supported alias still confirms a different root through its remote");
    }
  } finally {
    cleanup();
  }
});

test("Git diagnostics distinguish relative absolute and tilde SSH paths without connecting", () => {
  const fixture = buildInto(make => {
    const a = make();
    initRealRepo(a, "ssh-diagnostic.txt");
    return { a };
  });
  try {
    for (const [remote, effective] of [
      ["alice@host:repo", "repo"], ["alice@host:/repo", "/repo"],
      ["ssh://alice@host/repo", "/repo"], ["alice@host:~/repo", "~/repo"],
      ["alice@host:/~/repo", "~/repo"], ["ssh://alice@host/~/repo", "~/repo"],
      ["ssh://alice@host/~bob/repo", "~bob/repo"],
      ["alice@host:~/a%2Fb", "~/a%2Fb"], ["ssh://alice@host/~/a%2Fb", "~/a/b"]
    ]) {
      const diagnostic = gitOutput(["fetch-pack", "--diag-url", remote], fixture.a);
      assert.match(diagnostic, /^Diag: protocol=ssh$/m);
      assert.equal(diagnostic.split(/\r?\n/).find(line => line.startsWith("Diag: path=")), "Diag: path=" + effective);
    }
  } finally {
    fixture.cleanup();
  }
});

test("real Git namespace evidence retains SSH accounts and repository path semantics", async t => {
  const fixtureParent = path.resolve(HERE, "../_sandbox");
  fs.mkdirSync(fixtureParent, { recursive: true });
  const fixture = fs.mkdtempSync(path.join(fixtureParent, "564-ssh-namespace-"));
  const main = path.join(fixture, "main");
  const unrelated = path.join(fixture, "unrelated");
  const sameRemote = path.join(fixture, "same-remote");
  const sharedHistory = path.join(fixture, "shared-history");
  try {
    for (const [directory, content] of [[main, "main.txt"], [unrelated, "unrelated.txt"], [sameRemote, "same.txt"]]) {
      fs.mkdirSync(directory);
      initRealRepo(directory, content);
      gitOutput(["remote", "add", "origin", "alice@host.example:repo.git"], directory);
    }
    gitOutput(["clone", "-q", main, sharedHistory], main);
    const root = directory => gitOutput(["rev-list", "--max-parents=0", "HEAD"], directory).trim();
    assert.notEqual(root(main), root(unrelated), "the rejected control has unrelated history");
    assert.notEqual(root(main), root(sameRemote), "the confirmed control relies on its URL alone");
    assert.equal(root(main), root(sharedHistory), "the veto control really shares history");

    for (const [label, mainUrl, otherUrl, controlUrl = mainUrl] of [
      ["SCP account", "alice@host.example:repo.git", "bob@host.example:repo.git"],
      ["absolute SSH account", "ssh://alice@host.example/repo.git", "ssh://bob@host.example/repo.git"],
      ["home-relative SCP versus absolute SSH", "alice@host.example:repo.git", "ssh://alice@host.example/repo.git"],
      ["home-relative versus absolute SCP", "alice@host.example:repo.git", "alice@host.example:/repo.git"],
      ["explicit named home", "alice@host.example:~alice/repo.git", "alice@host.example:~bob/repo.git"],
      ["significant .git suffix", "https://host.example/Org/Main.git", "https://host.example/Org/Main"],
      ["path case", "ssh://alice@host.example/Org/Main.git", "ssh://alice@host.example/Org/main.git"],
      ["unknown cross-transport alias", "ssh://alice@host.example/Org/Main.git", "https://host.example/Org/Main.git"],
      ["nondefault GitHub SSH account", "ssh://alice@github.com:2222/Org/Main.git", "ssh://bob@github.com:2222/Org/Main.git"],
      ["documented GitHub aliases", "https://github.com/Org/Main.git", "git@github.com:Other/Main.git", "ssh://git@GITHUB.COM:22/org/main/"]
    ]) await t.test(label, () => {
      for (const [directory, url] of [[main, mainUrl], [unrelated, otherUrl], [sharedHistory, otherUrl], [sameRemote, controlUrl]]) {
        gitOutput(["remote", "set-url", "origin", url], directory);
        assert.equal(gitOutput(["remote", "get-url", "origin"], directory).trim(), url, "Git retains the actual configured namespace");
      }
      const { confirmed } = withConfirmedCloneRoots([unrelated, sharedHistory, sameRemote], [main]);
      assert.equal(confirmed.has(unrelated), false, "a different namespace cannot confirm an unrelated root");
      assert.equal(confirmed.has(sharedHistory), false, "a different namespace also vetoes shared history");
      assert.equal(real(confirmed.get(sameRemote)), real(sameRemote), "the same repository URL still confirms an unrelated root");
      assert.equal(confirmed.size, 1);
    });
  } finally {
    assert.equal(path.dirname(fixture), fixtureParent, "cleanup stays within the owning ignored fixture directory");
    fs.rmSync(fixture, { recursive: true, force: true });
  }
});

// --- 5c. Real git: a leaked GIT_DIR must not contaminate ANY answer ------------------------
//
// A pre-commit hook (or any git-invoking tool) commonly exports GIT_DIR/GIT_WORK_TREE/etc for its
// OWN children. This process inherits them like any other child process unless gitOutput scrubs
// them. Measured for real on this machine (git 2.55.0.windows.3): with GIT_DIR pointed at repo A,
// `git -C B rev-list --max-parents=0 HEAD` returns A's root commit, not B's — so B, a genuinely
// unrelated repo, would misread as sharing A's history. For signal 4 that is the FORBIDDEN
// direction: every candidate would "confirm" as a clone and REVERT on genuine outside use.
//
// These tests use REAL git via the imported `gitOutput`, not the fake — its own environment
// handling is exactly what a fake cannot prove. Contamination is injected via an EXPLICIT `env`
// object handed to `gitOutput`/`withConfirmedCloneRoots`/`resolveRepoRoots` — NEVER by mutating
// `process.env` itself. (Measured directly, Node v24.20.0: this file's own tests actually run
// SEQUENTIALLY, not concurrently — that is not the reason. The real reason is that several tests
// spawn the real CLI with `...process.env` spread into the child, so a global mutation here would
// leak into every later test in the same process for the rest of this run, however this file is
// eventually run, and fail them for a reason nobody would trace back to this section.) Fixture
// setup itself goes through `gitOutput` too, so it is protected by the SAME scrub being proven,
// not by a second, separately-written one that could drift from the real one.

function initRealRepo(dir, filename, git = gitOutput) {
  fs.writeFileSync(path.join(dir, filename), filename);
  git(["init", "-q"], dir);
  git(["add", filename], dir);
  // -c commit.gpgsign=false / core.hooksPath= / --no-verify: a contributor's global git config can
  // have commit signing or a hooksPath configured (this repo's own CLAUDE.md documents exactly
  // that risk for git hooks). Any of those can prompt, shell out, or simply run slowly on an
  // unrelated temp repo — inside GIT_TIMEOUT_MS (5s), that reads as "git is unavailable" and turns
  // an unrelated local config difference into a flaky test.
  git(
    [
      "-c", "user.email=trial-report-test@example.com", "-c", "user.name=trial-report-test",
      "-c", "commit.gpgsign=false", "-c", "core.hooksPath=",
      "commit", "-q", "--no-verify", "-m", "init"
    ],
    dir
  );
}

const leakedGitDirEnv = repoDir => ({
  ...process.env,
  GIT_DIR: path.join(repoDir, ".git"),
  GIT_INDEX_FILE: path.join(repoDir, ".git", "index")
});

test("gitOutput scrubs a leaked GIT_DIR/GIT_INDEX_FILE — the answer is about cwd, not the leaked repo", () => {
  const a = tmp(); const b = tmp();
  try {
    initRealRepo(a, "a.txt");
    initRealRepo(b, "b.txt");
    const bRootClean = gitOutput(["rev-list", "--max-parents=0", "HEAD"], b);
    assert.notEqual(gitOutput(["rev-list", "--max-parents=0", "HEAD"], a), bRootClean,
      "fixture sanity: a and b must have unrelated histories for this test to mean anything");

    const bRootContaminated = gitOutput(["rev-list", "--max-parents=0", "HEAD"], b, { env: leakedGitDirEnv(a) });
    assert.equal(bRootContaminated, bRootClean,
      "a leaked GIT_DIR must be scrubbed before asking about b, not silently answer with a's history");

    const bToplevel = gitOutput(["rev-parse", "--show-toplevel"], b, { env: leakedGitDirEnv(a) });
    assert.equal(real(bToplevel), real(b));
  } finally {
    fs.rmSync(a, { recursive: true, force: true });
    fs.rmSync(b, { recursive: true, force: true });
  }
});

test("withConfirmedCloneRoots is not fooled by a leaked GIT_DIR — the forbidden direction", () => {
  // The actual danger for signal 4, not just gitOutput in isolation: under contamination, a
  // GENUINELY UNRELATED directory must not "confirm" as a clone of the main root just because
  // every git call silently answered from the leaked repo instead of the one actually asked about.
  const a = tmp(); const b = tmp();
  try {
    initRealRepo(a, "a.txt");
    initRealRepo(b, "b.txt");
    const leakingGit = (args, cwd) => gitOutput(args, cwd, { env: leakedGitDirEnv(a) });
    const { confirmed } = withConfirmedCloneRoots([b], [a], { git: leakingGit });
    assert.equal(confirmed.size, 0,
      "b is a genuinely different, unrelated repo; a leaked GIT_DIR must not make it read as a's clone");
  } finally {
    fs.rmSync(a, { recursive: true, force: true });
    fs.rmSync(b, { recursive: true, force: true });
  }
});

test("resolveRepoRoots is not fooled by a leaked GIT_DIR either — same scrub, shared by both callers", () => {
  const a = tmp(); const hook = tmp();
  try {
    initRealRepo(a, "a.txt");
    initRealRepo(hook, "hook.txt");
    const leakingGit = (args, cwd) => gitOutput(args, cwd, { env: leakedGitDirEnv(hook) });
    const { roots, source } = resolveRepoRoots(null, { cwd: a, git: leakingGit });
    assert.equal(source, "git");
    // Normalized, then de-duplicated: through a link, --show-toplevel (physical) and the parent of
    // --git-common-dir (resolved against the cwd as given) are two spellings of one directory.
    assert.deepEqual([...new Set(roots.map(real))], [real(a)], "roots must reflect a, not the leaked hook repo");
  } finally {
    fs.rmSync(a, { recursive: true, force: true });
    fs.rmSync(hook, { recursive: true, force: true });
  }
});

test("global config rewrites cannot confirm an unrelated repo, while local rewrites still apply", () => {
  const { a, c, make, cleanup } = buildCloneFixture();
  const mainUrl = "https://example.invalid/org/repo.git";
  const otherUrl = "https://example.invalid/other/thing.git";
  const rewriteKey = "url." + mainUrl + ".insteadOf";
  try {
    for (const location of ["HOME", "XDG_CONFIG_HOME"]) {
      const home = make();
      const xdg = make();
      fs.mkdirSync(path.join(xdg, "git"));
      const config = location === "HOME" ? path.join(home, ".gitconfig") : path.join(xdg, "git", "config");
      fs.writeFileSync(config, '[url "' + mainUrl + '"]\n\tinsteadOf = ' + otherUrl + "\n");
      // Only this child's HOME/XDG paths change. The fixture uses the ordinary global config
      // locations, rather than GIT_CONFIG_GLOBAL, which the existing prefix scrub already removes.
      const env = scrubGitEnv({ ...process.env, HOME: home, USERPROFILE: home, XDG_CONFIG_HOME: xdg });
      const raw = spawnSync("git", ["remote", "get-url", "origin"], {
        cwd: c, env, encoding: "utf8", timeout: 5_000, windowsHide: true
      });
      assert.equal(raw.status, 0, raw.stderr);
      assert.equal(raw.stdout.trim(), mainUrl, location + ": fixture must exercise a real global rewrite");

      const git = (args, cwd) => gitOutput(args, cwd, { env });
      const { confirmed } = withConfirmedCloneRoots([c], [a], { git });
      assert.equal(confirmed.has(c), false, location + ": a global rewrite must not disguise a different repository");
      const summary = summarize({
        rows: [row({ session: "genuinely-external", cwd: c })], repoRoots: [a], repoRootSource: "git",
        cloneRoots: [...confirmed.keys()]
      });
      assert.deepEqual(find(summary, 4).detail.map(detail => detail.session), ["genuinely-external"]);

      // Only protected ownership exceptions may accompany local identity configuration.
      const settings = git(["config", "--show-scope", "--list"], c)
        .split(/\r?\n/).filter(Boolean).map(line => line.split("\t"));
      assert.ok(settings.every(([scope, setting]) => scope === "local"
        || scope === "command" && setting.startsWith("safe.directory=")),
      "global/system identity settings must not reach a probe");

      gitOutput(["config", "--local", rewriteKey, otherUrl], c);
      assert.equal(withConfirmedCloneRoots([c], [a], { git }).confirmed.has(c), true,
        "the same rewrite remains effective when explicitly configured in the candidate repository");
      gitOutput(["config", "--local", "--unset", rewriteKey], c);
    }
  } finally {
    cleanup();
  }
});

test("an inherited GIT_* variable that would confirm a non-clone or hide a clone leaves the clone check's answer as a clean environment gives it", () => {
  // Each row injects, through the `env` option, one inherited GIT_* variable measured to move this
  // answer, and compares the result with the answer a clean environment gives. Reaching git, the
  // first six would CONFIRM a repository that is not a clone of a; the last four would HIDE one
  // that is. GIT_CONFIG_GLOBAL, GIT_CONFIG_SYSTEM, GIT_CEILING_DIRECTORIES and GIT_REDIRECT_STDOUT
  // are not on git's own `--local-env-vars` list (as of 2.55). GIT_REDIRECT_STDOUT is a
  // Windows-only variable; elsewhere its row gives the clean answer whether or not it is scrubbed.
  const commitFile = (dir, file) => {
    fs.writeFileSync(path.join(dir, file), file);
    gitOutput(["add", file], dir);
    gitOutput(["-c", "user.email=trial-report-test@example.com", "-c", "user.name=trial-report-test",
      "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-q", "--no-verify", "-m", file], dir);
  };
  const fx = buildInto(make => {
    const a = make();
    initRealRepo(a, "a.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], a);
    const A0 = gitOutput(["rev-list", "--max-parents=0", "HEAD"], a);
    // A clone of a (so a's commits are in its object store) checked out on an ORPHAN branch with a
    // root of its own: unrelated history, a local-path remote, and so decided by root commit.
    const orphan = file => {
      const dir = make();
      gitOutput(["clone", "-q", a, dir], a);
      gitOutput(["checkout", "-q", "--orphan", "own"], dir);
      gitOutput(["rm", "-q", "-rf", "--cached", "."], dir);
      commitFile(dir, file);
      return { dir, root: gitOutput(["rev-list", "--max-parents=0", "HEAD"], dir) };
    };
    const graftOnto = (dir, root) => gitOutput(["-c", "user.email=trial-report-test@example.com", "-c",
      "user.name=trial-report-test", "replace", "--graft", root, A0], dir);

    const fork = make();
    gitOutput(["clone", "-q", a, fork], a);
    gitOutput(["remote", "set-url", "origin", "https://example.invalid/alice/repo.git"], fork);
    // An inherited graft file that gives the orphan root a as its parent.
    const grafted = orphan("g.txt");
    const graftFile = path.join(make(), "grafts");
    fs.writeFileSync(graftFile, `${grafted.root} ${A0}\n`);
    // A replace ref grafting the orphan onto a, kept where git only looks if told to.
    const hidden = orphan("r.txt");
    graftOnto(hidden.dir, hidden.root);
    const replacement = gitOutput(["rev-parse", `refs/replace/${hidden.root}`], hidden.dir);
    gitOutput(["update-ref", `refs/hidden-replace/${hidden.root}`, replacement], hidden.dir);
    gitOutput(["update-ref", "-d", `refs/replace/${hidden.root}`], hidden.dir);
    // A repository whose OWN refs/replace graft it onto a: its answer is a's root, unless an
    // inherited GIT_NO_REPLACE_OBJECTS hides that.
    const ownReplace = orphan("c.txt");
    graftOnto(ownReplace.dir, ownReplace.root);
    // A genuine clone with one commit of its own; an inherited shallow file cut at that commit hides
    // a's root from it and from nothing else.
    const shallow = make();
    gitOutput(["clone", "-q", a, shallow], a);
    commitFile(shallow, "s.txt");
    const shallowFile = path.join(make(), "shallow");
    fs.writeFileSync(shallowFile, `${gitOutput(["rev-parse", "HEAD"], shallow)}\n`);
    // A config whose rule rewrites the fork's urls to a's, for GIT_CONFIG_GLOBAL/_SYSTEM to name.
    const insteadOfConfig = path.join(make(), "insteadof.gitconfig");
    fs.writeFileSync(insteadOfConfig, '[url "https://example.invalid/org/"]\n\tinsteadOf = https://example.invalid/alice/\n');
    // A plain clone of a. Asked about from a directory inside it, a ceiling at the clone stops git
    // finding it; asked about directly, a redirected stdout leaves git's answers in a file instead.
    const clone = make();
    gitOutput(["clone", "-q", a, clone], a);
    const cloneSub = path.join(clone, "sub");
    fs.mkdirSync(cloneSub);
    const redirectedStdout = path.join(make(), "stdout.txt");
    return { a, fork, grafted: grafted.dir, graftFile, hidden: hidden.dir, ownReplace: ownReplace.dir, shallow, shallowFile,
      insteadOfConfig, clone, cloneSub, redirectedStdout };
  });
  try {
    const rows = [
      { name: "GIT_CONFIG_PARAMETERS", cwd: fx.fork, clean: false,
        env: { GIT_CONFIG_PARAMETERS: "'url.https://example.invalid/org/.insteadof'='https://example.invalid/alice/'" } },
      { name: "GIT_CONFIG_COUNT/KEY_0/VALUE_0", cwd: fx.fork, clean: false,
        env: { GIT_CONFIG_COUNT: "1", GIT_CONFIG_KEY_0: "url.https://example.invalid/org/.insteadOf", GIT_CONFIG_VALUE_0: "https://example.invalid/alice/" } },
      { name: "GIT_GRAFT_FILE", cwd: fx.grafted, clean: false, env: { GIT_GRAFT_FILE: fx.graftFile } },
      { name: "GIT_REPLACE_REF_BASE", cwd: fx.hidden, clean: false, env: { GIT_REPLACE_REF_BASE: "refs/hidden-replace/" } },
      { name: "GIT_CONFIG_GLOBAL", cwd: fx.fork, clean: false, env: { GIT_CONFIG_GLOBAL: fx.insteadOfConfig } },
      { name: "GIT_CONFIG_SYSTEM", cwd: fx.fork, clean: false, env: { GIT_CONFIG_SYSTEM: fx.insteadOfConfig } },
      { name: "GIT_NO_REPLACE_OBJECTS", cwd: fx.ownReplace, clean: true, env: { GIT_NO_REPLACE_OBJECTS: "1" } },
      { name: "GIT_SHALLOW_FILE", cwd: fx.shallow, clean: true, env: { GIT_SHALLOW_FILE: fx.shallowFile } },
      { name: "GIT_CEILING_DIRECTORIES", cwd: fx.cloneSub, clean: true, env: { GIT_CEILING_DIRECTORIES: fx.clone } },
      { name: "GIT_REDIRECT_STDOUT", cwd: fx.clone, clean: true, env: { GIT_REDIRECT_STDOUT: fx.redirectedStdout } }
    ];
    const wrong = [];
    for (const r of rows) {
      const inherited = (args, cwd) => gitOutput(args, cwd, { env: { ...process.env, ...r.env } });
      const clean = withConfirmedCloneRoots([r.cwd], [fx.a]).confirmed.has(r.cwd);
      const underIt = withConfirmedCloneRoots([r.cwd], [fx.a], { git: inherited }).confirmed.has(r.cwd);
      if (clean !== r.clean) wrong.push(`${r.name}: fixture sanity — a clean environment confirmed=${clean}, expected ${r.clean}`);
      if (underIt !== clean) wrong.push(`${r.name}: inherited, confirmed=${underIt}; clean, confirmed=${clean}`);
    }
    assert.deepEqual(wrong, [], "an inherited GIT_* variable changed the clone check's answer");
  } finally {
    fx.cleanup();
  }
});

test("scrubGitEnv drops every GIT_* variable, in any ASCII case, and keeps the rest", () => {
  // Windows reads environment names case-insensitively, so `Git_Dir` reaches git as GIT_DIR.
  const env = { PATH: "p", GIT_EDITOR: "e", HOME: "h", Git_Dir: "leak", GIT_CONFIG_KEY_0: "k", git_config_value_0: "v" };
  assert.deepEqual(scrubGitEnv(env), { PATH: "p", HOME: "h" });
});

test("the scrub is by prefix: GIT_* names on no list are dropped before git reads them, and look-alike names are kept", () => {
  // GIT_EDITOR is not on git's own `--local-env-vars` list, and no list can carry a name git has
  // yet to add. GITHUB_TOKEN and XGIT_DIR only resemble one, and XDG_CONFIG_HOME is outside the
  // prefix too.
  const env = { GIT_SOME_FUTURE_VARIABLE: "f", GIT_CONFIG_GLOBAL: "g", GIT_CEILING_DIRECTORIES: "c",
    GIT_REDIRECT_STDOUT: "o", GITHUB_TOKEN: "t", XGIT_DIR: "x", XDG_CONFIG_HOME: "d" };
  assert.deepEqual(scrubGitEnv(env), { GITHUB_TOKEN: "t", XGIT_DIR: "x", XDG_CONFIG_HOME: "d" });
  // Through real git: it reads GIT_EDITOR ahead of core.editor, so `git var GIT_EDITOR` prints
  // whichever of the two reached it. Windows reads the lower-case spelling as the same variable.
  for (const name of ["GIT_EDITOR", "git_editor"]) {
    const seen = gitOutput(["-c", "core.editor=kept-editor", "var", "GIT_EDITOR"], os.tmpdir(),
      { env: { ...process.env, [name]: "leaked-editor" } });
    assert.equal(seen, "kept-editor", `an inherited ${name} reached git`);
  }
});

function runGitHarness(make, settings, body) {
  const dir = make();
  const stateFile = path.join(dir, "state.json");
  const preload = path.join(dir, "git-boundary.cjs");
  const worker = path.join(dir, "worker.mjs");
  fs.writeFileSync(stateFile, JSON.stringify(settings));
  fs.writeFileSync(preload, [
    'const fs = require("node:fs");',
    'const cp = require("node:child_process");',
    'const original = cp.execFileSync;',
    'const stateFile = ' + JSON.stringify(stateFile) + ';',
    'const state = () => JSON.parse(fs.readFileSync(stateFile, "utf8"));',
    'const originalStat = fs.statSync;',
    'fs.statSync = function (value, ...args) {',
    '  const current = state();',
    '  if (current.missingCwd && value === current.failureCwd) throw Object.assign(new Error("fixture cwd missing"), { code: "ENOENT" });',
    '  return originalStat.call(this, value, ...args);',
    '};',
    'const nativeNow = performance.now.bind(performance);',
    'let clock = 0, fakeClock = false;',
    'globalThis.resetGitHarness = () => { clock = 0; fakeClock = Object.hasOwn(state(), "clockStep"); globalThis.gitCalls = []; };',
    'globalThis.resetGitHarness();',
    'Object.defineProperty(performance, "now", { value: () => fakeClock ? clock : nativeNow() });',
    'cp.execFileSync = function (file, args, options) {',
    '  if (!/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) return original.apply(this, arguments);',
    '  const current = state();',
    '  const reply = text => options.encoding === null ? Buffer.from(text) : text;',
    '  options = { ...options, env: { ...options.env, HOME: current.home, USERPROFILE: current.home,',
    '    XDG_CONFIG_HOME: current.xdg, GIT_CONFIG_SYSTEM: options.env.GIT_CONFIG_SYSTEM === "/dev/null" ? "/dev/null" : current.system,',
    '    GIT_TEST_ASSUME_DIFFERENT_OWNER: current.foreign ? "1" : "0" } };',
    '  globalThis.gitCalls.push({ file, args, timeout: options.timeout });',
    '  if (current.trace) fs.appendFileSync(current.trace, JSON.stringify({ args, cwd: options.cwd }) + "\\n");',
    '  if (args[0] === "--version" && options.cwd === current.failureCwd && current.versionFailure) throw Object.assign(new Error("fixture version launch failed"), current.versionFailure);',
    '  const phase = args[0] === "--version" ? "version" : args[0] === "config" && args.includes("safe.directory") ? "config" : "identity";',
    '  if (current.outputErrorPhase === phase) throw Object.assign(new Error("fixture output exceeded maxBuffer"), { code: "ENOBUFS" });',
    '  if (args[0] === "--version" && (Object.hasOwn(current, "versionReply") || current.versionByPath || current.versionByCwd)) {',
    '    if (current.versionReply === "ERROR") throw Object.assign(new Error("version read failed"), { status: 1 });',
    '    if (current.versionByCwd) return reply(options.cwd === current.oldCwd ? "git version 2.38.4\\n" : "git version 2.39.0\\n");',
    '    return reply(current.versionByPath ? (options.env.PATH.startsWith("old-version" + current.pathDelimiter) ? "git version 2.37.4\\n" : "git version 2.39.0\\n") : current.versionReply);',
    '  }',
    '  if (current.badFraming && args[0] === "config") {',
    '    if (current.badFraming === "odd") return reply("global\\0*\\0global\\0");',
    '    if (current.badFraming === "local" || current.badFraming === "command") return reply(current.badFraming + "\\0*\\0");',
    '    return reply("*");',
    '  }',
    '  try { return original.call(this, file, args, options); }',
    '  finally { if (fakeClock) clock += current.clockStep; }',
    '};',
    'require("node:module").syncBuiltinESMExports();'
  ].join("\n"));
  fs.writeFileSync(worker, [
    'import fs from "node:fs";',
    'import * as cp from "node:child_process";',
    'import * as trial from ' + JSON.stringify(new URL("./trial-report.mjs", import.meta.url).href) + ';',
    'const stateFile = ' + JSON.stringify(stateFile) + ';',
    'const settings = ' + JSON.stringify(settings) + ';',
    'console.log(JSON.stringify((' + body.toString() + ')(trial, fs, stateFile, settings, cp)));'
  ].join("\n"));
  const result = spawnSync(process.execPath, ["--require", preload, worker], {
    encoding: "utf8", timeout: 60_000, windowsHide: true, env: scrubGitEnv(process.env)
  });
  assert.equal(result.status, 0, result.stderr + result.stdout);
  return JSON.parse(result.stdout);
}

test("protected path bytes cannot become a different ownership exception through UTF8 replacement", () => {
  const fixture = buildInto(make => {
    const a = path.join(make(), "repo-\ufffd");
    fs.mkdirSync(a);
    initRealRepo(a, "protected-bytes.txt");
    const home = make(), xdg = make(), system = path.join(make(), "empty.gitconfig");
    fs.writeFileSync(system, "");
    const directory = Buffer.from(real(a).replaceAll("\\", "/"));
    const replacement = directory.indexOf(Buffer.from("\ufffd"));
    assert.ok(replacement >= 0);
    const rawPath = Buffer.concat([directory.subarray(0, replacement), Buffer.from([0xff]), directory.subarray(replacement + 3)]);
    fs.writeFileSync(path.join(home, ".gitconfig"), Buffer.concat([
      Buffer.from('[safe]\n\tdirectory = "'), rawPath, Buffer.from('"\n')
    ]));
    return { a, home, xdg, system };
  });
  try {
    const result = runGitHarness(fixture.make, { ...fixture, make: undefined, cleanup: undefined, foreign: true },
      (trial, fs, stateFile, settings, cp) => {
        const native = cp.spawnSync("git", ["rev-parse", "--show-toplevel"], {
          cwd: settings.a, encoding: "utf8", timeout: 5000, windowsHide: true,
          env: { ...trial.scrubGitEnv(process.env), HOME: settings.home, USERPROFILE: settings.home,
            XDG_CONFIG_HOME: settings.xdg, GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_SYSTEM: settings.system,
            GIT_CONFIG_GLOBAL: settings.home + "/.gitconfig", GIT_TEST_ASSUME_DIFFERENT_OWNER: "1" }
        });
        let result;
        try {
          result = { nativeStatus: native.status, nativeError: native.stderr,
            value: trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a) };
        } catch (error) {
          result = { nativeStatus: native.status, nativeError: native.stderr, errorCode: error.code, error: error.message };
        }
        const target = fs.realpathSync(settings.a).replaceAll("\\", "/"), linePath = target + "\nUnicode-☃";
        fs.writeFileSync(settings.home + "/.gitconfig", "[safe]\n\tdirectory = " + JSON.stringify(linePath)
          + "\n\tdirectory = " + JSON.stringify(target) + "\n");
        globalThis.resetGitHarness();
        return { ...result, linePath, control: trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a),
          replayed: globalThis.gitCalls.at(-1).args };
      });
    assert.equal(result.nativeStatus, 128, result.nativeError);
    assert.match(result.nativeError, /dubious ownership/i, "the same native Git ownership check refuses the raw-byte allowlist");
    assert.equal(result.errorCode, "git_invalid_utf8", "the report must reject malformed protected bytes before replaying a different exception: " + JSON.stringify(result));
    assert.equal(result.value, undefined);
    assert.equal(path.resolve(result.control), path.resolve(real(fixture.a)), "valid Unicode ownership exceptions still grant the configured trust");
    assert.ok(result.replayed.includes("safe.directory=" + result.linePath), "valid UTF8 and literal LF survive native NUL records unchanged");
  } finally { fixture.cleanup(); }
});

test("version launch availability is candidate-local but invalidates implicit and explicit main boundaries", () => {
  const { a, b, d, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make(), system = path.join(make(), "empty.gitconfig");
  const log = path.join(make(), "usage.jsonl"), trace = path.join(make(), "trace.jsonl");
  fs.writeFileSync(system, "");
  fs.writeFileSync(log, [row({ ts: "2026-11-01T00:00:02Z", session: "failed", cwd: b }),
    row({ ts: "2026-11-01T00:00:01Z", session: "valid", cwd: d })].map(value => JSON.stringify(value)).join("\n") + "\n");
  try {
    const results = runGitHarness(make, { a, b, d, home, xdg, system, log, trace, here: HERE,
      report: path.join(HERE, "trial-report.mjs"), foreign: false }, (trial, fs, stateFile, settings, cp) => {
      const cases = [
        { label: "timeout", error: { code: "ETIMEDOUT", signal: "SIGTERM" }, local: true },
        { label: "timeout-success-status", error: { code: "ETIMEDOUT", status: 0, signal: null }, local: true },
        { label: "signal", error: { signal: "SIGTERM" }, local: true },
        { label: "signal-success-status", error: { signal: "SIGTERM", status: 0 }, local: true },
        { label: "access", error: { code: "EACCES" }, local: true },
        { label: "permission", error: { code: "EPERM" }, local: true },
        { label: "missing-cwd", error: { code: "ENOENT" }, local: true, missingCwd: true },
        { label: "buffer-signal", error: { code: "ENOBUFS", signal: "SIGTERM" }, local: false },
        { label: "nonzero-exit", error: { status: 1 }, local: false },
        { label: "timeout-nonzero-exit", error: { code: "ETIMEDOUT", status: 1, signal: "SIGTERM" }, local: false }
      ];
      const results = [];
      for (const role of ["candidate", "explicit", "implicit"]) for (const failure of cases) {
        const failureCwd = role === "candidate" ? settings.b : role === "explicit" ? settings.a : settings.here;
        fs.writeFileSync(stateFile, JSON.stringify({ ...settings, failureCwd, missingCwd: failure.missingCwd, versionFailure: failure.error }));
        fs.writeFileSync(settings.trace, "");
        const child = cp.spawnSync(process.execPath, ["--require", stateFile.replace(/state\.json$/, "git-boundary.cjs"), settings.report,
          ...(role === "implicit" ? [] : ["--repo-root", settings.a]), "--json"], {
          cwd: settings.a, encoding: "utf8", timeout: 20_000, windowsHide: true,
          env: { ...trial.scrubGitEnv(process.env), KANBANFLOW_USAGE_LOG: settings.log, KANBANFLOW_API_URL: "http://127.0.0.1:1" }
        });
        const summary = JSON.parse(child.stdout);
        const calls = fs.readFileSync(settings.trace, "utf8").trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
        results.push({ role, failure: failure.label, measured: role === "candidate" && failure.local, status: child.status,
          verdict: summary.signals.find(signal => signal.id === 4).verdict, probe: summary.sources.cloneProbe,
          laterValidProbed: calls.some(call => call.cwd === settings.d && call.args[0] === "--version"),
          versionCalls: calls.filter(call => call.args[0] === "--version").length, stderr: child.stderr });
      }
      return results;
    });
    for (const result of results) {
      const context = result.role + ": " + result.failure;
      assert.equal(result.status, 1, context + result.stderr);
      assert.equal(result.verdict, result.measured ? "ADOPT" : "UNMEASURED", context);
      if (result.measured) {
        assert.equal(result.probe.probed, 2, context);
        assert.equal(result.laterValidProbed, true, context + ": a later valid clone must still run");
        assert.match(result.stderr, /1 of 2 outside cwd\(s\) confirmed/, context);
      } else {
        assert.equal(result.probe, null, context);
        assert.doesNotMatch(result.stderr, /folded into signal 4/, context);
        if (result.role !== "candidate") assert.equal(result.versionCalls, 1, context + ": main-boundary failure is latched globally");
      }
    }
  } finally { cleanup(); }
});

test("Git versions below the supported baseline, unknown versions and version failures stop before config or identity", () => {
  const { a, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make();
  const system = path.join(make(), "empty.gitconfig");
  fs.writeFileSync(system, "");
  try {
    const results = runGitHarness(make, { a, home, xdg, system, foreign: false }, (trial, fs, stateFile, settings) => {
      const results = [];
      for (const versionReply of ["git version 2.31.9\n", "git version 2.37.4\n", "git version 2.38.4\n", "unknown\n", "", "git version 2.55.0\nextra", "ERROR",
        "git version 2.39.0\n", "git version 2.55.0.windows.3\n", "git version 2.39.3 (Apple Git-145)\n", "git version 3.0.0\n"]) {
        fs.writeFileSync(stateFile, JSON.stringify({ ...settings, versionReply }));
        globalThis.resetGitHarness();
        try {
          results.push({ versionReply, value: trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a), calls: globalThis.gitCalls });
        } catch (error) {
          results.push({ versionReply, error: error.message, calls: globalThis.gitCalls });
        }
      }
      return results;
    });
    for (const result of results.slice(0, 7)) {
      assert.match(result.error, /Git 2\.39 or newer.*required/i, result.versionReply);
      assert.deepEqual(result.calls.map(call => call.args), [["--version"]], "unsupported Git must never capture config or probe identity");
    }
    for (const result of results.slice(7)) {
      assert.equal(result.error, undefined, result.versionReply);
      assert.equal(path.resolve(result.value), path.resolve(a));
      assert.equal(result.calls.length, 3, "supported versions delegate protected capture and identity to real Git");
    }
  } finally { cleanup(); }
});

test("each new runner pins its captured PATH and still checks fresh version replies", () => {
  const { a, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make();
  const system = path.join(make(), "empty.gitconfig");
  fs.writeFileSync(system, "");
  try {
    const results = runGitHarness(make, { a, home, xdg, system, foreign: false, versionByPath: true, pathDelimiter: path.delimiter }, (trial, fs, stateFile, settings) => {
      const results = [];
      for (const prefix of ["new-version", "old-version", "new-version"]) {
        globalThis.resetGitHarness();
        try {
          results.push({ value: trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a, { env: { ...process.env, PATH: prefix + settings.pathDelimiter + process.env.PATH } }), calls: globalThis.gitCalls });
        } catch (error) { results.push({ error: error.message, calls: globalThis.gitCalls }); }
      }
      return results;
    });
    assert.equal(path.resolve(results[0].value), path.resolve(a));
    assert.match(results[1].error, /Git 2\.39 or newer.*required/i);
    assert.deepEqual(results[1].calls.map(call => call.args), [["--version"]]);
    assert.equal(path.resolve(results[2].value), path.resolve(a));
  } finally { cleanup(); }
});

test("one captured report runner keeps its executable pin and rechecks versions when cwd changes", () => {
  const { a, c, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make();
  const system = path.join(make(), "empty.gitconfig");
  fs.writeFileSync(system, "");
  try {
    // Only version replies vary at the real child boundary; supported config/identity reads
    // still use the installed pin. The ignored relative prefix is a fixture marker, not discovery.
    const results = runGitHarness(make, { a, c, home, xdg, system, foreign: false, versionByCwd: true, oldCwd: c, pathDelimiter: path.delimiter },
      (trial, fs, stateFile, settings) => {
        const git = trial.createGitRunner({ env: { ...process.env, PATH: "relative-tools" + settings.pathDelimiter + process.env.PATH } });
        const results = [];
        for (const cwd of [settings.a, settings.c, settings.a]) {
          globalThis.resetGitHarness();
          try { results.push({ value: git(["rev-parse", "--show-toplevel"], cwd), calls: globalThis.gitCalls }); }
          catch (error) { results.push({ error: error.message, calls: globalThis.gitCalls }); }
        }
        return results;
      });
    assert.equal(path.resolve(results[0].value), path.resolve(a));
    assert.match(results[1].error, /Git 2\.39 or newer.*required/i);
    assert.deepEqual(results[1].calls.map(call => call.args), [["--version"]], "the second cwd cannot inherit the first binary's capabilities");
    assert.equal(path.resolve(results[2].value), path.resolve(a));
    assert.equal(results[2].calls[0].args[0], "--version");
    const files = results.flatMap(result => result.calls.map(call => call.file));
    assert.ok(files.every(file => path.isAbsolute(file)));
    assert.equal(new Set(files).size, 1, "version replies may change, but the selected executable cannot");
  } finally { cleanup(); }
});

test("a report runner pins one absolute executable across version, trust, metadata and identity", () => {
  const { a, c, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make(), system = path.join(make(), "empty.gitconfig");
  fs.writeFileSync(system, "");
  try {
    const result = runGitHarness(make, { a, c, home, xdg, system, foreign: false }, (trial, fs, stateFile, settings) => {
      const env = { ...process.env }, git = trial.createGitRunner({ env });
      for (const key of Object.keys(env)) if (/^path$/i.test(key)) delete env[key];
      env.PATH = "missing-after-capture";
      globalThis.resetGitHarness();
      const first = git(["remote", "get-url", "origin"], settings.a);
      const second = git(["rev-parse", "--show-toplevel"], settings.c);
      return { first, second, calls: globalThis.gitCalls };
    });
    assert.equal(result.first, "https://example.invalid/org/repo.git");
    assert.equal(path.resolve(result.second), path.resolve(c));
    assert.ok(result.calls.every(call => path.isAbsolute(call.file)), "no phase may execute a bare command");
    assert.equal(new Set(result.calls.map(call => call.file)).size, 1);
    assert.equal(result.calls.filter(call => call.args[0] === "--version").length, 2, "capabilities stay fresh after pinning");
    assert.ok(result.calls.some(call => call.args.includes("--get-regexp")), "the NUL metadata guard must use the pin too");
  } finally { cleanup(); }
});

test("native current-directory and relative PATH shadows cannot replace the pinned Git", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const windows = process.platform === "win32", name = windows ? "git.exe" : "git";
  const fake = path.join(b, name), relative = path.join(b, "relative-tools");
  const link = (from, to) => { try { fs.linkSync(from, to); } catch (error) {
    if (!["EPERM", "EACCES", "EXDEV", "ENOTSUP"].includes(error.code)) throw error;
    fs.copyFileSync(from, to);
  } };
  try {
    link(process.execPath, fake);
    fs.mkdirSync(relative); link(fake, path.join(relative, name));
    const env = scrubGitEnv(process.env);
    const key = windows ? Object.keys(env).sort().find(value => value.toUpperCase() === "PATH") : "PATH";
    const originalPath = env[key];
    for (const value of Object.keys(env)) if (/^path$/i.test(value)) delete env[value];
    for (const prefix of windows ? ["relative-tools", "", null] : ["relative-tools", ""]) {
      const selected = { ...env, PATH: prefix === null ? originalPath : prefix + path.delimiter + originalPath };
      const bare = spawnSync("git", ["--version"], { cwd: b, env: selected, encoding: "utf8", timeout: 5_000, windowsHide: true });
      assert.equal(bare.status, 0, bare.stderr);
      assert.equal(bare.stdout.trim(), process.version, "the native bare-name control must actually select candidate-owned Node");
      assert.equal(real(createGitRunner({ env: selected })(["rev-parse", "--show-toplevel"], b)), real(b));
    }
    const quoted = path.join(make(), "trusted;tools with spaces");
    fs.mkdirSync(quoted); link(fake, path.join(quoted, name));
    for (const quote of windows ? ['"', "'"] : [""]) {
      const selected = { ...env, PATH: quote + quoted + quote };
      assert.throws(() => createGitRunner({ env: selected })(["rev-parse", "--show-toplevel"], a),
        error => error.code === "git_unsupported" && error.message.includes(process.version), "a quoted absolute field must select its native file before version rejection");
    }
    if (windows) {
      const selected = { ...env, PATH: ["", ".", "relative-tools", "C:tools", "\\tools"].join(path.delimiter), Path: originalPath };
      assert.throws(() => createGitRunner({ env: selected })(["rev-parse", "--show-toplevel"], b),
        error => error.code === "git_unsupported" && error.cause?.code === "ENOENT", "the first casefolded PATH key cannot fall back to Path or drive-dependent fields");
    } else {
      const noExecute = make(); fs.writeFileSync(path.join(noExecute, "git"), "not executable", { mode: 0o644 });
      assert.throws(() => createGitRunner({ env: { ...env, PATH: noExecute } })(["rev-parse", "--show-toplevel"], a), error => error.code === "git_unsupported");
    }
  } finally { cleanup(); }
});

test("createGitRunner throws git_cwd_missing, not git_unsupported, when the cwd does not exist", () => {
  const missingDir = path.join(os.tmpdir(), "kanbanflow-test-missing-" + Math.random().toString(36).slice(2));
  // Verify the directory truly does not exist before running the probe.
  assert.ok(!fs.existsSync(missingDir), "precondition: directory must not exist");
  const git = createGitRunner();
  let thrown;
  try { git(["rev-parse", "--show-toplevel"], missingDir); }
  catch (e) { thrown = e; }
  assert.ok(thrown, "must throw when cwd does not exist");
  // git_cwd_missing keeps the per-candidate failure scoped; git_unsupported would cache
  // gitIncompatibility globally and suppress all further clone-probe git calls.
  assert.equal(thrown.code, "git_cwd_missing", "code must be git_cwd_missing, not git_unsupported");
  assert.match(thrown.message, /does not exist/i);
});

test("a deleted logged cwd stays outside while a later valid clone is still probed", () => {
  const { a, b, make, cleanup } = buildInto(make => {
    const a = make(), b = make();
    initRealRepo(a, "main.txt");
    gitOutput(["clone", "-q", a, b], a);
    return { a, b };
  });
  try {
    const removed = make();
    assert.equal(fs.statSync(removed).isDirectory(), true);
    fs.rmdirSync(removed);
    const log = path.join(make(), "usage.jsonl");
    fs.writeFileSync(log, [
      row({ session: "valid-clone", cwd: b, ts: "2026-09-01T10:00:00.000Z" }),
      row({ session: "deleted-first", cwd: removed, ts: "2026-09-01T10:00:01.000Z" })
    ].map(value => JSON.stringify(value)).join("\n") + "\n");
    const result = runCliJson(log, a);
    assert.equal(result.status, 1, result.stdout + result.stderr);
    const summary = JSON.parse(result.stdout);
    assert.notEqual(find(summary, 4).verdict, "UNMEASURED", "one deleted directory cannot disable the report's Git source");
    assert.deepEqual(find(summary, 4).detail.map(detail => detail.session), ["deleted-first"]);
    assert.equal(summary.sources.cloneProbe.probed, 2, "the valid clone is checked after the newer deleted cwd");
    assert.match(result.stderr, /1 of 2 outside cwd\(s\) confirmed/);
    assert.doesNotMatch(result.stderr, /Git 2\.39 or newer is required/);
  } finally { cleanup(); }
});

test("an absent Git executable with an existing cwd remains an unsupported source", () => {
  const { a, b, make, cleanup } = buildInto(make => ({ a: make(), b: make() }));
  try {
    const emptyPath = make();
    const env = scrubGitEnv(process.env);
    for (const key of Object.keys(env)) if (/^path$/i.test(key)) delete env[key];
    env.PATH = emptyPath;
    assert.equal(fs.statSync(a).isDirectory(), true);
    const control = spawnSync("git", ["--version"], { cwd: a, env, encoding: "utf8", timeout: 5_000, windowsHide: true });
    assert.equal(control.error?.code, "ENOENT", "the empty executable search path really cannot launch Git");
    assert.throws(() => createGitRunner({ env })(["rev-parse", "--show-toplevel"], a), error => error.code === "git_unsupported");
    const log = path.join(make(), "usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ cwd: b })) + "\n");
    const result = spawnSync(process.execPath, [path.join(HERE, "trial-report.mjs"), "--repo-root", a, "--json"], {
      cwd: a, env: { ...env, KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" },
      encoding: "utf8", timeout: 20_000, windowsHide: true
    });
    assert.equal(result.status, 1, result.stdout + result.stderr);
    const summary = JSON.parse(result.stdout);
    assert.equal(find(summary, 4).verdict, "UNMEASURED");
    assert.equal(summary.sources.cloneProbe, null);
    assert.equal((result.stderr.match(/Git 2\.39 or newer is required/g) ?? []).length, 1);
  } finally { cleanup(); }
});

test("a non-directory cwd is a candidate-local launch failure", () => {
  const { make, cleanup } = buildInto(() => ({}));
  try {
    const file = path.join(make(), "file.txt");
    fs.writeFileSync(file, "fixture");
    assert.throws(() => createGitRunner()(["rev-parse", "--show-toplevel"], file), error => error.code === "git_cwd_missing");
  } finally { cleanup(); }
});

test("protected capture matches native ownership resets and excludes repository-context trust", () => {
  const { a, c, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make();
  fs.mkdirSync(path.join(xdg, "git"));
  const system = path.join(make(), "system.gitconfig");
  const global = path.join(home, ".gitconfig");
  const xdgConfig = path.join(xdg, "git", "config");
  const included = path.join(make(), "included.gitconfig");
  const conditional = path.join(make(), "conditional.gitconfig");
  const set = (file, key, value) => gitOutput(["config", "--file", file, "--add", key, value], a);
  try {
    for (const file of [system, global, xdgConfig, included, conditional]) fs.writeFileSync(file, "");
    set(system, "safe.directory", "/synthetic/system");
    set(system, "safe.directory", c);
    set(system, "safe.directory", a);
    set(xdgConfig, "safe.directory", "");
    set(global, "safe.directory", a);
    set(global, "include.path", included);
    set(global, "include.path", included);
    set(global, "includeIf.gitdir:**.path", conditional);
    set(global, "includeIf.onbranch:**.path", conditional);
    set(included, "safe.directory", "/synthetic/included");
    set(conditional, "safe.directory", "*");
    set(global, "url.https://example.invalid/org/repo.git.insteadOf", "https://example.invalid/other/thing.git");
    gitOutput(["config", "--local", "safe.directory", "*"], a);
    gitOutput(["config", "--local", "safe.directory", "*"], c);
    const results = runGitHarness(make, { home, xdg, system, global, xdgConfig, conditional, foreign: true, a, c },
      (trial, fs, stateFile, settings, cp) => {
        const attempt = action => {
          try { return { ok: true, value: action() }; }
          catch (error) { return { ok: false, status: error.status ?? null, message: error.message }; }
        };
        const raw = cwd => attempt(() => cp.execFileSync("git", ["rev-parse", "--show-toplevel"], {
          cwd, env: trial.scrubGitEnv(process.env), encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 5_000, windowsHide: true
        }).trim());
        const ordinaryDeclared = raw(settings.a);
        const ordinaryReset = raw(settings.c);
        const declared = trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a);
        const resetAndLocal = attempt(() => trial.gitOutput(["rev-parse", "--show-toplevel"], settings.c));
        const values = cwd => {
          const fields = trial.gitOutput(["config", "--show-scope", "--null", "--get-all", "safe.directory"], cwd).split("\0");
          const command = [];
          for (let i = 0; i + 1 < fields.length; i += 2) if (fields[i] === "command") command.push(fields[i + 1]);
          return command;
        };
        const aValues = values(settings.a);
        fs.appendFileSync(settings.global, "\n[safe]\n\tdirectory = " + JSON.stringify(settings.c) + "\n");
        const refreshed = trial.gitOutput(["rev-parse", "--show-toplevel"], settings.c);
        const cValues = values(settings.c);
        const remote = trial.gitOutput(["remote", "get-url", "origin"], settings.c);
        const confirmed = trial.withConfirmedCloneRoots([settings.c], [settings.a]).confirmed.has(settings.c);
        fs.writeFileSync(settings.system, "");
        fs.writeFileSync(settings.xdgConfig, "");
        const conditionalOnly = [];
        for (const condition of ["gitdir:**", "onbranch:**"]) {
          fs.writeFileSync(settings.global, '[includeIf "' + condition + '"]\n\tpath = ' + JSON.stringify(settings.conditional) + "\n");
          conditionalOnly.push({
            condition, native: raw(settings.a),
            isolated: attempt(() => trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a))
          });
        }
        const inheritedTrust = attempt(() => trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a, {
          env: { ...process.env, GIT_CONFIG_COUNT: "1", GIT_CONFIG_KEY_0: "safe.directory", GIT_CONFIG_VALUE_0: "*" }
        }));
        fs.writeFileSync(stateFile, JSON.stringify({ ...settings, foreign: false }));
        for (const file of [settings.system, settings.global, settings.xdgConfig]) fs.unlinkSync(file);
        const optionalMissing = attempt(() => trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a));
        fs.writeFileSync(settings.global, "[broken\n");
        const malformed = attempt(() => trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a));
        const framing = [];
        for (const kind of ["raw", "odd", "local", "command"]) {
          fs.writeFileSync(stateFile, JSON.stringify({ ...settings, foreign: true, badFraming: kind }));
          framing.push({ kind, result: attempt(() => trial.gitOutput(["rev-parse", "--show-toplevel"], settings.a)) });
        }
        return { ordinaryDeclared, ordinaryReset, declared, resetAndLocal, aValues, refreshed, cValues, remote,
          confirmed, conditionalOnly, inheritedTrust, optionalMissing, malformed, framing };
      });
    assert.equal(results.ordinaryDeclared.ok, true, "native Git trusts the explicit protected declaration");
    assert.equal(results.ordinaryReset.ok, false, "native Git applies the XDG reset before the HOME file");
    assert.equal(results.ordinaryReset.status, 128);
    assert.equal(real(results.declared), real(a));
    assert.equal(results.resetAndLocal.ok, false, "isolation cannot restore reset system trust or accept local '*'");
    assert.equal(results.resetAndLocal.status, 128);
    assert.deepEqual(results.aValues, ["/synthetic/system", c, a, "", a, "/synthetic/included", "/synthetic/included"]);
    assert.equal(real(results.refreshed), real(c), "current declarations are read without stale caching");
    assert.deepEqual(results.cValues, ["/synthetic/system", c, a, "", a, "/synthetic/included", "/synthetic/included", c]);
    assert.equal(results.remote, "https://example.invalid/other/thing.git");
    assert.equal(results.confirmed, false, "protected ownership trust does not restore global URL rewrites");
    for (const result of results.conditionalOnly) {
      assert.equal(result.native.ok, false, result.condition + ": native ownership ignores repository-context includes");
      assert.equal(result.isolated.ok, false, result.condition + ": capture must not promote a conditional wildcard");
      assert.equal(result.isolated.status, 128);
    }
    assert.equal(results.inheritedTrust.ok, false, "inherited command-scope injection remains scrubbed");
    assert.equal(results.optionalMissing.ok, true, "missing optional config files do not break an owned repository");
    assert.equal(results.malformed.ok, false, "a failed protected read cannot be treated as an absent key");
    for (const value of results.framing) {
      assert.equal(value.result.ok, false, value.kind + ": malformed or unprotected values cannot create trust");
      if (value.kind === "raw" || value.kind === "odd") assert.match(value.result.message, /framing/);
      else assert.equal(value.result.status, 128);
    }
  } finally {
    cleanup();
  }
});


test("protected safe.directory exceptions survive isolated Git probes without restoring global URL rewrites", () => {
  const { a, b, c, d, make, cleanup } = buildCloneFixture();
  const mainUrl = "https://example.invalid/org/repo.git";
  try {
    // Git itself uses this test switch for ownership regressions:
    // https://github.com/git/git/blob/master/t/t0033-safe-directory.sh
    // Inject it after production scrubbing; every answer still comes from the real Git binary.
    const preload = path.join(make(), "different-owner.cjs");
    fs.writeFileSync(preload, [
      'const cp = require("node:child_process");',
      'const original = cp.execFileSync;',
      'cp.execFileSync = function (file, args, options) {',
      '  return original.call(this, file, args, /^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file))) ? { ...options, env: { ...options.env, GIT_TEST_ASSUME_DIFFERENT_OWNER: "1" } } : options);',
      '};',
      'require("node:module").syncBuiltinESMExports();'
    ].join("\n"));
    gitOutput(["config", "--local", "safe.directory", d], d);
    const moduleUrl = new URL("./trial-report.mjs", import.meta.url).href;
    const script = [
      `import { gitOutput, resolveRepoRoots, withConfirmedCloneRoots } from ${JSON.stringify(moduleUrl)};`,
      `const [a, b, c, d] = ${JSON.stringify([a, b, c, d])};`,
      'const git = (args, cwd) => gitOutput(args, cwd);',
      'let remote = null; try { remote = git(["remote", "get-url", "origin"], c); } catch {}',
      'const roots = resolveRepoRoots(null, { cwd: a, git });',
      'const { confirmed } = withConfirmedCloneRoots([b, c, d], [a], { git });',
      'console.log(JSON.stringify({ roots, remote, confirmed: [...confirmed.keys()] }));'
    ].join("\n");

    for (const location of ["HOME", "XDG_CONFIG_HOME", "GIT_CONFIG_GLOBAL", "SYSTEM", "INCLUDED", "NOSYSTEM"]) {
      const home = make();
      const xdg = make();
      fs.mkdirSync(path.join(xdg, "git"));
      const system = path.join(make(), "system.gitconfig");
      const global = location === "XDG_CONFIG_HOME" ? path.join(xdg, "git", "config")
        : location === "GIT_CONFIG_GLOBAL" ? path.join(make(), "explicit.gitconfig")
          : path.join(home, ".gitconfig");
      // An empty global value resets the system wildcard. Preserve that exact ordering;
      // d's repository-local exception cannot opt itself into protected trust.
      const exceptions = "[safe]\n" + [a, b, c]
        .map(directory => "\tdirectory = " + JSON.stringify(directory.replaceAll("\\", "/")) + "\n").join("");
      const rewrite = '[url "' + mainUrl + '"]\n\tinsteadOf = https://example.invalid/other/thing.git\n';
      fs.writeFileSync(system, location === "SYSTEM" ? exceptions : "[safe]\n\tdirectory = *\n");
      if (location === "INCLUDED") {
        const included = path.join(make(), "included.gitconfig");
        fs.writeFileSync(included, exceptions);
        fs.writeFileSync(global, "[safe]\n\tdirectory =\n[include]\n\tpath = "
          + JSON.stringify(included.replaceAll("\\", "/")) + "\n" + rewrite);
      } else {
        fs.writeFileSync(global, (location === "SYSTEM" ? "" : location === "NOSYSTEM" ? exceptions
          : "[safe]\n\tdirectory =\n" + exceptions) + rewrite);
      }
      const env = { ...scrubGitEnv(process.env), HOME: home, USERPROFILE: home, XDG_CONFIG_HOME: xdg,
        GIT_CONFIG_SYSTEM: system };
      if (location === "GIT_CONFIG_GLOBAL") env.GIT_CONFIG_GLOBAL = global;
      if (location === "NOSYSTEM") env.GIT_CONFIG_NOSYSTEM = "1";
      const rawEnv = { ...env, GIT_TEST_ASSUME_DIFFERENT_OWNER: "1" };
      const raw = spawnSync("git", ["remote", "get-url", "origin"], {
        cwd: c, env: rawEnv, encoding: "utf8", timeout: 5_000, windowsHide: true
      });
      assert.equal(raw.status, 0, raw.stderr);
      assert.equal(raw.stdout.trim(), mainUrl, "control must observe an actual global URL rewrite");
      const denied = spawnSync("git", ["rev-parse", "--show-toplevel"], {
        cwd: d, env: rawEnv, encoding: "utf8", timeout: 5_000, windowsHide: true
      });
      assert.notEqual(denied.status, 0, "empty reset and missing protected exception must deny d");
      assert.match(denied.stderr, /dubious ownership/);

      const child = spawnSync(process.execPath, ["--require", preload, "--input-type=module", "--eval", script], {
        cwd: a, env, encoding: "utf8", timeout: 20_000, windowsHide: true
      });
      assert.equal(child.status, 0, child.stderr);
      const result = JSON.parse(child.stdout);
      assert.equal(result.roots.source, "git", location + ": an explicitly trusted root must remain measurable");
      assert.equal(result.remote, "https://example.invalid/other/thing.git", "URL rewrites remain isolated");
      assert.deepEqual(result.confirmed, [b], "trust b's real clone, keep the distinct c and untrusted d outside");
    }
  } finally {
    cleanup();
  }
});

function runGitDeadlineFixture(stepMs, timeoutMs = 5000, urlRead = false, initialClock = []) {
  const fixture = buildInto(make => {
    const a = make();
    initRealRepo(a, "deadline.txt");
    if (urlRead) gitOutput(["config", "remote.origin.url", "https://example.invalid/org/repo.git"], a);
    return { a };
  });
  try {
    const emptyConfig = path.join(fixture.make(), "empty.gitconfig");
    fs.writeFileSync(emptyConfig, "");
    const preload = path.join(fixture.make(), "git-clock.cjs");
    fs.writeFileSync(preload, [
      'const cp = require("node:child_process");',
      'const original = cp.execFileSync;',
      'let elapsed = 0; globalThis.gitTimeouts = []; globalThis.gitMaxBuffers = []; Date.now = () => 50000 - elapsed;',
      'const steps = JSON.parse(process.env.KBF_TEST_GIT_STEPS_MS);',
      'const initial = JSON.parse(process.env.KBF_TEST_GIT_INITIAL_CLOCK_MS);',
      'Object.defineProperty(performance, "now", { value: () => initial.shift() ?? elapsed });',
      'cp.execFileSync = function (file, args, options) {',
      '  if (!/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) return original.apply(this, arguments);',
      '  globalThis.gitTimeouts.push(options.timeout);',
      '  globalThis.gitMaxBuffers.push(options.maxBuffer);',
      '  try { return original.apply(this, arguments); } finally { elapsed += steps.shift() ?? 0; }',
      '};',
      'require("node:module").syncBuiltinESMExports();'
    ].join("\n"));
    const script = [
      `import { gitOutput } from ${JSON.stringify(new URL("./trial-report.mjs", import.meta.url).href)};`,
      'let success = false, message = null, errorCode = null;',
      `try { success = Boolean(gitOutput(${JSON.stringify(urlRead ? ["remote", "get-url", "origin"] : ["rev-parse", "--show-toplevel"])}, ${JSON.stringify(fixture.a)}, { timeoutMs: ${JSON.stringify(timeoutMs)} })); } catch (error) { message = error.message; errorCode = error.code; }`,
      'console.log(JSON.stringify({ success, message, errorCode, timeouts: globalThis.gitTimeouts, maxBuffers: globalThis.gitMaxBuffers }));'
    ].join("\n");
    const child = spawnSync(process.execPath, ["--require", preload, "--input-type=module", "--eval", script], {
      cwd: fixture.a, encoding: "utf8", timeout: 20_000, windowsHide: true,
      env: { ...scrubGitEnv(process.env), GIT_CONFIG_GLOBAL: emptyConfig, GIT_CONFIG_SYSTEM: emptyConfig,
        KBF_TEST_GIT_INITIAL_CLOCK_MS: JSON.stringify(initialClock),
        KBF_TEST_GIT_STEPS_MS: JSON.stringify(Array.isArray(stepMs) ? stepMs : [stepMs, stepMs, stepMs, stepMs]) }
    });
    assert.equal(child.status, 0, child.stderr);
    return JSON.parse(child.stdout);
  } finally {
    fixture.cleanup();
  }
}

test("Git config reads and identity share one five-second deadline", () => {
  const result = runGitDeadlineFixture(1000.25);
  assert.equal(result.success, true, result.message);
  assert.deepEqual(result.timeouts, [5000, 3999, 2999], "version, protected capture and identity share the same budget; timeouts round down");
});

test("Git URL framing metadata and the URL read share the original bounded deadline", () => {
  const result = runGitDeadlineFixture(1000.25, 5000, true);
  assert.equal(result.success, true, result.message);
  assert.deepEqual(result.timeouts, [5000, 3999, 2999, 1999], "the metadata child receives only the same budget's remaining time");
  assert.deepEqual(result.maxBuffers, Array(4).fill(256 * 1024), "URL metadata has the same native byte cap");
  const exhausted = runGitDeadlineFixture(1666.5, 5000, true);
  assert.equal(exhausted.success, false);
  assert.deepEqual(exhausted.timeouts, [5000, 3333, 1667], "metadata cannot grant the final read a sub-millisecond remainder");
  assert.match(exhausted.message, /deadline/i);
});

test("Git identity is not started after config reads exhaust its deadline", () => {
  const result = runGitDeadlineFixture(2500);
  assert.equal(result.success, false);
  assert.equal(result.errorCode, "git_cwd_unavailable", "spent deadlines use the same reference/candidate availability policy");
  assert.deepEqual(result.timeouts, [5000, 2500], "identity must not start after version and config consume the budget");
  assert.match(result.message, /budget|deadline|timed out/i);
});

test("a deadline spent before version launch is an unavailable probe without spawning Git", () => {
  const result = runGitDeadlineFixture(0, 5000, false, [0, 5000.25]);
  assert.equal(result.success, false);
  assert.equal(result.errorCode, "git_cwd_unavailable");
  assert.deepEqual(result.timeouts, [], "no child may start after the initial deadline is spent");
  assert.match(result.message, /deadline/);
});

test("Git identity refuses a sub-millisecond remainder and accepts a whole millisecond", () => {
  const fractional = runGitDeadlineFixture([2499, 2500.5]);
  assert.equal(fractional.success, false);
  assert.deepEqual(fractional.timeouts, [5000, 2501], "half a millisecond cannot start another child");
  assert.match(fractional.message, /deadline/i);
  const boundary = runGitDeadlineFixture([2499, 2500]);
  assert.deepEqual(boundary.timeouts, [5000, 2501, 1], "one whole millisecond may start the final child");
});

test("Git refuses an identity subprocess when less than one millisecond remains", () => {
  const result = runGitDeadlineFixture([2500, 2499.25]);
  assert.equal(result.success, false);
  assert.deepEqual(result.timeouts, [5000, 2500]);
});

test("Git honors a shorter caller cap without extending its default maximum", () => {
  const short = runGitDeadlineFixture(1000.25, 3000);
  assert.equal(short.success, true, short.message);
  assert.deepEqual(short.timeouts, [3000, 1999, 999]);
  const long = runGitDeadlineFixture(1000.25, 10000);
  assert.equal(long.success, true, long.message);
  assert.deepEqual(long.timeouts, [5000, 3999, 2999]);
});

// --- 5d. Which candidates main() probes, and in what order (bounded-probe-budget priority) --

test("selectCloneCandidates excludes rows outside --since, with no usable session/ts, or already inside roots", () => {
  const roots = [path.resolve("C:\\real")];
  const inWindow = row({ session: "a", ts: "2026-09-10T00:00:00.000Z", cwd: path.resolve("C:\\outside\\a") });
  const beforeWindow = row({ session: "b", ts: "2026-09-01T00:00:00.000Z", cwd: path.resolve("C:\\outside\\b") });
  const noSession = row({ session: "", ts: "2026-09-10T00:00:00.000Z", cwd: path.resolve("C:\\outside\\c") });
  const badTs = row({ session: "d", ts: "not-a-date", cwd: path.resolve("C:\\outside\\d") });
  const alreadyInside = row({ session: "e", ts: "2026-09-10T00:00:00.000Z", cwd: path.join(roots[0], "src") });
  const failed = row({ session: "f", ts: "2026-09-10T00:00:00.000Z", cwd: path.resolve("C:\\outside\\f"), outcome: "error" });
  const cutoff = Date.parse("2026-09-05T00:00:00.000Z");
  const candidates = selectCloneCandidates(
    [inWindow, beforeWindow, noSession, badTs, alreadyInside, failed], cutoff, roots);
  assert.deepEqual(candidates, [path.resolve("C:\\outside\\a")], "only the valid, in-window, outside, successful row survives");
});

test("selectCloneCandidates orders NEWEST FIRST, so a downstream probe cap drops the STALEST candidates, never a live one", () => {
  // The defect this fixes: candidates used to come from the whole log in FILE order (append-only,
  // so oldest first in practice). With 50+ distinct stale cwds ahead of it, a live in-window clone
  // landed past MAX_CLONE_PROBES and was never probed at all — reading as outside for no reason but
  // where it happened to sort, not because it could not be confirmed.
  const roots = [path.resolve("C:\\real")];
  const stale = Array.from({ length: 51 }, (_, i) =>
    row({ session: `stale-${i}`, ts: `2026-01-01T00:00:${String(i).padStart(2, "0")}.000Z`, cwd: path.resolve(`C:\\outside\\stale-${i}`) }));
  const live = row({ session: "live", ts: "2026-09-20T00:00:00.000Z", cwd: path.resolve("C:\\outside\\live") });
  const rows = [...stale, live]; // live appended LAST, exactly as an append-only log would hold it
  const candidates = selectCloneCandidates(rows, null, roots);
  assert.equal(candidates.length, 52, "selectCloneCandidates itself does not cap — that stays withConfirmedCloneRoots's job");
  assert.equal(candidates[0], path.resolve("C:\\outside\\live"), "the newest candidate must sort FIRST, not land at index 51+");
});

test("selectCloneCandidates deduplicates by cwd — one entry per distinct cwd, in newest-occurrence order", () => {
  const roots = [path.resolve("C:\\real")];
  const repeated = path.resolve("C:\\outside\\repeat");
  const other = path.resolve("C:\\outside\\other");
  const rows = [
    row({ session: "a", ts: "2026-09-01T00:00:00.000Z", cwd: repeated }),
    row({ session: "b", ts: "2026-09-20T00:00:00.000Z", cwd: repeated }), // same cwd, later call
    row({ session: "c", ts: "2026-09-10T00:00:00.000Z", cwd: other })
  ];
  assert.deepEqual(selectCloneCandidates(rows, null, roots), [repeated, other]);
});

// --- 5e. End to end: the real CLI, real repos, real log, a scrubbed env ---------------------

// The REAL trial-report.mjs, `--json`, reading `log` and measuring against `--repo-root <repoRoot>`.
// Every GIT_* variable this test's OWN environment might carry is removed from the child — the
// result must be about the fixture's repositories, not about wherever the outer process happens to
// point those variables. (gitOutput scrubs internally regardless — section 5c — this is belt-and-
// braces for the TEST's own reliability, independent of that fix.) The activity port is dead
// (127.0.0.1:1) on purpose, matching the existing dead-port tests elsewhere in this file: signals
// 2/3 are UNMEASURED, so overall is INCOMPLETE and the CLI exits 1 — that is correct, not a failure
// of any test here. Signal 4 needs only the log, not the activity trail, so it is still scored.
const runCliJson = (log, repoRoot, args = [], nodeArgs = []) => {
  const env = { ...scrubGitEnv(process.env), KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" };
  return spawnSync(process.execPath, [...nodeArgs, path.join(HERE, "trial-report.mjs"), "--repo-root", repoRoot, ...args, "--json"], {
    encoding: "utf8", timeout: 30_000, windowsHide: true,
    env
  });
};

function remoteLimitFixture() {
  const fixture = buildCloneFixture();
  const { a, b, d } = fixture;
  for (const repo of [a, d]) gitOutput(["remote", "remove", "origin"], repo);
  const configs = new Map([a, b, d].map(repo => [repo, fs.readFileSync(path.join(repo, ".git", "config"), "utf8")]));
  // Git config supports these escapes; CR remains a literal payload character, not JSON's \\r.
  const quoteConfig = value => '"' + value.replaceAll("\\", "\\\\").replaceAll('"', '\\"')
    .replaceAll("\n", "\\n").replaceAll("\t", "\\t").replaceAll("\b", "\\b") + '"';
  const configure = (repo, remotes) => {
    let text = configs.get(repo);
    for (const { name = "origin", urls, push = [] } of remotes) {
      text += `\n[remote "${name}"]\n`;
      for (const url of urls) text += `\turl = ${quoteConfig(url)}\n`;
      for (const url of push) text += `\tpushurl = ${quoteConfig(url)}\n`;
    }
    fs.writeFileSync(path.join(repo, ".git", "config"), text);
  };
  return { ...fixture, configure, mainUrl: "https://example.invalid/org/repo.git" };
}

test("remote normalization retains meaningful surrounding whitespace in every namespace", () => {
  for (const base of ["https://example.invalid/org/repo.git", "git@example.invalid:org/repo.git",
    "ssh://alice@example.invalid/org/repo.git", "external::payload", "ftp://example.invalid/repo"]) {
    for (const padded of [" " + base, base + " ", "\t" + base, base + "\t"]) {
      assert.notEqual(normalizeRemoteUrl(padded), normalizeRemoteUrl(base), JSON.stringify(padded));
    }
  }
  assert.equal(normalizeRemoteUrl(" \t "), " \t ");
  assert.equal(isLocalPathRemote(" \t "), true, "a whitespace filename is still a local path");
  assert.equal(isLocalPathRemote(" file:///repo"), false, "leading space is part of Git's remote payload");
  assert.equal(isLocalPathRemote(" C:repo"), false, "leading space prevents a Windows DOS-drive prefix");
  assert.equal(normalizeRemoteUrl("HTTPS://github.com/Org/Repo.git"), normalizeRemoteUrl("git@github.com:org/repo"));
});

test("native Git whitespace fetch and push payloads veto a matching repo without losing raw evidence", () => {
  const { a, b, d, configure, mainUrl, cleanup } = remoteLimitFixture();
  const env = { ...scrubGitEnv(process.env), GIT_CONFIG_GLOBAL: "/dev/null", GIT_CONFIG_SYSTEM: "/dev/null", GIT_CONFIG_NOSYSTEM: "1" };
  const ssh = "git@example.invalid:org/repo.git";
  try {
    for (const [base, padded] of [[mainUrl, mainUrl + " "], [mainUrl, " " + mainUrl], [mainUrl, mainUrl + "\r"], [mainUrl, mainUrl + "\\r"],
      [ssh, ssh + " "], [ssh, "git@example.invalid: org/repo.git"]]) {
      configure(a, [{ urls: [base] }]);
      for (const role of ["fetch", "push"]) {
        configure(b, [{ urls: [role === "fetch" ? padded : base], push: [role === "push" ? padded : base] }]);
        const args = ["remote", "get-url", ...(role === "push" ? ["--push"] : []), "origin"];
        const native = spawnSync("git", args, { cwd: b, env, encoding: "utf8", timeout: 5_000, windowsHide: true });
        assert.equal(native.status, 0, native.stderr);
        assert.equal(native.stdout, padded + "\n", "native Git prints the complete payload followed by one record LF");
        assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, role + ": whitespace is still a foreign-network veto with shared history");
        assert.equal(gitOutput(args, b), padded, "the runner removes only Git's final record LF");
      }
      configure(a, [{ urls: [padded] }]); configure(d, [{ urls: [padded] }]);
      assert.equal(withConfirmedCloneRoots([d], [a]).confirmed.has(d), true, "identical complete payloads retain remote-only proof");
    }
  } finally { cleanup(); }
});

test("native Git whitespace remote names retain their selector and cannot hide a fork", () => {
  const { a, b, configure, mainUrl, cleanup } = remoteLimitFixture();
  try {
    configure(a, [{ urls: [mainUrl] }]);
    for (const name of [" origin ", "origin\r"]) {
      configure(b, [{ name: "origin", urls: [mainUrl] }, { name, urls: ["https://example.invalid/fork/repo.git"] }]);
      assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, "the exact selector must read the fork, not origin again");
      assert.ok(gitOutput(["remote"], b).split("\n").includes(name));
      configure(b, [{ name, urls: [mainUrl] }]);
      assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), true, "a valid whitespace selector still supports complete matching evidence");
      configure(b, [{ name, urls: [""] }]);
      assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, "an empty URL remains inconclusive even under a CR-bearing selector");
    }
  } finally { cleanup(); }
});

test("native Git rewrites to empty effective URLs reject the whole candidate or reference fingerprint", () => {
  const { a, b, d, configure, mainUrl, cleanup } = remoteLimitFixture();
  const foreign = "https://fork.invalid/repo";
  const env = { ...scrubGitEnv(process.env), GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_GLOBAL: "/dev/null", GIT_CONFIG_SYSTEM: "/dev/null" };
  const assertEmptyUrl = (repo, push = false) => {
    const args = ["remote", "get-url", ...(push ? ["--push"] : []), "--all", "origin"];
    const native = spawnSync("git", args, { cwd: repo, env, encoding: "utf8", timeout: 5_000, windowsHide: true });
    assert.equal(native.status, 0, native.stderr);
    assert.equal(native.stdout, "\n", "native Git permits a nonempty configured URL to rewrite to an empty effective URL");
    assert.throws(() => gitOutput(args, repo), error => error.code === "git_url_framing");
  };
  try {
    configure(a, [{ urls: [mainUrl] }]);
    for (const push of [false, true]) {
      configure(b, [{ urls: [push ? mainUrl : foreign], push: push ? [foreign] : [] }]);
      gitOutput(["config", "url..insteadOf", foreign], b);
      assertEmptyUrl(b, push);
      assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), false, "an empty effective URL cannot be mistaken for a local-only shared-history candidate");
    }
    configure(a, [{ urls: [mainUrl] }]);
    gitOutput(["config", "url..insteadOf", mainUrl], a);
    configure(b, [{ urls: [a] }]);
    configure(d, [{ urls: [mainUrl] }]);
    assertEmptyUrl(a);
    assert.equal(withConfirmedCloneRoots([b, d], [a]).confirmed.size, 0, "reject the reference's root commits as well as its remote proof");

    configure(a, [{ urls: [mainUrl] }]);
    configure(b, [{ urls: [foreign] }]);
    gitOutput(["config", `url.${mainUrl}.insteadOf`, foreign], b);
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), true, "ordinary nonempty repository-local rewrites remain valid evidence");
  } finally { cleanup(); }
});

test("whitespace-only native URL entries still consume the 64-entry remote allowance", () => {
  const { a, b, configure, mainUrl, cleanup } = remoteLimitFixture();
  try {
    configure(a, [{ urls: [mainUrl] }]);
    configure(b, [{ urls: [mainUrl], push: Array(63).fill(" \t ") }]);
    assert.equal(withConfirmedCloneRoots([b], [a]).confirmed.has(b), true, "64 complete entries remain within the allowance");
    configure(b, [{ urls: [mainUrl], push: Array(64).fill(" \t ") }]);
    const over = withConfirmedCloneRoots([b], [a]);
    assert.equal(over.confirmed.has(b), false);
    assert.equal(over.tooManyUrls, 1, "the 65th whitespace payload cannot disappear before counting");
  } finally { cleanup(); }
});

test("real Git remote URL limits preserve 64 entries but discard an overflowing candidate and its matching prefix", () => {
  const { a, d, make, configure, mainUrl, cleanup } = remoteLimitFixture();
  try {
    configure(a, [{ urls: [mainUrl] }]);
    const urls = Array(32).fill(mainUrl), push = Array(32).fill(mainUrl);
    configure(d, [{ urls, push }]);
    assert.equal(withConfirmedCloneRoots([d], [a]).confirmed.has(d), true, "64 observed fetch/push entries still prove the matching remote");
    configure(d, [{ urls: [...urls, mainUrl], push }]);
    const over = withConfirmedCloneRoots([d], [a]);
    assert.equal(over.confirmed.has(d), false, "65 matching entries must not be folded from a partial fingerprint");
    assert.equal(over.tooManyUrls, 1);
    const log = path.join(make(), "usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ session: "url-limit", cwd: d })) + "\n");
    const cli = runCliJson(log, a), summary = JSON.parse(cli.stdout);
    assert.equal(cli.status, 1, cli.stderr);
    assert.equal(summary.sources.cloneProbe.tooManyUrls, 1);
    assert.match(cli.stderr, /64 URL entries per remote.*256 per fingerprint/);
    assert.match(find(summary, 4).caveat, /64 URL entries per remote.*256 per fingerprint/);
    assert.doesNotMatch(cli.stderr, /folded into signal 4/);
    configure(d, [{ urls, push: [...push.slice(1), "https://example.invalid/fork/repo.git"] }]);
    const late = withConfirmedCloneRoots([d], [a]);
    assert.equal(late.confirmed.has(d), false, "the 64th URL still vetoes a matching prefix");
    assert.equal(late.tooManyUrls, 0);
    configure(d, [{ urls: [...urls, mainUrl], push: [...push.slice(1), "https://example.invalid/fork/repo.git"] }]);
    assert.equal(withConfirmedCloneRoots([d], [a]).confirmed.has(d), false, "an unmatched URL beyond the limit cannot be silently dropped");
  } finally { cleanup(); }
});

test("real Git fingerprints preserve 256 URL entries but reject a 257th across remote names", () => {
  const { a, d, configure, mainUrl, cleanup } = remoteLimitFixture();
  try {
    configure(a, [{ urls: [mainUrl] }]);
    const remotes = Array.from({ length: 4 }, (_, i) => ({ name: `r${i}`, urls: Array(32).fill(mainUrl), push: Array(32).fill(mainUrl) }));
    configure(d, remotes);
    assert.equal(withConfirmedCloneRoots([d], [a]).confirmed.has(d), true);
    configure(d, [...remotes, { name: "r4", urls: [mainUrl] }]);
    const over = withConfirmedCloneRoots([d], [a]);
    assert.equal(over.confirmed.has(d), false, "duplicates count toward the complete fingerprint's resource bound");
    assert.equal(over.tooManyUrls, 1);
  } finally { cleanup(); }
});

test("real Git main-root limits discard remote and shared-root-commit proof together", () => {
  const { a, b, d, make, configure, mainUrl, cleanup } = remoteLimitFixture();
  try {
    configure(d, [{ urls: [mainUrl] }]);
    const remotes = Array.from({ length: 16 }, (_, i) => ({ name: `r${i}`, urls: [mainUrl] }));
    configure(a, remotes);
    assert.deepEqual([...withConfirmedCloneRoots([b, d], [a]).confirmed.keys()], [b, d]);
    configure(a, [...remotes, { name: "r16", urls: [mainUrl] }]);
    const over = withConfirmedCloneRoots([b, d], [a]);
    assert.equal(over.confirmed.size, 0, "discarding an oversized main fingerprint must discard its root commits too");
    assert.equal(over.limitedMainRoots, 1);
    const log = path.join(make(), "usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ session: "main-limit", cwd: b })) + "\n");
    const cli = runCliJson(log, a), summary = JSON.parse(cli.stdout);
    assert.equal(summary.sources.cloneProbe.limitedMainRoots, 1);
    assert.match(cli.stderr, /1 main-root fingerprint.*discarded/);
    assert.match(find(summary, 4).caveat, /1 main-root fingerprint.*discarded/);
    // A single multiline value is ambiguous before line-count limits can classify its records.
    // Native NUL metadata rejects the whole reference, including its root history.
    configure(a, [{ urls: [Array(65).fill(mainUrl).join("\n")] }]);
    assert.equal(withConfirmedCloneRoots([b, d], [a]).confirmed.size, 0);
    assert.throws(() => gitOutput(["remote", "get-url", "origin"], a), error => error.code === "git_url_framing");
    const many = Array.from({ length: 4 }, (_, i) => ({ name: `r${i}`, urls: [Array(64).fill(mainUrl).join("\n")] }));
    configure(a, [...many, { name: "r4", urls: [mainUrl] }]);
    const total = withConfirmedCloneRoots([b, d], [a]);
    assert.equal(total.confirmed.size, 0);
    assert.throws(() => gitOutput(["remote", "get-url", "origin"], a), error => error.code === "git_url_framing");
  } finally { cleanup(); }
});

test("real Git command output is limited to 256 KiB on both sides of clone confirmation", () => {
  const { a, b, d, configure, mainUrl, cleanup } = remoteLimitFixture();
  try {
    const metadataOverhead = Buffer.byteLength("remote.origin.url\n\0");
    const prefix = mainUrl + "/", capacity = 256 * 1024 - metadataOverhead - Buffer.byteLength(prefix);
    const near = prefix + "é".repeat(Math.floor(capacity / 2)) + (capacity % 2 ? "x" : "");
    assert.equal(Buffer.byteLength(near) + metadataOverhead, 256 * 1024,
      "the complete native NUL metadata record sits exactly at the byte bound");
    configure(a, [{ urls: [near] }]); configure(d, [{ urls: [near] }]);
    assert.equal(gitOutput(["remote", "get-url", "origin"], a), near);
    assert.deepEqual([...withConfirmedCloneRoots([b, d], [a]).confirmed.keys()], [b, d]);
    const excessive = near + "xxxxxxxx";
    configure(a, [{ urls: [excessive] }]); configure(d, [{ urls: [excessive] }]);
    assert.throws(() => gitOutput(["remote", "get-url", "origin"], a), error => /ENOBUFS|MAXBUFFER/.test(error.code), "the child output bound must reject bytes before report parsing");
    const over = withConfirmedCloneRoots([b, d], [a]);
    assert.equal(over.confirmed.size, 0);
    assert.equal(over.limitedMainRoots, 1);
    assert.equal(over.outputTooLarge, 1);
  } finally { cleanup(); }
});

test("output limits from version, protected config and identity are disclosed for both fingerprint roles", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const home = make(), xdg = make(), system = path.join(make(), "empty.gitconfig");
  fs.writeFileSync(system, "");
  try {
    const results = runGitHarness(make, { a, b, home, xdg, system, foreign: false }, (trial, fs, stateFile, settings) => {
      return ["version", "config", "identity"].map(outputErrorPhase => {
        fs.writeFileSync(stateFile, JSON.stringify({ ...settings, outputErrorPhase }));
        globalThis.resetGitHarness();
        const result = trial.withConfirmedCloneRoots([settings.b], [settings.a]);
        return { phase: outputErrorPhase, ...result, confirmed: [...result.confirmed.keys()] };
      });
    });
    for (const result of results) {
      assert.deepEqual(result.confirmed, [], result.phase);
      assert.equal(result.limitedMainRoots, 1, result.phase);
      assert.equal(result.outputTooLarge, 1, result.phase);
    }
  } finally { cleanup(); }
});

test("CLI exposes unsupported Git and version-read failures without probing or folding clones", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const log = path.join(make(), "usage.jsonl");
  const trace = path.join(make(), "git-calls.jsonl");
  const preload = path.join(make(), "git-version.cjs");
  try {
    fs.writeFileSync(log, JSON.stringify(row({ session: "clone", cwd: b })) + "\n");
    for (const versionReply of ["git version 2.37.4\n", "unrecognized\n", null]) {
      fs.writeFileSync(trace, "");
      fs.writeFileSync(preload, [
        'const fs = require("node:fs"), cp = require("node:child_process");',
        'const original = cp.execFileSync;',
        'cp.execFileSync = function (file, args, options) {',
        '  if (!/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) return original.apply(this, arguments);',
        '  fs.appendFileSync(' + JSON.stringify(trace) + ', JSON.stringify(args) + "\\n");',
        '  if (args[0] === "--version") {',
        versionReply === null ? '    throw Object.assign(new Error("version read failed"), { status: 1 });' : '    return Buffer.from(' + JSON.stringify(versionReply) + ');',
        '  }',
        '  return original.apply(this, arguments);',
        '};',
        'require("node:module").syncBuiltinESMExports();'
      ].join("\n"));
      const result = runCliJson(log, a, [], ["--require", preload]);
      assert.equal(result.status, 1, result.stderr + result.stdout);
      assert.match(result.stderr, /Git 2\.39 or newer.*required/i);
      assert.doesNotMatch(result.stderr, /folded into signal 4/);
      const summary = JSON.parse(result.stdout);
      assert.match(find(summary, 4).caveat, /Git 2\.39 or newer.*required/i);
      assert.equal(summary.sources.cloneProbe, null);
      assert.equal(find(summary, 4).verdict, "UNMEASURED");
      const calls = fs.readFileSync(trace, "utf8").trim().split(/\r?\n/).map(line => JSON.parse(line));
      assert.ok(calls.length > 0);
      assert.ok(calls.every(args => args.length === 1 && args[0] === "--version"), "no config capture or identity probe may follow unsupported Git");
    }
  } finally { cleanup(); }
});
const escapeRe = text => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
function quotedValues(text) {
  return quotedLiterals(text).map(value => JSON.parse(value));
}
function quotedLiterals(text) {
  return [...text.matchAll(/"(?:[^"\\]|\\.)*"/g)].map(match => match[0]);
}

test("a deleted logged cwd does not poison valid clone probes or a trusted measured boundary", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const gone = path.join(b, "deleted-session-cwd"), log = path.join(make(), "usage.jsonl");
  try {
    fs.mkdirSync(gone);
    assert.equal(real(gitOutput(["rev-parse", "--show-toplevel"], gone)), real(b));
    fs.rmdirSync(gone);
    fs.writeFileSync(log, [
      row({ ts: "2026-11-01T00:00:02Z", session: "gone", cwd: gone }),
      row({ ts: "2026-11-01T00:00:01Z", session: "valid", cwd: b })
    ].map(value => JSON.stringify(value)).join("\n") + "\n");
    const child = runCliJson(log, a), summary = JSON.parse(child.stdout);
    assert.equal(child.status, 1, child.stderr);
    assert.equal(summary.repoRootSource, "override");
    assert.equal(find(summary, 4).verdict, "ADOPT", "only the missing cwd remains outside; the available log and boundary remain measurable");
    assert.equal(summary.sources.cloneProbe.probed, 2);
    assert.match(child.stderr, /1 of 2 outside cwd\(s\) confirmed/);
    assert.doesNotMatch(child.stderr, /Git 2\.39 or newer is required/);
    assert.throws(() => gitOutput(["rev-parse", "--show-toplevel"], gone), error => error.code === "git_cwd_missing" && /ENOENT|ENOTDIR/.test(error.cause?.code));
  } finally { cleanup(); }
});

test("a missing Git binary in an existing cwd remains a compatibility failure", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  try {
    assert.equal(real(gitOutput(["rev-parse", "--show-toplevel"], a)), real(a));
    const env = scrubGitEnv(process.env);
    for (const key of Object.keys(env)) if (/^path$/i.test(key)) delete env[key];
    env.PATH = make();
    assert.equal(fs.statSync(a).isDirectory(), true);
    assert.throws(() => gitOutput(["rev-parse", "--show-toplevel"], a, { env }), error => {
      assert.equal(error.code, "git_unsupported");
      assert.equal(error.cause?.code, "ENOENT");
      return true;
    });
    const log = path.join(make(), "usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ cwd: b })) + "\n");
    const child = spawnSync(process.execPath, [path.join(HERE, "trial-report.mjs"), "--repo-root", a, "--json"], {
      cwd: a, encoding: "utf8", windowsHide: true, timeout: 30_000,
      env: { ...env, KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" }
    });
    assert.equal(child.status, 1, child.stderr);
    const summary = JSON.parse(child.stdout);
    assert.equal(find(summary, 4).verdict, "UNMEASURED");
    assert.equal(summary.sources.cloneProbe, null);
    assert.equal((child.stderr.match(/Git 2\.39 or newer is required/g) ?? []).length, 1);
  } finally { cleanup(); }
});

function versionFailurePreload(dir, failCwd, failure, trace) {
  const preload = path.join(dir, "version-failure.cjs");
  fs.writeFileSync(preload, [
    'const fs = require("node:fs"), path = require("node:path"), cp = require("node:child_process"), original = cp.execFileSync;',
    'cp.execFileSync = function (file, args, options) {',
    `  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) fs.appendFileSync(${JSON.stringify(trace)}, JSON.stringify({ args, cwd: options.cwd }) + "\\n");`,
    `  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file))) && args[0] === "--version" && path.resolve(options.cwd) === ${JSON.stringify(path.resolve(failCwd))})`,
    `    throw Object.assign(new Error("controlled native version failure"), ${JSON.stringify(failure)});`,
    '  return original.apply(this, arguments);',
    '};',
    'require("node:module").syncBuiltinESMExports();'
  ].join("\n"));
  return preload;
}

test("candidate version failures stay local for recorded and resolved working directories", () => {
  const { a, b, d, make, cleanup } = buildCloneFixture();
  const dir = make(), log = path.join(dir, "usage.jsonl"), trace = path.join(dir, "trace.jsonl");
  const subdir = path.join(b, "recorded-subdir");
  fs.mkdirSync(subdir);
  try {
    for (const failure of [{ code: "ETIMEDOUT", signal: "SIGTERM" }, { code: "ETIMEDOUT", status: 0, signal: null },
      { code: "EACCES" }, { code: "EPERM" }, { signal: "SIGTERM", status: null }, { signal: "SIGTERM", status: 0 }]) {
      for (const failedCwd of [real(subdir), real(b)]) {
        const orders = failure.code === "ETIMEDOUT" && failedCwd === real(b) ? [[subdir, d], [d, subdir]] : [[subdir, d]];
        for (const order of orders) {
          fs.writeFileSync(trace, "");
          fs.writeFileSync(log, order.map((cwd, index) => JSON.stringify(row({ cwd, session: cwd,
            ts: `2026-11-01T00:00:0${2 - index}Z` }))).join("\n") + "\n");
          const preload = versionFailurePreload(dir, failedCwd, failure, trace);
          const child = runCliJson(log, a, [], ["--require", preload]);
          assert.equal(child.status, 1, child.stderr);
          const summary = JSON.parse(child.stdout);
          assert.equal(find(summary, 4).verdict, "ADOPT", "one candidate failure cannot make the trusted boundary globally unavailable");
          assert.equal(summary.sources.cloneProbe.probed, 2);
          assert.deepEqual(find(summary, 4).detail.flatMap(detail => detail.cwds), [subdir], "the failed candidate stays outside and the healthy one is folded");
          assert.match(child.stderr, /1 of 2 outside cwd\(s\) confirmed/);
          assert.doesNotMatch(child.stderr, /Git 2\.39 or newer is required/);
          const calls = fs.readFileSync(trace, "utf8").trim().split("\n").map(line => JSON.parse(line));
          assert.ok(calls.some(call => path.resolve(call.cwd) === failedCwd && call.args[0] === "--version"), "the selected failure must actually be reached");
          assert.ok(calls.filter(call => path.resolve(call.cwd) === failedCwd).every(call => call.args[0] === "--version"),
            "a failed version read cannot authorize config or identity commands at that cwd");
          assert.ok(calls.some(call => path.resolve(call.cwd) === path.resolve(d) && call.args[0] === "rev-parse"), "healthy commands still delegate to the installed Git");
        }
      }
    }
  } finally { cleanup(); }
});

test("reference version failures disable the report with and without an explicit root", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const dir = make(), log = path.join(dir, "usage.jsonl"), trace = path.join(dir, "trace.jsonl");
  try {
    fs.writeFileSync(log, JSON.stringify(row({ cwd: b })) + "\n");
    for (const failure of [{ code: "ETIMEDOUT", signal: "SIGTERM" }, { code: "ETIMEDOUT", status: 0, signal: null },
      { code: "EACCES" }, { code: "EPERM" }, { signal: "SIGTERM", status: null }, { signal: "SIGTERM", status: 0 }]) {
      for (const explicit of failure.code === "ETIMEDOUT" ? [true, false] : [true]) {
        fs.writeFileSync(trace, "");
        const preload = versionFailurePreload(dir, explicit ? a : HERE, failure, trace);
        const child = spawnSync(process.execPath, ["--require", preload, path.join(HERE, "trial-report.mjs"),
          ...(explicit ? ["--repo-root", a] : []), "--json"], {
          cwd: a, encoding: "utf8", windowsHide: true, timeout: 30_000,
          env: { ...scrubGitEnv(process.env), KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" }
        });
        assert.equal(child.status, 1, child.stderr);
        const summary = JSON.parse(child.stdout);
        assert.equal(find(summary, 4).verdict, "UNMEASURED");
        assert.equal(summary.sources.cloneProbe, null);
        const calls = fs.readFileSync(trace, "utf8").trim().split("\n").map(line => JSON.parse(line));
        assert.equal(calls.length, 1, "reference unavailability latches before later source or candidate commands");
        assert.deepEqual(calls[0].args, ["--version"]);
        assert.doesNotMatch(child.stderr, /folded into signal 4/);
      }
    }
  } finally { cleanup(); }
});

test("late Git incompatibility discards earlier clone confirmations and folded notes", () => {
  const { a, b, d, make, cleanup } = buildCloneFixture();
  const log = path.join(make(), "usage.jsonl"), trace = path.join(make(), "trace.jsonl"), preload = path.join(make(), "late-version.cjs");
  try {
    fs.writeFileSync(log, [
      row({ ts: "2026-11-01T00:00:02Z", session: "valid", cwd: b }),
      row({ ts: "2026-11-01T00:00:01Z", session: "later", cwd: d })
    ].map(value => JSON.stringify(value)).join("\n") + "\n");
    for (const version of ["git version 2.38.9", "unknown Git"]) {
      fs.writeFileSync(trace, "");
      fs.writeFileSync(preload, [
        'const fs = require("node:fs"), cp = require("node:child_process"), original = cp.execFileSync;',
        'cp.execFileSync = function (file, args, options) {',
        `  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) fs.appendFileSync(${JSON.stringify(trace)}, JSON.stringify({ args, cwd: options.cwd }) + "\\n");`,
        `  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file))) && args[0] === "--version" && options.cwd === ${JSON.stringify(d)}) return Buffer.from(${JSON.stringify(version + "\n")});`,
        '  return original.apply(this, arguments);',
        '};',
        'require("node:module").syncBuiltinESMExports();'
      ].join("\n"));
      const child = runCliJson(log, a, [], ["--require", preload]), summary = JSON.parse(child.stdout);
      assert.equal(child.status, 1, child.stderr);
      assert.equal(find(summary, 4).verdict, "UNMEASURED");
      assert.equal(summary.sources.cloneProbe, null);
      assert.doesNotMatch(child.stderr, /folded into signal 4/);
      assert.doesNotMatch(find(summary, 4).caveat, /outside cwd\(s\) were confirmed as separate clones/);
      assert.deepEqual(find(summary, 4).detail.flatMap(entry => entry.cwds).sort(), [b, d].sort(), "unmeasured evidence must retain both unconfirmed cwd strings");
      const calls = fs.readFileSync(trace, "utf8").trim().split(/\r?\n/).map(line => JSON.parse(line));
      const accepted = calls.findIndex(call => call.cwd === b && call.args.includes("rev-list"));
      const incompatible = calls.findIndex(call => call.cwd === d && call.args[0] === "--version");
      assert.ok(accepted >= 0 && incompatible > accepted, "the control must confirm a valid clone before the later failure");
    }
  } finally { cleanup(); }
});

test("CLI checks Git compatibility before each operation and leaves old or unknown versions unmeasured", () => {
  const { a, b, d, make, cleanup } = buildCloneFixture();
  try {
    const dir = make();
    const log = path.join(dir, "mcp-usage.jsonl");
    const trace = path.join(dir, "git-calls.jsonl");
    const preload = path.join(dir, "version.cjs");
    fs.writeFileSync(log, [b, d].map(cwd => JSON.stringify(row({ cwd, session: cwd }))).join("\n") + "\n");
    fs.writeFileSync(preload, [
      'const fs = require("node:fs"), cp = require("node:child_process");',
      'const original = cp.execFileSync;',
      'cp.execFileSync = function (file, args, options) {',
      `  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) fs.appendFileSync(${JSON.stringify(trace)}, JSON.stringify(args) + "\\n");`,
      '  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file))) && args.length === 1 && args[0] === "--version") return Buffer.from(process.env.KBF_TEST_VERSION + "\\n");',
      '  return original.apply(this, arguments);',
      '};',
      'require("node:module").syncBuiltinESMExports();'
    ].join("\n"));
    for (const [version, supported] of [["git version 2.37.9", false], ["git version 2.38.4", false], ["unknown Git", false], ["git version 2.39.0", true]]) {
      fs.writeFileSync(trace, "");
      const child = spawnSync(process.execPath, ["--require", preload, path.join(HERE, "trial-report.mjs"), "--repo-root", a, "--json"], {
        cwd: a, encoding: "utf8", windowsHide: true, timeout: 30_000,
        env: { ...scrubGitEnv(process.env), KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1", KBF_TEST_VERSION: version }
      });
      assert.equal(child.status, 1, child.stdout + child.stderr);
      const calls = fs.readFileSync(trace, "utf8").trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
      const versionCalls = calls.filter(args => args.length === 1 && args[0] === "--version").length;
      const summary = JSON.parse(child.stdout);
      if (supported) {
        assert.ok(versionCalls > 1, "each operation checks its selected Git");
        const urlReads = calls.filter(args => args[args.indexOf("remote") + 1] === "get-url").length;
        const metadataReads = calls.filter(args => args.includes("--get-regexp")).length;
        assert.equal(metadataReads, urlReads, "every URL operation validates native NUL metadata");
        assert.equal(calls.length, versionCalls * 3 + urlReads, "URL operations add one metadata read within their original budget");
        assert.equal(find(summary, 4).verdict, "REVERT");
        assert.equal(summary.sources.cloneProbe.probed, 2);
        assert.ok(calls.length > 4, "the real identity probes must run after the supported-version response");
      } else {
        assert.equal(versionCalls, 1, "one compatibility failure stops the report's probes");
        assert.deepEqual(calls, [["--version"]], "unsupported Git must never read config or identity");
        assert.equal(find(summary, 4).verdict, "UNMEASURED");
        assert.equal(summary.sources.cloneProbe, null);
        assert.equal((child.stderr.match(/Git 2\.39 or newer is required/g) ?? []).length, 1, "show one actionable compatibility diagnostic");
      }
    }
  } finally {
    cleanup();
  }
});

test("a report runner freezes its environment while rereading protected configuration", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  try {
    const dir = make();
    const config = path.join(dir, "global.gitconfig");
    const system = path.join(dir, "system.gitconfig");
    const otherConfig = path.join(dir, "unused.gitconfig");
    const preload = path.join(dir, "observe.cjs");
    fs.writeFileSync(system, "");
    fs.writeFileSync(otherConfig, "[safe]\n\tdirectory = *\n");
    const setting = value => "[safe]\n\tdirectory = " + JSON.stringify(value.replaceAll("\\", "/")) + "\n";
    fs.writeFileSync(config, setting(a));
    fs.writeFileSync(preload, [
      'const cp = require("node:child_process"), original = cp.execFileSync;',
      'globalThis.gitCalls = [];',
      'cp.execFileSync = function (file, args, options) {',
      '  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) globalThis.gitCalls.push({ args, system: options.env.GIT_CONFIG_SYSTEM, global: options.env.GIT_CONFIG_GLOBAL });',
      '  return original.apply(this, arguments);',
      '};',
      'require("node:module").syncBuiltinESMExports();'
    ].join("\n"));
    const script = [
      'import fs from "node:fs";',
      `import { createGitRunner } from ${JSON.stringify(new URL("./trial-report.mjs", import.meta.url).href)};`,
      'const env = { ...process.env }; const git = createGitRunner({ env });',
      `const first = git(["config", "--get-all", "safe.directory"], ${JSON.stringify(a)});`,
      `fs.writeFileSync(${JSON.stringify(config)}, ${JSON.stringify(setting(b))});`,
      `env.GIT_CONFIG_GLOBAL = ${JSON.stringify(otherConfig)}; env.PATH = "";`,
      `const second = git(["config", "--get-all", "safe.directory"], ${JSON.stringify(a)});`,
      'console.log(JSON.stringify({ first, second, calls: globalThis.gitCalls }));'
    ].join("\n");
    const child = spawnSync(process.execPath, ["--require", preload, "--input-type=module", "--eval", script], {
      cwd: a, encoding: "utf8", windowsHide: true, timeout: 30_000,
      env: { ...scrubGitEnv(process.env), GIT_CONFIG_GLOBAL: config, GIT_CONFIG_SYSTEM: system }
    });
    assert.equal(child.status, 0, child.stderr);
    const result = JSON.parse(child.stdout);
    assert.equal(result.first, a.replaceAll("\\", "/"));
    assert.equal(result.second, b.replaceAll("\\", "/"), "the second command sees the changed protected file");
    assert.equal(result.calls.filter(call => call.args[0] === "--version").length, 2);
    assert.equal(result.calls.filter(call => call.args[0] === "config" && call.args.includes("--show-scope")).length, 2);
    const identities = result.calls.filter(call => call.args.includes("-c"));
    assert.equal(identities.length, 2);
    assert.ok(identities.every(call => call.system === "/dev/null" && call.global === "/dev/null"),
      "identity reads also isolate protected system configuration");
  } finally {
    cleanup();
  }
});

test("real CLI quotes a recorded clone path with Markdown syntax and a line break on both surfaces", () => {
  const { a, make, cleanup } = buildCloneFixture();
  const lineBreak = process.platform === "win32" ? "\u2028" : "\n";
  const clone = path.join(make(), "clone [```plan```](fake)" + lineBreak + "## INJECTED VERDICT ADOPT");
  try {
    gitOutput(["clone", "-q", a, clone], a);
    const log = path.join(make(), "mcp-usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ cwd: clone })) + "\n");
    const json = runCliJson(log, a);
    assert.equal(json.status, 1, json.stdout + json.stderr);
    const signal = find(JSON.parse(json.stdout), 4);
    assert.equal(signal.detail.length, 0, "the real clone must still fold in by its recorded cwd");
    assert.doesNotMatch(signal.caveat, /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u);
    const caveatLiteral = quotedLiterals(signal.caveat).find(value => JSON.parse(value) === clone);
    assert.ok(caveatLiteral);
    assert.doesNotMatch(caveatLiteral, /[`*_\[\]<>|&~#!]/u);
    const note = json.stderr.split(/\r?\n/).find(line => line.includes("folded into signal 4 as inside"));
    assert.ok(note, "the actual CLI must disclose its confirmed clone");
    assert.doesNotMatch(note, /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u);
    const noteLiteral = quotedLiterals(note).find(value => JSON.parse(value) === clone);
    assert.ok(noteLiteral);
    assert.doesNotMatch(noteLiteral, /[`*_\[\]<>|&~#!]/u);
    assert.doesNotMatch(json.stderr, /^## INJECTED VERDICT/m);

    const env = { ...scrubGitEnv(process.env), KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" };
    const markdown = spawnSync(process.execPath, [path.join(HERE, "trial-report.mjs"), "--repo-root", a], {
      env, encoding: "utf8", windowsHide: true, timeout: 20_000
    });
    assert.equal(markdown.status, 1, markdown.stdout + markdown.stderr);
    assert.doesNotMatch(markdown.stdout, /^## INJECTED VERDICT/m);
    assert.ok(quotedValues(markdown.stdout).includes(clone));
    assert.doesNotMatch(markdown.stderr, /^## INJECTED VERDICT/m);
  } finally {
    cleanup();
  }
});

test("CLI integration: --repo-root a separates a clone (b) from a different project (c), --since excludes a stale clone (e)", () => {
  // main()'s own wiring has NO other test: removing it entirely still left every prior test green,
  // because every CLI-spawning test's fixture cwd does not exist on disk (fakeGitByCwd/plain string
  // fixtures never touch the filesystem). This spawns the REAL trial-report.mjs against a REAL log
  // and REAL repositories, so the wiring itself — not just the pure functions it calls — is proven.
  const { a, b, c, make, cleanup } = buildCloneFixture();
  try {
    // A second, genuine clone of a — confirmable exactly like b — but its ONLY row predates --since.
    // selectCloneCandidates (finding #2, prior commit) must exclude it from the git-probing candidate
    // set entirely, so it is never even ASKED about, let alone confirmed and folded in: it must not
    // appear in stderr's NOTE or the JSON caveat's "folded in" list. Made through the fixture's own
    // `make`, inside this try, so a clone that throws cannot leak it or the fixture it came from.
    const e = make();
    gitOutput(["clone", "-q", a, e], a);
    const log = path.join(make(), "mcp-usage.jsonl");
    const since = "2026-09-10T00:00:00.000Z";
    fs.writeFileSync(log, [
      JSON.stringify(row({ session: "stale-clone", cwd: e, ts: "2026-09-01T00:00:00.000Z" })), // BEFORE --since
      JSON.stringify(row({ session: "review-sandbox", cwd: b, ts: "2026-09-15T00:00:00.000Z" })), // in window
      JSON.stringify(row({ session: "genuinely-external", cwd: c, ts: "2026-09-15T00:00:00.000Z" })) // in window
    ].join("\n") + "\n");

    const result = runCliJson(log, a, ["--since", since]);
    assert.equal(result.status, 1, `expected INCOMPLETE (activity port is dead); got:\n${result.stdout}\n${result.stderr}`);

    const summary = JSON.parse(result.stdout);
    const s4 = summary.signals.find(s => s.id === 4);
    assert.equal(s4.detail.length, 1, "only the genuinely different, IN-WINDOW project (c) counts as outside");
    assert.equal(s4.detail[0].session, "genuinely-external");
    assert.ok(quotedValues(result.stderr).includes(b), "stderr must name b as a confirmed clone");

    assert.ok(!quotedValues(result.stderr).includes(e), "e is out of window — it must never be probed, let alone named as confirmed");
    assert.ok(!quotedValues(s4.caveat).includes(e), "the caveat's folded-in list must not name a candidate --since excluded from probing");

    // Probe counts (probed/capped/candidates), threaded through cloneProbe into the JSON output —
    // only b and c were ever candidates; e was excluded (by --since, finding #2) before either was
    // probed, so the count is 2, not 3.
    assert.deepEqual(summary.sources.cloneProbe, { probed: 2, capped: false, candidates: 2, budgetSpent: false, tooManyRemotes: 0,
      tooManyUrls: 0, outputTooLarge: 0, limitedMainRoots: 0 });
    assert.equal(summary.sources.cloneProbeCapped, false);
  } finally {
    cleanup();
  }
});

test("CLI integration: unavailable or corrupt usage logs skip clone probes and folded-in notes", () => {
  const { a, b, make, cleanup } = buildCloneFixture();
  const dir = make();
  const log = path.join(dir, "mcp-usage.jsonl");
  const trace = path.join(dir, "git-calls.jsonl");
  const preload = path.join(dir, "trace-git.cjs");
  try {
    // Observe the real CLI's Git boundary without replacing its answers. --repo-root resolves
    // without Git, so every recorded call belongs to clone confirmation.
    fs.writeFileSync(preload, [
      'const fs = require("node:fs");',
      'const cp = require("node:child_process");',
      'const original = cp.execFileSync;',
      'cp.execFileSync = function (file, args, options) {',
      '  if (/^(?:git|git[.](?:exe|com))$/i.test(require("node:path").basename(String(file)))) fs.appendFileSync(' + JSON.stringify(trace) + ', JSON.stringify({ args, cwd: options?.cwd }) + "\\n");',
      '  return original.apply(this, arguments);',
      '};',
      'require("node:module").syncBuiltinESMExports();'
    ].join("\n"));
    const run = (usageLog = log) => {
      fs.writeFileSync(trace, "");
      const result = runCliJson(usageLog, a, [], ["--require", preload]);
      assert.equal(result.status, 1, result.stdout + result.stderr);
      const calls = fs.readFileSync(trace, "utf8").split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
      return { result, calls, summary: JSON.parse(result.stdout) };
    };
    const assertUnavailable = (warning, usageLog = log) => {
      const { result, calls, summary } = run(usageLog);
      assert.equal(summary.sources.log, false);
      assert.equal(find(summary, 4).verdict, "UNMEASURED");
      assert.match(result.stderr, warning);
      assert.deepEqual(calls, [], "an unavailable usage log must not cause even a Git fingerprint or clone probe");
      assert.equal(summary.sources.cloneProbe, null, "clone confirmation did not run");
      assert.doesNotMatch(result.stderr, /folded into signal 4 as inside/);
      return summary;
    };

    // A usable log may contain a torn line. Keep scoring its good rows and prove the observer
    // sees actual probes before using an empty trace as evidence that probing was skipped.
    fs.writeFileSync(log, JSON.stringify(row({ session: "review-sandbox", cwd: b })) + "\n{torn\n");
    const control = run();
    assert.equal(control.summary.sources.log, true);
    assert.equal(find(control.summary, 4).verdict, "REVERT");
    assert.equal(control.summary.sources.cloneProbe.probed, 1);
    assert.ok(control.calls.some(call => call.cwd === b && call.args.includes("rev-parse")));
    assert.match(control.result.stderr, /folded into signal 4 as inside/);

    // A directory is discovered as an archive but cannot be read as a generation. The good
    // live row remains visible as partial evidence; it cannot justify clone confirmation.
    fs.mkdirSync(log + ".1");
    const partial = assertUnavailable(/1 of 2 usage-log generation\(s\) could be read; 1 UNREADABLE/);
    assert.deepEqual(find(partial, 4).detail.map(detail => detail.session), ["review-sandbox"]);
    fs.rmdirSync(log + ".1");

    fs.writeFileSync(log, "{torn\n{also-torn\n");
    assertUnavailable(/every one of the 2 line\(s\).*unparseable/);
    fs.unlinkSync(log);
    assertUnavailable(/no usage log .* yet/);
    assertUnavailable(/usage logging is disabled/, "off");
  } finally {
    cleanup();
  }
});

test("CLI integration: a checkout listing 17 remotes stays outside, and both the NOTE and the caveat say why", () => {
  // Real git, the real CLI: the per-candidate remote bound, end to end. Every one of the 17 remotes
  // names a's own URL, so the bound — not a veto — is what keeps this checkout outside.
  const fx = buildInto(make => {
    const a = make();
    initRealRepo(a, "a.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], a);
    const many = make();
    gitOutput(["clone", "-q", a, many], a);
    gitOutput(["remote", "set-url", "origin", "https://example.invalid/org/repo.git"], many);
    for (let i = 1; i <= 16; i++) gitOutput(["remote", "add", `r${i}`, "https://example.invalid/org/repo.git"], many);
    return { a, many, log: path.join(make(), "mcp-usage.jsonl") };
  });
  try {
    assert.equal(gitOutput(["remote"], fx.many).split(/\r?\n/).length, 17, "fixture sanity: 17 remotes");
    fs.writeFileSync(fx.log, JSON.stringify(row({ session: "many-remotes", cwd: fx.many, ts: "2026-09-15T00:00:00.000Z" })) + "\n");
    const result = runCliJson(fx.log, fx.a);
    assert.equal(result.status, 1, `expected INCOMPLETE (activity port is dead); got:\n${result.stdout}\n${result.stderr}`);
    const summary = JSON.parse(result.stdout);
    const s4 = summary.signals.find(s => s.id === 4);
    assert.deepEqual(s4.detail.map(d => d.session), ["many-remotes"], "17 remotes: inconclusive, so outside");
    assert.match(result.stderr, /NOTE: 1 checked cwd\(s\) listed more than 16 remotes/);
    assert.match(s4.caveat, /1 checked cwd\(s\) listed more than 16 remotes/);
    assert.equal(summary.sources.cloneProbe.tooManyRemotes, 1);
  } finally {
    fx.cleanup();
  }
});

// --- 5f. A confirmed clone is folded in as ITS OWN CWD, never as its toplevel ---------------
//
// main() used to fold each confirmed cwd's TOPLEVEL in as an extra prefix root, which scored every
// cwd lexically under a confirmed clone as inside — including a repository nested in it that
// withConfirmedCloneRoots had just REJECTED. The same fold-in failed the other way for a clone
// reached through a junction: git reports the junction's TARGET as the toplevel, which is not a
// prefix of the cwd the log recorded, so the run said the cwd was folded in as inside and still
// scored it outside. Neither shows up in `summarize` alone, which folds in whatever it is handed,
// so both run end to end: the real CLI, real git, a real log. The last test here pins the other
// half of "its own cwd": its own STRING, exactly as recorded, never a case-insensitive match.

test("a repository NESTED inside a confirmed clone, and rejected on its own, stays outside", () => {
  const base = tmp();
  try {
    const a = path.join(base, "a");
    fs.mkdirSync(a);
    initRealRepo(a, "a.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], a);
    // X: a genuine clone of a, confirmable by root commit (its origin is a's local path).
    const X = path.join(base, "X");
    gitOutput(["clone", "-q", a, X], base);
    // vendor-other: an UNRELATED repository living inside X's working tree — its own history, and a
    // network remote naming a different project.
    const nested = path.join(X, "vendor-other");
    fs.mkdirSync(nested);
    initRealRepo(nested, "n.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/other/thing.git"], nested);
    // Fixture sanity: git must see vendor-other as its OWN repository. A plain subdirectory of X would
    // rightly confirm through X, and this test would then prove nothing.
    assert.equal(real(gitOutput(["rev-parse", "--show-toplevel"], nested)), real(nested));

    const log = path.join(base, "mcp-usage.jsonl");
    fs.writeFileSync(log, [
      JSON.stringify(row({ session: "review-sandbox", cwd: X, ts: "2026-09-15T10:00:00.000Z" })),
      JSON.stringify(row({ session: "genuinely-external", cwd: nested, ts: "2026-09-15T10:00:00.000Z" }))
    ].join("\n") + "\n");

    const result = runCliJson(log, a);
    assert.equal(result.status, 1, `expected INCOMPLETE (activity port is dead); got:\n${result.stdout}\n${result.stderr}`);
    const s4 = JSON.parse(result.stdout).signals.find(s => s.id === 4);
    assert.deepEqual(s4.detail.map(d => d.session), ["genuinely-external"],
      "X is a confirmed clone and folds in; vendor-other was REJECTED and must stay outside, however it is nested");
    assert.equal(s4.verdict, "ADOPT", "the rejected repository is still outside use — the verdict must not flip to REVERT");
    // The disclosure agrees with the scoring: X is named as folded in, vendor-other is named nowhere.
    assert.match(s4.caveat, /folded in as inside/);
    assert.ok(quotedValues(s4.caveat).includes(X));
    assert.ok(!quotedValues(s4.caveat).includes(nested), "a rejected cwd must not be listed as folded in");
    assert.ok(!quotedValues(result.stderr).includes(nested), "nor named in the NOTE as confirmed");
  } finally {
    fs.rmSync(base, { recursive: true, force: true });
  }
});

test("a clone reached through a JUNCTION is scored inside, and the disclosure names the cwd it scored", () => {
  const base = tmp();
  const J = path.join(base, "J");
  try {
    const a = path.join(base, "a");
    fs.mkdirSync(a);
    initRealRepo(a, "a.txt");
    gitOutput(["remote", "add", "origin", "https://example.invalid/org/repo.git"], a);
    // X: a genuine clone of a. The junction's TARGET stays inside this test's own base.
    const X = path.join(base, "X");
    gitOutput(["clone", "-q", a, X], base);
    fs.symlinkSync(X, J, "junction");
    // A real link, never a copy: a copied directory is its own toplevel, and would pass this test
    // without ever exercising a junction.
    assert.ok(fs.lstatSync(J).isSymbolicLink(), "J must be a junction, not a copy of X");
    // Fixture sanity: git resolves the junction, so J's toplevel is X — the shape that let a toplevel
    // fold-in miss the cwd the log actually recorded.
    const jTop = gitOutput(["rev-parse", "--show-toplevel"], J);
    assert.equal(real(jTop), real(X));
    // Physical paths on both sides would pass even if git reported J itself; the spelling it
    // reports is what shows it resolved the junction.
    assert.notEqual(path.resolve(jTop), path.resolve(J), "git reports the junction's target, not the junction");

    const log = path.join(base, "mcp-usage.jsonl");
    fs.writeFileSync(log, JSON.stringify(row({ session: "review-sandbox-via-junction", cwd: J, ts: "2026-09-15T10:00:00.000Z" })) + "\n");

    const result = runCliJson(log, a);
    assert.equal(result.status, 1, `expected INCOMPLETE (activity port is dead); got:\n${result.stdout}\n${result.stderr}`);
    const s4 = JSON.parse(result.stdout).signals.find(s => s.id === 4);
    assert.deepEqual(s4.detail, [], "J is a confirmed clone of a, reached through a junction: it must be scored inside");
    // The disclosure must describe the scoring, not just the confirmation: it names J, the cwd that
    // was actually folded in.
    assert.match(s4.caveat, /folded in as inside/);
    assert.ok(quotedValues(s4.caveat).includes(J), "the caveat must name the cwd it folded in");
    assert.ok(quotedValues(result.stderr).includes(J), "and so must the NOTE");
  } finally {
    // The junction goes FIRST, as a statement of its own: if removing it throws, the recursive delete
    // below never runs, so a recursive walk is never the thing that meets a junction.
    let link = null;
    try { link = fs.lstatSync(J); } catch { /* never made: construction failed before the junction */ }
    if (link?.isSymbolicLink()) fs.unlinkSync(J);
    fs.rmSync(base, { recursive: true, force: true });
  }
});

test("a confirmed cwd folds in by its EXACT string — a sibling that differs only in case, rejected on its own, stays outside", () => {
  // In a per-directory case-SENSITIVE NTFS directory (`fsutil file setCaseSensitiveInfo`), `cs\KF`
  // and `cs\kf` are two different directories. A case-insensitive path comparison folded in the
  // rejected one because its sibling was confirmed, and the caveat — naming only the confirmed one —
  // then disagreed with the scoring. Exact string costs nothing below the probe cap: candidates are
  // de-duplicated and probed by exact string, so every spelling signal 4 scores is checked itself.
  const mainRoot = path.resolve("C:\\real\\KanbanFlow-v2");
  const confirmedCwd = path.resolve("C:\\cs\\KF");
  const siblingCwd = path.resolve("C:\\cs\\kf");
  const git = fakeGitByCwd({
    [mainRoot]: { "remote": "origin", "rev-list --max-parents=0 HEAD": "aaa111", "remote get-url origin": "https://github.com/joel/kanbanflow-v2.git" },
    // KF: a genuine clone — no remote at all, and this repository's root commit.
    [confirmedCwd]: { "remote": "", "rev-parse --show-toplevel": confirmedCwd, "rev-list --max-parents=0 HEAD": "aaa111" },
    // kf: an unrelated repository — its own history, and another project's remote.
    [siblingCwd]: {
      "remote": "origin", "rev-parse --show-toplevel": siblingCwd, "rev-list --max-parents=0 HEAD": "zzz999",
      "remote get-url origin": "https://github.com/someone/other.git"
    }
  });
  const { confirmed } = withConfirmedCloneRoots([confirmedCwd, siblingCwd], [mainRoot], { git });
  assert.deepEqual([...confirmed.keys()], [confirmedCwd], "fixture sanity: KF confirms and kf is rejected");

  const s4 = find(summarize({
    rows: [row({ session: "review-sandbox", cwd: confirmedCwd }), row({ session: "genuinely-external", cwd: siblingCwd })],
    repoRoots: [mainRoot], repoRootSource: "git", cloneRoots: [...confirmed.keys()]
  }), 4);
  assert.deepEqual(s4.detail.map(d => d.session), ["genuinely-external"],
    "only the exact confirmed string folds in; the sibling that differs only in case was rejected and stays outside");
  assert.equal(s4.verdict, "ADOPT");
  // The disclosure names exactly the strings that were folded in: KF, and not kf.
  assert.ok(quotedValues(s4.caveat).includes(confirmedCwd));
  assert.ok(!quotedValues(s4.caveat).includes(siblingCwd));
});

// --- 6. Reading a Done transition ---------------------------------------------------------

test("the target status comes from afterJson, not from the prose message", () => {
  assert.equal(statusAfter(statusEvent(1, "Done")), "Done");
  assert.equal(statusAfter({ afterJson: "not json" }), null);
  assert.equal(statusAfter({ afterJson: JSON.stringify({ id: 1 }) }), null);
});

test("task ids normalize so 7 and \"7\" are one task", () => {
  assert.equal(normalizeTaskId(7), 7);
  assert.equal(normalizeTaskId("7"), 7, "mcp-server's coerceInt accepts an integer STRING, and buildRow records the raw value");
  assert.equal(normalizeTaskId(""), null, "Number('') is 0 — an empty value must not invent task 0");
  assert.equal(normalizeTaskId("   "), null);
  assert.equal(normalizeTaskId(true), null, "Number(true) is 1 — a boolean must not invent task 1");
  assert.equal(normalizeTaskId(null), null);
  assert.equal(normalizeTaskId(undefined), null);
  assert.equal(normalizeTaskId("abc"), null);
  assert.equal(normalizeTaskId("99999999999999999999"), null, "beyond safe-integer range is a WRONG id, not a big one");
});

// --- 7. Signal 1: sessions invoking get_next_tasks ----------------------------------------

test("signal 1 counts distinct SESSIONS, not calls", () => {
  const rows = [row({ session: "a" }), row({ session: "a" }), row({ session: "a" })];
  assert.equal(find(summarize({ rows, repoRoot: REPO }), 1).value, 1, "three calls from one session is one session");

  const three = [row({ session: "a" }), row({ session: "b" }), row({ session: "c" })];
  const s1 = find(summarize({ rows: three, repoRoot: REPO }), 1);
  assert.equal(s1.value, 3);
  assert.equal(s1.verdict, "ADOPT");
});

test("signal 1 crosses its threshold at THREE — two sessions is PENDING, not ADOPT", () => {
  // The `>= 3` boundary itself, which nothing guarded: `>= 2` would have passed every other test
  // in this file.
  const two = [row({ session: "a" }), row({ session: "b" })];
  const s1 = find(summarize({ rows: two, repoRoot: REPO }), 1);
  assert.equal(s1.value, 2);
  assert.equal(s1.verdict, "PENDING");
  assert.equal(find(summarize({ rows: [...two, row({ session: "c" })], repoRoot: REPO }), 1).verdict, "ADOPT");
});

test("signal 1 reverts at zero and pends in between", () => {
  assert.equal(find(summarize({ rows: [], repoRoot: REPO }), 1).verdict, "REVERT");
  assert.equal(find(summarize({ rows: [row({ session: "a" })], repoRoot: REPO }), 1).verdict, "PENDING");
});

test("signal 1 counts only get_next_tasks, not every read", () => {
  const rows = [row({ session: "a", tool: "get_task" }), row({ session: "b", tool: "list_project_tasks" })];
  assert.equal(find(summarize({ rows, repoRoot: REPO }), 1).value, 0);
});

test("signal 1 always states that unprompted-ness is not measured", () => {
  const s1 = find(summarize({ rows: [row()], repoRoot: REPO }), 1);
  assert.match(s1.caveat, /UNPROMPTED-NESS IS NOT MEASURED/);
});

// --- 8. Signal 2: UNCOORDINATED overlaps (not refusals, not takeovers) ---------------------

test("signal 2 measures UNCOORDINATED overlap: two sessions writing one task with no claim is a REVERT", () => {
  // #477 §4.1's actual incident was an UNDETECTED collision — two sessions on one branch, neither
  // aware of the other. This scored a clean ADOPT before, because nothing had been refused.
  const rows = [
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "b", tool: "update_task", taskId: 7 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.equal(s2.verdict, "REVERT");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps, [
    { taskId: 7, writers: ["session:a", "session:b"], holders: [], unheld: ["session:a", "session:b"], refusedThenWrote: [], coordinated: false }
  ]);
  assert.deepEqual(s2.detail.coordinatedOverlaps, []);
});

test("A claims and writes task 7, B writes it and never claims — the #477 §4.1 incident is a REVERT", () => {
  // THE BLIND SPOT. `claimedTasks` was a flat Set of "tasks anyone ever claimed", so A's own claim
  // marked task 7 claimed and B's unclaimed write was excused as a legitimate handoff: 0 / ADOPT
  // for the exact incident #477 was filed about. Coordination is a property of a (task, session)
  // PAIR — an overlap is coordinated only when EVERY writer held that task.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "b", tool: "update_task", taskId: 7 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.equal(s2.verdict, "REVERT");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].unheld, ["session:b"], "B held nothing");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].holders, ["session:a", "actor:agent-a"]);
  assert.deepEqual(s2.detail.coordinatedOverlaps, []);
});

test("a claim by an UNRELATED session cannot excuse a collision between two others", () => {
  // Same shape, from the other direction: a flat "task 7 was claimed" set let a third party's
  // claim — before, after, or nowhere near the collision — score it 0 / ADOPT.
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7 }),
    row({ session: "b", tool: "update_task_status", taskId: 7, to: "Review" }),
    row({ session: "c", tool: "claim_task", taskId: 7, ts: "2026-09-01T23:00:00.000Z", actor: "agent-c" })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.equal(s2.verdict, "REVERT");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].unheld, ["session:a", "session:b"]);
});

test("a lone TaskClaimStolen anywhere in the window cannot excuse a collision on that task", () => {
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7 }),
    row({ session: "b", tool: "update_task", taskId: 7 })
  ];
  const events = [{ id: 9, projectId: 2, entityId: 7, entityType: "Task", eventType: "TaskClaimStolen", actor: "someone-else", createdAt: "2026-09-01T10:00:00" }];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1, "the takeover was somebody else's; neither writer held the task");
  assert.equal(s2.verdict, "REVERT");
  assert.equal(s2.detail.prevented.stolenClaims, 1, "and it is still reported as the primitive working");
});

test("a REFUSED claim followed by a write to that same task is a collision the system FAILED to prevent", () => {
  // No fixture combined a 409 with an overlap on the SAME task, which is finding 1's blind spot in
  // both directions at once: A's successful claim marked the task claimed, so B's write was excused
  // (0 / ADOPT), and B's refusal was ALSO counted under detail.prevented as a collision the system
  // PREVENTED — while B went on to write the task anyway.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "a", tool: "update_task", taskId: 7 }),
    row({ session: "b", tool: "claim_task", taskId: 7, actor: "agent-b", outcome: "error", http: 409 }),
    row({ session: "b", tool: "update_task", taskId: 7 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.equal(s2.verdict, "REVERT");
  assert.equal(s2.detail.prevented.refusedClaims, 0, "nothing was prevented — B wrote the task it was refused");
  assert.deepEqual(s2.detail.refusedThenWrote, [{ taskId: 7, writers: ["session:b"] }]);
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].unheld, ["session:b"]);
});

test("a refusal followed by a SUCCESSFUL retry is still a prevented collision, not a failure", () => {
  // The board said no, the session waited, claimed it properly and then wrote. That is the
  // primitive working end to end, and it must not be scored as a collision.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "b", tool: "claim_task", taskId: 7, actor: "agent-b", outcome: "error", http: 409 }),
    row({ session: "b", tool: "claim_task", taskId: 7, actor: "agent-b", ts: "2026-09-01T11:00:00.000Z" }),
    row({ session: "b", tool: "update_task", taskId: 7 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0);
  assert.equal(s2.detail.prevented.refusedClaims, 1);
  assert.deepEqual(s2.detail.refusedThenWrote, []);
});

test("a refused session that writes a DIFFERENT task is not a failure to prevent", () => {
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "b", tool: "claim_task", taskId: 7, actor: "agent-b", outcome: "error", http: 409 }),
    row({ session: "b", tool: "update_task", taskId: 8 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.detail.prevented.refusedClaims, 1);
  assert.deepEqual(s2.detail.refusedThenWrote, []);
  assert.equal(s2.value, 0);
});

test("signal 2 keys tasks by VALUE, so 7 and \"7\" collide", () => {
  // mcp-server's coerceInt deliberately accepts "42" because LLM clients send ids as strings, and
  // buildRow records the raw argument. Bucketing on the raw value let the two forms sit in
  // separate buckets and the collision was never seen.
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7 }),
    row({ session: "b", tool: "update_task", taskId: "7" })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].taskId, 7);
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].writers, ["session:a", "session:b"]);
});

test("a TTL takeover is the coordination primitive WORKING — reported as prevented, never as the verdict", () => {
  // #477 §5.2 draws the TTL takeover of a crashed agent's stale claim as the GREEN success path.
  // Counting it as a collision scored the system's success as its failure.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 1 }),
    row({ session: "b", tool: "claim_task", taskId: 2 })
  ];
  const events = [{ entityId: 4, entityType: "Task", eventType: "TaskClaimStolen", createdAt: "2026-09-01T10:00:00" }];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0, "a takeover is not an uncoordinated overlap");
  assert.equal(s2.verdict, "ADOPT");
  assert.equal(s2.detail.prevented.stolenClaims, 1);
});

test("a refused claim (409) is a collision the system PREVENTED, and does not drive the verdict", () => {
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 1 }),
    row({ session: "b", tool: "claim_task", taskId: 1, outcome: "error", http: 409 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.detail.prevented.refusedClaims, 1);
  assert.equal(s2.value, 0);
  assert.notEqual(s2.verdict, "REVERT", "the board refusing a second claim is the primitive working");
});

test("a claim_task error with a NON-409 status is not counted as a prevented collision", () => {
  // 409 is the ONLY status that means "someone else holds it". A 500 is the app failing and a 404
  // is a bad id; counting either would report contention that never happened.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 1, outcome: "error", http: 500 }),
    row({ session: "b", tool: "claim_task", taskId: 1, outcome: "error", http: 404 }),
    row({ session: "c", tool: "claim_task", taskId: 1, outcome: "error" })   // transport failure, no http at all
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.detail.prevented.refusedClaims, 0);
});

test("a SEQUENTIAL HANDOFF — both sessions claimed the task — is coordinated, and listed separately", () => {
  // The legitimate case, and the only shape that now qualifies: A claimed and worked it, released
  // it, B claimed it and continued. EVERY writer held the task, so the overlap is a handoff.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "a", tool: "release_task", taskId: 7, actor: "agent-a", ts: "2026-09-01T10:30:00.000Z" }),
    row({ session: "b", tool: "claim_task", taskId: 7, actor: "agent-b", ts: "2026-09-01T11:00:00.000Z" }),
    row({ session: "b", tool: "add_task_comment", taskId: 7, ts: "2026-09-01T11:01:00.000Z" })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0);
  assert.equal(s2.verdict, "ADOPT");
  assert.equal(s2.detail.coordinatedOverlaps.length, 1);
  assert.deepEqual(s2.detail.coordinatedOverlaps[0].writers, ["session:a", "session:b"]);
  assert.deepEqual(s2.detail.coordinatedOverlaps[0].unheld, []);
  assert.deepEqual(s2.detail.claimHolders, [
    { taskId: 7, holders: ["session:a", "actor:agent-a", "session:b", "actor:agent-b"] }
  ]);
});

test("a claim taken through the UI counts — bridged to its session by the actor the log recorded", () => {
  // A claim taken through the UI or REST leaves an ActivityEvent and no log row. Missing it would
  // misread a coordinated handoff as an uncoordinated overlap — the direction that manufactures a
  // REVERT. But the two sources name their participants in DIFFERENT NAMESPACES: the log has a
  // `session` (a per-process id), the event has an `actor` (free text the caller sent). The bridge
  // is the log row that carries both, because `actor` is a recorded argument.
  //
  // B's identity as `agent-b` is corroborated by a claim B made THROUGH THE WRAPPER, on a
  // different task — that is what the alias rule requires, and it is what separates a genuine
  // named actor from a label anyone might send. B's claim on task 7 itself exists only as an event.
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "b", tool: "claim_task", taskId: 3, actor: "agent-b" }),
    row({ session: "b", tool: "update_task", taskId: 7 })
  ];
  // B's claim ON TASK 7 exists only as an event, under B's ACTOR name.
  const events = [{ id: 5, projectId: 2, entityId: 7, entityType: "Task", eventType: "TaskClaimed", actor: "Agent-B", createdAt: "2026-09-01T09:00:00" }];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0, "both writers held the task, one of them out of band");
  const overlap7 = s2.detail.coordinatedOverlaps.find(c => c.taskId === 7);
  assert.deepEqual(overlap7.writers, ["session:a", "session:b"]);
  assert.deepEqual(overlap7.unheld, []);
  assert.ok(s2.detail.claimHolders.find(h => h.taskId === 7).holders.includes("actor:agent-b"),
    "actor identity is case-insensitive, as the server treats it");

  // Without the bridging row, the same event names an actor nobody can be shown to be, and the
  // overlap stands. This is the assertion that keeps the bridge from degrading into "assume equal".
  const unbridged = find(summarize({ rows: rows.map(r => ({ ...r, actor: undefined })), events, repoRoot: REPO }), 2);
  assert.equal(unbridged.value, 1);
  assert.deepEqual(unbridged.detail.uncoordinatedOverlaps[0].unheld, ["session:b"],
    "B's UI claim can no longer be tied to B, so B is not a holder — A still is, from its own claim row");
});

test("an actor is only an IDENTITY when it maps to one session AND that session claimed under it", () => {
  // Reported on PR #504, finding 1. The bridge used to alias a session to EVERY actor string it had
  // ever sent, so the wrapper's DEFAULT label fused unrelated sessions into one participant. Two
  // rules now gate an alias, and this pins both of them plus the raw maps they are computed from.
  const bridge = buildIdentityBridge([
    { session: "s1", actor: "Agent-A", tool: "claim_task", outcome: "ok" },
    { session: "s1", actor: "agent-a", tool: "update_task" },   // same actor, different case
    { session: "s2", actor: "agent-b", tool: "update_task" },   // never claimed under the name
    { session: "s3", actor: "shared", tool: "claim_task", outcome: "ok" },
    { session: "s4", actor: "shared", tool: "claim_task", outcome: "ok" },
    { session: "s5", actor: "agent-e", tool: "claim_task", outcome: "error", http: 409 },
    { session: "s6" },                                          // no actor: contributes nothing
    { actor: "orphan" },                                        // no session: contributes nothing
    { session: "s7", actor: "   ", tool: "claim_task", outcome: "ok" }  // whitespace is not an actor
  ]);
  // The RAW evidence: every (session, actor) co-occurrence, which is what rule 1 is computed from.
  assert.deepEqual([...bridge.sessionToActors.keys()], ["s1", "s2", "s3", "s4", "s5"]);
  assert.deepEqual([...bridge.sessionToActors.get("s1")], ["agent-a"], "case-folded to one identity");
  assert.deepEqual([...bridge.actorToSessions.get("agent-b")], ["s2"]);
  assert.deepEqual([...bridge.actorToSessions.get("shared")], ["s3", "s4"]);
  assert.equal(bridge.actorToSessions.has("orphan"), false);

  // And the IDENTIFYING subset, which is the only thing `aliasesOf` reads.
  assert.deepEqual([...bridge.identifyingActors.get("s1")], ["agent-a"], "unique AND claimed under");
  assert.deepEqual([...bridge.identifyingSessions.get("agent-a")], ["s1"]);
  assert.equal(bridge.identifyingActors.has("s2"), false, "rule 2: s2 never CLAIMED as agent-b");
  assert.equal(bridge.identifyingActors.has("s3"), false, "rule 1: `shared` names two sessions, so it names neither");
  assert.equal(bridge.identifyingActors.has("s4"), false);
  assert.equal(bridge.identifyingSessions.has("shared"), false);
  assert.equal(bridge.identifyingActors.has("s5"), false, "a REFUSED claim is not the board accepting the name");

  assert.equal(normalizeActor("  Mcp-Agent "), "mcp-agent");
  assert.equal(normalizeActor(""), null);
  assert.equal(normalizeActor(undefined), null);

  // BOTH DIRECTIONS of the alias obey both rules. Only session -> actor has a production caller
  // (isHeldBy is always handed a `session:` key, because writers come from the log alone), so the
  // actor -> session direction is pinned here rather than left as untested symmetry: it goes live
  // the moment anyone revisits seeding writers from ActivityEvent.Actor, and it must not be the
  // place where "an actor label is an identity" quietly comes back.
  assert.deepEqual([...aliasesOf("session:s1", bridge)].sort(), ["actor:agent-a", "session:s1"]);
  assert.deepEqual([...aliasesOf("actor:agent-a", bridge)].sort(), ["actor:agent-a", "session:s1"]);
  assert.deepEqual([...aliasesOf("session:s2", bridge)], ["session:s2"], "rule 2: never claimed under the name");
  assert.deepEqual([...aliasesOf("actor:agent-b", bridge)], ["actor:agent-b"]);
  assert.deepEqual([...aliasesOf("actor:shared", bridge)], ["actor:shared"], "rule 1: a label shared by two sessions names neither");
  assert.deepEqual([...aliasesOf("session:s3", bridge)], ["session:s3"]);
  assert.deepEqual([...aliasesOf("actor:agent-e", bridge)], ["actor:agent-e"], "a refused claim is not the board accepting the name");
  assert.deepEqual([...aliasesOf("session:unknown", bridge)], ["session:unknown"]);
});

test("two sessions on the wrapper's DEFAULT actor label are TWO participants — #477 §4.1's incident", () => {
  // THE BLOCKER on PR #504, reproduced exactly. `mcp-agent` is the wrapper's default actor: the
  // schema advertises it and update_task_status/release_task substitute it when `actor` is omitted,
  // so it is what a caller sends when it has nothing to say about who it is. The bridge aliased a
  // session to every actor it had ever sent and `isHeldBy` credited every alias, so A's claim under
  // the default vouched for B — a session that never claimed anything — and §4.1's own incident
  // (A holds the claim, B writes anyway) scored 0 / ADOPT. One call carrying the default label was
  // enough.
  const rows = [
    row({ session: "AAAA", tool: "claim_task", taskId: 7, actor: "mcp-agent", ts: "2026-09-01T10:00:00.000Z" }),
    row({ session: "AAAA", tool: "update_task_status", taskId: 7, to: "InProgress", actor: "mcp-agent", ts: "2026-09-01T10:01:00.000Z" }),
    row({ session: "BBBB", tool: "update_task_status", taskId: 7, to: "Review", actor: "mcp-agent", ts: "2026-09-01T10:02:00.000Z" })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1, "B never claimed task 7; sharing A's default label is not holding it");
  assert.equal(s2.verdict, "REVERT");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].unheld, ["session:BBBB"]);
  assert.equal(s2.detail.writingSessions, 2, "and the vacuity guard cannot be what produced the 1");
  // The label is still reported as a holder KEY — that is honest evidence — it simply vouches for
  // nobody, which is the distinction the fix turns on.
  assert.ok(s2.detail.uncoordinatedOverlaps[0].holders.includes("actor:mcp-agent"));
  assert.match(s2.caveat, /ONLY WHEN IT IDENTIFIES ONE/);

  // The same shape with B silent about its actor: the default then maps to ONE session, so rule 1
  // passes — and B is STILL not a holder, because nothing ever tied B to the label at all.
  const bSilent = [...rows.slice(0, 2), row({ session: "BBBB", tool: "update_task_status", taskId: 7, to: "Review", ts: "2026-09-01T10:02:00.000Z" })];
  assert.equal(find(summarize({ rows: bSilent, repoRoot: REPO }), 2).value, 1);
});

test("a UI claim under the default label cannot vouch for a session that merely SENT the label", () => {
  // Rule 1 alone is not enough. Here exactly one session ever sends `mcp-agent`, so the label is
  // "unique" — but it sent it on a WRITE, never on a claim, and the claim under that name was
  // somebody else's, taken through the UI. Aliasing on uniqueness alone would hand that session a
  // claim it never made, which is the ADOPT-manufacturing direction again.
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7, actor: "mcp-agent" }),
    row({ session: "b", tool: "update_task_status", taskId: 7, to: "Review" })
  ];
  const events = [{ id: 5, projectId: 2, entityId: 7, entityType: "Task", eventType: "TaskClaimed", actor: "mcp-agent", createdAt: "2026-09-01T09:00:00" }];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.deepEqual(s2.detail.uncoordinatedOverlaps[0].unheld, ["session:a", "session:b"],
    "neither writer is shown to be the mcp-agent that took the UI claim");

  // Corroborate it — session `a` claims something under the same name — and the alias is allowed,
  // because now the board has accepted `a` under that label and nobody else uses it.
  const corroborated = [row({ session: "a", tool: "claim_task", taskId: 9, actor: "mcp-agent" }), ...rows];
  const s2b = find(summarize({ rows: corroborated, events, repoRoot: REPO }), 2);
  assert.deepEqual(s2b.detail.uncoordinatedOverlaps[0].unheld, ["session:b"], "a is now a holder of 7; b still is not");
});

test("a claim event with NO actor vouches for nobody — it is counted, not credited", () => {
  // `ActivityEvent.Actor` is nullable and the UI does not always set it. An anonymous claim used to
  // mark the task "claimed" and excuse every writer on it.
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7 }),
    row({ session: "b", tool: "update_task_status", taskId: 7, to: "Review" })
  ];
  const events = [{ id: 5, projectId: 2, entityId: 7, entityType: "Task", eventType: "TaskClaimed", actor: null, createdAt: "2026-09-01T09:00:00" }];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1, "an unattributed claim cannot make this a handoff");
  assert.equal(s2.verdict, "REVERT");
  assert.equal(s2.detail.unattributedClaimEvents, 1, "but it IS disclosed");
});

test("ONE session doing ordinary work is never a collision, whatever the events say", () => {
  // The live false REVERT: a single session made two update_task_status calls on task 194 with no
  // `actor` argument, so the audit events carried the schema default "mcp-agent". Seeding writers
  // from `actor` counted that one session twice — once as `session:f891778b` from its own rows and
  // once as `actor:mcp-agent` from the events those very calls produced — and scored 1 / REVERT,
  // with `writingSessions: 1` in the same detail block. §11's consequence for REVERT is closing
  // #477 as not-planned; a signal that manufactures one from one session is worse than one that
  // under-reports, so writers come from the LOG ONLY and the vacuity guard covers every branch.
  const rows = [
    row({ session: "f891778b", tool: "update_task_status", taskId: 194, to: "InProgress", ts: "2026-09-01T10:00:00.000Z", ms: 40 }),
    row({ session: "f891778b", tool: "update_task_status", taskId: 194, to: "Review", ts: "2026-09-01T10:05:00.000Z", ms: 40 })
  ];
  const events = [
    statusEvent(194, "InProgress", { id: 100, actor: "mcp-agent", createdAt: "2026-09-01T10:00:00" }),
    statusEvent(194, "Review", { id: 101, actor: "mcp-agent", createdAt: "2026-09-01T10:05:00" })
  ];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.detail.writingSessions, 1);
  assert.equal(s2.value, 0, "one session cannot collide with itself, whatever actor its audit rows carry");
  assert.equal(s2.verdict, "PENDING");
  assert.deepEqual(s2.detail.uncoordinatedOverlaps, []);
});

test("the vacuity guard forces PENDING on EVERY branch, not only the zero-overlap one", () => {
  // If fewer than two sessions wrote, a collision was not possible, so a non-zero count there is an
  // artefact of the instrument rather than a finding — and REVERT is the verdict that closes #477.
  // The guard used to run only on the `uncoordinated.length === 0` branch, so any bug that inflated
  // the count went straight to REVERT past a guard that had already proven it impossible.
  const rows = [
    row({ session: "solo", tool: "update_task", taskId: 7 }),
    row({ session: "solo", tool: "add_task_comment", taskId: 7 })
  ];
  const summary = summarize({ rows, repoRoot: REPO });
  const s2 = find(summary, 2);
  assert.equal(s2.detail.writingSessions, 1);
  assert.equal(s2.verdict, "PENDING");
  assert.match(s2.caveat, /VACUOUS/);
  assert.match(s2.caveat, /whatever the count above says/);

  // THE BRANCH THAT ORDER ACTUALLY DECIDES, and the only fixture that can tell the two orderings
  // apart: a NON-ZERO count with a single writing session. One session is refused the claim (409)
  // and writes the task anyway, so `uncoordinated` is 1 while `writingSessions` is 1 — the holder
  // that refused it never wrote through the wrapper and is invisible here. Checking the count
  // first yields REVERT, which §11 turns into closing #477; checking the guard first yields
  // PENDING, which is what a collision that could not have happened deserves.
  const refusedSolo = [
    row({ session: "solo", tool: "claim_task", taskId: 9, actor: "agent-solo", outcome: "error", http: 409 }),
    row({ session: "solo", tool: "update_task", taskId: 9 })
  ];
  const lone = find(summarize({ rows: refusedSolo, repoRoot: REPO }), 2);
  assert.equal(lone.value, 1, "the refused-then-wrote collision is still counted and reported");
  assert.deepEqual(lone.detail.refusedThenWrote, [{ taskId: 9, writers: ["session:solo"] }]);
  assert.equal(lone.detail.writingSessions, 1);
  assert.equal(lone.verdict, "PENDING", "one writing session cannot collide, so the count is an artefact and not a REVERT");

  // The same shape with a SECOND writing session present is a REVERT, so the guard is about the
  // session count and not about the refusal.
  const withSecond = [...refusedSolo, row({ session: "other", tool: "claim_task", taskId: 9, actor: "agent-other", ts: "2026-09-01T09:00:00.000Z" })];
  assert.equal(find(summarize({ rows: withSecond, repoRoot: REPO }), 2).verdict, "REVERT");
});

test("signal 2 states the log-only writer limitation plainly rather than pretending to see UI writes", () => {
  const s2 = find(summarize({ rows: [row()], repoRoot: REPO }), 2);
  assert.match(s2.caveat, /WRITERS ARE COUNTED FROM THE USAGE LOG\s+ONLY/);
  assert.match(s2.caveat, /INVISIBLE to this signal/);
});

test("signal 2 does NOT read zero overlaps as adopt when nobody wrote — the vacuity guard", () => {
  const s2 = find(summarize({ rows: [row()], repoRoot: REPO }), 2);
  assert.equal(s2.value, 0);
  assert.equal(s2.verdict, "PENDING", "an unused board must not pass the collision signal");
  assert.match(s2.caveat, /VACUOUS/);
  assert.equal(s2.detail.writingSessions, 0);
});

test("the vacuity guard needs TWO writing sessions, not one", () => {
  const one = [row({ session: "a", tool: "claim_task", taskId: 1 }), row({ session: "a", tool: "update_task", taskId: 2 })];
  assert.equal(find(summarize({ rows: one, repoRoot: REPO }), 2).verdict, "PENDING");
  const two = [...one, row({ session: "b", tool: "update_task", taskId: 3 })];
  assert.equal(find(summarize({ rows: two, repoRoot: REPO }), 2).verdict, "ADOPT");
});

test("the vacuity guard counts EVERY board mutation, not only task-scoped ones", () => {
  // create_task, promote_idea and create_dependency were all missing from the set that drives
  // `writingSessions`, so a session that did nothing but author work counted as zero writers and
  // the guard read a busy trial as vacuous.
  for (const tool of ["create_task", "promote_idea", "create_dependency", "create_feature", "create_idea"]) {
    const rows = [row({ session: "a", tool }), row({ session: "b", tool })];
    const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
    assert.equal(s2.detail.writingSessions, 2, `${tool} must count as a write`);
    assert.equal(s2.verdict, "ADOPT", `${tool} must satisfy the vacuity guard`);
  }
  const reads = [row({ session: "a", tool: "get_task" }), row({ session: "b", tool: "list_projects" })];
  assert.equal(find(summarize({ rows: reads, repoRoot: REPO }), 2).detail.writingSessions, 0, "a read is not a write");
});

test("a FAILED write is not a write, in either the overlap buckets or the vacuity guard", () => {
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7, outcome: "error", http: 500 }),
    row({ session: "b", tool: "update_task", taskId: 7, outcome: "error", http: 500 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0);
  assert.equal(s2.detail.writingSessions, 0);
});

test("signal 2's name and caveat describe what it now measures", () => {
  const s2 = find(summarize({ rows: [row()], repoRoot: REPO }), 2);
  assert.match(s2.name, /[Uu]ncoordinated/);
  assert.match(s2.caveat, /prevented/i);
});

// --- 9. Signal 3: Done THROUGH the gate ---------------------------------------------------

test("signal 3 counts distinct tasks that reached Done", () => {
  const events = [statusEvent(1, "Done"), statusEvent(1, "Done"), statusEvent(2, "Done"), statusEvent(3, "Review")];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate.sort(), [1, 2]);
  assert.equal(s3.value, 2);
});

test("only Done counts — InProgress and Todo are ordinary board movement, not completion", () => {
  // The old suite only ever contrasted Done with Review, so any predicate separating those two
  // passed. `!== "Backlog"`, or `=== "Done" || === "InProgress"`, would have gone green while
  // scoring five tasks dragged to InProgress as five tasks through the DoD gate.
  const events = [
    statusEvent(1, "InProgress"), statusEvent(2, "Todo"), statusEvent(3, "Backlog"),
    statusEvent(4, "Review"), statusEvent(5, "Done")
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [5]);
  assert.equal(s3.value, 1);
});

test("a TaskStatusChanged on something that is not a Task does not count", () => {
  // Ideas move through their own statuses and emit the same event type. Counting one as a task
  // completion would inflate the one signal whose adopt condition is a raw count.
  const events = [
    statusEvent(1, "Done", { entityType: "Idea" }),
    statusEvent(2, "Done", { entityType: "Feature" }),
    statusEvent(3, "Done")
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [3]);
});

test("a Done that OVERRODE the gate does not count as having passed it", () => {
  const events = [statusEvent(1, "Done"), statusEvent(2, "Done"), overrideEvent(2)];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [1]);
  assert.deepEqual(s3.detail.doneWithOverride, [2]);
  assert.equal(s3.value, 1);
});

test("an override disqualifies THAT TRANSITION, not the task forever", () => {
  // The old code held a Set of overridden task ids and excluded the task from every window it
  // appeared in. A task overridden once and later completed cleanly through the gate could never
  // count again — and the exclusion depended on the window, since an override before the cutoff
  // excluded nothing at all.
  const events = [
    statusEvent(5, "Done", { createdAt: "2026-09-01T10:00:00" }),
    overrideEvent(5, { createdAt: "2026-09-01T10:00:00" }),          // same SaveChangesAsync: same instant
    statusEvent(5, "Done", { createdAt: "2026-09-03T12:00:00" })     // later, cleanly through the gate
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [5], "the clean completion counts");
  assert.deepEqual(s3.detail.doneWithOverride, [5], "and the overridden transition is still reported");
});

test("override pairing is on ActivityEvent.Id — a different task, or a distant id, does not pair", () => {
  const events = [
    statusEvent(1, "Done", { id: 10, createdAt: "2026-09-01T10:00:00" }),
    overrideEvent(2, { id: 11, createdAt: "2026-09-01T10:00:00" }),   // adjacent id, DIFFERENT task
    statusEvent(3, "Done", { id: 12, createdAt: "2026-09-01T10:00:00" }),
    overrideEvent(3, { id: 900, createdAt: "2026-09-02T10:00:00" })   // same task, a different save
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate.sort(), [1, 3]);
  assert.deepEqual(s3.detail.doneWithOverride, []);
  assert.deepEqual(s3.detail.overriddenTasks.sort(), [2, 3], "the overrides are still reported, just not paired");
  assert.deepEqual(s3.detail.unpairedOverrides.map(o => o.taskId).sort(), [2, 3],
    "an override with no Done to pair with is reported, not silently discarded");
});

test("a Done and its override pair on ID even when their timestamps straddle a millisecond", () => {
  // THE DEFECT. The old key was (entityId, exact instant) with a comment insisting it was "NOT a
  // tolerance window". The premise is false at the source: `ActivityEvent.CreatedAt` is a per-object
  // property initializer (`= DateTime.UtcNow`, ActivityEvent.cs:14), so the TaskStatusChanged and
  // TaskDoneOverridden rows are two SEPARATE clock reads — measured 20,000/20,000 consecutive reads
  // distinct. Pairing only ever worked because Date.parse truncates to milliseconds, i.e. because of
  // an accidental ~1 ms window. A pair 13 µs apart that straddles a millisecond boundary un-paired,
  // and five of those flip this signal from PENDING(4) to ADOPT(5) — the direction that manufactures
  // an adopt.
  const straddling = id => ([
    overrideEvent(id, { id: id * 10, createdAt: "2026-09-01T10:00:00.9999990" }),
    statusEvent(id, "Done", { id: id * 10 + 1, createdAt: "2026-09-01T10:00:01.0000120" })
  ]);
  const events = [1, 2, 3, 4, 5].flatMap(straddling);
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [], "13 µs apart is one SaveChangesAsync, not two transitions");
  assert.deepEqual(s3.detail.doneWithOverride.sort(), [1, 2, 3, 4, 5]);
  assert.equal(s3.value, 0);
  assert.notEqual(s3.verdict, "ADOPT", "five straddling pairs used to score the adopt threshold exactly");
  assert.deepEqual(s3.detail.unpairedOverrides, []);
});

test("pairing follows the ID even when the TIMESTAMPS say otherwise — the id is the join, not a window", () => {
  // The fixture that separates the two mechanisms, which a straddling pair cannot: 200 ms apart, so
  // ANY tolerance window of a second or more would pair them, but 500 ids apart — five hundred
  // events committed in between, which is two different saves on a busy board and not one
  // SaveChangesAsync. If this pairs, the pairing is running on the clock again.
  const events = [
    overrideEvent(5, { id: 400, createdAt: "2026-09-01T10:00:00.100" }),
    statusEvent(5, "Done", { id: 900, createdAt: "2026-09-01T10:00:00.300" })
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [5], "the Done did not come from the override's save, so it passed the gate");
  assert.deepEqual(s3.detail.doneWithOverride, []);
  assert.deepEqual(s3.detail.unpairedOverrides.map(o => o.id), [400]);

  // And the same two instants with ADJACENT ids DO pair, so this is about the id and not about the
  // 200 ms gap.
  const adjacent = [
    overrideEvent(5, { id: 400, createdAt: "2026-09-01T10:00:00.100" }),
    statusEvent(5, "Done", { id: 401, createdAt: "2026-09-01T10:00:00.300" })
  ];
  const paired = find(summarize({ rows: [], events: adjacent, repoRoot: REPO }), 3);
  assert.deepEqual(paired.detail.throughGate, []);
  assert.deepEqual(paired.detail.doneWithOverride, [5]);
});

test("each override disqualifies at most ONE Done — a matching, not a broadcast", () => {
  // A task overridden once and later completed cleanly must lose exactly one of its two
  // transitions. Pairing every nearby Done against the same override would disqualify both, and
  // pairing none would disqualify neither.
  const events = [
    overrideEvent(5, { id: 100, createdAt: "2026-09-01T10:00:00" }),
    statusEvent(5, "Done", { id: 101, createdAt: "2026-09-01T10:00:00" }),
    statusEvent(5, "Done", { id: 103, createdAt: "2026-09-03T12:00:00" })   // later, cleanly
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.doneWithOverride, [5]);
  assert.deepEqual(s3.detail.throughGate, [5]);
  assert.deepEqual(s3.detail.overriddenAndAlsoThroughGate, [5],
    "the same task in both lists is named explicitly, so it cannot read as a contradiction");
  assert.deepEqual(s3.detail.unpairedOverrides, []);
  assert.match(s3.caveat, /overriddenAndAlsoThroughGate/);
});

test("with no ids at all, pairing falls back to an EXPLICIT ±1 s window and says so", () => {
  // A caller whose events carry no id — another instrument, a REPL — still needs pairing, and the
  // fallback is a stated tolerance rather than a pretence of exactness.
  const near = [
    { ...overrideEvent(7, { createdAt: "2026-09-01T10:00:00.100" }), id: undefined },
    { ...statusEvent(7, "Done", { createdAt: "2026-09-01T10:00:00.900" }), id: undefined }
  ];
  assert.deepEqual(find(summarize({ rows: [], events: near, repoRoot: REPO }), 3).detail.doneWithOverride, [7]);

  const far = [
    { ...overrideEvent(8, { createdAt: "2026-09-01T10:00:00.000" }), id: undefined },
    { ...statusEvent(8, "Done", { createdAt: "2026-09-01T10:00:05.000" }), id: undefined }
  ];
  const s3 = find(summarize({ rows: [], events: far, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.doneWithOverride, [], "5 s apart is not one save");
  assert.deepEqual(s3.detail.throughGate, [8]);
  assert.match(s3.caveat, /1000 ms tolerance window/);
  assert.match(s3.caveat, /ActivityEvent\.Id, NOT ON THE TIMESTAMP/);
});

test("an override BEFORE the cutoff cannot disqualify a Done inside the window", () => {
  const events = [
    statusEvent(9, "Done", { createdAt: "2026-08-01T10:00:00Z" }),
    overrideEvent(9, { createdAt: "2026-08-01T10:00:00Z" }),
    statusEvent(9, "Done", { createdAt: "2026-09-05T10:00:00Z" })
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO, since: "2026-09-01" }), 3);
  assert.deepEqual(s3.detail.throughGate, [9]);
  assert.equal(s3.value, 1);
});

test("gate refusals are surfaced — a gate that never refuses is not being exercised", () => {
  const rows = [row({ tool: "update_task_status", taskId: 3, to: "Done", outcome: "error", http: 422 })];
  const s3 = find(summarize({ rows, repoRoot: REPO }), 3);
  assert.equal(s3.detail.gateRefusals, 1);
});

test("an update_task_status error with a NON-422 status is not a gate refusal", () => {
  // 422 is the Definition-of-Done gate specifically. A 409 is a claim conflict, a 500 is the app
  // failing — reporting either as a gate refusal would claim the gate was exercised when it was
  // never reached.
  const rows = [
    row({ tool: "update_task_status", taskId: 3, to: "Done", outcome: "error", http: 409 }),
    row({ tool: "update_task_status", taskId: 3, to: "Done", outcome: "error", http: 500 }),
    row({ tool: "update_task_status", taskId: 3, to: "Done", outcome: "error" }),
    row({ tool: "update_task_status", taskId: 3, to: "Done", outcome: "ok", http: 422 })  // not an error at all
  ];
  assert.equal(find(summarize({ rows, repoRoot: REPO }), 3).detail.gateRefusals, 0);
});

test("signal 3 crosses its threshold at five tasks once the gate has been exercised", () => {
  // The refusal row is what makes the gate evidence NON-vacuous; without it the vacuity guard
  // below holds five Dones at PENDING. That is the next test.
  const refusal = row({ tool: "update_task_status", taskId: 9, outcome: "error", http: 422 });
  const four = [1, 2, 3, 4].map(id => statusEvent(id, "Done"));
  assert.equal(find(summarize({ rows: [refusal], events: four, repoRoot: REPO }), 3).verdict, "PENDING");
  const five = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done"));
  assert.equal(find(summarize({ rows: [refusal], events: five, repoRoot: REPO }), 3).verdict, "ADOPT");
  assert.equal(find(summarize({ rows: [], events: [], repoRoot: REPO }), 3).verdict, "REVERT");
});

test("signal 3 does not adopt on five Dones when the gate evidence is vacuous", () => {
  const five = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done"));
  const s3 = find(summarize({ rows: [], events: five, repoRoot: REPO }), 3);
  assert.equal(s3.verdict, "PENDING");
  assert.match(s3.caveat, /VACUOUS/);
});

// --- 10. Signal 4: used from outside the repo ----------------------------------------------

test("signal 4 adopts only when some session ran outside KanbanFlow-v2/", () => {
  const inside = [row({ cwd: `${REPO}\\scripts` })];
  assert.equal(find(summarize({ rows: inside, repoRoot: REPO }), 4).verdict, "REVERT");

  const outside = [row({ session: "z", cwd: "C:\\Users\\Owner\\Documents\\Claude\\my-api" })];
  const s4 = find(summarize({ rows: outside, repoRoot: REPO }), 4);
  assert.equal(s4.verdict, "ADOPT");
  assert.equal(s4.value, "yes");
  assert.deepEqual(s4.detail[0].cwds, ["C:\\Users\\Owner\\Documents\\Claude\\my-api"]);

  // EVERY distinct outside cwd, not just the first: this list is the evidence an adjudicator reads
  // to decide whether the board was genuinely consulted for other work, and one session moving
  // between two projects is the strongest form of that evidence.
  const moving = [
    row({ session: "z", ts: "2026-09-01T10:00:00.000Z", cwd: "C:\\Users\\Owner\\Documents\\Claude\\my-api" }),
    row({ session: "z", ts: "2026-09-01T11:00:00.000Z", cwd: "C:\\Users\\Owner\\Documents\\Claude\\other-thing" }),
    row({ session: "z", ts: "2026-09-01T12:00:00.000Z", cwd: "C:\\Users\\Owner\\Documents\\Claude\\my-api" })
  ];
  const s4moving = find(summarize({ rows: moving, repoRoot: REPO }), 4);
  assert.deepEqual(s4moving.detail[0].cwds,
    ["C:\\Users\\Owner\\Documents\\Claude\\my-api", "C:\\Users\\Owner\\Documents\\Claude\\other-thing"],
    "distinct, de-duplicated, in call order");
  assert.equal(s4moving.detail[0].calls, 3);
});

test("signals 1 and 4 count only calls that SUCCEEDED", () => {
  // Live defect: signal 1 filtered on the tool name with no outcome check, so a session whose
  // get_next_tasks never reached the board (dead port, code -32000, no http status) was listed
  // beside the ones that worked. It read 4 against an adopt threshold of >= 3 — the failed session
  // manufactured the ADOPT — and supplied 2 of the 5 calls behind signal 4's ADOPT as well.
  const dead = { outcome: "error", code: -32000, cwd: "C:\\other-project" };
  const rows = [
    row({ session: "a", cwd: "C:\\other-project" }),
    row({ session: "b", cwd: "C:\\other-project" }),
    row({ session: "dead", ...dead }),
    row({ session: "dead", ...dead })
  ];
  const summary = summarize({ rows, repoRoot: REPO });
  assert.equal(find(summary, 1).value, 2, "the dead session is not a session that invoked get_next_tasks");
  assert.equal(find(summary, 1).verdict, "PENDING", "and must not push the count over the >= 3 threshold");
  assert.deepEqual(find(summary, 1).detail.map(d => d.session), ["a", "b"]);
  assert.deepEqual(find(summary, 4).detail.map(d => d.session), ["a", "b"],
    "nor count as the board being consulted from outside the repo");

  // With the same calls SUCCEEDING, all three sessions count — so this is about the outcome, not
  // about the session id.
  const alive = summarize({ rows: rows.map(r => ({ ...r, outcome: "ok", code: undefined })), repoRoot: REPO });
  assert.equal(find(alive, 1).value, 3);
  assert.equal(find(alive, 1).verdict, "ADOPT");
});

test("signal 1 records EVERY distinct cwd a session called from, in call order", () => {
  // The caveat tells the reader to adjudicate "unprompted" on exactly this field, and only the
  // FIRST row's cwd was kept — the reader was pointed at evidence the accumulator had discarded.
  const rows = [
    row({ session: "a", ts: "2026-09-01T10:00:00.000Z", cwd: REPO }),
    row({ session: "a", ts: "2026-09-01T11:00:00.000Z", cwd: "C:\\other-project" }),
    row({ session: "a", ts: "2026-09-01T12:00:00.000Z", cwd: "C:\\other-project" }),
    row({ session: "a", ts: "2026-09-01T13:00:00.000Z", cwd: "C:\\third" })
  ];
  const s1 = find(summarize({ rows, repoRoot: REPO }), 1);
  assert.deepEqual(s1.detail[0].cwds, [REPO, "C:\\other-project", "C:\\third"], "distinct, de-duplicated, in call order");
  assert.equal(s1.detail[0].calls, 4);
  assert.match(s1.caveat, /Every distinct cwd/);
});

test("signal 1's first/last bracket every call, and the detail is ordered by FIRST call", () => {
  // Both were unasserted: `first`/`last` could have held any row's ts, and the one test that
  // touched the detail order `.sort()`ed the names — discarding the very ordering it should check.
  const rows = [
    row({ session: "late", ts: "2026-09-03T10:00:00.000Z" }),
    row({ session: "early", ts: "2026-09-01T10:00:00.000Z" }),
    row({ session: "early", ts: "2026-09-05T10:00:00.000Z" }),   // out of order on purpose
    row({ session: "early", ts: "2026-09-02T10:00:00.000Z" }),
    row({ session: "middle", ts: "2026-09-02T09:00:00.000Z" })
  ];
  const s1 = find(summarize({ rows, repoRoot: REPO }), 1);
  assert.deepEqual(s1.detail.map(d => d.session), ["early", "middle", "late"],
    "ordered by first call — NOT alphabetically, and not by insertion");
  const early = s1.detail[0];
  assert.equal(early.first, "2026-09-01T10:00:00.000Z", "the EARLIEST call, not the first row seen");
  assert.equal(early.last, "2026-09-05T10:00:00.000Z", "the LATEST call, even though it arrived mid-stream");
  assert.equal(early.calls, 3);
});

// --- 11. The window ------------------------------------------------------------------------

test("--since excludes everything before the trial started, in both sources", () => {
  const rows = [row({ ts: "2026-08-01T00:00:00.000Z" }), row({ session: "b", ts: "2026-09-05T00:00:00.000Z" })];
  const events = [statusEvent(1, "Done", { createdAt: "2026-08-01T00:00:00Z" }), statusEvent(2, "Done", { createdAt: "2026-09-05T00:00:00Z" })];
  const summary = summarize({ rows, events, repoRoot: REPO, since: "2026-09-01" });
  assert.equal(summary.window.rows, 1);
  assert.equal(find(summary, 1).value, 1);
  assert.deepEqual(find(summary, 3).detail.throughGate, [2]);
});

test("timezone-less ActivityEvent timestamps are interpreted as UTC", () => {
  const events = [statusEvent(1, "Done", { createdAt: "2026-09-01T00:30:00" })];
  const summary = summarize({ rows: [], events, repoRoot: REPO, since: "2026-09-01T02:00:00Z" });
  assert.equal(summary.window.events, 0,
    "00:30 without a designator is 00:30 UTC, not local time after the 02:00Z cutoff");
});

test("fetchActivity distinguishes an HTTP error and applies a bounded timeout without network access", async () => {
  // The TYPE, not just the message: an ActivityHttpError means the board ANSWERED and said no,
  // which is a different operational fact from a refused port or a stall, and `main()`'s NOTE must
  // never call it "not answering". The timeout half is asserted here as well because both live on
  // the same call: a fetch handed no signal hangs forever on a socket that never replies.
  let signal;
  const fakeFetch = async (_url, options) => {
    signal = options.signal;
    return { ok: false, status: 500, statusText: "Internal Server Error", json: async () => ({}) };
  };
  await assert.rejects(() => fetchActivity("http://example.invalid", { fetchImpl: fakeFetch, timeoutMs: 25 }), error => {
    assert.ok(error instanceof ActivityHttpError, "an answered-with-an-error is its own type");
    assert.match(error.message, /returned HTTP 500 Internal Server Error/);
    assert.doesNotMatch(error.message, /not answering/);
    return true;
  });
  assert.ok(signal instanceof AbortSignal);
});

test("the --since boundary is INCLUSIVE, to the millisecond", () => {
  // `>` instead of `>=` drops the very first event of a trial that started on the hour, and `<=`
  // in place of it would admit the last event before it. Neither is visible without ±1 ms.
  const at = (ts, session) => row({ ts, session });
  const rows = [
    at("2026-09-01T00:00:00.000Z", "exact"),
    at("2026-08-31T23:59:59.999Z", "one-ms-before"),
    at("2026-09-01T00:00:00.001Z", "one-ms-after")
  ];
  const sessions = find(summarize({ rows, repoRoot: REPO, since: "2026-09-01T00:00:00.000Z" }), 1)
    .detail.map(d => d.session).sort();
  assert.deepEqual(sessions, ["exact", "one-ms-after"]);
});

test("an event whose timestamp will not parse is excluded from a windowed run, never admitted blindly", () => {
  const events = [statusEvent(1, "Done", { createdAt: "not a date" }), statusEvent(2, "Done", { createdAt: "2026-09-05T00:00:00Z" })];
  assert.deepEqual(find(summarize({ rows: [], events, repoRoot: REPO, since: "2026-09-01" }), 3).detail.throughGate, [2]);
});

// --- 12. Availability: a missing source is not evidence of absence -------------------------

test("an unreadable usage log makes its signals UNMEASURED, never REVERT", () => {
  // §11's consequence for REVERT is "close #477 as not-planned". A disabled recorder or a missing
  // log file must never be able to trigger that.
  const summary = summarize({ rows: [], events: [statusEvent(1, "Done")], repoRoot: REPO, sources: { log: false, activity: true } });
  assert.equal(find(summary, 1).verdict, "UNMEASURED");
  assert.equal(find(summary, 2).verdict, "UNMEASURED");
  assert.equal(find(summary, 4).verdict, "UNMEASURED");
  assert.equal(find(summary, 3).verdict, "PENDING", "the activity trail was readable, so signal 3 still scores");
  assert.equal(summary.overall.verdict, "INCOMPLETE");
  assert.match(summary.overall.reason, /neither ADOPT nor REVERT/);
  assert.match(find(summary, 1).caveat, /^UNMEASURED —/);
});

test("an app that is not answering makes the activity signals UNMEASURED, never REVERT", () => {
  const summary = summarize({ rows: [row()], events: [], repoRoot: REPO, sources: { log: true, activity: false } });
  assert.equal(find(summary, 3).verdict, "UNMEASURED", "an empty array from a stopped app is not zero Done transitions");
  assert.equal(find(summary, 2).verdict, "UNMEASURED", "claim events are event-derived too");
  assert.equal(find(summary, 1).verdict, "PENDING");
  assert.equal(summary.overall.verdict, "INCOMPLETE");
});

test("with both sources down, the report is INCOMPLETE — never the all-REVERT that closes the issue", () => {
  const summary = summarize({ rows: [], events: [], repoRoot: REPO, sources: { log: false, activity: false } });
  assert.deepEqual(summary.signals.map(s => s.verdict), ["UNMEASURED", "UNMEASURED", "UNMEASURED", "UNMEASURED"]);
  assert.equal(summary.overall.verdict, "INCOMPLETE");
  assert.notEqual(summary.overall.verdict, "REVERT");
  assert.deepEqual(summary.sources, {
    log: false, activity: false, activityTruncated: false, activityCapped: false,
    repoRootTrusted: true, cloneProbeCapped: false, cloneProbe: null
  });
});

test("fetchActivity treats an HTTP error as UNAVAILABLE rather than as no events", async () => {
  await assert.rejects(
    () => fetchActivity("http://x", { fetchImpl: async () => ({ ok: false, status: 500, json: async () => ({ message: "boom" }) }) }),
    /HTTP 500/,
    "fetch resolves happily on a 500 — without an ok check that error body used to read as an empty board");
});

test("fetchActivity treats a non-array body as UNAVAILABLE rather than silently skipping it", async () => {
  await assert.rejects(
    () => fetchActivity("http://x", { fetchImpl: okResponse({ message: "Project 1 not found." }) }),
    /did not return a list of projects/);

  await assert.rejects(
    () => fetchActivity("http://x", {
      fetchImpl: routed({
        "/activity": { ok: true, status: 200, json: async () => ({ message: "nope" }) },
        "/api/projects": { ok: true, status: 200, json: async () => [{ id: 1 }] }
      })
    }),
    /did not return a list of events/,
    "the old code skipped a non-array page and reported the rest as the whole trail");
});

test("a page of EXACTLY 500 is capped, not truncated — 501 is what proves events were dropped", async () => {
  // Reported on PR #504, finding 5. `page.length !== ACTIVITY_PAGE` could not tell "the endpoint
  // capped this" from "this project has exactly 500 lifetime events", and with no --since the
  // `oldest <= cutoff` escape is skipped — so a board where nothing had been dropped scored
  // signals 2 and 3 UNMEASURED and exited 1. The report now asks for 501, so a longer page PROVES
  // truncation; a page of exactly 500 is reported as CAPPED and deliberately not gated.
  const page = n => Array.from({ length: n }, (_, i) => statusEvent(i + 1, "Done"));
  const call = n => fetchActivity("http://x", {
    fetchImpl: routed({ "/activity": { ok: true, status: 200, json: async () => page(n) }, "/api/projects": { ok: true, status: 200, json: async () => [{ id: 7 }] } })
  });

  const exactly = await call(500);
  assert.deepEqual(exactly.truncated, [], "exactly 500 events is NOT evidence anything was dropped");
  assert.deepEqual(exactly.capped, [7], "but the doubt is disclosed rather than swallowed");

  const over = await call(501);
  assert.deepEqual(over.truncated, [7], "more than a full window came back, so more exist than it holds");
  assert.deepEqual(over.capped, []);

  const short = await call(499);
  assert.deepEqual(short.truncated, []);
  assert.deepEqual(short.capped, [], "a trail that ended on its own is complete, with no caveat at all");

  // And the consequence at the report level: a 500-event project is SCORED, not gated out.
  const summary = summarize({ rows: [row()], events: page(500), repoRoot: REPO, truncated: exactly.truncated, capped: exactly.capped });
  assert.notEqual(find(summary, 3).verdict, "UNMEASURED", "nothing is known to have been dropped, so nothing may be UNMEASURED");
  assert.notEqual(summary.overall.verdict, "INCOMPLETE");
  assert.match(find(summary, 3).caveat, /ACTIVITY PAGE CAPPED for project\(s\) 7/, "and the reader is told the endpoint could not rule it out");
  assert.match(find(summary, 2).caveat, /ACTIVITY PAGE CAPPED for project\(s\) 7/);
  assert.match(renderReport(summary, { logPath: "x" }), /NOTE:     activity page CAPPED at exactly 500 events for project\(s\) 7/);

  // A PROVEN truncation still gates, so the fix did not simply switch the detector off.
  const gated = summarize({ rows: [row()], events: page(501), repoRoot: REPO, truncated: over.truncated, capped: over.capped });
  assert.equal(find(gated, 3).verdict, "UNMEASURED");
  assert.equal(gated.overall.verdict, "INCOMPLETE");
});

test("a full page that reaches back PAST --since is covered, not harmful — the gate is not a one-way door", async () => {
  // The endpoint caps at 500 with no pagination, so gating on "a page was full" means the moment
  // any single project crosses 500 lifetime events the trial can never be adjudicated again — on a
  // board working exactly as intended. The page returns NEWEST FIRST, so what it drops is the
  // OLDEST events, and those cost the measurement nothing when the page already reaches back past
  // the cutoff. The decision has to live HERE, where a page is still a page: after the fetch there
  // is only a flat event list and no way to tell which project a full page belonged to.
  const page = (n, at) => Array.from({ length: n }, (_, i) => statusEvent(i + 1, "Done", { projectId: 7, createdAt: at }));
  const call = (events, since) => fetchActivity("http://x", {
    since,
    fetchImpl: routed({ "/activity": { ok: true, status: 200, json: async () => events }, "/api/projects": { ok: true, status: 200, json: async () => [{ id: 7 }] } })
  });

  // 501 events, so the page is PROVEN to have been cut short rather than merely capped — the
  // coverage question this test is about is then the only thing left deciding the outcome.
  const covered = await call(page(501, "2026-08-01T00:00:00"), "2026-09-01");
  assert.deepEqual(covered.truncated, [], "the oldest event returned predates the window, so nothing in the window was dropped");
  assert.deepEqual(covered.truncatedCovered, [7], "and it is still reported");

  const harmful = await call(page(501, "2026-09-05T00:00:00"), "2026-09-01");
  assert.deepEqual(harmful.truncated, [7], "the whole page sits inside the window, so older in-window events fell off");
  assert.deepEqual(harmful.truncatedCovered, []);

  const noWindow = await call(page(501, "2026-08-01T00:00:00"), null);
  assert.deepEqual(noWindow.truncated, [7], "with no --since there is no floor to compare against");

  // The coverage escape applies to a merely-CAPPED page too: whether or not the endpoint cut the
  // page short, everything it could have dropped is older than the cutoff.
  const cappedButCovered = await call(page(500, "2026-08-01T00:00:00"), "2026-09-01");
  assert.deepEqual(cappedButCovered.truncatedCovered, [7]);
  assert.deepEqual(cappedButCovered.capped, [], "no doubt is left to disclose once the page demonstrably spans the window");

  // And the consequence: the covered case is SCORED rather than gated, so a busy board can still
  // be adjudicated by narrowing the window.
  //
  // The 422 row is here to satisfy signal 3's OTHER precondition — the vacuity guard, which holds
  // five Dones at PENDING until the gate is shown to have refused something. Without it this
  // asserts PENDING, which would prove nothing about truncation: the point being pinned is that
  // `truncatedCovered` does not produce UNMEASURED, so the fixture has to clear every unrelated
  // reason signal 3 could fall short of ADOPT.
  const scoredRows = [row(), row({ tool: "update_task_status", taskId: 9, outcome: "error", http: 422 })];
  const scored = summarize({ rows: scoredRows, events: [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done")), repoRoot: REPO, since: "2026-09-01", truncated: [], truncatedCovered: [7] });
  assert.equal(find(scored, 3).verdict, "ADOPT");
  assert.notEqual(find(scored, 3).verdict, "UNMEASURED", "truncatedCovered must not gate the signal");
  assert.equal(find(scored, 2).verdict, "PENDING", "PENDING from the vacuity guard, not UNMEASURED from truncation");
  assert.notEqual(scored.overall.verdict, "INCOMPLETE");
  const text = renderReport(scored, { logPath: "x" });
  assert.match(text, /NOTE:     activity truncated at 500 events for project\(s\) 7/);
  assert.match(text, /Not gated/);
  assert.doesNotMatch(text, /WARNING:/);
});

test("two projects, one busy: only the harmful one gates, and both are named correctly", async () => {
  const busy = Array.from({ length: 501 }, (_, i) => statusEvent(i + 1, "Done", { projectId: 2, createdAt: "2026-09-05T00:00:00" }));
  const quiet = [statusEvent(1, "Done", { projectId: 3, createdAt: "2026-09-05T00:00:00" })];
  const { truncated, truncatedCovered } = await fetchActivity("http://x", {
    since: "2026-09-01",
    fetchImpl: async url => ({
      ok: true, status: 200,
      json: async () => (url.includes("/projects/2/activity") ? busy : url.includes("/projects/3/activity") ? quiet : [{ id: 2 }, { id: 3 }])
    })
  });
  assert.deepEqual(truncated, [2]);
  assert.deepEqual(truncatedCovered, []);
});

// --- 12b. Every fetch has a deadline --------------------------------------------------------

test("a server that accepts the connection and never answers is UNAVAILABLE, not a hang", async () => {
  // Node's fetch has NO default timeout. A server that completes the TCP handshake and then never
  // responds left this awaiting forever: no verdict, no exit code, and every UNMEASURED path
  // bypassed because nothing ever threw. It happened for real with the app paged out but still
  // LISTENing — and it is the opposite behaviour to a REFUSED port, which renders cleanly.
  const seen = [];
  const stalling = async (url, options) => {
    seen.push(options?.signal);
    return new Promise((_, reject) => options.signal.addEventListener("abort", () => reject(options.signal.reason)));
  };
  await assert.rejects(() => fetchActivity("http://x", { fetchImpl: stalling, timeoutMs: 25 }),
    /did not answer within 25 ms/);
  assert.ok(seen[0] instanceof AbortSignal, "the deadline must be handed to fetch, not merely raced against");
});

test("EVERY fetch carries the deadline, including the per-project activity pages", async () => {
  // The projects call and the activity calls are separate fetches; a timeout on only the first
  // still hangs forever on a board whose project list answers and whose activity page does not.
  const signals = [];
  const fetchImpl = async (url, options) => {
    signals.push({ url, signal: options?.signal });
    if (url.includes("/activity")) return new Promise((_, reject) => options.signal.addEventListener("abort", () => reject(options.signal.reason)));
    return { ok: true, status: 200, json: async () => [{ id: 1 }] };
  };
  // limit=501, not 500: the report asks for one MORE than a full page so a longer answer proves
  // truncation. Pinned here because it is the request that makes finding 5's fix possible at all.
  await assert.rejects(() => fetchActivity("http://x", { fetchImpl, timeoutMs: 25 }), /activity\?limit=501 did not answer within 25 ms/);
  assert.equal(signals.length, 2);
  assert.ok(signals.every(s => s.signal instanceof AbortSignal));
});

test("a non-timeout fetch failure keeps its own message — only a stall is reworded", async () => {
  await assert.rejects(
    () => fetchActivity("http://x", { fetchImpl: async () => { throw new Error("connect ECONNREFUSED 127.0.0.1:5123"); } }),
    /ECONNREFUSED/,
    "a refused connection must not be relabelled as a timeout");
});

test("the git call is bounded too — the same unbounded-blocking shape", () => {
  // Observe the real child-process boundary, rather than its position/spelling in source text.
  const result = runGitDeadlineFixture(0);
  assert.equal(result.success, true, result.message);
  assert.equal(result.timeouts.length, 3, "version, protected capture and identity must all be observed");
  assert.deepEqual(result.maxBuffers, [256 * 1024, 256 * 1024, 256 * 1024], "every Git phase must cap native output bytes");
  assert.ok(result.timeouts.every(timeout => Number.isInteger(timeout) && timeout > 0 && timeout <= 5000),
    "every Git subprocess must have a finite positive timeout within the five-second bound");
});

test("a TRUNCATED activity fetch makes the event-derived signals UNMEASURED, not a lower bound", () => {
  // The endpoint returns NEWEST FIRST and caps at 500 with no offset, so truncation drops the
  // OLDEST events — and a TaskDoneOverridden can be lost while the Done it qualifies survives,
  // scoring an overridden task as having passed the gate. That is an OVER-count, so "LOWER BOUND"
  // was false in exactly the direction that manufactures an adopt.
  const events = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done"));
  const summary = summarize({ rows: [row()], events, repoRoot: REPO, truncated: [2] });
  assert.equal(find(summary, 3).verdict, "UNMEASURED", "five Done events must NOT render as ADOPT off a truncated page");
  assert.equal(find(summary, 2).verdict, "UNMEASURED");
  assert.equal(find(summary, 1).verdict, "PENDING", "signal 1 is log-derived and unaffected");
  assert.equal(summary.overall.verdict, "INCOMPLETE");
  assert.match(find(summary, 3).caveat, /TRUNCATED/);
});

test("the truncation warning names the direction correctly", () => {
  const text = renderReport(summarize({ rows: [row()], repoRoot: REPO, truncated: [2] }), { logPath: "C:\\log.jsonl" });
  assert.match(text, /NEWEST FIRST/);
  assert.match(text, /OLDEST events were dropped/);
  assert.match(text, /NOT a lower bound/);
  assert.doesNotMatch(text, /signal 3 is a LOWER BOUND/,
    "the old warning claimed a lower bound, which is false in the direction that matters");
});

// --- 13. Aggregation -----------------------------------------------------------------------

test("an all-revert run reports REVERT and names §11's consequence", () => {
  // The old version of this test asserted MIXED twice and never once reached REVERT, so the branch
  // it claimed to guard — and §11's consequence string, the sentence that closes the issue — was
  // never executed. Every signal is driven to REVERT here deliberately:
  //   1: nobody called get_next_tasks                       -> 0
  //   2: two sessions wrote task 7 and neither held a claim -> 1
  //   3: no Done transitions                                -> 0
  //   4: every session ran inside the repo                  -> no
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7, cwd: `${REPO}\\src` }),
    row({ session: "b", tool: "update_task_status", taskId: 7, to: "Review", cwd: `${REPO}\\scripts` })
  ];
  const summary = summarize({ rows, events: [], repoRoot: REPO });
  assert.deepEqual(summary.signals.map(s => s.verdict), ["REVERT", "REVERT", "REVERT", "REVERT"]);
  assert.equal(summary.overall.verdict, "REVERT");
  assert.equal(summary.overall.reason,
    "All four signals met their revert threshold. Per §11: remove the mcpServers entry and close #477 as not-planned.");
  assert.match(renderReport(summary, { logPath: "C:\\log.jsonl" }), /OVERALL: REVERT — All four signals met their revert threshold/);
});

test("a genuinely EMPTY trial is MIXED, not REVERT — nothing happened is not the same as it failed", () => {
  const summary = summarize({ rows: [], events: [], repoRoot: REPO });
  // Signal 2 is PENDING when nothing wrote, so an empty trial cannot be unanimous.
  assert.equal(find(summary, 2).verdict, "PENDING");
  assert.equal(summary.overall.verdict, "MIXED");

  const used = summarize({
    rows: [row({ session: "a", tool: "claim_task", taskId: 1 }), row({ session: "b", tool: "claim_task", taskId: 2 })],
    events: [], repoRoot: REPO
  });
  assert.equal(find(used, 1).verdict, "REVERT", "nobody called get_next_tasks");
  assert.equal(used.overall.verdict, "MIXED");
});

test("a split verdict is handed back rather than aggregated by an invented rule", () => {
  const summary = summarize({ rows: [row()], repoRoot: REPO });
  assert.equal(summary.overall.verdict, "MIXED");
  assert.match(summary.overall.reason, /no aggregation rule for a split/);
});

test("all four adopting reports ADOPT", () => {
  const rows = [
    row({ session: "a", cwd: "C:\\other" }), row({ session: "b", cwd: "C:\\other" }), row({ session: "c", cwd: "C:\\other" }),
    row({ session: "a", tool: "claim_task", taskId: 1, cwd: "C:\\other" }),
    row({ session: "b", tool: "claim_task", taskId: 2, cwd: "C:\\other" }),
    row({ session: "a", tool: "update_task_status", taskId: 9, cwd: "C:\\other", outcome: "error", http: 422 })
  ];
  const events = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done"));
  const summary = summarize({ rows, events, repoRoot: REPO });
  assert.equal(summary.overall.verdict, "ADOPT");
});

test("a single UNMEASURED signal outranks an otherwise unanimous ADOPT", () => {
  const rows = [
    row({ session: "a", cwd: "C:\\other" }), row({ session: "b", cwd: "C:\\other" }), row({ session: "c", cwd: "C:\\other" }),
    row({ session: "a", tool: "claim_task", taskId: 1, cwd: "C:\\other" }),
    row({ session: "b", tool: "claim_task", taskId: 2, cwd: "C:\\other" })
  ];
  const events = [1, 2, 3, 4, 5].map(id => statusEvent(id, "Done"));
  const summary = summarize({ rows, events, repoRoot: REPO, truncated: [1] });
  assert.equal(summary.overall.verdict, "INCOMPLETE");
  assert.match(summary.overall.reason, /#2, #3/);
});

// --- 14. Output ----------------------------------------------------------------------------

test("the rendered report carries the table, the verdict and the source lines", () => {
  const summary = summarize({ rows: [row()], repoRoot: REPO });
  const text = renderReport(summary, { logPath: "C:\\log.jsonl", malformed: 3 });
  assert.match(text, /\| # \| Signal \| Adopt \| Revert \| Measured \| Verdict \|/);
  assert.match(text, /OVERALL: MIXED/);
  assert.match(text, /3 unparseable log line/);
  assert.match(text, /Sources:  usage log available, activity trail available/);
});

test("the rendered DATA ROWS carry each signal's own measured value and verdict", () => {
  // The old render test asserted only the header row and the OVERALL line, so a renderer that
  // printed `0 | ADOPT` on every row passed it. These assertions are built from four signals with
  // four DIFFERENT (value, verdict) pairs, so a row that borrows another's numbers goes red.
  const rows = [
    row({ session: "a", cwd: "C:\\other" }), row({ session: "b", cwd: "C:\\other" }), row({ session: "c", cwd: "C:\\other" }),
    row({ session: "a", tool: "update_task", taskId: 7, cwd: "C:\\other" }),
    row({ session: "b", tool: "update_task", taskId: 7, cwd: "C:\\other" })
  ];
  const events = [statusEvent(1, "Done"), statusEvent(2, "Done")];
  const summary = summarize({ rows, events, repoRoot: REPO });

  // Independently derived expectations, not read back off the object being rendered.
  assert.deepEqual(summary.signals.map(s => [s.value, s.verdict]),
    [[3, "ADOPT"], [1, "REVERT"], [2, "PENDING"], ["yes", "ADOPT"]]);

  const text = renderReport(summary, { logPath: "C:\\log.jsonl" });
  assert.ok(text.includes("| 1 | Distinct sessions invoking get_next_tasks | >= 3 | 0 | 3 | ADOPT |"), text);
  assert.ok(text.includes("| 2 | Uncoordinated concurrent-session overlaps after wiring | 0 | >= 1 | 1 | REVERT |"), text);
  assert.ok(text.includes("| 3 | Tasks reaching Done through the DoD gate | >= 5 | 0 | 2 | PENDING |"), text);
  assert.ok(text.includes("| 4 | Board consulted for work outside KanbanFlow-v2/ | yes | no | yes | ADOPT |"), text);
});

test("the table does not claim UNPROMPTEDNESS, which the caveat below it retracts", () => {
  // The row an adjudicator pastes into #477 said "Sessions invoking get_next_tasks unprompted"
  // while "UNPROMPTED-NESS IS NOT MEASURED" sat in prose several screens further down. The table
  // must not assert the property the instrument cannot see.
  const summary = summarize({ rows: [row()], repoRoot: REPO });
  const s1 = find(summary, 1);
  assert.equal(s1.name, "Distinct sessions invoking get_next_tasks");
  assert.doesNotMatch(s1.name, /unprompted/i);
  assert.match(s1.caveat, /UNPROMPTED-NESS IS NOT MEASURED/, "and the caveat still says so");
  assert.doesNotMatch(renderReport(summary, { logPath: "x" }).split("\n## ")[0], /unprompted/i,
    "nor anywhere in the table block");
});

test("an UNMEASURED signal prints no measured value — a number there reads as a measurement", () => {
  // `| 1 | ... | 1 | UNMEASURED |` reads at a glance as one collision, from a signal whose verdict
  // disowns the number beside it.
  const summary = summarize({
    rows: [row({ session: "a", tool: "update_task", taskId: 7 }), row({ session: "b", tool: "update_task", taskId: 7 })],
    events: [], repoRoot: REPO, sources: { log: true, activity: false }
  });
  assert.equal(find(summary, 2).verdict, "UNMEASURED");
  assert.equal(find(summary, 2).value, 1, "the value is still computed and still in the JSON");
  const text = renderReport(summary, { logPath: "C:\\log.jsonl" });
  assert.ok(text.includes("| 2 | Uncoordinated concurrent-session overlaps after wiring | 0 | >= 1 | — | UNMEASURED |"), text);
  assert.doesNotMatch(text.split("\n## ")[0], /\| 1 \| UNMEASURED \|/);
});

test("the header lines carry the window bounds, the measured counts and the roots", () => {
  // None of these four lines was asserted by any render test: `Measured:`, `Window:`, `Roots:` and
  // the per-signal `detail` dump could all have been dropped or transposed silently.
  const rows = [
    row({ session: "a", ts: "2026-09-01T08:00:00.000Z" }),
    row({ session: "b", ts: "2026-09-04T17:30:00.000Z" })
  ];
  const events = [statusEvent(1, "Done"), statusEvent(2, "Done")];
  const summary = summarize({ rows, events, repoRoots: ["C:\\repo", "C:\\repo\\.worktrees\\wt"], repoRootSource: "git", since: "2026-09-01" });
  const text = renderReport(summary, { logPath: "C:\\log.jsonl", malformed: 2 });

  assert.ok(text.includes("Window:   2026-09-01  (2026-09-01T08:00:00.000Z .. 2026-09-04T17:30:00.000Z)"), text);
  assert.ok(text.includes("Measured: 2 tool calls, 2 activity events, 2 unparseable log line(s)"), text);
  assert.ok(text.includes("Roots:    C:\\repo, C:\\repo\\.worktrees\\wt  [git]"), text);

  // And the `detail` JSON dump, which is the evidence every caveat points at.
  const dumped = text.split("```json")[1].split("```")[0];
  assert.deepEqual(JSON.parse(dumped), summary.signals[0].detail, "signal 1's detail is rendered verbatim");
  assert.equal(text.split("```json").length - 1, 4, "one dump per signal");
  assert.match(text, /### 3\. Tasks reaching Done through the DoD gate — PENDING/);
  assert.ok(text.includes(JSON.stringify(summary.signals[2].detail, null, 1)), "signal 3's detail too");

  // With no window and no malformed lines the same line reports the whole record.
  const all = renderReport(summarize({ rows, events, repoRoot: REPO }), { logPath: "C:\\log.jsonl" });
  assert.ok(all.includes("Window:   all recorded time  (2026-09-01T08:00:00.000Z .. 2026-09-04T17:30:00.000Z)"), all);
  assert.ok(all.includes("Measured: 2 tool calls, 2 activity events\n"), all);
  assert.doesNotMatch(all, /unparseable/);
});

test("an untrusted repo root is flagged on the Roots line, not only in the caveat", () => {
  const text = renderReport(summarize({ rows: [row()], repoRoots: ["C:\\wt"], repoRootSource: "fallback" }), { logPath: "x" });
  assert.match(text, /Roots:    C:\\wt  \[fallback\]  NOT TRUSTED — signal 4 is UNMEASURED/);
});

test("the report says plainly when a source was unavailable", () => {
  const summary = summarize({ rows: [], events: [], repoRoot: REPO, sources: { log: false, activity: false } });
  const text = renderReport(summary, { logPath: null, generations: 0 });
  assert.match(text, /usage log UNAVAILABLE, activity trail UNAVAILABLE/);
  assert.match(text, /OVERALL: INCOMPLETE/);
  assert.ok(text.includes("| 3 | Tasks reaching Done through the DoD gate | >= 5 | 0 | — | UNMEASURED |"), text);
});

// --- 15. CLI -------------------------------------------------------------------------------

test("--since is validated, never silently swallowed or silently NaN", () => {
  // Reported by Copilot on PR #485. Same shape as #468 (portfolio-sync consuming --dry-run as an
  // option value): all three failures end in a confident table computed from nothing.
  assert.deepEqual(parseArgs(["--since", "2026-08-30"]), { since: "2026-08-30", json: false, repoRoot: null });
  assert.deepEqual(parseArgs(["--json"]), { since: null, json: true, repoRoot: null });
  assert.deepEqual(parseArgs([]), { since: null, json: false, repoRoot: null });

  assert.throws(() => parseArgs(["--since"]), /last argument/, "trailing --since must not be ignored");
  assert.throws(() => parseArgs(["--since", "--json"]), /the option --json/, "must not eat the next flag");
  assert.throws(() => parseArgs(["--since", "yesterday"]), /not a date/);
  assert.throws(() => parseArgs(["--sicne", "2026-08-30"]), /Unknown option/, "a typo'd flag is not silently dropped");
});

test("--repo-root takes a directory and is guarded the same way", () => {
  assert.deepEqual(parseArgs(["--repo-root", "C:\\repo"]), { since: null, json: false, repoRoot: "C:\\repo" });
  assert.deepEqual(parseArgs(["--repo-root", "C:\\repo", "--json"]).json, true);
  assert.throws(() => parseArgs(["--repo-root"]), /last argument/);
  assert.throws(() => parseArgs(["--repo-root", "--json"]), /the option --json/);
});

test("summarize refuses an unparseable cutoff rather than excluding everything", () => {
  // The dangerous degradation: NaN comparisons are all false, so every row drops out and four
  // empty signals render as a REVERT-shaped verdict for a trial nobody actually read.
  assert.throws(() => summarize({ rows: [row()], repoRoot: REPO, since: "not-a-date" }), /not a parseable date/);
  assert.doesNotThrow(() => summarize({ rows: [row()], repoRoot: REPO, since: null }));
});

// `main()` sets `sources.log` and `sources.activity` from two INDEPENDENT reads, and the single
// both-sources-down test below kills both assignments at once: inverting either one alone left the
// suite green, because the summary was UNMEASURED either way. These two runs separate them — one
// with only the log missing, one with only the app missing.
const runReport = (env, args = []) => spawnSync(process.execPath, [path.join(HERE, "trial-report.mjs"), ...args], {
  encoding: "utf8",
  env: { ...process.env, ...env }
});
// The variant used when the test is ALSO the board: `spawnSync` blocks this process's event loop,
// so an in-test http server would never accept the child's connection and the run would deadlock
// until the fetch deadline fired.
const runReportAsync = (env, args = []) => new Promise(resolve => {
  const child = spawn(process.execPath, [path.join(HERE, "trial-report.mjs"), ...args], { env: { ...process.env, ...env } });
  let stdout = "";
  let stderr = "";
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", chunk => { stdout += chunk; });
  child.stderr.on("data", chunk => { stderr += chunk; });
  child.on("close", status => resolve({ status, stdout, stderr }));
});

test("recorder OFF but the app ANSWERING: the log is unavailable and the activity trail is not", async () => {
  // A stub board, not the app: one Node http server, closed at the end of the test.
  const server = http.createServer((req, res) => {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(req.url.includes("/activity") ? "[]" : JSON.stringify([{ id: 1 }]));
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  try {
    const result = await runReportAsync({
      KANBANFLOW_USAGE_LOG: "off",
      KANBANFLOW_API_URL: `http://127.0.0.1:${server.address().port}`
    });
    assert.match(result.stdout, /Sources:  usage log UNAVAILABLE, activity trail available/, result.stdout + result.stderr);
    assert.ok(result.stdout.includes("| 3 | Tasks reaching Done through the DoD gate | >= 5 | 0 | 0 | REVERT |"),
      "the activity trail WAS read, so signal 3 is scored from it");
    assert.match(result.stdout, /\| 1 \| .* \| — \| UNMEASURED \|/);
    assert.equal(result.status, 1);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});

test("recorder ON but the app DEAD: the log is available and the activity trail is not", () => {
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(log, [
    JSON.stringify(row({ session: "a" })),
    JSON.stringify(row({ session: "b" })),
    ""
  ].join("\n"));
  try {
    const result = runReport({ KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" });
    assert.match(result.stdout, /Sources:  usage log available, activity trail UNAVAILABLE/, result.stdout + result.stderr);
    assert.ok(result.stdout.includes("| 1 | Distinct sessions invoking get_next_tasks | >= 3 | 0 | 2 | PENDING |"),
      "the log WAS read, so signal 1 is scored from it");
    assert.match(result.stdout, /1 of 1 generation\(s\) read/);
    assert.equal(result.status, 1, "the activity signals are still unmeasured, so the run is INCOMPLETE");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("a log in which NOTHING parses is UNMEASURED, not REVERT", () => {
  // `logAvailable` asked whether the file was READ, never whether anything in it PARSED. Two torn
  // lines rendered `0 tool calls, 2 unparseable log line(s)` and then scored signals 1 and 4
  // REVERT off it — and §11's consequence for REVERT is closing #477 as not-planned.
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(log, '{"ts":"2026-09-01T10:00:00.000Z","to\n{"ts":"2026-09-01T10:00:01.000Z","to\n');
  try {
    const result = runReport({ KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" });
    assert.match(result.stdout, /usage log UNAVAILABLE/, result.stdout + result.stderr);
    assert.match(result.stdout, /2 unparseable log line\(s\)/, "and the corruption is still reported");
    assert.doesNotMatch(result.stdout, /OVERALL: REVERT/, "a corrupt log must never produce the verdict that closes #477");
    assert.doesNotMatch(result.stdout.split("## What each number")[0], /\| REVERT \|/, "and no signal row may read REVERT either");
    assert.match(result.stderr, /every one of the 2 line\(s\)/);
    assert.equal(result.status, 1);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("an EMPTY log is still available — the recorder worked and recorded nothing", () => {
  // The other side of the line above: no content is not corrupt content. A working recorder that
  // saw no calls is real evidence, and signal 1 should score REVERT from it.
  const dir = tmp();
  const log = path.join(dir, "mcp-usage.jsonl");
  fs.writeFileSync(log, "");
  try {
    const result = runReport({ KANBANFLOW_USAGE_LOG: log, KANBANFLOW_API_URL: "http://127.0.0.1:1" });
    assert.match(result.stdout, /usage log available/, result.stdout + result.stderr);
    assert.ok(result.stdout.includes("| 1 | Distinct sessions invoking get_next_tasks | >= 3 | 0 | 0 | REVERT |"), result.stdout);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("a run that could not measure anything EXITS NON-ZERO", () => {
  // End-to-end through main(): recording disabled and no app answering. Exit 0 would tell a
  // caller the verdict was in.
  const result = spawnSync(process.execPath, [path.join(HERE, "trial-report.mjs")], {
    encoding: "utf8",
    env: { ...process.env, KANBANFLOW_USAGE_LOG: "off", KANBANFLOW_API_URL: "http://127.0.0.1:1" }
  });
  assert.equal(result.status, 1, `expected exit 1, got ${result.status}\n${result.stdout}\n${result.stderr}`);
  assert.match(result.stdout, /OVERALL: INCOMPLETE/);
  assert.match(result.stdout, /UNMEASURED/);
  assert.doesNotMatch(result.stdout, /OVERALL: REVERT/);
});

// --- 16. Drift guards ----------------------------------------------------------------------

test("path semantics follow the INPUTS, not the host the report happens to run on", () => {
  // Reported by Copilot on PR #485. The rows are written on Windows; the report is a Node script
  // that may run anywhere. Under POSIX semantics `path.relative` does not know that `\` separates
  // or that a drive letter is case-insensitive, so a session that never left the repo would count
  // as evidence the board was consulted from outside it — inflating signal 4.
  assert.equal(pathFlavor("C:\\Repo"), path.win32);
  assert.equal(pathFlavor("\\\\srv\\share"), path.win32);
  assert.equal(pathFlavor("/home/owner/repo"), path.posix);

  assert.equal(isOutsideRepo("C:\\Repo\\scripts", "C:\\repo"), false, "case-differing drive path is inside");
  assert.equal(isOutsideRepo("\\\\srv\\share\\repo\\a", "\\\\srv\\share\\repo"), false, "UNC path is inside");
  assert.equal(isOutsideRepo("C:\\Repo-scratch", "C:\\Repo"), true, "sibling prefix is still outside");

  // THE HOST-INDEPENDENCE ASSERTION, and the reason it is written this way: this suite runs on
  // Windows, where `path === path.win32`, so any test that merely exercises Windows paths passes
  // whether the code picks the flavor deliberately or just inherits the host's. It cannot tell a
  // fix from the bug. Posix paths that differ only in CASE are the case where the two flavors
  // genuinely disagree — win32 folds case and calls this inside, posix does not — so this line
  // goes red on Windows the moment the flavor stops being chosen from the inputs.
  assert.equal(isOutsideRepo("/home/owner/Repo/src", "/home/owner/repo"), true,
    "posix paths are case-SENSITIVE; picking the flavor from the host would wrongly call this inside");
  assert.equal(isOutsideRepo("/home/owner/repo/src", "/home/owner/repo"), false);
});

test("every WRITE_TOOLS and MUTATING_TOOLS name still exists in the wrapper — a rename must go red here", () => {
  const wrapper = fs.readFileSync(path.join(HERE, "mcp-server.mjs"), "utf8");
  for (const tool of MUTATING_TOOLS) {
    assert.ok(wrapper.includes(`case "${tool}"`),
      `${tool} is in MUTATING_TOOLS but has no handler in mcp-server.mjs — the collision detector ` +
      "would silently stop counting writes through it");
  }
  for (const tool of WRITE_TOOLS) {
    assert.ok(MUTATING_TOOLS.has(tool), `${tool} must also be a mutating tool`);
  }
  // The names the vacuity guard was blind to, pinned by name so a future trim goes red.
  for (const tool of ["create_task", "promote_idea", "create_dependency"]) {
    assert.ok(MUTATING_TOOLS.has(tool), `${tool} must count as a board mutation`);
  }
});

// --- Review findings on PR #504 ------------------------------------------------------------

test("normalizeTaskId mirrors the wrapper's grammar exactly, not Number()'s", () => {
  // Reported on PR #504. `coerceInt` (mcp-server.mjs) accepts a base-10 integer string and
  // NOTHING else — it rejects hex, exponent and whitespace-padded forms BEFORE coercing. A bare
  // Number() accepted all three, so this report could resolve a task id the board never took,
  // merging rows into a collision that never happened or splitting a real one apart.
  assert.equal(normalizeTaskId("0x10"), null, "hex is rejected by coerceInt, so it must be here");
  assert.equal(normalizeTaskId("1e3"), null, "exponent likewise");
  assert.equal(normalizeTaskId(" 3 "), null, "whitespace-padded likewise");
  assert.equal(normalizeTaskId("7.5"), null);
  assert.equal(normalizeTaskId("7abc"), null);
  // ...while everything the wrapper DOES accept still resolves to one bucket.
  assert.equal(normalizeTaskId("42"), 42);
  assert.equal(normalizeTaskId("007"), 7);
  assert.equal(normalizeTaskId("+42"), 42);
  assert.equal(normalizeTaskId(42), 42);
});


test("a misconfigured log path degrades to UNMEASURED instead of aborting the run", async () => {
  // Reported on PR #504. resolveUsageLogPath() throws for a relative KANBANFLOW_USAGE_LOG, and
  // main() called it unguarded — so the whole run aborted at exit 2 and signal 3 was discarded,
  // even though signal 3 is measured entirely from ActivityEvent rows and does not touch the log.
  // A source that cannot be read must degrade, never prevent adjudication.
  const { execFileSync } = await import("node:child_process");
  const here = path.dirname(fileURLToPath(import.meta.url));
  let out = "", code = 0;
  try {
    out = execFileSync(process.execPath, [path.join(here, "trial-report.mjs")], {
      env: { ...process.env, KANBANFLOW_USAGE_LOG: "relative/path.jsonl", KANBANFLOW_API_URL: "http://127.0.0.1:1" },
      encoding: "utf8", stdio: ["ignore", "pipe", "pipe"]
    });
  } catch (e) { out = (e.stdout || "") + (e.stderr || ""); code = e.status ?? 1; }

  assert.match(out, /misconfigured/i, "the operator is told why the log could not be used");
  assert.match(out, /^\| # \| Signal/m, "the table is still rendered");
  assert.match(out, /^OVERALL/m, "a verdict line is still produced");
  assert.ok(code !== 0, "and the run still exits non-zero");
});

test("a row with no session is malformed, and cannot become a participant", () => {
  // Reported on PR #504. parseRows validated only tool and ts, so a session-less row reached
  // summarize and keyed every participant map on `undefined`. Every such row collapses to that
  // one key, so the log gains exactly one session no recorder ever minted — and signal 1's adopt
  // threshold is >= 3, so two genuine sessions plus one session-less row scored ADOPT. That is
  // the fabricated-adopt direction this file exists to prevent.
  const noSession = { ts: "2026-09-01T10:00:02.000Z", tool: "get_next_tasks", cwd: "C:\\other", outcome: "ok", ms: 1 };
  const parsed = parseRows(JSON.stringify(noSession));
  assert.equal(parsed.rows.length, 0, "a session-less row is not usable");
  assert.equal(parsed.malformed, 1, "and it is COUNTED, not silently dropped");

  // summarize is exported and documented as the pure adjudication entry point, so it must not
  // rely on the caller having sanitised its rows.
  const real = n => ({ ts: "2026-09-01T10:00:0" + n + ".000Z", session: "s" + n,
    tool: "get_next_tasks", cwd: "C:\\other", outcome: "ok", ms: 1 });
  const s1 = summarize({ rows: [real(0), real(1), noSession], events: [], repoRoots: ["C:\\repo"],
    repoRootSource: "git", sources: { log: true, activity: true } }).signals.find(s => s.id === 1);
  assert.equal(s1.value, 2, "two real sessions, not three");
  assert.notEqual(s1.verdict, "ADOPT", "a phantom session must not carry the >= 3 threshold");
});

// Placed LAST on purpose: node:test runs this file's tests sequentially (measured directly, not
// assumed — see section 5c), so by the time this runs, every real-git test above (initRealRepo,
// buildCloneFixture, buildRuleAFixture, gitOutput's own env-scrub tests) has already run to
// completion, each in its own os.tmpdir() directory, each with its own cleanup. This worktree's own
// HEAD must be untouched by every one of them.
test("this worktree's own HEAD is unchanged after every real-git test above", t => {
  if (HEAD_AT_MODULE_LOAD === null) {
    t.skip("no HEAD snapshot: git could not read this file's checkout when the file loaded");
    return;
  }
  const headNow = gitOutput(["rev-parse", "HEAD"], HERE);
  assert.equal(headNow, HEAD_AT_MODULE_LOAD,
    "this worktree's own HEAD must not move during a run that builds real git repos elsewhere");
});
