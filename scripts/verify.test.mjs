import { test } from "node:test";
import assert from "node:assert/strict";
import {
  chmodSync, copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync,
} from "node:fs";
import { spawnSync } from "node:child_process";
import { delimiter, dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const scratch = join(repository, "_sandbox", "verify-tests");

// Execute the real gate in a tiny checkout with a native dotnet stand-in. The stand-in only
// supplies process outcomes; all step ordering, exception handling, location restoration and
// final exit behavior come from verify.ps1. No application build or formatting is simulated as
// evidence about the application itself; those are covered by the real repository gate.
function runGate({ exits = {}, invocation = "call", hasTests = true } = {}) {
  mkdirSync(scratch, { recursive: true });
  const fixture = mkdtempSync(join(scratch, "gate with spaces-"));
  try {
    const repo = join(fixture, "repo");
    const scripts = join(repo, "scripts");
    const bin = join(fixture, "bin");
    const caller = join(fixture, "caller");
    for (const directory of [scripts, bin, caller]) mkdirSync(directory, { recursive: true });
    const gate = join(scripts, "verify.ps1");
    const target = join(repo, "Gate Fixture.slnx");
    copyFileSync(join(repository, "scripts", "verify.ps1"), gate);
    writeFileSync(target, "<Solution />\n");
    if (hasTests) writeFileSync(join(repo, "Fixture.Tests.csproj"), "<Project />\n");
    const traceFile = join(fixture, "steps.jsonl");
    const callerFile = join(fixture, "caller-state.json");
    const fakeDotnet = join(bin, "fake-dotnet.mjs");
    writeFileSync(fakeDotnet, `#!/usr/bin/env node
import { appendFileSync } from "node:fs";
const [step, ...args] = process.argv.slice(2);
appendFileSync(process.env.VERIFY_TEST_TRACE, JSON.stringify({ step, args }) + "\\n");
const exits = JSON.parse(process.env.VERIFY_TEST_EXITS);
process.exit(exits[step] ?? 0);
`);
    if (process.platform === "win32") {
      writeFileSync(join(bin, "dotnet.cmd"),
        `@echo off\r\n"${process.execPath}" "%~dp0fake-dotnet.mjs" %*\r\nexit /b %errorlevel%\r\n`);
    } else {
      copyFileSync(fakeDotnet, join(bin, "dotnet"));
      chmodSync(join(bin, "dotnet"), 0o755);
    }

    const wrapper = join(fixture, "invoke-gate.ps1");
    writeFileSync(wrapper, `$before = (Get-Location).Path
& $env:VERIFY_TEST_GATE
$invocationSucceeded = $?
$capturedExit = $LASTEXITCODE
[ordered]@{
    before = $before
    after = (Get-Location).Path
    succeeded = $invocationSucceeded
    exitCode = $capturedExit
} | ConvertTo-Json | Set-Content -LiteralPath $env:VERIFY_TEST_CALLER
exit $capturedExit
`);
    const pathKey = Object.keys(process.env).find(key => key.toLowerCase() === "path") ?? "PATH";
    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File",
      invocation === "file" ? gate : wrapper], {
      cwd: caller,
      env: {
        ...process.env,
        [pathKey]: `${bin}${delimiter}${process.env[pathKey] ?? ""}`,
        VERIFY_TEST_GATE: gate,
        VERIFY_TEST_TRACE: traceFile,
        VERIFY_TEST_CALLER: callerFile,
        VERIFY_TEST_EXITS: JSON.stringify(exits),
      },
      encoding: "utf8",
      windowsHide: true,
      timeout: 30_000,
    });
    assert.ifError(result.error);
    assert.equal(result.signal, null, `gate terminated by ${result.signal}: ${result.stderr}`);
    const steps = readFileSync(traceFile, "utf8").trim().split(/\r?\n/).map(line => JSON.parse(line));
    for (const step of steps) assert.equal(step.args[0], target, "the gate must use the fixture solution");
    if (invocation === "call") {
      const state = JSON.parse(readFileSync(callerFile, "utf8").replace(/^\uFEFF/, ""));
      assert.equal(state.before, caller);
      assert.equal(state.after, caller, "the gate must restore its caller's working directory");
      assert.equal(state.exitCode, result.status);
      assert.equal(state.succeeded, result.status === 0,
        `PowerShell success=${state.succeeded} disagrees with gate exit ${result.status}`);
    }
    return { ...result, steps: steps.map(step => step.step) };
  } finally {
    // Only this test's freshly created directory is removed, never another run's fixture.
    assert.ok(resolve(fixture).startsWith(resolve(scratch) + sep));
    rmSync(fixture, { recursive: true, force: true });
  }
}

for (const invocation of ["call", "file"]) {
  test(`verify exits zero after advisory format drift through ${invocation} invocation`, () => {
    const result = runGate({ exits: { format: 2 }, invocation });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(result.steps, ["restore", "build", "test", "format"]);
    assert.match(result.stdout, /WARNING: formatting drift detected/);
    assert.match(result.stdout, /Build \+ tests passed\. Safe to push\./);
  });
}

test("verify exits zero when every step is clean", () => {
  const result = runGate();
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.deepEqual(result.steps, ["restore", "build", "test", "format"]);
  assert.doesNotMatch(result.stdout, /WARNING:|FAILED:/);
});

for (const [step, nativeExit, expectedSteps] of [
  ["restore", 3, ["restore"]],
  ["build", 5, ["restore", "build"]],
  ["test", 7, ["restore", "build", "test"]],
]) {
  test(`verify exits one and stops after a ${step} failure`, () => {
    const result = runGate({ exits: { [step]: nativeExit } });
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.deepEqual(result.steps, expectedSteps);
    assert.match(result.stdout, new RegExp(`FAILED: ${step}.*exit ${nativeExit}`));
    assert.doesNotMatch(result.stdout, /Safe to push/);
  });
}

test("verify preserves the documented no-test-project path and advisory exit", () => {
  const result = runGate({ hasTests: false, exits: { format: 2 } });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.deepEqual(result.steps, ["restore", "build", "format"]);
  assert.match(result.stdout, /test \(skipped/);
  assert.match(result.stdout, /WARNING: formatting drift detected/);
});
