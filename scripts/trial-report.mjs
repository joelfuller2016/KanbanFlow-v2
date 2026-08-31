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
// failure #477 §4.3 already documents once.
//
// Usage:
//   npm run trial:report
//   npm run trial:report -- --since 2026-08-30 --json

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const DEFAULT_API = "http://127.0.0.1:5123";
const ACTIVITY_PAGE = 500; // server-side maximum (ApiEndpoints.cs Math.Clamp(limit ?? 100, 1, 500))

// Tools that MUTATE a task, used to decide whether two sessions touching one task collided.
// Curated deliberately: the log records tool names, not HTTP verbs, so write-ness cannot be
// derived from a row. A tool missing from this list makes the collision detector UNDER-report,
// never over-report — the safe direction for a signal whose adopt condition is zero. A drift test
// asserts every name here still exists in the wrapper, so a rename goes red instead of silent.
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
  "delete_acceptance_criterion"
]);

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
      if (row && typeof row === "object" && typeof row.tool === "string" && typeof row.ts === "string") rows.push(row);
      else malformed++;
    } catch {
      malformed++;
    }
  }
  return { rows, malformed };
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
  if (typeof cwd !== "string" || cwd.length === 0) return false;
  // Mixed flavors (a Windows cwd against a POSIX repo root) resolve as OUTSIDE, which is the
  // honest answer: those two paths are on different machines.
  const p = pathFlavor(cwd, repoRoot);
  const rel = p.relative(p.resolve(repoRoot), p.resolve(cwd));
  if (rel === "") return false;
  return rel.startsWith("..") || p.isAbsolute(rel);
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

/**
 * Compute the four §11 signals. Pure — every input is passed in, so the whole adjudication is
 * unit-testable against fixtures with no app, no clock and no filesystem.
 */
