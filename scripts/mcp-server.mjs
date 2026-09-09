#!/usr/bin/env node

import { createUsageLogger } from "./usage-log.mjs";

const apiBaseUrl = (process.env.KANBANFLOW_API_URL || "http://127.0.0.1:5123").replace(/\/$/, "");
const defaultRestTimeoutMs = 10000;
const minRestTimeoutMs = 1;
const parsedRestTimeoutMs = Number.parseInt(process.env.KANBANFLOW_MCP_TIMEOUT_MS || String(defaultRestTimeoutMs), 10);
const restTimeoutMs = Number.isFinite(parsedRestTimeoutMs) && parsedRestTimeoutMs >= minRestTimeoutMs
  ? parsedRestTimeoutMs
  : defaultRestTimeoutMs;
// Two independent protocol hops, deliberately versioned separately (PR #259 review):
// - SUPPORTED_PROTOCOL_VERSIONS is what THIS stdio wrapper speaks to its client. `initialize`
//   echoes the client's requested revision when it is in this list, else the newest we support
//   (SUPPORTED_PROTOCOL_VERSIONS[0]) — rather than unconditionally asserting one revision.
//   2024-11-05 remains supported; it is simply no longer asserted unilaterally.
// - nativeProtocolVersion is the header sent on the HTTP hop to the app's native /mcp server
//   (the revision the ModelContextProtocol.AspNetCore 1.4 SDK negotiates). Unrelated to the
//   stdio hop above and deliberately pinned.
// Newest first — SUPPORTED_PROTOCOL_VERSIONS[0] is the fallback.
// 2025-03-26 is deliberately EXCLUDED: it requires servers to accept JSON-RPC batches
// (batching was only removed in 2025-06-18 — see that revision's changelog, "Remove support
// for JSON-RPC batching"). validateRequest() rejects arrays with -32600, so advertising
// 2025-03-26 would claim conformance we do not have. Only advertise what the handlers implement.
const SUPPORTED_PROTOCOL_VERSIONS = ["2025-06-18", "2024-11-05"];
const protocolVersion = SUPPORTED_PROTOCOL_VERSIONS[0];
const nativeProtocolVersion = "2025-06-18";

// Upper bound on a single NDJSON line. Guards against a peer that streams bytes but never
// sends a "\n" — without it, inputBuffer would grow until the process exhausts memory.
const MAX_STDIO_MESSAGE_BYTES = 8 * 1024 * 1024;

function negotiateProtocolVersion(requested) {
  // Only echo a string we actually support; anything else (absent, number, null, array,
  // unknown revision) falls back to the newest supported version.
  return typeof requested === "string" && SUPPORTED_PROTOCOL_VERSIONS.includes(requested)
    ? requested
    : protocolVersion;
}
let inputBuffer = Buffer.alloc(0);

// Requests we've received an id for but not yet answered (crash-safety, #168). A fatal
// async fault (see the process handlers below) reports a -32603 internal error for these so
// a client isn't left hanging; a request's id is cleared the moment it is answered so the
// fault path and the request's own completion can never both emit a frame for the same id.
const inFlightRequestIds = new Set();

