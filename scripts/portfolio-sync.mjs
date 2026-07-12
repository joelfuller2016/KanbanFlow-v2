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
  const args = { dryRun: false, includeClosed: false, repos: null, actor: "portfolio-sync" };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--dry-run") args.dryRun = true;
    else if (a === "--include-closed") args.includeClosed = true;
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

async function fetchIssues(repo, includeClosed) {
  // --search "-is:pr" is redundant for `gh issue list` but the state filter is not: by
  // default gh returns only open issues.
  return gh([
    "issue", "list", "-R", repo,
    "--state", includeClosed ? "all" : "open",
    "--limit", String(GH_PAGE_LIMIT),
    "--json", "number,title,body,labels,milestone,assignees,state,updatedAt,url",
  ]);
}

async function fetchOpenPrs(repo) {
  return gh([
    "pr", "list", "-R", repo,
    "--state", "open",
    "--limit", String(GH_PAGE_LIMIT),
    "--json", "number,title,body,url,updatedAt,reviewDecision,isDraft",
  ]);
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
  tasksUpdated: 0, tasksUnchanged: 0, drift: [],
};

function plan(dryRun, verb, what) {
  console.log(`  ${dryRun ? "[dry-run] would" : "        "} ${verb} ${what}`);
}

async function ensureProject(repo, dryRun) {
  const shortName = repo.split("/").pop();
  const projects = await api("GET", "/api/projects");
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

async function syncTasks(repo, projectId, issues, prByIssue, featureIds, actor, dryRun) {
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
    const featureId = featureIds.get(featureNameFor(issue));
    const task = byIssueNumber.get(issue.number);

    if (!task) {
      plan(dryRun, "create task", `${desiredName}  [${desiredStatus}/${desiredPriority}]`);
      if (dryRun) continue;
      if (featureId == null) throw new Error(`No feature id for "${featureNameFor(issue)}" (issue #${issue.number})`);
      await api("POST", `/api/features/${featureId}/tasks`, {
        name: desiredName,
        description: descriptionFor(issue, linkedPr),
        status: desiredStatus,
        priority: desiredPriority,
        actor,
      });
      stats.tasksCreated++;
      continue;
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
    await api("PUT", `/api/tasks/${task.id}`, { name: desiredName, ...patch, actor });
    stats.tasksUpdated++;
  }

  // A task on the board whose issue is gone from GitHub. Never auto-delete (the audit
  // trail is sacred) — surface it and let a human decide.
  const seen = new Set(issues.map((i) => i.number));
  for (const [n, t] of byIssueNumber) {
    if (!seen.has(n) && t.status !== "Done") stats.drift.push(`${repo}: task "${t.name}" has no matching open issue #${n} (closed or deleted?)`);
  }
}

async function syncRepo(repo, args) {
  console.log(`\n${repo}`);
  const [issues, prs] = await Promise.all([fetchIssues(repo, args.includeClosed), fetchOpenPrs(repo)]);
  const prByIssue = mapIssueToOpenPr(prs);
  console.log(`  ${issues.length} issues · ${prs.length} open PRs · ${prByIssue.size} issues with a linked PR`);
  if (issues.length >= GH_PAGE_LIMIT) console.warn(`  ⚠ issues may be truncated (fetched ${issues.length} = limit); consider raising GH_PAGE_LIMIT`);
  if (prs.length >= GH_PAGE_LIMIT) console.warn(`  ⚠ PRs may be truncated (fetched ${prs.length} = limit); consider raising GH_PAGE_LIMIT`);

  const projectId = await ensureProject(repo, args.dryRun);
  const featureNames = [...new Set(issues.map(featureNameFor))].sort();
  const featureIds = await ensureFeatures(projectId, featureNames, args.dryRun);
  await syncTasks(repo, projectId, issues, prByIssue, featureIds, args.actor, args.dryRun);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(`portfolio-sync — reconcile GitHub into the KanbanFlow board (one-way, idempotent).

  --dry-run           print the write plan, mutate nothing
  --repos a/b,c/d     override the repo list
  --include-closed    also mirror closed issues (as Done)
  --actor <name>      actor recorded on the audit trail (default: portfolio-sync)

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
  console.log(`tasks unchanged:  ${stats.tasksUnchanged}`);
  if (stats.drift.length) {
    console.log(`\n── drift (${stats.drift.length}) — board and GitHub disagree ──`);
    for (const d of stats.drift) console.log(`  ! ${d}`);
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
