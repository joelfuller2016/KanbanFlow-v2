#!/usr/bin/env node
// backlog-sync — generate docs/BACKLOG.md, the authoritative log book for this repo.
//
// GitHub issues are the source of truth; docs/BACKLOG.md is their ordered, reviewable
// projection. This script is READ-ONLY against GitHub: it never creates, edits, closes or
// comments on anything. The only thing it writes is docs/BACKLOG.md.
//
// Ordering is DETERMINISTIC and TOTAL — two runs over unchanged input produce a
// byte-identical file. See `compare()` for the full sort key; the last component is the
// issue number, which is unique, so no pair of issues is ever left to engine order.
//
//   node scripts/backlog-sync.mjs              # regenerate docs/BACKLOG.md
//   node scripts/backlog-sync.mjs --dry-run    # print to stdout, write nothing
//   node scripts/backlog-sync.mjs --check      # exit 1 (with a diff) if the file is stale
//   node scripts/backlog-sync.mjs --check --strict   # ...and treat clock drift as stale too
//
// NO CI. This repo has no GitHub Actions by policy; --check is run by a human or an agent.

import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { createHash } from "node:crypto";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import { existsSync, realpathSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
// The single definition of "what priority is this issue?", shared with the board's sync.
// Imported, never transcribed — a copy is how the two drifted onto different answers for 13 of
// 119 open issues (#364).
import {
  derivePriority, priorityFromTitle, priorityFromLabel, priorityFromMilestone, PN_LEVELS,
} from "./priority-model.mjs";

const execFileAsync = promisify(execFile);

const REPO = process.env.KANBANFLOW_BACKLOG_REPO || "joelfuller2016/KanbanFlow-v2";
const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const OUT_PATH = join(REPO_ROOT, "docs", "BACKLOG.md");

// `gh issue list` defaults to 30 and silently returns only that many — the same trap
// `gh pr list --json` has. Always pass an explicit limit, and treat *reaching* it as a
// hard failure rather than a warning: a log book that quietly drops issues past the
// limit still looks complete, which is the single thing this file must never do.
const FETCH_LIMIT = 1000;

const MS_PER_DAY = 86_400_000;

// --- Priority model ---------------------------------------------------------------------
//
// PRIORITY IS NOT DECIDED HERE. It comes from `./priority-model.mjs`, which is the single
// definition for the whole repo — the same function `portfolio-sync.mjs` uses to set
// TaskItem.Priority on the board. Its order is: title prefix → priority label → milestone
// band → Medium.
//
// This file used to derive its own tier and then adjust it by TOPICAL label: `bug` /
// `dependencies` / `security` promoted a step, a docs-only issue demoted a step. That made the
// log book a SECOND source of priority truth, and the two disagreed on **13 of 119 open issues
// (10.9%)** — measured, not predicted (#364). #355 forbids the adjustment outright: a topical
// label is a KIND, not an urgency. #189 is labelled `bug` and self-declares `P2:`; #340 and #341
// are both `bug,tech-debt` but are a stored-markdown injection and some dead CSS hooks. Promoting
// on `bug` fabricates a signal the repo never authored.
//
// What survives here is ORDERING WITHIN a band (`signalRank`), which is presentation: it decides
// which of two equal-priority rows prints first and can never move an issue between tiers. That
// distinction is the whole point — see the comment on `signalRank`.
//
// Everything below is a pure function of the fetched JSON. Nothing consults the clock
// except `ageDays`, which is reported but never used for ordering.

const TIERS = [
  { id: "P0", emoji: "🔴", label: "Critical", blurb: "Correctness, data integrity, and the audit trail. Fix before anything else." },
  { id: "P1", emoji: "🟠", label: "High", blurb: "Foundation and agent-facing surface. The work that unblocks the rest." },
  { id: "P2", emoji: "🟡", label: "Medium", blurb: "Feature build-out, UI/UX polish, docs. Real work, not urgent." },
  { id: "P3", emoji: "🟢", label: "Low", blurb: "Speculative, nice-to-have, or explicitly deferred." },
];
const TIER_INDEX = new Map(TIERS.map((t, i) => [t.id, i]));

// Milestone -> display short name and sort rank. **No `tier` column**: the milestone→priority
// band lives once, in `priority-model.mjs`'s MILESTONE_BANDS, and is read through
// `derivePriority`. This list carries only what that module does not: the abbreviation shown in
// the table, and the roadmap order used as a sort key. Unmilestoned work sorts last within its
// tier — it has not been triaged into the roadmap, so it has not earned a place ahead of work
// that has.
//
// Keeping the titles here is still load-bearing: `milestoneOf` uses them to REFUSE an unmapped
// milestone (see below), which is the loud half of the contract `priority-model.mjs` describes —
// "the log book raises it loudly; the sync must keep running, so it degrades quietly."
const MILESTONES = [
  { title: "M1: Correctness & Audit Trail", short: "M1" },
  { title: "M2: Tech-debt & Foundation", short: "M2" },
  { title: "M3: Agent API Surface", short: "M3" },
  { title: "M4: UI / UX Polish", short: "M4" },
  { title: "M5: Docs & Process", short: "M5" },
  { title: "M6: AI Memory & Ops Hub", short: "M6" },
  { title: "M7: Self-Improving", short: "M7" },
];
const MILESTONE_BY_TITLE = new Map(MILESTONES.map((m, i) => [m.title, { ...m, rank: i }]));
const NO_MILESTONE = { title: null, short: "—", rank: MILESTONES.length };

// A milestone that exists on GitHub but is absent from MILESTONES has no display name and no
// sort rank, and `priorityFromMilestone` will not have a band for it either — so it would silently
// land in P2 and render with a `—` milestone cell, a state GitHub does not have, reported with no
// warning. That is the same failure this file already refuses elsewhere (a truncated fetch and an
// accounting mismatch both throw), so refuse here too rather than quietly misreporting.
//
// This is the LOUD half of the contract `priority-model.mjs` describes: the sync must keep running
// so it degrades to "Medium" quietly; the log book exists to be trusted, so it stops.
function milestoneOf(issue) {
  const title = issue.milestone?.title;
  if (!title) return NO_MILESTONE;
  const known = MILESTONE_BY_TITLE.get(title);
  if (!known) {
    throw new Error(
      `Issue #${issue.number} carries the milestone "${title}", which this generator does not know. ` +
      `Add it to MILESTONES in scripts/backlog-sync.mjs (name + order) AND to MILESTONE_BANDS in ` +
      `scripts/priority-model.mjs (its P0–P3 band) — both, or the board and this file will disagree ` +
      `about it. Refusing to render it as "no milestone".`,
    );
  }
  return known;
}

// Topical labels used for READING ORDER ONLY. These once adjusted the tier itself; they no
// longer touch priority at all (#364/#355).
const DEFECT_LABELS = new Set(["bug", "dependencies", "security"]);
const DOCS_LABELS = new Set(["documentation"]);

function hasDefectLabel(labelSet) {
  for (const l of DEFECT_LABELS) if (labelSet.has(l)) return true;
  return false;
}

// Rank *within* one tier band. Defects first (something is broken), then the epic that groups
// the band's children, then ordinary build-out, then docs.
//
// WHY THIS IS NOT A SECOND PRIORITY RULE, and the line not to cross: `signal` is a sort key
// applied AFTER `tier` and `milestone.rank` in `compare`, so it can only reorder rows that are
// already in the same band. It cannot move an issue between tiers, and it cannot change what any
// consumer reads as the issue's priority. The moment something here feeds back into `tier`, this
// file is a second source of truth again — which is exactly the defect #364 was filed for.
//
// Computed ONCE in `classify` and stored, because `compare` runs it O(n log n) times.
function signalRank(labelSet) {
  if (hasDefectLabel(labelSet)) return 0;
  if (labelSet.has("epic")) return 1;
  if (isDocsOnly(labelSet)) return 3;
  return 2;
}

function isDocsOnly(labelSet) {
  if (labelSet.size === 0) return false;
  for (const l of labelSet) if (!DOCS_LABELS.has(l)) return false;
  return true;
}

// `derivePriority` answers in the board's vocabulary (Critical/High/Medium/Low); the log book
// prints tiers (P0–P3). PN_LEVELS is that mapping, exported from the same module, so the two
// scales cannot drift apart either.
function tierOfIssue(issue) {
  return `P${PN_LEVELS.indexOf(derivePriority(issue))}`;
}

// The tier a milestone maps to, for the rendered explainer.
//
// This file lists the milestones it knows; `priority-model.mjs` owns their P0-P3 bands. Dropping
// the duplicated tier column removed one drift risk and created a smaller one: a milestone added
// HERE but not THERE. `priorityFromMilestone` returns null for it, `PN_LEVELS.indexOf(null)` is
// -1, and `TIERS[-1].id` is a bare TypeError from deep inside renderBody — a confusing way to
// learn that two lists disagree. Name both files and the fix instead.
function tierIdForMilestone(title) {
  const level = priorityFromMilestone(title);
  if (level === null) {
    throw new Error(
      `Milestone "${title}" is listed in MILESTONES (scripts/backlog-sync.mjs) but has no band in ` +
      `MILESTONE_BANDS (scripts/priority-model.mjs). Add it there, or remove it here — the two must ` +
      `agree, or the board and this log book will disagree about every issue carrying it.`,
    );
  }
  return TIERS[PN_LEVELS.indexOf(level)].id;
}

// Which signal `derivePriority` actually used — reported, never used for ordering. Recomputed
// from the same inputs rather than returned by the model, so this stays a pure description of a
// decision made elsewhere and can never become the decision itself.
function tierSourceOf(issue) {
  if (priorityFromTitle(issue.title)) return "title";
  for (const label of issue.labels || []) if (priorityFromLabel(label.name)) return "label";
  return priorityFromMilestone(issue.milestone) ? "milestone" : "default";
}

function classify(issue) {
  const labels = (issue.labels || []).map((l) => l.name).sort();
  const labelSet = new Set(labels);
  const milestone = milestoneOf(issue);
  const tier = tierOfIssue(issue);
  const tierSource = tierSourceOf(issue);

  return {
    signal: signalRank(labelSet),
    number: issue.number,
    title: String(issue.title || "").replace(/\s+/g, " ").trim(),
    url: issue.url,
    state: issue.state,
    stateReason: issue.stateReason || "",
    labels,
    milestone,
    tier,
    tierSource,
    createdAt: issue.createdAt,
    closedAt: issue.closedAt || null,
    closingPrs: (issue.closedByPullRequestsReferences || [])
      .map((p) => ({ number: p.number, url: p.url }))
      .sort((a, b) => a.number - b.number),
    isEpic: labels.includes("epic"),
  };
}

// Total order. Every component is derived from the issue payload; the final component is the
// issue number, which is unique, so `compare` never returns 0 for two distinct issues and the
// result never depends on the order `gh` happened to hand us.
function compare(a, b) {
  return (
    TIER_INDEX.get(a.tier) - TIER_INDEX.get(b.tier) ||
    a.milestone.rank - b.milestone.rank ||
    a.signal - b.signal ||
    Date.parse(a.createdAt) - Date.parse(b.createdAt) ||
    a.number - b.number
  );
}

// --- GitHub (read-only) -----------------------------------------------------------------

// `gh` can block indefinitely — an expired token that wants to prompt, a stalled network read.
// With no timeout the script simply never returns, and an agent running `npm run backlog:check`
// hangs with no output and nothing to act on. Bound it, and turn the two failures that actually
// happen into messages that name the fix.
const GH_TIMEOUT_MS = 120_000;

async function gh(args) {
  let stdout;
  try {
    ({ stdout } = await execFileAsync("gh", args, {
      maxBuffer: 64 * 1024 * 1024,
      timeout: GH_TIMEOUT_MS,
    }));
  } catch (err) {
    if (err.code === "ENOENT") throw new Error("`gh` is not installed or not on PATH.");
    if (err.killed) {
      throw new Error(
        `\`gh ${args.join(" ")}\` timed out after ${GH_TIMEOUT_MS / 1000}s. Check \`gh auth status\`.`,
      );
    }
    throw err;
  }
  try {
    return JSON.parse(stdout || "[]");
  } catch {
    throw new Error(`\`gh ${args.join(" ")}\` did not return JSON.`);
  }
}

const ISSUE_FIELDS = [
  "number", "title", "labels", "milestone", "state",
  "createdAt", "closedAt", "url", "stateReason",
  "closedByPullRequestsReferences",
].join(",");

async function fetchIssues(repo) {
  const list = await gh([
    "issue", "list", "-R", repo, "--state", "all",
    "--limit", String(FETCH_LIMIT), "--json", ISSUE_FIELDS,
  ]);
  if (list.length >= FETCH_LIMIT) {
    throw new Error(
      `gh returned ${list.length} issues — it hit the ${FETCH_LIMIT} fetch limit, so the set is ` +
      `probably TRUNCATED. Raise FETCH_LIMIT in scripts/backlog-sync.mjs and re-run. ` +
      `Refusing to write a log book that may be silently missing issues.`,
    );
  }
  return list;
}

async function masterSha() {
  // The sha the log book was generated against. origin/master when resolvable so the header
  // is stable no matter which feature branch the generator happens to be run from.
  for (const ref of ["origin/master", "master", "HEAD"]) {
    try {
      const { stdout } = await execFileAsync("git", ["rev-parse", ref], { cwd: REPO_ROOT });
      const sha = stdout.trim();
      if (/^[0-9a-f]{40}$/.test(sha)) return { ref, sha };
    } catch { /* try the next ref */ }
  }
  return { ref: "unknown", sha: "0".repeat(40) };
}

// --- Rendering --------------------------------------------------------------------------

const BODY_MARKER = "<!-- backlog-sync:body -->";

// Titles are free text from GitHub. Neutralise anything that would break a Markdown table
// cell or be swallowed as inline HTML. `&` is deliberately left alone: GitHub renders a bare
// `&` literally and escaping it would only hurt plain-text readability.
function cell(text) {
  return String(text ?? "")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/\|/g, "\\|")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function ageDays(iso, now) {
  return Math.max(0, Math.floor((now - Date.parse(iso)) / MS_PER_DAY));
}

function labelCell(labels) {
  return labels.length ? labels.map((l) => `\`${cell(l)}\``).join(" ") : "—";
}

function prCell(prs) {
  if (!prs.length) return "—";
  return prs.map((p) => `[#${p.number}](${p.url})`).join(", ");
}

function isoDay(iso) {
  return iso ? String(iso).slice(0, 10) : "—";
}

function plural(n, one, many) {
  return n === 1 ? one : many;
}

function renderBody(model) {
  const { open, completed, notPlanned, now, repo } = model;
  const out = [];
  const p = (s = "") => out.push(s);

  // ---- summary -------------------------------------------------------------------------
  p("## 📊 At a glance");
  p();
  p("| Tier | Meaning | Open | What it covers |");
  p("| :--- | :--- | ---: | :--- |");
  for (const t of TIERS) {
    const n = open.filter((i) => i.tier === t.id).length;
    p(`| ${t.emoji} **${t.id} · ${t.label}** | \`${t.id}\` | ${n} | ${cell(t.blurb)} |`);
  }
  p(`| | **Total open** | **${open.length}** | |`);
  p();

  // ---- mermaid -------------------------------------------------------------------------
  p("### Open issues per milestone");
  p();
  p("```mermaid");
  p("pie showData");
  p('    title Open issues per milestone');
  const perMilestone = [...MILESTONES.map((m) => m.title), null];
  for (const title of perMilestone) {
    const n = open.filter((i) => i.milestone.title === title).length;
    if (n === 0) continue;
    const label = (title ?? "No milestone").replace(/"/g, "'");
    p(`    "${label}" : ${n}`);
  }
  p("```");
  p();

  // ---- epic index ----------------------------------------------------------------------
  const epics = open.filter((i) => i.isEpic);
  if (epics.length) {
    p(`### 🧩 Open epics (${epics.length})`);
    p();
    p("Epics are containers — their children are the ordinary issues listed in the tiers below.");
    p();
    p("| Issue | Tier | Milestone | Title |");
    p("| :--- | :--- | :--- | :--- |");
    for (const e of epics) {
      p(`| [#${e.number}](${e.url}) | ${tierOf(e).emoji} ${e.tier} | ${cell(e.milestone.short)} | ${cell(e.title)} |`);
    }
    p();
  }

  // ---- open, by tier -------------------------------------------------------------------
  p("---");
  p();
  p("## 📋 Open backlog — highest to lowest priority");
  p();
  p(`All **${open.length}** open issues, each appearing exactly once. \`Rank\` is the global work order.`);
  p();

  let rank = 0;
  for (const t of TIERS) {
    const rows = open.filter((i) => i.tier === t.id);
    p(`### ${t.emoji} ${t.id} · ${t.label} — ${rows.length} open`);
    p();
    p(`_${cell(t.blurb)}_`);
    p();
    if (rows.length === 0) {
      p("_Nothing in this tier._");
      p();
      continue;
    }
    p("| Rank | Issue | Title | Milestone | Labels | Age |");
    p("| ---: | :--- | :--- | :--- | :--- | ---: |");
    for (const i of rows) {
      rank += 1;
      const epicMark = i.isEpic ? "🧩 " : "";
      p(
        `| ${rank} | [#${i.number}](${i.url}) | ${epicMark}${cell(i.title)} ` +
        `| ${cell(i.milestone.short)} | ${labelCell(i.labels)} | ${ageDays(i.createdAt, now)}d |`,
      );
    }
    p();
  }

  // ---- completed ledger ----------------------------------------------------------------
  p("---");
  p();
  p(`## ✅ Completed`);
  p();
  p(
    `**${completed.length}** ${plural(completed.length, "issue", "issues")} closed as completed, newest first. This is the ledger half ` +
    `of the log book: when an issue is finished it is recorded here, with the PR that closed it ` +
    `when GitHub can resolve one.`,
  );
  p();
  p("| Issue | Title | Closed | Milestone | Closed by |");
  p("| :--- | :--- | :--- | :--- | :--- |");
  for (const i of completed) {
    p(
      `| [#${i.number}](${i.url}) | ${cell(i.title)} | ${isoDay(i.closedAt)} ` +
      `| ${cell(i.milestone.short)} | ${prCell(i.closingPrs)} |`,
    );
  }
  p();

  // ---- not planned ---------------------------------------------------------------------
  p("---");
  p();
  p(`## 🚫 Closed — not planned`);
  p();
  p(
    `**${notPlanned.length}** ${plural(notPlanned.length, "issue", "issues")} closed without being done (wontfix, superseded, duplicate). ` +
    `Recorded so the history is complete — **these are not accomplishments** and are deliberately ` +
    `kept out of the ✅ ledger above.`,
  );
  p();
  if (notPlanned.length === 0) {
    p("_None._");
    p();
  } else {
    p("| Issue | Title | Closed | Milestone |");
    p("| :--- | :--- | :--- | :--- |");
    for (const i of notPlanned) {
      p(`| [#${i.number}](${i.url}) | ${cell(i.title)} | ${isoDay(i.closedAt)} | ${cell(i.milestone.short)} |`);
    }
    p();
  }

  // ---- how ranking works ---------------------------------------------------------------
  p("---");
  p();
  p("## 🧮 How the ranking is computed");
  p();
  p("Ordering is a pure function of the issue payload — no hand-placed rows, no engine order.");
  p("Signals are applied in this precedence:");
  p();
  p("**Priority is not decided here.** It comes from `scripts/priority-model.mjs`, the single definition");
  p("shared with the board's GitHub sync — so a card and its row in this file can never disagree (#364).");
  p("That module resolves, first non-null winning:");
  p();
  p("1. **Explicit title prefix** — a `P0:`/`P1:`/`P2:`/`P3:` prefix in the issue title wins outright. It is a human's stated decision.");
  p("2. **Priority label** — the cross-repo vocabulary (`P0`, `priority:high`, a bare `critical`, …). This repo carries none, so it rarely fires here.");
  p("3. **Milestone band** — the execution phase as a proxy: " +
    MILESTONES.map((m) => `\`${m.short}\`→${tierIdForMilestone(m.title)}`).join(", ") + ", no milestone→P2.");
  p("4. **`Medium` (P2)** — no signal at all.");
  p();
  p("**Topical labels do not affect priority.** `bug` / `security` / `tech-debt` are a *kind*, not an");
  p("urgency (#355): #189 is labelled `bug` and self-declares `P2:`. They order rows *within* an");
  p("already-decided tier — defects → epics → build-out → docs — and can never move one between tiers.");
  p();
  p("Then, as the deterministic tiebreak: **age (older first), then issue number**. Because numbers are unique the order is *total*, so re-running the generator over unchanged input reproduces this file byte for byte.");
  p();
  p(`Source of truth: <https://github.com/${repo}/issues>. This file is its ordered projection.`);
  p();

  return out.join("\n");
}

function tierOf(issue) {
  return TIERS[TIER_INDEX.get(issue.tier)];
}

function renderHeader(meta) {
  const { generatedAt, sha, ref, open, completed, notPlanned, digest, repo } = meta;
  return [
    "# 📓 KanbanFlow-v2 Backlog — Authoritative Log Book",
    "",
    "> **This file is the authoritative log book for this repo.** Every open issue is listed",
    "> below, ordered highest to lowest priority, and every completed issue is recorded in the",
    "> ✅ ledger. Read it to know what is next and what has been done.",
    ">",
    "> **What \"authoritative\" means here — and what it does not.** This file is the single",
    "> sanctioned *ranking* and the *completion record*: the priority order below is the one to",
    "> work from, and a finished issue is logged here. It is **not** a second source of truth for",
    "> issue state — **GitHub remains the source of truth**, and this file is a verified projection",
    "> of it, accurate as of the timestamp below. It goes stale the moment anyone edits an issue.",
    ">",
    "> **So never trust it blind — prove it.** `npm run backlog:check` exits non-zero the instant",
    "> this file and GitHub disagree, and again if the file was hand-edited (it carries a content",
    "> digest). A green check is what converts this from a snapshot into something you can rely on;",
    "> run it before acting on the order below.",
    ">",
    "> **This file is GENERATED — do not hand-edit it.** `scripts/backlog-sync.mjs` is the only",
    "> sanctioned way to update it. To change what appears here, change the issue on GitHub and",
    "> regenerate with `npm run backlog`.",
    "",
    "| | |",
    "| :--- | :--- |",
    `| **Generated** | \`${generatedAt}\` |`,
    `| **Against** | \`${ref}\` @ [\`${sha.slice(0, 8)}\`](https://github.com/${repo}/commit/${sha}) |`,
    `| **Open issues** | **${open}** |`,
    `| **Closed issues** | **${completed + notPlanned}** — ${completed} completed, ${notPlanned} not planned |`,
    `| **Regenerate** | \`npm run backlog\` &nbsp;·&nbsp; verify with \`npm run backlog:check\` |`,
    `| **Content digest** | \`sha256:${digest}\` |`,
    "",
    BODY_MARKER,
    "",
  ].join("\n");
}

// --- Digest & drift ---------------------------------------------------------------------

function sha256(text) {
  return createHash("sha256").update(text, "utf8").digest("hex");
}

// The digest covers the RENDERED body, not the underlying data model. That is deliberate: a
// digest over the model would still match after somebody deleted a row from the file by hand,
// which is precisely the tampering this is here to catch.
function digestOf(body) {
  return sha256(body);
}

// This repo is developed on Windows with `core.autocrlf=true` and ships no `.gitattributes`,
// so a fresh clone hands back CRLF even though the generator writes LF. Comparing raw bytes
// would then report every single line as drift AND accuse the file of having been hand-edited
// — a false alarm severe enough to make the guard worthless. Normalise on read so the digest,
// the drift check and the timestamp carry-forward are all line-ending agnostic.
function normalizeEol(text) {
  return text.replace(/\r\n/g, "\n");
}

function splitFile(rawText) {
  const text = normalizeEol(rawText);
  const idx = text.indexOf(BODY_MARKER);
  if (idx === -1) return { header: text, body: "" };
  return {
    header: text.slice(0, idx + BODY_MARKER.length),
    body: text.slice(idx + BODY_MARKER.length).replace(/^\n/, ""),
  };
}

function headerDigest(header) {
  const m = /`sha256:([0-9a-f]{64})`/.exec(header);
  return m ? m[1] : null;
}

function headerGeneratedAt(header) {
  const m = /\*\*Generated\*\*\s*\|\s*`([^`]+)`/.exec(header);
  return m ? m[1] : null;
}

// `Age` is the one rendered value that moves with the wall clock. Masking it lets --check
// distinguish "somebody edited this file / GitHub moved on" (real drift, exit 1) from "a day
// went by" (cosmetic, exit 0 + a nudge). --strict skips the masking.
function maskVolatile(body) {
  return body.replace(/\| \d+d \|/g, "| ##d |");
}

function diffLines(expected, actual, limit = 25) {
  const e = expected.split("\n");
  const a = actual.split("\n");
  let firstDiff = -1;
  for (let i = 0; i < Math.max(e.length, a.length); i++) {
    if (e[i] !== a[i]) { firstDiff = i; break; }
  }
  const eSet = new Set(e);
  const aSet = new Set(a);
  const missing = e.filter((l) => l.trim() && !aSet.has(l));
  const unexpected = a.filter((l) => l.trim() && !eSet.has(l));
  return { firstDiff, missing: missing.slice(0, limit), unexpected: unexpected.slice(0, limit), missingTotal: missing.length, unexpectedTotal: unexpected.length };
}

// --- Build --------------------------------------------------------------------------------

function buildModel(rawIssues, now, repo) {
  const all = rawIssues.map(classify);
  const open = all.filter((i) => i.state === "OPEN").sort(compare);
  const closed = all.filter((i) => i.state !== "OPEN");

  // GitHub leaves stateReason empty on some older closes. Treat "closed and not explicitly
  // NOT_PLANNED" as completed — the conservative read, since the alternative is silently
  // demoting real finished work out of the ledger.
  const byRecency = (a, b) => (Date.parse(b.closedAt) - Date.parse(a.closedAt)) || (b.number - a.number);
  const completed = closed.filter((i) => i.stateReason !== "NOT_PLANNED").sort(byRecency);
  const notPlanned = closed.filter((i) => i.stateReason === "NOT_PLANNED").sort(byRecency);

  // The invariant that makes this file trustworthy. A log book that silently drops issues is
  // worse than no log book at all, so this throws rather than warning.
  const rendered = open.length + completed.length + notPlanned.length;
  if (rendered !== all.length) {
    throw new Error(`Issue accounting failed: ${all.length} fetched but ${rendered} classified for rendering.`);
  }
  const seen = new Set();
  for (const i of all) {
    if (seen.has(i.number)) throw new Error(`Issue #${i.number} appeared twice in the fetched set.`);
    seen.add(i.number);
  }

  return { all, open, completed, notPlanned, now, repo };
}

