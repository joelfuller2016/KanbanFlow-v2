#!/usr/bin/env node
// Portfolio sync — reconcile GitHub (source of truth) into the KanbanFlow board.
//
// One-way: GitHub -> KanbanFlow. This script NEVER writes to GitHub, and never touches
// SQLite/EF directly — every mutation goes through the REST API so the ActivityEvent
// audit trail stays complete.
//
// Idempotent. Re-running converges: repo -> Project (matched by name), milestone ->
// Feature (matched by name), issue -> TaskItem (matched by the "#<n> " title prefix).
// A second consecutive run creates nothing.
//
//   node scripts/portfolio-sync.mjs --dry-run
//   node scripts/portfolio-sync.mjs --repos joelfuller2016/ZeroRouter
//   KANBANFLOW_PORTFOLIO_REPOS="o/a,o/b" node scripts/portfolio-sync.mjs

import { execFile } from "node:child_process";
import { promisify } from "node:util";

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
const BODY_EXCERPT_LIMIT = 1500;
const GH_PAGE_LIMIT = 500;

// Statuses the sync is allowed to advance a task *out of* when GitHub shows a linked open
// PR. A task a human has already parked in Review/Done is left alone (reported as drift
// instead) — the sync must never fight the board's owner.
const ADVANCEABLE_TO_REVIEW = new Set(["Backlog", "Todo", "InProgress"]);

function parseArgs(argv) {
  const args = {
    dryRun: false, includeClosed: false, repos: null, actor: "portfolio-sync",
    comments: true, maxCommentChars: 0, // 0 = no cap; mirror the comment whole
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--dry-run") args.dryRun = true;
    else if (a === "--include-closed") args.includeClosed = true;
    else if (a === "--no-comments") args.comments = false;
    else if (a === "--max-comment-chars") args.maxCommentChars = Number.parseInt(argv[++i], 10) || 0;
    else if (a === "--repos") args.repos = (argv[++i] || "").split(",").map((r) => r.trim()).filter(Boolean);
    else if (a === "--actor") args.actor = argv[++i] || args.actor;
    else if (a === "--help" || a === "-h") args.help = true;
  }
  return args;
}

function resolveRepos(args) {
  if (args.repos?.length) return args.repos;
  const env = process.env.KANBANFLOW_PORTFOLIO_REPOS;
  if (env) return env.split(",").map((r) => r.trim()).filter(Boolean);
  return DEFAULT_REPOS;
}

// --- GitHub (read-only) -----------------------------------------------------------------

async function gh(args) {
  const { stdout } = await execFileAsync("gh", args, { maxBuffer: 64 * 1024 * 1024 });
  return JSON.parse(stdout || "[]");
}

// `comments` rides along on the list call we already make, so issue comments cost zero extra
// requests. Each carries a stable node id (e.g. `IC_kwDO…`) — that id is the dedupe key that
// makes comment mirroring idempotent.
const ISSUE_FIELDS = "number,title,body,labels,milestone,assignees,state,updatedAt,url,comments";

