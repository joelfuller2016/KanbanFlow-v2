// Tests that the backlog log book and the board agree about priority (#364).
//
// Real code, no stubs: every assertion calls the exported functions the generator itself uses,
// with plain issue objects shaped exactly like the `gh issue list --json
// number,title,labels,milestone` payload it is really fed.
//
// The defect these pin: `backlog-sync.mjs` used to derive its own tier and then adjust it by
// TOPICAL label — `bug`/`dependencies`/`security` promoted a step, docs-only demoted a step —
// while `portfolio-sync.mjs` used the shared model, which does not. Measured against the live
// backlog the two disagreed on 13 of 119 open issues (10.9%). #355 forbids the adjustment: a
// topical label is a KIND, not an urgency.
//
// Run: node --test scripts/

import { test } from "node:test";
import assert from "node:assert/strict";
import { tierOfIssue, milestoneOf, signalRank, tierIdForMilestone, MILESTONES } from "./backlog-sync.mjs";
import { derivePriority, PN_LEVELS, priorityFromMilestone } from "./priority-model.mjs";

const issue = ({ number = 1, title = "untitled", labels = [], milestone = null } = {}) => ({
  number,
  title,
  labels: labels.map((name) => ({ name })),
  milestone: milestone ? { title: milestone } : null,
});

const boardTier = (i) => `P${PN_LEVELS.indexOf(derivePriority(i))}`;

// ── The invariant: one rule, two consumers ────────────────────────────────────────────────

// Every case here is a real shape from the live backlog at the time #364 was measured. The four
// marked "was a disagreement" are the ones this change fixes; including the agreeing cases too
// means a regression that broke them would also be caught, not just the ones that were broken.
const CASES = [
  // was a disagreement: bug + M4 -> board P2, log book promoted to P1
  { number: 367, title: "a11y: white text on --bs-primary measures 4.24:1", labels: ["ui", "bug"], milestone: "M4: UI / UX Polish" },
  { number: 359, title: "Timeline renders every description change as X → X", labels: ["bug", "ui"], milestone: "M4: UI / UX Polish" },
  // was a disagreement: bug + M2 -> board P1, log book promoted to P0
  { number: 330, title: "Some tech-debt defect", labels: ["bug", "tech-debt"], milestone: "M2: Tech-debt & Foundation" },
  // was a disagreement: documentation-only -> board P2, log book demoted to P3
  { number: 277, title: "A docs-only issue", labels: ["documentation"], milestone: "M5: Docs & Process" },
  // agreeing shapes, kept so a regression cannot hide behind the fixed cases
  { number: 189, title: "P2: an issue that states its own priority", labels: ["bug"], milestone: "M1: Correctness & Audit Trail" },
  { number: 100, title: "Ordinary build-out", labels: ["enhancement"], milestone: "M3: Agent API Surface" },
  { number: 101, title: "Speculative", labels: [], milestone: "M7: Self-Improving" },
  { number: 102, title: "Untriaged, no milestone", labels: ["bug"], milestone: null },
  { number: 103, title: "P0: explicit critical", labels: ["documentation"], milestone: "M7: Self-Improving" },
];

test("the log book's tier matches the shared priority model for every issue shape", () => {
  for (const c of CASES) {
    const i = issue(c);
    assert.equal(
      tierOfIssue(i), boardTier(i),
      `#${c.number} "${c.title}" [${c.labels.join(",")}] @ ${c.milestone ?? "no milestone"}: ` +
      `the log book and the board disagree. They must read the SAME rule — see #364.`,
    );
  }
});

// ── The specific rule that was wrong ──────────────────────────────────────────────────────

test("a topical label does not change the tier", () => {
  const milestone = "M4: UI / UX Polish";
  const plain = tierOfIssue(issue({ title: "a task", milestone }));

  for (const label of ["bug", "dependencies", "security", "tech-debt", "epic"]) {
    assert.equal(
      tierOfIssue(issue({ title: "a task", labels: [label], milestone })), plain,
      `the \`${label}\` label moved the tier off ${plain}. A topical label is a KIND, not an ` +
      `urgency (#355) — #189 is labelled \`bug\` and self-declares P2:.`,
    );
  }
});