export function summarize({ rows = [], events = [], repoRoot, since = null, truncated = [] } = {}) {
  // An unparseable cutoff must NOT degrade to "exclude everything". NaN comparisons are all false,
  // so a typo'd date would silently drop every row and every event, and the report would then
  // score four empty signals and hand back a REVERT-shaped verdict for a trial it never read.
  // A wrong answer delivered confidently is the one outcome this whole report exists to prevent.
  const cutoff = since === null || since === undefined ? null : Date.parse(since);
  if (cutoff !== null && !Number.isFinite(cutoff)) {
    throw new Error(`summarize: since=${JSON.stringify(since)} is not a parseable date.`);
  }
  const parseTimestamp = ts => {
    if (typeof ts !== "string") return NaN;
    const normalized = /(?:Z|[+-]\d{2}:\d{2})$/i.test(ts) ? ts : `${ts}Z`;
    return Date.parse(normalized);
  };
  const inWindow = ts => cutoff === null || (Number.isFinite(parseTimestamp(ts)) && parseTimestamp(ts) >= cutoff);

  const windowRows = rows.filter(r => inWindow(r.ts));
  const windowEvents = events.filter(e => inWindow(e.createdAt));

  // ── Signal 1: sessions invoking get_next_tasks ────────────────────────────────────────────
  const nextTaskSessions = new Map();
  for (const row of windowRows) {
    if (row.tool !== "get_next_tasks") continue;
    const entry = nextTaskSessions.get(row.session) ?? { session: row.session, cwd: row.cwd, calls: 0, first: row.ts, last: row.ts };
    entry.calls++;
    if (row.ts < entry.first) entry.first = row.ts;
    if (row.ts > entry.last) entry.last = row.ts;
    nextTaskSessions.set(row.session, entry);
  }
  const signal1 = {
    id: 1,
    name: "Sessions invoking get_next_tasks unprompted",
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
      "rooted in KanbanFlow-v2 during a task about KanbanFlow-v2 is not."
  };

  // ── Signal 2: concurrent-session collisions ───────────────────────────────────────────────
  // Two independent measures, because one is crisp and one is broad:
  //   contended  — a claim the board REFUSED (409) or a claim that was STOLEN from its holder.
  //                Unambiguous: the system itself said two parties wanted the same work.
  //   overlapping — one task written by two different sessions. Broader, and advisory: sequential
  //                handoff between two sessions is legitimate, so this is a prompt to look, not a
  //                finding on its own.
  const contendedClaims = windowRows.filter(r => r.tool === "claim_task" && r.outcome === "error" && r.http === 409);
  const stolenClaims = windowEvents.filter(e => e.eventType === "TaskClaimStolen");

  const writersByTask = new Map();
  for (const row of windowRows) {
    if (!WRITE_TOOLS.has(row.tool) || typeof row.taskId !== "number" || row.outcome !== "ok") continue;
    const sessions = writersByTask.get(row.taskId) ?? new Set();
    sessions.add(row.session);
    writersByTask.set(row.taskId, sessions);
  }
  const overlapping = [...writersByTask.entries()]
    .filter(([, sessions]) => sessions.size > 1)
    .map(([taskId, sessions]) => ({ taskId, sessions: [...sessions] }));

  const writingSessions = new Set(windowRows.filter(r => WRITE_TOOLS.has(r.tool) && r.outcome === "ok").map(r => r.session));
  const collisions = contendedClaims.length + stolenClaims.length;
  const signal2 = {
    id: 2,
    name: "Concurrent-session collisions after wiring",
    adopt: "0",
    revert: ">= 1",
    value: collisions,
    // VACUITY GUARD. Zero collisions among fewer than two writing sessions measures absence of
    // USE, not absence of collision, and scoring that as an adopt would let a completely unused
    // board pass the signal it exists to test. This is the whole reason the trial has a revert
    // column at all.
    verdict: collisions > 0 ? "REVERT" : writingSessions.size >= 2 ? "ADOPT" : "PENDING",
    detail: {
      refusedClaims: contendedClaims.length,
      stolenClaims: stolenClaims.length,
      overlappingWrites: overlapping,
      writingSessions: writingSessions.size
    },
    caveat:
      writingSessions.size >= 2
        ? "Overlapping writes are advisory — a sequential handoff between two sessions is legitimate."
        : `VACUOUS: only ${writingSessions.size} session(s) ever wrote. Zero collisions here measures ` +
          "absence of use, not absence of collision, so this is PENDING rather than ADOPT."
  };

  // ── Signal 3: tasks reaching Done through the DoD gate ────────────────────────────────────
  // "Through the gate" is the operative phrase: a Done reached by OVERRIDING the gate (#428) did
  // not pass it. Those are counted separately, and the refusals are counted too — a gate that
  // never refuses anything is not being tested either.
  const overriddenTasks = new Set(windowEvents.filter(e => e.eventType === "TaskDoneOverridden").map(e => e.entityId));
  const doneTasks = new Set();
  for (const event of windowEvents) {
    if (event.eventType !== "TaskStatusChanged" || event.entityType !== "Task") continue;
    if (statusAfter(event) === "Done") doneTasks.add(event.entityId);
  }
  const throughGate = [...doneTasks].filter(id => !overriddenTasks.has(id));
  const gateRefusals = windowRows.filter(r => r.tool === "update_task_status" && r.outcome === "error" && r.http === 422);
  const signal3 = {
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
      doneWithOverride: [...overriddenTasks],
      gateRefusals: gateRefusals.length
    },
    caveat:
      gateRefusals.length > 0
        ? "Counted from the board's own ActivityEvent rows, not from this log, so a Done reached " +
          "through the UI or REST counts too. A Done that OVERRODE the gate is excluded — it did not pass it."
        : "VACUOUS: the gate never refused a Done transition. Completed tasks may have had zero " +
          "acceptance criteria, so this is PENDING rather than ADOPT."
  };

  // ── Signal 4: board consulted from outside KanbanFlow-v2/ ─────────────────────────────────
  const outsideSessions = new Map();
  for (const row of windowRows) {
    if (!isOutsideRepo(row.cwd, repoRoot)) continue;
    const entry = outsideSessions.get(row.session) ?? { session: row.session, cwd: row.cwd, calls: 0 };
    entry.calls++;
    outsideSessions.set(row.session, entry);
  }
  const signal4 = {
    id: 4,
    name: "Board consulted for work outside KanbanFlow-v2/",
    adopt: "yes",
    revert: "no",
    value: outsideSessions.size > 0 ? "yes" : "no",
    verdict: outsideSessions.size > 0 ? "ADOPT" : "REVERT",
    detail: [...outsideSessions.values()],
    caveat: `Measured against repo root ${repoRoot}.`
  };

  const signals = [signal1, signal2, signal3, signal4];
  const verdicts = signals.map(s => s.verdict);
  let overall;
  if (verdicts.every(v => v === "ADOPT")) {
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
      truncatedProjects: truncated
    },
    signals,
    overall
  };
}

