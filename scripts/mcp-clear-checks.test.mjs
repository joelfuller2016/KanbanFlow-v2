import assert from "node:assert/strict";
import { createServer } from "node:http";
import { test } from "node:test";
import { nativeToolCall } from "./mcp-clear-checks.mjs";

// Local HTTP protocol fixtures exercise the native smoke adapter over an actual socket.
// The full smoke separately exercises the application's real native MCP implementation.
async function withNativeResponse(t, contentType, body, run) {
  const requests = [];
  const server = createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    requests.push({ method: request.method, path: request.url, body: JSON.parse(Buffer.concat(chunks).toString()) });
    response.writeHead(200, { "Content-Type": contentType });
    response.end(body);
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  t.after(() => new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
    server.closeAllConnections();
  }));
  await run(`http://127.0.0.1:${server.address().port}`);
  assert.deepEqual(requests, [{
    method: "POST", path: "/mcp",
    body: { jsonrpc: "2.0", id: 462, method: "tools/call", params: { name: "update_idea", arguments: { ideaId: 7, body: "" } } }
  }]);
}

const success = { jsonrpc: "2.0", id: 462, result: { content: [{ type: "text", text: '{"id":7,"body":null}' }] } };
const invoke = url => nativeToolCall(url, 2000, "update_idea", { ideaId: 7, body: "" });

test("native smoke accepts the matching terminal SSE response after unrelated frames", async t => {
  const frames = [
    ": keepalive", "data: heartbeat",
    'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}',
    'data: {"jsonrpc":"2.0","id":462,"method":"roots/list","params":{}}',
    'data: {"jsonrpc":"2.0","id":999,"result":{}}',
    `event: message\ndata: ${JSON.stringify(success)}`
  ].join("\n\n") + "\n\n";
  await withNativeResponse(t, "text/event-stream", frames, url => assert.doesNotReject(invoke(url)));
});

test("native smoke accepts a plain JSON response", async t => {
  await withNativeResponse(t, "application/json", JSON.stringify(success), url => assert.doesNotReject(invoke(url)));
});

test("native smoke rejects an SSE stream without a matching terminal response", async t => {
  const frames = 'data: heartbeat\n\ndata: {"jsonrpc":"2.0","id":462,"method":"roots/list","params":{}}\n\n';
  await withNativeResponse(t, "text/event-stream", frames, url => assert.rejects(invoke(url), /matching JSON-RPC response/));
});

test("native smoke preserves tool errors after progress and heartbeat frames", async t => {
  const failure = { jsonrpc: "2.0", id: 462, result: { isError: true, content: [{ type: "text", text: "Validation refused" }] } };
  const frames = `data: heartbeat\n\ndata: ${JSON.stringify(failure)}\n\n`;
  await withNativeResponse(t, "text/event-stream", frames, url => assert.rejects(invoke(url), /Validation refused/));
});
