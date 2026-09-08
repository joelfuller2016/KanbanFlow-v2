import { test } from "node:test";
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import {
  commentTextFor, completeCommentIds, descriptionFor, descriptionRefreshFor,
  needsContentRestoration, parseArgs, restoreContent, syncComments,
} from "./portfolio-sync.mjs";

const FOOTER = "…(truncated — see the issue)";
const COMMENT_FOOTER = "…(truncated — read it on GitHub)";
const issue = (body, number = 488) => ({ number, title: "Keep the complete source", body,
  url: `https://github.com/owner/repository/issues/${number}`, state: "OPEN",
  milestone: { title: "M1" }, labels: [{ name: "bug" }] });
const bodyOf = (description) => description.slice(description.indexOf("<!-- /kanbanflow-sync -->\n\n") + "<!-- /kanbanflow-sync -->\n\n".length);
const legacy = (body, number = 488) => `<!-- kanbanflow-sync -->\n**Issue:** https://github.com/owner/repository/issues/${number}\n**State:** OPEN · **Milestone:** M1 · **Labels:** bug\n**Open PR:** none\n**Synced:** 2026-08-30T12:00:00.000Z\n<!-- /kanbanflow-sync -->\n\n${body}`;

test("descriptionFor preserves the entire large Unicode Markdown body including whitespace and tail", () => {
  const body = `  \r\n# Full issue\r\n\r\n\`\`\`json\r\n${'{"path":"C:\\\\work", "text":"漢字🙂"}\r\n'.repeat(4000)}\`\`\`\r\nLAST REQUIRED ACCEPTANCE CHECK  \r\n\t`;
  assert.ok(Buffer.byteLength(JSON.stringify(body), "utf8") > 29491);
  const saved = descriptionFor(issue(body), { url: "https://github.com/owner/repository/pull/511", isDraft: true });
  assert.equal(bodyOf(saved), body);
  assert.ok(saved.includes("**Open PR:** https://github.com/owner/repository/pull/511 (draft)"));
  assert.equal(needsContentRestoration(saved), false);
});

test("descriptionFor preserves empty and whitespace-only source bodies without normalizing them", () => {
  for (const body of ["", " \t\r\n ", "single line  ", "\nleading and trailing\n", null]) {
    assert.equal(bodyOf(descriptionFor(issue(body), null)), body ?? "");
  }
});

test("large provenance is retained with the complete body instead of a replacement stub", () => {
  const source = issue("DO NOT LOSE THIS BODY\n");
  source.labels = [{ name: "label".repeat(9000) }];
  const saved = descriptionFor(source, null);
  assert.ok(saved.includes(source.labels[0].name));
  assert.ok(saved.includes(source.url));
  assert.equal(bodyOf(saved), source.body);
});

test("legacy limits cannot shorten a new issue or comment", async () => {
  const previous = process.env.KANBANFLOW_BODY_EXCERPT_LIMIT;
  process.env.KANBANFLOW_BODY_EXCERPT_LIMIT = "1024";
  try {
    const module = await import("./portfolio-sync.mjs?legacy-limit");
    const body = "row 漢🙂\\\"\n".repeat(8000) + "FINAL TEXT  \n";
    assert.equal(bodyOf(module.descriptionFor(issue(body), null)), body);
    const comment = module.commentTextFor({ ghId: "IC_full", header: "source", body }, 10);
    assert.ok(comment.endsWith(body));
    assert.equal(comment.split("source\n\n")[1], body);
    assert.match(parseArgs(["sync", "--max-comment-chars", "10"]).error, /full comment text must be preserved/);
  } finally {
    if (previous === undefined) delete process.env.KANBANFLOW_BODY_EXCERPT_LIMIT;
    else process.env.KANBANFLOW_BODY_EXCERPT_LIMIT = previous;
  }
});

test("matching legacy prefixes restore complete text once", () => {
  const body = "  original prefix\ncontinued source\nTHE FINAL REQUIREMENT  \n";
  const complete = descriptionFor(issue(body), null);
  assert.equal(descriptionRefreshFor(legacy(`original prefix\n\n${FOOTER}`), complete), complete);
  assert.equal(descriptionRefreshFor(complete, descriptionFor(issue("changed remote text"), null)), null);
});

test("synthetic closing fences do not become duplicated local text during repair", () => {
  for (const fence of ["```", "~~~~"]) {
    const body = `intro\n${fence}js\nconst first = 1;\nconst last = 2;\n${fence}\nfooter\n`;
    const old = legacy(`intro\n${fence}js\nconst first = 1;\n${fence}\n\n${FOOTER}`);
    const complete = descriptionFor(issue(body), null);
    assert.equal(descriptionRefreshFor(old, complete), complete);
  }
});