// Never truncate silently: a board that quietly drops issues past the limit still *looks*
// like a full mirror, which is the one thing this tool must not do.
function warnIfTruncated(repo, kind, list) {
  if (list.length >= GH_PAGE_LIMIT) {
    console.warn(`  ! ${repo}: ${kind} hit the ${GH_PAGE_LIMIT} fetch limit — the mirror may be INCOMPLETE. Raise GH_PAGE_LIMIT.`);
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

function commentTextFor(c, maxChars) {
  let body = c.body;
  if (maxChars > 0 && body.length > maxChars) {
    body = `${body.slice(0, maxChars)}\n\n…(truncated at ${maxChars} chars — read it on GitHub)`;
  }
  return `<!-- gh:${c.ghId} -->\n${c.header}\n\n${body}`;
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

const PRIORITY_BY_LABEL = { p0: "Critical", p1: "High", p2: "Medium", p3: "Low" };

function derivePriority(issue) {
  for (const label of issue.labels || []) {
    const hit = PRIORITY_BY_LABEL[String(label.name).trim().toLowerCase()];
    if (hit) return hit;
  }
  return "Medium";
}

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

function descriptionFor(issue, linkedPr) {
  const labels = (issue.labels || []).map((l) => l.name).join(", ") || "none";
  const milestone = issue.milestone?.title || "none";
  const lines = [
    "<!-- kanbanflow-sync -->",
    `**Issue:** ${issue.url}`,
    `**State:** ${issue.state || "OPEN"} · **Milestone:** ${milestone} · **Labels:** ${labels}`,
    linkedPr ? `**Open PR:** ${linkedPr.url}${linkedPr.isDraft ? " (draft)" : ""}` : "**Open PR:** none",
    `**Synced:** ${new Date().toISOString()}`,
    "<!-- /kanbanflow-sync -->",
    "",
  ];
  const body = String(issue.body || "").trim();
  if (body) {
    lines.push(body.length > BODY_EXCERPT_LIMIT ? `${body.slice(0, BODY_EXCERPT_LIMIT)}\n\n…(truncated — see the issue)` : body);
  }
  return lines.join("\n");
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
  tasksUpdated: 0, tasksMoved: 0, tasksUnchanged: 0,
  commentsCreated: 0, commentsUnchanged: 0, commentFetchFailures: 0,
  biggestComment: { chars: 0, author: null, issue: null },
  drift: [],
};

function plan(dryRun, verb, what) {
  console.log(`  ${dryRun ? "[dry-run] would" : "        "} ${verb} ${what}`);
}

async function ensureProject(repo, dryRun) {
  const shortName = repo.split("/").pop();
  const projects = await api("GET", "/api/projects");
  // Case-insensitive: "ZeroRouter" and "zerorouter" are the same repo, and creating a second
  // project for the same repo would fork the mirror permanently.
  const existing = projects.find((p) => p.name.toLowerCase() === shortName.toLowerCase());
  if (existing) return existing.id;

  plan(dryRun, "create project", shortName);
  if (dryRun) return null;
  const created = await api("POST", "/api/projects", {
    name: shortName,
    description: `Synced from https://github.com/${repo} — GitHub issues are the source of truth.`,
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
async function syncComments(issue, linkedPr, taskId, args) {
  const incoming = collectComments(issue, linkedPr);
  if (incoming.length === 0) return;

  // Dedupe against what is already on the task. This read is gated on whether the task EXISTS
  // (taskId != null) — deliberately NOT on dry-run. Gating it on dryRun was a real bug: a
  // --dry-run against an already-synced board skipped the read, so `seen` was always empty and
  // every previously-mirrored comment was reported as "would add". A preview that overstates its
  // own writes is worse than no preview — it is exactly the lie --dry-run exists to prevent.
  // Reads are safe under --dry-run; only writes are suppressed.
  const existing = taskId == null ? [] : await api("GET", `/api/tasks/${taskId}/comments`);
  const seen = new Set((existing || []).map((c) => ghIdOf(c.text)).filter(Boolean));

  for (const c of incoming) {
    if (seen.has(c.ghId)) { stats.commentsUnchanged++; continue; }
    const text = commentTextFor(c, args.maxCommentChars);
    plan(args.dryRun, "add comment", `#${issue.number} ← ${c.author} [${c.type}] ${c.body.length.toLocaleString()} chars`);
    if (args.dryRun) { stats.commentsCreated++; continue; }
    if (taskId == null) {
      throw new Error(`syncComments: taskId is null outside --dry-run (issue #${issue.number}) — refusing to POST to /api/tasks/null/comments`);
    }
    await api("POST", `/api/tasks/${taskId}/comments`, { text, author: c.author, type: c.type });
    stats.commentsCreated++;
    if (c.body.length > stats.biggestComment.chars) {
      stats.biggestComment = { chars: c.body.length, author: c.author, issue: issue.number };
    }
  }
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

    const closed = issue.state && issue.state.toUpperCase() === "CLOSED";
    if (closed && task.status !== "Done") {
      patch.status = "Done";
    } else if (!closed && linkedPr && ADVANCEABLE_TO_REVIEW.has(task.status) && task.status !== "Review") {
      patch.status = "Review";
    } else if (!closed && task.status === "Done") {
      stats.drift.push(`${repo}#${issue.number}: board says Done, issue is still open — ${issue.url}`);
    }

    if (Object.keys(patch).length === 0) { stats.tasksUnchanged++; continue; }

    plan(dryRun, "update task", `${desiredName}  ${JSON.stringify(patch)}`);
    if (dryRun) continue;
    // PUT /api/tasks/{id} rejects any payload without `name` — 422 "Name is required."
    // (ApiEndpoints.cs:1451). So `name` always goes on the wire, even when unchanged;
    // `patch` exists only to detect *whether* anything differs and to log what.
    await api("PUT", `/api/tasks/${task.id}`, { ...patch, name: desiredName, actor });
    stats.tasksUpdated++;
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
    stats.drift.push(`${repo}: task "${task.name}" has no matching issue #${n} on GitHub (deleted or transferred?)`);
  }
}

async function syncRepo(repo, args) {
  console.log(`\n${repo}`);
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

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(`portfolio-sync — reconcile GitHub into the KanbanFlow board (one-way, idempotent).

  --dry-run              print the write plan, mutate nothing
  --repos a/b,c/d        override the repo list
  --include-closed       also mirror closed issues (as Done)
  --no-comments          skip GitHub comment mirroring (on by default)
  --max-comment-chars N  truncate each mirrored comment at N chars (default: no cap)
  --actor <name>         actor recorded on the audit trail (default: portfolio-sync)

Comments: issue comments, PR conversation, PR reviews (APPROVED -> Verification,
CHANGES_REQUESTED -> Blocked), and inline file:line review comments are all mirrored onto
the task, deduped by GitHub node id. Bot reviews are mirrored whole and can be very large.

env: KANBANFLOW_API_URL (default http://127.0.0.1:5123), KANBANFLOW_PORTFOLIO_REPOS`);
    return;
  }

  const repos = resolveRepos(args);
  console.log(`portfolio-sync → ${apiBaseUrl}${args.dryRun ? "  (DRY RUN — no writes)" : ""}`);
  console.log(`${repos.length} repos: ${repos.join(", ")}`);

  try {
    await api("GET", "/api/projects");
  } catch (err) {
    console.error(`\nCannot reach KanbanFlow at ${apiBaseUrl} — is it running?\n  ${err.message}`);
    process.exitCode = 1;
    return;
  }

  for (const repo of repos) {
    try {
      await syncRepo(repo, args);
    } catch (err) {
      console.error(`  ERROR syncing ${repo}: ${err.message}`);
      process.exitCode = 1;
    }
  }

  console.log(`\n── summary ──`);
  console.log(`projects created: ${stats.projectsCreated}`);
  console.log(`features created: ${stats.featuresCreated}`);
  console.log(`tasks created:    ${stats.tasksCreated}`);
  console.log(`tasks updated:    ${stats.tasksUpdated}`);
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
      console.log(`    Mirrored whole. Use --max-comment-chars N to cap it (agents reading this task over MCP pay for it).`);
    }
  }
  if (stats.drift.length) {
    console.log(`\n── drift (${stats.drift.length}) — board and GitHub disagree ──`);
    for (const d of stats.drift) console.log(`  ! ${d}`);
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
