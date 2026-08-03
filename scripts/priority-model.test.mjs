// Tests for the priority model (#355). Real code, no stubs: every assertion calls the exported
// function under test with a plain issue object shaped exactly like the `gh issue list --json
// number,title,labels,milestone` payload `portfolio-sync.mjs` actually feeds it.
//
// Run: node --test scripts/

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  derivePriority,
  priorityFromTitle,
  priorityFromLabel,
  priorityFromMilestone,
} from "./priority-model.mjs";

const issue = ({ title = "untitled", labels = [], milestone = null } = {}) => ({
  title,
  labels: labels.map((name) => ({ name })),
  milestone: milestone ? { title: milestone } : null,
});

// --- 1. The title prefix ------------------------------------------------------------------

test("title prefix maps P0..P3 to Critical/High/Medium/Low", () => {
  assert.equal(derivePriority(issue({ title: "P0: fix the thing" })), "Critical");
  assert.equal(derivePriority(issue({ title: "P1: fix the thing" })), "High");
  assert.equal(derivePriority(issue({ title: "P2: fix the thing" })), "Medium");
  assert.equal(derivePriority(issue({ title: "P3: fix the thing" })), "Low");
});

test("prefix accepts the separators the backlog generator accepts, and is case-insensitive", () => {
  for (const t of ["P0: x", "P0 : x", "p0- x", "P0 — x", "P0–x"]) {
    assert.equal(priorityFromTitle(t), "Critical", `expected a prefix match on ${JSON.stringify(t)}`);
  }
});

test("prefix is anchored: a title that merely CONTAINS the token is rejected", () => {
  // AC6. Each of these would match an unanchored /P([0-3])/ and mis-rank the issue.
  for (const t of ["P2P sync rewrite", "HTTP3: chunked", "SP1: sprint one", "not-P1: nope", "APP0: x"]) {
    assert.equal(priorityFromTitle(t), null, `expected NO prefix match on ${JSON.stringify(t)}`);
  }
});

test("a bare 'P1' with no separator is not a priority prefix", () => {
  assert.equal(priorityFromTitle("P1 without a separator"), null);
});

test("AC7: a severity word inside the title does not leak into the priority", () => {
  // #265's real title. `Critical-path` must not read as Critical; it has no prefix and no
  // priority label, so it resolves from its milestone band (M3 -> High).
  const i = issue({
    title: "Critical-path & slack signal over the dependency DAG (CPM forward/backward pass)",
    labels: ["enhancement", "api", "ai-agent"],
    milestone: "M3: Agent API Surface",
  });
  assert.equal(derivePriority(i), "High");
});

// --- 2. Labels: the pre-#355 path must be byte-identical ----------------------------------

test("AC3: with a priority label and no prefix, the label still decides (unchanged behaviour)", () => {
  assert.equal(derivePriority(issue({ title: "plain", labels: ["priority:high"] })), "High");
  assert.equal(derivePriority(issue({ title: "plain", labels: ["P0-critical"] })), "Critical");
  assert.equal(derivePriority(issue({ title: "plain", labels: ["low-priority"] })), "Low");
  assert.equal(derivePriority(issue({ title: "plain", labels: ["critical"] })), "Critical");
});

test("highest severity wins across multiple labels", () => {
  assert.equal(derivePriority(issue({ title: "x", labels: ["bug", "P0-critical", "low"] })), "Critical");
});

test("a topical label that merely contains a severity word is not a priority", () => {
  assert.equal(priorityFromLabel("critical-path"), null);
  assert.equal(priorityFromLabel("size/medium"), null);
  assert.equal(priorityFromLabel("slow"), null);
});

test("AC4: prefix outranks a conflicting priority label", () => {
  const i = issue({ title: "P1: urgent", labels: ["priority:low"] });
  assert.equal(derivePriority(i), "High");
});

// --- 3. Milestone band --------------------------------------------------------------------

