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
  fetchActivity, ActivityHttpError, dedupeRows, buildIdentityBridge, normalizeActor, WRITE_TOOLS,
  MUTATING_TOOLS
} from "./trial-report.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = "C:\\Users\\Owner\\Documents\\Claude\\KanbanFlow-v2";
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
    row({ session: "b", tool: "add_task_comment", taskId: 7 })
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
    row({ session: "b", tool: "add_task_comment", taskId: 7 }),
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
    row({ session: "b", tool: "add_task_comment", taskId: 7 })
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
    row({ session: "b", tool: "add_task_comment", taskId: "7" })
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
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 7, actor: "agent-a" }),
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "b", tool: "add_task_comment", taskId: 7, actor: "agent-b" })
  ];
  // B's claim exists only as an event, under B's ACTOR name.
  const events = [{ id: 5, projectId: 2, entityId: 7, entityType: "Task", eventType: "TaskClaimed", actor: "Agent-B", createdAt: "2026-09-01T09:00:00" }];
  const s2 = find(summarize({ rows, events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 0, "both writers held the task, one of them out of band");
  assert.deepEqual(s2.detail.coordinatedOverlaps[0].writers, ["session:a", "session:b"]);
  assert.deepEqual(s2.detail.coordinatedOverlaps[0].unheld, []);
  assert.ok(s2.detail.claimHolders[0].holders.includes("actor:agent-b"), "actor identity is case-insensitive, as the server treats it");

  // Without the bridging row, the same event names an actor nobody can be shown to be, and the
  // overlap stands. This is the assertion that keeps the bridge from degrading into "assume equal".
  const unbridged = find(summarize({ rows: rows.map(r => ({ ...r, actor: undefined })), events, repoRoot: REPO }), 2);
  assert.equal(unbridged.value, 1);
  assert.deepEqual(unbridged.detail.uncoordinatedOverlaps[0].unheld, ["session:b"],
    "B's UI claim can no longer be tied to B, so B is not a holder — A still is, from its own claim row");
});

test("the identity bridge is built from rows that carry BOTH names, and never invents one", () => {
  const bridge = buildIdentityBridge([
    { session: "s1", actor: "Agent-A" },
    { session: "s1", actor: "agent-a" },        // same actor, different case
    { session: "s2", actor: "agent-b" },
    { session: "s3" },                          // no actor: contributes nothing
    { actor: "orphan" },                        // no session: contributes nothing
    { session: "s4", actor: "   " }             // whitespace is not an actor
  ]);
  assert.deepEqual([...bridge.sessionToActors.keys()], ["s1", "s2"]);
  assert.deepEqual([...bridge.sessionToActors.get("s1")], ["agent-a"], "case-folded to one identity");
  assert.deepEqual([...bridge.actorToSessions.get("agent-b")], ["s2"]);
  assert.equal(bridge.actorToSessions.has("orphan"), false);
  assert.equal(normalizeActor("  Mcp-Agent "), "mcp-agent");
  assert.equal(normalizeActor(""), null);
  assert.equal(normalizeActor(undefined), null);
});

test("a claim event with NO actor vouches for nobody — it is counted, not credited", () => {
  // `ActivityEvent.Actor` is nullable and the UI does not always set it. An anonymous claim used to
  // mark the task "claimed" and excuse every writer on it.
  const rows = [
    row({ session: "a", tool: "update_task", taskId: 7 }),
    row({ session: "b", tool: "add_task_comment", taskId: 7 })
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
  assert.deepEqual(summary.sources, { log: false, activity: false, activityTruncated: false, repoRootTrusted: true });
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

test("fetchActivity reports a full page as truncated, and a short page as complete", async () => {
  const page = n => Array.from({ length: n }, (_, i) => statusEvent(i + 1, "Done"));
  const full = await fetchActivity("http://x", {
    fetchImpl: routed({ "/activity": { ok: true, status: 200, json: async () => page(500) }, "/api/projects": { ok: true, status: 200, json: async () => [{ id: 7 }] } })
  });
  assert.deepEqual(full.truncated, [7]);
  const short = await fetchActivity("http://x", {
    fetchImpl: routed({ "/activity": { ok: true, status: 200, json: async () => page(499) }, "/api/projects": { ok: true, status: 200, json: async () => [{ id: 7 }] } })
  });
  assert.deepEqual(short.truncated, []);
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

  const covered = await call(page(500, "2026-08-01T00:00:00"), "2026-09-01");
  assert.deepEqual(covered.truncated, [], "the oldest event returned predates the window, so nothing in the window was dropped");
  assert.deepEqual(covered.truncatedCovered, [7], "and it is still reported");

  const harmful = await call(page(500, "2026-09-05T00:00:00"), "2026-09-01");
  assert.deepEqual(harmful.truncated, [7], "the whole page sits inside the window, so older in-window events fell off");
  assert.deepEqual(harmful.truncatedCovered, []);

  const noWindow = await call(page(500, "2026-08-01T00:00:00"), null);
  assert.deepEqual(noWindow.truncated, [7], "with no --since there is no floor to compare against");

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
  const busy = Array.from({ length: 500 }, (_, i) => statusEvent(i + 1, "Done", { projectId: 2, createdAt: "2026-09-05T00:00:00" }));
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
  await assert.rejects(() => fetchActivity("http://x", { fetchImpl, timeoutMs: 25 }), /activity\?limit=500 did not answer within 25 ms/);
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
  // `execFileSync` without a `timeout` blocks forever, and `git rev-parse` can sit indefinitely on
  // a network path or against a held index lock — hanging the run before a single signal is
  // computed. There is no seam to inject a hanging git through, so this guards the wiring itself.
  const source = fs.readFileSync(path.join(HERE, "trial-report.mjs"), "utf8");
  const call = source.slice(source.indexOf("function gitOutput"), source.indexOf("function gitOutput") + 600);
  assert.match(call, /execFileSync\(/);
  assert.match(call, /timeout: GIT_TIMEOUT_MS/, "the git call must pass a timeout");
  assert.match(source, /const GIT_TIMEOUT_MS = 5_000;/);
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
    row({ session: "b", tool: "add_task_comment", taskId: 7, cwd: `${REPO}\\scripts` })
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
