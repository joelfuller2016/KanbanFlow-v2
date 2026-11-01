import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { fileURLToPath } from "node:url";

// Actual JSON-RPC clients shared by workspace adapter tests and disposable-app smoke checks.
export async function openWorkspaceStdio(baseUrl, { usageLog = "off" } = {}) {
  assert.ok(baseUrl, "An explicit disposable API URL is required.");
  const child = spawn(process.execPath, [fileURLToPath(new URL("./mcp-server.mjs", import.meta.url))], {
    windowsHide: true, stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env, KANBANFLOW_API_URL: baseUrl, KANBANFLOW_USAGE_LOG: usageLog },
  });
  let sequence = 0;
  let buffer = "";
  const pending = new Map();
  const fail = error => {
    for (const request of pending.values()) { clearTimeout(request.timeout); request.reject(error); }
    pending.clear();
  };
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", data => {
    buffer += data;
    let end;
    while ((end = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, end); buffer = buffer.slice(end + 1);
      if (!line.trim()) continue;
      let packet;
      try { packet = JSON.parse(line); } catch (error) { fail(error); continue; }
      const request = pending.get(packet.id);
      if (request) { clearTimeout(request.timeout); pending.delete(packet.id); request.resolve(packet); }
    }
  });
  child.stderr.resume();
  child.on("error", fail);
  child.stdin.on("error", fail);
  child.on("exit", (code, signal) => fail(new Error(`Workspace MCP process exited ${code ?? signal}`)));
  const rpc = (method, params) => new Promise((resolve, reject) => {
    if (child.exitCode !== null || child.signalCode !== null || child.stdin.destroyed || child.stdin.writableEnded)
      return reject(new Error(`Workspace MCP process is not accepting calls: ${method}`));
    const id = ++sequence;
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`Workspace MCP timeout: ${method}`)); }, 20000);
    pending.set(id, { resolve, reject, timeout });
    try { child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n"); }
    catch (error) { fail(error); }
  });
  const close = async () => {
    const alive = child.exitCode === null && child.signalCode === null;
    const exited = alive ? once(child, "exit") : Promise.resolve();
    child.stdin.end();
    if (alive) child.kill();
    await exited;
  };
  try {
    const initialized = await rpc("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "workspace-verification", version: "1" } });
    assert.equal(initialized.error, undefined);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
  } catch (error) { await close(); throw error; }
  return { rpc, close, call: (name, args = {}) => rpc("tools/call", { name, arguments: args }) };
}

export function workspaceToolValue(packet) {
  assert.equal(packet.error, undefined, JSON.stringify(packet.error));
  assert.notEqual(packet.result?.isError, true, JSON.stringify(packet.result));
  if (packet.result?.structuredContent !== undefined) return packet.result.structuredContent;
  return JSON.parse(packet.result.content.find(item => item.type === "text").text);
}

let nativeSequence = 0;
export async function workspaceNativePacket(baseUrl, method, params) {
  const id = ++nativeSequence;
  const response = await fetch(baseUrl + "/mcp", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18" },
    body: JSON.stringify({ jsonrpc: "2.0", id, method, params }), signal: AbortSignal.timeout(20000),
  });
  const text = await response.text();
  assert.equal(response.status, 200, text);
  const packets = [];
  if (response.headers.get("content-type")?.includes("text/event-stream")) {
    for (const event of text.split(/\r?\n\r?\n/)) {
      const lines = event.split(/\r?\n/).filter(line => line.startsWith("data:")).map(line => line.slice(5).trimStart());
      if (lines.length) packets.push(JSON.parse(lines.join("\n")));
    }
  } else packets.push(JSON.parse(text));
  const packet = packets.find(item => item.id === id);
  assert.ok(packet, "Native MCP must return the response matching the submitted request id.");
  return packet;
}