test("milestone bands match the backlog generator's M1..M7 -> P0..P3 table", () => {
  const expected = [
    ["M1: Correctness & Audit Trail", "Critical"],
    ["M2: Tech-debt & Foundation", "High"],
    ["M3: Agent API Surface", "High"],
    ["M4: UI / UX Polish", "Medium"],
    ["M5: Docs & Process", "Medium"],
    ["M6: AI Memory & Ops Hub", "Medium"],
    ["M7: Self-Improving", "Low"],
  ];
  for (const [title, band] of expected) {
    assert.equal(priorityFromMilestone({ title }), band, `milestone ${title}`);
    assert.equal(derivePriority(issue({ title: "no prefix", milestone: title })), band);
  }
});

test("an unknown milestone falls through to Medium rather than guessing", () => {
  assert.equal(priorityFromMilestone({ title: "M9: Something New" }), null);
  assert.equal(derivePriority(issue({ title: "x", milestone: "M9: Something New" })), "Medium");
});

test("no prefix, no label, no milestone -> Medium", () => {
  assert.equal(derivePriority(issue({ title: "just a title" })), "Medium");
});

test("the label path outranks the milestone band", () => {
  const i = issue({ title: "x", labels: ["priority:low"], milestone: "M1: Correctness & Audit Trail" });
  assert.equal(derivePriority(i), "Low");
});

// --- 4. Shapes the real payload actually produces -------------------------------------------

test("tolerates the payload shapes gh emits", () => {
  assert.equal(derivePriority({ title: "P0: x" }), "Critical", "labels/milestone absent");
  assert.equal(derivePriority({ title: "x", labels: [], milestone: null }), "Medium");
  assert.equal(priorityFromTitle(null), null);
  assert.equal(priorityFromTitle(undefined), null);
  assert.equal(priorityFromMilestone(null), null);
  assert.equal(priorityFromMilestone(undefined), null);
});

test("accepts a milestone passed as a bare string as well as an object", () => {
  assert.equal(priorityFromMilestone("M1: Correctness & Audit Trail"), "Critical");
});

// --- 5. The regression this issue exists for -------------------------------------------------

test("REGRESSION #355: a repo with zero priority labels no longer collapses to one value", () => {
  // The KanbanFlow-v2 shape: no priority labels anywhere. Before #355 every one of these
  // returned "Medium"; the board rendered 91/91 Medium and /next-tasks ranked on a constant.
  const board = [
    issue({ title: "P1: smoke-api.ps1 is RED on master", labels: ["bug"], milestone: "M1: Correctness & Audit Trail" }),
    issue({ title: "REST add-comment sets audit Actor = author", labels: ["bug", "api"], milestone: "M1: Correctness & Audit Trail" }),
    issue({ title: "test: add WebApplicationFactory tests", labels: ["tech-debt"], milestone: "M2: Tech-debt & Foundation" }),
    issue({ title: "P2: Expose MCP resources alongside tools", labels: ["api"], milestone: "M3: Agent API Surface" }),
    issue({ title: "a11y: confirm-action modals", labels: ["enhancement"], milestone: "M4: UI / UX Polish" }),
    issue({ title: "Self-tuning recommender", labels: ["self-improving"], milestone: "M7: Self-Improving" }),
  ];
  const got = board.map(derivePriority);
  assert.deepEqual(got, ["High", "Critical", "High", "Medium", "Medium", "Low"]);

  const distinct = new Set(got);
  assert.ok(distinct.size >= 3, `expected a real spread, got ${JSON.stringify([...distinct])}`);

  // AC5, stated as the property rather than the count: no single value dominates.
  const counts = got.reduce((m, p) => m.set(p, (m.get(p) ?? 0) + 1), new Map());
  const top = Math.max(...counts.values());
  assert.ok(top / got.length <= 0.7, `one priority holds ${top}/${got.length}`);
});
