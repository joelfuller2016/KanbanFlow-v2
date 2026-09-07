import { test } from "node:test";
import assert from "node:assert/strict";

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