export function renderReport(summary, { logPath, malformed = 0 } = {}) {
  const out = [];
  out.push("# KanbanFlow adoption trial — #477 §11");
  out.push("");
  out.push(`Log:      ${logPath ?? "(none)"}`);
  out.push(`Window:   ${summary.window.since ?? "all recorded time"}  (${summary.window.first ?? "-"} .. ${summary.window.last ?? "-"})`);
  out.push(`Measured: ${summary.window.rows} tool calls, ${summary.window.events} activity events` + (malformed ? `, ${malformed} unparseable log line(s)` : ""));
  if (summary.window.truncatedProjects.length > 0) {
    out.push(`WARNING:  activity truncated at ${ACTIVITY_PAGE} events for project(s) ${summary.window.truncatedProjects.join(", ")} — signal 3 is a LOWER BOUND.`);
  }
  out.push("");
  out.push("| # | Signal | Adopt | Revert | Measured | Verdict |");
  out.push("|---|--------|-------|--------|----------|---------|");
  for (const s of summary.signals) {
    out.push(`| ${s.id} | ${s.name} | ${s.adopt} | ${s.revert} | ${s.value} | ${s.verdict} |`);
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

export class ActivityHttpError extends Error {}

export async function fetchActivity(apiBase, fetchImpl = fetch, timeoutMs = 10000) {
  const events = [];
  const truncated = [];
  const fetchJson = async url => {
    const response = await fetchImpl(url, { signal: AbortSignal.timeout(timeoutMs) });
    if (!response.ok) {
      throw new ActivityHttpError(`KanbanFlow returned HTTP ${response.status} ${response.statusText}`.trim());
    }
    return response.json();
  };
  const projects = await fetchJson(`${apiBase}/api/projects`);
  for (const project of projects) {
    const page = await fetchJson(`${apiBase}/api/projects/${project.id}/activity?limit=${ACTIVITY_PAGE}`);
    if (Array.isArray(page)) {
      events.push(...page);
      // The endpoint caps at 500 with no continuation token, so a full page means events were
      // dropped. Say so rather than silently reporting a floor as a total.
      if (page.length === ACTIVITY_PAGE) truncated.push(project.id);
    }
  }
  return { events, truncated };
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
  const parsed = { since: null, json: false };
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
    throw new Error(`Unknown option ${JSON.stringify(arg)}. Usage: trial-report [--since <date>] [--json]`);
  }
  return parsed;
}

async function main(argv) {
  const { since, json: asJson } = parseArgs(argv.slice(2));
  const apiBase = (process.env.KANBANFLOW_API_URL || DEFAULT_API).replace(/\/$/, "");

  const { resolveUsageLogPath } = await import("./usage-log.mjs");
  const logPath = resolveUsageLogPath();
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

  let text = "";
  if (logPath && fs.existsSync(logPath)) text = fs.readFileSync(logPath, "utf8");
  else if (!logPath) console.error("NOTE: usage logging is disabled (KANBANFLOW_USAGE_LOG) — signals 1, 2 and 4 cannot be measured.\n");
  else console.error(`NOTE: no usage log at ${logPath} yet — no MCP tool call has been recorded.\n`);

  const { rows, malformed } = parseRows(text);

  let events = [];
  let truncated = [];
  try {
    ({ events, truncated } = await fetchActivity(apiBase));
  } catch (error) {
    const problem = error instanceof ActivityHttpError
      ? error.message
      : `KanbanFlow is not answering at ${apiBase} (${error.message})`;
    console.error(`NOTE: ${problem} — signal 3 cannot be measured.\n`);
  }

  const summary = summarize({ rows, events, repoRoot, since, truncated });
  console.log(asJson ? JSON.stringify(summary, null, 2) : renderReport(summary, { logPath, malformed }));
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
