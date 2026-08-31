// Tests for the #477 §11 adoption-trial report. Real code, no stubs: `summarize` is pure, so the
// fixtures below are the exact row and ActivityEvent shapes the recorder and the API actually
// emit — a usage row from usage-log.mjs `buildRow`, and an activity event from
// GET /api/projects/{id}/activity.
//
// Run: node --test scripts/

import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { parseRows, isOutsideRepo, statusAfter, summarize, renderReport, parseArgs, pathFlavor, WRITE_TOOLS, fetchActivity, ActivityHttpError } from "./trial-report.mjs";

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
const find = (summary, id) => summary.signals.find(s => s.id === id);

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

test("a ts-less usage row is malformed and cannot crash summarize", () => {
  const parsed = parseRows([JSON.stringify(row()), JSON.stringify({ tool: "get_next_tasks", session: "broken" })].join("\n"));
  assert.equal(parsed.malformed, 1);
  assert.doesNotThrow(() => summarize({ rows: parsed.rows, repoRoot: REPO }));
  assert.equal(find(summarize({ rows: parsed.rows, repoRoot: REPO }), 1).value, 1);
});

// --- 2. Inside vs outside the repo --------------------------------------------------------

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

// --- 3. Reading a Done transition ---------------------------------------------------------

test("the target status comes from afterJson, not from the prose message", () => {
  assert.equal(statusAfter(statusEvent(1, "Done")), "Done");
  assert.equal(statusAfter({ afterJson: "not json" }), null);
  assert.equal(statusAfter({ afterJson: JSON.stringify({ id: 1 }) }), null);
});

// --- 4. Signal 1: sessions invoking get_next_tasks ----------------------------------------

test("signal 1 counts distinct SESSIONS, not calls", () => {
  const rows = [row({ session: "a" }), row({ session: "a" }), row({ session: "a" })];
  assert.equal(find(summarize({ rows, repoRoot: REPO }), 1).value, 1, "three calls from one session is one session");

  const three = [row({ session: "a" }), row({ session: "b" }), row({ session: "c" })];
  const s1 = find(summarize({ rows: three, repoRoot: REPO }), 1);
  assert.equal(s1.value, 3);
  assert.equal(s1.verdict, "ADOPT");
});

test("signal 1 reverts at zero and pends in between", () => {
  assert.equal(find(summarize({ rows: [], repoRoot: REPO }), 1).verdict, "REVERT");
  assert.equal(find(summarize({ rows: [row({ session: "a" })], repoRoot: REPO }), 1).verdict, "PENDING");
});

test("signal 1 always states that unprompted-ness is not measured", () => {
  const s1 = find(summarize({ rows: [row()], repoRoot: REPO }), 1);
  assert.match(s1.caveat, /UNPROMPTED-NESS IS NOT MEASURED/);
});

// --- 5. Signal 2: collisions, and the vacuity guard ---------------------------------------

test("signal 2 does NOT read zero collisions as adopt when nobody wrote — the vacuity guard", () => {
  const s2 = find(summarize({ rows: [row()], repoRoot: REPO }), 2);
  assert.equal(s2.value, 0);
  assert.equal(s2.verdict, "PENDING", "an unused board must not pass the collision signal");
  assert.match(s2.caveat, /VACUOUS/);
});

test("signal 2 adopts on zero collisions once two sessions have genuinely written", () => {
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 1 }),
    row({ session: "b", tool: "update_task_status", taskId: 2, to: "Done" })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.verdict, "ADOPT");
  assert.equal(s2.detail.writingSessions, 2);
});

