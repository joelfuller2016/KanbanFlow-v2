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
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { tierOfIssue, milestoneOf, signalRank, tierIdForMilestone, MILESTONES } from "./backlog-sync.mjs";
import { derivePriority, PN_LEVELS, priorityFromMilestone } from "./priority-model.mjs";

const execFileAsync = promisify(execFile);

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

// ── AC2, closed over the input SPACE rather than a sample ─────────────────────────────────
//
// The CASES table above is nine real shapes taken from the backlog as it stood when #364 was
// measured. That proves the two consumers agree on the issues that existed that day; it proves
// nothing about the ones filed tomorrow. #364's AC2 asks for agreement over the whole backlog,
// so close the gap from both ends: sweep the cross product of every signal that can reach either
// consumer, and then check the live backlog itself.

const TITLE_CASES = [
  "no prefix at all",
  "P0: explicit critical",
  "P1: explicit high",
  "P2: explicit medium",
  "P3: explicit low",
  "p1 — lowercase with an em dash",
  "P2P sync rewrite",          // decoy: contains the token, is not a prefix
  "HTTP3: chunked transfer",   // decoy: not at the start
];

const LABEL_CASES = [
  [],
  ["bug"],                          // promoted the tier before #364
  ["dependencies"],                 // promoted the tier before #364
  ["security"],                     // promoted the tier before #364
  ["documentation"],                // demoted the tier before #364 (docs-only)
  ["documentation", "bug"],         // not docs-only; promoted before #364
  ["epic"],
  ["tech-debt", "ui"],
  ["bug", "tech-debt"],             // #340 / #341 wear exactly this
  ["priority:high"],                // a real priority label (the other portfolio repos)
  ["P0-critical"],
  ["low-priority", "bug"],          // priority label + topical label
  ["critical-path", "size/medium"], // severity WORDS that are not priorities
];

const MILESTONE_CASES = [null, ...MILESTONES.map((m) => m.title)];

test("AC2 (space): the log book and the board agree on every combination of signals", () => {
  const disagreements = [];
  let compared = 0;
  let n = 0;
  for (const title of TITLE_CASES) {
    for (const labels of LABEL_CASES) {
      for (const milestone of MILESTONE_CASES) {
        const i = issue({ number: ++n, title, labels, milestone });
        compared++;
        if (tierOfIssue(i) !== boardTier(i)) {
          disagreements.push(
            `title=${JSON.stringify(title)} labels=[${labels.join(",")}] ` +
            `milestone=${milestone ?? "none"} -> book=${tierOfIssue(i)} board=${boardTier(i)}`,
          );
        }
      }
    }
  }
  assert.equal(
    disagreements.length, 0,
    `${disagreements.length}/${compared} combinations disagree:\n  ` + disagreements.join("\n  "),
  );
  // A sweep that quietly stopped sweeping is the failure this number exists to catch.
  assert.equal(compared, TITLE_CASES.length * LABEL_CASES.length * MILESTONE_CASES.length);
  assert.ok(compared >= 800, `expected a real sweep, compared only ${compared}`);
});

// ── AC1 structurally: no second copy of the rule, even one that AGREES today ───────────────
//
// The sweeps above catch a private rule that DISAGREES. They cannot catch one that agrees today
// and drifts next month — which is exactly how #364 was born: #360 transcribed #339's band table
// and all seven rows were identical, right up until they were not. So assert the structural
// property too.

const GENERATOR_SRC = await readFile(
  join(dirname(fileURLToPath(import.meta.url)), "backlog-sync.mjs"), "utf8",
);

test("AC1: the generator declares no private title-prefix regex and no tier-adjusting label set", () => {
  assert.ok(
    !/\/\^[^\n/]*P\[0-3\]/.test(GENERATOR_SRC),
    "backlog-sync.mjs declares its own P0-P3 title-prefix regex. priorityFromTitle() in " +
    "scripts/priority-model.mjs is the only one that may exist (#364 AC1).",
  );
  for (const gone of ["PROMOTING_LABELS", "DEMOTING_LABELS", "clampTier"]) {
    assert.ok(
      !GENERATOR_SRC.includes(gone),
      `backlog-sync.mjs still references ${gone} — the promote/demote step #364 removed.`,
    );
  }
});

test("AC1: the generator pairs no milestone title with a priority band of its own", () => {
  // MILESTONES legitimately keeps the titles — `milestoneOf` refuses an unmapped milestone with
  // them. What it must never regrow is a title -> Critical/High/Medium/Low column beside them.
  const bandLike = new RegExp(
    `"M[1-7]:[^"]*"\\s*(?::|,|=>|\\]\\s*,?\\s*)\\s*["'\`](?:${PN_LEVELS.join("|")}|P[0-3])`,
    "i",
  );
  assert.ok(
    !bandLike.test(GENERATOR_SRC),
    "backlog-sync.mjs appears to pair a milestone title with a priority band again. That table " +
    "belongs to MILESTONE_BANDS in scripts/priority-model.mjs and must be read through " +
    "derivePriority (#364 AC1).",
  );
  // Prove the detector can actually fire, so a silently-broken regex cannot pass forever.
  assert.ok(
    bandLike.test('const X = [["M4: UI / UX Polish", "Medium"]];'),
    "the band-table detector matched nothing at all — it cannot be trusted to catch a real one",
  );
});

// ── AC2 over the LIVE backlog ─────────────────────────────────────────────────────────────

test("AC2 (live): the log book and the board agree on every OPEN issue", async (t) => {
  let issues;
  try {
    const { stdout } = await execFileAsync("gh", [
      "issue", "list", "-R", process.env.KANBANFLOW_BACKLOG_REPO || "joelfuller2016/KanbanFlow-v2",
      "--state", "open", "--limit", "1000", "--json", "number,title,labels,milestone",
    ], { maxBuffer: 64 * 1024 * 1024, timeout: 120_000 });
    issues = JSON.parse(stdout);
  } catch (err) {
    // `gh` missing or unauthenticated is not a divergence. Skip LOUDLY — a silent pass here would
    // be the same lie the log book itself refuses to tell. This cannot mask a real disagreement:
    // the assertion below runs unconditionally once the fetch succeeds.
    t.skip(`gh unavailable, live half not run: ${err.message}`);
    return;
  }

  assert.ok(issues.length > 0, "gh returned no open issues — refusing to pass on an empty set");

  const disagreements = [];
  for (const i of issues) {
    if (tierOfIssue(i) !== boardTier(i)) {
      disagreements.push(
        `#${i.number} book=${tierOfIssue(i)} board=${boardTier(i)} ` +
        `labels=[${(i.labels || []).map((l) => l.name).join(",")}] ` +
        `milestone=${i.milestone?.title ?? "none"}`,
      );
    }
  }
  assert.equal(
    disagreements.length, 0,
    `${disagreements.length} of ${issues.length} open issues disagree:\n  ` + disagreements.join("\n  "),
  );
  console.error(`  agreement: ${issues.length}/${issues.length} open issues`);
});
