// Guards that `scripts/portfolio-sync.mjs` keeps the imports it actually uses (#364, PR #373).
//
// The incident this pins: the import line carried a comment claiming `priorityFromLabel` was
// "imported but unused HERE on purpose: PR #339's `labelForPriority()` calls it". #339 merged and
// `labelForPriority()` is in portfolio-sync.mjs itself, 740 lines below the import, calling
// `priorityFromLabel` directly. So the claim was false — and its framing was precisely what
// invites the "remove the unused import" edit it was written to prevent, which throws
// ReferenceError the first time `push-issue` runs.
//
// A comment prevents that only if every future editor reads it first. CodeRabbit's review of
// PR #373 asked for the invariant to be enforced rather than narrated, which is the same argument
// this PR makes about structural guards elsewhere — so it is enforced here.
//
// WHY THIS IS A SOURCE-TEXT TEST, honestly stated: the property under guard is lexical — "is this
// binding imported, and is it referenced anywhere outside the import line?" — so reading the
// source is measuring the right artifact, not a proxy for it. It is not a stand-in for behaviour
// that could have been executed instead: `portfolio-sync.mjs` calls `main()` unconditionally at
// module scope with no entry-point guard and exports nothing, so importing it to call
// `labelForPriority()` for real would fire a live GitHub sync as a side effect of `node --test`.
// Adding a guard and an export to that file is a sensible follow-up; it is out of scope for a PR
// whose only other change there is a comment. Both assertions below are mutation-proven: deleting
// either name from the import line turns this file red.
//
// Run: npm run test:scripts

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const SRC = await readFile(
  join(dirname(fileURLToPath(import.meta.url)), "portfolio-sync.mjs"), "utf8",
);

// The import statement, whatever its formatting. Captured so the names can be checked one by one
// and a partial removal cannot hide behind the line still existing.
const IMPORT_LINE = /import\s*\{([^}]*)\}\s*from\s*["']\.\/priority-model\.mjs["']\s*;?/;

// Every binding portfolio-sync.mjs takes from the shared priority model, with the call site that
// makes it load-bearing. Add a row here when the file starts using another export — the point of
// the table is that "unused" becomes a claim this suite can check, not one a comment asserts.
const REQUIRED_IMPORTS = [
  { name: "derivePriority", why: "sets TaskItem.Priority in syncTasks() and backs the priority-drift check in statusRepo()" },
  { name: "priorityFromLabel", why: "labelForPriority() reads it back so a pushed issue's priority survives the round trip" },
];

test("portfolio-sync imports every priority-model binding it depends on", () => {
  const m = IMPORT_LINE.exec(SRC);
  assert.ok(m, "portfolio-sync.mjs no longer imports from ./priority-model.mjs at all");

  const imported = new Set(m[1].split(",").map((s) => s.trim().split(/\s+as\s+/)[0].trim()).filter(Boolean));
  for (const { name, why } of REQUIRED_IMPORTS) {
    assert.ok(
      imported.has(name),
      `portfolio-sync.mjs no longer imports "${name}" from ./priority-model.mjs, but ${why}. ` +
      `Removing it throws ReferenceError at runtime, not at load. If the call site really is gone, ` +
      `delete its row from REQUIRED_IMPORTS in this file and say so in the PR.`,
    );
  }
});

test("every imported binding has a real call site — none is dead weight", () => {
  // Strip the import statement first, so the declaration cannot count as its own usage. Without
  // this the assertion is vacuous: the name appears in the import line by definition.
  const body = SRC.replace(IMPORT_LINE, "");
  for (const { name, why } of REQUIRED_IMPORTS) {
    const calls = body.match(new RegExp(`\\b${name}\\s*\\(`, "g")) || [];
    assert.ok(
      calls.length > 0,
      `"${name}" is imported by portfolio-sync.mjs but never called outside the import line. ` +
      `Either it is genuinely dead and both the import and its row here should go, or a call site ` +
      `was lost. It is documented as: ${why}.`,
    );
  }

  // Prove the detector can fire, so a regex that silently matches nothing cannot pass forever.
  assert.equal(("x = priorityFromLabel(n);".match(/\bpriorityFromLabel\s*\(/g) || []).length, 1);
  assert.equal(("nothing here".match(/\bpriorityFromLabel\s*\(/g) || []).length, 0);
});

test("the import comment no longer claims a used binding is unused", () => {
  // The specific false statement that caused this. It read "imported but unused HERE on purpose",
  // which is what invited the removal. Assert on the CLAIM, not on the current wording, so a
  // rewrite of the comment is free but a reintroduction of the falsehood is not.
  const m = IMPORT_LINE.exec(SRC);
  const preamble = SRC.slice(0, m.index);
  const lastBlock = preamble.slice(preamble.lastIndexOf("\n\n"));
  assert.ok(
    !/\bunused\b/i.test(lastBlock) || /\bnot\s+unused\b|\bunused\s+import\b.{0,200}\bhazard\b/is.test(lastBlock),
    `The comment above the priority-model import calls something "unused" again. Both bindings ` +
    `are used (see REQUIRED_IMPORTS above); a comment saying otherwise is what invited the ` +
    `ReferenceError-causing removal in the first place.`,
  );
});
