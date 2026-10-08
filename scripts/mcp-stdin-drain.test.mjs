// Regression test for the stdin-EOF drain race (repository review of master @ 1589e43,
// finding F1, plus a follow-up independent review of the fix itself): a client that writes its
// requests and then closes stdin — a normal shutdown, not a crash — must not lose the response to
// a request still awaiting its REST round-trip. See the stdin "end" handler and the crash-safety
// comments around inFlightRequestIds (#168) in mcp-server.mjs.
//
// PRECONDITION not covered by any test here (see the handler's own comment in mcp-server.mjs):
// the client must keep READING stdout, not merely keep it open. A peer that holds its read end
// open without draining it blocks process.stdout.write() once a response exceeds the OS pipe
// buffer (a synchronous syscall on Windows), freezing the whole process — including this drain and
// its own deadline. That is a precondition violation by a broken client, not something a
// node:test case spawning a normally-behaving reader can exercise; documented, not tested.
//
// Real stdio JSON-RPC child process and a real HTTP stub (delayed, not mocked) — the race is a
// property of actual event-loop ordering between the stdin "end" event and a pending fetch(),
// which a mocked transport cannot reproduce.

import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { spawn, execFileSync } from "node:child_process";
import { once } from "node:events";
import { fileURLToPath } from "node:url";

const wrapperPath = fileURLToPath(new URL("./mcp-server.mjs", import.meta.url));

// A stub REST server whose response is delayed so the wrapper's tools/call is still tracked in
// inFlightRequestIds (mcp-server.mjs:46) at the moment the test closes stdin — the exact race
// from the finding, not a synthetic one.
async function startDelayedApi(delayMs) {
  const api = createServer((req, res) => {
    setTimeout(() => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end("[]");
    }, delayMs);
  });
  api.listen(0, "127.0.0.1");
  await once(api, "listening");
  return api;
}

function spawnWrapper(apiPort, envOverrides = {}) {
  const child = spawn(process.execPath, [wrapperPath], {
    windowsHide: true,
    env: {
      ...process.env,
      KANBANFLOW_API_URL: `http://127.0.0.1:${apiPort}`,
      // "off" (not a temp file): matches atomic-projection-mcp.test.mjs's own idiom for this
      // wrapper's child-process tests — a real per-spawn temp dir/file is unnecessary here and
      // was leaking one directory per test run with nothing ever cleaning it up.
      KANBANFLOW_USAGE_LOG: "off",
      KANBANFLOW_MCP_TIMEOUT_MS: "3000",
      ...envOverrides,
    },
    stdio: ["pipe", "pipe", "pipe"],
  });
  const messages = [];
  // Arrival time per response id (Date.now() when its line completed) — additive, existing tests
  // that only destructure {child, messages, stderrText} are unaffected. Lets a test assert on the
  // gap between "the response arrived" and "the process exited" instead of only elapsed-since-
  // start, which a wait-out-the-deadline regression can still satisfy (see the never-responds
  // test below and its own comment).
  const messageTimes = new Map();
  let buffer = "";
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", chunk => {
    buffer += chunk;
    let boundary;
    while ((boundary = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 1);
      if (line.trim().length > 0) {
        const message = JSON.parse(line);
        messages.push(message);
        if (message && typeof message === "object" && "id" in message) messageTimes.set(message.id, Date.now());
      }
    }
  });
  let stderr = "";
  child.stderr.setEncoding("utf8");
  child.stderr.on("data", chunk => { stderr += chunk; });
  return { child, messages, messageTimes, stderrText: () => stderr };
}

function writeLine(child, message) {
  child.stdin.write(JSON.stringify(message) + "\n");
}

// The wrapper's own drain deadline (restTimeoutMs + margin) must make it exit unassisted; this is
// an independent, generous OUTER bound so a broken fix fails this test fast instead of hanging the
// whole suite. It waits for "close", not "exit": Node documents that the child's stdio streams
// might still be open when "exit" fires, and the callers assert on what stdout and stderr carried.
async function waitForExit(child, ms) {
  let timer;
  const timedOut = new Promise(resolve => { timer = setTimeout(() => resolve(true), ms); });
  const exited = once(child, "close").then(([code, signal]) => ({ code, signal }));
  const result = await Promise.race([exited, timedOut]);
  clearTimeout(timer);
  if (result === true) {
    if (child.exitCode === null) child.kill();
    throw new Error(`wrapper did not exit within ${ms}ms of stdin.end()`);
  }
  return result;
}

