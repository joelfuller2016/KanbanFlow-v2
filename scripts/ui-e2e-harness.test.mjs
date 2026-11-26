import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

function runHarness(t, { buildDiagnostic, failBrowser = false } = {}) {
  const worktree = mkdtempSync(join(tmpdir(), "kanbanflow-harness-test-"));
  t.after(() => rmSync(worktree, { recursive: true, force: true }));
  for (const file of ["ui-e2e.mjs", "project-skip-navigation.browser.mjs"]) {
    copyFileSync(new URL(file, import.meta.url), join(worktree, file));
  }
  const env = { ...process.env };
  delete env.SKIP_BUILD;
  delete env.NODE_OPTIONS;
  const playwright = join(worktree, "node_modules", "playwright");
  mkdirSync(playwright, { recursive: true });
  // A local manifest makes an unavailable entry point deterministic even if the
  // temporary directory is under a checkout with Playwright installed.
  writeFileSync(join(playwright, "package.json"), '{"type":"module","exports":"./index.js"}');
  const args = [];
  if (buildDiagnostic) {
    writeFileSync(join(playwright, "index.js"), "export const chromium = {};");
    const buildStub = join(worktree, "build-stub.cjs");
    writeFileSync(buildStub, `
      const child = require("node:child_process");
      const diagnostic = ${JSON.stringify(buildDiagnostic)};
      child.spawnSync = (file, args) => {
        require("node:assert/strict").equal(file, "dotnet");
        require("node:assert/strict").equal(args[0], "build");
        return { status: 1, stdout: "Building project...\\n", stderr: diagnostic + "\\n" };
      };
      child.execSync = () => { throw new Error(diagnostic); };
      require("node:module").syncBuiltinESMExports();
    `);
    args.push("--require", buildStub);
  }
  if (failBrowser) {
    env.SKIP_BUILD = "1";
    writeFileSync(join(playwright, "index.js"), `
      export const chromium = { launch: async () => ({
        newPage: async () => { throw new Error("Could not open a browser page."); },
        close: async () => { throw new Error("Could not close the browser."); },
      }) };
    `);
    const appStub = join(worktree, "app-stub.cjs");
    writeFileSync(appStub, `
      const { EventEmitter } = require("node:events");
      require("node:child_process").spawn = () => new EventEmitter();
      require("node:module").syncBuiltinESMExports();
      globalThis.fetch = async () => ({ ok: true, text: async () => '{"status":"ok"}' });
    `);
    args.push("--require", appStub);
  }
  return spawnSync(process.execPath, [...args, join(worktree, "ui-e2e.mjs")], {
    cwd: worktree, env, encoding: "utf8", timeout: 10_000,
  });
}

function failureSummary(result) {
  assert.ifError(result.error);
  assert.equal(result.status, 1);
  const terminalLines = result.stdout.trimEnd().split(/\r?\n/);
  const summaries = terminalLines.filter(line => line.startsWith("UI e2e:"));
  assert.equal(summaries.length, 1, result.stdout + result.stderr);
  assert.equal(terminalLines.at(-1), summaries[0]);
  assert.match(summaries[0], /^UI e2e: 0\/0 .*HARNESS FAILED:/);
  return summaries[0];
}

test("missing Playwright prints one failure summary and preserves the original error", t => {
  const result = runHarness(t);
  assert.match(failureSummary(result), /npm ci/);
  assert.match(result.stderr, /ERR_MODULE_NOT_FOUND/);
});

test("locked build output prints the MSBuild error and a worktree hint", t => {
  const diagnostic = "error MSB3027: access failed. The file is locked by: KanbanFlow (42)";
  const result = runHarness(t, { buildDiagnostic: diagnostic });
  const summary = failureSummary(result);
  assert.match(summary, /locked/i);
  assert.match(summary, /worktree/i);
  assert.ok(result.stderr.includes(diagnostic));
});

test("ordinary copy failures retain their diagnostics without suggesting a locked DLL", t => {
  const diagnostic = "error MSB3021: Unable to copy file. Access to the path is denied.";
  const result = runHarness(t, { buildDiagnostic: diagnostic });
  const summary = failureSummary(result);
  assert.match(summary, /dotnet build failed/i);
  assert.doesNotMatch(summary, /worktree|locked/i);
  assert.ok(result.stderr.includes(diagnostic));
});

test("a browser cleanup failure preserves the original harness error", t => {
  const result = runHarness(t, { failBrowser: true });
  assert.match(failureSummary(result), /Could not open a browser page/);
  assert.match(result.stderr, /Could not close the browser/);
});
