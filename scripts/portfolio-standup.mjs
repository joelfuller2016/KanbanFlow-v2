#!/usr/bin/env node
// Portfolio standup — the short read over a big board.
//
// The board may hold hundreds of tasks; this report stays ~25 lines. It rolls each project
// up to one row, asks KanbanFlow's own direction engine (`/next-tasks`) what to do next,
// then overlays live GitHub state to surface what's in flight, what's gone stale, and where
// the board and GitHub disagree.
//
// Read-only. Writes nothing, anywhere.
//
//   node scripts/portfolio-standup.mjs
//   node scripts/portfolio-standup.mjs --repos joelfuller2016/ZeroRouter

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

const PR_REVIEW_STALE_DAYS = 3;   // open PR sitting without a review
const TASK_PROGRESS_STALE_DAYS = 14; // task parked InProgress, untouched

function parseArgs(argv) {
  const args = { repos: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--repos") args.repos = (argv[++i] || "").split(",").map((r) => r.trim()).filter(Boolean);
    else if (argv[i] === "--help" || argv[i] === "-h") args.help = true;
  }
  return args;
}

function resolveRepos(args) {
  if (args.repos?.length) return args.repos;
  const env = process.env.KANBANFLOW_PORTFOLIO_REPOS;
  if (env) return env.split(",").map((r) => r.trim()).filter(Boolean);
  return DEFAULT_REPOS;
}

async function api(path) {
  const res = await fetch(`${apiBaseUrl}${path}`, { signal: AbortSignal.timeout(restTimeoutMs) });
  if (!res.ok) throw new Error(`GET ${path} -> ${res.status} ${res.statusText}`);
  return res.json();
}

async function gh(args) {
  const { stdout } = await execFileAsync("gh", args, { maxBuffer: 32 * 1024 * 1024 });
  return JSON.parse(stdout || "[]");
}

const daysSince = (iso) => Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000);
const pad = (s, n) => String(s).padEnd(n).slice(0, n);
const lpad = (s, n) => String(s).padStart(n);

