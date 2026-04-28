#!/usr/bin/env node

const apiBaseUrl = (process.env.KANBANFLOW_API_URL || "http://127.0.0.1:5123").replace(/\/$/, "");
const defaultRestTimeoutMs = 10000;
const minRestTimeoutMs = 1;
const parsedRestTimeoutMs = Number.parseInt(process.env.KANBANFLOW_MCP_TIMEOUT_MS || String(defaultRestTimeoutMs), 10);
const restTimeoutMs = Number.isFinite(parsedRestTimeoutMs) && parsedRestTimeoutMs >= minRestTimeoutMs
  ? parsedRestTimeoutMs
  : defaultRestTimeoutMs;
const protocolVersion = "2024-11-05";
let inputBuffer = Buffer.alloc(0);

const tools = [
  {
    name: "list_projects",
    description: "List all projects with feature and task counts.",
    inputSchema: {
      type: "object",
      properties: {}
    }
  },
  {
    name: "get_project",
    description: "Get one project with ordered features and task counts.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id to inspect." }
      },
      required: ["projectId"]
    }
  },
  {
    name: "list_project_tasks",
    description: "List all tasks in a project as a flat ordered list.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id to inspect." }
      },
      required: ["projectId"]
    }
  },
  {
    name: "get_task",
    description: "Get one task with feature information, subtasks, and comments.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id to inspect." }
      },
      required: ["taskId"]
    }
  },
  {
    name: "get_project_board",
    description: "Get project tasks grouped by Kanban status.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id to inspect." }
      },
      required: ["projectId"]
    }
  },
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
        author: { type: "string", description: "Optional comment author." },
        type: {
          type: "string",
          enum: ["Note", "Decision", "Question", "Blocked", "Handoff", "Verification", "Comment"],
          description: "Optional typed handoff comment kind. Defaults to Note. Comment is accepted for backwards compatibility."
        }
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

class McpError extends Error {
  constructor(code, message, data = undefined) {
    super(message);
    this.code = code;
    this.data = data;
  }
}

function readMessages() {
  while (inputBuffer.length > 0) {
    const headerEnd = inputBuffer.indexOf("\r\n\r\n");
    if (headerEnd === -1) {
      return;
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
    if (hasRequestId(message)) {
      writeError(message.id, error.code || -32000, error.message, error.data);
    }
  });
}

