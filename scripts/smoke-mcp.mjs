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
const server = spawn(process.execPath, [serverPath], {
  stdio: ["pipe", "pipe", "inherit"],
  env: { ...process.env }
});

let nextId = 1;
let buffer = Buffer.alloc(0);
const pending = new Map();

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

  const list = await request("tools/list", {});
  const toolNames = new Set(list.tools.map(tool => tool.name));
  for (const requiredTool of ["get_project_context", "get_next_tasks", "claim_task", "release_task", "update_task_status", "add_task_comment"]) {
    if (!toolNames.has(requiredTool)) {
      throw new Error(`Missing MCP tool: ${requiredTool}`);
    }
  }

  const context = await callTool("get_project_context", { projectId });
  const task = findFirstTask(context.features || []);
  if (!task) {
    throw new Error(`No task found in project ${projectId}; cannot run MCP mutation smoke.`);
  }

  await callTool("get_next_tasks", { projectId, limit: 3 });
  await callTool("claim_task", { taskId: task.id, actor: "mcp-smoke" });
  await callTool("add_task_comment", { taskId: task.id, text: "MCP smoke test comment", author: "mcp-smoke" });
  await callTool("update_task_status", { taskId: task.id, status: "Review" });
  await callTool("release_task", { taskId: task.id, actor: "mcp-smoke" });

  console.log(`MCP smoke tests passed for project ${projectId}, task ${task.id}.`);
} finally {
  server.kill();
}

function request(method, params) {
  const id = nextId++;
  const message = { jsonrpc: "2.0", id, method, params };
  writeMessage(message);

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

function notify(method, params = {}) {
  writeMessage({ jsonrpc: "2.0", method, params });
}

async function callTool(name, args) {
  const result = await request("tools/call", { name, arguments: args });
  const text = result.content?.find(item => item.type === "text")?.text;
  if (!text) return null;
  return JSON.parse(text);
}

function writeMessage(message) {
  const body = JSON.stringify(message);
  server.stdin.write(`Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`);
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
    if (!deferred) continue;
    pending.delete(message.id);

    if (message.error) {
      deferred.reject(new Error(message.error.message));
    } else {
      deferred.resolve(message.result);
    }
  }
}

function findFirstTask(features) {
  for (const feature of features) {
    if (Array.isArray(feature.tasks) && feature.tasks.length > 0) {
      return feature.tasks[0];
    }

    const childTask = findFirstTask(feature.childFeatures || feature.children || []);
    if (childTask) return childTask;
  }

  return null;
}