// The next-tasks surfaces disagree on response shape (#231): REST wraps its ranked list in
// `{ projectId, count, recommendations: [...] }`, the native MCP tool returns a flat array,
// and the Node wrapper uses a `{ tasks, blockedTasks }` envelope. Accept all three so the
// standup keeps working whichever one it is pointed at.
function topRecommendation(next) {
  if (!next) return null;
  if (Array.isArray(next)) return next[0] ?? null;
  return next.recommendations?.[0] ?? next.tasks?.[0] ?? null;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(`portfolio-standup — cross-project status over the KanbanFlow board (read-only).

  --repos a/b,c/d     override the repo list

env: KANBANFLOW_API_URL (default http://127.0.0.1:5123), KANBANFLOW_PORTFOLIO_REPOS`);
    return;
  }

  const repos = resolveRepos(args);
  const shortNames = new Map(repos.map((r) => [r.split("/").pop().toLowerCase(), r]));

  let projects;
  try {
    projects = await api("/api/projects");
  } catch (err) {
    console.error(`Cannot reach KanbanFlow at ${apiBaseUrl} — is it running?\n  ${err.message}`);
    process.exitCode = 1;
    return;
  }

  const tracked = projects.filter((p) => shortNames.has(p.name.toLowerCase()));
  if (tracked.length === 0) {
    console.log(`No synced projects on the board at ${apiBaseUrl}.`);
    console.log(`Run:  node scripts/portfolio-sync.mjs`);
    return;
  }

  // Board state + the direction engine's pick, per project.
  const rows = await Promise.all(tracked.map(async (p) => {
    const [tasks, next] = await Promise.all([
      api(`/api/projects/${p.id}/tasks`),
      api(`/api/projects/${p.id}/next-tasks`).catch(() => []),
    ]);
    const count = (s) => tasks.filter((t) => t.status === s).length;
    const top = topRecommendation(next);
    const stale = tasks.filter((t) => t.status === "InProgress" && daysSince(t.updatedAt) > TASK_PROGRESS_STALE_DAYS);
    return {
      name: p.name,
      repo: shortNames.get(p.name.toLowerCase()),
      backlog: count("Backlog"), todo: count("Todo"), prog: count("InProgress"),
      review: count("Review"), done: count("Done"),
      top, stale, tasks,
    };
  }));

  // Live GitHub — PRs are the real "in flight" signal.
  const prs = (await Promise.all(repos.map(async (repo) => {
    const list = await gh(["pr", "list", "-R", repo, "--state", "open", "--limit", "50",
      "--json", "number,title,createdAt,reviewDecision,isDraft,url"]).catch(() => []);
    return list.map((pr) => ({ ...pr, repo: repo.split("/").pop() }));
  }))).flat();

  console.log(`\nPORTFOLIO  ${new Date().toISOString().slice(0, 10)}   ${apiBaseUrl}`);
  console.log(`${"─".repeat(78)}`);
  console.log(`${pad("PROJECT", 22)}${lpad("BLOG", 6)}${lpad("TODO", 6)}${lpad("PROG", 6)}${lpad("REVW", 6)}${lpad("DONE", 6)}`);

  for (const r of rows) {
    console.log(
      `${pad(r.name, 22)}${lpad(r.backlog, 6)}${lpad(r.todo, 6)}${lpad(r.prog, 6)}${lpad(r.review, 6)}${lpad(r.done, 6)}`
    );
  }

  const totals = rows.reduce((a, r) => ({
    backlog: a.backlog + r.backlog, todo: a.todo + r.todo, prog: a.prog + r.prog,
    review: a.review + r.review, done: a.done + r.done,
  }), { backlog: 0, todo: 0, prog: 0, review: 0, done: 0 });
  console.log(`${"─".repeat(78)}`);
  console.log(
    `${pad("TOTAL", 22)}${lpad(totals.backlog, 6)}${lpad(totals.todo, 6)}${lpad(totals.prog, 6)}${lpad(totals.review, 6)}${lpad(totals.done, 6)}`
  );

  // The direction engine's pick per project, with the reason it gives. Don't re-rank these.
  const picks = rows.filter((r) => r.top);
  if (picks.length) {
    console.log(`\nNEXT UP`);
    for (const r of picks) {
      console.log(`  ${pad(r.name, 22)}${pad(r.top.name, 44)} ${r.top.priority}`);
    }
  }

  if (prs.length) {
    console.log(`\nIN FLIGHT — ${prs.length} open PR${prs.length === 1 ? "" : "s"}`);
    for (const pr of prs.sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt))) {
      const age = daysSince(pr.createdAt);
      const review = pr.reviewDecision || "no review";
      const flag = !pr.isDraft && !pr.reviewDecision && age > PR_REVIEW_STALE_DAYS ? "  ← STALE" : "";
      console.log(`  ${pad(pr.repo, 20)} #${pad(pr.number, 5)} ${lpad(`${age}d`, 4)}  ${pad(review.toLowerCase(), 16)}${pad(pr.title, 30)}${flag}`);
    }
  }

  const staleTasks = rows.flatMap((r) => r.stale.map((t) => ({ project: r.name, t })));
  if (staleTasks.length) {
    console.log(`\nSTALLED — InProgress > ${TASK_PROGRESS_STALE_DAYS}d`);
    for (const { project, t } of staleTasks) {
      console.log(`  ${pad(project, 20)} ${pad(t.name, 42)} ${daysSince(t.updatedAt)}d`);
    }
  }

  console.log(`\n${totals.backlog + totals.todo + totals.prog + totals.review + totals.done} tasks tracked across ${rows.length} projects.`);
  console.log(`Drill into one:  get_next_tasks(projectId) — see the kanban-project-ops skill.\n`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
