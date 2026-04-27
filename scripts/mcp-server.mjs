#!/usr/bin/env node

const apiBaseUrl = (process.env.KANBANFLOW_API_URL || "http://127.0.0.1:5123").replace(/\/$/, "");
const protocolVersion = "2024-11-05";
let inputBuffer = Buffer.alloc(0);

const tools = [
  {
    name: "get_project_context",
    description: "Get AI-readable project metadata, summary counts, requirements, nested features, tasks, subtasks, and comments.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", description: "Project id to inspect." }
      },
      required: ["projectId"]
    }
  },
  {
    name: "get_next_tasks",
    description: "Get ranked next-task recommendations for a project.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", description: "Project id to inspect." },
        limit: { type: "integer", minimum: 1, maximum: 25, description: "Optional maximum number of recommendations." }
      },
      required: ["projectId"]
    }
  },
  {
    name: "claim_task",
    description: "Claim a task for an AI/human actor. Returns a conflict if another actor already owns the claim.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", description: "Task id to claim." },
        actor: { type: "string", description: "Actor claiming the task." }
      },
      required: ["taskId", "actor"]
    }
  },
  {
    name: "release_task",
    description: "Release a task claimed by the same actor.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", description: "Task id to release." },
        actor: { type: "string", description: "Actor releasing the task." }
      },
      required: ["taskId", "actor"]
    }
  },
  {
    name: "update_task_status",
    description: "Move a task to a different Kanban status.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", description: "Task id to update." },
        status: {
          type: "string",
          enum: ["Backlog", "Todo", "InProgress", "Review", "Done"],
          description: "New task status."
        }
      },
      required: ["taskId", "status"]
    }
  },
  {
    name: "add_task_comment",
    description: "Add a comment to a task's activity stream.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", description: "Task id to comment on." },
        text: { type: "string", description: "Comment text." },
        author: { type: "string", description: "Optional comment author." }
      },
      required: ["taskId", "text"]
    }
  }
];

process.stdin.on("data", chunk => {
  inputBuffer = Buffer.concat([inputBuffer, chunk]);
  readMessages();
});

process.stdin.on("end", () => process.exit(0));
process.stdin.resume();

function readMessages() {
  while (inputBuffer.length > 0) {
    const headerEnd = inputBuffer.indexOf("\r\n\r\n");
    if (headerEnd === -1) {
      const newlineEnd = inputBuffer.indexOf("\n");
      if (newlineEnd === -1) return;

      const line = inputBuffer.subarray(0, newlineEnd).toString("utf8").trim();
      inputBuffer = inputBuffer.subarray(newlineEnd + 1);
      if (line.length === 0) continue;
      handleRawMessage(line);
      continue;
    }

    const header = inputBuffer.subarray(0, headerEnd).toString("utf8");
    const contentLengthMatch = /Content-Length:\s*(\d+)/i.exec(header);
    if (!contentLengthMatch) {
      inputBuffer = inputBuffer.subarray(headerEnd + 4);
      continue;
    }

    const contentLength = Number.parseInt(contentLengthMatch[1], 10);
    const messageStart = headerEnd + 4;
    const messageEnd = messageStart + contentLength;
    if (inputBuffer.length < messageEnd) return;

    const rawMessage = inputBuffer.subarray(messageStart, messageEnd).toString("utf8");
    inputBuffer = inputBuffer.subarray(messageEnd);
    handleRawMessage(rawMessage);
  }
}

function handleRawMessage(rawMessage) {
  let message;
  try {
    message = JSON.parse(rawMessage);
  } catch (error) {
    writeError(null, -32700, `Parse error: ${error.message}`);
    return;
  }

  handleMessage(message).catch(error => {
    if (message.id !== undefined) {
      writeError(message.id, -32000, error.message);
    }
  });
}

async function handleMessage(message) {
  const { id, method, params } = message;

  if (method === "initialize") {
    writeResult(id, {
      protocolVersion,
      capabilities: { tools: {} },
      serverInfo: { name: "kanbanflow-mcp", version: "0.1.0" }
    });
    return;
  }

  if (method === "notifications/initialized") {
    return;
  }

  if (method === "tools/list") {
    writeResult(id, { tools });
    return;
  }

  if (method === "tools/call") {
    const toolName = params?.name;
    const args = params?.arguments || {};
    const result = await callTool(toolName, args);
    writeResult(id, {
      content: [
        {
          type: "text",
          text: JSON.stringify(result, null, 2)
        }
      ]
    });
    return;
  }

  if (id !== undefined) {
    writeError(id, -32601, `Method not found: ${method}`);
  }
}

async function callTool(toolName, args) {
  switch (toolName) {
    case "get_project_context":
      return restJson(`/api/projects/${requiredInt(args, "projectId")}/context`);
    case "get_next_tasks": {
      const projectId = requiredInt(args, "projectId");
      const limit = optionalInt(args, "limit");
      const query = limit === undefined ? "" : `?limit=${encodeURIComponent(limit)}`;
      return restJson(`/api/projects/${projectId}/next-tasks${query}`);
    }
    case "claim_task":
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/claim`, {
        method: "POST",
        body: { actor: requiredString(args, "actor") }
      });
    case "release_task":
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/release`, {
        method: "POST",
        body: { actor: requiredString(args, "actor") }
      });
    case "update_task_status":
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/status`, {
        method: "PATCH",
        body: { status: requiredString(args, "status") }
      });
    case "add_task_comment":
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/comments`, {
        method: "POST",
        body: { text: requiredString(args, "text"), author: args.author ?? "MCP" }
      });
    default:
      throw new Error(`Unknown tool: ${toolName}`);
  }
}

async function restJson(path, options = {}) {
  const requestOptions = {
    method: options.method || "GET",
    headers: { Accept: "application/json" }
  };

  if (options.body !== undefined) {
    requestOptions.headers["Content-Type"] = "application/json";
    requestOptions.body = JSON.stringify(options.body);
  }

  const response = await fetch(`${apiBaseUrl}${path}`, requestOptions);
  const text = await response.text();
  const payload = text.length > 0 ? parseJsonOrText(text) : null;

  if (!response.ok) {
    const detail = typeof payload === "string" ? payload : JSON.stringify(payload);
    throw new Error(`REST ${response.status} ${response.statusText}: ${detail}`);
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

function requiredInt(args, name) {
  const value = Number(args[name]);
  if (!Number.isInteger(value)) {
    throw new Error(`${name} must be an integer.`);
  }
  return value;
}

function optionalInt(args, name) {
  if (args[name] === undefined || args[name] === null || args[name] === "") return undefined;
  const value = Number(args[name]);
  if (!Number.isInteger(value)) {
    throw new Error(`${name} must be an integer.`);
  }
  return value;
}

function requiredString(args, name) {
  const value = args[name];
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`${name} is required.`);
  }
  return value.trim();
}

function writeResult(id, result) {
  writeMessage({ jsonrpc: "2.0", id, result });
}

function writeError(id, code, message) {
  writeMessage({ jsonrpc: "2.0", id, error: { code, message } });
}

function writeMessage(message) {
  const body = JSON.stringify(message);
  process.stdout.write(`Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`);
}