test("stdin EOF drains an in-flight tools/call response before the process exits", async () => {
  const api = await startDelayedApi(300);
  try {
    const { child, messages, stderrText } = spawnWrapper(api.address().port);
    writeLine(child, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "drain-test", version: "1" } } });
    writeLine(child, { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "list_projects", arguments: {} } });
    const startedAt = Date.now();
    child.stdin.end(); // write, then close stdin immediately — the exact sequence from the finding

    const { code } = await waitForExit(child, 5000);
    const elapsedMs = Date.now() - startedAt;

    assert.ok(messages.some(m => m.id === 1), `expected an initialize response; stderr: ${stderrText()}`);
    assert.ok(messages.some(m => m.id === 2),
      `expected a response for the in-flight tools/call (id 2) before exit — the finding's exact failure; got messages: ${JSON.stringify(messages)}; stderr: ${stderrText()}`);
    assert.equal(code, 0);
    // Proves the drain resolved because inFlightRequestIds emptied (the REST stub answers at
    // ~300ms), not merely because the outer deadline (~4000ms with the timeout override above)
    // was waited out regardless of outcome. Upper bound is deliberately well below that deadline,
    // not tight against the ~300ms expected case: this box runs several agents' builds
    // concurrently (shared-machine Node cold-start + fetch jitter is real), and the bound only
    // needs to catch "waited out the deadline," not shave milliseconds off the happy path.
    assert.ok(elapsedMs < 3500, `expected the drain to finish well before the ~4000ms deadline, not wait it out; took ${elapsedMs}ms`);
  } finally {
    await new Promise(resolve => api.close(resolve));
  }
});

test("stdin EOF still exits cleanly (no uncaught-exception crash) when the client has stopped reading stdout", async () => {
  const api = await startDelayedApi(300);
  try {
    const { child, stderrText } = spawnWrapper(api.address().port);
    writeLine(child, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "drain-epipe-test", version: "1" } } });
    writeLine(child, { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "list_projects", arguments: {} } });
    const startedAt = Date.now();
    child.stdout.destroy(); // the client stops reading stdout — the child's later write raises EPIPE
    child.stdin.end();

    const { code, signal } = await waitForExit(child, 5000);
    const elapsedMs = Date.now() - startedAt;

    // Note on scope: destroying stdout means id 2's response is unrecoverable either way (EPIPE is
    // permanent — see mcp-server.mjs's stdout "error" comment) — this test is not about delivery,
    // it is about the wrapper not crashing and not cutting the still-genuine REST exchange short.
    assert.equal(code, 0, `expected a clean, deliberate exit; stderr: ${stderrText()}`);
    assert.equal(signal, null);
    // Tighter than "no crash": an EPIPE on the id-1 response must not reach reportFatal (#168) and
    // force-answer the still-genuinely-in-flight id-2 request early. If it did, this exit would
    // look identical on the surface (code 0, no crash) but at ~80-100ms instead of ~300ms+ —
    // proven live (without the process.stdout "error" listener, id 1's EPIPE becomes an
    // uncaughtException that reportFatal answers by force-claiming EVERY in-flight id, well before
    // id 2's real REST response; see mcp-server.mjs's stdout "error" comment and the PR's test
    // evidence for the full before/after trace).
    assert.doesNotMatch(stderrText(), /uncaughtException/,
      `expected the EPIPE to be swallowed directly, not cascade into reportFatal; stderr: ${stderrText()}`);
    assert.ok(elapsedMs >= 250,
      `expected the exit to wait for the REAL 300ms REST completion, not resolve early via an unrelated fault; took only ${elapsedMs}ms; stderr: ${stderrText()}`);
    // See the sibling test above for why this is 3500, not tight against ~300-400ms.
    assert.ok(elapsedMs < 3500, `expected the drain to finish well before the ~4000ms deadline, not wait it out; took ${elapsedMs}ms`);
  } finally {
    await new Promise(resolve => api.close(resolve));
  }
});

