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
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

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
// Signals, in the precedence order the backlog was actually authored with:
//   1. an explicit `P0:`/`P1:`/`P2:`/`P3:` prefix in the issue TITLE — always wins outright
//   2. milestone (M1 is the correctness/audit work, M7 the speculative end)
//   3. labels — defects and dependency/security work ride high; docs-only rides low
//   4. age (older first), then issue number, as the deterministic tiebreak
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

// Milestone -> rank (lower = higher priority) and the tier an issue lands in when its title
// carries no explicit prefix. Unmilestoned work sorts last within its tier: it has not been
// triaged into the roadmap yet, so it has not earned a place ahead of work that has.
const MILESTONES = [
  { title: "M1: Correctness & Audit Trail", short: "M1", tier: "P0" },
  { title: "M2: Tech-debt & Foundation", short: "M2", tier: "P1" },
  { title: "M3: Agent API Surface", short: "M3", tier: "P1" },
  { title: "M4: UI / UX Polish", short: "M4", tier: "P2" },
  { title: "M5: Docs & Process", short: "M5", tier: "P2" },
  { title: "M6: AI Memory & Ops Hub", short: "M6", tier: "P2" },
  { title: "M7: Self-Improving", short: "M7", tier: "P3" },
];
const MILESTONE_BY_TITLE = new Map(MILESTONES.map((m, i) => [m.title, { ...m, rank: i }]));
const NO_MILESTONE = { title: null, short: "—", tier: "P2", rank: MILESTONES.length };

// Labels that promote / demote a *derived* tier by one step. An explicit title prefix is
// never adjusted — it is a human's stated decision and outranks every inferred signal.
const PROMOTING_LABELS = new Set(["bug", "dependencies", "security"]);
const DEMOTING_LABELS = new Set(["documentation"]);

// Rank *within* a tier+milestone band. Defects first (something is broken), then the epic
// that groups the band's children, then ordinary build-out, then docs.
function signalRank(labels) {
  const set = new Set(labels);
  if ([...PROMOTING_LABELS].some((l) => set.has(l))) return 0;
  if (set.has("epic")) return 1;
  if (isDocsOnly(set)) return 3;
  return 2;
}

function isDocsOnly(labelSet) {
  if (labelSet.size === 0) return false;
  for (const l of labelSet) if (!DEMOTING_LABELS.has(l)) return false;
  return true;
}

const TITLE_PRIORITY = /^\s*(P[0-3])\s*[:\-–—]/i;

function clampTier(index) {
  return TIERS[Math.max(0, Math.min(TIERS.length - 1, index))].id;
}

function classify(issue) {
  const labels = (issue.labels || []).map((l) => l.name).sort();
  const milestone = MILESTONE_BY_TITLE.get(issue.milestone?.title ?? "") ?? NO_MILESTONE;

  const explicit = TITLE_PRIORITY.exec(issue.title || "");
  let tier;
  let tierSource;
  if (explicit) {
    tier = explicit[1].toUpperCase();
    tierSource = "title";
  } else {
    let idx = TIER_INDEX.get(milestone.tier);
    const set = new Set(labels);
    if ([...PROMOTING_LABELS].some((l) => set.has(l))) idx -= 1;
    else if (isDocsOnly(set)) idx += 1;
    tier = clampTier(idx);
    tierSource = milestone.title ? "milestone+labels" : "labels";
  }

  return {
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
    signalRank(a.labels) - signalRank(b.labels) ||
    Date.parse(a.createdAt) - Date.parse(b.createdAt) ||
    a.number - b.number
  );
}

// --- GitHub (read-only) -----------------------------------------------------------------

async function gh(args) {
  const { stdout } = await execFileAsync("gh", args, { maxBuffer: 64 * 1024 * 1024 });
  return JSON.parse(stdout || "[]");
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
  p("1. **Explicit title prefix** — a `P0:`/`P1:`/`P2:`/`P3:` prefix in the issue title wins outright and is never adjusted. It is a human's stated decision.");
  p("2. **Milestone** — otherwise the tier is inherited from the milestone: " +
    MILESTONES.map((m) => `\`${m.short}\`→${m.tier}`).join(", ") + ", no milestone→P2.");
  p("3. **Labels** — a derived tier is promoted one step by `bug` / `dependencies` / `security`, and demoted one step when the issue is `documentation`-only. Within a band: defects → epics → build-out → docs.");
  p("4. **Age, then issue number** — older first; the issue number is the final tiebreak. Because numbers are unique the order is *total*, so re-running the generator over unchanged input reproduces this file byte for byte.");
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
    "> **This file is GENERATED — do not hand-edit it.** `scripts/backlog-sync.mjs` is the only",
    "> sanctioned way to update it. Hand edits are detected and rejected by",
    "> `node scripts/backlog-sync.mjs --check`, which is also how you tell whether this file has",
    "> fallen behind GitHub. To change what appears here, change the issue on GitHub and",
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

function assertRendered(body, model) {
  // Count the rows actually rendered into the open section and compare against what `gh`
  // returned. This is a check on the OUTPUT TEXT, not on the in-memory arrays — it is the
  // only assertion that can catch a rendering bug that drops a row.
  const openSection = body.slice(
    body.indexOf("## 📋 Open backlog"),
    body.indexOf("## ✅ Completed"),
  );
  const rows = openSection.match(/^\| \d+ \| \[#\d+\]\(/gm) || [];
  if (rows.length !== model.open.length) {
    throw new Error(
      `Rendered ${rows.length} open rows but gh returned ${model.open.length} open issues. ` +
      `Refusing to write an incomplete log book.`,
    );
  }
  const ledger = body.slice(body.indexOf("## ✅ Completed"), body.indexOf("## 🚫 Closed — not planned"));
  const ledgerRows = ledger.match(/^\| \[#\d+\]\(/gm) || [];
  if (ledgerRows.length !== model.completed.length) {
    throw new Error(`Rendered ${ledgerRows.length} completed rows but ${model.completed.length} issues are closed-as-completed.`);
  }
  return rows.length;
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
      console.error(`\nTAMPERED: docs/BACKLOG.md has been hand-edited.`);
      console.error(`  header claims  sha256:${claimed}`);
      console.error(`  body hashes to sha256:${actualDigest}`);
    }

    // 2. Content drift against GitHub.
    const expected = args.strict ? body : maskVolatile(body);
    const found = args.strict ? prior.body : maskVolatile(prior.body);
    if (expected === found && (!claimed || claimed === actualDigest)) {
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

main().catch((err) => {
  console.error(`backlog-sync: ${err.message}`);
  process.exit(1);
});
