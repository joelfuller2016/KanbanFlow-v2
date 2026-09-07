#!/usr/bin/env node
// GitHub <-> KanbanFlow sync — "KanbanFlow IS GitHub, visualised".
//
// GitHub is the source of truth for issue CONTENT. The board is the visualisation, plus the
// one place where work that does not exist on GitHub yet can be drafted and then pushed.
//
// Five commands (full contract: docs/GITHUB-SYNC.md):
//
//   sync        GitHub -> board mirror. The default command.
//   onboard     register a NEW repo: create/link the Project, then backfill everything.
//   push-issue  Preview by default. `--allow-write --task <id>` performs one journaled,
//               additive issue create; `--resume <uuid>` explicitly reconciles an interruption.
//   backfill-links  verify sync-block URLs into durable links; quarantine ambiguity.
//   status      health check: counts both sides, unsynced tasks, drift, orphans, last sync.
//               Exit 0 = clean and complete · 1 = confirmed drift · 2 = incomplete or error.
//
// GITHUB WRITE SURFACE: exactly one dedicated `gh issue create` wrapper. It is unreachable
// without both --allow-write and --task, sends the frozen body over stdin, and runs only after
// the board has persisted Prepared -> Dispatched. Generic gh() stays structurally read-only.
// There is no edit/close/delete/comment path. GitHub has no issue-create idempotency key, so an
// exact operation marker plus authoritative paginated recovery closes the crash window; an
// ambiguous or incomplete result fails closed in NeedsReview and never auto-creates.
//
// Invariants this tool holds:
//   * GitHub owns issue content. Nothing here can reword, close, re-label-after-create or delete.
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
// Identity: repo -> Project (description marker for project selection), milestone -> Feature
// (name), issue -> TaskItem (Active ExternalWorkItemLink by immutable repo/issue node ids first;
// legacy "#<n> " prefix only when structured state is absent), GitHub comment -> TaskComment
// (node id marker). Linked task names are plain, human-editable titles.
//
//   node scripts/portfolio-sync.mjs --dry-run
//   node scripts/portfolio-sync.mjs --repos joelfuller2016/ZeroRouter
//   node scripts/portfolio-sync.mjs onboard joelfuller2016/new-repo
//   node scripts/portfolio-sync.mjs push-issue joelfuller2016/new-repo --all --dry-run
//   node scripts/portfolio-sync.mjs status
//   KANBANFLOW_PORTFOLIO_REPOS="o/a,o/b" node scripts/portfolio-sync.mjs