test("differing prior board text and its Markdown whitespace survive full-source recovery", () => {
  const local = "Human decision: keep this behavior.  \n\n\t";
  const complete = descriptionFor(issue("The complete current GitHub issue.\n"), null);
  const restored = descriptionRefreshFor(legacy(`${local}\n\n${FOOTER}`), complete);
  assert.ok(restored.startsWith(complete));
  assert.ok(restored.includes(`\n\n${local}\n<!-- /kanbanflow-preserved-board-text -->`));
  assert.match(restored, /## Preserved prior board text/);
  assert.equal(needsContentRestoration(restored), false);
  assert.equal(descriptionRefreshFor(restored, complete), null);
});

test("repair preserves leading Markdown indentation added on the board", () => {
  const local = "    keep as code";
  const complete = descriptionFor(issue("keep as code\nrest of the current issue"), null);
  const restored = descriptionRefreshFor(legacy(`${local}\n\n${FOOTER}`), complete);
  assert.ok(restored.startsWith(complete));
  assert.ok(restored.includes(`\n\n${local}\n<!-- /kanbanflow-preserved-board-text -->`));
});

test("ordinary complete content and marker-like human prose are not overwritten", () => {
  const complete = descriptionFor(issue("new source"), null);
  for (const old of [legacy("Human rewrite with no cutoff"), `notes\n\n${FOOTER}`, "<!-- kanbanflow-sync -->\nmalformed\n" + FOOTER]) {
    assert.equal(descriptionRefreshFor(old, complete), null);
  }
  assert.equal(descriptionRefreshFor(legacy(`old\n\n${FOOTER}`, 100), complete), null);
  assert.equal(descriptionRefreshFor(legacy(`old\n\n${FOOTER}`), legacy(`new\n\n${FOOTER}`)), null);
});

test("a complete source may itself quote the old cutoff marker without being treated as incomplete", () => {
  const complete = descriptionFor(issue(`A bug report can end with the literal text\n${FOOTER}`), null);
  assert.equal(needsContentRestoration(complete), false);
  assert.equal(bodyOf(complete), `A bug report can end with the literal text\n${FOOTER}`);
  assert.equal(descriptionRefreshFor(complete, descriptionFor(issue("later source"), null)), null);
});

test("a legacy omitted-provenance stub can be recovered from its task identity", () => {
  const stub = "<!-- kanbanflow-sync -->\n**Issue:** (provenance omitted: header exceeded description budget)\n<!-- /kanbanflow-sync -->";
  const complete = descriptionFor(issue("full source"), null);
  assert.equal(needsContentRestoration(stub), true);
  assert.equal(descriptionRefreshFor(stub, complete), complete);
});

test("an incomplete comment does not block a full additive repair, which then deduplicates", () => {
  const old = { text: `<!-- gh:IC_old -->\nsource\n\npartial\n\n${COMMENT_FOOTER}` };
  assert.equal(completeCommentIds([old]).has("IC_old"), false);
  const body = " complete 漢字🙂\n".repeat(6000) + "final requirement  \n";
  const restored = { text: commentTextFor({ ghId: "IC_old", header: "source", body }) };
  assert.ok(restored.text.endsWith(body));
  assert.deepEqual([...completeCommentIds([old, restored])], ["IC_old"]);
  assert.equal(old.text, `<!-- gh:IC_old -->\nsource\n\npartial\n\n${COMMENT_FOOTER}`);
  const literal = { text: commentTextFor({ ghId: "IC_literal", header: "source", body: COMMENT_FOOTER }) };
  assert.equal(completeCommentIds([literal]).has("IC_literal"), true);
  assert.deepEqual([...completeCommentIds([{ text: "ordinary note" }, { text: "<!-- gh:IC_existing -->\nold complete copy" }])], ["IC_existing"]);
});

function fixture({ taskChanges = {}, sourceMissing = false, conflictingProject = false, mutateBeforeWrite = false, corruptSave = false, projectDescription = "<!-- github: owner/repository -->" } = {}) {
  const source = issue("old prefix\n" + "full source 漢🙂\n".repeat(3000) + "END  \n");
  let task = { id: 7, name: "#488 Human-kept task title", description: legacy(`old prefix\n\n${FOOTER}`), updatedAt: "before", status: "Review", priority: "High", featureId: 4, sortOrder: 2.5, assignee: "Human", ...taskChanges };
  const calls = [];
  const request = async (method, path, body) => {
    calls.push({ method, path, body });
    if (method === "GET" && path === "/api/projects") {
      const project = { id: 2, name: "repository", description: projectDescription };
      return conflictingProject ? [project, { ...project, id: 3 }] : [project];
    }
    if (method === "GET" && path === "/api/projects/2/tasks") return [{ ...task }];
    if (method === "GET" && path === "/api/tasks/7") return { ...task, ...(mutateBeforeWrite ? { description: "human changed this while the repair was prepared" } : {}) };
    if (method === "PUT" && path === "/api/tasks/7") {
      assert.deepEqual(Object.keys(body).sort(), ["actor", "description", "name"]);
      task = { ...task, name: body.name, description: corruptSave ? "partial" : body.description, updatedAt: "after" };
      return { ...task };
    }
    throw new Error(`Unexpected request ${method} ${path}`);
  };
  let sourceReads = 0;
  const readSources = async () => { sourceReads++; return { issues: sourceMissing ? [] : [source], prByIssue: new Map() }; };
  return { request, readSources, calls, source, getTask: () => task, sourceReads: () => sourceReads };
}

test("restore-content dry-run reads real candidates but performs no mutation", async () => {
  const io = fixture();
  const before = structuredClone(io.getTask());
  const result = await restoreContent("owner/repository", { dryRun: true, actor: "repair" }, io);
  assert.deepEqual(result, { candidates: 1, restored: 1, preservedPriorText: 0 });
  assert.ok(io.calls.every(c => c.method === "GET"));
  assert.deepEqual(io.getTask(), before);
  assert.equal(io.sourceReads(), 1);
});

test("restore-content changes only an existing description and its audit actor, then becomes a no-op", async () => {
  const io = fixture();
  const before = structuredClone(io.getTask());
  await restoreContent("owner/repository", { dryRun: false, actor: "repair" }, io);
  assert.equal(bodyOf(io.getTask().description), io.source.body);
  assert.deepEqual({ ...io.getTask(), description: before.description, updatedAt: before.updatedAt }, before);
  assert.equal(io.calls.filter(c => c.method === "PUT").length, 1);
  assert.equal(io.calls.find(c => c.method === "PUT").body.actor, "repair");
  const writes = io.calls.filter(c => c.method !== "GET").length;
  const second = await restoreContent("owner/repository", { dryRun: false, actor: "repair" }, io);
  assert.equal(second.candidates, 0);
  assert.equal(second.restored, 0);
  assert.equal(io.calls.filter(c => c.method !== "GET").length, writes);
  assert.equal(io.sourceReads(), 1);
});

test("content restoration refuses ambiguous projects, missing source and concurrent board changes before writing", async () => {
  for (const options of [{ conflictingProject: true }, { sourceMissing: true }, { mutateBeforeWrite: true }]) {
    const io = fixture(options);
    await assert.rejects(restoreContent("owner/repository", { dryRun: false, actor: "repair" }, io));
    assert.ok(io.calls.every(c => c.method === "GET"));
  }
});

test("content restoration fails when persistence does not return the complete text", async () => {
  const io = fixture({ corruptSave: true });
  await assert.rejects(restoreContent("owner/repository", { dryRun: false, actor: "repair" }, io), /full-content read-back failed/);
  assert.equal(io.calls.filter(c => c.method === "PUT").length, 1);
});

test("content restoration preserves board text whose source changed and leaves foreign provenance untouched", async () => {
  const io = fixture({ taskChanges: { description: legacy(`local decision  \n\n${FOOTER}`) } });
  const result = await restoreContent("owner/repository", { dryRun: false, actor: "repair" }, io);
  assert.equal(result.preservedPriorText, 1);
  assert.ok(io.getTask().description.includes(io.source.body));
  assert.ok(io.getTask().description.includes("local decision  \n<!-- /kanbanflow-preserved-board-text -->"));
  const foreign = fixture({ taskChanges: { description: legacy(`old\n\n${FOOTER}`).replace("owner/repository/issues/", "someone/else/issues/") } });
  await assert.rejects(restoreContent("owner/repository", { dryRun: false, actor: "repair" }, foreign), /different repository/);
  assert.ok(foreign.calls.every(c => c.method === "GET"));
});

test("title-only stub repair requires a durable matching repository marker", async () => {
  const description = "<!-- kanbanflow-sync -->\n**Issue:** (provenance omitted: header exceeded description budget)\n<!-- /kanbanflow-sync -->";
  const unmarked = fixture({ taskChanges: { description }, projectDescription: "unmarked repository" });
  await assert.rejects(restoreContent("owner/repository", { dryRun: false, actor: "repair" }, unmarked), /durable matching project marker/);
  assert.ok(unmarked.calls.every(c => c.method === "GET"));
  const marked = fixture({ taskChanges: { description } });
  await restoreContent("owner/repository", { dryRun: false, actor: "repair" }, marked);
  assert.equal(bodyOf(marked.getTask().description), marked.source.body);
});

test("comment sync appends an incomplete source once and does not duplicate a complete legacy literal", async () => {
  const url = "https://github.com/owner/repository/issues/488#issuecomment-1";
  const fullBody = "Full comment 漢🙂\n".repeat(4000) + "required final sentence  \n";
  const incoming = { ...issue("source"), comments: [
    { id: "IC_repair", url, body: fullBody, author: { login: "octocat" } },
    { id: "IC_literal", url, body: COMMENT_FOOTER, author: { login: "octocat" } },
  ] };
  const oldText = `<!-- gh:IC_repair -->\n_[issue comment](${url})_\n\npartial\n\n${COMMENT_FOOTER}`;
  const literalText = `<!-- gh:IC_literal -->\n_[issue comment](${url})_\n\n${COMMENT_FOOTER}`;
  const stored = [{ text: oldText }, { text: literalText }];
  const writes = [];
  const request = async (method, path, body) => {
    assert.equal(path, "/api/tasks/7/comments");
    if (method === "GET") return structuredClone(stored);
    assert.equal(method, "POST");
    writes.push(body);
    stored.push({ text: body.text });
    return { id: 99 };
  };
  await syncComments(incoming, null, 7, { dryRun: true }, request);
  assert.equal(writes.length, 0);
  await syncComments(incoming, null, 7, { dryRun: false }, request);
  assert.equal(writes.length, 1);
  assert.ok(writes[0].text.endsWith(fullBody));
  assert.equal(writes[0].author, "octocat");
  assert.equal(stored[0].text, oldText);
  assert.equal(stored[1].text, literalText);
  await syncComments(incoming, null, 7, { dryRun: false }, request);
  assert.equal(writes.length, 1);
});

test("restore-content CLI refuses an omitted repo before reading an environment portfolio", async () => {
  const rejectFetch = "globalThis.fetch = async () => { throw new Error('UNEXPECTED_API_REQUEST'); };";
  const cli = fileURLToPath(new URL("./portfolio-sync.mjs", import.meta.url));
  await assert.rejects(promisify(execFile)(process.execPath,
    ["--import", `data:text/javascript,${encodeURIComponent(rejectFetch)}`, cli, "restore-content"], {
      env: { ...process.env, KANBANFLOW_API_URL: "http://127.0.0.1:1", KANBANFLOW_PORTFOLIO_REPOS: "owner/should-not-be-read" },
      timeout: 10_000,
      windowsHide: true,
    }), (error) => {
      assert.equal(error.code, 2);
      assert.match(error.stderr, /`restore-content` needs an explicit repo/);
      assert.doesNotMatch(error.stderr, /UNEXPECTED_API_REQUEST/);
      return true;
    });
});

function batchFixture({ missingSecondSource = false, changeSecondBeforeWrite = false } = {}) {
  const sources = [issue("first prefix\nFirst complete source 漢🙂\nFINAL FIRST  \n", 488),
    { ...issue("second prefix\nSecond complete source\r\nFINAL SECOND\t\n", 489), state: "CLOSED" }];
  const tasks = new Map(sources.map((source, index) => [7 + index, {
    id: 7 + index, name: `#${source.number} Local title ${index + 1}`,
    description: legacy(`${index === 0 ? "first" : "second"} prefix\n\n${FOOTER}`, source.number),
    updatedAt: `before-${7 + index}`, status: index === 0 ? "Review" : "Todo",
    priority: index === 0 ? "High" : "Low", featureId: 4 + index, sortOrder: index + 0.5, assignee: "Human",
  }]));
  const correction = "Human correction during preparation  ";
  const calls = [];
  let changePending = changeSecondBeforeWrite;
  let sourceReads = 0;
  const request = async (method, path, body) => {
    calls.push({ method, path, body: body && structuredClone(body) });
    if (method === "GET" && path === "/api/projects") return [{ id: 2, name: "repository", description: "<!-- github: owner/repository -->" }];
    if (method === "GET" && path === "/api/projects/2/tasks") return structuredClone([...tasks.values()]);
    const id = Number(/^\/api\/tasks\/(\d+)$/.exec(path)?.[1]);
    const task = tasks.get(id);
    if (!task) throw new Error(`Unexpected request ${method} ${path}`);
    if (method === "GET") {
      if (id === 8 && changePending) {
        task.description = legacy(`${correction}\n\n${FOOTER}`, 489);
        task.updatedAt = "human-revision";
        changePending = false;
      }
      return structuredClone(task);
    }
    assert.equal(method, "PUT");
    assert.deepEqual(Object.keys(body).sort(), ["actor", "description", "name"]);
    tasks.set(id, { ...task, name: body.name, description: body.description, updatedAt: `saved-${id}` });
    return structuredClone(tasks.get(id));
  };
  const readSources = async () => {
    sourceReads++;
    // Reverse the source order so selecting an issue by array position corrupts a tested result.
    return { issues: missingSecondSource ? [sources[0]] : [...sources].reverse(), prByIssue: new Map() };
  };
  return { request, readSources, calls, sources, correction,
    getTasks: () => structuredClone([...tasks.values()]), sourceReads: () => sourceReads };
}

test("restore-content restores both candidates from their own sources without changing other fields", async () => {
  const io = batchFixture();
  const before = io.getTasks();
  const result = await restoreContent("owner/repository", { dryRun: false, actor: "batch-repair" }, io);
  assert.deepEqual(result, { candidates: 2, restored: 2, preservedPriorText: 0 });
  assert.deepEqual(io.calls.filter(c => c.method !== "GET").map(c => [c.method, c.path, c.body.actor]),
    [["PUT", "/api/tasks/7", "batch-repair"], ["PUT", "/api/tasks/8", "batch-repair"]]);
  for (const [index, task] of io.getTasks().entries()) {
    assert.equal(bodyOf(task.description), io.sources[index].body);
    assert.deepEqual({ ...task, description: before[index].description, updatedAt: before[index].updatedAt }, before[index]);
  }
  assert.equal(io.sourceReads(), 1);
});

test("restore-content preflights a missing second source before writing the valid first candidate", async () => {
  const io = batchFixture({ missingSecondSource: true });
  const before = io.getTasks();
  await assert.rejects(restoreContent("owner/repository", { dryRun: false, actor: "batch-repair" }, io),
    /Task 8: full GitHub issue 489 is unavailable/);
  assert.deepEqual(io.calls.filter(c => c.method !== "GET"), []);
  assert.deepEqual(io.getTasks(), before);
  assert.equal(io.sourceReads(), 1);
});

test("restore-content retries a partially successful batch without rewriting the first task or losing a correction", async () => {
  const io = batchFixture({ changeSecondBeforeWrite: true });
  await assert.rejects(restoreContent("owner/repository", { dryRun: false, actor: "batch-repair" }, io),
    /Task 8 changed while the repair was being prepared/);
  const [firstSaved, secondChanged] = io.getTasks();
  assert.equal(bodyOf(firstSaved.description), io.sources[0].body);
  assert.equal(secondChanged.description, legacy(`${io.correction}\n\n${FOOTER}`, 489));
  assert.deepEqual(io.calls.filter(c => c.method !== "GET").map(c => c.path), ["/api/tasks/7"]);

  const retried = await restoreContent("owner/repository", { dryRun: false, actor: "batch-repair" }, io);
  assert.deepEqual(retried, { candidates: 1, restored: 1, preservedPriorText: 1 });
  const [firstAfterRetry, secondSaved] = io.getTasks();
  assert.deepEqual(firstAfterRetry, firstSaved);
  assert.ok(bodyOf(secondSaved.description).startsWith(`${io.sources[1].body}\n\n<!-- kanbanflow-preserved-board-text -->`));
  assert.ok(secondSaved.description.includes(`\n\n${io.correction}\n<!-- /kanbanflow-preserved-board-text -->`));
  assert.deepEqual({ ...secondSaved, description: secondChanged.description, updatedAt: secondChanged.updatedAt }, secondChanged);
  assert.deepEqual(io.calls.filter(c => c.method !== "GET").map(c => c.path), ["/api/tasks/7", "/api/tasks/8"]);

  const repeated = await restoreContent("owner/repository", { dryRun: false, actor: "batch-repair" }, io);
  assert.deepEqual(repeated, { candidates: 0, restored: 0, preservedPriorText: 0 });
  assert.deepEqual(io.calls.filter(c => c.method !== "GET").map(c => c.path), ["/api/tasks/7", "/api/tasks/8"]);
  assert.equal(io.sourceReads(), 2);
});
