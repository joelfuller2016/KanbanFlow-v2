import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:http";
import { dirname, join, sep } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(new URL("./register-kanbanflow-task.ps1", import.meta.url));
const fixtureDirectory = join(dirname(scriptPath), "..", "_sandbox", "task health fixture");
const expectedDb = join(fixtureDirectory, "expected.db");
const otherDb = join(fixtureDirectory, "other.db");
const equivalentDb = `${fixtureDirectory}${sep}nested${sep}..${sep}expected.db`;
const cases = [
  { name: "matching-ok", status: "ok", db: { path: expectedDb }, healthy: true },
  { name: "matching-degraded", status: "degraded", db: { path: equivalentDb }, healthy: true },
  { name: "wrong-ok", status: "ok", db: { path: otherDb }, healthy: false, mismatch: true },
  { name: "wrong-degraded", status: "degraded", db: { path: otherDb }, healthy: false, mismatch: true },
  { name: "missing-path", status: "ok", db: { status: "ok" }, healthy: false },
  { name: "invalid-path", status: "ok", db: { path: `${expectedDb}\0` }, healthy: false },
  { name: "error", status: "error", db: { path: expectedDb, status: "ok" }, healthy: false },
];

// Evaluate both call sites from the actual script with its real probe definition. The enclosing
// registration and polling statements never run, so this does not need a scheduled task.
const runProbeCalls = `
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:REGISTER_TASK_TEST_SCRIPT, [ref]$null, [ref]$null)
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-KanbanFlowHealthy'
}, $true)
. ([scriptblock]::Create($definition.Extent.Text))
$calls = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Test-KanbanFlowHealthy'
}, $true))
if ($calls.Count -ne 2) { throw 'Expected the already-serving and post-start readiness probes.' }
$resolvedDb = $env:REGISTER_TASK_TEST_DATABASE
$output = foreach ($name in $env:REGISTER_TASK_TEST_CASES.Split(',')) {
    $healthUri = "$($env:REGISTER_TASK_TEST_ORIGIN)/$name"
    foreach ($call in $calls) {
        $result = @(. ([scriptblock]::Create($call.Extent.Text)) 3>&1)
        $healthy = @($result | Where-Object { $_ -is [bool] })
        if ($healthy.Count -ne 1) { throw 'A readiness probe must return one boolean.' }
        [pscustomobject]@{
            name = $name
            healthy = $healthy[0]
            warnings = @($result | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object Message)
        }
    }
}
ConvertTo-Json -InputObject @($output) -Depth 4 -Compress
`;

for (const shell of process.platform === "win32" ? ["powershell.exe", "pwsh"] : ["pwsh"]) {
  test(`both scheduled task readiness checks require the pinned database in ${shell}`, async t => {
    const requests = [];
    const server = createServer((request, response) => {
      const name = request.url.slice(1);
      requests.push(`${request.method} ${name}`);
      const health = cases.find(item => item.name === name);
      if (!health) {
        response.writeHead(404).end();
        return;
      }
      response.writeHead(200, { "Content-Type": "application/json", Connection: "close" });
      response.end(JSON.stringify({ status: health.status, db: health.db }));
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    t.after(() => new Promise(resolve => server.close(resolve)));

    const encoded = Buffer.from(runProbeCalls, "utf16le").toString("base64");
    const processResult = spawn(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], {
      env: {
        ...process.env,
        REGISTER_TASK_TEST_SCRIPT: scriptPath,
        REGISTER_TASK_TEST_DATABASE: expectedDb,
        REGISTER_TASK_TEST_ORIGIN: `http://127.0.0.1:${server.address().port}`,
        REGISTER_TASK_TEST_CASES: cases.map(item => item.name).join(","),
      },
      timeout: 30_000,
      windowsHide: true,
    });
    let stdout = "";
    let stderr = "";
    processResult.stdout.setEncoding("utf8").on("data", chunk => { stdout += chunk; });
    processResult.stderr.setEncoding("utf8").on("data", chunk => { stderr += chunk; });
    const [status, signal] = await once(processResult, "close");
    assert.equal(signal, null, stderr);
    assert.equal(status, 0, stdout + stderr);
    assert.deepEqual(requests, cases.flatMap(({ name }) => [`GET ${name}`, `GET ${name}`]));

    const results = JSON.parse(stdout);
    for (const { name, healthy, mismatch } of cases) {
      const probes = results.filter(probe => probe.name === name);
      assert.deepEqual(probes.map(probe => probe.healthy), [healthy, healthy], name);
      if (mismatch) {
        for (const { warnings } of probes) {
          assert.ok(warnings.some(warning => warning.includes(expectedDb) && warning.includes(otherDb)),
            `${name}: mismatch warning must name both databases`);
        }
      }
      if (name === "invalid-path") {
        for (const { warnings } of probes) {
          assert.ok(warnings.some(warning => warning.includes(expectedDb) && !warning.includes("\0")),
            `${name}: invalid path warning must identify the database without control characters`);
        }
      }
    }
  });
}
