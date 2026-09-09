import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";

const FOOTER = "…(truncated — see the issue)";

async function load(limit, tag = "test") {
  if (limit === undefined) delete process.env.KANBANFLOW_BODY_EXCERPT_LIMIT;
  else process.env.KANBANFLOW_BODY_EXCERPT_LIMIT = String(limit);
  return import(`./portfolio-sync.mjs?${tag}-${limit ?? "default"}`);
}

test("a multi-byte body is capped by UTF-8 bytes", async () => {
  const { excerptFor } = await load(undefined, "multibyte");
  const budget = 160;
  const body = Array.from({ length: 12 }, (_, i) => `第${i}行：漢字漢字漢字`).join("\n");
  assert.ok(body.length < budget, "fixture must fit by UTF-16 units so .length would fail this test");
  const output = excerptFor(body, budget);
  assert.ok(Buffer.byteLength(output, "utf8") <= budget);
  assert.match(output, /truncated/);
});

test("--max-comment-chars truncates multi-byte comments by characters, not UTF-8 bytes", async () => {
  const { commentTextFor } = await load(undefined, "multibyte-comment-chars");
  const firstLine = "漢字漢字漢字漢字漢字";
  const comment = {
    ghId: "IC_multibyte",
    header: "**octocat** commented:",
    body: `${firstLine}\n${"続".repeat(60)}`,
  };
  const output = commentTextFor(comment, 50);
  const mirroredBody = output.slice(output.indexOf("\n\n") + 2);

  assert.equal(mirroredBody, `${firstLine}\n\n…(truncated — read it on GitHub)`);
  assert.ok(mirroredBody.length <= 50);
  assert.ok(Buffer.byteLength(mirroredBody, "utf8") > 50,
    "fixture must exceed the byte budget so reverting the call site to byte counting fails");
});

test("an ASCII body near the byte boundary is not truncated unnecessarily", async () => {
  const { excerptFor } = await load(undefined, "ascii");
  const body = "a".repeat(200);
  assert.equal(excerptFor(body, 200), body);
});

