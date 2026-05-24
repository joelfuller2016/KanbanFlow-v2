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
  },

  // ── Authoring tools (R-2.2): agents can create + update + delete the entities they need. ──

  {
    name: "create_project",
    description: "Create a new project. Optional fields populate first-class documentation surfaces (description, goals, readme markdown).",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "Project name (required, ≤200 chars)." },
        description: { type: "string", description: "Optional short description (markdown)." },
        goals: { type: "string", description: "Optional high-level goals (markdown)." },
        readme: { type: "string", description: "Optional living README for the project (markdown). Primary documentation surface for agents." }
      },
      required: ["name"]
    }
  },
  {
    name: "update_project",
    description: "Update project name, description, goals, and/or readme. Use this to maintain the project's living documentation as the work evolves.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id to update." },
        name: { type: "string", description: "New name (required by API; must be non-empty)." },
        description: { type: "string", description: "New description (markdown)." },
        goals: { type: "string", description: "New goals (markdown)." },
        readme: { type: "string", description: "New README (markdown)." }
      },
      required: ["projectId", "name"]
    }
  },
  {
    name: "create_feature",
    description: "Create a feature inside a project. Optionally nest under a parent feature via parentFeatureId (cycle-protected).",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id this feature belongs to." },
        name: { type: "string", description: "Feature name (≤200 chars)." },
        description: { type: "string", description: "Optional feature description (markdown)." },
        parentFeatureId: { type: "integer", minimum: 1, description: "Optional parent feature id for nesting." },
        sortOrder: { type: "integer", minimum: 0, description: "Optional sort order within parent." }
      },
      required: ["projectId", "name"]
    }
  },
  {
    name: "update_feature",
    description: "Update an existing feature. Set parentFeatureId to 0 to clear it (make it top-level). Cycle-protected.",
    inputSchema: {
      type: "object",
      properties: {
        featureId: { type: "integer", minimum: 1, description: "Feature id to update." },
        name: { type: "string", description: "New name (required by API; must be non-empty)." },
        description: { type: "string", description: "New description (markdown)." },
        parentFeatureId: { type: "integer", minimum: 0, description: "New parent feature id, or 0 to make top-level." },
        sortOrder: { type: "integer", minimum: 0, description: "New sort order within parent." }
      },
      required: ["featureId", "name"]
    }
  },
  {
    name: "delete_feature",
    description: "Delete a feature. Cascades to its tasks and their children. Errors if the feature has child features (delete those first).",
    inputSchema: {
      type: "object",
      properties: {
        featureId: { type: "integer", minimum: 1, description: "Feature id to delete." }
      },
      required: ["featureId"]
    }
  },
  {
    name: "create_task",
    description: "Create a task inside a feature. Defaults: status=Backlog, priority=Low.",
    inputSchema: {
      type: "object",
      properties: {
        featureId: { type: "integer", minimum: 1, description: "Feature id this task belongs to." },
        name: { type: "string", description: "Task name (≤500 chars)." },
        description: { type: "string", description: "Optional task description (markdown)." },
        status: {
          type: "string",
          enum: ["Backlog", "Todo", "InProgress", "Review", "Done"],
          description: "Optional initial status. Defaults to Backlog."
        },
        priority: {
          type: "string",
          enum: ["Low", "Medium", "High", "Critical"],
          description: "Optional initial priority. Defaults to Low."
        },
        assignee: { type: "string", description: "Optional free-text assignee label." },
        sortOrder: { type: "integer", minimum: 0, description: "Optional sort order within column." }
      },
      required: ["featureId", "name"]
    }
  },
  {
    name: "update_task",
    description: "Update task fields (name, description, status, priority, assignee). Use update_task_status for status-only or reassign_task to move between features.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id to update." },
        name: { type: "string", description: "New name (required by API; must be non-empty)." },
        description: { type: "string", description: "New description (markdown)." },
        status: {
          type: "string",
          enum: ["Backlog", "Todo", "InProgress", "Review", "Done"],
          description: "New status."
        },
        priority: {
          type: "string",
          enum: ["Low", "Medium", "High", "Critical"],
          description: "New priority."
        },
        assignee: { type: "string", description: "New assignee label." },
        sortOrder: { type: "integer", minimum: 0, description: "New sort order within column." }
      },
      required: ["taskId", "name"]
    }
  },
  {
    name: "delete_task",
    description: "Delete a task. Cascades to its subtasks, acceptance criteria, comments, and dependencies. Activity row remains in audit log.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id to delete." }
      },
      required: ["taskId"]
    }
  },
  {
    name: "reassign_task",
    description: "Reassign a task to a different feature (typically in the same project). NOTE: project-boundary enforcement is a known gap (#83); the API today permits cross-project moves.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id to reassign." },
        featureId: { type: "integer", minimum: 1, description: "Target feature id." }
      },
      required: ["taskId", "featureId"]
    }
  },
  {
    name: "get_step_context",
    description: "AI agent's primary entry point: returns one task plus everything needed to act on it — feature breadcrumb, acceptance criteria, recursive subtasks, resolved dependencies (with names + status + IsComplete), recent comments (latest first), related requirements (linked to this task or its feature), and recent activity events. One call instead of five round-trips.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id to bundle context for." }
      },
      required: ["taskId"]
    }
  },

  // ── Subtask CRUD ───────────────────────────────────────────────────

  {
    name: "list_subtasks",
    description: "List all subtasks for a task (ordered by SortOrder). Returns a flat list with ParentSubtaskId so the caller can build a tree.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id whose subtasks to list." }
      },
      required: ["taskId"]
    }
  },
  {
    name: "create_subtask",
    description: "Create a subtask under a task. Optionally nest under a parent subtask via parentSubtaskId (cycle-protected).",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id this subtask belongs to." },
        text: { type: "string", description: "Subtask text (required, non-empty)." },
        parentSubtaskId: { type: "integer", minimum: 1, description: "Optional parent subtask id (must belong to the same task)." },
        sortOrder: { type: "integer", minimum: 0, description: "Optional sort order within the parent group." }
      },
      required: ["taskId", "text"]
    }
  },
  {
    name: "update_subtask",
    description: "Update a subtask's text, completed flag, parent, and/or sort order. Set parentSubtaskId to 0 to detach from any parent (make top-level under the task).",
    inputSchema: {
      type: "object",
      properties: {
        subtaskId: { type: "integer", minimum: 1, description: "Subtask id to update." },
        text: { type: "string", description: "New text. If omitted, text is left unchanged." },
        completed: { type: "boolean", description: "New completed flag." },
        parentSubtaskId: { type: "integer", minimum: 0, description: "New parent subtask id, or 0 to detach." },
        sortOrder: { type: "integer", minimum: 0, description: "New sort order within parent group." }
      },
      required: ["subtaskId"]
    }
  },
  {
    name: "mark_subtask_done",
    description: "Convenience: toggle a subtask's completed flag without supplying its text. Equivalent to update_subtask with only the completed field set.",
    inputSchema: {
      type: "object",
      properties: {
        subtaskId: { type: "integer", minimum: 1, description: "Subtask id to mark." },
        completed: { type: "boolean", description: "True to mark done, false to mark not-done." }
      },
      required: ["subtaskId", "completed"]
    }
  },
  {
    name: "delete_subtask",
    description: "Delete a subtask. Cascades to any child subtasks under it.",
    inputSchema: {
      type: "object",
      properties: {
        subtaskId: { type: "integer", minimum: 1, description: "Subtask id to delete." }
      },
      required: ["subtaskId"]
    }
  },

  // ── Acceptance criterion CRUD ──────────────────────────────────────

  {
    name: "list_acceptance_criteria",
    description: "List acceptance criteria for a task (ordered by SortOrder).",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id whose acceptance criteria to list." }
      },
      required: ["taskId"]
    }
  },
  {
    name: "create_acceptance_criterion",
    description: "Create an acceptance criterion on a task. Acceptance criteria are checkable definition-of-done items separate from subtasks.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id this acceptance criterion belongs to." },
        text: { type: "string", description: "Acceptance criterion text (required, non-empty)." },
        sortOrder: { type: "integer", minimum: 0, description: "Optional sort order within the task's criteria list." }
      },
      required: ["taskId", "text"]
    }
  },
  {
    name: "update_acceptance_criterion",
    description: "Update an acceptance criterion's text, completed flag, and/or sort order.",
    inputSchema: {
      type: "object",
      properties: {
        acId: { type: "integer", minimum: 1, description: "Acceptance criterion id to update." },
        text: { type: "string", description: "New text. If omitted, text is left unchanged." },
        completed: { type: "boolean", description: "New completed flag." },
        sortOrder: { type: "integer", minimum: 0, description: "New sort order." }
      },
      required: ["acId"]
    }
  },
  {
    name: "mark_acceptance_criterion_done",
    description: "Convenience: toggle an acceptance criterion's completed flag without supplying its text. Equivalent to update_acceptance_criterion with only the completed field set.",
    inputSchema: {
      type: "object",
      properties: {
        acId: { type: "integer", minimum: 1, description: "Acceptance criterion id to mark." },
        completed: { type: "boolean", description: "True to mark done, false to mark not-done." }
      },
      required: ["acId", "completed"]
    }
  },
  {
    name: "delete_acceptance_criterion",
    description: "Delete an acceptance criterion.",
    inputSchema: {
      type: "object",
      properties: {
        acId: { type: "integer", minimum: 1, description: "Acceptance criterion id to delete." }
      },
      required: ["acId"]
    }
  },

  // ── Task dependency CRUD ───────────────────────────────────────────

  {
    name: "list_dependencies",
    description: "List dependencies involving a task — both directions (tasks this task depends on, and tasks that depend on this task).",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id whose dependencies to list." }
      },
      required: ["taskId"]
    }
  },
  {
    name: "create_dependency",
    description: "Create a dependency between two tasks (same project; cycle-protected). For type=DependsOn, taskId is the blocked task and otherTaskId is the blocker. For type=Blocks, taskId is the blocker and otherTaskId is the blocked task. Defaults to DependsOn.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Route task id (interpreted via 'type': blocked when DependsOn, blocker when Blocks)." },
        otherTaskId: { type: "integer", minimum: 1, description: "The other task in the relationship — the blocker (when type=DependsOn) or the blocked task (when type=Blocks)." },
        type: {
          type: "string",
          enum: ["DependsOn", "Blocks"],
          description: "Optional dependency direction. Defaults to DependsOn."
        }
      },
      required: ["taskId", "otherTaskId"]
    }
  },
  {
    name: "delete_dependency",
    description: "Delete a task dependency by its id.",
    inputSchema: {
      type: "object",
      properties: {
        dependencyId: { type: "integer", minimum: 1, description: "Dependency id to delete." }
      },
      required: ["dependencyId"]
    }
  },

  // ── Activity ────────────────────────────────────────────────────────

  {
    name: "list_project_activity",
    description: "List recent activity events for a project (newest first). Limit defaults to 100 server-side, max 500.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id whose activity to list." },
        limit: { type: "integer", minimum: 1, maximum: 500, description: "Optional max number of events. Server clamps to [1, 500] and defaults to 100." }
      },
      required: ["projectId"]
    }
  },
  {
    name: "list_task_activity",
    description: "List recent activity events for a task (newest first). Limit defaults to 100 server-side, max 500.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id whose activity to list." },
        limit: { type: "integer", minimum: 1, maximum: 500, description: "Optional max number of events. Server clamps to [1, 500] and defaults to 100." }
      },
      required: ["taskId"]
    }
  },

  // ── Idea inbox CRUD (#180) ─────────────────────────────────────────

  {
    name: "create_idea",
    description: "Capture a new idea in the inbox. Always starts status=New. Promote it later with promote_idea.",
    inputSchema: {
      type: "object",
      properties: {
        title: { type: "string", description: "Idea title (required, ≤500 chars)." },
        body: { type: "string", description: "Optional markdown brain-dump body." },
        plan: { type: "string", description: "Optional markdown plan (problem · approach · scope · value)." },
        tags: { type: "string", description: "Optional comma-separated tags." },
        priority: {
          type: "string",
          enum: ["Low", "Medium", "High", "Critical"],
          description: "Optional priority."
        },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["title"]
    }
  },
  {
    name: "list_ideas",
    description: "List ideas with optional status/tag/search filters. Use to triage the inbox before promoting.",
    inputSchema: {
      type: "object",
      properties: {
        status: {
          type: "string",
          enum: ["New", "Triaging", "Promoted", "Archived", "Rejected"],
          description: "Optional status filter."
        },
        tag: { type: "string", description: "Optional case-insensitive tag substring filter." },
        q: { type: "string", description: "Optional case-insensitive search across title + body." },
        limit: { type: "integer", minimum: 1, maximum: 500, description: "Optional max results." },
        offset: { type: "integer", minimum: 0, description: "Optional offset for paging." }
      }
    }
  },
  {
    name: "get_idea",
    description: "Get one idea in detail (title, body, plan, status, tags, priority, promoted project id).",
    inputSchema: {
      type: "object",
      properties: {
        ideaId: { type: "integer", minimum: 1, description: "Idea id to inspect." }
      },
      required: ["ideaId"]
    }
  },
  {
    name: "update_idea",
    description: "Update mutable idea fields (title, body, plan, status, tags, priority). Omitted fields are left unchanged.",
    inputSchema: {
      type: "object",
      properties: {
        ideaId: { type: "integer", minimum: 1, description: "Idea id to update." },
        title: { type: "string", description: "New title (≤500 chars)." },
        body: { type: "string", description: "New markdown body." },
        plan: { type: "string", description: "New markdown plan." },
        status: {
          type: "string",
          enum: ["New", "Triaging", "Promoted", "Archived", "Rejected"],
          description: "New status."
        },
        tags: { type: "string", description: "New comma-separated tags." },
        priority: {
          type: "string",
          enum: ["Low", "Medium", "High", "Critical"],
          description: "New priority."
        },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["ideaId"]
    }
  },
  {
    name: "promote_idea",
    description: "Promote an idea into a new project (Title→Name, Body→Description, Plan→Goals). Fails if already promoted. Returns the idea id and new project id.",
    inputSchema: {
      type: "object",
      properties: {
        ideaId: { type: "integer", minimum: 1, description: "Idea id to promote." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["ideaId"]
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

    // ── Authoring tools ──

    case "create_project":
      return restJson("/api/projects", {
        method: "POST",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          goals: optionalString(args, "goals"),
          readme: optionalString(args, "readme")
        }
      });
    case "update_project":
      return restJson(`/api/projects/${requiredInt(args, "projectId", { min: 1 })}`, {
        method: "PUT",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          goals: optionalString(args, "goals"),
          readme: optionalString(args, "readme")
        }
      });
    case "create_feature":
      return restJson(`/api/projects/${requiredInt(args, "projectId", { min: 1 })}/features`, {
        method: "POST",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          parentFeatureId: optionalInt(args, "parentFeatureId", { min: 1 }),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "update_feature":
      return restJson(`/api/features/${requiredInt(args, "featureId", { min: 1 })}`, {
        method: "PUT",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          parentFeatureId: optionalInt(args, "parentFeatureId", { min: 0 }),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "delete_feature":
      return restJson(`/api/features/${requiredInt(args, "featureId", { min: 1 })}`, { method: "DELETE" });
    case "create_task":
      if (args.status !== undefined && args.status !== null && args.status !== "")
        assertEnum(args, "status", ["Backlog", "Todo", "InProgress", "Review", "Done"]);
      if (args.priority !== undefined && args.priority !== null && args.priority !== "")
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson(`/api/features/${requiredInt(args, "featureId", { min: 1 })}/tasks`, {
        method: "POST",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          status: optionalString(args, "status"),
          priority: optionalString(args, "priority"),
          assignee: optionalString(args, "assignee"),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "update_task":
      if (args.status !== undefined && args.status !== null && args.status !== "")
        assertEnum(args, "status", ["Backlog", "Todo", "InProgress", "Review", "Done"]);
      if (args.priority !== undefined && args.priority !== null && args.priority !== "")
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}`, {
        method: "PUT",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          status: optionalString(args, "status"),
          priority: optionalString(args, "priority"),
          assignee: optionalString(args, "assignee"),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "delete_task":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}`, { method: "DELETE" });
    case "reassign_task":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/feature`, {
        method: "PATCH",
        body: { featureId: requiredInt(args, "featureId", { min: 1 }) }
      });
    case "get_step_context":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/context`);

    // ── Subtask CRUD ──

    case "list_subtasks":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/subtasks`);
    case "create_subtask":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/subtasks`, {
        method: "POST",
        body: {
          text: requiredString(args, "text"),
          parentSubtaskId: optionalInt(args, "parentSubtaskId", { min: 1 }),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "update_subtask":
      return restJson(`/api/subtasks/${requiredInt(args, "subtaskId", { min: 1 })}`, {
        method: "PUT",
        body: {
          text: optionalString(args, "text"),
          completed: optionalBool(args, "completed"),
          parentSubtaskId: optionalInt(args, "parentSubtaskId", { min: 0 }),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "mark_subtask_done":
      return restJson(`/api/subtasks/${requiredInt(args, "subtaskId", { min: 1 })}`, {
        method: "PUT",
        body: { completed: requiredBool(args, "completed") }
      });
    case "delete_subtask":
      return restJson(`/api/subtasks/${requiredInt(args, "subtaskId", { min: 1 })}`, { method: "DELETE" });

    // ── Acceptance criterion CRUD ──

    case "list_acceptance_criteria":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/acceptance-criteria`);
    case "create_acceptance_criterion":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/acceptance-criteria`, {
        method: "POST",
        body: {
          text: requiredString(args, "text"),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "update_acceptance_criterion":
      return restJson(`/api/acceptance-criteria/${requiredInt(args, "acId", { min: 1 })}`, {
        method: "PUT",
        body: {
          text: optionalString(args, "text"),
          completed: optionalBool(args, "completed"),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "mark_acceptance_criterion_done":
      return restJson(`/api/acceptance-criteria/${requiredInt(args, "acId", { min: 1 })}`, {
        method: "PUT",
        body: { completed: requiredBool(args, "completed") }
      });
    case "delete_acceptance_criterion":
      return restJson(`/api/acceptance-criteria/${requiredInt(args, "acId", { min: 1 })}`, { method: "DELETE" });

    // ── Task dependency CRUD ──

    case "list_dependencies":
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}/dependencies`);
    case "create_dependency": {
      const taskId = requiredInt(args, "taskId", { min: 1 });
      const otherTaskId = requiredInt(args, "otherTaskId", { min: 1 });
      let type;
      if (args.type !== undefined && args.type !== null && args.type !== "") {
        assertEnum(args, "type", ["DependsOn", "Blocks"]);
        type = args.type;
      }
      // For type=Blocks, the route task IS the blocker, so the other side is the BLOCKED
      // task — that field is `BlockedTaskId` server-side. For DependsOn, the other side
      // is the BLOCKING task — `DependsOnTaskItemId`. Sending the wrong one silently
      // inverted the relationship before this fix.
      const body = type === "Blocks"
        ? { blockedTaskId: otherTaskId, type }
        : { dependsOnTaskItemId: otherTaskId, type };
      return restJson(`/api/tasks/${taskId}/dependencies`, { method: "POST", body });
    }
    case "delete_dependency":
      return restJson(`/api/task-dependencies/${requiredInt(args, "dependencyId", { min: 1 })}`, { method: "DELETE" });

    // ── Activity ──

    case "list_project_activity": {
      const projectId = requiredInt(args, "projectId", { min: 1 });
      const limit = optionalInt(args, "limit", { min: 1, max: 500 });
      const query = limit === undefined ? "" : `?limit=${encodeURIComponent(limit)}`;
      return restJson(`/api/projects/${projectId}/activity${query}`);
    }
    case "list_task_activity": {
      const taskId = requiredInt(args, "taskId", { min: 1 });
      const limit = optionalInt(args, "limit", { min: 1, max: 500 });
      const query = limit === undefined ? "" : `?limit=${encodeURIComponent(limit)}`;
      return restJson(`/api/tasks/${taskId}/activity${query}`);
    }

    // ── Idea inbox CRUD ──

    case "create_idea":
      // A present-but-blank enum is invalid (consistent with native tools + REST):
      // only a genuinely omitted (undefined/null) value stays optional.
      if (args.priority !== undefined && args.priority !== null)
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson("/api/ideas", {
        method: "POST",
        body: {
          title: requiredString(args, "title"),
          body: optionalString(args, "body"),
          plan: optionalString(args, "plan"),
          tags: optionalString(args, "tags"),
          priority: optionalString(args, "priority"),
          actor: optionalString(args, "actor")
        }
      });
    case "list_ideas": {
      if (args.status !== undefined && args.status !== null && args.status !== "")
        assertEnum(args, "status", ["New", "Triaging", "Promoted", "Archived", "Rejected"]);
      const params = new URLSearchParams();
      const status = optionalString(args, "status");
      if (status !== undefined) params.set("status", status);
      const tag = optionalString(args, "tag");
      if (tag !== undefined) params.set("tag", tag);
      const q = optionalString(args, "q");
      if (q !== undefined) params.set("q", q);
      const limit = optionalInt(args, "limit", { min: 1, max: 500 });
      if (limit !== undefined) params.set("limit", String(limit));
      const offset = optionalInt(args, "offset", { min: 0 });
      if (offset !== undefined) params.set("offset", String(offset));
      const query = params.toString();
      return restJson(`/api/ideas${query ? `?${query}` : ""}`);
    }
    case "get_idea":
      return restJson(`/api/ideas/${requiredInt(args, "ideaId", { min: 1 })}`);
    case "update_idea":
      // A present-but-blank enum is invalid (consistent with native tools + REST):
      // only a genuinely omitted (undefined/null) value stays optional.
      if (args.status !== undefined && args.status !== null)
        assertEnum(args, "status", ["New", "Triaging", "Promoted", "Archived", "Rejected"]);
      if (args.priority !== undefined && args.priority !== null)
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson(`/api/ideas/${requiredInt(args, "ideaId", { min: 1 })}`, {
        method: "PATCH",
        body: {
          title: optionalString(args, "title"),
          body: optionalString(args, "body"),
          plan: optionalString(args, "plan"),
          status: optionalString(args, "status"),
          tags: optionalString(args, "tags"),
          priority: optionalString(args, "priority"),
          actor: optionalString(args, "actor")
        }
      });
    case "promote_idea":
      return restJson(`/api/ideas/${requiredInt(args, "ideaId", { min: 1 })}/promote`, {
        method: "POST",
        body: { actor: optionalString(args, "actor") }
      });

    default:
      throw new McpError(-32601, `Unknown tool: ${toolName}`);
  }
}

function optionalString(args, name) {
  const value = args[name];
  if (value === undefined || value === null || value === "") return undefined;
  if (typeof value !== "string") {
    throw new McpError(-32602, `${name} must be a string when provided.`);
  }
  return value;
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

function requiredBool(args, name) {
  const value = args[name];
  if (typeof value !== "boolean") {
    throw new McpError(-32602, `${name} is required and must be a boolean.`);
  }
  return value;
}

function optionalBool(args, name) {
  const value = args[name];
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "boolean") {
    throw new McpError(-32602, `${name} must be a boolean when provided.`);
  }
  return value;
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