test("stdin EOF still exits promptly when the client has stopped reading BOTH stdout and stderr", async () => {
  // A fully dead client, not just a broken stdout: this is what actually exercises the
  // process.stderr "error" listener. Without it, the stdout listener's OWN stderr.write() (its
  // fallback logging) fails asynchronously in exactly the same way an unguarded stdout EPIPE
  // does, cascading into reportFatal and re-introducing the early force-claim exit — measured live
  // at 78-98ms without the stderr listener, 400ms+ (the real REST completion) with it. Since
  // stderr is destroyed here too, this test cannot inspect its content (there is nowhere to read
  // it from) — the timing is the only, but sufficient, signal, exactly as the stdout-only test
  // above uses timing to prove the SAME class of premature exit did not happen.
  const api = await startDelayedApi(300);
  try {
    const { child } = spawnWrapper(api.address().port);
    writeLine(child, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "drain-dead-client-test", version: "1" } } });
    writeLine(child, { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "list_projects", arguments: {} } });
    const startedAt = Date.now();
    child.stdout.destroy();
    child.stderr.destroy();
    child.stdin.end();

    const { code, signal } = await waitForExit(child, 5000);
    const elapsedMs = Date.now() - startedAt;

    assert.equal(code, 0, "expected a clean, deliberate exit even with both output pipes dead");
    assert.equal(signal, null);
    assert.ok(elapsedMs >= 250,
      `expected the exit to wait for the REAL 300ms REST completion, not resolve early via the reportFatal cascade; took only ${elapsedMs}ms`);
    assert.ok(elapsedMs < 3500, `expected the drain to finish well before the ~4000ms deadline, not wait it out; took ${elapsedMs}ms`);
  } finally {
    await new Promise(resolve => api.close(resolve));
  }
});

// Reads a child process's cumulative CPU time via PowerShell — Windows-specific (this is a
// Windows-first, non-Docker project); returns null if the process has already exited or the call
// fails/hangs. [int] (not a bare .TotalMilliseconds) avoids a locale trap: on a comma-decimal
// culture PowerShell would print e.g. "46,875", which Number() parses as NaN — and NaN compares
// false against every threshold below, so a locale mismatch would fail correct code, not just
// flag a real spin. Whole milliseconds are ample precision for a 200ms threshold. `timeout` bounds
// a hung/slow PowerShell invocation rather than letting it block indefinitely.
function cpuTimeMs(pid) {
  try {
    const out = execFileSync(
      "powershell",
      ["-NoProfile", "-Command", `[int](Get-Process -Id ${pid}).TotalProcessorTime.TotalMilliseconds`],
      { encoding: "utf8", timeout: 15000 },
    ).trim();
    const value = Number(out);
    return Number.isFinite(value) ? value : null;
  } catch {
    return null;
  }
}

// cpuTimeMs runs `powershell`, Windows PowerShell's executable; other platforms don't have it (even
// PowerShell 7 installs as `pwsh`), so every sample there would be null and the test would fail for
// want of an instrument, not because the wrapper spins.
test("stdin left open with both stdout and stderr dead does not spin the CPU", {
  skip: process.platform !== "win32" && "samples the child's CPU time with `powershell` (Windows PowerShell), which non-Windows hosts don't have",
}, async () => {
  // Distinct from the test above: stdin here is NEVER ended, so the drain logic is not even in
  // play — this is a pre-existing bug on unmodified master itself. Measured directly against
  // isolated copies of both: 1589e43 shows a ~1234ms CPU delta, 92200b7 (before the
  // process.stderr "error" listener existed) ~1250ms — both over the same sampling span this test
  // uses below (nominally "1s"; see that comment for why it is really closer to 1.22s). Identical
  // code with the listener present measures ~0ms. Mechanism: the response write for id 2 fails
  // (EPIPE, stdout dead); with no stdout listener that would cascade into reportFatal once, but
  // WITH the stdout listener installed (as it is here) its own fallback stderr.write() is what
  // fails instead, since stderr is dead too — and on unmodified code THAT failure has no listener
  // either, so it reaches uncaughtException, reportFatal writes to stderr again, fails again,
  // forever. The process.stderr "error" listener breaks the loop at its root.
  const api = await startDelayedApi(20);
  // Declared OUTSIDE try so finally can still reach it — `const { child } = ...` inside try is
  // block-scoped to try, and referencing it from finally throws ReferenceError there instead
  // (caught earlier by self-review: that error happened AFTER the assertions below, so the test
  // reported the ReferenceError as its failure and api.close() was never reached, which kept the
  // server's handle alive and hung the entire node --test run, not just this one test).
  let child;
  try {
    ({ child } = spawnWrapper(api.address().port));
    writeLine(child, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "cpu-spin-test", version: "1" } } });
    child.stdout.destroy();
    child.stderr.destroy();
    writeLine(child, { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "list_projects", arguments: {} } });
    // Deliberately never end stdin: the client is still connected (stdin open) but has stopped reading both output pipes.

    await new Promise(resolve => setTimeout(resolve, 300)); // let the write failure + any cascade start
    const cpuAtStart = cpuTimeMs(child.pid);
    // Nominally a 1s sampling window, but cpuAtEnd's own PowerShell call (a blocking
    // execFileSync, ~200-250ms observed) runs AFTER this delay, so the actual span between the
    // two samples is closer to ~1.22s — a spin measures ~1200-1250ms against it, i.e. close to
    // 100% of one core, not "1234ms in 1000ms". The 200ms threshold below is generous either way.
    await new Promise(resolve => setTimeout(resolve, 1000));
    const cpuAtEnd = cpuTimeMs(child.pid);
    const cpuDeltaMs = (cpuAtStart !== null && cpuAtEnd !== null) ? cpuAtEnd - cpuAtStart : null;

    assert.equal(child.exitCode, null, "expected the wrapper to still be alive — stdin was never closed");
    assert.ok(cpuDeltaMs !== null, "expected to be able to sample the child's CPU time via PowerShell");
    // Measured live: ~1200-1250ms over the ~1.22s span above when spinning (unmodified master and
    // 92200b7 alike — see this test's leading comment), ~0ms when not. 200ms leaves a wide margin
    // above idle-process noise while remaining far below any real spin.
    assert.ok(cpuDeltaMs < 200,
      `expected near-idle CPU usage, not a reportFatal/stderr.write cascade; measured ${cpuDeltaMs}ms of CPU over the ~1.22s sampling span`);
  } finally {
    // NOT `if (child) await once(child, "exit")` unconditionally — 'exit' fires exactly once, so
    // awaiting it AFTER it has already fired (e.g. the wrapper crashed on startup, independent of
    // anything this test does) waits forever: the whole file hangs, no summary, later tests never
    // run. Register the listener before kill() (not after) so there is no gap in which 'exit'
    // could fire unobserved, and only do any of this when the child is confirmed still alive.
    if (child && child.exitCode === null && child.signalCode === null) {
      const exited = once(child, "exit");
      child.kill();
      await exited;
    }
    // A killed (rather than gracefully exited) child can leave its half of the keep-alive
    // connection for api.close() to wait out; force it closed rather than let that stall.
    api.closeAllConnections();
    await new Promise(resolve => api.close(resolve));
  }
});