import { execFile, spawn } from "node:child_process";
import { realpathSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { resolve } from "node:path";
import { promisify } from "node:util";
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
const SYNC_DESCRIPTION_PREFIX = "<!-- kanbanflow-sync -->\n**Issue:** ";
const TRUNCATION_FOOTER = "…(truncated — see the issue)";
const SIGNALR_MAX_RECEIVE_BYTES = 32_768;
// Assume a 32,768-byte receive limit and reserve at least 10% for any protocol envelope and fields
// other than the markdown: ceil(32,768 / 10) = 3,277 bytes. This repo does not configure or prove
// that transport limit, so it is a conservative compatibility assumption, not a protocol guarantee.
// The enforced 29,491-byte description limit measures JSON.stringify(description), including the
// expansion of newlines, quotes and backslashes plus the JSON string's surrounding quotes.
const SIGNALR_ENVELOPE_HEADROOM_BYTES = Math.ceil(SIGNALR_MAX_RECEIVE_BYTES / 10);
const MAX_DESCRIPTION_BYTES = SIGNALR_MAX_RECEIVE_BYTES - SIGNALR_ENVELOPE_HEADROOM_BYTES;
const MIN_BODY_EXCERPT_BYTES = 1_024;
const requestedBodyLimit = Number.parseInt(process.env.KANBANFLOW_BODY_EXCERPT_LIMIT || "", 10);
const BODY_EXCERPT_LIMIT = Math.min(
  MAX_DESCRIPTION_BYTES,
  Math.max(MIN_BODY_EXCERPT_BYTES, Number.isFinite(requestedBodyLimit) ? requestedBodyLimit : MAX_DESCRIPTION_BYTES),
);
// High enough that no repo in the portfolio comes near it, because SATURATING it is a hard
// failure, not a warning: a read that stopped early makes every count and every "no drift"
// verdict downstream a lie. Overridable so the incompleteness path can actually be exercised.
const GH_PAGE_LIMIT = Math.max(1, Number.parseInt(process.env.KANBANFLOW_GH_PAGE_LIMIT || "2000", 10) || 2000);

const COMMANDS = ["sync", "onboard", "push-issue", "backfill-links", "status"];
const PUSH_MARKER_RE = /<!--\s*kanbanflow-push:([0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})\s*-->/gi;
const RAW_PUSH_MARKER_TOKEN_RE = /kanbanflow-push\s*:\s*([0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})/gi;
const GITHUB_ISSUE_URL_RE = /^https:\/\/github\.com\/([^/]+)\/([^/]+)\/issues\/(\d+)\/?$/i;

// Statuses the sync is allowed to advance a task *out of* when GitHub shows a linked open
// PR. A task a human has already parked in Review/Done is left alone (reported as drift
// instead) — the sync must never fight the board's owner.
const ADVANCEABLE_TO_REVIEW = new Set(["Backlog", "Todo", "InProgress"]);

function parseArgs(argv) {
  const args = {
    command: "sync",
    dryRun: false, includeClosed: false, repos: null, actor: "portfolio-sync", actorExplicit: false,
    comments: true, maxCommentChars: 0, // 0 = no cap; mirror the comment whole
    allowWrite: false, task: null, resume: null, authorizeAmbiguousRetry: null,
    resolutionReason: null, all: false, strict: false, json: false, limit: 0,
    internalTestHooks: false,
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
  //
  // The recovery flags matter most of all. `--resume --dry-run` swallowed the preview flag and
  // left `dryRun` false on the ONE path whose whole purpose is finishing a half-written remote
  // create; the post-loop UUID regexes below would then refuse for the wrong reason, or — with
  // a UUID present later on the line — not refuse at all.
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
      const value = takeValue("--max-comment-chars", "a character count");
      if (value === null) return args;
      args.maxCommentChars = Number.parseInt(value, 10) || 0;
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
      args.actorExplicit = true;
    }
    // --push is the alias; both spellings mean "you may create issues on GitHub".
    else if (a === "--allow-write" || a === "--push") args.allowWrite = true;
    else if (a === "--task") {
      const value = takeValue("--task", "an integer task id");
      if (value === null) return args;
      args.task = Number.parseInt(value, 10);
    }
    else if (a === "--resume") {
      const value = takeValue("--resume", "an operation UUID");
      if (value === null) return args;
      args.resume = value;
    }
    else if (a === "--authorize-ambiguous-retry") {
      const value = takeValue("--authorize-ambiguous-retry", "an operation UUID");
      if (value === null) return args;
      args.authorizeAmbiguousRetry = value;
    }
    else if (a === "--resolution-reason") {
      const value = takeValue("--resolution-reason", "non-blank text");
      if (value === null) return args;
      args.resolutionReason = value;
    }
    else if (a === "--all") args.all = true;
    else if (a === "--strict") args.strict = true;
    else if (a === "--json") args.json = true;
    else if (a === "--limit") {
      const value = takeValue("--limit", "an integer count");
      if (value === null) return args;
      args.limit = Number.parseInt(value, 10) || 0;
    }
    // Arming key 1 of 2 for the #336 crash harness. Useless on its own: without
    // KANBANFLOW_336_TEST_HOOKS naming a module, main() refuses to start.
    else if (a === "--internal-test-hooks") args.internalTestHooks = true;
    else if (a === "--help" || a === "-h") args.help = true;
    // `onboard owner/repo` — a bare owner/repo positional is a repo, so the common case
    // needs no flag at all.
    else if (!a.startsWith("-") && a.includes("/")) args.repos = [...(args.repos || []), a];
    else { args.error = `Unknown option "${a}".`; return args; }
  }

  if (args.task !== null && !Number.isFinite(args.task)) args.error = "--task needs an integer task id.";
  if (args.resume !== null && !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(args.resume)) {
    args.error = "--resume needs an operation UUID.";
  }
  if (args.authorizeAmbiguousRetry !== null && !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(args.authorizeAmbiguousRetry)) {
    args.error = "--authorize-ambiguous-retry needs an operation UUID.";
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

// Generic `gh()` is permanently read-only, enforced here instead of by review convention. A
// WHITELIST, not a blacklist: anything outside these read verbs is refused. The sole issue-create
// capability is the separate, fixed-argv `ghIssueCreate()` wrapper above its own explicit gate.
const GH_READ_VERBS = new Set(["issue list", "pr list", "label list", "api"]);
// `gh api` is GET unless something makes it otherwise. These are the flags that do.
const GH_API_WRITE_FLAGS = new Set(["--method", "-X", "-f", "--field", "-F", "--raw-field", "--input"]);

function assertReadOnly(args) {
  const verb = args[0] === "api" ? "api" : `${args[0]} ${args[1]}`;
  if (!GH_READ_VERBS.has(verb)) {
    throw new Error(`portfolio-sync is READ-ONLY against GitHub; refusing to run "gh ${verb}". If a write is genuinely needed, it belongs behind the durable-link design in docs/GITHUB-SYNC.md, not here.`);
  }
  if (args[0] === "api") {
    const offender = args.find((a) =>
      GH_API_WRITE_FLAGS.has(a)
      || /^--(?:method|field|raw-field|input)=/.test(a)
      || /^-[XfF].+/.test(a));
    if (offender) throw new Error(`portfolio-sync is READ-ONLY against GitHub; "gh api ${offender}" would issue a write.`);
  }
}

async function gh(args) {
  assertReadOnly(args);
  const { stdout } = await execFileAsync("gh", args, { maxBuffer: 64 * 1024 * 1024 });
  return JSON.parse(stdout || "[]");
}

function assertGhIssueCreateAllowed(options) {
  if (!options?.allowWrite || !Number.isInteger(options?.task) || options.task <= 0) {
    throw new Error("GitHub issue creation requires both --allow-write and --task <positive-id>.");
  }
  if (options.dryRun) {
    throw new Error("GitHub issue creation is disabled during --dry-run/preview.");
  }
}

function buildGhIssueCreateArgs(repo, request) {
  const args = [
    "issue", "create", "--repo", repo,
    "--title", request.title,
    "--body-file", "-",
  ];
  if (request.milestoneTitle) args.push("--milestone", request.milestoneTitle);
  if (request.label) args.push("--label", request.label);
  return args;
}

async function spawnWithStdin(command, args, input, spawnImpl = spawn) {
  return new Promise((resolvePromise, reject) => {
    const child = spawnImpl(command, args, {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", reject);
    child.on("close", (code) => {
      if (code === 0) resolvePromise({ stdout, stderr });
      else reject(new Error(`gh issue create exited ${code}${stderr.trim() ? `: ${stderr.trim()}` : ""}`));
    });
    child.stdin.on("error", reject);
    child.stdin.end(String(input ?? ""), "utf8");
  });
}

// The only GitHub write primitive in this file. It deliberately does not call generic `gh()`:
// callers cannot smuggle a write verb through the read-only allow-list, and the body travels on
// stdin so Markdown/newlines never cross a Windows command-line quoting boundary.
async function ghIssueCreate(repo, request, options, spawnImpl = spawn) {
  assertGhIssueCreateAllowed(options);
  const { stdout } = await spawnWithStdin(
    "gh",
    buildGhIssueCreateArgs(repo, request),
    request.body,
    spawnImpl,
  );
  const url = stdout.trim().split(/\r?\n/).find((line) => GITHUB_ISSUE_URL_RE.test(line.trim()))?.trim();
  if (!url) throw new Error(`gh issue create returned no GitHub issue URL: ${stdout.trim() || "(empty stdout)"}`);
  return url;
}

async function fetchRepositoryIdentity(repo) {
  const record = await gh(["api", `repos/${repo}`]);
  if (!record?.node_id || !record?.owner?.login || !record?.name) {
    throw new Error(`${repo}: GitHub repository response omitted node_id/owner/name; refusing an unstable repository link.`);
  }
  return {
    externalId: record.node_id,
    owner: record.owner.login,
    name: record.name,
    fullName: record.full_name || `${record.owner.login}/${record.name}`,
  };
}

function parseGitHubIssueUrl(value) {
  const match = GITHUB_ISSUE_URL_RE.exec(String(value || "").trim());
  if (!match) return null;
  return { owner: match[1], repo: match[2], number: Number.parseInt(match[3], 10) };
}

function pushOperationIds(body) {
  const ids = [];
  for (const match of String(body || "").matchAll(PUSH_MARKER_RE)) ids.push(match[1].toLowerCase());
  return [...new Set(ids)];
}

function rawPushOperationIds(body) {
  const ids = [];
  for (const match of String(body || "").matchAll(new RegExp(RAW_PUSH_MARKER_TOKEN_RE.source, "gi"))) {
    ids.push(match[1].toLowerCase());
  }
  return ids;
}

function issuePushMarkerDisposition(body) {
  const rawOperationIds = rawPushOperationIds(body);
  if (rawOperationIds.length === 0) return { kind: "none", operationId: null, rawOperationIds };
  const exactOperationIds = pushOperationIds(body);
  const operationId = rawOperationIds[0];
  if (rawOperationIds.length === 1
    && exactOperationIds.length === 1
    && exactOperationIds[0] === operationId
    && exactMarkerCount(body, operationId) === 1) {
    return { kind: "one", operationId, rawOperationIds };
  }
  return { kind: "ambiguous", operationId: null, rawOperationIds, exactOperationIds };
}

function pushMarker(operationId) {
  return `<!-- kanbanflow-push:${String(operationId).toLowerCase()} -->`;
}

function exactMarkerCount(body, operationId) {
  const expected = String(operationId).toLowerCase();
  let count = 0;
  for (const match of String(body || "").matchAll(new RegExp(PUSH_MARKER_RE.source, "gi"))) {
    if (match[1].toLowerCase() === expected) count++;
  }
  return count;
}

function rawTargetMarkerTokenCount(body, operationId) {
  const escaped = String(operationId).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  // Manual retry needs a stronger negative than protocol parsing: even a malformed/comment-less
  // copy or a token pasted into a PR proves the UUID is visible remotely and blocks recreation.
  const token = new RegExp(`kanbanflow-push\\s*:\\s*${escaped}`, "gi");
  return [...String(body || "").matchAll(token)].length;
}

function flattenGhPages(value) {
  if (!Array.isArray(value)) return [];
  return value.flatMap((page) => Array.isArray(page) ? page : [page]);
}

function exactMarkerMatches(records, operationId) {
  return flattenGhPages(records).filter((record) =>
    !record?.pull_request
    && pushOperationIds(record?.body).length === 1
    && exactMarkerCount(record?.body, operationId) === 1);
}

function analyzeMarkerRecords(records, operationId) {
  const allRecords = flattenGhPages(records);
  const issues = allRecords.filter((record) => !record?.pull_request);
  const rawRecords = allRecords
    .map((record) => ({ record, occurrences: rawTargetMarkerTokenCount(record?.body, operationId) }))
    .filter(({ occurrences }) => occurrences > 0);
  return {
    matches: exactMarkerMatches(issues, operationId),
    rawTargetOccurrenceCount: rawRecords.reduce((count, item) => count + item.occurrences, 0),
    rawTargetRecordCount: rawRecords.length,
  };
}

function issueHasOneCleanOperationMarker(issue, operationId) {
  const diagnostics = analyzeMarkerRecords([issue], operationId);
  return exhaustiveMarkerDisposition(
    true,
    diagnostics.matches.length,
    diagnostics.rawTargetOccurrenceCount,
  ) === "one";
}

async function fetchIssueByNumber(repo, number) {
  const issue = await gh(["api", `repos/${repo}/issues/${number}`]);
  if (!issue || issue.pull_request || !issue.node_id || issue.number !== number || !issue.html_url) {
    throw new Error(`${repo}#${number}: GitHub did not return a verifiable issue record.`);
  }
  return issue;
}

async function findIssueByPushMarker(repo, operationId) {
  const marker = pushMarker(operationId);
  let hints = [];
  try {
    hints = await gh([
      "issue", "list", "-R", repo, "--state", "all",
      "--search", `\"${marker}\" in:body`,
      "--limit", String(GH_PAGE_LIMIT), "--json", "id,number,url,title,body,state",
    ]);
  } catch (err) {
    // Search is an indexed hint only. Recovery still proceeds to the authoritative REST scan.
    console.warn(`  ! marker search hint failed for ${operationId}: ${err.message}`);
  }

  const pages = await gh([
    "api", "--paginate", "--slurp",
    `repos/${repo}/issues?state=all&per_page=100`,
  ]);
  const diagnostics = analyzeMarkerRecords(pages, operationId);
  return { hints, ...diagnostics, complete: true };
}

// `comments` rides along on the list call we already make, so issue comments cost zero extra
// requests. Each carries a stable node id (e.g. `IC_kwDO…`) — that id is the dedupe key that
// makes comment mirroring idempotent.
const ISSUE_FIELDS = "id,number,title,body,labels,milestone,assignees,state,updatedAt,url,comments";

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

function commentTextFor(c, maxChars) {
  let body = c.body;
  if (maxChars > 0) {
    body = excerptFor(body, maxChars, "…(truncated — read it on GitHub)", (value) => value.length);
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

// Legacy fallback only. New links use ExternalWorkItemLink; pre-#336 boards continue to resolve
// the historical "#<n> " display prefix until verified backfill, with no flag day.
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
const REPO_NODE_LINK_RE = /<!--\s*github-node:\s*([^\s>]+)\s*-->/;

function repoLinkMarker(repo) {
  return `<!-- github: ${repo} -->`;
}

function repoNodeLinkMarker(repositoryExternalId) {
  return `<!-- github-node: ${repositoryExternalId} -->`;
}

function linkedRepoOf(project) {
  const m = REPO_LINK_RE.exec(String(project?.description || ""));
  return m ? m[1] : null;
}

function linkedRepoNodeOf(project) {
  const match = REPO_NODE_LINK_RE.exec(String(project?.description || ""));
  return match ? match[1] : null;
}

function projectDescriptionFor(repo, repoIdentity) {
  return `${repoNodeLinkMarker(repoIdentity.externalId)}\n${repoLinkMarker(repo)}\nSynced from https://github.com/${repo} — GitHub issues are the source of truth.`;
}

function refreshedProjectDescription(project, repo, repoIdentity) {
  const content = String(project?.description || "")
    .replace(new RegExp(REPO_NODE_LINK_RE.source, "g"), "")
    .replace(new RegExp(REPO_LINK_RE.source, "g"), "")
    .replace(/^Synced from https:\/\/github\.com\/\S+ — GitHub issues are the source of truth\.$/m,
      `Synced from https://github.com/${repo} — GitHub issues are the source of truth.`)
    .replace(/^\s+/, "")
    .trimEnd();
  const markers = `${repoNodeLinkMarker(repoIdentity.externalId)}\n${repoLinkMarker(repo)}`;
  return content ? `${markers}\n${content}` : projectDescriptionFor(repo, repoIdentity);
}

// Returns { project, linkedBy: "marker" | "name" | null, conflict }. `conflict` is a
// human-readable warning that is ALWAYS surfaced — a forked or hijacked link is exactly the
// class of failure that otherwise looks like a healthy mirror.
function resolveProjectLink(repo, projects, repoIdentity = null) {
  const shortName = repo.split("/").pop();
  const lower = repo.toLowerCase();

  if (repoIdentity?.externalId) {
    const byNode = projects.filter((project) => linkedRepoNodeOf(project) === repoIdentity.externalId);
    if (byNode.length > 1) {
      return {
        project: byNode[0], linkedBy: "node", needsRefresh: true, hardConflict: true,
        conflict: `${byNode.length} projects carry repository node ${repoIdentity.externalId} (${byNode.map((p) => `#${p.id} "${p.name}"`).join(", ")}) — the mirror is FORKED.`,
      };
    }
    if (byNode.length === 1) {
      const project = byNode[0];
      return {
        project,
        linkedBy: "node",
        needsRefresh: (linkedRepoOf(project) || "").toLowerCase() !== lower,
        conflict: null,
      };
    }
  }

  const marked = projects.filter((p) => (linkedRepoOf(p) || "").toLowerCase() === lower);
  const conflictingNodeMarkers = repoIdentity?.externalId
    ? marked.filter((project) => {
      const nodeId = linkedRepoNodeOf(project);
      return nodeId && nodeId !== repoIdentity.externalId;
    })
    : [];
  if (conflictingNodeMarkers.length > 0) {
    const project = conflictingNodeMarkers[0];
    return {
      project,
      linkedBy: "marker",
      needsRefresh: false,
      hardConflict: true,
      conflict: `project #${project.id} carries mutable path ${repo} but conflicting immutable repository node ${linkedRepoNodeOf(project)} (expected ${repoIdentity.externalId}); refusing to overwrite it.`,
    };
  }
  if (marked.length > 1) {
    return {
      project: marked[0], linkedBy: "marker", needsRefresh: Boolean(repoIdentity?.externalId),
      conflict: `${marked.length} projects carry the ${repo} link marker (${marked.map((p) => `#${p.id} "${p.name}"`).join(", ")}) — the mirror is FORKED. Merge them by hand; syncing into #${marked[0].id}.`,
    };
  }
  if (marked.length === 1) return { project: marked[0], linkedBy: "marker", needsRefresh: Boolean(repoIdentity?.externalId), conflict: null };

  // Fallback: match the repo's short name, but only against a project no OTHER repo has
  // claimed. Adopting a project already marked for a different repo would merge two repos'
  // issues into one board permanently.
  const named = projects.filter((p) => p.name.toLowerCase() === shortName.toLowerCase());
  const unclaimed = named.filter((p) => linkedRepoOf(p) === null && linkedRepoNodeOf(p) === null);
  if (unclaimed.length === 1) return { project: unclaimed[0], linkedBy: "name", needsRefresh: true, conflict: null };
  if (unclaimed.length > 1) {
    return {
      project: unclaimed[0], linkedBy: "name",
      conflict: `${unclaimed.length} unlinked projects are named "${shortName}" (${unclaimed.map((p) => `#${p.id}`).join(", ")}) — name matching is ambiguous. Run \`onboard ${repo}\` after deleting or renaming the wrong one; using #${unclaimed[0].id}.`,
    };
  }
  if (named.length > 0) {
    const claimedBy = linkedRepoNodeOf(named[0])
      ? `repository node ${linkedRepoNodeOf(named[0])}`
      : linkedRepoOf(named[0]);
    return {
      project: null, linkedBy: null,
      conflict: `project "${named[0].name}" (#${named[0].id}) is already linked to ${claimedBy}, so ${repo} will NOT be merged into it.`,
    };
  }
  return { project: null, linkedBy: null, conflict: null };
}

function assertLivePushProjectLink(link, live) {
  if (live && link?.conflict) {
    throw new Error(`Live push refuses an ambiguous project mapping: ${link.conflict}`);
  }
}

// The sync stamps a provenance block into every task description it creates. When a task is
// previewed for the other direction that block is noise on GitHub, so strip it out.
function stripSyncBlock(description) {
  return String(description || "")
    .replace(/<!--\s*kanbanflow-sync\s*-->[\s\S]*?<!--\s*\/kanbanflow-sync\s*-->/g, "")
    .trim();
}

const utf8Bytes = (value) => Buffer.byteLength(value, "utf8");
const serializedUtf8Bytes = (value) => utf8Bytes(JSON.stringify(String(value)));
const OPEN_FENCE_LINE = /^ {0,3}(`{3,}|~{3,})(.*)$/;
const CLOSE_FENCE_LINE = /^ {0,3}(`{3,}|~{3,})[ \t]*$/;

function unclosedFence(text) {
  let open = null;
  for (const line of text.split("\n")) {
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

// Return the longest line-boundary excerpt whose complete rendered value fits maxBytes. Both
// backtick and tilde fences are recognised; an open retained fence is closed before the prose
// footer. Recomputing the footer and fence for each candidate charges the caller's exact size.
function excerptFor(body, maxBytes, footer = TRUNCATION_FOOTER, sizeOf = utf8Bytes) {
  const source = String(body || "");
  if (sizeOf(source) <= maxBytes) return source;

  const render = (kept) => {
    const fence = unclosedFence(kept);
    return `${kept}${fence ? `\n${fence}` : ""}\n\n${footer}`;
  };
  let kept = "";
  let newline = source.indexOf("\n");
  while (newline !== -1) {
    const candidate = source.slice(0, newline);
    if (sizeOf(render(candidate)) > maxBytes) break;
    kept = candidate;
    newline = source.indexOf("\n", newline + 1);
  }
  return render(kept);
}

function descriptionFor(issue, linkedPr) {
  const oversizedHeaderStub = "<!-- kanbanflow-sync -->\n**Issue:** (provenance omitted: header exceeded description budget)\n<!-- /kanbanflow-sync -->";
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
  let prefix = `${lines.join("\n")}\n`;
  if (serializedUtf8Bytes(`${prefix}\n\n${TRUNCATION_FOOTER}`) > BODY_EXCERPT_LIMIT) {
    return oversizedHeaderStub;
  }
  const body = String(issue.body || "").trim();
  if (body) {
    // Charge the complete JSON-serialized description for every candidate. This accounts for
    // escaping in both the generated prefix and markdown body, rather than treating raw bytes as
    // though they were the wire representation.
    const descriptionSize = (candidateBody) => serializedUtf8Bytes(`${prefix}${candidateBody}`);
    lines.push(excerptFor(body, BODY_EXCERPT_LIMIT, TRUNCATION_FOOTER, descriptionSize));
  }
  return lines.join("\n");
}

// Issue #495 tracks general description reconciliation. This is intentionally only its narrow,
// conservative slice: refresh descriptions that still have both our exact generated header shape
// and our truncation footer. Anything else may contain a human edit and must be left untouched.
function descriptionRefreshFor(storedDescription, regeneratedDescription) {
  const stored = String(storedDescription || "");
  if (!stored.startsWith(SYNC_DESCRIPTION_PREFIX)) return null;
  if (!stored.trimEnd().endsWith(TRUNCATION_FOOTER)) return null;
  // `descriptionFor()` stamps the current time. Ignore that one generated field when deciding
  // whether the body is stale, or a still-truncated large issue would be rewritten on every run.
  const withoutSyncTime = (value) => value.replace(/^\*\*Synced:\*\* .*$/m, "**Synced:**");
  return withoutSyncTime(stored) === withoutSyncTime(regeneratedDescription)
    ? null
    : regeneratedDescription;
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

// All #336 REST paths live behind this adapter. The service owns journal/uniqueness semantics;
// the script owns GitHub I/O. Keeping the route vocabulary here makes endpoint naming changes a
// one-block edit while the C# and Node work land in parallel.
const externalApi = {
  listProjectLinks: (projectId) =>
    api("GET", `/api/projects/${projectId}/external-work-item-links`),
  findGitHubLinkByNode: async (externalNodeId) => {
    try {
      return await api("GET", `/api/external-work-item-links/github/by-node?externalNodeId=${encodeURIComponent(externalNodeId)}`);
    } catch (err) {
      if (/->\s*404\b/.test(err.message)) return null;
      throw err;
    }
  },
  registerLink: (taskId, request) =>
    api("PUT", `/api/tasks/${taskId}/external-work-item-links/github`, request),
  quarantine: (taskId, request) =>
    api("POST", `/api/tasks/${taskId}/external-work-item-quarantines/github`, request),
  prepare: (taskId, request) =>
    api("POST", `/api/tasks/${taskId}/external-write-operations`, request),
  getOperation: (operationId) =>
    api("GET", `/api/external-write-operations/${operationId}`),
  resume: (operationId) =>
    api("POST", `/api/external-write-operations/${operationId}/resume`),
  dispatch: (operationId, request) =>
    api("POST", `/api/external-write-operations/${operationId}/dispatch`, request),
  finalize: (operationId, request) =>
    api("POST", `/api/external-write-operations/${operationId}/finalize`, request),
  failure: (operationId, request) =>
    api("POST", `/api/external-write-operations/${operationId}/failure`, request),
  authorizeAmbiguousRetry: (operationId, request) =>
    api("POST", `/api/external-write-operations/${operationId}/authorize-ambiguous-retry`, request),
};

const WRITE_STATE_BY_ORDINAL = ["Prepared", "Dispatched", "Completed", "NeedsReview", "DefinitiveFailure", "ManualRetryAuthorized"];
function writeStateName(value) {
  if (typeof value === "number") return WRITE_STATE_BY_ORDINAL[value] || `Unknown(${value})`;
  return String(value || "");
}

function isActiveLink(link) {
  return link && (link.state === 0 || String(link.state).toLowerCase() === "active");
}

function githubLinksOf(task, projectLinks = []) {
  const embedded = Array.isArray(task?.externalLinks) ? task.externalLinks : [];
  // A transfer refresh updates the separately fetched project link list while the task DTO's
  // embedded snapshot remains stale for this run. Put refreshed/project rows first so id-based
  // dedupe cannot resurrect the old repository identity.
  return [...projectLinks.filter((link) => link.taskId === task?.id), ...embedded]
    .filter((link, index, all) => all.findIndex((other) =>
      other.id != null && link.id != null ? other.id === link.id : other === link) === index)
    .filter((link) => link.provider === 0 || String(link.provider).toLowerCase() === "github");
}

function activeGithubLink(task, repositoryExternalId, projectLinks = []) {
  const matches = githubLinksOf(task, projectLinks).filter((link) =>
    isActiveLink(link) && link.repositoryExternalId === repositoryExternalId);
  if (matches.length > 1) {
    throw new Error(`task ${task.id} has ${matches.length} active GitHub links for repository ${repositoryExternalId}`);
  }
  return matches[0] || null;
}

function operationFromLease(value) {
  return value?.operation || value;
}

function leaseTokenFrom(value) {
  return value?.leaseToken || operationFromLease(value)?.leaseToken || null;
}

// --- Sync -------------------------------------------------------------------------------

const stats = {
  projectsCreated: 0, featuresCreated: 0, tasksCreated: 0,
  tasksUpdated: 0, tasksMoved: 0, tasksUnchanged: 0, descriptionsRefreshed: 0,
  commentsCreated: 0, commentsUnchanged: 0, commentFetchFailures: 0,
  projectsLinked: 0, issuesPreviewed: 0, issuesCreated: 0,
  linksFinalized: 0, linksRegistered: 0, linksQuarantined: 0,
  biggestComment: { chars: 0, author: null, issue: null },
  drift: [],
  incomplete: [], // a fetch that hit its bound — every count derived from it is unreliable
};

function plan(dryRun, verb, what) {
  console.log(`  ${dryRun ? "[dry-run] would" : "        "} ${verb} ${what}`);
}

function recoveryDisposition(stateValue, safeToCreate, scanComplete, matchCount) {
  const state = writeStateName(stateValue);
  if (state === "Completed") return "completed";
  if (state === "Dispatched" || state === "NeedsReview") {
    return scanComplete && matchCount === 1 ? "finalize" : "needs-review";
  }
  if ((state === "Prepared" || state === "DefinitiveFailure" || state === "ManualRetryAuthorized") && safeToCreate) return "create";
  return "needs-review";
}

function exhaustiveMarkerDisposition(scanComplete, matchCount, rawTargetOccurrenceCount = matchCount) {
  if (!scanComplete) return "incomplete";
  if (rawTargetOccurrenceCount === 0 && matchCount === 0) return "zero";
  if (rawTargetOccurrenceCount === 1 && matchCount === 1) return "one";
  return "ambiguous";
}

function operationRepo(operation) {
  return `${operation.repositoryOwner}/${operation.repositoryName}`;
}

function validateOperationTarget(operation, repoIdentity, taskId) {
  if (!operation?.operationId) throw new Error("External write response omitted operation.operationId.");
  if (operation.taskId !== taskId) {
    throw new Error(`operation ${operation.operationId} belongs to task ${operation.taskId}, not requested task ${taskId}`);
  }
  if (operation.repositoryExternalId !== repoIdentity.externalId) {
    throw new Error(`operation ${operation.operationId} targets repository node ${operation.repositoryExternalId}, not ${repoIdentity.externalId}`);
  }
  if (operationRepo(operation).toLowerCase() !== repoIdentity.fullName.toLowerCase()) {
    console.warn(`  ! operation ${operation.operationId} was prepared for ${operationRepo(operation)}; immutable repository node still resolves to renamed/transferred ${repoIdentity.fullName}`);
  }
}

function operationRequiresFreshPrecreateScan(operation) {
  return writeStateName(operation?.state) === "ManualRetryAuthorized" || Boolean(operation?.dispatchedAt);
}

function assertFreshPrecreateScan(operation, args) {
  if (!operationRequiresFreshPrecreateScan(operation)) return;
  const evidence = args?.freshZeroScanEvidence;
  const scanCompletedAt = Date.parse(evidence?.scanCompletedAt);
  const lastAttemptAt = Date.parse(operation?.lastAttemptAt);
  if (String(evidence?.confirmationOperationId || "").toLowerCase() !== String(operation.operationId).toLowerCase()
    || evidence?.scanComplete !== true
    || evidence?.observedMatchCount !== 0
    || evidence?.observedRawTargetOccurrenceCount !== 0
    || !Number.isFinite(scanCompletedAt)
    || (writeStateName(operation?.state) === "ManualRetryAuthorized"
      && (!Number.isFinite(lastAttemptAt) || scanCompletedAt < lastAttemptAt))) {
    throw new Error(`operation ${operation.operationId} has prior dispatch history; a fresh exhaustive zero-marker scan is required immediately before create`);
  }
}

function buildExternalDispatchRequest(operation, leaseToken, args) {
  assertFreshPrecreateScan(operation, args);
  const request = { operationId: operation.operationId, leaseToken };
  if (writeStateName(operation.state) === "ManualRetryAuthorized") {
    return {
      ...request,
      ...args.freshZeroScanEvidence,
      confirmationOperationId: operation.operationId,
      expectedAttemptCount: operation.attemptCount,
    };
  }
  if (operationRequiresFreshPrecreateScan(operation)) {
    throw new Error(`operation ${operation.operationId} is Prepared with prior dispatch history; it may proof-finalize a late marker but cannot dispatch a replacement without ManualRetryAuthorized state`);
  }
  return request;
}

async function recordAmbiguousWriteFailure(operationId, leaseToken, error, api = externalApi) {
  if (!leaseToken) return null;
  return api.failure(operationId, {
    operationId,
    leaseToken,
    // Every failure this script can observe occurs after a lease may represent prior dispatch
    // lineage. It never asks the service to erase that fact as DefinitivePreCreate.
    kind: "AmbiguousOrIncomplete",
    error: String(error?.message || error).slice(0, 4000),
  });
}

function linkMetadataNeedsRefresh(link, repoIdentity, issue) {
  return Boolean(link && repoIdentity && (
    link.repositoryExternalId !== repoIdentity.externalId
    || link.repositoryOwner !== repoIdentity.owner
    || link.repositoryName !== repoIdentity.name
    || link.externalNodeId !== (issue.node_id || issue.id)
    || link.externalNumber !== issue.number
    || link.url !== (issue.html_url || issue.url)
  ));
}

function verifiedFinalizeRequest(operation, leaseToken, repoIdentity, issue, actor) {
  return {
    operationId: operation.operationId,
    leaseToken: leaseToken || null,
    repositoryExternalId: repoIdentity.externalId,
    repositoryOwner: repoIdentity.owner,
    repositoryName: repoIdentity.name,
    externalNodeId: issue.node_id || issue.id,
    externalNumber: issue.number,
    url: issue.html_url || issue.url,
    actor,
  };
}

async function finalizeVerifiedIssue(operation, leaseToken, issue, repoIdentity, actor) {
  let link = await externalApi.finalize(
    operation.operationId,
    verifiedFinalizeRequest(operation, leaseToken, repoIdentity, issue, actor),
  );
  stats.linksFinalized++;
  // An operation freezes owner/name before GitHub contact, but repository node identity survives
  // rename/transfer. Immediately refresh mutable display metadata through the idempotent verified
  // registration boundary so a resumed in-flight operation never leaves a stale URL behind.
  if (linkMetadataNeedsRefresh(link, repoIdentity, issue)) {
    link = await externalApi.registerLink(
      operation.taskId,
      verifiedLinkRequest(operation.taskId, repoIdentity, issue, actor),
    );
    stats.linksRegistered++;
  }
  return link;
}

async function recoverAmbiguousOperation(repo, operation, leaseToken, repoIdentity, args) {
  let scan;
  try {
    scan = await findIssueByPushMarker(repo, operation.operationId);
  } catch (err) {
    await recordAmbiguousWriteFailure(operation.operationId, leaseToken, `Authoritative marker scan incomplete: ${err.message}`)
      .catch((recordErr) => console.error(`  ! could not persist NeedsReview: ${recordErr.message}`));
    throw new Error(`operation ${operation.operationId}: authoritative GitHub scan failed; outcome is NeedsReview and no create was attempted (${err.message})`);
  }

  const markerDisposition = exhaustiveMarkerDisposition(
    scan.complete,
    scan.matches.length,
    scan.rawTargetOccurrenceCount,
  );
  if (markerDisposition === "one") {
    const issue = scan.matches[0];
    const link = await finalizeVerifiedIssue(operation, leaseToken, issue, repoIdentity, args.actor);
    console.log(`  recovered operation ${operation.operationId} → ${issue.html_url || issue.url}`);
    return link;
  }

  const hint = scan.hints.length === 1 ? " (indexed search had one hint, but hints are never trusted)" : "";
  const reason = `Authoritative marker scan found ${scan.matches.length} clean match(es) and ${scan.rawTargetOccurrenceCount ?? "unknown"} raw target occurrence(s) across ${scan.rawTargetRecordCount ?? "unknown"} issue record(s)${hint}; expected exactly one clean record with one occurrence.`;
  await recordAmbiguousWriteFailure(operation.operationId, leaseToken, reason)
    .catch((recordErr) => console.error(`  ! could not persist NeedsReview: ${recordErr.message}`));
  throw new Error(`operation ${operation.operationId}: ${reason} State remains NeedsReview; no create was attempted.`);
}

async function scanForExplicitRetryDecision(repo, operationId) {
  let scan;
  try {
    scan = await findIssueByPushMarker(repo, operationId);
  } catch (err) {
    throw new Error(`operation ${operationId}: exhaustive GitHub marker scan failed; retry authorization/create remains blocked (${err.message})`);
  }
  return {
    scan,
    disposition: exhaustiveMarkerDisposition(
      scan.complete,
      scan.matches.length,
      scan.rawTargetOccurrenceCount,
    ),
  };
}

// --- test hooks -------------------------------------------------------------------------
//
// Four named instants in the live create path. In production every one of them is a no-op
// function call: no environment reads, no filesystem access, no waiting. The crash/barrier
// harness that `scripts/verify-336-github.ps1` drives lives in scripts/test-hooks/, which
// this module never imports.
//
// This is deliberately arming-by-two-keys. The barriers used to sit here inline, armed by the
// mere PRESENCE of an environment variable, so one stray KANBANFLOW_336_* pair in a shell
// turned a real `push-issue --allow-write` into a run that blocked on a file and then threw
// into the ambiguous-failure handler. Nothing documented it. Now the operator must pass
// --internal-test-hooks AND point KANBANFLOW_336_TEST_HOOKS at a module; either alone arms
// nothing, and loading prints a banner that cannot be mistaken for a production run.
const hooks = {
  beforePrepare: async () => {},
  afterDispatch: async () => {},
  afterManualResume: async () => {},
  afterCreate: async () => {},
};

// A hook giving up is a statement about the HARNESS, never about GitHub. It must not reach
// recordAmbiguousWriteFailure: the after-create hook fires once the issue exists and its
// marker is verified, so recording NeedsReview there would mark a succeeded operation for
// human review and burn an attempt. A distinct type is what lets the create-window handler
// tell the two apart; a string match on the message would not survive a reworded barrier.
class HookAbort extends Error {
  constructor(message) {
    super(message);
    this.name = "HookAbort";
  }
}

// Loaded from main() only. Returns the banner lines it printed so a test can assert that an
// env-only arming attempt printed nothing and installed nothing.
async function installTestHooks(args, env = process.env) {
  const modulePath = env.KANBANFLOW_336_TEST_HOOKS;
  if (!args.internalTestHooks) return { installed: false, reason: modulePath ? "env-set-without-flag" : "not-requested" };
  if (!modulePath) {
    throw new Error("--internal-test-hooks requires KANBANFLOW_336_TEST_HOOKS to name a hook module.");
  }
  // The crash-injection scenarios genuinely need live creates against the fixture repo, so
  // this stays possible — never by accident. Two keys to arm the harness, a third to let it
  // touch GitHub.
  if (args.allowWrite && env.KANBANFLOW_336_ALLOW_WRITE_HOOKS !== "1") {
    throw new Error("--internal-test-hooks with --allow-write additionally requires KANBANFLOW_336_ALLOW_WRITE_HOOKS=1.");
  }
  const module = await import(pathToFileURL(resolve(modulePath)).href);
  const installed = await module.install({ HookAbort });
  for (const name of Object.keys(hooks)) {
    if (typeof installed?.[name] === "function") hooks[name] = installed[name];
  }
  const banner = [
    "!! #336 TEST HOOKS ACTIVE — this is not a production run",
    `!! hook module: ${modulePath}`,
    `!! hooks installed: ${Object.keys(hooks).filter((name) => typeof installed?.[name] === "function").join(", ")}`,
  ];
  for (const line of banner) console.error(line);
  return { installed: true, banner };
}

async function executePreparedOperation(repo, leaseResult, repoIdentity, args) {
  const operation = operationFromLease(leaseResult);
  const leaseToken = leaseTokenFrom(leaseResult);
  validateOperationTarget(operation, repoIdentity, args.task);
  assertFreshPrecreateScan(operation, args);

  const disposition = recoveryDisposition(operation.state, leaseResult?.safeToCreate, true, 0);
  const writeState = writeStateName(operation.state);
  if (disposition !== "create" || !["Prepared", "ManualRetryAuthorized"].includes(writeState) || !leaseToken) {
    throw new Error(`operation ${operation.operationId} is ${writeStateName(operation.state)} and is not safe to create; use --resume for reconciliation`);
  }
  if (exactMarkerCount(operation.body, operation.operationId) !== 1) {
    const error = new Error(`operation ${operation.operationId} frozen body does not contain exactly one exact push marker`);
    await recordAmbiguousWriteFailure(operation.operationId, leaseToken, error);
    throw error;
  }

  if (operationRequiresFreshPrecreateScan(operation)) await runManualResumeHook(operation);
  const dispatchRequest = buildExternalDispatchRequest(operation, leaseToken, args);
  await externalApi.dispatch(operation.operationId, dispatchRequest);
  let issueUrl = null;
  try {
    await hooks.afterDispatch({
      operationId: operation.operationId,
      taskId: operation.taskId,
      attemptCount: operation.attemptCount,
    });
    issueUrl = await ghIssueCreate(repo, {
      title: operation.title,
      body: operation.body,
      milestoneTitle: operation.milestoneTitle,
      label: operation.label,
    }, args);
    const parsed = parseGitHubIssueUrl(issueUrl);
    if (!parsed || `${parsed.owner}/${parsed.repo}`.toLowerCase() !== repo.toLowerCase()) {
      throw new Error(`created issue URL ${issueUrl} does not belong to ${repo}`);
    }
    const issue = await fetchIssueByNumber(repo, parsed.number);
    if (!issueHasOneCleanOperationMarker(issue, operation.operationId)) {
      throw new Error(`created issue ${issueUrl} did not preserve exactly one clean operation marker`);
    }
    await hooks.afterCreate({
      operationId: operation.operationId,
      taskId: operation.taskId,
      issueNumber: issue.number,
      issueUrl: issue.html_url,
    });
    const link = await finalizeVerifiedIssue(operation, leaseToken, issue, repoIdentity, args.actor);
    stats.issuesCreated++;
    console.log(`  created ${issue.html_url} and finalized durable link for task ${operation.taskId}`);
    return link;
  } catch (err) {
    return await handleCreateWindowFailure(operation, leaseToken, issueUrl, err);
  }
}

// The pre-dispatch hook, extracted so its abort behaviour is testable. This one sits OUTSIDE
// the create window's try on purpose: nothing has reached GitHub yet, so there is no ambiguous
// outcome to record and recording one would be the very path a hook must never enter. What an
// abort here DOES leave behind is a claimed lease and a burned attempt — the resume that got us
// here took both — so the message says so rather than leaving the operator to infer it from a
// bare timeout. (Releasing the lease early would need a service verb that does not exist; the
// bound is the lease TTL.)
async function runManualResumeHook(operation) {
  try {
    await hooks.afterManualResume({
      operationId: operation.operationId,
      taskId: operation.taskId,
      attemptCount: operation.attemptCount,
    });
  } catch (err) {
    if (err instanceof HookAbort) {
      throw new HookAbort(`operation ${operation.operationId}: #336 test hook aborted BEFORE dispatch, so no create was attempted and no state was written. The lease stays claimed until it expires; re-run --resume ${operation.operationId} after that. (${err.message})`);
    }
    throw err;
  }
}

// The create window's failure handler, extracted so a test can drive it without a live create.
//
// The FIRST line is the load-bearing one. A HookAbort is the test harness giving up; it is not
// evidence about GitHub. The after-create hook fires when the issue already exists and its
// marker has already been verified, so letting a barrier timeout fall through here would record
// NeedsReview against an operation that demonstrably succeeded — burning an attempt and
// demanding human review for a run that did everything right.
async function handleCreateWindowFailure(operation, leaseToken, issueUrl, err, api = externalApi) {
  if (err instanceof HookAbort) throw err;
  const latest = await api.getOperation(operation.operationId).catch(() => null);
  if (writeStateName(latest?.state) === "Completed") return latest;
  await recordAmbiguousWriteFailure(
    operation.operationId,
    leaseToken,
    `${issueUrl ? `Issue may exist at ${issueUrl}; ` : "GitHub create outcome is unknown; "}${err.message}`,
    api,
  ).catch((recordErr) => console.error(`  ! could not persist NeedsReview: ${recordErr.message}`));
  throw new Error(`operation ${operation.operationId} is ambiguous/NeedsReview; re-run explicitly with --resume ${operation.operationId}. ${err.message}`);
}

// Drift wording lives here, shared, so `status` can never disagree with what `sync` reports.
// Two kinds, and only two: both describe a disagreement the sync deliberately WILL NOT fix.
const driftDoneButOpen = (repo, issue) =>
  `${repo}#${issue.number}: board says Done, issue is still open — ${issue.url}`;
const driftOrphanTask = (repo, taskName, issueNumber) =>
  `${repo}: task "${taskName}" has no matching issue #${issueNumber} on GitHub (deleted or transferred?)`;

async function ensureProject(repo, repoIdentity, dryRun) {
  const shortName = repo.split("/").pop();
  const projects = await api("GET", "/api/projects");
  // Marker first, then the case-insensitive short name ("ZeroRouter" and "zerorouter" are the
  // same repo, and creating a second project for one repo would fork the mirror permanently).
  const link = resolveProjectLink(repo, projects, repoIdentity);
  if (link.conflict) console.warn(`  ! ${link.conflict}`);
  if (link.hardConflict) throw new Error(link.conflict);
  if (link.project) {
    if (link.needsRefresh) {
      plan(dryRun, "refresh project repository identity", `#${link.project.id} → ${repo} (${repoIdentity.externalId})`);
      if (!dryRun) {
        await api("PUT", `/api/projects/${link.project.id}`, {
          name: link.project.name,
          description: refreshedProjectDescription(link.project, repo, repoIdentity),
        });
        stats.projectsLinked++;
      }
    }
    return link.project.id;
  }

  // The short name is taken by a project another repo owns, so it cannot be reused without
  // merging two repos' issues into one board. Fall back to the unambiguous "owner/repo".
  const nameTaken = projects.some((p) => p.name.toLowerCase() === shortName.toLowerCase());
  const name = nameTaken ? repo : shortName;

  plan(dryRun, "create project", name);
  if (dryRun) return null;
  const created = await api("POST", "/api/projects", {
    name,
    description: projectDescriptionFor(repo, repoIdentity),
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

function addUnique(map, key, task, kind) {
  if (key == null || key === "") return;
  const existing = map.get(key);
  if (existing && existing.id !== task.id) {
    throw new Error(`${kind} ${key} is claimed by tasks ${existing.id} and ${task.id}; refusing an ambiguous sync`);
  }
  map.set(key, task);
}

function buildTaskIdentityIndex(tasks, repoIdentity, projectLinks = [], operationTasks = new Map()) {
  const byId = new Map(tasks.map((task) => [task.id, task]));
  const durableByNode = new Map();
  const durableByNumber = new Map();
  const durableLinkByTaskId = new Map();
  const legacyByNumber = new Map();
  const quarantineByNumber = new Map();
  const foreignLinkByNumber = new Map();
  const durableTaskIds = new Set();
  const quarantinedTaskIds = new Set();
  const projectWideQuarantines = [];
  const foreignLinkedTaskIds = new Set();

  for (const task of tasks) {
    const allGithubLinks = githubLinksOf(task, projectLinks);
    const repoLinks = allGithubLinks
      .filter((candidate) => candidate.repositoryExternalId === repoIdentity.externalId);
    if (allGithubLinks.length > 0 && repoLinks.length === 0) {
      foreignLinkedTaskIds.add(task.id);
      const candidateNumber = allGithubLinks[0].externalNumber ?? issueNumberFromTaskName(task.name);
      if (candidateNumber != null) {
        const group = foreignLinkByNumber.get(candidateNumber) || [];
        group.push(task);
        foreignLinkByNumber.set(candidateNumber, group);
      }
      continue;
    }
    const link = activeGithubLink(task, repoIdentity.externalId, projectLinks);
    if (link) {
      durableTaskIds.add(task.id);
      durableLinkByTaskId.set(task.id, link);
      addUnique(durableByNode, link.externalNodeId, task, "GitHub node id");
      addUnique(durableByNumber, link.externalNumber, task, "GitHub issue number");
      continue;
    }
    const quarantined = repoLinks.find((candidate) => !isActiveLink(candidate));
    if (quarantined) {
      quarantinedTaskIds.add(task.id);
      if (quarantined.externalNumber == null) projectWideQuarantines.push(task);
      // CandidateNumber can be null for malformed provenance. A visible legacy prefix may still
      // correlate it to an incoming issue for the sole purpose of refusing all automation.
      const candidateNumber = quarantined.externalNumber ?? issueNumberFromTaskName(task.name);
      if (candidateNumber != null) {
        const group = quarantineByNumber.get(candidateNumber) || [];
        group.push(task);
        quarantineByNumber.set(candidateNumber, group);
      }
      continue;
    }
    // Legacy title parsing is deliberately the fallback, never a competitor to structured data.
    const legacyNumber = issueNumberFromTaskName(task.name);
    addUnique(legacyByNumber, legacyNumber, task, "legacy issue prefix");
  }

  return {
    byId, durableByNode, durableByNumber, durableLinkByTaskId,
    legacyByNumber, quarantineByNumber, foreignLinkByNumber,
    durableTaskIds, quarantinedTaskIds, projectWideQuarantines, foreignLinkedTaskIds,
    operationTasks,
  };
}

function taskMatchForIssue(index, issue) {
  const byNode = issue.id ? index.durableByNode.get(issue.id) : null;
  const byNumber = index.durableByNumber.get(issue.number);
  if (byNode && byNumber && byNode.id !== byNumber.id) {
    return {
      task: null,
      kind: "identity-mismatch",
      conflictingTasks: [byNode, byNumber],
      reason: `GitHub node ${issue.id} and issue number #${issue.number} are claimed by different tasks`,
    };
  }
  if (byNode) {
    // Issue transfers can change repository-local numbers. The immutable node is canonical and
    // the number/URL are refreshable metadata, never a reason to fork a second task.
    return { task: byNode, kind: "durable" };
  }
  if (byNumber) {
    const stored = index.durableLinkByTaskId.get(byNumber.id);
    // Number fallback exists for compatibility with an old structured row that has no node id.
    // Once a node id is recorded it is authoritative: a matching mutable/repo-local number must
    // never override a different immutable issue identity.
    if (issue.id && stored?.externalNodeId && stored.externalNodeId !== issue.id) {
      return {
        task: null,
        kind: "identity-mismatch",
        conflictingTasks: [byNumber],
        reason: `task ${byNumber.id} links #${issue.number} to GitHub node ${stored.externalNodeId}, not ${issue.id}`,
      };
    }
    return { task: byNumber, kind: "durable" };
  }
  const operationTaskId = index.operationTasks.get(issue.id) ?? index.operationTasks.get(issue.number);
  if (operationTaskId != null) {
    const task = index.byId.get(operationTaskId);
    if (!task) throw new Error(`push marker targets task ${operationTaskId}, which is not in this project`);
    return { task, kind: "operation" };
  }
  const quarantined = index.quarantineByNumber.get(issue.number);
  if (quarantined?.length) {
    return { task: quarantined[0], kind: "quarantine", quarantinedTasks: quarantined };
  }
  if (index.projectWideQuarantines.length) {
    return {
      task: index.projectWideQuarantines[0],
      kind: "quarantine",
      quarantinedTasks: index.projectWideQuarantines,
      projectWide: true,
    };
  }
  const foreign = index.foreignLinkByNumber.get(issue.number);
  if (foreign?.length) {
    return { task: foreign[0], kind: "foreign-link", foreignTasks: foreign };
  }
  const legacy = index.legacyByNumber.get(issue.number);
  return legacy ? { task: legacy, kind: "legacy" } : { task: null, kind: null };
}

function verifiedLinkRequest(taskId, repoIdentity, issue, actor) {
  return {
    taskId,
    provider: "GitHub",
    repositoryExternalId: repoIdentity.externalId,
    repositoryOwner: repoIdentity.owner,
    repositoryName: repoIdentity.name,
    externalNodeId: issue.node_id || issue.id,
    externalNumber: issue.number,
    url: issue.html_url || issue.url,
    actor,
  };
}

function operationTaskDisposition(stateValue, taskExists, completedResultMatches = true) {
  const state = writeStateName(stateValue);
  if (state === "Completed") {
    if (!completedResultMatches) return "block";
    return taskExists ? "map" : "project-new-task";
  }
  return taskExists ? "continue" : "block";
}

function completedOperationMatchesIssue(operation, issue) {
  const recordedNode = String(operation?.resultingExternalNodeId || "").trim();
  const recordedNumber = operation?.resultingExternalNumber;
  if (!recordedNode && recordedNumber == null) return false;
  return recordedNode ? recordedNode === issue.id : recordedNumber === issue.number;
}

function markerOperationCanFinalize(stateValue, dispatchedAt) {
  const state = writeStateName(stateValue);
  return state === "Dispatched"
    || state === "NeedsReview"
    || state === "ManualRetryAuthorized"
    || (state === "Prepared" && Boolean(dispatchedAt));
}

function globalNodeLinkDisposition(lookup, currentProjectId, index) {
  if (!lookup) return { kind: "none" };
  const link = lookup.link;
  if (!link?.taskId || lookup.projectId == null) {
    return { kind: "invalid", reason: "global node lookup omitted link.taskId or projectId" };
  }
  if (lookup.projectId !== currentProjectId) {
    return { kind: "cross-project", link, projectId: lookup.projectId };
  }
  const task = index.byId.get(link.taskId);
  if (!task) {
    return {
      kind: "invalid",
      link,
      projectId: lookup.projectId,
      reason: `global node lookup points to task ${link.taskId}, which is absent from current project ${currentProjectId}`,
    };
  }
  return { kind: "local-transfer", task, link, projectId: lookup.projectId };
}

function operationMarkerRepositoryDisposition(operation, repoIdentity, issue) {
  if (operation.repositoryExternalId === repoIdentity.externalId) return "same-repository";
  return writeStateName(operation.state) === "Completed"
    && Boolean(operation.resultingExternalNodeId)
    && operation.resultingExternalNodeId === issue.id
    ? "completed-node-transfer"
    : "block";
}

async function reconcilePushMarkers(repo, repoIdentity, issues, tasks, args) {
  const byOperation = new Map();
  const blockedIssues = new Set();
  const block = (issue, reason) => {
    blockedIssues.add(issue.id);
    blockedIssues.add(issue.number);
    stats.drift.push(`${repo}#${issue.number}: ${reason}; sync skipped task creation and every mutation`);
  };
  for (const issue of issues) {
    const marker = issuePushMarkerDisposition(issue.body);
    if (marker.kind === "none") continue;
    if (marker.kind === "ambiguous") {
      const distinct = new Set(marker.rawOperationIds);
      block(issue, `contains ${marker.rawOperationIds.length} raw push-marker token(s) across ${distinct.size} operation id(s), but not exactly one clean protocol marker`);
      continue;
    }
    const list = byOperation.get(marker.operationId) || [];
    list.push(issue);
    byOperation.set(marker.operationId, list);
  }

  const operationTasks = new Map();
  const finalizedLinks = [];
  const taskIds = new Set(tasks.map((task) => task.id));
  for (const [operationId, markedIssues] of byOperation) {
    if (markedIssues.length !== 1) {
      for (const issue of markedIssues) block(issue, `operation ${operationId} appears on ${markedIssues.length} fetched issues`);
      continue;
    }
    const issue = markedIssues[0];
    let operation;
    try {
      operation = operationFromLease(await externalApi.getOperation(operationId));
    } catch (err) {
      if (!/->\s*404\b/.test(err.message)) throw err;
      block(issue, `references operation ${operationId}, which is unknown to this board database`);
      continue;
    }
    if (!operation) {
      block(issue, `references unknown operation ${operationId}`);
      continue;
    }
    const repositoryDisposition = operationMarkerRepositoryDisposition(operation, repoIdentity, issue);
    if (repositoryDisposition === "block") {
      block(issue, `marker ${operationId} belongs to another repository node`);
      continue;
    }
    const state = writeStateName(operation.state);
    if (state === "Completed") {
      const same = completedOperationMatchesIssue(operation, issue);
      const taskDisposition = operationTaskDisposition(state, taskIds.has(operation.taskId), same);
      if (taskDisposition === "block") {
        block(issue, `completed operation ${operationId} points at a different GitHub issue`);
        continue;
      }
      if (taskDisposition === "project-new-task") {
        // Journal rows deliberately survive task deletion. A completed remote issue is still
        // valid GitHub truth; with its old task/link gone, normal projection may create and link
        // one replacement task instead of blocking the issue forever.
        continue;
      }
    } else if (markerOperationCanFinalize(state, operation.dispatchedAt)) {
      if (operationTaskDisposition(state, taskIds.has(operation.taskId)) === "block") {
        block(issue, `nonterminal operation ${operationId} targets missing task ${operation.taskId}`);
        continue;
      }
      // The fetched issue is not sufficient proof by itself: scan all issue pages so a copied
      // marker cannot make sync finalize the first of two claimants.
      const scan = await findIssueByPushMarker(repo, operationId);
      const markerDisposition = exhaustiveMarkerDisposition(
        scan.complete,
        scan.matches.length,
        scan.rawTargetOccurrenceCount,
      );
      if (markerDisposition !== "one") {
        block(issue, `operation ${operationId} authoritative scan found ${scan.matches.length} clean match(es) and ${scan.rawTargetOccurrenceCount} raw target occurrence(s)`);
        continue;
      }
      const verified = scan.matches[0];
      const same = verified.node_id === issue.id && verified.number === issue.number;
      if (!same) {
        block(issue, `operation ${operationId} fetched issue does not match the authoritative marker result`);
        continue;
      }
      if (!args.dryRun) {
        const link = await finalizeVerifiedIssue(operation, null, verified, repoIdentity, args.actor);
        finalizedLinks.push(link);
      }
    } else {
      if (!taskIds.has(operation.taskId)) {
        block(issue, `nonterminal operation ${operationId} targets missing task ${operation.taskId}`);
        continue;
      }
      block(issue, `operation ${operationId} is ${state}; remote marker requires human review`);
      continue;
    }

    operationTasks.set(issue.id, operation.taskId);
    operationTasks.set(issue.number, operation.taskId);
  }
  return { operationTasks, finalizedLinks, blockedIssues };
}

async function syncTasks(repo, repoIdentity, projectId, issues, closedNumbers, prByIssue, featureIds, args, existing, projectLinks, operationTasks, blockedIssues = new Set()) {
  const { actor, dryRun } = args;
  const index = buildTaskIdentityIndex(existing, repoIdentity, projectLinks, operationTasks);
  const byIssueNumber = new Map([...index.legacyByNumber, ...index.durableByNumber]);

  for (const issue of issues) {
    if (blockedIssues.has(issue.id) || blockedIssues.has(issue.number)) continue;
    const linkedPr = prByIssue.get(issue.number) || null;
    const desiredPriority = derivePriority(issue);
    const desiredStatus = deriveStatus(issue, linkedPr);
    const featureName = featureNameFor(issue);
    const featureId = featureIds.get(featureName);
    let matched = taskMatchForIssue(index, issue);
    // A repository transfer can change repo node + issue number/URL while the issue node remains
    // immutable. Consult the global node owner before trusting a fallback or creating anything.
    // Cross-project relocation is deliberately a human operation; sync never moves a task.
    if (!index.durableByNode.has(issue.id)) {
      const transfer = globalNodeLinkDisposition(
        await externalApi.findGitHubLinkByNode(issue.id),
        projectId,
        index,
      );
      if (transfer.kind === "cross-project") {
        stats.drift.push(`${repo}#${issue.number}: immutable GitHub node ${issue.id} is already linked to task ${transfer.link.taskId} in project ${transfer.projectId}; issue transfer requires an explicit human move/link decision, so sync made zero mutations and did not create a task`);
        continue;
      }
      if (transfer.kind === "invalid") {
        stats.drift.push(`${repo}#${issue.number}: ${transfer.reason}; sync made zero mutations and did not create a task`);
        continue;
      }
      if (transfer.kind === "local-transfer") {
        matched = { task: transfer.task, kind: "transfer", link: transfer.link };
      }
    }
    let task = matched.task;
    if (matched.kind === "identity-mismatch") {
      stats.drift.push(`${repo}#${issue.number}: ${matched.reason}; sync skipped every mutation pending human repair`);
      continue;
    }
    if (matched.kind === "quarantine") {
      const taskIds = matched.quarantinedTasks.map((candidate) => candidate.id).join(", ");
      const scope = matched.projectWide
        ? "null-number quarantine cannot be correlated safely; unmatched issue projection is blocked project-wide"
        : "quarantined link candidate";
      stats.drift.push(`${repo}#${issue.number}: ${scope} on task(s) ${taskIds}; sync skipped every mutation pending human review`);
      continue;
    }
    if (matched.kind === "foreign-link") {
      const taskIds = matched.foreignTasks.map((candidate) => candidate.id).join(", ");
      stats.drift.push(`${repo}#${issue.number}: task(s) ${taskIds} carry a GitHub link for a different repository; sync skipped every mutation`);
      continue;
    }
    const durableIdentity = matched.kind === "durable" || matched.kind === "operation" || matched.kind === "transfer";
    const desiredName = durableIdentity ? String(issue.title || "").trim() : taskNameFor(issue);
    if (task) byIssueNumber.set(issue.number, task);

    if (!task) {
      const safeInitialName = taskNameFor(issue);
      plan(dryRun, "create task", `${safeInitialName}  [${desiredStatus}/${desiredPriority}]`);
      if (dryRun) {
        plan(true, "register durable link", `${repo}#${issue.number} → new task, then render plain title "${String(issue.title || "").trim()}"`);
        if (args.comments) await syncComments(issue, linkedPr, null, args);
        continue;
      }
      if (featureId == null) throw new Error(`No feature id for "${featureName}" (issue #${issue.number})`);
      const created = await api("POST", `/api/features/${featureId}/tasks`, {
        // The legacy prefix is a crash-safe temporary fallback until the link insert succeeds.
        name: safeInitialName,
        description: descriptionFor(issue, linkedPr),
        status: desiredStatus,
        priority: desiredPriority,
        actor,
      });
      stats.tasksCreated++;
      const registered = await externalApi.registerLink(created.id, verifiedLinkRequest(created.id, repoIdentity, issue, actor));
      projectLinks.push(registered);
      stats.linksRegistered++;
      await api("PUT", `/api/tasks/${created.id}`, {
        name: String(issue.title || "").trim(),
        actor,
      });
      created.name = String(issue.title || "").trim();
      if (args.comments) await syncComments(issue, linkedPr, created.id, args);
      continue;
    }

    if (matched.kind === "durable" || matched.kind === "transfer") {
      const currentLink = matched.kind === "transfer"
        ? matched.link
        : activeGithubLink(task, repoIdentity.externalId, projectLinks);
      const metadataDrift = currentLink && (
        currentLink.repositoryExternalId !== repoIdentity.externalId
        || currentLink.repositoryOwner !== repoIdentity.owner
        || currentLink.repositoryName !== repoIdentity.name
        || currentLink.externalNodeId !== issue.id
        || currentLink.externalNumber !== issue.number
        || currentLink.url !== issue.url
      );
      if (metadataDrift) {
        const verb = currentLink.repositoryExternalId === repoIdentity.externalId
          ? "refresh durable link metadata"
          : "refresh transferred issue repository metadata";
        plan(dryRun, verb, `task ${task.id} → ${repo}#${issue.number}`);
        if (!dryRun) {
          const refreshed = await externalApi.registerLink(task.id, verifiedLinkRequest(task.id, repoIdentity, issue, actor));
          const indexInLinks = projectLinks.findIndex((candidate) => candidate.id === refreshed.id);
          if (indexInLinks >= 0) projectLinks[indexInLinks] = refreshed;
          else projectLinks.push(refreshed);
          stats.linksRegistered++;
        }
      }
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

async function syncRepo(repo, args, { header = true, repoIdentity: suppliedRepoIdentity = null } = {}) {
  if (header) console.log(`\n${repo}`);
  const [openIssues, closed, prs, repoIdentity] = await Promise.all([
    fetchOpenIssues(repo),
    fetchClosedIssues(repo, args.includeClosed),
    fetchOpenPrs(repo, args.comments),
    suppliedRepoIdentity || fetchRepositoryIdentity(repo),
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

  const projectId = await ensureProject(repo, repoIdentity, args.dryRun);
  const featureNames = [...new Set(issues.map(featureNameFor))].sort();
  const featureIds = await ensureFeatures(projectId, featureNames, args.dryRun);
  const [existingTasks, projectLinks] = projectId == null
    ? [[], []]
    : await Promise.all([
      api("GET", `/api/projects/${projectId}/tasks`),
      externalApi.listProjectLinks(projectId),
    ]);
  const reconciliation = projectId == null
    ? { operationTasks: new Map(), finalizedLinks: [], blockedIssues: new Set() }
    : await reconcilePushMarkers(repo, repoIdentity, issues, existingTasks, args);
  projectLinks.push(...reconciliation.finalizedLinks);
  await syncTasks(
    repo, repoIdentity, projectId, issues, closedNumbers, prByIssue, featureIds, args,
    existingTasks, projectLinks, reconciliation.operationTasks, reconciliation.blockedIssues,
  );
}

// --- onboard ------------------------------------------------------------------------------
//
// Register a repo into the portfolio: make the repo<->project link DURABLE, then backfill
// everything (milestones -> features, issues -> tasks, labels -> priority, comments).
// Idempotent in both halves — re-running links nothing and creates nothing.

async function linkProject(repo, repoIdentity, args) {
  const projects = await api("GET", "/api/projects");
  const link = resolveProjectLink(repo, projects, repoIdentity);
  if (link.conflict) console.warn(`  ! ${link.conflict}`);
  if (link.hardConflict) throw new Error(link.conflict);

  if (link.linkedBy === "node" && !link.needsRefresh) {
    console.log(`          already linked → project #${link.project.id} "${link.project.name}"`);
    return link.project.id;
  }

  if (link.project) {
    // Adopting a project the old name-matching found, and making that link permanent. This is
    // the upgrade path for every project synced before the marker existed.
    plan(args.dryRun, "link project", `#${link.project.id} "${link.project.name}" ← ${repo}  (name match → durable marker)`);
    if (args.dryRun) return link.project.id;
    const description = refreshedProjectDescription(link.project, repo, repoIdentity);
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
  const repoIdentity = await fetchRepositoryIdentity(repo);
  await linkProject(repo, repoIdentity, args);
  await syncRepo(repo, args, { header: false, repoIdentity });
}

// --- push-issue (preview by default; explicit single-task live mode) ------------------------

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
  const live = args.allowWrite && !args.dryRun;
  console.log(`\n${repo}  (push-issue — ${live ? "LIVE additive create" : "PREVIEW, zero writes"})`);
  const [projects, repoIdentity] = await Promise.all([
    api("GET", "/api/projects"),
    fetchRepositoryIdentity(repo),
  ]);
  const link = resolveProjectLink(repo, projects, repoIdentity);
  if (link.conflict) console.warn(`  ! ${link.conflict}`);
  assertLivePushProjectLink(link, live);
  if (!link.project) {
    throw new Error(`${repo} has no project on the board. Run: node scripts/portfolio-sync.mjs onboard ${repo}`);
  }
  const projectId = link.project.id;

  const [tasks, projectLinks] = await Promise.all([
    api("GET", `/api/projects/${projectId}/tasks`),
    externalApi.listProjectLinks(projectId),
  ]);
  // Resume is operation-driven, never re-planned from mutable task text/current repo labels.
  // This must run before the active/quarantine/#n candidate short-circuits below: a user may
  // rename an in-flight task arbitrarily and the operation UUID still has to recover it.
  if (args.resume) {
    const chosen = tasks.find((task) => task.id === args.task);
    if (!chosen) throw new Error(`task ${args.task} is not in project #${projectId} "${link.project.name}"`);
    const existingOperation = operationFromLease(await externalApi.getOperation(args.resume));
    validateOperationTarget(existingOperation, repoIdentity, chosen.id);
    if (writeStateName(existingOperation.state) === "Completed") {
      console.log(`  operation ${args.resume} is already Completed → ${existingOperation.resultingUrl}`);
      return;
    }

    if (args.authorizeAmbiguousRetry) {
      // This is deliberately lease-free: authorizing an ambiguous retry must be a separate,
      // auditable operator action. It reads GitHub exhaustively, then exits without reaching
      // ResumeAsync or any GitHub mutation/create primitive.
      const { scan, disposition } = await scanForExplicitRetryDecision(repo, existingOperation.operationId);
      if (disposition === "one") {
        const issue = scan.matches[0];
        await finalizeVerifiedIssue(existingOperation, null, issue, repoIdentity, args.actor);
        console.log(`  operation ${existingOperation.operationId}: exhaustive scan found ${issue.html_url}; finalized instead of authorizing a retry.`);
        return;
      }
      if (disposition !== "zero") {
        throw new Error(`operation ${existingOperation.operationId}: exhaustive scan was ${disposition} with ${scan.matches.length} clean match(es) and ${scan.rawTargetOccurrenceCount} raw target occurrence(s); retry authorization is blocked.`);
      }
      if (!Number.isInteger(existingOperation.attemptCount) || existingOperation.attemptCount <= 0) {
        throw new Error(`operation ${existingOperation.operationId}: API omitted a valid attemptCount; retry authorization is blocked.`);
      }
      const authorized = operationFromLease(await externalApi.authorizeAmbiguousRetry(
        existingOperation.operationId,
        {
          confirmationOperationId: args.authorizeAmbiguousRetry,
          expectedAttemptCount: existingOperation.attemptCount,
          scanComplete: true,
          observedMatchCount: 0,
          observedRawTargetOccurrenceCount: scan.rawTargetOccurrenceCount,
          scanCompletedAt: new Date().toISOString(),
          actor: args.actor,
          reason: args.resolutionReason,
        },
      ));
      validateOperationTarget(authorized, repoIdentity, chosen.id);
      if (writeStateName(authorized.state) !== "ManualRetryAuthorized") {
        throw new Error(`operation ${authorized.operationId}: authorization returned unexpected state ${writeStateName(authorized.state)}`);
      }
      console.log(`  operation ${authorized.operationId}: ManualRetryAuthorized after a complete zero-match scan. No GitHub create was attempted.`);
      console.log(`  Re-run a separate normal --resume ${authorized.operationId}; it will scan every issue again before any create.`);
      return;
    }

    if (writeStateName(existingOperation.state) === "Prepared" && operationRequiresFreshPrecreateScan(existingOperation)) {
      // Defensive compatibility only: an older Prepared row that retains dispatch history may
      // proof-finalize a late marker, but it cannot acquire a replacement-create lease.
      const { scan, disposition } = await scanForExplicitRetryDecision(repo, existingOperation.operationId);
      if (disposition === "one") {
        const issue = scan.matches[0];
        await finalizeVerifiedIssue(existingOperation, null, issue, repoIdentity, args.actor);
        console.log(`  operation ${existingOperation.operationId}: compatibility scan found late issue ${issue.html_url}; finalized without creating.`);
        return;
      }
      throw new Error(`operation ${existingOperation.operationId}: Prepared with prior dispatch history is a compatibility-only proof-finalization state; scan was ${disposition}, so no replacement create is permitted.`);
    }

    if (writeStateName(existingOperation.state) === "ManualRetryAuthorized") {
      // Claim/increment the durable attempt first. The exhaustive scan must finish after the
      // returned LastAttemptAt, otherwise old negative evidence could be replayed under a newer
      // lease. The service independently enforces the same ordering at dispatch.
      const leaseResult = await externalApi.resume(args.resume);
      const resumed = operationFromLease(leaseResult);
      const leaseToken = leaseTokenFrom(leaseResult);
      validateOperationTarget(resumed, repoIdentity, chosen.id);
      if (writeStateName(resumed.state) === "Completed") {
        console.log(`  operation ${resumed.operationId} completed concurrently → ${resumed.resultingUrl}`);
        return;
      }
      if (writeStateName(resumed.state) !== "ManualRetryAuthorized" || !leaseResult.safeToCreate || !leaseToken) {
        throw new Error(`operation ${resumed.operationId}: Resume did not return a leased ManualRetryAuthorized attempt; no GitHub scan/create was attempted.`);
      }

      let scanResult;
      try {
        scanResult = await scanForExplicitRetryDecision(repo, resumed.operationId);
      } catch (err) {
        await recordAmbiguousWriteFailure(resumed.operationId, leaseToken, `Post-Resume exhaustive marker scan failed: ${err.message}`)
          .catch((recordErr) => console.error(`  ! could not persist NeedsReview: ${recordErr.message}`));
        throw err;
      }
      const { scan, disposition } = scanResult;
      if (disposition === "one") {
        const issue = scan.matches[0];
        await finalizeVerifiedIssue(resumed, leaseToken, issue, repoIdentity, args.actor);
        console.log(`  operation ${resumed.operationId}: post-Resume exhaustive scan found late issue ${issue.html_url}; finalized without creating.`);
        return;
      }
      if (disposition !== "zero") {
        const reason = `Post-Resume exhaustive scan was ${disposition} with ${scan.matches.length} clean match(es) and ${scan.rawTargetOccurrenceCount} raw target occurrence(s); no create was attempted.`;
        await recordAmbiguousWriteFailure(resumed.operationId, leaseToken, reason)
          .catch((recordErr) => console.error(`  ! could not persist NeedsReview: ${recordErr.message}`));
        throw new Error(`operation ${resumed.operationId}: ${reason}`);
      }

      const executionArgs = {
        ...args,
        freshZeroScanEvidence: {
          confirmationOperationId: resumed.operationId,
          scanComplete: true,
          observedMatchCount: 0,
          observedRawTargetOccurrenceCount: scan.rawTargetOccurrenceCount,
          scanCompletedAt: new Date().toISOString(),
        },
      };
      console.log(`  operation ${resumed.operationId}: leased attempt ${resumed.attemptCount}, then exhaustive scan found zero target occurrences; proceeding to evidence-bound dispatch.`);
      await executePreparedOperation(repo, leaseResult, repoIdentity, executionArgs);
      return;
    }

    const leaseResult = await externalApi.resume(args.resume);
    const resumed = operationFromLease(leaseResult);
    validateOperationTarget(resumed, repoIdentity, chosen.id);
    const disposition = recoveryDisposition(resumed.state, leaseResult.safeToCreate, true, 0);
    if (disposition === "create") {
      await executePreparedOperation(repo, leaseResult, repoIdentity, args);
    } else if (writeStateName(resumed.state) === "Dispatched" || writeStateName(resumed.state) === "NeedsReview") {
      await recoverAmbiguousOperation(repo, resumed, leaseTokenFrom(leaseResult), repoIdentity, args);
    } else {
      throw new Error(`operation ${resumed.operationId} resumed in unsupported state ${writeStateName(resumed.state)}`);
    }
    return;
  }

  const hasAnyGithubState = (task) => githubLinksOf(task, projectLinks).length > 0;
  let candidates = tasks.filter((task) =>
    !hasAnyGithubState(task) && issueNumberFromTaskName(task.name) === null);

  if (args.task !== null) {
    const chosen = tasks.find((t) => t.id === args.task);
    if (!chosen) throw new Error(`task ${args.task} is not in project #${projectId} "${link.project.name}"`);
    const linkState = githubLinksOf(chosen, projectLinks)[0];
    if (linkState) {
      console.log(`  task ${chosen.id} already has ${isActiveLink(linkState) ? "an active" : "a quarantined"} GitHub link — nothing to create.`);
      return;
    }
    const already = issueNumberFromTaskName(chosen.name);
    if (already !== null) {
      console.log(`  task ${chosen.id} is already mirrored as #${already} — nothing to preview.`);
      return;
    }
    candidates = [chosen];
  } else if (!args.all) {
    // Neither --task nor --all: list what COULD be pushed and stop.
    console.log(`  ${candidates.length} task(s) have no durable link or legacy "#<n> " fallback and would become issues:`);
    for (const t of candidates.slice(0, 50)) console.log(`    ${String(t.id).padStart(5)}  [${t.status}/${t.priority}]  ${t.name}`);
    if (candidates.length > 50) console.log(`    … and ${candidates.length - 50} more`);
    console.log(`\n  Choose: --task <id> for one, --all for every one.`);
    return;
  }

  if (live && (args.task === null || args.all)) {
    throw new Error("Live GitHub creation is intentionally single-task only: use --allow-write --task <id>. --all is preview-only.");
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
    const request = { title, body, milestoneTitle: milestone, label };

    console.log(`  ${live ? "creating" : "would create"} issue on ${repo} ← task ${task.id}`);
    console.log(`      title:     ${title}`);
    console.log(`      milestone: ${milestone ?? "(none)"}${featureName !== UNSORTED_FEATURE && !milestone ? `   ← feature "${featureName}" has no milestone on ${repo}; one would NOT be created` : ""}`);
    console.log(`      labels:    ${label ?? "(none)"}`);
    console.log(`      body:`);
    for (const bodyLine of body.split("\n")) console.log(`      │ ${bodyLine}`);
    if (task.priority !== "Medium" && !label) {
      console.warn(`      ! no label on ${repo} maps to priority ${task.priority} — a sync would read this issue back as Medium.`);
    }
    stats.issuesPreviewed++;

    if (!live) continue;
    await hooks.beforePrepare({ taskId: task.id, repository: repo });
    const leaseResult = await externalApi.prepare(task.id, {
      taskId: task.id,
      provider: "GitHub",
      repositoryExternalId: repoIdentity.externalId,
      repositoryOwner: repoIdentity.owner,
      repositoryName: repoIdentity.name,
      title: request.title,
      body: request.body,
      milestoneTitle: request.milestoneTitle,
      label: request.label,
      expectedTaskUpdatedAt: task.updatedAt,
    });
    await executePreparedOperation(repo, leaseResult, repoIdentity, args);
  }

  if (!live) console.log(`\n  Preview complete. No GitHub or KanbanFlow writes were made.`);
}

// --- durable-link backfill ---------------------------------------------------------------

const SYNC_BLOCK_RE = /<!--\s*kanbanflow-sync\s*-->([\s\S]*?)<!--\s*\/kanbanflow-sync\s*-->/gi;
const SYNC_ISSUE_LINE_RE = /^\s*\*\*Issue:\*\*\s+(\S+)\s*$/gmi;

function stripLegacyIssuePrefix(name) {
  return String(name || "").replace(/^#\d+\s+/, "").trim();
}

function malformedProvenanceCandidate(description, legacyNumber) {
  const canonical = [];
  for (const match of String(description || "").matchAll(new RegExp(SYNC_ISSUE_LINE_RE.source, "gmi"))) {
    const parsed = parseGitHubIssueUrl(match[1]);
    if (!parsed) continue;
    const key = `${parsed.owner.toLowerCase()}/${parsed.repo.toLowerCase()}#${parsed.number}`;
    if (!canonical.some((candidate) => candidate.key === key)) canonical.push({ ...parsed, key });
  }
  if (canonical.length === 1) {
    const candidate = canonical[0];
    return {
      candidateNumber: candidate.number,
      candidateUrl: `https://github.com/${candidate.owner}/${candidate.repo}/issues/${candidate.number}`,
      canonicalCount: 1,
    };
  }
  return {
    candidateNumber: canonical.length === 0 ? legacyNumber : null,
    candidateUrl: null,
    canonicalCount: canonical.length,
  };
}

function classifyBackfillCandidate(task) {
  const description = String(task?.description || "");
  const blocks = [...description.matchAll(new RegExp(SYNC_BLOCK_RE.source, "gi"))];
  const legacyNumber = issueNumberFromTaskName(task?.name);
  if (blocks.length === 0) {
    const { canonicalCount, ...candidate } = malformedProvenanceCandidate(description, legacyNumber);
    if (legacyNumber === null && canonicalCount === 0) return { kind: "none" };
    return {
      kind: "quarantine",
      ...candidate,
      reason: canonicalCount > 0
        ? `malformed provenance: found ${canonicalCount} canonical **Issue:** URL(s) outside a kanbanflow-sync block`
        : "title-only #n candidate has no authoritative kanbanflow-sync issue URL",
    };
  }
  if (blocks.length !== 1) {
    const { canonicalCount: _canonicalCount, ...candidate } = malformedProvenanceCandidate(description, legacyNumber);
    return {
      kind: "quarantine",
      ...candidate,
      reason: `malformed provenance: found ${blocks.length} kanbanflow-sync blocks`,
    };
  }

  const issueLines = [...blocks[0][1].matchAll(new RegExp(SYNC_ISSUE_LINE_RE.source, "gmi"))];
  if (issueLines.length !== 1) {
    const { canonicalCount: _canonicalCount, ...candidate } = malformedProvenanceCandidate(description, legacyNumber);
    return {
      kind: "quarantine",
      ...candidate,
      reason: `malformed provenance: expected one **Issue:** URL, found ${issueLines.length}`,
    };
  }
  const parsed = parseGitHubIssueUrl(issueLines[0][1]);
  if (!parsed) {
    return { kind: "quarantine", candidateNumber: legacyNumber, candidateUrl: null, reason: "malformed provenance: **Issue:** value is not a canonical GitHub issue URL" };
  }
  const canonicalUrl = `https://github.com/${parsed.owner}/${parsed.repo}/issues/${parsed.number}`;
  return { kind: "verify", ...parsed, candidateNumber: parsed.number, candidateUrl: canonicalUrl };
}

function planBackfill(tasks, projectLinks, repoIdentity) {
  const plans = [];
  const verifyGroups = new Map();
  const persistedClaims = new Map();
  for (const link of projectLinks) {
    if ((link.provider === 0 || String(link.provider).toLowerCase() === "github")
      && link.repositoryExternalId === repoIdentity.externalId
      && link.externalNumber != null) {
      const group = persistedClaims.get(link.externalNumber) || [];
      group.push(link);
      persistedClaims.set(link.externalNumber, group);
    }
  }
  for (const task of tasks) {
    const existing = githubLinksOf(task, projectLinks)[0];
    if (existing) {
      const plainName = isActiveLink(existing) ? stripLegacyIssuePrefix(task.name) : null;
      plans.push({
        task,
        kind: "existing",
        link: existing,
        plainName: plainName && plainName !== task.name ? plainName : null,
      });
      continue;
    }
    const candidate = classifyBackfillCandidate(task);
    const planItem = { task, ...candidate };
    if (candidate.kind === "verify") {
      const sameRepo = `${candidate.owner}/${candidate.repo}`.toLowerCase() === repoIdentity.fullName.toLowerCase();
      planItem.pathDrift = !sameRepo;
      const claims = persistedClaims.get(candidate.number) || [];
      if (claims.length > 0) {
        planItem.kind = "quarantine";
        planItem.reason = `issue candidate #${candidate.number} is already claimed by persisted ${claims.map((claim) => `${claim.state} task ${claim.taskId}`).join(", ")}`;
        plans.push(planItem);
        continue;
      }
      const key = `${repoIdentity.externalId}:${candidate.number}`;
      const group = verifyGroups.get(key) || [];
      group.push(planItem);
      verifyGroups.set(key, group);
    }
    plans.push(planItem);
  }

  for (const group of verifyGroups.values()) {
    if (group.length <= 1) continue;
    const ids = group.map((item) => item.task.id).join(", ");
    for (const item of group) {
      item.kind = "quarantine";
      item.reason = `duplicate authoritative issue candidate #${item.number} is claimed by tasks ${ids}`;
    }
  }
  return plans;
}

async function persistQuarantine(item, repoIdentity, args) {
  plan(args.dryRun, "quarantine link candidate", `task ${item.task.id}: ${item.reason}`);
  if (args.dryRun) return;
  await externalApi.quarantine(item.task.id, {
    taskId: item.task.id,
    provider: "GitHub",
    repositoryExternalId: repoIdentity.externalId,
    repositoryOwner: repoIdentity.owner,
    repositoryName: repoIdentity.name,
    candidateNumber: item.candidateNumber ?? null,
    candidateUrl: item.candidateUrl ?? null,
    reason: item.reason,
    actor: args.actor,
  });
  stats.linksQuarantined++;
}

async function backfillLinks(repo, args) {
  console.log(`\n${repo}  (backfill-links${args.dryRun ? " — DRY RUN" : ""})`);
  const [projects, repoIdentity] = await Promise.all([
    api("GET", "/api/projects"),
    fetchRepositoryIdentity(repo),
  ]);
  const projectLink = resolveProjectLink(repo, projects, repoIdentity);
  if (projectLink.conflict) throw new Error(projectLink.conflict);
  if (!projectLink.project) throw new Error(`${repo} has no linked board project; run onboard first`);

  const [tasks, projectLinks] = await Promise.all([
    api("GET", `/api/projects/${projectLink.project.id}/tasks`),
    externalApi.listProjectLinks(projectLink.project.id),
  ]);
  const plans = planBackfill(tasks, projectLinks, repoIdentity);

  for (const item of plans) {
    if (item.kind === "none") continue;
    if (item.kind === "existing") {
      console.log(`          task ${item.task.id}: already ${isActiveLink(item.link) ? "linked" : "quarantined"}`);
      if (item.plainName) {
        plan(args.dryRun, "render linked task name without legacy prefix", `task ${item.task.id}: "${item.plainName}"`);
        if (!args.dryRun) await api("PUT", `/api/tasks/${item.task.id}`, { name: item.plainName, actor: args.actor });
      }
      continue;
    }
    if (item.kind === "quarantine") {
      await persistQuarantine(item, repoIdentity, args);
      continue;
    }

    const candidateRepo = `${item.owner}/${item.repo}`;
    if (item.pathDrift) {
      let candidateRepoIdentity;
      try {
        candidateRepoIdentity = await fetchRepositoryIdentity(candidateRepo);
      } catch (err) {
        if (!/(?:HTTP\s+404|Not Found|status 404)/i.test(err.message)) throw err;
        await persistQuarantine({ ...item, reason: `authoritative sync URL repository ${candidateRepo} no longer resolves` }, repoIdentity, args);
        continue;
      }
      if (candidateRepoIdentity.externalId !== repoIdentity.externalId) {
        await persistQuarantine({
          ...item,
          reason: `authoritative sync URL belongs to different repository node ${candidateRepoIdentity.externalId}, not ${repoIdentity.externalId}`,
        }, repoIdentity, args);
        continue;
      }
      console.log(`          task ${item.task.id}: legacy URL ${candidateRepo} follows immutable node ${repoIdentity.externalId} to ${repoIdentity.fullName}`);
    }

    let issue;
    try {
      issue = await fetchIssueByNumber(candidateRepo, item.number);
    } catch (err) {
      if (!/(?:HTTP\s+404|Not Found|status 404)/i.test(err.message)) throw err;
      await persistQuarantine({
        ...item,
        reason: `authoritative sync URL did not resolve to an issue in ${repo}`,
      }, repoIdentity, args);
      continue;
    }

    if (issue.number !== item.number || !issue.node_id || issue.pull_request) {
      await persistQuarantine({ ...item, reason: "authoritative sync URL did not verify as a GitHub issue" }, repoIdentity, args);
      continue;
    }

    plan(args.dryRun, "register durable link", `task ${item.task.id} → ${repo}#${issue.number} (${issue.node_id})`);
    if (args.dryRun) continue;
    await externalApi.registerLink(item.task.id, verifiedLinkRequest(item.task.id, repoIdentity, issue, args.actor));
    stats.linksRegistered++;
    const plainName = stripLegacyIssuePrefix(item.task.name);
    if (plainName && plainName !== item.task.name) {
      await api("PUT", `/api/tasks/${item.task.id}`, { name: plainName, actor: args.actor });
    }
  }

  const actionable = plans.filter((item) => item.kind !== "none");
  console.log(`  ${actionable.length} candidate(s): ${plans.filter((x) => x.kind === "verify").length} verified/linkable · ${plans.filter((x) => x.kind === "quarantine").length} quarantined · ${plans.filter((x) => x.kind === "existing").length} already recorded`);
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

  const repoIdentity = await fetchRepositoryIdentity(repo);
  const link = resolveProjectLink(repo, projects, repoIdentity);
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

  const [tasks, projectLinks] = await Promise.all([
    api("GET", `/api/projects/${link.project.id}/tasks`),
    externalApi.listProjectLinks(link.project.id),
  ]);
  const index = buildTaskIdentityIndex(tasks, repoIdentity, projectLinks);
  const byIssueNumber = new Map([...index.legacyByNumber, ...index.durableByNumber]);
  for (const task of tasks) {
    const links = githubLinksOf(task, projectLinks);
    if (index.foreignLinkedTaskIds.has(task.id)) {
      rec.drift.push(`${repo}: task ${task.id} has a GitHub link for repository node ${links[0]?.repositoryExternalId}; structured identity blocks legacy fallback`);
      continue;
    }
    if (links.some((candidate) => !isActiveLink(candidate))) {
      rec.drift.push(`${repo}: task ${task.id} is quarantined for GitHub link review`);
      continue;
    }
    if (!index.durableTaskIds.has(task.id) && issueNumberFromTaskName(task.name) === null) {
      rec.unsynced.push({ id: task.id, name: task.name, status: task.status, priority: task.priority });
    }
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
    const expectedName = index.durableTaskIds.has(task.id)
      ? String(issue.title || "").trim()
      : taskNameFor(issue);
    if (task.name !== expectedName) titleLag++;
    if (task.priority !== derivePriority(issue)) priorityLag++;
    if ((task.featureName || "") !== featureNameFor(issue)) featureLag++;
    if (prByIssue.get(n) && ADVANCEABLE_TO_REVIEW.has(task.status) && task.status !== "Review") reviewLag++;
  }

  const openMatches = openIssues.map((issue) => ({ issue, match: taskMatchForIssue(index, issue) }));
  for (const { issue, match } of openMatches) {
    if (match.kind === "identity-mismatch") {
      rec.drift.push(`${repo}#${issue.number}: ${match.reason}; immutable identity requires human repair`);
    }
  }
  const missing = openMatches.filter(({ match }) => !match.task && match.kind !== "identity-mismatch").length;
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
    : `project #${rec.projectId} "${rec.projectName}"  (${rec.link === "node" ? "immutable repository node" : rec.link === "marker" ? "legacy path marker — run `onboard` to add node identity" : "name match only — run `onboard` to make it durable"})`;
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
  const notDurable = records.filter((r) => r.link !== "node").length;
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

function commandValidationError(args) {
  if (args.allowWrite && args.command !== "push-issue") return "--allow-write is valid only with push-issue.";
  if (args.authorizeAmbiguousRetry !== null) {
    if (!args.resume || args.authorizeAmbiguousRetry.toLowerCase() !== args.resume.toLowerCase()) {
      return "--authorize-ambiguous-retry must exactly repeat the UUID supplied to --resume.";
    }
    if (!args.actorExplicit) {
      return "--authorize-ambiguous-retry requires an explicit non-blank --actor <name>.";
    }
    if (!String(args.resolutionReason || "").trim()) {
      return "--authorize-ambiguous-retry requires --resolution-reason <text>.";
    }
  }
  if (args.resolutionReason !== null && args.authorizeAmbiguousRetry === null) {
    return "--resolution-reason is valid only with --authorize-ambiguous-retry.";
  }
  if (args.resume && (!args.allowWrite || args.command !== "push-issue" || args.task === null)) {
    return "--resume requires: push-issue <owner/repo> --allow-write --task <id> --resume <operation-uuid>.";
  }
  if (args.resume && args.dryRun) return "--resume performs recovery and cannot be combined with --dry-run.";
  if (args.command === "push-issue" && args.allowWrite && args.task === null) {
    return "Live push-issue requires --task <id>; bulk --all writes are intentionally forbidden.";
  }
  if (args.command === "push-issue" && args.allowWrite && args.all) {
    return "--all is preview-only and cannot be combined with --allow-write.";
  }
  return null;
}

const HELP = `portfolio-sync — GitHub <-> KanbanFlow. GitHub is the source of truth; the board is
the visualisation. Every command is idempotent and honours --dry-run.

  sync                        GitHub -> board mirror. Read-only on GitHub. THE DEFAULT.
  onboard <owner/repo>…       register a repo: link the Project durably, then backfill it.
  push-issue <owner/repo>     preview by default; one additive issue create only with the
                              explicit --allow-write --task <id> gate.
  backfill-links <owner/repo> verify legacy sync-block URLs and persist durable links;
                              ambiguous/title-only candidates are quarantined.
  status                      health check both sides.
                              exit 0 = clean and complete · 1 = drift · 2 = incomplete/error

common
  --dry-run              print exactly what WOULD be written, write nothing
  --repos a/b,c/d        override the repo list (bare "owner/repo" args work too)
  --actor <name>         actor recorded on the audit trail (default: portfolio-sync)

sync / onboard
  --include-closed       also mirror closed issues (as Done)
  --no-comments          skip GitHub comment mirroring (on by default)
  --max-comment-chars N  truncate each mirrored comment at N chars (default: no cap)

push-issue
  --task <id>            preview exactly this board task
  --all                  preview every task with no durable link or legacy "#<n> " fallback
  --limit N              cap how many --all previews in one run
  --allow-write          enable one live additive create; requires --task (alias: --push)
  --resume <uuid>        explicitly resume/reconcile one persisted operation; requires
                         --allow-write and --task. Dispatched/NeedsReview never auto-create.
  --authorize-ambiguous-retry <same uuid>
                         after a normal zero-match recovery reached NeedsReview, record an
                         operator-authorized retry and EXIT without creating. Must accompany
                         --resume plus explicit --actor and --resolution-reason. A later, separate
                         normal --resume scans every issue again before it may create.
  --resolution-reason T  required audit reason for --authorize-ambiguous-retry

status
  --strict               also exit 1 on pending work, unsynced tasks, or a non-durable link
  --json                 machine-readable report (includes the exit code and completeness)

testing only
  --internal-test-hooks  load the #336 crash/barrier harness named by KANBANFLOW_336_TEST_HOOKS
                         (scripts/test-hooks/336-barriers.mjs). BOTH are required — neither the
                         flag nor the environment variable arms anything alone — and combining
                         it with --allow-write additionally requires
                         KANBANFLOW_336_ALLOW_WRITE_HOOKS=1. Loading prints a banner on stderr.
                         Used by scripts/verify-336-github.ps1; never in a production run.

WRITE SURFACE ON GITHUB: one dedicated stdin-based "gh issue create" primitive, reachable only
through push-issue --allow-write --task. Generic gh() remains read-only. There is no close,
edit, delete, reword, re-label-after-create, comment, or assignment path.

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
  const validationError = commandValidationError(args);
  if (validationError) {
    console.error(`${validationError}\n\n${HELP}`);
    process.exitCode = 2;
    return;
  }
  // `sync` and `status` sweep the whole portfolio by design. onboard/push-issue act on ONE
  // repo at a time and must never fall back to the default six — silently onboarding or
  // pushing to six repos because a repo argument was forgotten is not a recoverable mistake.
  if ((args.command === "onboard" || args.command === "push-issue" || args.command === "backfill-links") && !args.repos?.length) {
    console.error(`\`${args.command}\` needs an explicit repo:\n  node scripts/portfolio-sync.mjs ${args.command} owner/repo`);
    process.exitCode = 2;
    return;
  }

  try {
    await installTestHooks(args);
  } catch (err) {
    console.error(`${err.message}

${HELP}`);
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

  const runners = { sync: syncRepo, onboard: onboardRepo, "push-issue": pushIssues, "backfill-links": backfillLinks };
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
    console.log(`issues created:   ${stats.issuesCreated}`);
    console.log(`links finalized:  ${stats.linksFinalized}`);
    return;
  }
  if (args.command === "backfill-links") {
    console.log(`links registered: ${stats.linksRegistered}`);
    console.log(`links quarantined:${String(stats.linksQuarantined).padStart(3)}`);
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
      console.log(`    Mirrored whole. Use --max-comment-chars N to cap it (agents reading this task over MCP pay for it).`);
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
function isDirectRun(metaUrl, argv1 = process.argv[1]) {
  if (!argv1) return false;
  const selfPath = fileURLToPath(metaUrl);
  return resolve(argv1) === resolve(selfPath)
    || resolvedEntry(argv1) === resolvedEntry(selfPath);
}

export {
  BODY_EXCERPT_LIMIT,
  MAX_DESCRIPTION_BYTES,
  activeGithubLink,
  analyzeMarkerRecords,
  assertFreshPrecreateScan,
  assertLivePushProjectLink,
  assertGhIssueCreateAllowed,
  assertReadOnly,
  buildGhIssueCreateArgs,
  buildExternalDispatchRequest,
  buildTaskIdentityIndex,
  classifyBackfillCandidate,
  completedOperationMatchesIssue,
  commandValidationError,
  commentTextFor,
  descriptionRefreshFor,
  descriptionFor,
  exactMarkerCount,
  exactMarkerMatches,
  excerptFor,
  exhaustiveMarkerDisposition,
  ghIssueCreate,
  globalNodeLinkDisposition,
  githubLinksOf,
  handleCreateWindowFailure,
  hooks,
  HookAbort,
  installTestHooks,
  isDirectRun,
  runManualResumeHook,
  issueBodyFor,
  issueHasOneCleanOperationMarker,
  issueNumberFromTaskName,
  issuePushMarkerDisposition,
  linkMetadataNeedsRefresh,
  markerOperationCanFinalize,
  operationTaskDisposition,
  operationRequiresFreshPrecreateScan,
  operationMarkerRepositoryDisposition,
  parseArgs,
  parseGitHubIssueUrl,
  planBackfill,
  pushMarker,
  pushOperationIds,
  rawPushOperationIds,
  recoveryDisposition,
  refreshedProjectDescription,
  repoLinkMarker,
  repoNodeLinkMarker,
  resolveProjectLink,
  stripLegacyIssuePrefix,
  taskMatchForIssue,
  verifiedFinalizeRequest,
  writeStateName,
};

if (isDirectRun(import.meta.url)) {
  main().catch((err) => {
    console.error(err);
    process.exit(1);
  });
}
