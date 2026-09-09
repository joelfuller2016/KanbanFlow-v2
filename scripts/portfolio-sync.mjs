#!/usr/bin/env node
// GitHub <-> KanbanFlow sync — "KanbanFlow IS GitHub, visualised".
//
// GitHub is the source of truth for issue CONTENT. The board is the visualisation, plus the
// one place where work that does not exist on GitHub yet can be drafted and then pushed.
//
// Commands (full contract: docs/GITHUB-SYNC.md):
//
//   sync        GitHub -> board mirror. The default command.
//   onboard     register a NEW repo: create/link the Project, then backfill everything.
//   restore-content  recover incomplete legacy descriptions for one explicit repo.
//   push-issue  PREVIEW ONLY. Computes and prints the exact GitHub issue a board task would
//               become, and REFUSES to perform the write. See "why" below.
//   status      health check: counts both sides, unsynced tasks, drift, orphans, last sync.
//               Exit 0 = clean and complete · 1 = confirmed drift · 2 = incomplete or error.
//
// THIS FILE NEVER WRITES TO GITHUB. There is no create/update/close/label/delete call in it —
// `grep -n "gh\(\"\|(\[\"\)" ` finds only reads. push-issue stops one step short of the write
// on purpose: the board has no durable external-id column, so a task's only identity is the
// "#<n> " title prefix, and that prefix does not exist until AFTER the issue is created. That
// window is unrecoverable under additive-only rules — a crash, a concurrent push, or a sync
// landing between create and stamp produces a duplicate issue that nothing is allowed to
// close. Enabling the write needs a pending-operation id persisted BEFORE the GitHub call;
// that is schema work, tracked separately. Until then, preview is the honest deliverable.
//
// Invariants this tool holds:
//   * GitHub owns issue content. Nothing here can reword, close, re-label or delete an issue.
//   * Every board mutation goes through the REST API — never SQLite/EF directly — so the
//     ActivityEvent audit trail stays complete.
//   * Idempotent. A second consecutive run of any command creates nothing.
//   * --dry-run works for every command and prints exactly what WOULD be written.
//
// Status ownership is NOT symmetric, and the doc says so rather than claiming otherwise:
// the sync ADVANCES a task to Review on an open linked PR only from Backlog/Todo/InProgress
// (a hand-set Review/Done is left alone and reported as drift), but a CLOSED issue FORCES the
// task to Done regardless of what a human set. There is no manual-pin field on TaskItem, so
// "never override a hand-set status" is an aspiration, not the current behaviour. See the
// field-ownership table in docs/GITHUB-SYNC.md.
//
// Identity: repo -> Project (durable "<!-- github: owner/repo -->" marker in the project
// description; short-name matching survives only as a fallback for pre-marker boards),
// milestone -> Feature (matched by name), issue -> TaskItem (matched by the "#<n> " title
// prefix), GitHub comment -> TaskComment (matched by node id).
//
//   node scripts/portfolio-sync.mjs --dry-run
//   node scripts/portfolio-sync.mjs --repos joelfuller2016/ZeroRouter
//   node scripts/portfolio-sync.mjs onboard joelfuller2016/new-repo
//   node scripts/portfolio-sync.mjs push-issue joelfuller2016/new-repo --all --dry-run
//   node scripts/portfolio-sync.mjs status
//   KANBANFLOW_PORTFOLIO_REPOS="o/a,o/b" node scripts/portfolio-sync.mjs