// One recorder per wrapper process, which is one client session — the identity #477 §11 signal 1
// actually counts. Constructed here rather than lazily so the session id is stable from the first
// call. Construction and record() are both non-throwing by contract in usage-log.mjs.
const usageLogger = createUsageLogger();

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
        limit: { type: "integer", minimum: 1, maximum: 50, description: "Optional maximum number of recommendations (1–50). Matches the REST clamp (#151) and the native tool." }
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
    description: "Release a task claimed by the same actor. Actor defaults to \"mcp-agent\" when omitted.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", description: "Task id to release." },
        actor: { type: "string", description: "Optional actor releasing the task. Defaults to \"mcp-agent\" (mirrors the native tool)." }
      },
      required: ["taskId"]
    }
  },
  {
    name: "update_task_status",
    description: "Move a task to a different Kanban status. Moving to Done is refused (HTTP 422) while the task has unmet acceptance criteria; pass overrideReason to record why it is done anyway.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", description: "Task id to update." },
        status: {
          type: "string",
          enum: ["Backlog", "Todo", "InProgress", "Review", "Done"],
          description: "New task status."
        },
        actor: {
          type: "string",
          description: "Optional actor label recorded in the audit trail (default 'mcp-agent')."
        },
        overrideReason: {
          type: "string",
          description: "Optional. Only needed when moving to Done with unmet acceptance criteria: a short reason why the task is done anyway. Recorded as an audited Verification comment plus a TaskDoneOverridden activity event."
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
    description: "Update project name and living documentation. For description/goals/readme: omit or null to preserve, empty/whitespace to clear, non-empty text to replace.",
    inputSchema: {
      type: "object",
      properties: {
        projectId: { type: "integer", minimum: 1, description: "Project id to update." },
        name: { type: "string", description: "New name (required by API; must be non-empty)." },
        description: { type: ["string", "null"], description: "Markdown description: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
        goals: { type: ["string", "null"], description: "Markdown goals: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
        readme: { type: ["string", "null"], description: "Markdown README: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." }
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
    description: "Update an existing feature. For description: omit or null to preserve, empty/whitespace to clear, non-empty text to replace. Set parentFeatureId to 0 to make it top-level (cycle-protected).",
    inputSchema: {
      type: "object",
      properties: {
        featureId: { type: "integer", minimum: 1, description: "Feature id to update." },
        name: { type: "string", description: "New name (required by API; must be non-empty)." },
        description: { type: ["string", "null"], description: "Markdown description: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
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
        sortOrder: { type: "number", description: "Optional finite task rank within the column; may be fractional." }
      },
      required: ["featureId", "name"]
    }
  },
  {
    name: "update_task",
    description: "Update task fields (name, description, status, priority, assignee). For description/assignee: omit or null to preserve, empty/whitespace to clear, non-empty text to replace. Use update_task_status for status-only or reassign_task to move between features.",
    inputSchema: {
      type: "object",
      properties: {
        taskId: { type: "integer", minimum: 1, description: "Task id to update." },
        name: { type: "string", description: "New name (required by API; must be non-empty)." },
        description: { type: ["string", "null"], description: "Markdown description: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
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
        assignee: { type: ["string", "null"], description: "Assignee label: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
        sortOrder: { type: "number", description: "New finite task rank within the column; may be fractional." }
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
        completed: { type: "boolean", description: "Optional. True (the default) marks done, false marks not-done." }
      },
      // `completed` is OPTIONAL, matching the native /mcp tool of the same name (#438). It was
      // required here while native defaulted it to true, so the identical call succeeded on one
      // transport and failed -32602 on the other. Relaxing rather than tightening keeps every
      // existing caller working, and the default the tool NAME implies is the useful one.
      required: ["acId"]
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
    description: "Update mutable idea fields (title, body, plan, status, tags, priority). For body/plan/tags: omit or null to preserve, empty/whitespace to clear, non-empty text to replace. Other omitted/null fields are left unchanged.",
    inputSchema: {
      type: "object",
      properties: {
        ideaId: { type: "integer", minimum: 1, description: "Idea id to update." },
        title: { type: "string", description: "New title (≤500 chars)." },
        body: { type: ["string", "null"], description: "Markdown body: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
        plan: { type: ["string", "null"], description: "Markdown plan: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
        status: {
          type: "string",
          // `Promoted` is DERIVED and deliberately absent (#283): the service rejects it here, so
          // advertising it would hand agents a status that always 400s. promote_idea is its only
          // writer. (list_ideas keeps it — filtering by promoted ideas is legitimate.)
          enum: ["New", "Triaging", "Archived", "Rejected"],
          description: "New status. 'Promoted' is derived and terminal — use promote_idea (it also creates the linked project); an already-promoted idea cannot be moved to another status either, though its other fields stay editable."
        },
        tags: { type: ["string", "null"], description: "Comma-separated tags: omit or null to preserve, empty/whitespace to clear, non-empty text to replace." },
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
  },

  // ── Knowledge-graph memory tools (#224) — official MCP memory server contract ──
  // These proxy to the NATIVE MCP server at POST {API}/mcp (stateless Streamable HTTP) rather
  // than a REST route: the knowledge-graph semantics (name resolution, dupe skipping, cascade)
  // live in the native tools, and forwarding keeps one source of truth. Shapes match
  // modelcontextprotocol/servers src/memory exactly; scopeKey/actor are optional extensions.

  {
    name: "create_entities",
    description: "Create multiple new entities in the knowledge-graph memory. Entities whose name already exists are ignored; returns only the newly created entities.",
    inputSchema: {
      type: "object",
      properties: {
        entities: {
          type: "array",
          description: "Entities to create.",
          items: {
            type: "object",
            properties: {
              name: { type: "string", description: "Unique entity name (identity text)." },
              entityType: { type: "string", description: "Free-form entity type label (e.g. 'person', 'project')." },
              observations: { type: "array", items: { type: "string" }, description: "Optional atomic facts about the entity." }
            },
            required: ["name", "entityType"]
          }
        },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["entities"]
    }
  },
  {
    name: "create_relations",
    description: "Create multiple new relations between existing knowledge-graph entities (directed, active voice). Exact duplicates are skipped; errors if a referenced entity does not exist.",
    inputSchema: {
      type: "object",
      properties: {
        relations: {
          type: "array",
          description: "Relations to create.",
          items: {
            type: "object",
            properties: {
              from: { type: "string", description: "Source entity name." },
              to: { type: "string", description: "Target entity name." },
              relationType: { type: "string", description: "Active-voice relation verb (e.g. 'works_at')." }
            },
            required: ["from", "to", "relationType"]
          }
        },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["relations"]
    }
  },
  {
    name: "add_observations",
    description: "Add new observations to existing knowledge-graph entities. Already-present observations are skipped; returns the observations actually added per entity. Errors if an entity does not exist.",
    inputSchema: {
      type: "object",
      properties: {
        observations: {
          type: "array",
          description: "Additions per entity.",
          items: {
            type: "object",
            properties: {
              entityName: { type: "string", description: "Name of the entity to add observations to." },
              contents: { type: "array", items: { type: "string" }, description: "Observation strings to add." }
            },
            required: ["entityName", "contents"]
          }
        },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["observations"]
    }
  },
  {
    name: "delete_entities",
    description: "Delete entities from the knowledge-graph memory by name, cascading their relations. Missing names are silently skipped. Deletion is a soft-supersede — audit history remains.",
    inputSchema: {
      type: "object",
      properties: {
        entityNames: { type: "array", items: { type: "string" }, description: "Names of the entities to delete." },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["entityNames"]
    }
  },
  {
    name: "delete_observations",
    description: "Delete specific observations from knowledge-graph entities. Missing entities or observations are silently skipped.",
    inputSchema: {
      type: "object",
      properties: {
        deletions: {
          type: "array",
          description: "Deletions per entity.",
          items: {
            type: "object",
            properties: {
              entityName: { type: "string", description: "Name of the entity to delete observations from." },
              observations: { type: "array", items: { type: "string" }, description: "Observation strings to remove." }
            },
            required: ["entityName", "observations"]
          }
        },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["deletions"]
    }
  },
  {
    name: "delete_relations",
    description: "Delete relations from the knowledge-graph memory (matched exactly). Missing relations or entities are silently skipped.",
    inputSchema: {
      type: "object",
      properties: {
        relations: {
          type: "array",
          description: "Relations to delete.",
          items: {
            type: "object",
            properties: {
              from: { type: "string", description: "Source entity name." },
              to: { type: "string", description: "Target entity name." },
              relationType: { type: "string", description: "Relation verb to match." }
            },
            required: ["from", "to", "relationType"]
          }
        },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." },
        actor: { type: "string", description: "Optional actor label recorded in the audit trail." }
      },
      required: ["relations"]
    }
  },
  {
    name: "read_graph",
    description: "Read the entire knowledge-graph memory: every current entity and the relations between them (capped at the first 1000 entities).",
    inputSchema: {
      type: "object",
      properties: {
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." }
      }
    }
  },
  {
    name: "search_nodes",
    description: "Search the knowledge-graph memory (hybrid semantic + keyword retrieval; degrades to keyword when embeddings are unavailable). Returns matching entities plus only the relations between them.",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Free-text query; matches names, types, and observations." },
        k: { type: "integer", minimum: 1, maximum: 100, description: "Optional max entities to return (default 10)." },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." }
      },
      required: ["query"]
    }
  },
  {
    name: "open_nodes",
    description: "Open specific knowledge-graph entities by name: returns those entities plus only the relations between them. Missing names are silently skipped.",
    inputSchema: {
      type: "object",
      properties: {
        names: { type: "array", items: { type: "string" }, description: "Names of the entities to open." },
        scopeKey: { type: "string", description: "Optional memory namespace key (default 'knowledge-graph')." }
      },
      required: ["names"]
    }
  }
];

process.stdin.on("data", chunk => {
  inputBuffer = Buffer.concat([inputBuffer, chunk]);
  readMessages();
});

process.stdin.on("end", () => process.exit(0));
process.stdin.resume();

// Crash-safety (#168): a rejected promise with no catch handler, or a throw in a callback
// outside the per-request try/catch, would otherwise terminate this long-running stdio
// wrapper and silently drop every subsequent request. Log the fault to STDERR only — stdout
// carries the JSON-RPC framing and any stray byte there corrupts the stream — answer any
// in-flight request with an internal error (the -32603 is itself a JSON-RPC frame and so
// correctly goes to stdout), and stay alive. process.exit is deliberately NOT called: a
// single bad turn must not take the server down.
function reportFatal(kind, reason) {
  try {
    const detail = reason instanceof Error ? (reason.stack || reason.message) : String(reason);
    process.stderr.write(`[kanbanflow-mcp] ${kind}: ${detail}\n`);
  } catch {
    // stderr itself is unavailable — nothing safe is left to do.
  }
  // Snapshot the ids: writeError clears each from the set as it responds.
  for (const id of [...inFlightRequestIds]) {
    try {
      writeError(id, -32603, `Internal error (${kind}).`);
    } catch {
      // A failed write must not re-enter the fault handler.
    }
  }
}

process.on("unhandledRejection", reason => reportFatal("unhandledRejection", reason));
process.on("uncaughtException", error => reportFatal("uncaughtException", error));

class McpError extends Error {
  constructor(code, message, data = undefined) {
    super(message);
    this.code = code;
    this.data = data;
  }
}

function readMessages() {
  // Newline-delimited JSON per the MCP stdio spec. A chunk may split mid-message,
  // so anything after the last newline is retained in inputBuffer until it completes.
  while (inputBuffer.length > 0) {
    const newlineIndex = inputBuffer.indexOf("\n");
    if (newlineIndex === -1) {
      // Bound the retained partial: a peer that never sends "\n" would otherwise grow
      // inputBuffer without limit until the process runs out of memory.
      if (inputBuffer.length > MAX_STDIO_MESSAGE_BYTES) {
        inputBuffer = Buffer.alloc(0); // drop the oversized partial; do not try to parse it
        writeMessage({
          jsonrpc: "2.0",
          id: null,
          error: {
            code: -32700,
            message: `Parse error: message exceeded ${MAX_STDIO_MESSAGE_BYTES} bytes without a newline delimiter`,
          },
        });
      }
      return; // partial message; wait for more input
    }

    // Slice the line off first so a throw inside handleRawMessage cannot re-process it.
    const line = inputBuffer.subarray(0, newlineIndex).toString("utf8");
    inputBuffer = inputBuffer.subarray(newlineIndex + 1);

    // Tolerate CRLF senders and blank keep-alive lines.
    const rawMessage = line.replace(/\r$/, "").trim();
    if (rawMessage.length === 0) continue;

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

  // Track answerable request ids (crash-safety #168) so reportFatal can respond -32603 for an
  // in-flight request instead of leaving the client hanging. The id is removed once the request
  // settles; claimResponse also removes it on the first response so no id is answered twice.
  const trackedId = isPlainObject(message) && hasRequestId(message)
    && isValidJsonRpcId(message.id) && message.id !== null ? message.id : undefined;
  if (trackedId !== undefined) inFlightRequestIds.add(trackedId);

  handleMessage(message)
    .catch(error => {
      if (hasRequestId(message)) {
        writeError(message.id, error.code || -32000, error.message, error.data);
      }
    })
    .finally(() => {
      if (trackedId !== undefined) inFlightRequestIds.delete(trackedId);
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
      protocolVersion: negotiateProtocolVersion(message?.params?.protocolVersion),
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
    // Record EVERY tool call, both outcomes (#477 §11). This is the only choke point every call
    // passes through, and the error path matters as much as the happy one: a refused claim (409)
    // is the collision signal, and a Definition-of-Done refusal (422) is the gate doing its job.
    // Logging a failure as if it never happened would erase exactly the evidence the trial needs.
    const startedAt = Date.now();
    let result;
    try {
      result = await callTool(toolName, args);
    } catch (error) {
      usageLogger.record({ tool: toolName, args, outcome: "error", ms: Date.now() - startedAt, error });
      throw error;
    }
    usageLogger.record({ tool: toolName, args, outcome: "ok", ms: Date.now() - startedAt });
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
      const limit = optionalInt(args, "limit", { min: 1, max: 50 });
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
        // Actor is optional and defaults to "mcp-agent", mirroring the native tool (#168).
        body: { actor: optionalNonEmptyString(args, "actor") ?? "mcp-agent" }
      });
    case "update_task_status":
      assertEnum(args, "status", ["Backlog", "Todo", "InProgress", "Review", "Done"]);
      return restJson(`/api/tasks/${requiredInt(args, "taskId")}/status`, {
        method: "PATCH",
        // overrideReason is forwarded so a wrapper client has the SAME escape hatch as the native
        // tool. Without it an agent on this wrapper hits the Definition-of-Done gate (#428) with
        // no way past it — a 422 it can neither satisfy nor override.
        body: {
          // Trim + treat whitespace-only as absent, matching the native tool's
          // `string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor.Trim()`. Forwarding "  "
          // verbatim would attribute audit rows to a blank actor on this path only.
          actor: optionalString(args, "actor")?.trim() || "mcp-agent",
          status: args.status,
          overrideReason: optionalNonEmptyString(args, "overrideReason")
        }
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
          description: optionalNonEmptyString(args, "description"),
          goals: optionalNonEmptyString(args, "goals"),
          readme: optionalNonEmptyString(args, "readme")
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
          description: optionalNonEmptyString(args, "description"),
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
      if (args.status !== undefined && args.status !== null)
        assertEnum(args, "status", ["Backlog", "Todo", "InProgress", "Review", "Done"]);
      if (args.priority !== undefined && args.priority !== null)
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson(`/api/features/${requiredInt(args, "featureId", { min: 1 })}/tasks`, {
        method: "POST",
        body: {
          name: requiredString(args, "name"),
          description: optionalNonEmptyString(args, "description"),
          status: optionalString(args, "status"),
          priority: optionalString(args, "priority"),
          assignee: optionalNonEmptyString(args, "assignee"),
          sortOrder: optionalFiniteNumber(args, "sortOrder")
        }
      });
    case "update_task":
      if (args.status !== undefined && args.status !== null)
        assertEnum(args, "status", ["Backlog", "Todo", "InProgress", "Review", "Done"]);
      if (args.priority !== undefined && args.priority !== null)
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson(`/api/tasks/${requiredInt(args, "taskId", { min: 1 })}`, {
        method: "PUT",
        body: {
          name: requiredString(args, "name"),
          description: optionalString(args, "description"),
          status: optionalString(args, "status"),
          priority: optionalString(args, "priority"),
          assignee: optionalString(args, "assignee"),
          sortOrder: optionalFiniteNumber(args, "sortOrder")
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
          text: optionalNonEmptyString(args, "text"),
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
          text: optionalNonEmptyString(args, "text"),
          completed: optionalBool(args, "completed"),
          sortOrder: optionalInt(args, "sortOrder", { min: 0 })
        }
      });
    case "mark_acceptance_criterion_done":
      return restJson(`/api/acceptance-criteria/${requiredInt(args, "acId", { min: 1 })}`, {
        method: "PUT",
        body: { completed: optionalBool(args, "completed") ?? true }
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
          body: optionalNonEmptyString(args, "body"),
          plan: optionalNonEmptyString(args, "plan"),
          tags: optionalNonEmptyString(args, "tags"),
          priority: optionalString(args, "priority"),
          actor: optionalNonEmptyString(args, "actor")
        }
      });
    case "list_ideas": {
      if (args.status !== undefined && args.status !== null && args.status !== "")
        assertEnum(args, "status", ["New", "Triaging", "Promoted", "Archived", "Rejected"]);
      const params = new URLSearchParams();
      const status = optionalNonEmptyString(args, "status");
      if (status !== undefined) params.set("status", status);
      const tag = optionalNonEmptyString(args, "tag");
      if (tag !== undefined) params.set("tag", tag);
      const q = optionalNonEmptyString(args, "q");
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
      // `Promoted` is excluded on purpose (#283): it is derived, and the service refuses to set it
      // here, so accepting it would only forward a guaranteed rejection. Fail fast, and say where
      // the status actually comes from. (list_ideas still accepts it as a filter.)
      if (args.status !== undefined && args.status !== null)
        assertEnum(args, "status", ["New", "Triaging", "Archived", "Rejected"],
          "Promoted is a derived status and cannot be set directly — use promote_idea to promote an idea (it also creates the linked project).");
      if (args.priority !== undefined && args.priority !== null)
        assertEnum(args, "priority", ["Low", "Medium", "High", "Critical"]);
      return restJson(`/api/ideas/${requiredInt(args, "ideaId", { min: 1 })}`, {
        method: "PATCH",
        body: {
          title: optionalNonEmptyString(args, "title"),
          body: optionalString(args, "body"),
          plan: optionalString(args, "plan"),
          status: optionalString(args, "status"),
          tags: optionalString(args, "tags"),
          priority: optionalString(args, "priority"),
          actor: optionalNonEmptyString(args, "actor")
        }
      });
    case "promote_idea":
      return restJson(`/api/ideas/${requiredInt(args, "ideaId", { min: 1 })}/promote`, {
        method: "POST",
        body: { actor: optionalNonEmptyString(args, "actor") }
      });

    // ── Knowledge-graph memory tools (#224) — forwarded to the native MCP server ──
    // The graph semantics live in the native tools; the wrapper forwards the call verbatim so
    // stdio clients get exactly the same behavior (single source of truth, no drift).

    case "create_entities":
    case "create_relations":
    case "add_observations":
    case "delete_entities":
    case "delete_observations":
    case "delete_relations":
    case "read_graph":
    case "search_nodes":
    case "open_nodes":
      return nativeMcpToolCall(toolName, args);

    default:
      throw new McpError(-32601, `Unknown tool: ${toolName}`);
  }
}

// ── Native MCP forwarding (#224) ─────────────────────────────────────
// POST a single JSON-RPC tools/call to the app's native Streamable HTTP MCP endpoint (/mcp).
// The native server runs stateless (Program.cs), so no initialize handshake or session id is
// needed. The response may be plain JSON or a text/event-stream frame — both are handled.

let nativeMcpNextId = 1;

async function nativeMcpToolCall(toolName, args) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), restTimeoutMs);
  const requestId = nativeMcpNextId++;

  let response;
  let text;
  try {
    response = await fetch(`${apiBaseUrl}/mcp`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json, text/event-stream",
        "MCP-Protocol-Version": nativeProtocolVersion
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: requestId,
        method: "tools/call",
        params: { name: toolName, arguments: args }
      }),
      signal: controller.signal
    });
    text = await response.text();
  } catch (error) {
    if (error.name === "AbortError") {
      throw new McpError(-32000, `Native MCP request timed out after ${restTimeoutMs}ms.`, { tool: toolName, timeoutMs: restTimeoutMs });
    }
    throw new McpError(-32000, `Native MCP request failed: ${error.message}`, { tool: toolName });
  } finally {
    clearTimeout(timeout);
  }

  if (!response.ok) {
    throw new McpError(-32000, `Native MCP ${response.status} ${response.statusText}: ${text}`, {
      status: response.status,
      statusText: response.statusText,
      tool: toolName
    });
  }

  const contentType = response.headers.get("content-type") || "";
  const message = contentType.includes("text/event-stream")
    ? extractSseJsonRpcMessage(text, requestId)
    : parseJsonOrText(text);

  if (!isPlainObject(message)) {
    throw new McpError(-32000, `Native MCP returned an unparseable response for ${toolName}.`, { body: text });
  }
  if (message.error) {
    throw new McpError(message.error.code ?? -32000, message.error.message, message.error.data);
  }

  const result = message.result ?? {};
  const inner = Array.isArray(result.content)
    ? result.content.find(item => item?.type === "text")?.text
    : undefined;
  if (result.isError) {
    throw new McpError(-32000, inner || `Native MCP tool ${toolName} failed.`, { tool: toolName });
  }
  // Prefer structuredContent when the SDK emits it; otherwise the text content carries the
  // tool's JSON-serialized return value.
  if (result.structuredContent !== undefined && result.structuredContent !== null) {
    return result.structuredContent;
  }
  return inner === undefined ? null : parseJsonOrText(inner);
}

// Pull the JSON-RPC message with the matching id out of an SSE body ("data: {...}" lines,
// events separated by blank lines). Falls back to the last parsed message when ids are absent.
function extractSseJsonRpcMessage(body, requestId) {
  let lastParsed = null;
  for (const rawEvent of body.split(/\r?\n\r?\n/)) {
    const dataLines = rawEvent
      .split(/\r?\n/)
      .filter(line => line.startsWith("data:"))
      .map(line => line.slice(5).trimStart());
    if (dataLines.length === 0) continue;

    try {
      const parsed = JSON.parse(dataLines.join("\n"));
      lastParsed = parsed;
      if (isPlainObject(parsed) && parsed.id === requestId) {
        return parsed;
      }
    } catch {
      // Not JSON (keep scanning) — e.g. a comment/heartbeat frame.
    }
  }
  return lastParsed;
}

// Nullable-text updates have three states: omitted/null preserves, empty/whitespace clears,
// and non-empty text replaces. Preserve the caller's actual string here; the receiving
// REST/service boundary owns normalization and auditing. JSON.stringify must not drop "".
function optionalString(args, name) {
  const value = args[name];
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string") {
    throw new McpError(-32602, `${name} must be a string when provided.`);
  }
  return value;
}

// Compatibility policy for creation, filters, actors and non-clearable optional text.
// Unlike nullable update fields, these call sites historically treat exactly "" as absent.
// Do not trim here: whitespace handling remains with each existing receiving contract.
function optionalNonEmptyString(args, name) {
  const value = optionalString(args, name);
  return value === "" ? undefined : value;
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

// Integer coercion is deliberately strict (#168): only a real number, or a base-10 integer
// STRING, is accepted. Number() alone silently coerces bad input — Number(true)===1,
// Number([5])===5, Number("")===0, Number("  ")===0, Number(" 3 ")===3 — so those types (and
// whitespace-padded / decimal / hex / exponent strings) are rejected BEFORE coercion. Anything
// that isn't an integer after that throws JSON-RPC invalid-params (-32602).
function coerceInt(rawValue, name) {
  let value;
  if (typeof rawValue === "number") {
    value = rawValue;
  } else if (typeof rawValue === "string" && /^[+-]?\d+$/.test(rawValue)) {
    value = Number(rawValue);
  } else {
    throw new McpError(-32602, `${name} must be an integer.`);
  }
  // Number.isSafeInteger (not isInteger): a huge id string like "99999999999999999999" would
  // Number()-round to a nearby float that is still "integer" but a WRONG entity id — reject it.
  // Ids here are 32-bit DB keys, so the safe-integer range is far more than enough (#168 review).
  if (!Number.isSafeInteger(value)) {
    throw new McpError(-32602, `${name} must be a safe integer.`);
  }
  return value;
}

function assertIntRange(value, name, min, max) {
  if (min !== undefined && value < min) {
    throw new McpError(-32602, `${name} must be greater than or equal to ${min}.`);
  }
  if (max !== undefined && value > max) {
    throw new McpError(-32602, `${name} must be less than or equal to ${max}.`);
  }
}

function requiredInt(args, name, { min = undefined, max = undefined } = {}) {
  const value = coerceInt(args[name], name);
  assertIntRange(value, name, min, max);
  return value;
}

function optionalInt(args, name, { min = undefined, max = undefined } = {}) {
  // Only null/undefined mean "not provided". An explicit "" (or any other non-integer value) is a
  // provided-but-invalid value, so it is rejected by coerceInt (-32602) rather than silently
  // falling back to the default — keeping the strict-int guarantee for optional params too (#168 review).
  if (args[name] === undefined || args[name] === null) return undefined;
  const value = coerceInt(args[name], name);
  assertIntRange(value, name, min, max);
  return value;
}

function optionalFiniteNumber(args, name) {
  if (args[name] === undefined || args[name] === null) return undefined;
  const value = args[name];
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new McpError(-32602, `${name} must be a finite number.`);
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

// `hint` is optional: when a value is rejected for a reason the allowed-list alone does not explain
// (e.g. update_idea's `Promoted`, which is derived rather than simply invalid), it appends the
// remedy. Omitting it yields the original message verbatim, so existing callers are unchanged.
function assertEnum(args, name, allowedValues, hint) {
  if (!allowedValues.includes(args[name])) {
    const remedy = hint ? ` ${hint}` : "";
    throw new McpError(-32602, `${name} must be one of: ${allowedValues.join(", ")}.${remedy}`);
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
  if (!claimResponse(id)) return;
  writeMessage({ jsonrpc: "2.0", id, result });
}

function writeError(id, code, message, data = undefined) {
  if (!claimResponse(id)) return;
  const error = data === undefined ? { code, message } : { code, message, data };
  writeMessage({ jsonrpc: "2.0", id, error });
}

// Guarantees at most one response per tracked request id (#168). Protocol-level responses with
// a null id (e.g. parse errors) always pass through. A tracked id is cleared here on first use
// so a fatal-fault -32603 (reportFatal) and the request's own completion can't both frame it.
function claimResponse(id) {
  if (id === null || id === undefined) return true;
  if (!inFlightRequestIds.has(id)) return false;
  inFlightRequestIds.delete(id);
  return true;
}

function writeMessage(message) {
  // MCP stdio framing is newline-delimited JSON (spec 2025-06-18, Transports/stdio:
  // "Messages are delimited by newlines, and MUST NOT contain embedded newlines").
  // JSON.stringify escapes U+000A and U+000D inside string values, so no raw 0x0A/0x0D
  // byte is ever emitted and the delimiter is unambiguous for any "\n"-splitting reader.
  // (U+2028/U+2029/U+0085 ARE emitted raw, but none of them is 0x0A — they only matter to
  // a reader that splits on Unicode line boundaries, e.g. Python's str.splitlines().)
  process.stdout.write(JSON.stringify(message) + "\n");
}