test("a refused claim (409) is a collision and forces REVERT", () => {
  const rows = [
    row({ session: "a", tool: "claim_task", taskId: 1 }),
    row({ session: "b", tool: "claim_task", taskId: 1, outcome: "error", http: 409 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.equal(s2.verdict, "REVERT");
  assert.equal(s2.detail.refusedClaims, 1);
});

test("a stolen claim from the audit trail is a collision even with no log row for it", () => {
  const events = [{ entityId: 4, entityType: "Task", eventType: "TaskClaimStolen", createdAt: "2026-09-01T10:00:00" }];
  const s2 = find(summarize({ rows: [], events, repoRoot: REPO }), 2);
  assert.equal(s2.value, 1);
  assert.equal(s2.verdict, "REVERT");
});

test("two sessions writing the same task is reported as an overlap for review", () => {
  const rows = [
    row({ session: "a", tool: "update_task_status", taskId: 7, to: "InProgress" }),
    row({ session: "b", tool: "add_task_comment", taskId: 7 })
  ];
  const s2 = find(summarize({ rows, repoRoot: REPO }), 2);
  assert.deepEqual(s2.detail.overlappingWrites, [{ taskId: 7, sessions: ["a", "b"] }]);
  assert.equal(s2.verdict, "ADOPT", "an overlap alone is advisory — the board never refused anything");
});

// --- 6. Signal 3: Done THROUGH the gate ---------------------------------------------------

test("signal 3 counts distinct tasks that reached Done", () => {
  const events = [statusEvent(1, "Done"), statusEvent(1, "Done"), statusEvent(2, "Done"), statusEvent(3, "Review")];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate.sort(), [1, 2]);
  assert.equal(s3.value, 2);
});

test("a Done that OVERRODE the gate does not count as having passed it", () => {
  const events = [
    statusEvent(1, "Done"),
    statusEvent(2, "Done"),
    { entityId: 2, entityType: "Task", eventType: "TaskDoneOverridden", createdAt: "2026-09-01T10:00:00" }
  ];
  const s3 = find(summarize({ rows: [], events, repoRoot: REPO }), 3);
  assert.deepEqual(s3.detail.throughGate, [1]);
  assert.deepEqual(s3.detail.doneWithOverride, [2]);
  assert.equal(s3.value, 1);
});

test("gate refusals are surfaced — a gate that never refuses is not being exercised", () => {
  const rows = [row({ tool: "update_task_status", taskId: 3, to: "Done", outcome: "error", http: 422 })];
  const s3 = find(summarize({ rows, repoRoot: REPO }), 3);
  assert.equal(s3.detail.gateRefusals, 1);
});

test("signal 3 crosses its threshold at five tasks once the gate has been exercised", () => {
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

// --- 7. Signal 4: used from outside the repo ----------------------------------------------

test("signal 4 adopts only when some session ran outside KanbanFlow-v2/", () => {
  const inside = [row({ cwd: `${REPO}\\scripts` })];
  assert.equal(find(summarize({ rows: inside, repoRoot: REPO }), 4).verdict, "REVERT");

  const outside = [row({ session: "z", cwd: "C:\\Users\\Owner\\Documents\\Claude\\my-api" })];
  const s4 = find(summarize({ rows: outside, repoRoot: REPO }), 4);
  assert.equal(s4.verdict, "ADOPT");
  assert.equal(s4.value, "yes");
  assert.equal(s4.detail[0].cwd, "C:\\Users\\Owner\\Documents\\Claude\\my-api");
});

// --- 8. The window ------------------------------------------------------------------------

test("--since excludes everything before the trial started, in both sources", () => {
  const rows = [row({ ts: "2026-08-01T00:00:00.000Z" }), row({ session: "b", ts: "2026-09-05T00:00:00.000Z" })];
  const events = [statusEvent(1, "Done", { createdAt: "2026-08-01T00:00:00" }), statusEvent(2, "Done", { createdAt: "2026-09-05T00:00:00" })];
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
  let signal;
  const fakeFetch = async (_url, options) => {
    signal = options.signal;
    return { ok: false, status: 500, statusText: "Internal Server Error", json: async () => ({}) };
  };
  await assert.rejects(() => fetchActivity("http://example.invalid", fakeFetch, 25), error => {
    assert.ok(error instanceof ActivityHttpError);
    assert.match(error.message, /returned HTTP 500 Internal Server Error/);
    assert.doesNotMatch(error.message, /not answering/);
    return true;
  });
  assert.ok(signal instanceof AbortSignal);
});

// --- 9. Aggregation -----------------------------------------------------------------------

test("an all-revert run reports REVERT and names §11's consequence", () => {
  const summary = summarize({ rows: [], events: [], repoRoot: REPO });
  // Signal 2 is PENDING when nothing wrote, so a genuinely empty trial is MIXED, not REVERT —
  // which is correct: "nothing happened" is not the same finding as "it was tried and failed".
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

// --- 10. Output + drift guards ------------------------------------------------------------

test("the rendered report carries the table, the verdict and the truncation warning", () => {
  const summary = summarize({ rows: [row()], repoRoot: REPO, truncated: [2] });
  const text = renderReport(summary, { logPath: "C:\\log.jsonl", malformed: 3 });
  assert.match(text, /\| # \| Signal \| Adopt \| Revert \| Measured \| Verdict \|/);
  assert.match(text, /OVERALL: MIXED/);
  assert.match(text, /3 unparseable log line/);
  assert.match(text, /LOWER BOUND/);
});

// --- 11. Findings from the PR #485 review -------------------------------------------------

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

test("--since is validated, never silently swallowed or silently NaN", () => {
  // Reported by Copilot on PR #485. Same shape as #468 (portfolio-sync consuming --dry-run as an
  // option value): all three failures end in a confident table computed from nothing.
  assert.deepEqual(parseArgs(["--since", "2026-08-30"]), { since: "2026-08-30", json: false });
  assert.deepEqual(parseArgs(["--json"]), { since: null, json: true });
  assert.deepEqual(parseArgs([]), { since: null, json: false });

  assert.throws(() => parseArgs(["--since"]), /last argument/, "trailing --since must not be ignored");
  assert.throws(() => parseArgs(["--since", "--json"]), /the option --json/, "must not eat the next flag");
  assert.throws(() => parseArgs(["--since", "yesterday"]), /not a date/);
  assert.throws(() => parseArgs(["--sicne", "2026-08-30"]), /Unknown option/, "a typo'd flag is not silently dropped");
});

test("summarize refuses an unparseable cutoff rather than excluding everything", () => {
  // The dangerous degradation: NaN comparisons are all false, so every row drops out and four
  // empty signals render as a REVERT-shaped verdict for a trial nobody actually read.
  assert.throws(() => summarize({ rows: [row()], repoRoot: REPO, since: "not-a-date" }), /not a parseable date/);
  assert.doesNotThrow(() => summarize({ rows: [row()], repoRoot: REPO, since: null }));
});

test("every WRITE_TOOLS name still exists in the wrapper — a rename must go red here", () => {
  const wrapper = fs.readFileSync(
    path.join(path.dirname(fileURLToPath(import.meta.url)), "mcp-server.mjs"), "utf8");
  for (const tool of WRITE_TOOLS) {
    assert.ok(wrapper.includes(`case "${tool}"`),
      `${tool} is in WRITE_TOOLS but has no handler in mcp-server.mjs — the collision detector ` +
      "would silently stop counting writes through it");
  }
});
