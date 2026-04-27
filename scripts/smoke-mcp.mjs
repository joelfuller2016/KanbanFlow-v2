#!/usr/bin/env node

import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const projectId = Number.parseInt(process.argv[2] || "1", 10);
if (!Number.isInteger(projectId)) {
  console.error("Usage: node scripts/smoke-mcp.mjs [projectId]");
  process.exit(1);
}

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const serverPath = path.join(scriptDir, "mcp-server.mjs");
const apiBaseUrl = (process.env.KANBANFLOW_API_URL || "http://127.0.0.1:5123").replace(/\/$/, "");
const restTimeoutMs = Number.parseInt(process.env.KANBANFLOW_MCP_SMOKE_TIMEOUT_MS || "10000", 10);
const server = spawn(process.execPath, [serverPath], {
  stdio: ["pipe", "pipe", "inherit"],
  env: { ...process.env }
});

let nextId = 1;
let buffer = Buffer.alloc(0);
const pending = new Map();
const unexpectedMessages = [];
const unexpectedWaiters = [];
const runMarker = `mcp-smoke-${Date.now()}-${Math.random().toString(16).slice(2)}`;
let tempFeatureId;

server.stdout.on("data", chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  readMessages();
});

server.on("exit", code => {
  if (pending.size > 0) {
    for (const { reject } of pending.values()) {
      reject(new Error(`MCP server exited with code ${code}`));
    }
    pending.clear();
  }
});

try {
  await request("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "kanbanflow-smoke", version: "0.1.0" }
  });
  notify("notifications/initialized");

  await expectJsonRpcError({ jsonrpc: "2.0", id: 9001, params: {} }, -32600);
  await expectJsonRpcError({ jsonrpc: "1.0", id: 9002, method: "tools/list", params: {} }, -32600);
  await expectJsonRpcError({
    jsonrpc: "2.0",
    id: 9003,
    method: "unknown/method",
    params: {}
  }, -32601);
  await expectJsonRpcError({
    jsonrpc: "2.0",
    id: 9004,
    method: "tools/call",
    params: { name: "get_project_context", arguments: {} }
  }, -32602, { chunked: true });
  await expectUnexpectedJsonRpcError({ jsonrpc: "2.0", params: {} }, -32600, null);
  await expectRawJsonRpcError('{"jsonrpc":"2.0","id":9005,"method":', -32700);
  await expectNoResponse({ jsonrpc: "2.0", method: "tools/list", params: {} });

  const list = await request("tools/list", {});
  const toolNames = new Set(list.tools.map(tool => tool.name));
  for (const requiredTool of [
    "list_projects",
    "get_project",
    "list_project_tasks",
    "get_task",
    "get_project_board",
    "get_project_context",
    "get_next_tasks",
    "claim_task",
    "release_task",
    "update_task_status",
    "add_task_comment"
  ]) {
    if (!toolNames.has(requiredTool)) {
      throw new Error(`Missing MCP tool: ${requiredTool}`);
    }
  }

  const projects = await callTool("list_projects", {});
  if (!Array.isArray(projects) || !projects.some(project => project.id === projectId)) {
    throw new Error(`list_projects did not include project ${projectId}.`);
  }

  const project = await callTool("get_project", { projectId });
  if (project?.id !== projectId) {
    throw new Error(`get_project returned unexpected project: ${JSON.stringify(project)}`);
  }

  const projectTasks = await callTool("list_project_tasks", { projectId });
  if (!Array.isArray(projectTasks)) {
    throw new Error("list_project_tasks did not return an array.");
  }

  const board = await callTool("get_project_board", { projectId });
  if (board === null || typeof board !== "object") {
    throw new Error("get_project_board did not return an object.");
  }

  await callTool("get_project_context", { projectId });
  await expectJsonRpcError({
    jsonrpc: "2.0",
    id: 9006,
    method: "tools/call",
    params: { name: "get_project_context", arguments: { projectId: -1 } }
  }, -32000, {
    assertError: error => {
      if (typeof error.data?.status !== "number" || !error.data.path?.includes("/api/projects/-1/context")) {
        throw new Error(`Expected REST status/path error data, received: ${JSON.stringify(error.data)}`);
      }
    }
  });

  const tempFeature = await restJson(`/api/projects/${projectId}/features`, {
    method: "POST",
    body: {
      name: `MCP smoke temp feature ${Date.now()}`,
      description: `Temporary feature ${runMarker} created by scripts/smoke-mcp.mjs and deleted during cleanup.`,
      sortOrder: 999999
    }
  });
  tempFeatureId = tempFeature.id;

  const task = await restJson(`/api/features/${tempFeatureId}/tasks`, {
    method: "POST",
    body: {
      name: `MCP smoke temp task ${runMarker}`,
      description: `Temporary task ${runMarker} created by scripts/smoke-mcp.mjs and deleted during cleanup.`,
      status: "Backlog",
      priority: "Low"
    }
  });

  const fetchedTask = await callTool("get_task", { taskId: task.id });
  if (fetchedTask?.id !== task.id) {
    throw new Error(`get_task returned unexpected task: ${JSON.stringify(fetchedTask)}`);
  }

  await callTool("get_next_tasks", { projectId, limit: 3 });
  await callTool("claim_task", { taskId: task.id, actor: "mcp-smoke" });
  await callTool("add_task_comment", { taskId: task.id, text: `MCP smoke test comment ${runMarker}`, author: "mcp-smoke" });
  await callTool("update_task_status", { taskId: task.id, status: "Review" });
  await callTool("release_task", { taskId: task.id, actor: "mcp-smoke" });

  console.log(`MCP smoke tests passed for project ${projectId}, task ${task.id}.`);
} finally {
  try {
    if (tempFeatureId !== undefined) {
      try {
        await restJson(`/api/features/${tempFeatureId}`, { method: "DELETE", expectNoContent: true });
        console.log(`Cleaned up MCP smoke feature ${tempFeatureId}.`);
        const contextAfterCleanup = await restJson(`/api/projects/${projectId}/context`);
        const serializedContext = JSON.stringify(contextAfterCleanup);
        if (serializedContext.includes(runMarker)) {
          console.error(`MCP smoke cleanup failed; project context still contains ${runMarker}.`);
          process.exitCode = 1;
        }
      } catch (error) {
        console.error(`MCP smoke cleanup failed for feature ${tempFeatureId}: ${error.message}`);
        process.exitCode = 1;
      }
    }
  } finally {
    server.kill();
  }
}