test("a documentation-only issue is not demoted", () => {
  const milestone = "M5: Docs & Process";
  assert.equal(
    tierOfIssue(issue({ title: "a task", labels: ["documentation"], milestone })),
    tierOfIssue(issue({ title: "a task", milestone })),
    "a docs-only issue was demoted below its milestone band; the board does not do this",
  );
});

test("an explicit title prefix still wins outright", () => {
  // P0: in the title, on the LOWEST band, with a demoting label — the prefix must survive all of it.
  assert.equal(
    tierOfIssue(issue({ title: "P0: urgent", labels: ["documentation"], milestone: "M7: Self-Improving" })),
    "P0",
  );
});

// ── What deliberately survives: ordering within a band ────────────────────────────────────

test("signalRank still orders within a band — defects, epics, build-out, docs", () => {
  assert.equal(signalRank(new Set(["bug"])), 0);
  assert.equal(signalRank(new Set(["epic"])), 1);
  assert.equal(signalRank(new Set(["enhancement"])), 2);
  assert.equal(signalRank(new Set(["documentation"])), 3);
});

test("signalRank is ordering only — it cannot move an issue between tiers", () => {
  // Two issues in the same band with opposite signal ranks must still share a tier. This is the
  // line that must not be crossed: the moment `signal` feeds back into `tier`, the log book is a
  // second source of priority truth again.
  const milestone = "M4: UI / UX Polish";
  const defect = issue({ title: "a defect", labels: ["bug"], milestone });
  const docs = issue({ title: "a doc", labels: ["documentation"], milestone });

  assert.notEqual(signalRank(new Set(["bug"])), signalRank(new Set(["documentation"])));
  assert.equal(tierOfIssue(defect), tierOfIssue(docs));
});

// ── The loud-failure contract ─────────────────────────────────────────────────────────────

test("an unmapped milestone is refused, not silently rendered as 'no milestone'", () => {
  assert.throws(
    () => milestoneOf(issue({ number: 999, milestone: "M8: Something New" })),
    /M8: Something New.*does not know/s,
    "an unknown milestone must stop the generator rather than misreport the issue's state",
  );
});

test("no milestone is a legitimate state, not an error", () => {
  assert.equal(milestoneOf(issue({ milestone: null })).short, "—");
});

// ── The two milestone lists must not drift ────────────────────────────────────────────────
//
// Removing the duplicated `tier` column left a smaller coupling: this file lists the milestones
// it knows, `priority-model.mjs` owns their bands, and a milestone present in one but not the
// other is a silent inconsistency. This is the eager check — it fails on `node --test` rather
// than at render time, and long before anyone reads a wrong number in the log book.

test("every milestone this generator lists has a band in priority-model", () => {
  for (const m of MILESTONES) {
    assert.notEqual(
      priorityFromMilestone(m.title), null,
      `"${m.title}" is in MILESTONES (backlog-sync.mjs) but has no band in MILESTONE_BANDS ` +
      `(priority-model.mjs). The board and the log book would disagree about every issue in it.`,
    );
    assert.match(tierIdForMilestone(m.title), /^P[0-3]$/);
  }
});

test("a milestone with no band fails with a message naming both files, not a TypeError", () => {
  // The pre-fix behaviour was `TIERS[PN_LEVELS.indexOf(null)].id` -> TypeError on undefined,
  // raised from deep inside renderBody with nothing pointing at the real cause.
  assert.throws(
    () => tierIdForMilestone("M9: Invented For This Test"),
    (err) => {
      assert.ok(!(err instanceof TypeError), "should not be a bare TypeError");
      assert.match(err.message, /backlog-sync\.mjs/);
      assert.match(err.message, /priority-model\.mjs/);
      return true;
    },
  );
});