// Resolve a section by its two bounding headings, and FAIL if either is missing.
//
// A bare `indexOf` fails OPEN, which is the worst possible behaviour for the one assertion that
// reads the rendered output. `body.slice(-1, end)` (start not found) silently yields a wrong,
// usually empty region; `body.slice(start, -1)` (end not found) swallows the entire rest of the
// file. Either way the row counts stop measuring what they claim to. These heading strings are
// one character from a mismatch — `## 🚫 Closed — not planned` carries an em dash — so a
// renderer edit that drifts one of them must be loud, not silently self-approving.
function sectionBounds(body, startHeading, endHeading) {
  const start = body.indexOf(startHeading);
  const end = body.indexOf(endHeading);
  if (start === -1 || end === -1 || end < start) {
    throw new Error(
      `Rendered body is missing the expected headings "${startHeading}" / "${endHeading}" ` +
      `(start=${start}, end=${end}). assertRendered cannot verify the output, so it refuses to ` +
      `pass it. If a heading was renamed in renderBody, rename it here too.`,
    );
  }
  return body.slice(start, end);
}

function assertRendered(body, model) {
  // Count the rows actually rendered into the open section and compare against what `gh`
  // returned. This is a check on the OUTPUT TEXT, not on the in-memory arrays — it is the
  // only assertion that can catch a rendering bug that drops a row.
  const openSection = sectionBounds(body, "## 📋 Open backlog", "## ✅ Completed");
  const rows = openSection.match(/^\| \d+ \| \[#\d+\]\(/gm) || [];
  if (rows.length !== model.open.length) {
    throw new Error(
      `Rendered ${rows.length} open rows but gh returned ${model.open.length} open issues. ` +
      `Refusing to write an incomplete log book.`,
    );
  }
  const ledger = sectionBounds(body, "## ✅ Completed", "## 🚫 Closed — not planned");
  const ledgerRows = ledger.match(/^\| \[#\d+\]\(/gm) || [];
  if (ledgerRows.length !== model.completed.length) {
    throw new Error(`Rendered ${ledgerRows.length} completed rows but ${model.completed.length} issues are closed-as-completed.`);
  }

  // The THIRD section gets the same treatment as the other two. It was left unchecked, which
  // made the whole assertion an incomplete enumeration: a renderer defect could drop or
  // duplicate not-planned rows while the header still reported model.notPlanned.length, and
  // nothing here would notice. "Not an accomplishment" is still history, and the file's claim
  // that every issue appears exactly once covers this section too.
  // When notPlanned is empty the renderer emits "_None._" and no table, so 0 === 0 holds.
  const notPlanned = sectionBounds(body, "## 🚫 Closed — not planned", "## 🧮 How the ranking is computed");
  const notPlannedRows = notPlanned.match(/^\| \[#\d+\]\(/gm) || [];
  if (notPlannedRows.length !== model.notPlanned.length) {
    throw new Error(`Rendered ${notPlannedRows.length} not-planned rows but ${model.notPlanned.length} issues are closed-as-not-planned.`);
  }
  return rows.length;
}

// The headline counts live in the HEADER, and until this existed nothing checked them: `digestOf`
// hashes the body only, and `--check` compares body against body. Edit `| **Open issues** | **103**`
// to `**203**` by hand and every guard stayed green — the file asserted a number GitHub does not
// support, which is the single failure mode the log book claims to prevent.
function headerCounts(header) {
  const open = /\|\s*\*\*Open issues\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|/.exec(header);
  const closed = /\|\s*\*\*Closed issues\*\*\s*\|\s*\*\*(\d+)\*\*\s*—\s*(\d+) completed, (\d+) not planned\s*\|/.exec(header);
  return {
    open: open ? Number(open[1]) : null,
    closed: closed ? Number(closed[1]) : null,
    completed: closed ? Number(closed[2]) : null,
    notPlanned: closed ? Number(closed[3]) : null,
  };
}

// Returns a list of human-readable problems; empty means the header agrees with GitHub. A count
// the regex could not find at all (null) is itself a problem — a header that lost its counts must
// not pass by default.
function headerCountProblems(header, model) {
  const found = headerCounts(header);
  const expected = {
    open: model.open.length,
    closed: model.completed.length + model.notPlanned.length,
    completed: model.completed.length,
    notPlanned: model.notPlanned.length,
  };
  const problems = [];
  for (const key of ["open", "closed", "completed", "notPlanned"]) {
    if (found[key] === null) problems.push(`header has no readable "${key}" count`);
    else if (found[key] !== expected[key]) {
      problems.push(`header says ${key}=${found[key]}, GitHub says ${expected[key]}`);
    }
  }
  return problems;
}

// --- CLI ------------------------------------------------------------------------------------

function parseArgs(argv) {
  const args = { dryRun: false, check: false, strict: false, help: false, repo: REPO };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--dry-run") args.dryRun = true;
    else if (a === "--check") args.check = true;
    else if (a === "--strict") args.strict = true;
    else if (a === "--repo") args.repo = argv[++i] || args.repo;
    else if (a === "--help" || a === "-h") args.help = true;
    else {
      console.error(`Unknown argument: ${a}  (try --help)`);
      process.exit(2);
    }
  }
  return args;
}

const HELP = `backlog-sync — generate docs/BACKLOG.md, the authoritative log book.

  (no flags)     regenerate docs/BACKLOG.md from GitHub
  --dry-run      render to stdout, write nothing
  --check        exit 1 with a diff if docs/BACKLOG.md is stale or hand-edited
  --strict       with --check, also treat clock drift (the Age column) as stale
  --repo O/R     override the repo (default ${REPO})

GitHub issues are the source of truth; this script is read-only against GitHub and never
writes to it. Ordering is deterministic — re-running over unchanged input reproduces the
file byte for byte. There is no CI on this repo; --check is run by a human or an agent.`;

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) { console.log(HELP); return; }

  const [raw, { sha, ref }] = await Promise.all([fetchIssues(args.repo), masterSha()]);
  const now = Date.now();
  const model = buildModel(raw, now, args.repo);

  const body = renderBody(model);
  const renderedOpen = assertRendered(body, model);
  const digest = digestOf(body);

  console.error(
    `backlog-sync: ${model.all.length} issues fetched — ${model.open.length} open, ` +
    `${model.completed.length} completed, ${model.notPlanned.length} not planned. ` +
    `${renderedOpen} open rows rendered (invariant OK).`,
  );

  const existing = existsSync(OUT_PATH) ? await readFile(OUT_PATH, "utf8") : null;
  const prior = existing ? splitFile(existing) : null;

  if (args.check) {
    if (!existing) {
      console.error(`\nSTALE: ${OUT_PATH} does not exist. Run \`npm run backlog\`.`);
      process.exit(1);
    }
    // 1. Self-consistency: does the file's own body still hash to the digest in its header?
    //    This catches a hand edit without consulting GitHub at all.
    const claimed = headerDigest(prior.header);
    const actualDigest = digestOf(prior.body);
    if (claimed && claimed !== actualDigest) {
      // EXIT HERE. This used to print and fall through to the drift comparison, which then
      // reported "STALE: does not match what GitHub says" — blaming GitHub for a local hand edit.
      // Worse, when the edit only touched the Age column, `maskVolatile` made the two sides equal
      // and `diffLines` found nothing, so the output was the word STALE followed by no diff at
      // all. The exit code was right and the diagnosis was wrong; a guard that misnames the fault
      // sends the reader to the wrong file.
      console.error(`\nTAMPERED: docs/BACKLOG.md has been hand-edited.`);
      console.error(`  header claims  sha256:${claimed}`);
      console.error(`  body hashes to sha256:${actualDigest}`);
      console.error(`\n  Fix: revert the edit, or change the issue on GitHub and run \`npm run backlog\`.`);
      process.exit(1);
    }

    // 2. Content drift against GitHub.
    const expected = args.strict ? body : maskVolatile(body);
    const found = args.strict ? prior.body : maskVolatile(prior.body);
    if (expected === found) {
      // The BODY agrees. The header still might not: the digest above covers the body only, and
      // the comparison just made was body against body, so a hand-edited headline count has
      // passed through both untouched. This is the last place it can be caught.
      const problems = headerCountProblems(prior.header, model);
      if (problems.length) {
        console.error(`\nTAMPERED: docs/BACKLOG.md's header counts disagree with GitHub.`);
        for (const p of problems) console.error(`  - ${p}`);
        console.error(`\n  The body is intact, so this is a hand edit to the header alone.`);
        console.error(`  Fix: \`npm run backlog\` to regenerate the header from GitHub.`);
        process.exit(1);
      }
      if (!args.strict && prior.body !== body) {
        console.error(`\nOK (content matches). Only the Age column has moved with the clock — refresh with \`npm run backlog\` when convenient.`);
      } else {
        console.error(`\nOK: docs/BACKLOG.md is up to date.`);
      }
      return;
    }

    const d = diffLines(expected, found);
    console.error(`\nSTALE: docs/BACKLOG.md does not match what GitHub says.`);
    if (d.firstDiff >= 0) console.error(`  first difference at body line ${d.firstDiff + 1}`);
    if (d.missingTotal) {
      console.error(`\n  MISSING from docs/BACKLOG.md (${d.missingTotal} line(s), showing ${d.missing.length}):`);
      for (const l of d.missing) console.error(`    - ${l.slice(0, 160)}`);
    }
    if (d.unexpectedTotal) {
      console.error(`\n  UNEXPECTED in docs/BACKLOG.md (${d.unexpectedTotal} line(s), showing ${d.unexpected.length}):`);
      for (const l of d.unexpected) console.error(`    + ${l.slice(0, 160)}`);
    }
    console.error(`\n  Fix: \`npm run backlog\` (or change the issue on GitHub, then regenerate).`);
    process.exit(1);
  }

  // Carry the generation timestamp forward when the rendered body is byte-identical to what
  // is already on disk. Without this the header alone would churn on every run and the file
  // could never be byte-reproducible — the timestamp would be the only thing that ever moved.
  // It still advances the moment any real content changes.
  const generatedAt = prior && prior.body === body
    ? (headerGeneratedAt(prior.header) ?? new Date(now).toISOString())
    : new Date(now).toISOString();

  const file = renderHeader({
    generatedAt, sha, ref, repo: args.repo, digest,
    open: model.open.length, completed: model.completed.length, notPlanned: model.notPlanned.length,
  }) + body;

  if (args.dryRun) {
    process.stdout.write(file);
    console.error(`\nbacklog-sync: --dry-run — nothing written.`);
    return;
  }

  await mkdir(dirname(OUT_PATH), { recursive: true });
  await writeFile(OUT_PATH, file, "utf8");
  console.error(
    `backlog-sync: wrote docs/BACKLOG.md (${file.length.toLocaleString()} chars, ` +
    `digest sha256:${digest.slice(0, 12)}…)${existing !== null && normalizeEol(existing) === file ? " — unchanged" : ""}`,
  );
}

// Run only when invoked as the script, not when imported. Without this guard, importing anything
// from this file to TEST it would fire a live `gh` fetch and write docs/BACKLOG.md as a side
// effect of `node --test`. `npm run backlog` still runs main() exactly as before.
// Compared as resolved FILESYSTEM PATHS, not as URL strings. Both sides are absolute already
// (Node resolves process.argv[1] before handing it over), so a URL comparison also works — but it
// is fragile on Windows, where drive-letter casing and percent-encoding of spaces or non-ASCII
// characters in the checkout path can make two spellings of the same file compare unequal.
//
// The comparison must accept the symlink-RESOLVED form too. Node's ESM loader resolves
// `import.meta.url` THROUGH a symlink to the real file, while `process.argv[1]` keeps the
// symlink's own path, so a raw-only comparison silently fails when the CLI is reached through
// one: `main()` never runs, nothing is written, and the process exits 0 with no output at all —
// the quietest possible failure in a file whose every other guard refuses loudly. Reproduced on
// Node 24.0.1/Windows: `node <symlink-to-backlog-sync.mjs> --help` printed nothing and exited 0.
// (`--preserve-symlinks` inverts which side is resolved, so accept either match.)
function resolvedEntry(p) {
  try {
    return realpathSync(resolve(p));
  } catch {
    return resolve(p); // argv[1] need not exist on disk; fall back to the raw resolution.
  }
}
const selfPath = fileURLToPath(import.meta.url);
const invokedDirectly = Boolean(process.argv[1])
  && (resolve(process.argv[1]) === selfPath || resolvedEntry(process.argv[1]) === selfPath);

if (invokedDirectly) {
  main().catch((err) => {
    console.error(`backlog-sync: ${err.message}`);
    process.exit(1);
  });
}

// Exported for `backlog-sync.test.mjs`, which pins this file's tier to the shared priority model
// so the two can never silently diverge again (#364).
export { tierOfIssue, milestoneOf, signalRank, tierIdForMilestone, MILESTONES };