import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { realpathSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
// BOTH imports are used. `derivePriority` sets TaskItem.Priority in `syncTasks()` and backs the
// priority-drift check in `statusRepo()`; `priorityFromLabel` is called by `labelForPriority()`
// below, which picks the label a pushed issue should carry so the priority survives the round trip.
//
// This line used to carry a comment calling `priorityFromLabel` "imported but unused HERE on
// purpose" — true only while its caller was still on the unmerged #339 branch. #339 has landed
// and the caller is in this file. The stale claim was itself the hazard: it invited exactly the
// "clean up the unused import" edit that throws ReferenceError the first time `push-issue` runs.
// Search for call sites before removing either name.
import { derivePriority, priorityFromLabel } from "./priority-model.mjs";

const execFileAsync = promisify(execFile);

const apiBaseUrl = (process.env.KANBANFLOW_API_URL || "http://127.0.0.1:5123").replace(/\/$/, "");
const restTimeoutMs = Number.parseInt(process.env.KANBANFLOW_MCP_TIMEOUT_MS || "15000", 10) || 15000;

const DEFAULT_REPOS = [
  "joelfuller2016/KanbanFlow-v2",
  "joelfuller2016/ZeroRouter",
  "joelfuller2016/idea-builder-v2",
  "joelfuller2016/job-scraper-mvp",
  "joelfuller2016/Job-Email-Tracker",
  "joelfuller2016/resume-builder",
];

const UNSORTED_FEATURE = "Unsorted";
const TRUNCATION_FOOTER = "…(truncated — see the issue)";
const COMMENT_TRUNCATION_FOOTER = "…(truncated — read it on GitHub)";
const COMPLETE_COMMENT_MARKER = "<!-- kanbanflow-comment:complete -->";
const OVERSIZED_HEADER_STUB = "<!-- kanbanflow-sync -->\n**Issue:** (provenance omitted: header exceeded description budget)\n<!-- /kanbanflow-sync -->";
// Historical markers are read only to repair old imports. Source text is never shortened.
// High enough that no repo in the portfolio comes near it, because SATURATING it is a hard
// failure, not a warning: a read that stopped early makes every count and every "no drift"
// verdict downstream a lie. Overridable so the incompleteness path can actually be exercised.
const GH_PAGE_LIMIT = Math.max(1, Number.parseInt(process.env.KANBANFLOW_GH_PAGE_LIMIT || "2000", 10) || 2000);

const COMMANDS = ["sync", "onboard", "restore-content", "push-issue", "status"];

// Statuses the sync is allowed to advance a task *out of* when GitHub shows a linked open
// PR. A task a human has already parked in Review/Done is left alone (reported as drift
// instead) — the sync must never fight the board's owner.
const ADVANCEABLE_TO_REVIEW = new Set(["Backlog", "Todo", "InProgress"]);

function parseArgs(argv) {
  const args = {
    command: "sync",
    dryRun: false, includeClosed: false, repos: null, actor: "portfolio-sync",
    comments: true,
    allowWrite: false, task: null, all: false, strict: false, json: false, limit: 0,
    error: null,
  };

  // The command is an optional leading bare word. Omitting it means `sync`, so every
  // pre-existing invocation (`portfolio-sync.mjs --dry-run`, `--repos …`) behaves exactly
  // as it always has. A bare word that is NOT a command is an error rather than a silent
  // fallback to sync — mistyping `statuss` must not quietly run a full mirror.
  let i = 0;
  if (argv.length > 0 && !argv[0].startsWith("-") && !argv[0].includes("/")) {
    if (!COMMANDS.includes(argv[0])) {
      args.error = `Unknown command "${argv[0]}". Valid commands: ${COMMANDS.join(", ")}.`;
      return args;
    }
    args.command = argv[0];
    i = 1;
  }

  // Every flag below that consumes the NEXT argv token goes through this. Un-guarded, that
  // token is taken on faith, so `--max-comment-chars --dry-run` EATS the --dry-run: the loop
  // never sees it, `args.dryRun` stays false, and `sync` with no --repos falls through to all
  // of DEFAULT_REPOS. The operator asked for a preview and got a live six-repo mirror —
  // projects, features, tasks and every mirrored comment, none of it reversible.
  //
  // Every plausible "just cope with it" answer is worse than refusing, because all of them
  // still RUN: `Number.parseInt("--dry-run", 10) || 0` is a clean-looking 0, `--actor` keeps
  // "--dry-run" as the audit-trail actor, `--repos` builds ["--dry-run"]. Only an error stops
  // the write. main() prints args.error and exits 2 before it touches a repo, so a refusal is
  // free and a swallow is not. This lives in ONE helper on purpose: hand-written per-flag
  // copies are exactly how the check ends up on one flag and not the other four.
  const takeValue = (flag, need) => {
    const value = argv[i + 1];
    if (value === undefined || value.trim() === "" || value.trim().startsWith("-")) {
      const got = value === undefined ? "nothing" : value.trim() === "" ? "a blank value" : `the flag "${value}"`;
      args.error = `${flag} needs ${need}, but got ${got}. A flag is never a value: consuming one silently discards it, and a swallowed --dry-run runs a LIVE sync.`;
      return null;
    }
    i += 1;
    return value.trim();
  };

  for (; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--dry-run") args.dryRun = true;
    else if (a === "--include-closed") args.includeClosed = true;
    else if (a === "--no-comments") args.comments = false;
    else if (a === "--max-comment-chars") {
      args.error = "--max-comment-chars is no longer supported: full comment text must be preserved. Remove this option.";
      return args;
    }
    else if (a === "--repos") {
      const value = takeValue("--repos", "a comma-separated owner/repo list");
      if (value === null) return args;
      args.repos = [...(args.repos || []), ...value.split(",").map((r) => r.trim()).filter(Boolean)];
    }
    else if (a === "--actor") {
      const value = takeValue("--actor", "a non-blank name");
      if (value === null) return args;
      args.actor = value;
    }
    // --push is the alias; both spellings mean "you may create issues on GitHub".
    else if (a === "--allow-write" || a === "--push") args.allowWrite = true;
    else if (a === "--task") {
      const value = takeValue("--task", "an integer task id");
      if (value === null) return args;
      args.task = Number.parseInt(value, 10);
    }
    else if (a === "--all") args.all = true;
    else if (a === "--strict") args.strict = true;
    else if (a === "--json") args.json = true;
    else if (a === "--limit") {
      const value = takeValue("--limit", "an integer count");
      if (value === null) return args;
      args.limit = Number.parseInt(value, 10) || 0;
    }
    else if (a === "--help" || a === "-h") args.help = true;
    // `onboard owner/repo` — a bare owner/repo positional is a repo, so the common case
    // needs no flag at all.
    else if (!a.startsWith("-") && a.includes("/")) args.repos = [...(args.repos || []), a];
    else { args.error = `Unknown option "${a}".`; return args; }
  }

  if (args.task !== null && !Number.isFinite(args.task)) args.error = "--task needs an integer task id.";
  return args;
}

function resolveRepos(args) {
  if (args.repos?.length) return args.repos;
  const env = process.env.KANBANFLOW_PORTFOLIO_REPOS;
  if (env) return env.split(",").map((r) => r.trim()).filter(Boolean);
  return DEFAULT_REPOS;
}

// --- GitHub (read-only) -----------------------------------------------------------------

// "This file never writes to GitHub" is a property a future edit must not be able to break by
// accident, so it is enforced here instead of at review time. A WHITELIST, not a blacklist:
// anything that is not one of these four read verbs is refused, so a newly added `gh issue
// create` / `issue edit` / `issue close` fails immediately and loudly.
const GH_READ_VERBS = new Set(["issue list", "pr list", "label list", "api"]);
// `gh api` is GET unless something makes it otherwise. These are the flags that do.
const GH_API_WRITE_FLAGS = new Set(["--method", "-X", "-f", "--field", "-F", "--raw-field", "--input"]);

function assertReadOnly(args) {
  const verb = args[0] === "api" ? "api" : `${args[0]} ${args[1]}`;
  if (!GH_READ_VERBS.has(verb)) {
    throw new Error(`portfolio-sync is READ-ONLY against GitHub; refusing to run "gh ${verb}". If a write is genuinely needed, it belongs behind the durable-link design in docs/GITHUB-SYNC.md, not here.`);
  }
  if (args[0] === "api") {
    const offender = args.find((a) => GH_API_WRITE_FLAGS.has(a) || /^--method=/.test(a));
    if (offender) throw new Error(`portfolio-sync is READ-ONLY against GitHub; "gh api ${offender}" would issue a write.`);
  }
}

async function gh(args) {
  assertReadOnly(args);
  const { stdout } = await execFileAsync("gh", args, { maxBuffer: 64 * 1024 * 1024 });
  return JSON.parse(stdout || "[]");
}

// `comments` rides along on the list call we already make, so issue comments cost zero extra
// requests. Each carries a stable node id (e.g. `IC_kwDO…`) — that id is the dedupe key that
// makes comment mirroring idempotent.
const ISSUE_FIELDS = "number,title,body,labels,milestone,assignees,state,updatedAt,url,comments";

// Never truncate silently: a board that quietly drops issues past the limit still *looks*
// like a full mirror, which is the one thing this tool must not do. Saturation is RECORDED,
// not just printed — `status` turns it into exit 2 ("I could not see everything"), because a
// drift count computed over a partial read is worse than no drift count at all.
function warnIfTruncated(repo, kind, list) {
  if (list.length >= GH_PAGE_LIMIT) {
    const note = `${repo}: ${kind} hit the ${GH_PAGE_LIMIT}-item fetch bound — this read is INCOMPLETE, so every count and every "no drift" verdict derived from it is unreliable. Raise KANBANFLOW_GH_PAGE_LIMIT.`;
    console.error(`  ! ${note}`);
    stats.incomplete.push(note);
  }
  return list;
}

async function fetchOpenIssues(repo) {
  const list = await gh([
    "issue", "list", "-R", repo, "--state", "open",
    "--limit", String(GH_PAGE_LIMIT), "--json", ISSUE_FIELDS,
  ]);
  return warnIfTruncated(repo, "open issues", list);
}

// Closed issues are fetched even when we aren't mirroring them, because a task already on
// the board whose issue has since been closed must still converge to Done. Without this the
// close is invisible (the issue simply vanishes from the open set) and the task is instead
// mis-reported as drift forever. Cheap by default: numbers only.
async function fetchClosedIssues(repo, full) {
  const list = await gh([
    "issue", "list", "-R", repo, "--state", "closed",
    "--limit", String(GH_PAGE_LIMIT), "--json", full ? ISSUE_FIELDS : "number",
  ]);
  return warnIfTruncated(repo, "closed issues", list);
}

async function fetchOpenPrs(repo, withComments) {
  const fields = withComments
    ? "number,title,body,url,updatedAt,reviewDecision,isDraft,comments,reviews"
    : "number,title,body,url,updatedAt,reviewDecision,isDraft";
  const list = await gh([
    "pr", "list", "-R", repo, "--state", "open",
    "--limit", String(GH_PAGE_LIMIT), "--json", fields,
  ]);
  warnIfTruncated(repo, "open PRs", list);

  // Inline review comments (the file:line ones) are not exposed on `gh pr list` — they need
  // the REST endpoint, one call per PR. Fan them out rather than spawning `gh` serially:
  // a repo with a deep PR queue would otherwise pay the full process-spawn latency per PR.
  //
  // A failure here is REPORTED, never swallowed. `.catch(() => [])` alone would make an auth /
  // rate-limit / network error indistinguishable from "this PR genuinely has no inline comments"
  // — the board would quietly lose a whole review and still look like a complete mirror, which
  // is the exact failure mode the truncation warning above exists to prevent.
  if (withComments) {
    await Promise.all(list.map(async (pr) => {
      try {
        pr.inlineComments = await gh(["api", `repos/${repo}/pulls/${pr.number}/comments`, "--paginate"]);
      } catch (err) {
        pr.inlineComments = [];
        console.warn(`  ! ${repo} PR #${pr.number}: could NOT fetch inline review comments — ${err.message}`);
        console.warn(`    Its file:line review comments are MISSING from the board. Re-run once the cause is fixed.`);
        stats.commentFetchFailures++;
      }
    }));
  }
  return list;
}

// Milestones are the Feature axis. Read-only: a milestone is only ever ATTACHED in a
// push-issue preview, never created.
async function fetchMilestones(repo) {
  return gh(["api", `repos/${repo}/milestones?state=all&per_page=100`, "--paginate"]);
}

// The repo's label vocabulary. A push-issue preview picks the label that priorityFromLabel()
// would read back as the priority the task already has — without it, pushing a High task
// would create an unlabelled issue that the very next sync reads back as Medium (a silent
// round-trip downgrade). Reported in the preview so the loss is visible before anyone acts.
async function fetchLabels(repo) {
  return gh(["label", "list", "-R", repo, "--limit", "200", "--json", "name"]);
}

// --- GitHub comments -> TaskComments -----------------------------------------------------

// A GitHub comment's node id is stable and globally unique. Stamping it into the comment body
// makes the mirror idempotent with no schema change: on re-sync we read back what's already on
// the task and skip anything whose id we've seen. (TaskComment has no external-id column —
// this is the same trick as the "#<n> " task-title prefix.)
const GH_ID_MARKER = /<!--\s*gh:([^\s>]+)\s*-->/;

function ghIdOf(commentText) {
  const m = GH_ID_MARKER.exec(String(commentText || ""));
  return m ? m[1] : null;
}

// KanbanFlow's own comment vocabulary carries intent, so a PR review's verdict maps onto it
// rather than being flattened to a generic Comment. Allowed types (ApiEndpoints.cs:2554):
// Note, Decision, Question, Blocked, Handoff, Verification, Comment.
function commentTypeForReview(state) {
  switch (String(state || "").toUpperCase()) {
    case "APPROVED": return "Verification";
    case "CHANGES_REQUESTED": return "Blocked";
    default: return "Comment";
  }
}

// Flatten every GitHub comment surface attached to an issue (and its linked PR) into one
// ordered list of {ghId, author, type, text} ready to POST.
function collectComments(issue, linkedPr) {
  const out = [];

  for (const c of issue.comments || []) {
    out.push({
      ghId: c.id,
      author: c.author?.login || "github",
      type: "Comment",
      createdAt: c.createdAt,
      header: `_[issue comment](${c.url || issue.url})_`,
      body: c.body || "",
    });
  }

  if (linkedPr) {
    for (const c of linkedPr.comments || []) {
      out.push({
        ghId: c.id,
        author: c.author?.login || "github",
        type: "Comment",
        createdAt: c.createdAt,
        header: `_[PR #${linkedPr.number} comment](${c.url || linkedPr.url})_`,
        body: c.body || "",
      });
    }
    for (const r of linkedPr.reviews || []) {
      // A review with no body and no verdict is noise (GitHub emits one per inline batch).
      const state = String(r.state || "").toUpperCase();
      if (!r.body && (state === "COMMENTED" || !state)) continue;
      out.push({
        ghId: r.id,
        author: r.author?.login || "github",
        type: commentTypeForReview(state),
        createdAt: r.submittedAt || r.createdAt,
        header: `_[PR #${linkedPr.number} review · **${state || "COMMENTED"}**](${r.url || linkedPr.url})_`,
        body: r.body || `(no body — ${state})`,
      });
    }
    for (const c of linkedPr.inlineComments || []) {
      out.push({
        ghId: c.node_id || String(c.id),
        author: c.user?.login || "github",
        type: "Comment",
        createdAt: c.created_at,
        header: `_[PR #${linkedPr.number} · \`${c.path}:${c.line ?? c.original_line ?? "?"}\`](${c.html_url || linkedPr.url})_`,
        body: c.body || "",
      });
    }
  }

  out.sort((a, b) => new Date(a.createdAt || 0) - new Date(b.createdAt || 0));
  return out;
}

function commentTextFor(c) {
  return `<!-- gh:${c.ghId} -->\n${COMPLETE_COMMENT_MARKER}\n${c.header}\n\n${c.body ?? ""}`;
}

function completeCommentIds(comments, incoming = []) {
  const completeLegacyText = new Map(incoming.map((c) => [c.ghId, `<!-- gh:${c.ghId} -->\n${c.header}\n\n${c.body ?? ""}`]));
  return new Set(comments.filter((c) => {
    const text = String(c.text || "");
    return /^<!-- gh:[^>]+ -->\r?\n<!-- kanbanflow-comment:complete -->\r?\n/.test(text)
      || text === completeLegacyText.get(ghIdOf(text))
      || !text.trimEnd().endsWith(COMMENT_TRUNCATION_FOOTER);
  }).map((c) => ghIdOf(c.text)).filter(Boolean));
}

// "Closes #12", "fixes #34", "resolved: #56" — GitHub's own closing-keyword vocabulary.
const CLOSING_KEYWORDS = /\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b[:\s]*#(\d+)/gi;

function mapIssueToOpenPr(prs) {
  const byIssue = new Map();
  for (const pr of prs) {
    const body = pr.body || "";
    for (const m of body.matchAll(CLOSING_KEYWORDS)) {
      const issueNumber = Number.parseInt(m[1], 10);
      if (Number.isFinite(issueNumber) && !byIssue.has(issueNumber)) byIssue.set(issueNumber, pr);
    }
  }
  return byIssue;
}

// --- Mapping ----------------------------------------------------------------------------

// Priority resolution (title prefix -> label -> milestone -> Medium) lives in
// ./priority-model.mjs, which is the single definition for the whole repo — the board and the
// backlog log book both read it, so they cannot disagree. See #355 for the rule and #364 for
// the decision that settled it.

function deriveStatus(issue, linkedPr) {
  if (issue.state && issue.state.toUpperCase() === "CLOSED") return "Done";
  if (linkedPr) return "Review";
  if ((issue.assignees || []).length > 0) return "InProgress";
  return "Backlog";
}

function featureNameFor(issue) {
  const title = issue.milestone?.title?.trim();
  return title || UNSORTED_FEATURE;
}

function taskNameFor(issue) {
  const title = String(issue.title || "").trim();
  return `#${issue.number} ${title}`;
}

// The "#<n> " prefix is the idempotency key: it survives round-trips through the board and
// is how a re-sync recognises a task it already created. (A durable TaskItem.IssueNumber
// column is #237's job; this needs no schema change.)
function issueNumberFromTaskName(name) {
  const m = /^#(\d+)\s/.exec(String(name || ""));
  return m ? Number.parseInt(m[1], 10) : null;
}

// --- repo <-> project link ---------------------------------------------------------------
//
// Name matching is fragile: rename the repo or the project and the mirror silently forks into
// a second project. The durable link is a marker stamped into the project description — the
// same trick as the "#<n> " task prefix and the "<!-- gh:id -->" comment id, and it needs no
// schema change. Resolution order is marker -> short name, so boards synced before the marker
// existed keep working untouched.
const REPO_LINK_RE = /<!--\s*github:\s*([A-Za-z0-9._-]+\/[A-Za-z0-9._-]+)\s*-->/;

function repoLinkMarker(repo) {
  return `<!-- github: ${repo} -->`;
}

function linkedRepoOf(project) {
  const m = REPO_LINK_RE.exec(String(project?.description || ""));
  return m ? m[1] : null;
}

function projectDescriptionFor(repo) {
  return `${repoLinkMarker(repo)}\nSynced from https://github.com/${repo} — GitHub issues are the source of truth.`;
}

// Returns { project, linkedBy: "marker" | "name" | null, conflict }. `conflict` is a
// human-readable warning that is ALWAYS surfaced — a forked or hijacked link is exactly the
// class of failure that otherwise looks like a healthy mirror.
function resolveProjectLink(repo, projects) {
  const shortName = repo.split("/").pop();
  const lower = repo.toLowerCase();

  const marked = projects.filter((p) => (linkedRepoOf(p) || "").toLowerCase() === lower);
  if (marked.length > 1) {
    return {
      project: marked[0], linkedBy: "marker",
      conflict: `${marked.length} projects carry the ${repo} link marker (${marked.map((p) => `#${p.id} "${p.name}"`).join(", ")}) — the mirror is FORKED. Merge them by hand; syncing into #${marked[0].id}.`,
    };
  }
  if (marked.length === 1) return { project: marked[0], linkedBy: "marker", conflict: null };

  // Fallback: match the repo's short name, but only against a project no OTHER repo has
  // claimed. Adopting a project already marked for a different repo would merge two repos'
  // issues into one board permanently.
  const named = projects.filter((p) => p.name.toLowerCase() === shortName.toLowerCase());
  const unclaimed = named.filter((p) => linkedRepoOf(p) === null);
  if (unclaimed.length === 1) return { project: unclaimed[0], linkedBy: "name", conflict: null };
  if (unclaimed.length > 1) {
    return {
      project: unclaimed[0], linkedBy: "name",
      conflict: `${unclaimed.length} unlinked projects are named "${shortName}" (${unclaimed.map((p) => `#${p.id}`).join(", ")}) — name matching is ambiguous. Run \`onboard ${repo}\` after deleting or renaming the wrong one; using #${unclaimed[0].id}.`,
    };
  }
  if (named.length > 0) {
    return {
      project: null, linkedBy: null,
      conflict: `project "${named[0].name}" (#${named[0].id}) is already linked to ${linkedRepoOf(named[0])}, so ${repo} will NOT be merged into it.`,
    };
  }
  return { project: null, linkedBy: null, conflict: null };
}

// The sync stamps a provenance block into every task description it creates. When a task is
// previewed for the other direction that block is noise on GitHub, so strip it out.
function stripSyncBlock(description) {
  return String(description || "")
    .replace(/<!--\s*kanbanflow-sync\s*-->[\s\S]*?<!--\s*\/kanbanflow-sync\s*-->/g, "")
    .trim();
}

const OPEN_FENCE_LINE = /^ {0,3}(`{3,}|~{3,})(.*)$/;
const CLOSE_FENCE_LINE = /^ {0,3}(`{3,}|~{3,})[ \t]*$/;

function unclosedFence(text) {
  let open = null;
  for (const rawLine of text.split("\n")) {
    const line = rawLine.replace(/\r$/, "");
    if (open) {
      const close = CLOSE_FENCE_LINE.exec(line);
      const marker = close?.[1];
      if (marker && marker[0] === open[0] && marker.length >= open.length) open = null;
      continue;
    }
    const opener = OPEN_FENCE_LINE.exec(line);
    if (!opener) continue;
    const marker = opener[1];
    const info = opener[2];
    if (marker[0] === "`" && info.includes("`")) continue;
    open = marker;
  }
  return open;
}

function descriptionFor(issue, linkedPr) {
  const body = String(issue.body ?? "");
  const labels = (issue.labels || []).map((l) => l.name).join(", ") || "none";
  const milestone = issue.milestone?.title || "none";
  const lines = [
    "<!-- kanbanflow-sync -->",
    `**Issue:** ${issue.url}`,
    `**State:** ${issue.state || "OPEN"} · **Milestone:** ${milestone} · **Labels:** ${labels}`,
    linkedPr ? `**Open PR:** ${linkedPr.url}${linkedPr.isDraft ? " (draft)" : ""}` : "**Open PR:** none",
    `**Synced:** ${new Date().toISOString()}`,
    "**Content:** complete",
    `**Source SHA-256:** ${createHash("sha256").update(body, "utf8").digest("hex")}`,
    "<!-- /kanbanflow-sync -->",
    "",
  ];
  return `${lines.join("\n")}\n${body}`;
}

function syncDescriptionParts(description) {
  const match = /^(<!-- kanbanflow-sync -->\r?\n\*\*Issue:\*\* ([^\r\n]+)\r?\n[\s\S]*?<!-- \/kanbanflow-sync -->)\r?\n\r?\n([\s\S]*)$/.exec(String(description || ""));
  return match ? { header: match[1], issueUrl: match[2], body: match[3] } : null;
}

function needsContentRestoration(description) {
  if (description === OVERSIZED_HEADER_STUB) return true;
  const parts = syncDescriptionParts(description);
  return Boolean(parts && !/^\*\*Content:\*\* complete\r?$/m.test(parts.header)
    && parts.body.trimEnd().endsWith(TRUNCATION_FOOTER));
}

// Restore only recognizable incomplete imports. A changed source or local edit is kept in a
// labeled section as well as the existing audit snapshot; no assumption of GitHub ownership
// is used to discard differing board text. Complete imports remain untouched (#495).
function descriptionRefreshFor(storedDescription, regeneratedDescription) {
  if (!needsContentRestoration(storedDescription)) return null;
  const next = syncDescriptionParts(regeneratedDescription);
  if (!next || !/^\*\*Content:\*\* complete\r?$/m.test(next.header)) return null;
  if (storedDescription === OVERSIZED_HEADER_STUB) return regeneratedDescription;
  const old = syncDescriptionParts(storedDescription);
  if (old.issueUrl !== next.issueUrl) return null;
  const beforeFooter = old.body.trimEnd().slice(0, -TRUNCATION_FOOTER.length);
  const kept = beforeFooter.endsWith("\r\n\r\n") ? beforeFooter.slice(0, -4)
    : beforeFooter.endsWith("\n\n") ? beforeFooter.slice(0, -2) : beforeFooter;
  const sourceHasPrefix = (prefix) => next.body.startsWith(prefix) || next.body.trimStart().startsWith(prefix);
  let matchesSource = sourceHasPrefix(kept);
  // Older clipping closed an open Markdown fence synthetically. Recognize that one added
  // closing line, while retaining any differing text verbatim in the preservation section.
  const lastLine = kept.lastIndexOf("\n");
  if (!matchesSource && lastLine >= 0) {
    const prefix = kept.slice(0, lastLine);
    const closer = kept.slice(lastLine + 1).trim();
    matchesSource = unclosedFence(prefix) === closer && sourceHasPrefix(prefix);
  }
  if (matchesSource) return regeneratedDescription;
  return `${regeneratedDescription}\n\n<!-- kanbanflow-preserved-board-text -->\n## Preserved prior board text\n\nThe prior board text differs from the current GitHub source. It is retained below; the complete current issue is above.\n\n${kept}\n<!-- /kanbanflow-preserved-board-text -->`;
}

// --- KanbanFlow REST --------------------------------------------------------------------

async function api(method, path, body) {
  const res = await fetch(`${apiBaseUrl}${path}`, {
    method,
    headers: body ? { "content-type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(restTimeoutMs),
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(`${method} ${path} -> ${res.status} ${res.statusText}${text ? `: ${text.slice(0, 300)}` : ""}`);
  }
  if (res.status === 204) return null;
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

// --- Sync -------------------------------------------------------------------------------

const stats = {
  projectsCreated: 0, featuresCreated: 0, tasksCreated: 0,
  tasksUpdated: 0, tasksMoved: 0, tasksUnchanged: 0, descriptionsRefreshed: 0,
  commentsCreated: 0, commentsUnchanged: 0, commentFetchFailures: 0,
  projectsLinked: 0, issuesPreviewed: 0,
  biggestComment: { chars: 0, author: null, issue: null },
  drift: [],
  incomplete: [], // a fetch that hit its bound — every count derived from it is unreliable
};

function plan(dryRun, verb, what) {
  console.log(`  ${dryRun ? "[dry-run] would" : "        "} ${verb} ${what}`);
}

// Drift wording lives here, shared, so `status` can never disagree with what `sync` reports.
// Two kinds, and only two: both describe a disagreement the sync deliberately WILL NOT fix.
const driftDoneButOpen = (repo, issue) =>
  `${repo}#${issue.number}: board says Done, issue is still open — ${issue.url}`;
const driftOrphanTask = (repo, taskName, issueNumber) =>
  `${repo}: task "${taskName}" has no matching issue #${issueNumber} on GitHub (deleted or transferred?)`;

async function ensureProject(repo, dryRun) {
  const shortName = repo.split("/").pop();
  const projects = await api("GET", "/api/projects");
  // Marker first, then the case-insensitive short name ("ZeroRouter" and "zerorouter" are the
  // same repo, and creating a second project for one repo would fork the mirror permanently).
  const link = resolveProjectLink(repo, projects);
  if (link.conflict) console.warn(`  ! ${link.conflict}`);
  if (link.project) return link.project.id;

  // The short name is taken by a project another repo owns, so it cannot be reused without
  // merging two repos' issues into one board. Fall back to the unambiguous "owner/repo".
  const nameTaken = projects.some((p) => p.name.toLowerCase() === shortName.toLowerCase());
  const name = nameTaken ? repo : shortName;

  plan(dryRun, "create project", name);
  if (dryRun) return null;
  const created = await api("POST", "/api/projects", {
    name,
    description: projectDescriptionFor(repo),
  });
  stats.projectsCreated++;
  return created.id;
}

async function ensureFeatures(projectId, names, dryRun) {
  const existing = projectId ? await api("GET", `/api/projects/${projectId}/features`) : [];
  const byName = new Map(existing.map((f) => [f.name, f.id]));

  for (const name of names) {
    if (byName.has(name)) continue;
    plan(dryRun, "create feature", name);
    if (dryRun) { byName.set(name, null); continue; }
    const created = await api("POST", `/api/projects/${projectId}/features`, { name });
    byName.set(name, created.id);
    stats.featuresCreated++;
  }
  return byName;
}

// Mirror an issue's (and its linked PR's) GitHub comments onto the task, skipping any whose
// node id is already present. Returns nothing; updates stats.
// taskId is null ONLY on the dry-run "this task would be created" path — a task that does not
// exist yet cannot have comments, so an empty dedupe set is correct there. Everywhere else a real
// id is required, and the guard below enforces that rather than trusting the call sites.
async function syncComments(issue, linkedPr, taskId, args, request = api) {
  const incoming = collectComments(issue, linkedPr);
  if (incoming.length === 0) return;

  // Dedupe against what is already on the task. This read is gated on whether the task EXISTS
  // (taskId != null) — deliberately NOT on dry-run. Gating it on dryRun was a real bug: a
  // --dry-run against an already-synced board skipped the read, so `seen` was always empty and
  // every previously-mirrored comment was reported as "would add". A preview that overstates its
  // own writes is worse than no preview — it is exactly the lie --dry-run exists to prevent.
  // Reads are safe under --dry-run; only writes are suppressed.
  const existing = taskId == null ? [] : await request("GET", `/api/tasks/${taskId}/comments`);
  // A legacy clipped import is not a complete copy. Append the full source under the same
  // node identity, preserving its earlier history; the full marker makes the next run a no-op.
  const seen = completeCommentIds(existing || [], incoming);

  for (const c of incoming) {
    if (seen.has(c.ghId)) { stats.commentsUnchanged++; continue; }
    const text = commentTextFor(c);
    plan(args.dryRun, "add comment", `#${issue.number} ← ${c.author} [${c.type}] ${c.body.length.toLocaleString()} chars`);
    if (args.dryRun) { stats.commentsCreated++; continue; }
    if (taskId == null) {
      throw new Error(`syncComments: taskId is null outside --dry-run (issue #${issue.number}) — refusing to POST to /api/tasks/null/comments`);
    }
    await request("POST", `/api/tasks/${taskId}/comments`, { text, author: c.author, type: c.type });
    seen.add(c.ghId);
    stats.commentsCreated++;
    if (c.body.length > stats.biggestComment.chars) {
      stats.biggestComment = { chars: c.body.length, author: c.author, issue: issue.number };
    }
  }
}

async function restorationSources(repo) {
  const [issues, prs] = await Promise.all([
    gh(["issue", "list", "-R", repo, "--state", "all", "--limit", String(GH_PAGE_LIMIT),
      "--json", "number,title,body,labels,milestone,assignees,state,updatedAt,url"]),
    fetchOpenPrs(repo, false),
  ]);
  if (issues.length >= GH_PAGE_LIMIT || prs.length >= GH_PAGE_LIMIT) {
    throw new Error("Content restoration requires a complete source read. Raise KANBANFLOW_GH_PAGE_LIMIT and retry; no descriptions were changed.");
  }
  return { issues, prByIssue: mapIssueToOpenPr(prs) };
}

// A content-only repair does not run normal sync's title/priority/status/feature reconciliation.
// Injectable I/O keeps dry-run, source failure and description-only writes testable without GitHub.
async function restoreContent(repo, args, { request = api, readSources = restorationSources } = {}) {
  const link = resolveProjectLink(repo, await request("GET", "/api/projects"));
  if (link.conflict || !link.project) throw new Error(link.conflict || `No existing project for ${repo}. Content restoration creates nothing.`);
  const tasks = await request("GET", `/api/projects/${link.project.id}/tasks`);
  const candidates = tasks.filter((t) => needsContentRestoration(t.description));
  const result = { candidates: candidates.length, restored: 0, preservedPriorText: 0 };
  if (!candidates.length) return result;
  const { issues, prByIssue } = await readSources(repo);
  const byNumber = new Map(issues.map((issue) => [issue.number, issue]));
  // Resolve every candidate before the first write, so missing/deleted source is an explicit
  // failure rather than a misleading successful repair of only the first part of the project.
  const repairs = candidates.map((task) => {
    const parts = syncDescriptionParts(task.description);
    const source = parts && /^https:\/\/github\.com\/([^/]+\/[^/]+)\/issues\/(\d+)$/.exec(parts.issueUrl);
    if (source && source[1].toLowerCase() !== repo.toLowerCase()) throw new Error(`Task ${task.id} points to a different repository; no content was changed.`);
    if (!source && !(task.description === OVERSIZED_HEADER_STUB && link.linkedBy === "marker")) {
      throw new Error(`Task ${task.id}: a title-only source needs a durable matching project marker; no content was changed.`);
    }
    const number = source ? Number(source[2]) : issueNumberFromTaskName(task.name);
    const issue = byNumber.get(number);
    if (!issue) throw new Error(`Task ${task.id}: full GitHub issue ${number ?? "identity"} is unavailable; no content was changed.`);
    const completeDescription = descriptionFor(issue, prByIssue.get(number) || null);
    const description = descriptionRefreshFor(task.description, completeDescription);
    if (description === null) throw new Error(`Task ${task.id}: source provenance does not match; no content was changed.`);
    return { task, description, preserved: description !== completeDescription };
  });
  for (const { task, description, preserved } of repairs) {
    plan(args.dryRun, "restore content", `task ${task.id}: ${task.description.length} → ${description.length} characters${preserved ? "; differing prior board text retained" : ""}`);
    if (!args.dryRun) {
      const fresh = await request("GET", `/api/tasks/${task.id}`);
      if (fresh.name !== task.name || fresh.description !== task.description
        || (task.updatedAt != null && fresh.updatedAt !== task.updatedAt)) {
        throw new Error(`Task ${task.id} changed while the repair was being prepared. Its repair was not written; re-read and retry. Earlier successful repairs are retained and logged.`);
      }
      await request("PUT", `/api/tasks/${task.id}`, { name: fresh.name, description, actor: args.actor });
      const saved = await request("GET", `/api/tasks/${task.id}`);
      if (saved.description !== description) throw new Error(`Task ${task.id}: full-content read-back failed. Retain the source and inspect the saved record before retrying.`);
    }
    result.restored++;
    if (preserved) result.preservedPriorText++;
    stats.descriptionsRefreshed++;
  }
  return result;
}

async function syncTasks(repo, projectId, issues, closedNumbers, prByIssue, featureIds, args) {
  const { actor, dryRun } = args;
  const existing = projectId ? await api("GET", `/api/projects/${projectId}/tasks`) : [];
  const byIssueNumber = new Map();
  for (const t of existing) {
    const n = issueNumberFromTaskName(t.name);
    if (n !== null) byIssueNumber.set(n, t);
  }

  for (const issue of issues) {
    const linkedPr = prByIssue.get(issue.number) || null;
    const desiredName = taskNameFor(issue);
    const desiredPriority = derivePriority(issue);
    const desiredStatus = deriveStatus(issue, linkedPr);
    const featureName = featureNameFor(issue);
    const featureId = featureIds.get(featureName);
    let task = byIssueNumber.get(issue.number);

    if (!task) {
      plan(dryRun, "create task", `${desiredName}  [${desiredStatus}/${desiredPriority}]`);
      if (dryRun) {
        if (args.comments) await syncComments(issue, linkedPr, null, args);
        continue;
      }
      if (featureId == null) throw new Error(`No feature id for "${featureName}" (issue #${issue.number})`);
      const created = await api("POST", `/api/features/${featureId}/tasks`, {
        name: desiredName,
        description: descriptionFor(issue, linkedPr),
        status: desiredStatus,
        priority: desiredPriority,
        actor,
      });
      stats.tasksCreated++;
      if (args.comments) await syncComments(issue, linkedPr, created.id, args);
      continue;
    }

    if (args.comments) await syncComments(issue, linkedPr, task.id, args);

    // The issue's milestone changed on GitHub -> the task belongs under a different Feature.
    // featureId is not settable through PUT /api/tasks/{id}; reassignment is its own verb.
    if (featureId != null && task.featureId !== featureId) {
      plan(dryRun, "move task", `${desiredName} -> feature "${featureName}"`);
      if (!dryRun) {
        await api("PATCH", `/api/tasks/${task.id}/feature`, { featureId, actor });
        stats.tasksMoved++;
      }
    }

    // Task exists. GitHub owns the title and the priority — always reconcile those.
    // Status is different: the board's owner may have moved a task by hand, and the sync
    // must not stomp that. Only advance on authoritative GitHub evidence.
    const patch = {};
    if (task.name !== desiredName) patch.name = desiredName;
    if (task.priority !== desiredPriority) patch.priority = desiredPriority;
    const refreshedDescription = descriptionRefreshFor(task.description, descriptionFor(issue, linkedPr));
    if (refreshedDescription !== null) patch.description = refreshedDescription;

    const closed = issue.state && issue.state.toUpperCase() === "CLOSED";
    if (closed && task.status !== "Done") {
      patch.status = "Done";
    } else if (!closed && linkedPr && ADVANCEABLE_TO_REVIEW.has(task.status) && task.status !== "Review") {
      patch.status = "Review";
    } else if (!closed && task.status === "Done") {
      stats.drift.push(driftDoneButOpen(repo, issue));
    }

    if (Object.keys(patch).length === 0) { stats.tasksUnchanged++; continue; }

    plan(dryRun, "update task", `${desiredName}  ${JSON.stringify(patch)}`);
    if (dryRun) {
      if (patch.description !== undefined) stats.descriptionsRefreshed++;
      continue;
    }
    // PUT /api/tasks/{id} rejects any payload without `name` — 422 "Name is required."
    // (ApiEndpoints.cs:1451). So `name` always goes on the wire, even when unchanged;
    // `patch` exists only to detect *whether* anything differs and to log what.
    await api("PUT", `/api/tasks/${task.id}`, { ...patch, name: desiredName, actor });
    stats.tasksUpdated++;
    if (patch.description !== undefined) stats.descriptionsRefreshed++;
  }

  // Board tasks whose issue was not in the set we just processed. Two very different cases —
  // conflating them is what made every closed issue look like drift.
  const processed = new Set(issues.map((i) => i.number));
  for (const [n, task] of byIssueNumber) {
    if (processed.has(n)) continue;

    if (closedNumbers.has(n)) {
      // Closed on GitHub since the last sync. This is the *only* path by which a close
      // reaches the board when --include-closed is off: the issue is simply absent from the
      // open set, so without this it would never converge to Done.
      if (task.status === "Done") { stats.tasksUnchanged++; continue; }
      plan(dryRun, "close task", `${task.name} -> Done (issue #${n} closed)`);
      if (dryRun) continue;
      await api("PATCH", `/api/tasks/${task.id}/status`, { status: "Done", actor });
      stats.tasksUpdated++;
      continue;
    }

    // Genuinely absent from GitHub — deleted or transferred. Never auto-delete (the audit
    // trail is sacred); surface it and let a human decide.
    stats.drift.push(driftOrphanTask(repo, task.name, n));
  }
}

async function syncRepo(repo, args, { header = true } = {}) {
  if (header) console.log(`\n${repo}`);
  const [openIssues, closed, prs] = await Promise.all([
    fetchOpenIssues(repo),
    fetchClosedIssues(repo, args.includeClosed),
    fetchOpenPrs(repo, args.comments),
  ]);
  const closedNumbers = new Set(closed.map((i) => i.number));
  // With --include-closed, closed issues are mirrored as tasks too. Without it they are still
  // fetched (numbers only) so an already-synced task can converge to Done when its issue closes.
  const issues = args.includeClosed ? [...openIssues, ...closed] : openIssues;

  const prByIssue = mapIssueToOpenPr(prs);

  // A PR that closes no issue has no task to hang its comments on (Dependabot PRs are the
  // usual case). Count those separately and SAY so — a comment total that silently includes
  // comments we can't mirror reads as a complete mirror when it isn't.
  const linkedPrNumbers = new Set([...prByIssue.values()].map((p) => p.number));
  const countPr = (p) => (p.comments?.length || 0) + (p.reviews?.length || 0) + (p.inlineComments?.length || 0);
  const orphanedPrComments = args.comments
    ? prs.filter((p) => !linkedPrNumbers.has(p.number)).reduce((n, p) => n + countPr(p), 0)
    : 0;
  const mirrorable = args.comments
    ? issues.reduce((n, i) => n + (i.comments?.length || 0), 0)
      + prs.filter((p) => linkedPrNumbers.has(p.number)).reduce((n, p) => n + countPr(p), 0)
    : 0;

  let line = `  ${openIssues.length} open${args.includeClosed ? ` + ${closed.length} closed` : ""} issues · ${prs.length} open PRs · ${prByIssue.size} issues with a linked PR`;
  if (args.comments) line += ` · ${mirrorable} GitHub comments`;
  console.log(line);
  if (orphanedPrComments > 0) {
    console.log(`  ! ${orphanedPrComments} comment(s) on PRs that close no issue — no task to attach them to, NOT mirrored.`);
  }

  const projectId = await ensureProject(repo, args.dryRun);
  const featureNames = [...new Set(issues.map(featureNameFor))].sort();
  const featureIds = await ensureFeatures(projectId, featureNames, args.dryRun);
  await syncTasks(repo, projectId, issues, closedNumbers, prByIssue, featureIds, args);
}

// --- onboard ------------------------------------------------------------------------------
//
// Register a repo into the portfolio: make the repo<->project link DURABLE, then backfill
// everything (milestones -> features, issues -> tasks, labels -> priority, comments).
// Idempotent in both halves — re-running links nothing and creates nothing.

async function linkProject(repo, args) {
  const projects = await api("GET", "/api/projects");
  const link = resolveProjectLink(repo, projects);
  if (link.conflict) console.warn(`  ! ${link.conflict}`);

  if (link.linkedBy === "marker") {
    console.log(`          already linked → project #${link.project.id} "${link.project.name}"`);
    return link.project.id;
  }

  if (link.linkedBy === "name") {
    // Adopting a project the old name-matching found, and making that link permanent. This is
    // the upgrade path for every project synced before the marker existed.
    plan(args.dryRun, "link project", `#${link.project.id} "${link.project.name}" ← ${repo}  (name match → durable marker)`);
    if (args.dryRun) return link.project.id;
    const current = String(link.project.description || "").trim();
    const description = current ? `${repoLinkMarker(repo)}\n${current}` : projectDescriptionFor(repo);
    // PUT /api/projects/{id} is a TRUE partial update since #203: `name` is required (422
    // without it) and any field NOT sent is preserved — so Goals and Readme are safe by
    // omission. Sending them all back would be the riskier move here, not the safer one.
    await api("PUT", `/api/projects/${link.project.id}`, { name: link.project.name, description });
    stats.projectsLinked++;
    return link.project.id;
  }

  // No project at all. Deliberately NOT created here: syncRepo's ensureProject creates it a
  // moment later WITH the marker, and doing it in both places would print the plan twice and
  // overstate a --dry-run's write set.
  return null;
}

async function onboardRepo(repo, args) {
  console.log(`\n${repo}  (onboard)`);
  await linkProject(repo, args);
  await syncRepo(repo, args, { header: false });
}

// --- push-issue (PREVIEW ONLY) -------------------------------------------------------------
//
// Computes, for every board task with no "#<n> " prefix, the exact GitHub issue it would
// become — title, milestone, labels, body — and prints it. Then stops.
//
// WHY IT STOPS. Performing the write needs two operations that cannot be made one:
// create the issue, then record its number on the board. The board's only identity for a
// mirrored issue is the "#<n> " title prefix (TaskItem has no external-id column), and that
// prefix cannot exist until the first operation has already succeeded. Everything that can
// happen in that window produces a duplicate issue, and additive-only means nothing may close
// it afterwards:
//   * a crash between the two → the retry sees an unprefixed task and creates a second issue;
//   * two concurrent push runs → both read the same unprefixed task, both create;
//   * a `sync` landing mid-window → a second board task for the issue just created;
//   * stamping the title races a human editing that same title — last writer wins, silently.
// PUT /api/tasks/{id} has no ETag/version check, so there is nothing to lose that race on.
//
// The fix is a pending-operation id persisted through REST BEFORE the GitHub call and matched
// back from the issue body on restart — real schema work, deliberately out of scope here.
// Previewing is honest and useful; a write keyed on the title prefix would not be.

function labelForPriority(labelNames, priority) {
  // Pick a label the sync's own priorityFromLabel() will read back as this priority — the
  // round trip has to be lossless or pushing a P0 silently returns as Medium.
  return labelNames.find((n) => priorityFromLabel(n) === priority) ?? null;
}

function issueBodyFor(project, task, featureName) {
  const description = stripSyncBlock(task.description);
  const lines = [
    `_From the KanbanFlow board — project **${project.name}**, feature **${featureName}**, task \`${task.id}\` (${task.status}/${task.priority})._`,
    "",
  ];
  if (description) lines.push(description, "");
  lines.push("---", "_GitHub owns this issue's content; the board task mirrors it._");
  return lines.join("\n");
}

async function pushIssues(repo, args) {
  console.log(`\n${repo}  (push-issue — PREVIEW ONLY, nothing is written to GitHub)`);
  const projects = await api("GET", "/api/projects");
  const link = resolveProjectLink(repo, projects);
  if (link.conflict) console.warn(`  ! ${link.conflict}`);
  if (!link.project) {
    throw new Error(`${repo} has no project on the board. Run: node scripts/portfolio-sync.mjs onboard ${repo}`);
  }
  const projectId = link.project.id;

  const tasks = await api("GET", `/api/projects/${projectId}/tasks`);
  let candidates = tasks.filter((t) => issueNumberFromTaskName(t.name) === null);

  if (args.task !== null) {
    const chosen = tasks.find((t) => t.id === args.task);
    if (!chosen) throw new Error(`task ${args.task} is not in project #${projectId} "${link.project.name}"`);
    const already = issueNumberFromTaskName(chosen.name);
    if (already !== null) {
      console.log(`  task ${chosen.id} is already mirrored as #${already} — nothing to preview.`);
      return;
    }
    candidates = [chosen];
  } else if (!args.all) {
    // Neither --task nor --all: list what COULD be pushed and stop.
    console.log(`  ${candidates.length} task(s) have no "#<n> " prefix and would become issues:`);
    for (const t of candidates.slice(0, 50)) console.log(`    ${String(t.id).padStart(5)}  [${t.status}/${t.priority}]  ${t.name}`);
    if (candidates.length > 50) console.log(`    … and ${candidates.length - 50} more`);
    console.log(`\n  Choose: --task <id> for one, --all for every one.`);
    return;
  }

  if (args.limit > 0 && candidates.length > args.limit) {
    console.log(`  ${candidates.length} candidates, capped at --limit ${args.limit}.`);
    candidates = candidates.slice(0, args.limit);
  }
  if (candidates.length === 0) { console.log(`  no unsynced tasks — nothing to preview.`); return; }

  const [milestones, labels] = await Promise.all([fetchMilestones(repo), fetchLabels(repo)]);
  const milestoneTitles = new Set(milestones.map((m) => m.title));
  const labelNames = labels.map((l) => l.name);

  for (const task of candidates) {
    const featureName = task.featureName || UNSORTED_FEATURE;
    // A milestone is only ever ATTACHED, never created — so a feature with no matching
    // milestone would land unfiled, and the preview says so instead of implying otherwise.
    const milestone = featureName !== UNSORTED_FEATURE && milestoneTitles.has(featureName) ? featureName : null;
    const label = labelForPriority(labelNames, task.priority);
    const title = String(task.name).trim();
    const body = issueBodyFor(link.project, task, featureName);

    console.log(`  would create issue on ${repo} ← task ${task.id}`);
    console.log(`      title:     ${title}`);
    console.log(`      milestone: ${milestone ?? "(none)"}${featureName !== UNSORTED_FEATURE && !milestone ? `   ← feature "${featureName}" has no milestone on ${repo}; one would NOT be created` : ""}`);
    console.log(`      labels:    ${label ?? "(none)"}`);
    console.log(`      body:`);
    for (const bodyLine of body.split("\n")) console.log(`      │ ${bodyLine}`);
    if (task.priority !== "Medium" && !label) {
      console.warn(`      ! no label on ${repo} maps to priority ${task.priority} — a sync would read this issue back as Medium.`);
    }
    stats.issuesPreviewed++;
  }

  console.log(`\n  Nothing was written. Performing these creates safely needs a durable board-side`);
  console.log(`  issue link (see the "Why push-issue does not write" section of docs/GITHUB-SYNC.md).`);
}

// --- status -------------------------------------------------------------------------------
//
// Health check over both sides. Reports three distinct things that are easy to conflate:
//   pending  — a disagreement `sync` WILL converge on its next run. Not a problem.
//   unsynced — board tasks with no GitHub issue. `push-issue` candidates. Not a problem.
//   drift    — a disagreement `sync` deliberately will NOT fix. Exits non-zero.
// Read-only everywhere: it writes to neither GitHub nor the board.

// KanbanFlow stores UTC but serialises DateTime with Kind=Unspecified, so its timestamps come
// back WITHOUT a zone designator ("2026-08-02T22:08:43.594"). `new Date()` reads those as LOCAL
// time, which reported a sync that had just run as "-239m ago" — exactly one UTC offset out.
function asUtcDate(iso) {
  const s = String(iso || "");
  return new Date(/(?:Z|[+-]\d{2}:?\d{2})$/.test(s) ? s : `${s}Z`);
}

function agoText(iso) {
  const ms = Date.now() - asUtcDate(iso).getTime();
  if (!Number.isFinite(ms)) return "?";
  const mins = Math.floor(ms / 60_000);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 48) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

async function lastSyncAt(projectId, actor) {
  try {
    const events = await api("GET", `/api/projects/${projectId}/activity?limit=500`);
    const hit = (events || []).find((e) => e.actor === actor);
    return hit ? { at: hit.createdAt, eventType: hit.eventType } : null;
  } catch (err) {
    console.warn(`  ! could not read the activity trail for project ${projectId}: ${err.message}`);
    return null;
  }
}

async function statusRepo(repo, args, projects) {
  const rec = {
    repo, link: "none", projectId: null, projectName: null,
    github: {}, board: { tasks: 0, mirrored: 0, unsynced: 0 },
    pending: [], drift: [], unsynced: [], lastSync: null,
  };

  const link = resolveProjectLink(repo, projects);
  rec.link = link.linkedBy ?? "none";
  // A forked or ambiguous link is drift, not a warning: sync will not repair it and the
  // mirror looks healthy while it silently splits.
  if (link.conflict) rec.drift.push(`${repo}: ${link.conflict}`);
  if (link.project) { rec.projectId = link.project.id; rec.projectName = link.project.name; }

  const [openIssues, closedIssues, prs, milestones] = await Promise.all([
    fetchOpenIssues(repo),
    fetchClosedIssues(repo, false), // numbers only — enough to tell "closed" from "gone"
    fetchOpenPrs(repo, false),
    fetchMilestones(repo),
  ]);
  const closedNumbers = new Set(closedIssues.map((i) => i.number));
  const openByNumber = new Map(openIssues.map((i) => [i.number, i]));
  const prByIssue = mapIssueToOpenPr(prs);
  rec.github = {
    openIssues: openIssues.length, closedIssues: closedIssues.length,
    openPrs: prs.length, linkedPrs: prByIssue.size, milestones: milestones.length,
  };

  if (!link.project) {
    rec.pending.push(`${openIssues.length} issue(s) not on the board — no project yet (run \`onboard ${repo}\`)`);
    return rec;
  }

  const tasks = await api("GET", `/api/projects/${link.project.id}/tasks`);
  const byIssueNumber = new Map();
  for (const t of tasks) {
    const n = issueNumberFromTaskName(t.name);
    if (n === null) rec.unsynced.push({ id: t.id, name: t.name, status: t.status, priority: t.priority });
    else byIssueNumber.set(n, t);
  }
  rec.board = { tasks: tasks.length, mirrored: byIssueNumber.size, unsynced: rec.unsynced.length };

  let closeLag = 0, titleLag = 0, priorityLag = 0, featureLag = 0, reviewLag = 0;
  for (const [n, task] of byIssueNumber) {
    const issue = openByNumber.get(n);
    if (!issue) {
      // Absent from the open set: either closed (sync converges it to Done) or genuinely gone.
      if (closedNumbers.has(n)) { if (task.status !== "Done") closeLag++; }
      else rec.drift.push(driftOrphanTask(repo, task.name, n));
      continue;
    }
    // Same two conditions syncTasks uses, and the same message text (shared builders above),
    // so `status` can never report drift that `sync` would not.
    if (task.status === "Done") rec.drift.push(driftDoneButOpen(repo, issue));
    if (task.name !== taskNameFor(issue)) titleLag++;
    if (task.priority !== derivePriority(issue)) priorityLag++;
    if ((task.featureName || "") !== featureNameFor(issue)) featureLag++;
    if (prByIssue.get(n) && ADVANCEABLE_TO_REVIEW.has(task.status) && task.status !== "Review") reviewLag++;
  }

  const missing = openIssues.filter((i) => !byIssueNumber.has(i.number)).length;
  if (missing) rec.pending.push(`${missing} GitHub issue(s) not on the board`);
  if (closeLag) rec.pending.push(`${closeLag} task(s) whose issue is closed but which are not Done`);
  if (titleLag) rec.pending.push(`${titleLag} task title(s) behind the issue title`);
  if (priorityLag) rec.pending.push(`${priorityLag} task priority/-ies behind the issue labels`);
  if (featureLag) rec.pending.push(`${featureLag} task(s) whose milestone moved`);
  if (reviewLag) rec.pending.push(`${reviewLag} task(s) with an open linked PR not yet in Review`);

  rec.lastSync = await lastSyncAt(link.project.id, args.actor);
  return rec;
}

function printStatusRepo(rec) {
  const g = rec.github;
  console.log(`\n${rec.repo}`);
  const linkText = rec.projectId === null
    ? `UNLINKED — no project on the board`
    : `project #${rec.projectId} "${rec.projectName}"  (${rec.link === "marker" ? "durable marker" : "name match only — run `onboard` to make it durable"})`;
  console.log(`  link       ${linkText}`);
  console.log(`  github     ${g.openIssues} open · ${g.closedIssues} closed issues · ${g.openPrs} open PRs (${g.linkedPrs} linked) · ${g.milestones} milestones`);
  console.log(`  board      ${rec.board.tasks} tasks · ${rec.board.mirrored} mirrored · ${rec.board.unsynced} unsynced`);
  // Normalised to an unambiguous ISO-with-Z rather than echoing the naive value back.
  console.log(`  last sync  ${rec.lastSync ? `${asUtcDate(rec.lastSync.at).toISOString()}  (${agoText(rec.lastSync.at)}, ${rec.lastSync.eventType})` : "never — no activity by this actor"}`);
  console.log(`  pending    ${rec.pending.length}${rec.pending.length ? "  (a `sync` run converges these)" : ""}`);
  for (const p of rec.pending) console.log(`             · ${p}`);
  console.log(`  unsynced   ${rec.unsynced.length}${rec.unsynced.length ? "  (`push-issue` candidates)" : ""}`);
  for (const t of rec.unsynced.slice(0, 10)) console.log(`             · task ${t.id} [${t.status}/${t.priority}] ${t.name}`);
  if (rec.unsynced.length > 10) console.log(`             · … and ${rec.unsynced.length - 10} more`);
  console.log(`  drift      ${rec.drift.length}${rec.drift.length ? "  (`sync` will NOT fix these)" : ""}`);
  for (const d of rec.drift) console.log(`             ! ${d}`);
}

async function statusAll(repos, args) {
  const projects = await api("GET", "/api/projects");
  const records = [];
  const errors = [];
  for (const repo of repos) {
    try {
      records.push(await statusRepo(repo, args, projects));
    } catch (err) {
      const note = `${repo}: ${err.message}`;
      console.error(`  ERROR reading ${note}`);
      errors.push(note);
    }
  }

  const total = (key) => records.reduce((n, r) => n + r[key].length, 0);
  const drift = total("drift"), pending = total("pending"), unsynced = total("unsynced");
  const notDurable = records.filter((r) => r.link !== "marker").length;
  // "I could not see everything" is a different answer from "I looked and it is clean", and
  // conflating them is how a partial read reports a green board.
  const blind = stats.incomplete.length > 0 || errors.length > 0;
  const strictFail = args.strict && (pending > 0 || unsynced > 0 || notDurable > 0);
  const exit = blind ? 2 : (drift > 0 || strictFail) ? 1 : 0;

  if (args.json) {
    console.log(JSON.stringify({
      apiBaseUrl, exit, complete: !blind,
      incomplete: stats.incomplete, errors,
      repos: records, totals: { drift, pending, unsynced, notDurable },
    }, null, 2));
  } else {
    for (const rec of records) printStatusRepo(rec);
    console.log(`\n── summary ──`);
    console.log(`repos:     ${records.length}${errors.length ? `  (${errors.length} could not be read)` : ""}`);
    console.log(`drift:     ${drift}${drift ? "   ← FAIL" : ""}`);
    console.log(`pending:   ${pending}`);
    console.log(`unsynced:  ${unsynced}`);
    console.log(`links not durable: ${notDurable}${notDurable ? "  (run `onboard <repo>`)" : ""}`);
    if (blind) {
      console.log(`\n── INCOMPLETE — this report cannot be trusted ──`);
      for (const n of stats.incomplete) console.log(`  ! ${n}`);
      for (const n of errors) console.log(`  ! could not read ${n}`);
    }
    console.log(`\nexit ${exit}  (0 = clean and complete · 1 = confirmed drift · 2 = incomplete or error)`);
  }

  // The gate an agent scripts against: `portfolio-sync.mjs status && <do work>`.
  // --strict widens code 1 from "confirmed drift" to "anything outstanding at all".
  process.exitCode = exit;
}

// --allow-write / --push are recognised so that asking for the write gets a real explanation
// rather than "unknown option". They are refused, not honoured.
function refusesWrite(args) {
  if (!args.allowWrite) return false;
  console.error(`
push-issue is PREVIEW ONLY — --allow-write / --push is refused, by design.

Creating the issue and recording its number on the board are two operations, and the board's
only identity for a mirrored issue is the "#<n> " title prefix, which cannot exist until the
first one has already succeeded. A crash, a second push run, or a \`sync\` landing in that
window creates a duplicate issue — and additive-only means nothing here may close it.

Enabling the write needs a pending-operation id persisted through REST BEFORE the GitHub call.
That is schema work and is tracked separately. Until then:

  node scripts/portfolio-sync.mjs push-issue ${args.repos?.[0] ?? "owner/repo"} --all
      prints the exact issue each unsynced task would become, and writes nothing.

See "Why push-issue does not write" in docs/GITHUB-SYNC.md.`);
  process.exitCode = 2;
  return true;
}

const HELP = `portfolio-sync — GitHub <-> KanbanFlow. GitHub is the source of truth; the board is
the visualisation. Every command is idempotent and honours --dry-run.

  sync                        GitHub -> board mirror. Read-only on GitHub. THE DEFAULT.
  onboard <owner/repo>…       register a repo: link the Project durably, then backfill it.
  restore-content <repo>      restore incomplete imported descriptions on existing cards only.
  push-issue <owner/repo>     PREVIEW ONLY: print the exact issue each unsynced board task
                              would become. Refuses to write — see docs/GITHUB-SYNC.md.
  status                      health check both sides.
                              exit 0 = clean and complete · 1 = drift · 2 = incomplete/error

common
  --dry-run              print exactly what WOULD be written, write nothing
  --repos a/b,c/d        override the repo list (bare "owner/repo" args work too)
  --actor <name>         actor recorded on the audit trail (default: portfolio-sync)

sync / onboard
  --include-closed       also mirror closed issues (as Done)
  --no-comments          skip GitHub comment mirroring (on by default)

push-issue
  --task <id>            preview exactly this board task
  --all                  preview every task with no "#<n> " prefix
  --limit N              cap how many --all previews in one run

status
  --strict               also exit 1 on pending work, unsynced tasks, or a non-durable link
  --json                 machine-readable report (includes the exit code and completeness)

WRITE SURFACE ON GITHUB: NONE. This script only ever reads GitHub. It cannot create, close,
delete, reword, re-label or reassign an issue. All writes go to the KanbanFlow REST API.

Comments: issue comments, PR conversation, PR reviews (APPROVED -> Verification,
CHANGES_REQUESTED -> Blocked), and inline file:line review comments are all mirrored onto
the task, deduped by GitHub node id. Bot reviews are mirrored whole and can be very large.

Full contract, mapping table and onboarding runbook: docs/GITHUB-SYNC.md
env: KANBANFLOW_API_URL (default http://127.0.0.1:5123), KANBANFLOW_PORTFOLIO_REPOS`;

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) { console.log(HELP); return; }
  if (args.error) {
    console.error(`${args.error}\n\n${HELP}`);
    process.exitCode = 2;
    return;
  }
  if (args.command === "push-issue" && refusesWrite(args)) return;
  if (process.env.KANBANFLOW_BODY_EXCERPT_LIMIT !== undefined) {
    console.warn("KANBANFLOW_BODY_EXCERPT_LIMIT is obsolete and ignored. Complete issue and comment text is always preserved.");
  }
  // `sync` and `status` sweep the whole portfolio by design. onboard/push-issue act on ONE
  // repo at a time and must never fall back to the default six — silently onboarding or
  // pushing to six repos because a repo argument was forgotten is not a recoverable mistake.
  if ((args.command === "onboard" || args.command === "push-issue" || args.command === "restore-content") && !args.repos?.length) {
    console.error(`\`${args.command}\` needs an explicit repo:\n  node scripts/portfolio-sync.mjs ${args.command} owner/repo`);
    process.exitCode = 2;
    return;
  }

  const repos = resolveRepos(args);
  console.log(`portfolio-sync ${args.command} → ${apiBaseUrl}${args.dryRun ? "  (DRY RUN — no writes)" : ""}`);
  console.log(`${repos.length} repos: ${repos.join(", ")}`);

  try {
    await api("GET", "/api/projects");
  } catch (err) {
    console.error(`\nCannot reach KanbanFlow at ${apiBaseUrl} — is it running?\n  ${err.message}`);
    process.exitCode = 1;
    return;
  }

  if (args.command === "status") {
    if (args.dryRun) console.log(`(status is read-only — --dry-run has nothing to suppress)`);
    await statusAll(repos, args);
    return;
  }

  const runners = { sync: syncRepo, onboard: onboardRepo, "restore-content": restoreContent, "push-issue": pushIssues };
  const run = runners[args.command];
  for (const repo of repos) {
    try {
      await run(repo, args);
    } catch (err) {
      console.error(`  ERROR on ${repo}: ${err.message}`);
      process.exitCode = 1;
    }
  }

  console.log(`\n── summary ──`);
  if (args.command === "push-issue") {
    console.log(`issues previewed: ${stats.issuesPreviewed}`);
    console.log(`issues created:   0   (push-issue never writes to GitHub)`);
    return;
  }

  // Sync's summary, unchanged. The lifecycle counters print only when they are non-zero, so a
  // plain `sync` run's output is byte-identical to what it has always been.
  if (stats.projectsLinked) console.log(`projects linked:  ${stats.projectsLinked}`);
  console.log(`projects created: ${stats.projectsCreated}`);
  console.log(`features created: ${stats.featuresCreated}`);
  console.log(`tasks created:    ${stats.tasksCreated}`);
  console.log(`tasks updated:    ${stats.tasksUpdated}`);
  console.log(`descriptions refreshed: ${stats.descriptionsRefreshed}`);
  console.log(`tasks moved:      ${stats.tasksMoved}`);
  console.log(`tasks unchanged:  ${stats.tasksUnchanged}`);
  if (args.comments) {
    console.log(`comments mirrored:${String(stats.commentsCreated).padStart(4)}`);
    console.log(`comments existing:${String(stats.commentsUnchanged).padStart(4)}`);
    if (stats.commentFetchFailures > 0) {
      console.log(`\n  ! ${stats.commentFetchFailures} PR(s) whose inline review comments could NOT be fetched — the board is INCOMPLETE for those. See the warnings above.`);
      process.exitCode = 1;
    }
    if (stats.biggestComment.chars > 20_000) {
      const b = stats.biggestComment;
      console.log(`\n  ! biggest comment: ${b.chars.toLocaleString()} chars from ${b.author} on #${b.issue}.`);
      console.log("    Full content preserved. Clients can page or collapse their views without changing the saved text.");
    }
  }
  if (stats.drift.length) {
    console.log(`\n── drift (${stats.drift.length}) — board and GitHub disagree ──`);
    for (const d of stats.drift) console.log(`  ! ${d}`);
  }
  // A mirror built from a read that stopped early is not a mirror. Same rule as the
  // comment-fetch failures above: say so, and fail.
  if (stats.incomplete.length) {
    console.log(`\n── INCOMPLETE (${stats.incomplete.length}) — the mirror is missing data ──`);
    for (const n of stats.incomplete) console.log(`  ! ${n}`);
    process.exitCode = 1;
  }
}

function resolvedEntry(p) {
  try { return realpathSync(resolve(p)); } catch { return resolve(p); }
}
const selfPath = fileURLToPath(import.meta.url);
const invokedDirectly = Boolean(process.argv[1])
  && (resolve(process.argv[1]) === selfPath || resolvedEntry(process.argv[1]) === selfPath);

if (invokedDirectly) {
  main().catch((err) => {
    console.error(err);
    process.exit(1);
  });
}

export {
  commentTextFor,
  completeCommentIds,
  descriptionRefreshFor,
  descriptionFor,
  needsContentRestoration,
  restoreContent,
  parseArgs,
  syncComments,
};
