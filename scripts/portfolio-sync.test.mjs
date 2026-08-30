// Pins the description excerpt the board's reader actually sees.
//
// The defect these pin: BODY_EXCERPT_LIMIT was 1500. Measured against all 60 issues in this repo
// the median body is 11,900 chars and the SMALLEST clears 1500 — so 100% of synced cards carried
// "…(truncated — see the issue)", and the marker meant nothing because it was never absent. The
// board reported every description as incomplete while being, in the only sense that matters to a
// reader, complete-able.
//
// Run: node --test scripts/
//
// Each case loads the module with a cache-busting query so a per-test env limit is actually read
// at module scope; without the query, Node's ESM cache would hand back the first instance and
// every limit-dependent assertion would silently test the default.

import { test } from "node:test";
import assert from "node:assert/strict";

const MARKER = /truncated at/;
const URL_ = "https://github.com/joelfuller2016/KanbanFlow-v2/issues/454";

async function load(limit) {
  if (limit === undefined) delete process.env.KANBANFLOW_BODY_EXCERPT_LIMIT;
  else process.env.KANBANFLOW_BODY_EXCERPT_LIMIT = String(limit);
  return import(`./portfolio-sync.mjs?limit=${limit ?? "default"}`);
}

test("a real median-sized issue body is NOT truncated (the #user-visible bug)", async () => {
  const { excerptFor, BODY_EXCERPT_LIMIT } = await load();
  // 11,900 = the measured median body across all 60 issues in this repo.
  const body = "x".repeat(11900);
  assert.ok(BODY_EXCERPT_LIMIT > 11900, `limit ${BODY_EXCERPT_LIMIT} must clear the median body`);
  const out = excerptFor(body, URL_);
  assert.equal(out, body);
  assert.doesNotMatch(out, MARKER);
});

test("the largest body in the portfolio (21,057) is NOT truncated", async () => {
  const { excerptFor } = await load();
  const body = "y".repeat(21057);
  assert.equal(excerptFor(body, URL_), body);
});

// NON-VACUITY CONTROL. Every assertion above is an ABSENCE check, and an absence check passes
// against a function that returns "" — or against a marker regex that matches nothing. This case
// proves the same regex DOES fire on the same helper, so the absences above are informative.
test("control: the marker still fires when the body genuinely exceeds the limit", async () => {
  const { excerptFor } = await load(100);
  const out = excerptFor("z".repeat(500), URL_);
  assert.match(out, MARKER);
  assert.match(out, /read the full issue on GitHub/);
  assert.ok(out.includes(URL_), "the marker must name where the rest is");
  assert.ok(out.length > 0);
});

test("the cut lands on a line boundary, not mid-line", async () => {
  const { excerptFor } = await load(100);
  const body = Array.from({ length: 40 }, (_, i) => `line ${i} ${"-".repeat(10)}`).join("\n");
  const kept = excerptFor(body, URL_).split("\n\n---\n\n")[0];
  // Every retained line is a WHOLE line of the original: no half-open fence, no split table row.
  const originals = new Set(body.split("\n"));
  for (const l of kept.split("\n")) assert.ok(originals.has(l), `partial line: ${JSON.stringify(l)}`);
});

test("the cap stays absolute when a single line exceeds it", async () => {
  const { excerptFor } = await load(100);
  const body = "q".repeat(500); // one line, no boundary to cut on
  const kept = excerptFor(body, URL_).split("\n\n---\n\n")[0];
  assert.equal(kept.length, 100, "must fall back to the hard slice rather than keep the whole line");
});

test("descriptionFor emits no truncation marker for a real-sized body", async () => {
  const { descriptionFor } = await load();
  const issue = { url: URL_, state: "OPEN", body: "b".repeat(11900), labels: [], milestone: null };
  const out = descriptionFor(issue, null);
  assert.doesNotMatch(out, MARKER);
  assert.ok(out.includes("b".repeat(11900)), "the whole body must reach the board");
});

// ── The truncation footer is PERSISTED into a task description, so it must be locale-STABLE ────
// Review catch: `toLocaleString()` yields 64,000 / 64.000 / 64 000 on en-US / de-DE / fr-FR. Two
// machines syncing the same board would write different bytes for the same body, and any future
// comparison of descriptions (see the follow-up issue on reconciliation) would read that as a
// change. Deterministic here costs nothing and removes the trap before it is laid.
test("digit grouping is deterministic, not locale-dependent", async () => {
  const { groupDigits } = await load();
  assert.equal(groupDigits(64000), "64,000");
  assert.equal(groupDigits(21057), "21,057");
  assert.equal(groupDigits(999), "999");
  assert.equal(groupDigits(1000), "1,000");
  assert.equal(groupDigits(1234567), "1,234,567");
  assert.equal(groupDigits(0), "0");

  // The property that actually matters: identical to what a de-DE machine produces here.
  // toLocaleString would NOT satisfy this.
  assert.equal(groupDigits(64000), (64000).toLocaleString("en-US"));
  assert.notEqual((64000).toLocaleString("de-DE"), groupDigits(64000));
});

// An astral character straddling the cut used to leave a lone high surrogate - ill-formed text that
// renders as a replacement glyph and, because the description is persisted, poisons every later
// comparison of it. Found by an independent review pass, not by the tests above.
test("truncation never splits a surrogate pair", async () => {
  const { excerptFor } = await load(100);
  const emoji = String.fromCodePoint(0x1F600);       // one astral char, two UTF-16 units
  const body = "a".repeat(99) + emoji + "z".repeat(50);

  // Non-vacuity: the naive slice at this boundary IS ill-formed, so a pass below means the guard
  // acted rather than that the fixture was harmless.
  assert.equal(body.slice(0, 100).isWellFormed(), false, "fixture must straddle the boundary");

  const kept = excerptFor(body, "https://github.com/o/r/issues/1").split("\n\n---\n\n")[0];
  assert.ok(kept.isWellFormed(), "excerpt must not end in a lone surrogate");
  assert.equal(kept.length, 99, "the orphaned high surrogate is dropped, costing one unit");
});

// BODY_EXCERPT_LIMIT budgets the SOURCE BODY, not the returned string: the footer is appended after
// the budget. Pinned so the "absolute cap" reading cannot creep back into a comment or a test name.
test("the limit budgets the retained body, and the footer is charged on top", async () => {
  const { excerptFor, BODY_EXCERPT_LIMIT } = await load(100);
  assert.equal(BODY_EXCERPT_LIMIT, 100);

  const out = excerptFor("x".repeat(500), "https://github.com/o/r/issues/1");
  const kept = out.split("\n\n---\n\n")[0];

  assert.equal(kept.length, 100, "retained body is exactly the budget");
  assert.ok(out.length > BODY_EXCERPT_LIMIT, "the returned string is deliberately longer");
  assert.ok(out.length < BODY_EXCERPT_LIMIT + 200, "and the footer is bounded, not open-ended");
});