test("stdin EOF exits via the per-request REST timeout when a single-hop call never responds (not the drain's own deadline)", async () => {
  // Naming note: this does NOT exercise the drain's own backstop deadline (restTimeoutMs + 1000ms
  // after "end" fires) — it exercises the PRE-EXISTING per-request restTimeoutMs AbortController
  // (restJson), which fires first and empties inFlightRequestIds on its own, well before the
  // drain's own deadline would ever be reached. See the next test for a case that reaches the
  // drain's own deadline specifically (a real compound tool, get_task, whose two sequential hops
  // outlast it).
  const api = createServer(() => { /* deliberately never respond */ });
  api.listen(0, "127.0.0.1");
  await once(api, "listening");
  try {
    const { child, messages, messageTimes, stderrText } = spawnWrapper(api.address().port, { KANBANFLOW_MCP_TIMEOUT_MS: "200" });
    writeLine(child, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "drain-hang-test", version: "1" } } });
    writeLine(child, { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "list_projects", arguments: {} } });
    child.stdin.end();

    // waitForExit's own 5000ms bound is what stands between this test and a genuine hang; no
    // separate elapsed-since-start assertion is made here (one was tried and dropped — see the
    // gap check below for why it, not an elapsed-time bound, is the actual promptness proof).
    const { code } = await waitForExit(child, 5000);
    const exitedAt = Date.now();

    const response = messages.find(m => m.id === 2);
    assert.ok(response, `expected SOME terminal response for id 2 (a timeout error counts); messages: ${JSON.stringify(messages)}; stderr: ${stderrText()}`);
    assert.equal(response.error?.code, -32000, `expected the REST-timeout error the wrapper already raises for a hung backend; got: ${JSON.stringify(response)}`);
    assert.equal(code, 0);
    // The actual promptness check: how long AFTER id 2's response was already written did the
    // process take to exit? Correct behavior answers via the per-request timeout and then exits
    // through the drain's own "already drained" fast path — that gap should be near-zero (~50ms).
    // A drain that ignores the fast path and always waits out its deadline (e.g. writableLength
    // stuck "never drained") would still deliver the SAME response at the SAME time, then sit for
    // roughly another 1000ms before exiting — invisible to any elapsed-since-start bound loose
    // enough to tolerate load (an elapsed bound here was measured, under 2x oversubscription, at
    // 918-1424ms for CORRECT code — only ~376ms of headroom below a 1800ms threshold — while this
    // gap assertion held at 99-201ms under the same load; dropped the elapsed bound, kept this).
    const respondedAt = messageTimes.get(2);
    assert.ok(respondedAt !== undefined, "expected to have recorded id 2's arrival time");
    const gapAfterResponseMs = exitedAt - respondedAt;
    assert.ok(gapAfterResponseMs < 500,
      `expected the process to exit shortly after id 2's response (the "already drained" fast path), not wait out the deadline separately; exited ${gapAfterResponseMs}ms after the response arrived`);
  } finally {
    await new Promise(resolve => api.close(resolve));
  }
});