async function handleMessage(message) {
  const validation = validateRequest(message);
  if (!validation.ok) {
    if (validation.shouldRespond) {
      writeError(validation.id, -32600, validation.message);
    }
    return;
  }

  const { id, method, params } = message;
  const shouldRespond = hasRequestId(message);

  if (method === "initialize") {
    if (!shouldRespond) return;
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
    if (!shouldRespond) return;
    writeResult(id, { tools });
    return;
  }

  if (method === "tools/call") {
    if (!shouldRespond) return;
    if (!isPlainObject(params) || typeof params.name !== "string") {
      throw new McpError(-32602, "tools/call params must include a string name.");
    }
    const toolName = params.name;
    const args = params.arguments ?? {};
    if (!isPlainObject(args)) {
      throw new McpError(-32602, "tools/call arguments must be an object.");
    }
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

  if (shouldRespond) {
    writeError(id, -32601, `Method not found: ${method}`);
  }
}

function validateRequest(message) {
  const id = isPlainObject(message) && hasRequestId(message) && isValidJsonRpcId(message.id) ? message.id : null;
  const shouldRespond = true;

  if (!isPlainObject(message)) {
    return { ok: false, id, shouldRespond, message: "Invalid request: message must be an object." };
  }

  if (message.jsonrpc !== "2.0") {
    return { ok: false, id, shouldRespond, message: "Invalid request: jsonrpc must be \"2.0\"." };
  }

  if (hasRequestId(message) && !isValidJsonRpcId(message.id)) {
    return { ok: false, id: null, shouldRespond, message: "Invalid request: id must be a string, number, or null." };
  }

  if (typeof message.method !== "string" || message.method.trim().length === 0) {
    return { ok: false, id, shouldRespond, message: "Invalid request: method must be a non-empty string." };
  }

  if (message.params !== undefined && !isPlainObject(message.params) && !Array.isArray(message.params)) {
    return { ok: false, id, shouldRespond, message: "Invalid request: params must be an object or array when provided." };
  }

  return { ok: true };
}

function hasRequestId(message) {
  return Object.prototype.hasOwnProperty.call(message, "id");
}

function isValidJsonRpcId(id) {
  return id === null || typeof id === "string" || typeof id === "number";
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

async function callTool(toolName, args) {
  switch (toolName) {
    case "list_projects":
      return restJson("/api/projects");
    case "get_project":
      return restJson(`/api/projects/${requiredInt(args, "projectId", { min: 1 })}`);
    case "list_project_tasks":
      return restJson(`/api/projects/${requiredInt(args, "projectId", { min: 1 })}/tasks`);
    case "get_task": {
      const taskId = requiredInt(args, "taskId", { min: 1 });
      const task = await restJson(`/api/tasks/${taskId}`);
      const comments = await restJson(`/api/tasks/${taskId}/comments`);
      return { ...task, comments };
    }
    case "get_project_board":
      return restJson(`/api/projects/${requiredInt(args, "projectId", { min: 1 })}/board`);
    case "get_project_context":
      return restJson(`/api/projects/${requiredInt(args, "projectId")}/context`);
    case "get_next_tasks": {
      const projectId = requiredInt(args, "projectId");
      const limit = optionalInt(args, "limit", { min: 1, max: 25 });
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
      assertEnum(args, "status", ["Backlog", "Todo", "InProgress", "Review", "Done"]);
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/status`, {
        method: "PATCH",
        body: { status: args.status }
      });
    case "add_task_comment":
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/comments`, {
        method: "POST",
        body: { text: requiredString(args, "text"), author: args.author ?? "MCP", type: optionalCommentType(args) }
      });
    default:
      throw new McpError(-32601, `Unknown tool: ${toolName}`);
  }
}

function optionalCommentType(args) {
  if (args.type === undefined || args.type === null || args.type === "") return undefined;
  assertEnum(args, "type", ["Note", "Decision", "Question", "Blocked", "Handoff", "Verification", "Comment"]);
  return args.type;
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
      throw new McpError(-32000, `REST request timed out after ${restTimeoutMs}ms.`, { path, timeoutMs: restTimeoutMs });
    }
    throw new McpError(-32000, `REST request failed: ${error.message}`, { path });
  } finally {
    clearTimeout(timeout);
  }

  const payload = text.length > 0 ? parseJsonOrText(text) : null;

  if (!response.ok) {
    const detail = typeof payload === "string" ? payload : JSON.stringify(payload);
    throw new McpError(-32000, `REST ${response.status} ${response.statusText}: ${detail}`, {
      status: response.status,
      statusText: response.statusText,
      path,
      payload
    });
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

function requiredInt(args, name, { min = undefined, max = undefined } = {}) {
  const value = Number(args[name]);
  if (!Number.isInteger(value)) {
    throw new McpError(-32602, `${name} must be an integer.`);
  }
  if (min !== undefined && value < min) {
    throw new McpError(-32602, `${name} must be greater than or equal to ${min}.`);
  }
  if (max !== undefined && value > max) {
    throw new McpError(-32602, `${name} must be less than or equal to ${max}.`);
  }
  return value;
}

function optionalInt(args, name, { min = undefined, max = undefined } = {}) {
  if (args[name] === undefined || args[name] === null || args[name] === "") return undefined;
  const value = Number(args[name]);
  if (!Number.isInteger(value)) {
    throw new McpError(-32602, `${name} must be an integer.`);
  }
  if (min !== undefined && value < min) {
    throw new McpError(-32602, `${name} must be greater than or equal to ${min}.`);
  }
  if (max !== undefined && value > max) {
    throw new McpError(-32602, `${name} must be less than or equal to ${max}.`);
  }
  return value;
}

function requiredString(args, name) {
  const value = args[name];
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new McpError(-32602, `${name} is required.`);
  }
  return value.trim();
}

function assertEnum(args, name, allowedValues) {
  if (!allowedValues.includes(args[name])) {
    throw new McpError(-32602, `${name} must be one of: ${allowedValues.join(", ")}.`);
  }
}

function writeResult(id, result) {
  writeMessage({ jsonrpc: "2.0", id, result });
}

function writeError(id, code, message, data = undefined) {
  const error = data === undefined ? { code, message } : { code, message, data };
  writeMessage({ jsonrpc: "2.0", id, error });
}

function writeMessage(message) {
  const body = JSON.stringify(message);
  process.stdout.write(`Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`);
}
