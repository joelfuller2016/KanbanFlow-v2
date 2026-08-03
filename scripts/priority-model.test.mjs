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
  SEVERITY_RANK,
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

// A FROZEN label -> priority corpus: every label vocabulary the six DEFAULT_REPOS actually
// carry, plus topical decoys, with the value master's `priorityFromLabel` returned for each.
//
// Frozen as literals on purpose. The obvious way to write the differential below is to call the
// live `priorityFromLabel()` on both sides — but then a regression IN that function produces the
// same wrong answer on both sides and the differential stays green (CodeRabbit, PR #360). These
// literals are the independent oracle that closes that hole.
//
// `P4`/`P9` are in the corpus for a specific reason: without them, widening the label regex
// `^p([0-3])\b` to `^p([0-9])\b` is inert over this vocabulary — every P0-P3 label still resolves
// the same way, so the mutation is undetectable by construction rather than by a gap in the
// oracle. With them, the widened regex returns `undefined` for `P4` where the frozen value is
// `null`, and the mutation goes red. An oracle you cannot make fail is not an oracle.
const LEGACY_LABEL_PRIORITY = new Map([
  ["P0", "Critical"], ["P1", "High"], ["P2", "Medium"], ["P3", "Low"],
  ["P4", null], ["P9", null], ["P4-urgent", null],
  ["P0-critical", "Critical"], ["P1-high", "High"], ["P2-medium", "Medium"], ["P3-low", "Low"],
  ["priority:critical", "Critical"], ["priority:high", "High"],
  ["priority:medium", "Medium"], ["priority:low", "Low"],
  ["priority-high", "High"], ["priority-medium", "Medium"], ["priority-critical", "Critical"],
  ["high-priority", "High"], ["medium-priority", "Medium"], ["low-priority", "Low"],
  ["critical", "Critical"], ["high", "High"], ["medium", "Medium"], ["low", "Low"],
  ["bug", null], ["enhancement", null], ["epic", null], ["tech-debt", null],
  ["documentation", null], ["critical-path", null], ["size/medium", null], ["slow", null],
]);

// The pre-#355 implementation, transcribed verbatim from
// `git show origin/master:scripts/portfolio-sync.mjs` (lines 275-283), except that it reads the
// frozen corpus instead of the live label parser. AC3 says label-carrying repos must behave
// exactly as before; a hardcoded expectation only asserts that, so DIFFERENTIAL against this.
function oldDerivePriority(issue) {
  let best = null;
  for (const label of issue.labels || []) {
    const level = LEGACY_LABEL_PRIORITY.get(label.name) ?? null;
    if (level && (best === null || SEVERITY_RANK[level] < SEVERITY_RANK[best])) best = level;
  }
  return best ?? "Medium";
}

test("the frozen corpus still describes the live label parser", () => {
  // Guards the oracle itself: if `priorityFromLabel` changes, this names the label that moved
  // rather than letting the differential below quietly compare two copies of the new behaviour.
  for (const [label, expected] of LEGACY_LABEL_PRIORITY) {
    assert.equal(priorityFromLabel(label), expected, `label "${label}" changed meaning`);
  }
});

test("AC3 (differential): with no title prefix, the new chain matches the OLD function exactly", () => {
  let compared = 0;
  for (const a of LEGACY_LABEL_PRIORITY.keys()) {
    for (const b of LEGACY_LABEL_PRIORITY.keys()) {
      // No milestone: isolates the label tier, which is the tier AC3 is about.
      const i = issue({ title: "a title with no prefix", labels: [a, b] });
      assert.equal(derivePriority(i), oldDerivePriority(i), `labels [${a}, ${b}] diverged`);
      compared++;
    }
  }
  assert.ok(compared >= 900, `expected a real sweep, compared only ${compared}`);
});

test("AC3: with a priority label and no prefix, the label still decides (unchanged behaviour)", () => {
  assert.equal(derivePriority(issue({ title: "plain", labels: ["priority:high"] })), "High");
  assert.equal(derivePriority(issue({ title: "plain", labels: ["P0-critical"] })), "Critical");
  assert.equal(derivePriority(issue({ title: "plain", labels: ["low-priority"] })), "Low");
  assert.equal(derivePriority(issue({ title: "plain", labels: ["critical"] })), "Critical");
});

test("highest severity wins across multiple labels", () => {
  assert.equal(derivePriority(issue({ title: "x", labels: ["bug", "P0-critical", "low"] })), "Critical");
});

test("a label named after an Object prototype member is not a priority", () => {
  // A bare `SEVERITY_WORDS[n]` lookup walks the prototype chain, so these returned truthy
  // non-strings (Object.prototype / the Object function) and the sync would PUT one as the
  // priority. Pre-existing on master; closed when this module became the canonical definition.
  for (const name of ["__proto__", "constructor", "toString", "hasOwnProperty", "valueOf"]) {
    assert.equal(priorityFromLabel(name), null, `label "${name}" must not resolve to a priority`);
  }
  assert.equal(derivePriority(issue({ title: "x", labels: ["__proto__"] })), "Medium");
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

// NOTE the honest scope: this pins the table to the values recorded HERE. It goes red if
// MILESTONE_BANDS changes (proven by mutation), but it cannot see the backlog generator's copy
// — that file does not exist on master yet (#339). Import it here once it lands.
test("milestone bands match the M1..M7 -> P0..P3 table recorded in this test", () => {
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