function request(method, params) {
  const id = nextId++;
  return requestMessage({ jsonrpc: "2.0", id, method, params });
}

function requestMessage(message, options = {}) {
  if (message.id === undefined || message.id === null) {
    throw new Error("requestMessage requires a JSON-RPC id.");
  }

  const { id, method = `message ${message.id}` } = message;
  writeMessage(message, options);

  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Timed out waiting for ${method}`));
    }, 10000);

    pending.set(id, {
      resolve: value => {
        clearTimeout(timeout);
        resolve(value);
      },
      reject: error => {
        clearTimeout(timeout);
        reject(error);
      }
    });
  });
}

async function expectJsonRpcError(message, expectedCode, options = {}) {
  try {
    await requestMessage(message, options);
  } catch (error) {
    if (error.code !== expectedCode) {
      throw new Error(`Expected JSON-RPC error ${expectedCode}, received ${error.code ?? "unknown"}: ${error.message}`);
    }

    options.assertError?.(error);

    return;
  }

  throw new Error(`Expected JSON-RPC error ${expectedCode}, but request succeeded.`);
}

async function expectRawJsonRpcError(rawBody, expectedCode) {
  const unexpectedStart = unexpectedMessages.length;
  writeRawFrame(rawBody);
  const message = await waitForUnexpectedMessage(unexpectedStart, 1000);
  if (!message?.error) {
    throw new Error(`Expected raw JSON-RPC error ${expectedCode}, received: ${JSON.stringify(message)}`);
  }
  if (message.error.code !== expectedCode) {
    throw new Error(`Expected raw JSON-RPC error ${expectedCode}, received ${message.error.code}: ${message.error.message}`);
  }
}

async function expectNoResponse(message, timeoutMs = 300) {
  const unexpectedStart = unexpectedMessages.length;
  writeMessage(message);

  const unexpected = await waitForUnexpectedMessage(unexpectedStart, timeoutMs);
  if (unexpected) {
    throw new Error(`Expected no response for notification, but received: ${JSON.stringify(unexpected)}`);
  }
}

async function expectUnexpectedJsonRpcError(message, expectedCode, expectedId, timeoutMs = 1000) {
  const unexpectedStart = unexpectedMessages.length;
  writeMessage(message);

  const response = await waitForUnexpectedMessage(unexpectedStart, timeoutMs);
  if (!response?.error) {
    throw new Error(`Expected JSON-RPC error ${expectedCode}, received: ${JSON.stringify(response)}`);
  }
  if (response.id !== expectedId) {
    throw new Error(`Expected JSON-RPC error id ${expectedId}, received: ${JSON.stringify(response.id)}`);
  }
  if (response.error.code !== expectedCode) {
    throw new Error(`Expected JSON-RPC error ${expectedCode}, received ${response.error.code}: ${response.error.message}`);
  }
}

function waitForUnexpectedMessage(startIndex, timeoutMs) {
  if (unexpectedMessages.length > startIndex) {
    return Promise.resolve(unexpectedMessages[startIndex]);
  }

  return new Promise(resolve => {
    const timeout = setTimeout(() => {
      removeUnexpectedWaiter(waiter);
      resolve(null);
    }, timeoutMs);

    const waiter = message => {
      clearTimeout(timeout);
      resolve(message);
    };

    unexpectedWaiters.push(waiter);
  });
}

function removeUnexpectedWaiter(waiter) {
  const index = unexpectedWaiters.indexOf(waiter);
  if (index >= 0) {
    unexpectedWaiters.splice(index, 1);
  }
}

function notify(method, params = {}) {
  writeMessage({ jsonrpc: "2.0", method, params });
}

async function callTool(name, args) {
  const result = await request("tools/call", { name, arguments: args });
  const text = result.content?.find(item => item.type === "text")?.text;
  if (!text) return null;
  return JSON.parse(text);
}

async function restJson(path, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), restTimeoutMs);
  const requestOptions = {
    method: options.method || "GET",
    headers: { Accept: "application/json" },
    signal: controller.signal
  };

  if (options.body !== undefined) {
    requestOptions.headers["Content-Type"] = "application/json";
    requestOptions.body = JSON.stringify(options.body);
  }

  let response;
  let text;
  try {
    response = await fetch(`${apiBaseUrl}${path}`, requestOptions);
    text = await response.text();
  } catch (error) {
    if (error.name === "AbortError") {
      throw new Error(`REST ${requestOptions.method} ${path} timed out after ${restTimeoutMs}ms.`);
    }
    throw error;
  } finally {
    clearTimeout(timeout);
  }

  const payload = text.length > 0 ? parseJsonOrText(text) : null;

  if (!response.ok) {
    const detail = typeof payload === "string" ? payload : JSON.stringify(payload);
    throw new Error(`REST ${response.status} ${response.statusText}: ${detail}`);
  }

  if (options.expectNoContent) {
    return null;
  }

  return payload;
}

function parseJsonOrText(text) {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function writeMessage(message, options = {}) {
  const body = JSON.stringify(message);
  writeRawFrame(body, options);
}

function writeRawFrame(body, options = {}) {
  const framed = `Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`;
  if (options.chunked) {
    const splitAt = Math.max(1, Math.floor(framed.length / 2));
    server.stdin.write(framed.slice(0, splitAt));
    setTimeout(() => server.stdin.write(framed.slice(splitAt)), 10);
    return;
  }

  server.stdin.write(framed);
}

function readMessages() {
  while (buffer.length > 0) {
    const headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd === -1) return;

    const header = buffer.subarray(0, headerEnd).toString("utf8");
    const contentLengthMatch = /Content-Length:\s*(\d+)/i.exec(header);
    if (!contentLengthMatch) {
      buffer = buffer.subarray(headerEnd + 4);
      continue;
    }

    const contentLength = Number.parseInt(contentLengthMatch[1], 10);
    const messageStart = headerEnd + 4;
    const messageEnd = messageStart + contentLength;
    if (buffer.length < messageEnd) return;

    const rawMessage = buffer.subarray(messageStart, messageEnd).toString("utf8");
    buffer = buffer.subarray(messageEnd);
    const message = JSON.parse(rawMessage);
    const deferred = pending.get(message.id);
    if (!deferred) {
      unexpectedMessages.push(message);
      while (unexpectedWaiters.length > 0) {
        const waiter = unexpectedWaiters.shift();
        waiter(message);
      }
      continue;
    }
    pending.delete(message.id);

    if (message.error) {
      const error = new Error(message.error.message);
      error.code = message.error.code;
      error.data = message.error.data;
      deferred.reject(error);
    } else {
      deferred.resolve(message.result);
    }
  }
}