test("truncation inside a fenced code block closes the fence before the prose footer", async () => {
  const { excerptFor } = await load(undefined, "fence");
  const body = `intro\n\`\`\`js\n${"const value = 1;\n".repeat(30)}\`\`\``;
  const output = excerptFor(body, 180);
  const fences = output.match(/^```/gm) || [];
  assert.equal(fences.length % 2, 0, "backtick fences must be balanced");
  assert.ok(output.lastIndexOf("```") < output.lastIndexOf(FOOTER), "footer must follow the closing fence");
});

test("truncation inside a tilde-fenced code block closes the fence before the prose footer", async () => {
  const { excerptFor } = await load(undefined, "tilde-fence");
  const body = `intro\n~~~~js\n${"const value = 1;\n".repeat(30)}~~~~`;
  const output = excerptFor(body, 180);
  const fences = output.match(/^~~~~(?:js)?$/gm) || [];
  assert.equal(fences.length, 2, "tilde fence opener and generated closer must both be present");
  assert.ok(output.lastIndexOf("~~~~") < output.lastIndexOf(FOOTER), "footer must follow the tilde closer");
});

test("a closing fence cannot carry an info string", async () => {
  const { excerptFor } = await load(undefined, "invalid-fence-close");
  const body = `intro\n\`\`\`js\nkept\n\`\`\`not-a-close\n${"more code\n".repeat(30)}`;
  const output = excerptFor(body, 160);
  assert.match(output, /^```not-a-close$/m, "the invalid closer must be retained in the fixture excerpt");
  assert.match(output, /\n```\n\n…\(truncated — see the issue\)$/, "an actual closing fence must be added before the footer");
});

test("truncation outside a fence still cuts on a line boundary", async () => {
  const { excerptFor } = await load(undefined, "line");
  const body = Array.from({ length: 30 }, (_, i) => `whole line ${i}`).join("\n");
  const output = excerptFor(body, 150);
  const kept = output.slice(0, output.indexOf(`\n\n${FOOTER}`));
  assert.ok(body.split("\n").includes(kept.split("\n").at(-1)), "last retained line must be whole");
  assert.ok(!kept.endsWith("\n"));
});

test("the environment override is clamped at both ends and cannot exceed the safe maximum", async () => {
  const high = await load(Number.MAX_SAFE_INTEGER, "high-clamp");
  const low = await load(-100, "low-clamp");
  assert.equal(high.BODY_EXCERPT_LIMIT, high.MAX_DESCRIPTION_BYTES);
  assert.equal(low.BODY_EXCERPT_LIMIT, 1_024);
  assert.ok(high.BODY_EXCERPT_LIMIT < 32_768);
});

test("the complete mirrored description, including header and footer, stays within its frame budget", async () => {
  const { descriptionFor, BODY_EXCERPT_LIMIT } = await load(undefined, "description");
  const issue = {
    url: "https://github.com/owner/repository/issues/488",
    state: "OPEN",
    milestone: { title: "M1" },
    labels: [{ name: "bug" }],
    body: Array.from({ length: 4000 }, (_, i) => `line ${i} plain ASCII`).join("\n"),
  };
  const output = descriptionFor(issue, null);
  assert.ok(Buffer.byteLength(JSON.stringify(output), "utf8") <= BODY_EXCERPT_LIMIT);
  assert.match(output, /truncated/);
});

test("a newline-dense body is capped by its JSON-serialized size, not its raw size", async () => {
  const { descriptionFor, BODY_EXCERPT_LIMIT } = await load(undefined, "serialized-description");
  const issue = {
    url: "https://github.com/owner/repository/issues/489",
    state: "OPEN",
    labels: [],
    body: Array.from({ length: 14_000 }, () => "x").join("\n"),
  };
  assert.ok(Buffer.byteLength(issue.body, "utf8") < BODY_EXCERPT_LIMIT, "raw fixture must fit the old budget");
  assert.ok(Buffer.byteLength(JSON.stringify(issue.body), "utf8") > BODY_EXCERPT_LIMIT, "serialized fixture must exceed it");
  const output = descriptionFor(issue, null);
  assert.ok(Buffer.byteLength(JSON.stringify(output), "utf8") <= BODY_EXCERPT_LIMIT);
  assert.match(output, /truncated/);
});

test("an oversized provenance header is replaced by a bounded sync stub", async () => {
  const { descriptionFor, BODY_EXCERPT_LIMIT } = await load(undefined, "oversized-header");
  const issue = {
    url: "https://github.com/owner/repository/issues/490",
    state: "OPEN",
    labels: [{ name: "x".repeat(BODY_EXCERPT_LIMIT + 1_000) }],
    body: "",
  };
  const output = descriptionFor(issue, null);
  assert.ok(Buffer.byteLength(JSON.stringify(output), "utf8") <= BODY_EXCERPT_LIMIT);
  assert.match(output, /^<!-- kanbanflow-sync -->/);
  assert.match(output, /provenance omitted/);
});

const syncDescription = (body) => `<!-- kanbanflow-sync -->
**Issue:** https://github.com/owner/repository/issues/488
**State:** OPEN · **Milestone:** M1 · **Labels:** bug
**Open PR:** none
**Synced:** 2026-08-30T12:00:00.000Z
<!-- /kanbanflow-sync -->

${body}`;

test("a sync-shaped, still-truncated description is selected for refresh", async () => {
  const { descriptionRefreshFor } = await load(undefined, "refresh-truncated");
  const stored = syncDescription(`old excerpt\n\n${FOOTER}`);
  const regenerated = syncDescription("complete current body");
  assert.equal(descriptionRefreshFor(stored, regenerated), regenerated);
});

test("a sync-shaped description without the truncation marker is not refreshed", async () => {
  const { descriptionRefreshFor } = await load(undefined, "refresh-untruncated");
  assert.equal(descriptionRefreshFor(syncDescription("complete old body"), syncDescription("complete new body")), null);
});

test("a human-edited description with the sync header but no marker is never overwritten", async () => {
  const { descriptionRefreshFor } = await load(undefined, "refresh-human-edit");
  const humanEdit = syncDescription("Human rewrite: preserve this decision and its rationale.");
  assert.equal(descriptionRefreshFor(humanEdit, syncDescription("GitHub's current issue body")), null);
});

test("a truncated description that regenerates identically produces no write", async () => {
  const { descriptionRefreshFor } = await load(undefined, "refresh-identical");
  const identical = syncDescription(`same excerpt\n\n${FOOTER}`);
  assert.equal(descriptionRefreshFor(identical, identical), null);
});

test("a sync timestamp change alone does not rewrite a still-truncated description", async () => {
  const { descriptionRefreshFor } = await load(undefined, "refresh-timestamp-only");
  const stored = syncDescription(`same excerpt\n\n${FOOTER}`);
  const regenerated = stored.replace("2026-08-30T12:00:00.000Z", "2026-08-30T12:01:00.000Z");
  assert.equal(descriptionRefreshFor(stored, regenerated), null);
});

test("a non-sync description with copied truncation text is not refreshed", async () => {
  const { descriptionRefreshFor } = await load(undefined, "refresh-nonsync");
  assert.equal(descriptionRefreshFor(`human notes\n\n${FOOTER}`, syncDescription("new body")), null);
});

// --- argv guards: a flag must never be consumed as another flag's value -------------------
//
// The hazard is not a WRONG value, it is a LOST flag. `sync --max-comment-chars --dry-run`
// used to eat the --dry-run as the character count: `args.dryRun` stayed false, `sync` with no
// --repos fell through to all of DEFAULT_REPOS, and the operator who asked for a preview got a
// live mirror of six repositories. Every test below asserts the flag's SPECIFIC refusal — its
// own name and the swallowed token — never merely `error !== null`, because `--task` already
// produced an error by accident (its NaN trips the unrelated post-loop Number.isFinite check),
// so a vague assertion would stay green with the guard ripped back out.

const REFUSAL_TAIL =
  ` A flag is never a value: consuming one silently discards it, and a swallowed --dry-run runs a LIVE sync.`;

for (const [flag, argv, need] of [
  ["--max-comment-chars", ["sync", "--max-comment-chars", "--dry-run"], "a character count"],
  ["--repos", ["sync", "--repos", "--dry-run"], "a comma-separated owner/repo list"],
  ["--actor", ["sync", "--actor", "--dry-run"], "a non-blank name"],
  ["--task", ["push-issue", "--task", "--dry-run"], "an integer task id"],
  ["--limit", ["push-issue", "--limit", "--dry-run"], "an integer count"],
]) {
  test(`${flag} refuses a following --dry-run instead of swallowing it and running live`, async () => {
    const { parseArgs } = await load(undefined, `swallow${flag}`);
    const args = parseArgs(argv);
    assert.equal(args.error, `${flag} needs ${need}, but got the flag "--dry-run".${REFUSAL_TAIL}`);
    assert.equal(args.dryRun, false,
      "the --dry-run really was consumed — refusing is the only thing standing between this and a live write");
  });
}

test("--actor refuses a short flag (-h) just as it refuses a long flag (--dry-run)", async () => {
  const { parseArgs } = await load(undefined, "short-flag-actor");
  const args = parseArgs(["sync", "--actor", "-h"]);
  assert.equal(args.error, `--actor needs a non-blank name, but got the flag "-h".${REFUSAL_TAIL}`);
});

test("a value flag at the end of argv refuses instead of falling through to the default repo list", async () => {
  const { parseArgs } = await load(undefined, "argv-trailing-value-flag");
  const args = parseArgs(["sync", "--dry-run", "--repos"]);
  assert.equal(args.error, `--repos needs a comma-separated owner/repo list, but got nothing.${REFUSAL_TAIL}`);
});

test("a fully specified command line still parses every value-taking flag", async () => {
  const { parseArgs } = await load(undefined, "argv-happy-path");
  const args = parseArgs([
    "push-issue", "owner/repo", "--task", "42", "--limit", "5",
    "--actor", "nightly-sync", "--max-comment-chars", "500", "--dry-run",
  ]);
  assert.equal(args.error, null);
  assert.equal(args.command, "push-issue");
  assert.equal(args.dryRun, true);
  assert.deepEqual(args.repos, ["owner/repo"]);
  assert.equal(args.task, 42);
  assert.equal(args.limit, 5);
  assert.equal(args.actor, "nightly-sync");
  assert.equal(args.maxCommentChars, 500);
});

test("--repos still accepts a comma-separated list and still leaves --dry-run visible", async () => {
  const { parseArgs } = await load(undefined, "argv-repos-list");
  const args = parseArgs(["sync", "--repos", "owner/a, owner/b", "--dry-run"]);
  assert.equal(args.error, null);
  assert.deepEqual(args.repos, ["owner/a", "owner/b"]);
  assert.equal(args.dryRun, true);
});

import {
  assertGhIssueCreateAllowed,
  assertFreshPrecreateScan,
  assertLivePushProjectLink,
  assertReadOnly,
  analyzeMarkerRecords,
  buildGhIssueCreateArgs,
  buildExternalDispatchRequest,
  buildTaskIdentityIndex,
  classifyBackfillCandidate,
  completedOperationMatchesIssue,
  commandValidationError,
  exactMarkerCount,
  exactMarkerMatches,
  exhaustiveMarkerDisposition,
  ghIssueCreate,
  globalNodeLinkDisposition,
  githubLinksOf,
  issueBodyFor,
  issueHasOneCleanOperationMarker,
  linkMetadataNeedsRefresh,
  issuePushMarkerDisposition,
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
  resolveProjectLink,
  stripLegacyIssuePrefix,
  taskMatchForIssue,
  verifiedFinalizeRequest,
} from "./portfolio-sync.mjs";

const OP = "11111111-2222-4333-8444-555555555555";
const REPO_ID = "R_fixture";
const REPO = { externalId: REPO_ID, owner: "octo", name: "board", fullName: "octo/board" };

function task(id, name, description = "", externalLinks = []) {
  return { id, name, description, status: "Todo", priority: "High", externalLinks };
}

function link(taskId, number, nodeId, state = "Active") {
  return {
    id: taskId + 100,
    taskId,
    provider: "GitHub",
    state,
    repositoryExternalId: REPO_ID,
    externalNumber: number,
    externalNodeId: nodeId,
  };
}

test("generic gh guard remains a closed read-only allow-list", () => {
  assert.doesNotThrow(() => assertReadOnly(["issue", "list", "-R", "octo/board"]));
  assert.doesNotThrow(() => assertReadOnly(["api", "repos/octo/board"]));
  assert.throws(() => assertReadOnly(["issue", "create"]), /READ-ONLY/);
  assert.throws(() => assertReadOnly(["issue", "edit"]), /READ-ONLY/);
  assert.throws(() => assertReadOnly(["api", "repos/octo/board/issues", "--method", "POST"]), /READ-ONLY/);
  assert.throws(() => assertReadOnly(["api", "repos/octo/board/issues", "-f", "title=x"]), /READ-ONLY/);
  for (const compact of [
    "-XPOST", "-ftitle=x", "-Fbody=@file",
    "--method=POST", "--field=title=x", "--raw-field=title=x", "--input=file.json",
  ]) {
    assert.throws(() => assertReadOnly(["api", "repos/octo/board/issues", compact]), /READ-ONLY/, compact);
  }
});

test("dedicated create guard requires allow-write, one task, and non-preview mode", () => {
  assert.throws(() => assertGhIssueCreateAllowed({ allowWrite: false, task: 7 }), /both --allow-write and --task/);
  assert.throws(() => assertGhIssueCreateAllowed({ allowWrite: true, task: null }), /both --allow-write and --task/);
  for (const task of [0, -1, 1.5, Number.NaN, "7"]) {
    assert.throws(
      () => assertGhIssueCreateAllowed({ allowWrite: true, task }),
      /both --allow-write and --task/,
      `task=${String(task)} must not authorize a GitHub create`,
    );
  }
  assert.throws(() => assertGhIssueCreateAllowed({ allowWrite: true, task: 7, dryRun: true }), /disabled during/);
  assert.doesNotThrow(() => assertGhIssueCreateAllowed({ allowWrite: true, task: 7, dryRun: false }));
});

test("create argv is fixed and the Markdown body is never placed on argv", () => {
  const request = {
    title: "quotes ' and \" plus $() stay data",
    body: "# body\n\n`code`\n",
    milestoneTitle: "M1",
    label: "priority:high",
  };
  assert.deepEqual(buildGhIssueCreateArgs("octo/board", request), [
    "issue", "create", "--repo", "octo/board",
    "--title", request.title, "--body-file", "-",
    "--milestone", "M1", "--label", "priority:high",
  ]);
  assert.equal(buildGhIssueCreateArgs("octo/board", request).includes(request.body), false);
});

test("dedicated create wrapper passes the body byte-for-byte over stdin", async () => {
  let invocation;
  let stdin = "";
  const fakeSpawn = (command, args, options) => {
    invocation = { command, args, options };
    const child = new EventEmitter();
    child.stdin = new PassThrough();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.stdin.setEncoding("utf8");
    child.stdin.on("data", (chunk) => { stdin += chunk; });
    child.stdin.on("finish", () => {
      child.stdout.end("https://github.com/octo/board/issues/42\n");
      child.stderr.end();
      queueMicrotask(() => child.emit("close", 0));
    });
    return child;
  };
  const body = "line one\r\nline two with `code` and $()\n";
  const url = await ghIssueCreate(
    "octo/board",
    { title: "safe title", body, milestoneTitle: null, label: null },
    { allowWrite: true, task: 9, dryRun: false },
    fakeSpawn,
  );
  assert.equal(url, "https://github.com/octo/board/issues/42");
  assert.equal(invocation.command, "gh");
  assert.deepEqual(invocation.args, ["issue", "create", "--repo", "octo/board", "--title", "safe title", "--body-file", "-"]);
  assert.equal(invocation.options.stdio[0], "pipe");
  assert.equal(stdin, body);
});

test("dedicated wrapper surfaces a child-process pre-spawn error", async () => {
  const fakeMissingGh = () => {
    const child = new EventEmitter();
    child.stdin = new PassThrough();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    queueMicrotask(() => {
      const error = Object.assign(new Error("spawn gh ENOENT"), { code: "ENOENT" });
      child.emit("error", error);
    });
    return child;
  };
  await assert.rejects(
    ghIssueCreate(
      "octo/board",
      { title: "x", body: "body", milestoneTitle: null, label: null },
      { allowWrite: true, task: 9, dryRun: false },
      fakeMissingGh,
    ),
    /ENOENT/,
  );
});

test("CLI makes preview the default and rejects every broad or implicit write", () => {
  const preview = parseArgs(["push-issue", "octo/board", "--task", "7"]);
  assert.equal(preview.allowWrite, false);
  assert.equal(commandValidationError(preview), null);

  const noTask = parseArgs(["push-issue", "octo/board", "--allow-write"]);
  assert.match(commandValidationError(noTask), /requires --task/);
  const bulk = parseArgs(["push-issue", "octo/board", "--allow-write", "--task", "7", "--all"]);
  assert.match(commandValidationError(bulk), /--all is preview-only/);
  const implicitResume = parseArgs(["push-issue", "octo/board", "--task", "7", "--resume", OP]);
  assert.match(commandValidationError(implicitResume), /--resume requires/);
  const explicitResume = parseArgs(["push-issue", "octo/board", "--allow-write", "--task", "7", "--resume", OP]);
  assert.equal(commandValidationError(explicitResume), null);
  const dryResume = parseArgs(["push-issue", "octo/board", "--allow-write", "--task", "7", "--resume", OP, "--dry-run"]);
  assert.match(commandValidationError(dryResume), /cannot be combined/);
});

test("manual ambiguous retry authorization is explicit, UUID-bound, reasoned, and separate", () => {
  const valid = parseArgs([
    "push-issue", "octo/board", "--allow-write", "--task", "7", "--resume", OP,
    "--authorize-ambiguous-retry", OP, "--actor", "operator-a",
    "--resolution-reason", "Reviewed the exhaustive zero-marker scan",
  ]);
  assert.equal(valid.actorExplicit, true);
  assert.equal(valid.authorizeAmbiguousRetry, OP);
  assert.equal(commandValidationError(valid), null);

  const other = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
  const mismatch = { ...valid, authorizeAmbiguousRetry: other };
  assert.match(commandValidationError(mismatch), /exactly repeat/);
  assert.match(commandValidationError({ ...valid, actorExplicit: false }), /explicit non-blank --actor/);
  assert.match(commandValidationError({ ...valid, resolutionReason: "" }), /resolution-reason/);
  assert.match(commandValidationError({ ...valid, authorizeAmbiguousRetry: null }), /valid only/);

  const noResume = parseArgs([
    "push-issue", "octo/board", "--allow-write", "--task", "7",
    "--authorize-ambiguous-retry", OP, "--actor", "operator-a", "--resolution-reason", "reviewed",
  ]);
  assert.match(commandValidationError(noResume), /repeat the UUID supplied to --resume/);
});

test("push markers are exact UUID protocol data and duplicate exact markers are visible", () => {
  const marker = pushMarker(OP);
  const body = `${marker}\ntext\n${marker}`;
  assert.deepEqual(pushOperationIds(body), [OP]);
  assert.equal(exactMarkerCount(body, OP), 2);
  assert.equal(exactMarkerCount(`<!-- kanbanflow-push:${OP.slice(0, -1)}6 -->`, OP), 0);
  assert.deepEqual(rawPushOperationIds(`prefix kanbanflow-push : ${OP} suffix`), [OP]);
  assert.deepEqual(issuePushMarkerDisposition(pushMarker(OP)), {
    kind: "one",
    operationId: OP,
    rawOperationIds: [OP],
  });
  assert.equal(issuePushMarkerDisposition(`kanbanflow-push : ${OP}`).kind, "ambiguous");
  assert.equal(issuePushMarkerDisposition(`${pushMarker(OP)}\nkanbanflow-push : ${OP}`).kind, "ambiguous");
  assert.equal(issuePushMarkerDisposition(body).kind, "ambiguous");
});

test("authoritative marker filtering excludes PRs and requires exactly one marker in an issue", () => {
  const marker = pushMarker(OP);
  const issue = { number: 1, body: marker, node_id: "I_1" };
  const duplicate = { number: 2, body: `${marker}\n${marker}`, node_id: "I_2" };
  const pr = { number: 3, body: marker, node_id: "P_3", pull_request: {} };
  const otherOp = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
  const mixed = { number: 4, body: `${marker}\n${pushMarker(otherOp)}`, node_id: "I_4" };
  assert.deepEqual(exactMarkerMatches([[issue, duplicate], [pr, mixed]], OP), [issue]);
  assert.equal(issueHasOneCleanOperationMarker(issue, OP), true);
  assert.equal(issueHasOneCleanOperationMarker(duplicate, OP), false);
  assert.equal(issueHasOneCleanOperationMarker(mixed, OP), false);
  assert.equal(issueHasOneCleanOperationMarker({ body: `${marker}\nkanbanflow-push : ${OP}` }, OP), false);
});

test("manual retry zero means no raw target occurrence, not merely no clean match", () => {
  const marker = pushMarker(OP);
  const other = pushMarker("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
  const cases = [
    [{ number: 1, body: `${marker}\n${marker}` }],
    [{ number: 1, body: `${marker}\n${other}` }],
    [{ number: 1, body: marker }, { number: 2, body: marker }],
    [{ number: 1, body: `kanbanflow-push : ${OP}` }],
    [{ number: 1, body: marker, pull_request: {} }],
  ];
  for (const records of cases) {
    const scan = analyzeMarkerRecords([records], OP);
    assert.notEqual(scan.rawTargetOccurrenceCount, 0);
    assert.equal(
      exhaustiveMarkerDisposition(true, scan.matches.length, scan.rawTargetOccurrenceCount),
      "ambiguous",
    );
  }
  const clean = analyzeMarkerRecords([[{ number: 3, body: marker }]], OP);
  assert.equal(clean.rawTargetOccurrenceCount, 1);
  assert.equal(exhaustiveMarkerDisposition(true, clean.matches.length, clean.rawTargetOccurrenceCount), "one");
  const absent = analyzeMarkerRecords([[{ number: 4, body: "none" }]], OP);
  assert.equal(exhaustiveMarkerDisposition(true, absent.matches.length, absent.rawTargetOccurrenceCount), "zero");
});

test("recovery planning never creates from Dispatched or NeedsReview", () => {
  assert.equal(recoveryDisposition("Dispatched", true, true, 0), "needs-review");
  assert.equal(recoveryDisposition("Dispatched", true, true, 1), "finalize");
  assert.equal(recoveryDisposition("Dispatched", true, true, 2), "needs-review");
  assert.equal(recoveryDisposition("NeedsReview", true, true, 0), "needs-review");
  assert.equal(recoveryDisposition("NeedsReview", true, true, 1), "finalize");
  assert.equal(recoveryDisposition("NeedsReview", true, false, 1), "needs-review");
  assert.equal(recoveryDisposition("Prepared", true, true, 0), "create");
  assert.equal(recoveryDisposition("Prepared", false, true, 0), "needs-review");
  assert.equal(recoveryDisposition("DefinitiveFailure", true, true, 0), "create");
  assert.equal(recoveryDisposition("ManualRetryAuthorized", false, true, 0), "needs-review");
  assert.equal(recoveryDisposition("ManualRetryAuthorized", true, true, 0), "create");
  assert.equal(recoveryDisposition("Completed", false, false, 99), "completed");
  assert.equal(exhaustiveMarkerDisposition(true, 0), "zero");
  assert.equal(exhaustiveMarkerDisposition(true, 1), "one");
  assert.equal(exhaustiveMarkerDisposition(true, 2), "ambiguous");
  assert.equal(exhaustiveMarkerDisposition(false, 0), "incomplete");
});

test("only Prepared operations with evidence of a prior dispatch may proof-finalize a late marker", () => {
  assert.equal(markerOperationCanFinalize("Prepared", null), false);
  assert.equal(markerOperationCanFinalize("Prepared", "2026-08-30T12:00:00Z"), true);
  assert.equal(markerOperationCanFinalize("ManualRetryAuthorized", null), true);
  assert.equal(markerOperationCanFinalize("NeedsReview", null), true);
});

test("every create from prior-dispatch lineage requires this invocation's fresh zero scan", () => {
  const prior = { operationId: OP, state: "Prepared", dispatchedAt: "2026-08-30T12:00:00Z" };
  const fresh = { operationId: OP, state: "Prepared", dispatchedAt: null };
  const manual = {
    operationId: OP,
    state: "ManualRetryAuthorized",
    dispatchedAt: "2026-08-30T12:00:00Z",
    lastAttemptAt: "2026-08-30T12:00:00Z",
  };
  assert.equal(operationRequiresFreshPrecreateScan(prior), true);
  assert.equal(operationRequiresFreshPrecreateScan(fresh), false);
  assert.equal(operationRequiresFreshPrecreateScan(manual), true);
  assert.throws(() => assertFreshPrecreateScan(prior, {}), /fresh exhaustive zero-marker scan/);
  assert.throws(() => assertFreshPrecreateScan(prior, {
    freshZeroScanEvidence: {
      confirmationOperationId: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
      scanComplete: true,
      observedMatchCount: 0,
      observedRawTargetOccurrenceCount: 0,
      scanCompletedAt: "2026-08-30T12:00:01Z",
    },
  }), /fresh exhaustive/);
  assert.doesNotThrow(() => assertFreshPrecreateScan(prior, {
    freshZeroScanEvidence: {
      confirmationOperationId: OP.toUpperCase(),
      scanComplete: true,
      observedMatchCount: 0,
      observedRawTargetOccurrenceCount: 0,
      scanCompletedAt: "2026-08-30T12:00:01Z",
    },
  }));
  assert.throws(() => buildExternalDispatchRequest(
    { ...prior, attemptCount: 2 },
    "prior-dispatch-lease",
    {
      freshZeroScanEvidence: {
        confirmationOperationId: OP,
        scanComplete: true,
        observedMatchCount: 0,
        observedRawTargetOccurrenceCount: 0,
        scanCompletedAt: "2026-08-30T12:00:01Z",
      },
    },
  ), /cannot dispatch a replacement without ManualRetryAuthorized/,
  "Prepared operations with dispatch history must never dispatch a duplicate create");
  assert.throws(() => assertFreshPrecreateScan(manual, {
    freshZeroScanEvidence: {
      confirmationOperationId: OP,
      scanComplete: true,
      observedMatchCount: 0,
      observedRawTargetOccurrenceCount: 1,
      scanCompletedAt: "2026-08-30T12:00:01Z",
    },
  }), /fresh exhaustive/);
  assert.throws(() => assertFreshPrecreateScan(
    { ...manual, lastAttemptAt: "2026-08-30T12:00:02Z" },
    {
      freshZeroScanEvidence: {
        confirmationOperationId: OP,
        scanComplete: true,
        observedMatchCount: 0,
        observedRawTargetOccurrenceCount: 0,
        scanCompletedAt: "2026-08-30T12:00:01Z",
      },
    },
  ), /fresh exhaustive/, "a negative scan captured before Resume/LastAttemptAt is never replayable");
  assert.doesNotThrow(() => assertFreshPrecreateScan(fresh, {}));

  const evidence = {
    confirmationOperationId: OP,
    scanComplete: true,
    observedMatchCount: 0,
    observedRawTargetOccurrenceCount: 0,
    scanCompletedAt: "2026-08-30T12:00:01Z",
  };
  assert.deepEqual(buildExternalDispatchRequest(
    { ...manual, attemptCount: 3 },
    "lease-token",
    { freshZeroScanEvidence: evidence },
  ), {
    operationId: OP,
    leaseToken: "lease-token",
    ...evidence,
    expectedAttemptCount: 3,
  });
  assert.deepEqual(buildExternalDispatchRequest(fresh, "fresh-lease", {}), {
    operationId: OP,
    leaseToken: "fresh-lease",
  });
});

test("structured Active identity wins; legacy prefix is fallback only", () => {
  const durable = task(1, "plain linked title", "", [link(1, 41, "I_41")]);
  const legacy = task(2, "#42 legacy title");
  const misleading = task(3, "#77 misleading fallback", "", [link(3, 99, "I_99")]);
  const index = buildTaskIdentityIndex([durable, legacy, misleading], REPO);
  assert.deepEqual(taskMatchForIssue(index, { id: "I_41", number: 41 }).task, durable);
  assert.equal(taskMatchForIssue(index, { id: "I_41", number: 41 }).kind, "durable");
  assert.deepEqual(taskMatchForIssue(index, { id: "I_42", number: 42 }).task, legacy);
  assert.equal(taskMatchForIssue(index, { id: "I_42", number: 42 }).kind, "legacy");
  assert.equal(taskMatchForIssue(index, { id: "I_other", number: 77 }).task, null, "active link suppresses its misleading #77 prefix");
});

test("immutable issue node is canonical while number-only fallback rejects a different node", () => {
  const linked = task(1, "plain linked title", "", [link(1, 41, "I_original")]);
  const index = buildTaskIdentityIndex([linked], REPO);

  const reusedNumber = taskMatchForIssue(index, { id: "I_different", number: 41 });
  assert.equal(reusedNumber.kind, "identity-mismatch");
  assert.equal(reusedNumber.task, null);
  assert.match(reusedNumber.reason, /I_original/);

  const changedNumber = taskMatchForIssue(index, { id: "I_original", number: 42 });
  assert.equal(changedNumber.kind, "durable");
  assert.equal(changedNumber.task, linked);
});

test("global issue-node lookup refreshes only in-project transfers and blocks cross-project moves", () => {
  const linkedTask = task(7, "transferred issue");
  const index = buildTaskIdentityIndex([linkedTask], REPO);
  const transferredLink = {
    ...link(7, 12, "I_transfer"),
    repositoryExternalId: "R_old_repo",
  };
  assert.deepEqual(globalNodeLinkDisposition({ projectId: 4, link: transferredLink }, 4, index), {
    kind: "local-transfer",
    task: linkedTask,
    link: transferredLink,
    projectId: 4,
  });
  assert.deepEqual(globalNodeLinkDisposition({ projectId: 9, link: transferredLink }, 4, index), {
    kind: "cross-project",
    link: transferredLink,
    projectId: 9,
  });
  assert.equal(globalNodeLinkDisposition(null, 4, index).kind, "none");
  assert.equal(globalNodeLinkDisposition({ projectId: 4, link: { ...transferredLink, taskId: 999 } }, 4, index).kind, "invalid");
});

test("refreshed project link snapshot wins over stale embedded link with the same row id", () => {
  const stale = { ...link(7, 12, "I_transfer"), repositoryExternalId: "R_old_repo" };
  const refreshed = {
    ...stale,
    repositoryExternalId: REPO_ID,
    repositoryOwner: "octo",
    repositoryName: "board",
    externalNumber: 77,
  };
  const linkedTask = task(7, "transferred", "", [stale]);
  assert.deepEqual(githubLinksOf(linkedTask, [refreshed]), [refreshed]);
});

test("only Completed marker with matching immutable result node crosses repository identity", () => {
  const issue = { id: "I_transferred", number: 77 };
  assert.equal(operationMarkerRepositoryDisposition({ repositoryExternalId: REPO_ID }, REPO, issue), "same-repository");
  assert.equal(operationMarkerRepositoryDisposition({
    state: "Completed",
    repositoryExternalId: "R_old",
    resultingExternalNodeId: issue.id,
    resultingExternalNumber: 12,
  }, REPO, issue), "completed-node-transfer");
  assert.equal(operationMarkerRepositoryDisposition({
    state: "Completed",
    repositoryExternalId: "R_old",
    resultingExternalNodeId: "I_other",
    resultingExternalNumber: issue.number,
  }, REPO, issue), "block");
  assert.equal(operationMarkerRepositoryDisposition({
    state: "Dispatched",
    repositoryExternalId: "R_old",
    resultingExternalNodeId: issue.id,
  }, REPO, issue), "block");
});

test("project resolution upgrades an old mutable path marker without a flag day", () => {
  const project = { id: 4, name: "board", description: "<!-- github: octo/board -->\nnotes" };
  const result = resolveProjectLink("octo/board", [project], REPO);
  assert.equal(result.project, project);
  assert.equal(result.linkedBy, "marker");
  assert.equal(result.needsRefresh, true);
});

test("immutable repository node survives a repo rename and refreshes display metadata", () => {
  const renamedIdentity = { ...REPO, owner: "new-owner", name: "renamed", fullName: "new-owner/renamed" };
  const project = {
    id: 4,
    name: "human board name",
    description: `<!-- github-node: ${REPO_ID} -->\n<!-- github: octo/board -->\nSynced from https://github.com/octo/board — GitHub issues are the source of truth.\nnotes`,
  };
  const result = resolveProjectLink(renamedIdentity.fullName, [project], renamedIdentity);
  assert.equal(result.project, project);
  assert.equal(result.linkedBy, "node");
  assert.equal(result.needsRefresh, true);
  const refreshed = refreshedProjectDescription(project, renamedIdentity.fullName, renamedIdentity);
  assert.match(refreshed, /<!-- github-node: R_fixture -->/);
  assert.match(refreshed, /<!-- github: new-owner\/renamed -->/);
  assert.match(refreshed, /github\.com\/new-owner\/renamed/);
  assert.doesNotMatch(refreshed, /github: octo\/board/);
});

test("immutable project conflicts never fall through to path/name adoption or live create", () => {
  const nameClaimed = {
    id: 7,
    name: "board",
    description: "<!-- github-node: R_other -->\nclaimed elsewhere",
  };
  const byName = resolveProjectLink("octo/board", [nameClaimed], REPO);
  assert.equal(byName.project, null);
  assert.match(byName.conflict, /repository node R_other/);

  const contradictory = {
    id: 8,
    name: "anything",
    description: "<!-- github-node: R_other -->\n<!-- github: octo/board -->",
  };
  const byPath = resolveProjectLink("octo/board", [contradictory], REPO);
  assert.equal(byPath.project, contradictory);
  assert.match(byPath.conflict, /conflicting immutable repository node/);
  assert.doesNotThrow(() => assertLivePushProjectLink(byPath, false), "preview may report the conflict");
  assert.throws(() => assertLivePushProjectLink(byPath, true), /Live push refuses/);
});

test("a finalized link with frozen pre-rename display metadata requires immediate refresh", () => {
  const renamedIdentity = { ...REPO, owner: "new-owner", name: "renamed", fullName: "new-owner/renamed" };
  const issue = { node_id: "I_41", number: 41, html_url: "https://github.com/new-owner/renamed/issues/41" };
  const stale = {
    repositoryExternalId: REPO_ID,
    repositoryOwner: "octo",
    repositoryName: "board",
    externalNodeId: "I_41",
    externalNumber: 41,
    url: "https://github.com/octo/board/issues/41",
  };
  assert.equal(linkMetadataNeedsRefresh(stale, renamedIdentity, issue), true);
  assert.equal(linkMetadataNeedsRefresh({
    ...stale,
    repositoryOwner: renamedIdentity.owner,
    repositoryName: renamedIdentity.name,
    url: issue.html_url,
  }, renamedIdentity, issue), false);
  assert.deepEqual(
    verifiedFinalizeRequest({ operationId: OP }, "lease", renamedIdentity, issue, "tester"),
    {
      operationId: OP,
      leaseToken: "lease",
      repositoryExternalId: REPO_ID,
      repositoryOwner: "new-owner",
      repositoryName: "renamed",
      externalNodeId: "I_41",
      externalNumber: 41,
      url: issue.html_url,
      actor: "tester",
    },
    "finalize validates the issue URL against the current repository path while immutable repo id stays authoritative",
  );
});

test("operation identity maps a create-window issue to its intended existing task", () => {
  const intended = task(7, "plain task");
  const operationTasks = new Map([["I_new", 7], [88, 7]]);
  const index = buildTaskIdentityIndex([intended], REPO, [], operationTasks);
  assert.deepEqual(taskMatchForIssue(index, { id: "I_new", number: 88 }), { task: intended, kind: "operation" });
});

test("completed operation with deleted task permits one normal reprojection; nonterminal does not", () => {
  assert.equal(operationTaskDisposition("Completed", false, true), "project-new-task");
  assert.equal(operationTaskDisposition("Completed", true, true), "map");
  assert.equal(operationTaskDisposition("Completed", false, false), "block");
  assert.equal(operationTaskDisposition("Dispatched", false), "block");
  assert.equal(operationTaskDisposition("NeedsReview", false), "block");
  assert.equal(operationTaskDisposition("Dispatched", true), "continue");
});

test("completed operation result uses immutable node first and number only for old node-less rows", () => {
  const issue = { id: "I_result", number: 88 };
  assert.equal(completedOperationMatchesIssue({
    resultingExternalNodeId: "I_result",
    resultingExternalNumber: 88,
  }, issue), true);
  assert.equal(completedOperationMatchesIssue({
    resultingExternalNodeId: "I_other",
    resultingExternalNumber: 88,
  }, issue), false, "a matching repo-local number cannot override a different immutable node");
  assert.equal(completedOperationMatchesIssue({
    resultingExternalNodeId: "I_result",
    resultingExternalNumber: 89,
  }, issue), true, "repository-local number drift is refreshable when the immutable node matches");
  assert.equal(completedOperationMatchesIssue({ resultingExternalNumber: 88 }, issue), true, "old number-only journals remain readable");
});

test("a Quarantined row blocks legacy fallback even when CandidateNumber is null", () => {
  const quarantined = task(8, "#77 suspicious literal", "", [link(8, null, null, "Quarantined")]);
  const index = buildTaskIdentityIndex([quarantined], REPO);
  assert.equal(index.legacyByNumber.has(77), false);
  assert.equal(index.quarantinedTaskIds.has(8), true);
  assert.deepEqual(taskMatchForIssue(index, { id: "I_77", number: 77 }), {
    task: quarantined,
    kind: "quarantine",
    quarantinedTasks: [quarantined],
  });
});

test("a structured GitHub link for another repository blocks legacy fallback", () => {
  const foreign = task(9, "#55 must not be adopted", "", [{
    ...link(9, 55, "I_foreign"),
    repositoryExternalId: "R_other",
  }]);
  const index = buildTaskIdentityIndex([foreign], REPO);
  assert.equal(index.legacyByNumber.has(55), false);
  assert.equal(index.foreignLinkedTaskIds.has(9), true);
  assert.equal(taskMatchForIssue(index, { id: "I_current", number: 55 }).kind, "foreign-link");
});

test("outbound board body has no caller marker and strips inbound sync protocol", () => {
  const body = issueBodyFor(
    { name: "Board" },
    task(5, "Draft", "<!-- kanbanflow-sync -->\n**Issue:** https://github.com/octo/board/issues/1\n<!-- /kanbanflow-sync -->\nUser content"),
    "Feature",
  );
  assert.doesNotMatch(body, /kanbanflow-sync/);
  assert.doesNotMatch(body, /kanbanflow-push/);
  assert.match(body, /User content/);
});

test("backfill accepts only one canonical issue URL inside one sync block", () => {
  const valid = task(1, "#12 old", "<!-- kanbanflow-sync -->\n**Issue:** https://github.com/octo/board/issues/12\n<!-- /kanbanflow-sync -->");
  assert.deepEqual(classifyBackfillCandidate(valid), {
    kind: "verify", owner: "octo", repo: "board", number: 12,
    candidateNumber: 12, candidateUrl: "https://github.com/octo/board/issues/12",
  });
  assert.equal(parseGitHubIssueUrl("https://github.com/octo/board/pull/12"), null);
  assert.equal(stripLegacyIssuePrefix("#12 preserve my words"), "preserve my words");
});

test("backfill quarantines title-only, duplicate, and malformed candidates; path drift awaits node verification", () => {
  const titleOnly = task(1, "#12 literal text");
  const duplicateA = task(2, "#13 first", "<!-- kanbanflow-sync -->\n**Issue:** https://github.com/octo/board/issues/13\n<!-- /kanbanflow-sync -->");
  const duplicateB = task(3, "#13 second", duplicateA.description);
  const malformed = task(4, "#14 malformed", "<!-- kanbanflow-sync -->\n**Issue:** not-a-url\n<!-- /kanbanflow-sync -->");
  const crossRepo = task(5, "#15 cross", "<!-- kanbanflow-sync -->\n**Issue:** https://github.com/elsewhere/other/issues/15\n<!-- /kanbanflow-sync -->");
  const plans = planBackfill([titleOnly, duplicateA, duplicateB, malformed, crossRepo], [], REPO);
  assert.deepEqual(plans.map((item) => item.kind), ["quarantine", "quarantine", "quarantine", "quarantine", "verify"]);
  assert.match(plans[0].reason, /title-only/);
  assert.match(plans[1].reason, /duplicate/);
  assert.match(plans[2].reason, /duplicate/);
  assert.match(plans[3].reason, /malformed/);
  assert.equal(plans[4].pathDrift, true);
});

test("backfill rerun treats persisted active and quarantine rows as existing", () => {
  const activeTask = task(1, "#12 old linked title", "", [link(1, 12, "I_12", "Active")]);
  const quarantinedTask = task(2, "#99 literal", "", [link(2, 99, null, "Quarantined")]);
  const plans = planBackfill([activeTask, quarantinedTask], [], REPO);
  assert.deepEqual(plans.map((item) => item.kind), ["existing", "existing"]);
  assert.equal(plans[0].plainName, "old linked title", "rerun converges a crash between link registration and prefix stripping");
  assert.equal(plans[1].plainName, null, "quarantine never authorizes a title mutation");
});

test("malformed provenance preserves one safe URL identity and null identity fails closed for unmatched issues", () => {
  const sameUrlTwice = task(1, "no prefix", [
    "<!-- kanbanflow-sync -->",
    "**Issue:** https://github.com/octo/board/issues/12",
    "<!-- /kanbanflow-sync -->",
    "<!-- kanbanflow-sync -->",
    "**Issue:** https://github.com/octo/board/issues/12",
    "<!-- /kanbanflow-sync -->",
  ].join("\n"));
  const classified = classifyBackfillCandidate(sameUrlTwice);
  assert.equal(classified.kind, "quarantine");
  assert.equal(classified.candidateNumber, 12);
  assert.equal(classified.candidateUrl, "https://github.com/octo/board/issues/12");

  const outsideBlock = classifyBackfillCandidate(task(
    3,
    "no prefix",
    "**Issue:** https://github.com/octo/board/issues/13",
  ));
  assert.equal(outsideBlock.kind, "quarantine");
  assert.equal(outsideBlock.candidateNumber, 13);

  const conflictingUrls = classifyBackfillCandidate(task(4, "no prefix", [
    "**Issue:** https://github.com/octo/board/issues/14",
    "**Issue:** https://github.com/octo/board/issues/15",
  ].join("\n")));
  assert.equal(conflictingUrls.kind, "quarantine");
  assert.equal(conflictingUrls.candidateNumber, null);

  const unknown = task(2, "no correlatable prefix", "", [link(2, null, null, "Quarantined")]);
  const index = buildTaskIdentityIndex([unknown], REPO);
  const match = taskMatchForIssue(index, { id: "I_any", number: 999 });
  assert.equal(match.kind, "quarantine");
  assert.equal(match.projectWide, true);
  assert.deepEqual(match.quarantinedTasks, [unknown]);
});

test("backfill quarantines a new candidate colliding with persisted Active or Quarantined identity", () => {
  const description = "<!-- kanbanflow-sync -->\n**Issue:** https://github.com/octo/board/issues/12\n<!-- /kanbanflow-sync -->";
  for (const state of ["Active", "Quarantined"]) {
    const persisted = { ...link(1, 12, state === "Active" ? "I_12" : null, state), repositoryExternalId: REPO_ID };
    const plans = planBackfill(
      [task(1, "existing"), task(2, "#12 new claimant", description)],
      [persisted],
      REPO,
    );
    assert.equal(plans[0].kind, "existing");
    assert.equal(plans[1].kind, "quarantine");
    assert.match(plans[1].reason, new RegExp(`persisted ${state} task 1`));
  }
});

// --- argv: every value-consuming flag refuses a flag as its value (PR #510's takeValue) -----
//
// One case per flag, each asserting THAT flag's name in the refusal, so reverting a single
// flag to `argv[++i]` reddens exactly one row. The two assertions per row are deliberate and
// neither is redundant: `args.error` alone is NOT failable for --resume /
// --authorize-ambiguous-retry, because the post-loop UUID regex sets an error anyway when the
// swallowed token is "--dry-run". Only "the value never became the flag" plus the takeValue
// wording distinguishes a refusal from an accident.
test("every value-consuming flag refuses a following flag instead of swallowing it", () => {
  const cases = [
    ["--max-comment-chars", "maxCommentChars", 0],
    // `octo/board` is a bare positional repo, so --repos starts from that one entry.
    ["--repos", "repos", ["octo/board"]],
    ["--actor", "actor", "portfolio-sync"],
    ["--task", "task", null],
    ["--resume", "resume", null],
    ["--authorize-ambiguous-retry", "authorizeAmbiguousRetry", null],
    ["--resolution-reason", "resolutionReason", null],
    ["--limit", "limit", 0],
  ];
  for (const [flag, field, untouched] of cases) {
    const swallowed = parseArgs(["push-issue", "octo/board", flag, "--dry-run"]);
    assert.ok(String(swallowed.error).startsWith(`${flag} needs `),
      `${flag} must name itself in the refusal, so reverting one flag reddens exactly one row (got: ${swallowed.error})`);
    assert.match(swallowed.error ?? "", /but got the flag "--dry-run"/,
      `${flag} must refuse --dry-run as a value rather than consuming it`);
    assert.deepEqual(swallowed[field], untouched, `${flag} must not keep "--dry-run" as its value`);
    assert.equal(swallowed.dryRun, false, `${flag} must not have consumed the --dry-run it refused`);

    // A short flag is a flag too: `-h` was silently eaten before #510's follow-up commit.
    const short = parseArgs(["push-issue", "octo/board", flag, "-h"]);
    assert.match(short.error ?? "", /but got the flag "-h"/, `${flag} must refuse a short flag`);
    assert.deepEqual(short[field], untouched, `${flag} must not keep "-h" as its value`);

    // Trailing flag with nothing after it, and an explicitly blank value.
    assert.match(parseArgs(["push-issue", "octo/board", flag]).error ?? "", /but got nothing/);
    assert.match(parseArgs(["push-issue", "octo/board", flag, "   "]).error ?? "", /but got a blank value/);
  }
});

test("--resume --dry-run refuses before main() can run a live recovery", () => {
  const args = parseArgs(["push-issue", "octo/board", "--allow-write", "--task", "7", "--resume", "--dry-run"]);
  // The whole point: the recovery path WRITES to GitHub. A swallowed --dry-run left dryRun
  // false and resume "--dry-run"; main() exits 2 on args.error before it touches a repo.
  assert.notEqual(args.resume, "--dry-run");
  assert.equal(args.resume, null);
  assert.equal(args.dryRun, false);
  assert.match(args.error, /^--resume needs an operation UUID, but got the flag "--dry-run"/);
});

test("a genuine value still parses, so the guard refuses flags and nothing else", () => {
  const args = parseArgs([
    "push-issue", "octo/board", "--allow-write", "--task", " 7 ", "--resume", OP,
    "--authorize-ambiguous-retry", OP, "--actor", "operator-a",
    "--resolution-reason", "Reviewed the exhaustive zero-marker scan", "--limit", "5",
    "--max-comment-chars", "120", "--repos", "octo/other",
  ]);
  assert.equal(args.error, null);
  assert.equal(args.task, 7);
  assert.equal(args.resume, OP);
  assert.equal(args.authorizeAmbiguousRetry, OP);
  assert.equal(args.actor, "operator-a");
  assert.equal(args.actorExplicit, true);
  assert.equal(args.resolutionReason, "Reviewed the exhaustive zero-marker scan");
  assert.equal(args.limit, 5);
  assert.equal(args.maxCommentChars, 120);
  assert.deepEqual(args.repos, ["octo/board", "octo/other"]);
});

// --- #336 crash harness: out of the shipped path -------------------------------------------
import { mkdtempSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join as joinPath, resolve as resolvePath } from "node:path";

const BARRIER_ENV = {
  KANBANFLOW_336_BEFORE_PREPARE_SIGNAL: null,
  KANBANFLOW_336_BEFORE_PREPARE_RELEASE: null,
  KANBANFLOW_336_AFTER_DISPATCH_SIGNAL: null,
  KANBANFLOW_336_AFTER_DISPATCH_RELEASE: null,
  KANBANFLOW_336_AFTER_MANUAL_RESUME_SIGNAL: null,
  KANBANFLOW_336_AFTER_MANUAL_RESUME_RELEASE: null,
  KANBANFLOW_336_AFTER_CREATE_SIGNAL: null,
  KANBANFLOW_336_AFTER_CREATE_RELEASE: null,
};

function barrierEnv(dir, extra = {}) {
  const env = {};
  let n = 0;
  for (const name of Object.keys(BARRIER_ENV)) {
    env[name] = joinPath(dir, `${n++}-${name.endsWith("SIGNAL") ? "signal.json" : "release"}`);
  }
  // Keep a sabotaged build from blocking the suite for ten minutes: the floor is 1s.
  env.KANBANFLOW_336_BARRIER_TIMEOUT_MS = "1000";
  return { ...env, ...extra };
}

test("all eight barrier env vars, without --internal-test-hooks, arm absolutely nothing", async () => {
  const mod = await import("./portfolio-sync.mjs?hooks-env-only");
  const dir = mkdtempSync(joinPath(tmpdir(), "kf336-envonly-"));
  try {
    const env = barrierEnv(dir, { KANBANFLOW_336_TEST_HOOKS: "scripts/test-hooks/336-barriers.mjs" });
    const result = await mod.installTestHooks({ internalTestHooks: false, allowWrite: true }, env);
    assert.equal(result.installed, false);
    assert.equal(result.reason, "env-set-without-flag");

    // Arming used to be "an env var exists". Prove the production hooks are inert against the
    // fully-armed environment: no file written, no wait — process.env is what they would have
    // read, so set it there too and put it back afterwards.
    const saved = {};
    for (const [key, value] of Object.entries(env)) { saved[key] = process.env[key]; process.env[key] = value; }
    try {
      const started = Date.now();
      const context = { operationId: OP, taskId: 7, attemptCount: 1, issueNumber: 1, issueUrl: "u" };
      await mod.hooks.beforePrepare(context);
      await mod.hooks.afterDispatch(context);
      await mod.hooks.afterManualResume(context);
      await mod.hooks.afterCreate(context);
      assert.ok(Date.now() - started < 900, "a production hook must not wait on anything");
      assert.deepEqual(readdirSync(dir), [], "a production hook must not touch the filesystem");
    } finally {
      for (const [key, value] of Object.entries(saved)) {
        if (value === undefined) delete process.env[key]; else process.env[key] = value;
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("the harness needs both keys, and a third to reach GitHub", async () => {
  const mod = await import("./portfolio-sync.mjs?hooks-gates");
  const modulePath = resolvePath("scripts/test-hooks/336-barriers.mjs");

  await assert.rejects(
    () => mod.installTestHooks({ internalTestHooks: true, allowWrite: false }, {}),
    /requires KANBANFLOW_336_TEST_HOOKS/,
  );
  await assert.rejects(
    () => mod.installTestHooks({ internalTestHooks: true, allowWrite: true }, { KANBANFLOW_336_TEST_HOOKS: modulePath }),
    /KANBANFLOW_336_ALLOW_WRITE_HOOKS=1/,
  );

  const errors = [];
  const realError = console.error;
  console.error = (line) => errors.push(String(line));
  try {
    const result = await mod.installTestHooks(
      { internalTestHooks: true, allowWrite: true },
      { KANBANFLOW_336_TEST_HOOKS: modulePath, KANBANFLOW_336_ALLOW_WRITE_HOOKS: "1" },
    );
    assert.equal(result.installed, true);
    assert.match(errors.join("\n"), /#336 TEST HOOKS ACTIVE — this is not a production run/);
  } finally {
    console.error = realError;
  }
  // And now they really are installed on THIS module instance — proving the flag path works
  // rather than merely returning a truthy object.
  const dir = mkdtempSync(joinPath(tmpdir(), "kf336-armed-"));
  try {
    const env = barrierEnv(dir);
    const saved = {};
    for (const [key, value] of Object.entries(env)) { saved[key] = process.env[key]; process.env[key] = value; }
    try {
      await assert.rejects(
        () => mod.hooks.afterCreate({ operationId: OP, taskId: 7 }),
        (err) => err instanceof mod.HookAbort && /Timed out waiting for #336 release file/.test(err.message),
      );
      assert.ok(readdirSync(dir).some((name) => name.endsWith("signal.json")),
        "an armed barrier writes its signal file");
    } finally {
      for (const [key, value] of Object.entries(saved)) {
        if (value === undefined) delete process.env[key]; else process.env[key] = value;
      }
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("a HookAbort never becomes an ambiguous GitHub outcome", async () => {
  const mod = await import("./portfolio-sync.mjs?hooks-abort");
  const calls = [];
  const api = {
    getOperation: async (id) => { calls.push(["getOperation", id]); return { state: "Dispatched" }; },
    failure: async (id, body) => { calls.push(["failure", id, body.kind]); return { state: "NeedsReview" }; },
  };
  const operation = { operationId: OP, taskId: 7 };

  // The after-create hook fires when the issue EXISTS and its marker is already verified.
  const abort = new mod.HookAbort("Timed out waiting for #336 release file r");
  await assert.rejects(
    () => mod.handleCreateWindowFailure(operation, "lease-1", "https://github.com/octo/board/issues/9", abort, api),
    (err) => err === abort,
  );
  assert.deepEqual(calls, [], "a harness abort must not read or write the operation's state");

  // The discriminator, not a blanket exemption: a real create-window error still records.
  await assert.rejects(
    () => mod.handleCreateWindowFailure(operation, "lease-1", null, new Error("gh exploded"), api),
    // The truthful replacement for "State remains NeedsReview; no create was attempted."
    /State is now NeedsReview; a create MAY have reached GitHub\./,
  );
  assert.deepEqual(calls, [["getOperation", OP], ["failure", OP, "AmbiguousOrIncomplete"]]);
});

test("a pre-dispatch hook abort records nothing and says what it left behind", async () => {
  const mod = await import("./portfolio-sync.mjs?hooks-predispatch");
  const operation = { operationId: OP, taskId: 7, attemptCount: 2 };
  const saved = mod.hooks.afterManualResume;
  try {
    mod.hooks.afterManualResume = async () => { throw new mod.HookAbort("Timed out waiting for #336 release file r"); };
    await assert.rejects(
      () => mod.runManualResumeHook(operation),
      (err) => {
        assert.ok(err instanceof mod.HookAbort, "the abort must stay a HookAbort so the create window re-raises it untouched");
        assert.match(err.message, /aborted BEFORE dispatch, so no create was attempted and no state was written/);
        assert.match(err.message, /lease stays claimed until it expires/);
        return true;
      },
    );
    // A non-hook failure is NOT reclassified — the guard discriminates rather than swallowing.
    mod.hooks.afterManualResume = async () => { throw new Error("something else"); };
    await assert.rejects(() => mod.runManualResumeHook(operation), /^Error: something else$/);
  } finally {
    mod.hooks.afterManualResume = saved;
  }
});

// --- legacy -> durable auto-promote --------------------------------------------------------
//
// Without this the two new tables stay empty on every existing board and the whole durable
// identity feature is inert. WITH it, and without the sync-block precondition, it is a brand
// new way to bind a task's immutable identity to a FOREIGN repository's issue by nothing but a
// matching integer -- so the precondition gets as much coverage as the promotion.

const SYNC_BLOCK = (owner, repo, number) =>
  `<!-- kanbanflow-sync -->\n**Issue:** https://github.com/${owner}/${repo}/issues/${number}\n<!-- /kanbanflow-sync -->`;

const GH_ISSUE = { id: "I_kwDOnode42", number: 42, title: "Foo", state: "OPEN", url: "https://github.com/octo/board/issues/42" };

test("promotion is refused when the task's authoritative sync block names another repo", async () => {
  const { legacyPromotionDisposition } = await import("./portfolio-sync.mjs?promote-precondition");

  // The ordinary pre-#336 task: a "#42 " prefix and no sync block at all. This is the case
  // promotion exists for, so it MUST be allowed.
  const bare = legacyPromotionDisposition(task(1, "#42 Foo", ""), GH_ISSUE, REPO);
  assert.equal(bare.kind, "promote");
  assert.equal(bare.nodeId, "I_kwDOnode42");

  // Same repo, same number -> the sync block agrees, so promotion proceeds.
  const agrees = legacyPromotionDisposition(task(1, "#42 Foo", SYNC_BLOCK("octo", "board", 42)), GH_ISSUE, REPO);
  assert.equal(agrees.kind, "promote");
  // Case is not identity: "Octo/Board" is the same repository.
  assert.equal(legacyPromotionDisposition(task(1, "#42 Foo", SYNC_BLOCK("Octo", "Board", 42)), GH_ISSUE, REPO).kind, "promote");

  // THE ONE THAT MATTERS. A task moved between projects, or a project re-pointed at another
  // repo: the number matches by coincidence and the authoritative URL says otherwise.
  const foreign = legacyPromotionDisposition(task(1, "#42 Foo", SYNC_BLOCK("other", "repo", 42)), GH_ISSUE, REPO);
  assert.equal(foreign.kind, "drift");
  assert.match(foreign.reason, /disagrees with the task's authoritative sync-block URL/);
  assert.match(foreign.reason, /other\/repo\/issues\/42/);
  assert.match(foreign.reason, /backfill-links octo\/board/);

  // Same repo, different number: the prefix and the sync block disagree about WHICH issue.
  const wrongNumber = legacyPromotionDisposition(task(1, "#42 Foo", SYNC_BLOCK("octo", "board", 99)), GH_ISSUE, REPO);
  assert.equal(wrongNumber.kind, "drift");
  assert.match(wrongNumber.reason, /names #99/);

  // Malformed provenance fails closed; backfill-links owns that taxonomy.
  const twoUrls = task(1, "#42 Foo",
    "<!-- kanbanflow-sync -->\n**Issue:** https://github.com/octo/board/issues/42\n**Issue:** https://github.com/octo/board/issues/43\n<!-- /kanbanflow-sync -->");
  assert.equal(legacyPromotionDisposition(twoUrls, GH_ISSUE, REPO).kind, "drift");

  // A REST-shaped record puts a repo-local NUMBER in `id`. Persisting that as the immutable
  // externalNodeId would corrupt the one field the durable-identity contract rests on.
  const restShaped = legacyPromotionDisposition(task(1, "#42 Foo", ""), { id: 42, number: 42, title: "Foo" }, REPO);
  assert.equal(restShaped.kind, "drift");
  assert.match(restShaped.reason, /no immutable GitHub node id/);
  // ...but a REST record that DOES carry node_id is fine: the node id is what is checked.
  assert.equal(
    legacyPromotionDisposition(task(1, "#42 Foo", ""), { id: 42, node_id: "I_kwDOnode42", number: 42 }, REPO).kind,
    "promote",
  );
});

// Drives the real syncTasks with a stubbed transport, so the CALL SITE is covered and not just
// the predicate. Records every request the sync makes.
async function runSyncTasks(tag, tasks, issues, {
  dryRun = false,
  registerLinkStatus = 200,
  comments = false,
  featureIds = new Map(),
} = {}) {
  const mod = await import(`./portfolio-sync.mjs?${tag}`);
  const requests = [];
  const realFetch = globalThis.fetch;
  const realLog = console.log;
  const driftBefore = mod.stats.drift.length;
  const registeredBefore = mod.stats.linksRegistered;
  globalThis.fetch = async (url, init = {}) => {
    const path = String(url).replace(/^https?:\/\/[^/]+/, "");
    const body = init.body ? JSON.parse(init.body) : null;
    requests.push({ method: init.method || "GET", path, body });
    if (path.startsWith("/api/external-work-item-links/github/by-node")) {
      return new Response("", { status: 404, statusText: "Not Found" });
    }
    if ((init.method || "GET") === "GET" && /^\/api\/tasks\/\d+\/comments$/.test(path)) {
      return Response.json([]);
    }
    if (/^\/api\/tasks\/\d+\/external-work-item-links\/github$/.test(path)) {
      if (registerLinkStatus !== 200) {
        return new Response("external-node-already-linked", { status: registerLinkStatus, statusText: "Conflict" });
      }
      return Response.json({ id: 500, taskId: body.taskId, provider: "GitHub", state: "Active", ...body });
    }
    return Response.json({ ok: true });
  };
  console.log = () => {};
  try {
    await mod.syncTasks(
      "octo/board", REPO, 1, issues, new Set(), new Map(), featureIds,
      { actor: "tester", dryRun, comments },
      tasks, [], new Map(),
    );
  } finally {
    globalThis.fetch = realFetch;
    console.log = realLog;
  }
  return {
    requests,
    drift: mod.stats.drift.slice(driftBefore),
    linksRegistered: mod.stats.linksRegistered - registeredBefore,
  };
}

test("an ordinary sync promotes a legacy prefix match and strips the prefix", async () => {
  const result = await runSyncTasks("promote-live", [task(1, "#42 Foo", "")], [GH_ISSUE]);

  const registrations = result.requests.filter((r) => r.path === "/api/tasks/1/external-work-item-links/github");
  assert.equal(registrations.length, 1, "exactly one durable link is registered");
  assert.equal(registrations[0].method, "PUT");
  assert.equal(registrations[0].body.externalNodeId, "I_kwDOnode42");
  assert.equal(registrations[0].body.externalNumber, 42);
  assert.equal(registrations[0].body.repositoryExternalId, REPO_ID);
  assert.equal(result.linksRegistered, 1);

  const renames = result.requests.filter((r) => r.method === "PUT" && r.path === "/api/tasks/1");
  assert.equal(renames.length, 1, "the legacy prefix is stripped in the same run");
  assert.equal(renames[0].body.name, "Foo");
  assert.deepEqual(result.drift, []);
});

test("promotion never binds a task to a foreign repository's issue", async () => {
  const result = await runSyncTasks(
    "promote-foreign",
    [task(1, "#42 Foo", SYNC_BLOCK("other", "repo", 42))],
    [GH_ISSUE],
  );

  assert.deepEqual(
    result.requests.filter((r) => r.path === "/api/tasks/1/external-work-item-links/github"),
    [],
    "zero durable links registered when the sync block contradicts the prefix",
  );
  assert.equal(result.linksRegistered, 0);
  assert.equal(result.drift.length, 1);
  assert.match(result.drift[0], /backfill-links octo\/board/);
  assert.deepEqual(result.requests.filter((r) => r.method !== "GET"), [],
    "the task's full content, status, name and location must survive an identity refusal");
});

for (const [name, description, issue, reason] of [
  ["foreign repository", SYNC_BLOCK("other", "repo", 42), GH_ISSUE, /disagrees with/],
  ["different issue", SYNC_BLOCK("octo", "board", 99), GH_ISSUE, /names #99/],
  ["ambiguous provenance", `${SYNC_BLOCK("octo", "board", 42)}\n${SYNC_BLOCK("octo", "board", 43)}`, GH_ISSUE, /2 kanbanflow-sync blocks/],
  ["numeric issue identity", "", { ...GH_ISSUE, id: 42 }, /no immutable GitHub node id/],
]) {
  test(`rejected legacy identity (${name}) skips every mutation and continues other issues`, async () => {
    const unrelatedIssue = { ...GH_ISSUE, id: "I_other43", number: 43, title: "Other", url: "https://github.com/octo/board/issues/43" };
    const result = await runSyncTasks(`reject-legacy-${name}`, [
      { ...task(1, "#42 Original title", description), featureId: 10 },
      { ...task(2, "#43 Other", ""), featureId: 20 },
    ], [{
      ...issue,
      state: "CLOSED",
      body: "Replacement source content",
      milestone: { title: "Incoming milestone" },
      comments: [{ id: "IC_wrong-source", body: "Do not copy this comment", author: { login: "octo" } }],
    }, unrelatedIssue], {
      comments: true,
      featureIds: new Map([["Incoming milestone", 99], ["Unsorted", 20]]),
    });

    assert.equal(result.drift.length, 1);
    assert.match(result.drift[0], reason);
    assert.deepEqual(result.requests.filter((r) => r.method !== "GET" && /^\/api\/tasks\/1(?:\/|$)/.test(r.path)), [],
      "rejected identity must not register a link, add comments, move the task or update any task fields");
    assert.equal(result.linksRegistered, 1, "a rejected issue must not abort a later valid issue");
    assert.equal(result.requests.find((r) => r.method === "PUT" && r.path === "/api/tasks/2")?.body.name, "Other");
  });
}

test("a refused registration is drift, never an aborted repo", async () => {
  const result = await runSyncTasks("promote-409", [task(1, "#42 Foo", "")], [GH_ISSUE], { registerLinkStatus: 409 });
  assert.equal(result.linksRegistered, 0);
  assert.equal(result.drift.length, 1);
  assert.match(result.drift[0], /legacy promotion refused/);
  assert.deepEqual(result.requests.filter((r) => r.method !== "GET" && !r.path.endsWith("/external-work-item-links/github")), [],
    "a rejected link registration must not be followed by changes to the unverified task");
});

test("--dry-run promotes nothing", async () => {
  const result = await runSyncTasks("promote-dry", [task(1, "#42 Foo", "")], [GH_ISSUE], { dryRun: true });
  assert.deepEqual(result.requests.filter((r) => r.method !== "GET"), [], "a dry run writes nothing at all");
  assert.equal(result.linksRegistered, 0);
});

// --- the ambiguous-failure message says only what was observed -----------------------------
//
// "State remains NeedsReview; no create was attempted." was appended to a .catch() that had
// just swallowed the failure. Both halves can be false, and the second one is what an operator
// reads while deciding whether to retry a create.

const DISPATCHED_OP = {
  operationId: OP,
  state: "Dispatched",
  dispatchedAt: "2026-09-01T10:00:00",
  leaseExpiresAt: "2026-09-01T10:02:00",
};
const PREPARED_OP = { operationId: OP, state: "Prepared", dispatchedAt: null, leaseExpiresAt: "2026-09-01T10:02:00" };

test("a persisted failure reports the state that was actually written", async () => {
  const { ambiguousFailureMessage } = await import("./portfolio-sync.mjs?msg-persisted");

  const dispatched = ambiguousFailureMessage(DISPATCHED_OP, "Scan found 2 clean matches.", {
    persisted: { ...DISPATCHED_OP, state: "NeedsReview" },
  });
  assert.match(dispatched, /State is now NeedsReview/);
  assert.match(dispatched, /a create MAY have reached GitHub/);
  assert.doesNotMatch(dispatched, /no create was attempted/,
    "from Dispatched, a create may well have reached GitHub");
  // NeedsReview is the one persisted outcome the operator can still act on, so the message
  // keeps the next step the old text carried.
  assert.match(dispatched, /Re-run --resume 11111111-2222-4333-8444-555555555555 to reconcile it\./);

  // RecordFailureAsync classifies a never-dispatched ambiguous failure as DefinitivePreCreate,
  // so the message must report DefinitiveFailure rather than assuming NeedsReview.
  const predispatch = ambiguousFailureMessage(PREPARED_OP, "Frozen body self-check failed.", {
    persisted: { ...PREPARED_OP, state: "DefinitiveFailure" },
  });
  assert.match(predispatch, /State is now DefinitiveFailure/);
  assert.match(predispatch, /no create was attempted/);
  assert.doesNotMatch(predispatch, /NeedsReview/);
  assert.doesNotMatch(predispatch, /Re-run --resume/,
    "a DefinitiveFailure needs no re-run hint: Resume restores it to Prepared on the next ordinary run");
});

test("a failed persist never claims the state was saved", async () => {
  const { ambiguousFailureMessage } = await import("./portfolio-sync.mjs?msg-failed");

  const failed = ambiguousFailureMessage(DISPATCHED_OP, "Scan was incomplete.", {
    persisted: null,
    error: new Error("POST /failure -> 409 Conflict: operation-lease-expired"),
    observed: DISPATCHED_OP,
  });
  assert.match(failed, /State could NOT be persisted/);
  assert.match(failed, /operation-lease-expired/);
  assert.match(failed, /It remains Dispatched/);
  assert.match(failed, /its lease expires 2026-09-01T10:02:00/);
  assert.match(failed, /Re-run --resume 11111111-2222-4333-8444-555555555555/);
  // The two sentences the old text asserted unconditionally, both refuted here.
  assert.doesNotMatch(failed, /State is now/);
  assert.doesNotMatch(failed, /no create was attempted/);

  // recordAmbiguousWriteFailure returns null WITHOUT sending anything when there is no lease
  // token -- the second way the old sentence lied, and indistinguishable from success.
  const noLease = ambiguousFailureMessage(DISPATCHED_OP, "Scan was incomplete.", {
    persisted: null,
    skipped: "no-lease-token",
    observed: DISPATCHED_OP,
  });
  assert.match(noLease, /no lease token was held, so nothing was sent/);
  assert.doesNotMatch(noLease, /State is now/);
});

test("recordAmbiguousOutcome reports what happened instead of swallowing it", async () => {
  const mod = await import("./portfolio-sync.mjs?msg-outcome");
  const calls = [];

  const failing = {
    failure: async () => { calls.push("failure"); throw new Error("POST -> 409 Conflict: operation-lease-expired"); },
    getOperation: async () => { calls.push("getOperation"); return DISPATCHED_OP; },
  };
  const realError = console.error;
  console.error = () => {};
  let failedOutcome;
  try {
    failedOutcome = await mod.recordAmbiguousOutcome(DISPATCHED_OP, "lease-1", "reason", failing);
  } finally {
    console.error = realError;
  }
  assert.equal(failedOutcome.persisted, null);
  assert.match(failedOutcome.error.message, /operation-lease-expired/);
  assert.equal(failedOutcome.observed.state, "Dispatched");
  assert.deepEqual(calls, ["failure", "getOperation"]);

  // No lease token: nothing is sent at all, and that is reported rather than returned as null.
  calls.length = 0;
  const skipped = await mod.recordAmbiguousOutcome(DISPATCHED_OP, null, "reason", failing);
  assert.equal(skipped.skipped, "no-lease-token");
  assert.deepEqual(calls, ["getOperation"], "no failure request is sent without a lease token");

  // And the success path still reports the persisted row.
  const ok = {
    failure: async () => ({ ...DISPATCHED_OP, state: "NeedsReview" }),
    getOperation: async () => { throw new Error("must not be called"); },
  };
  const persisted = await mod.recordAmbiguousOutcome(DISPATCHED_OP, "lease-1", "reason", ok);
  assert.equal(persisted.persisted.state, "NeedsReview");
});

test("failure recording sends complete evidence through the real HTTP adapter", async () => {
  const mod = await import("./portfolio-sync.mjs?full-failure-evidence");
  const evidence = `\r\n    Trace 漢字 🧪\r\n${"x".repeat(4_001)}\r\n    Root cause at the end  \r\n\t`;
  const calls = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    calls.push({ url: String(url), method: init.method, body: JSON.parse(init.body) });
    return Response.json({ ...DISPATCHED_OP, state: "NeedsReview", lastError: JSON.parse(init.body).error });
  };
  try {
    const outcome = await mod.recordAmbiguousOutcome(DISPATCHED_OP, "lease-full", evidence);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, "POST");
    assert.ok(calls[0].url.endsWith(`/api/external-write-operations/${OP}/failure`));
    assert.equal(calls[0].body.error, evidence);
    assert.equal(outcome.persisted.lastError, evidence);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("failure recording preserves complete HTTP rejection details", async () => {
  const mod = await import("./portfolio-sync.mjs?full-http-failure");
  const details = `\r\n    Validation detail 漢字 🧪\r\n${"x".repeat(301)}\r\n    Final explanation  \r\n\t`;
  const originalFetch = globalThis.fetch;
  const originalError = console.error;
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push(init.method);
    return init.method === "POST"
      ? new Response(details, { status: 409, statusText: "Conflict" })
      : Response.json(DISPATCHED_OP);
  };
  console.error = () => {};
  try {
    const outcome = await mod.recordAmbiguousOutcome(DISPATCHED_OP, "lease-1", "reason");
    assert.equal(outcome.persisted, null);
    assert.equal(outcome.error.message, `POST /api/external-write-operations/${OP}/failure -> 409 Conflict: ${details}`);
    assert.equal(outcome.observed.state, "Dispatched");
    assert.deepEqual(calls, ["POST", "GET"]);
  } finally {
    globalThis.fetch = originalFetch;
    console.error = originalError;
  }
});

test("the create window's failure text describes the row, not a hope", async () => {
  const mod = await import("./portfolio-sync.mjs?msg-createwindow");
  // The snapshot the create window holds predates dispatch, so "no create was attempted" would
  // be inferred from stale data. This handler is only reachable past externalApi.dispatch.
  const api = {
    getOperation: async () => ({ ...PREPARED_OP, state: "Dispatched", dispatchedAt: "2026-09-01T10:00:00" }),
    failure: async () => { throw new Error("POST -> 409 Conflict: operation-lease-expired"); },
  };
  const realError = console.error;
  console.error = () => {};
  try {
    await assert.rejects(
      () => mod.handleCreateWindowFailure(PREPARED_OP, "lease-1", null, new Error("gh exploded"), api),
      (err) => {
        assert.match(err.message, /State could NOT be persisted/);
        assert.match(err.message, /a create MAY have reached GitHub/);
        assert.doesNotMatch(err.message, /no create was attempted/);
        return true;
      },
    );
  } finally {
    console.error = realError;
  }
});

// --- the agent-facing skill teaches the model this PR actually ships ------------------------
//
// SKILL.md is the first thing every future agent loads, so a stale contract there is not a
// documentation nit: it is the model the next contributor codes against. Both sentences below
// were true before #336 and are false after it, and neither is reachable from any code path, so
// nothing else in this suite would ever notice them rotting back in.
import { readFileSync } from "node:fs";
import { fileURLToPath as toPath } from "node:url";

const skillDir = new URL("../.claude/skills/kanban-portfolio/", import.meta.url);
const readSkill = (name) => readFileSync(toPath(new URL(name, skillDir)), "utf8");

test("the kanban-portfolio skill no longer teaches the pre-#336 contracts", () => {
  const skill = readSkill("SKILL.md");
  const mapping = readSkill("reference/mapping.md");

  // Contract 1: the sync no longer "never writes to GitHub" -- push-issue --allow-write --task
  // performs one additive create.
  assert.doesNotMatch(skill, /never writes to GitHub/);
  assert.match(skill, /exactly one write surface, and it is additive/);
  assert.match(skill, /push-issue owner\/repo\s*\n?--allow-write --task <id>/);

  // Contract 2: the "#<n> " prefix is no longer the idempotency key.
  for (const [name, text] of [["SKILL.md", skill], ["reference/mapping.md", mapping]]) {
    assert.doesNotMatch(text, /is the idempotency key/, `${name} still calls the prefix the key`);
    assert.match(text, /externalNodeId/, `${name} must name the durable identity`);
  }
  assert.match(skill, /legacy display-only fallback/);
  assert.match(mapping, /the legacy fallback/);

  // And it tells an agent what an upgraded board actually does, since that is the question a
  // stale doc leaves them guessing at.
  assert.match(skill, /promotes/);
  assert.match(mapping, /backfill-links owner\/repo/);
});