test("stdin EOF reaches the drain's own deadline for a real compound tool (get_task) whose two hops outlast it", async () => {
  // get_task (scripts/mcp-server.mjs, case "get_task") makes two SEQUENTIAL restJson hops, each
  // independently timed out at KANBANFLOW_MCP_TIMEOUT_MS. EACH hop individually stays well under
  // that per-request timeout (3000ms hop vs. a 4000ms timeout) — it is only the two hops TOGETHER,
  // run sequentially and measured from when "end" fires, that add up to more than the drain's own
  // deadline (that same timeout + 1000ms margin). That sum being reachable is a real scenario, not
  // a hypothetical requiring something to bypass restJson, contrary to an earlier draft of this PR.
  // T=4000ms => deadline ~5000ms after "end"; each hop takes ~3000ms (deliberately 1000ms under T,
  // not 3500ms as an earlier draft used — that left only a 500ms margin against a hop's OWN
  // per-request timeout firing first under load, which would break the test's premise rather than
  // exercise the drain's deadline). Hop 1 finishes ~3000ms in (well under its own 4000ms timeout);
  // hop 2 then needs another ~3000ms, finishing ~6000ms — still ~1000ms past the ~5000ms deadline
  // on that side too. The stub records when it replies to each hop, so the first assertion below
  // can check that hop 2 had not been answered when the wrapper exited.
  const T = 4000;
  const hopDelayMs = 3000;
  const hopReplies = { task: null, comments: null };
  const t0Ref = { t0: 0 };
  // Reaching the deadline (by design, that's what this test proves) means the wrapper exits ~1s
  // before hop 2's own reply timer would fire — track and clear pending timers so that stray timer
  // doesn't fire after api.close() and write to a since-abandoned response.
  const pendingReplyTimers = new Set();
  const api = createServer((req, res) => {
    const isComments = req.url.endsWith("/comments");
    const timer = setTimeout(() => {
      pendingReplyTimers.delete(timer);
      if (isComments) hopReplies.comments = Date.now() - t0Ref.t0; else hopReplies.task = Date.now() - t0Ref.t0;
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(isComments ? "[]" : JSON.stringify({ id: 7, title: "t" }));
    }, hopDelayMs);
    pendingReplyTimers.add(timer);
  });
  api.listen(0, "127.0.0.1");
  await once(api, "listening");
  try {
    const { child, messages, stderrText } = spawnWrapper(api.address().port, { KANBANFLOW_MCP_TIMEOUT_MS: String(T) });
    t0Ref.t0 = Date.now();
    writeLine(child, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "drain-deadline-test", version: "1" } } });
    writeLine(child, { jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "get_task", arguments: { taskId: 7 } } });
    child.stdin.end();

    // Correct behavior exits at ~5000ms (the deadline); the deadline-deleted mutant instead waits
    // for the real ~6000ms second-hop completion and answers normally — bound high enough to let
    // BOTH outcomes finish naturally, so the mutation run below fails on content, not on timeout.
    const { code } = await waitForExit(child, 9000);

    // Content checks, not timing: prove the exit happened WHILE hop 2 was still outstanding.
    assert.equal(hopReplies.comments, null,
      `expected the wrapper to exit before the stub replied to the second hop (get_task/comments); it had already replied at t+${hopReplies.comments}ms`);
    assert.ok(!messages.some(m => m.id === 2),
      `expected NO frame for id 2 — the deadline fired while it was still genuinely in flight; got: ${JSON.stringify(messages)}`);
    // Exit code stays 0 by design (a supervisor may read non-zero as a server failure, and this
    // shutdown is still orderly) — the stderr line is the signal, not the exit code. See the
    // issue's Open questions for the case against making this non-zero.
    assert.equal(code, 0, `expected exit code 0 even on deadline expiry; stderr: ${stderrText()}`);
    assert.match(stderrText(), /\[2\]/,
      `expected stderr to name the abandoned request id (2) — the only observable signal this happened; stderr: ${stderrText()}`);
  } finally {
    // In the (expected, correct) case the wrapper exits via the deadline ~1s before hop 2's own
    // reply timer would otherwise fire — clear it so it doesn't write to a since-abandoned
    // response after this test (and api.close() below) has already moved on.
    for (const timer of pendingReplyTimers) clearTimeout(timer);
    pendingReplyTimers.clear();
    await new Promise(resolve => api.close(resolve));
  }
});
