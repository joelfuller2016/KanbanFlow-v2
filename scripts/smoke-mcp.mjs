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
let tempIdeaId;
let promotedProjectId;
// Knowledge-graph smoke entities (#224) — unique per run; cleaned up via delete_entities.
const kgEntityA = `Smoke Entity A ${runMarker}`;
const kgEntityB = `Smoke Entity B ${runMarker}`;
let kgEntitiesCreated = false;

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
    // Read tools (original 11)
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
    "add_task_comment",
    // Authoring tools (added with /api/tasks/{id}/context)
    "create_project",
    "update_project",
    "create_feature",
    "update_feature",
    "delete_feature",
    "create_task",
    "update_task",
    "delete_task",
    "reassign_task",
    "get_step_context",
    // Subtask CRUD
    "list_subtasks",
    "create_subtask",
    "update_subtask",
    "mark_subtask_done",
    "delete_subtask",
    // Acceptance criterion CRUD
    "list_acceptance_criteria",
    "create_acceptance_criterion",
    "update_acceptance_criterion",
    "mark_acceptance_criterion_done",
    "delete_acceptance_criterion",
    // Dependency CRUD
    "list_dependencies",
    "create_dependency",
    "delete_dependency",
    // Activity
    "list_project_activity",
    "list_task_activity",
    // Idea inbox CRUD
    "create_idea",
    "list_ideas",
    "get_idea",
    "update_idea",
    "promote_idea",
    // Knowledge-graph memory (#224) — the official MCP memory server 9-tool contract
    "create_entities",
    "create_relations",
    "add_observations",
    "delete_entities",
    "delete_observations",
    "delete_relations",
    "read_graph",
    "search_nodes",
    "open_nodes"
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
  if (typeof fetchedTask.featureName !== "string" || !Array.isArray(fetchedTask.subtasks)) {
    throw new Error(`get_task did not return expected inspection fields: ${JSON.stringify(fetchedTask)}`);
  }
  if (!Array.isArray(fetchedTask.comments)) {
    throw new Error("get_task did not include a comments array.");
  }

  // Step context: the single-call AI bundle that closes R-1.6.
  const stepContext = await callTool("get_step_context", { taskId: task.id });
  for (const key of [
    "task",
    "feature",
    "acceptanceCriteria",
    "subtasks",
    "dependencies",
    "openBlockerCount",
    "isBlocked",
    "recentComments",
    "relatedRequirements",
    "recentActivity"
  ]) {
    if (!(key in stepContext)) {
      throw new Error(`get_step_context payload missing '${key}' key: ${JSON.stringify(stepContext)}`);
    }
  }
  if (stepContext.task?.id !== task.id) {
    throw new Error(`get_step_context returned wrong task: ${JSON.stringify(stepContext.task)}`);
  }
  if (stepContext.feature?.projectId !== projectId) {
    throw new Error(`get_step_context returned wrong project: ${JSON.stringify(stepContext.feature)}`);
  }

  // Authoring tools spot-check: create_task -> update_task -> delete_task round-trip.
  const authoredTask = await callTool("create_task", {
    featureId: tempFeatureId,
    name: `MCP authored task ${runMarker}`,
    description: "Created via the new create_task MCP tool.",
    status: "Backlog",
    priority: "Medium"
  });
  if (typeof authoredTask?.id !== "number") {
    throw new Error(`create_task did not return an id: ${JSON.stringify(authoredTask)}`);
  }
  const updatedTask = await callTool("update_task", {
    taskId: authoredTask.id,
    name: `MCP authored task ${runMarker} (updated)`,
    description: "Updated via update_task.",
    status: "Todo",
    priority: "High"
  });
  if (updatedTask?.priority !== "High") {
    throw new Error(`update_task did not return updated priority: ${JSON.stringify(updatedTask)}`);
  }
  await callTool("delete_task", { taskId: authoredTask.id });

  // Idea inbox spot-check: create_idea -> get_idea -> update_idea -> promote_idea round-trip.
  const createdIdea = await callTool("create_idea", {
    title: `MCP smoke idea ${runMarker}`,
    body: "Created via the new create_idea MCP tool.",
    tags: "smoke,mcp",
    priority: "Medium"
  });
  if (typeof createdIdea?.id !== "number") {
    throw new Error(`create_idea did not return an id: ${JSON.stringify(createdIdea)}`);
  }
  tempIdeaId = createdIdea.id;
  const fetchedIdea = await callTool("get_idea", { ideaId: createdIdea.id });
  if (fetchedIdea?.title !== `MCP smoke idea ${runMarker}` || fetchedIdea?.status !== "New") {
    throw new Error(`get_idea returned unexpected idea: ${JSON.stringify(fetchedIdea)}`);
  }
  const updatedIdea = await callTool("update_idea", {
    ideaId: createdIdea.id,
    status: "Triaging",
    priority: "High"
  });
  if (updatedIdea?.status !== "Triaging") {
    throw new Error(`update_idea did not return updated status: ${JSON.stringify(updatedIdea)}`);
  }
  const promotedIdea = await callTool("promote_idea", { ideaId: createdIdea.id, actor: "mcp-smoke" });
  if (typeof promotedIdea?.projectId !== "number" || promotedIdea.projectId <= 0) {
    throw new Error(`promote_idea did not return a project id: ${JSON.stringify(promotedIdea)}`);
  }
  promotedProjectId = promotedIdea.projectId;

  // Subtask CRUD spot-check: create_subtask -> mark_subtask_done -> delete_subtask round-trip.
  const createdSubtask = await callTool("create_subtask", {
    taskId: task.id,
    text: `MCP smoke subtask ${runMarker}`,
    sortOrder: 0
  });
  if (typeof createdSubtask?.id !== "number") {
    throw new Error(`create_subtask did not return an id: ${JSON.stringify(createdSubtask)}`);
  }
  if (createdSubtask.completed !== false) {
    throw new Error(`create_subtask should default completed=false: ${JSON.stringify(createdSubtask)}`);
  }
  const markedSubtask = await callTool("mark_subtask_done", { subtaskId: createdSubtask.id, completed: true });
  if (markedSubtask?.completed !== true) {
    throw new Error(`mark_subtask_done did not return completed=true: ${JSON.stringify(markedSubtask)}`);
  }
  await callTool("delete_subtask", { subtaskId: createdSubtask.id });
  const subtasksAfterDelete = await callTool("list_subtasks", { taskId: task.id });
  if (!Array.isArray(subtasksAfterDelete) || subtasksAfterDelete.some(s => s.id === createdSubtask.id)) {
    throw new Error(`delete_subtask did not remove subtask ${createdSubtask.id}: ${JSON.stringify(subtasksAfterDelete)}`);
  }

  // Acceptance criterion spot-check: create -> update -> delete round-trip.
  const createdAc = await callTool("create_acceptance_criterion", {
    taskId: task.id,
    text: `MCP smoke AC ${runMarker}`,
    sortOrder: 0
  });
  if (typeof createdAc?.id !== "number") {
    throw new Error(`create_acceptance_criterion did not return an id: ${JSON.stringify(createdAc)}`);
  }
  const updatedAc = await callTool("update_acceptance_criterion", {
    acId: createdAc.id,
    text: `MCP smoke AC ${runMarker} (updated)`,
    completed: true,
    sortOrder: 5
  });
  if (updatedAc?.completed !== true || updatedAc?.sortOrder !== 5) {
    throw new Error(`update_acceptance_criterion did not return updated fields: ${JSON.stringify(updatedAc)}`);
  }
  await callTool("delete_acceptance_criterion", { acId: createdAc.id });
  const acAfterDelete = await callTool("list_acceptance_criteria", { taskId: task.id });
  if (!Array.isArray(acAfterDelete) || acAfterDelete.some(a => a.id === createdAc.id)) {
    throw new Error(`delete_acceptance_criterion did not remove criterion ${createdAc.id}: ${JSON.stringify(acAfterDelete)}`);
  }

  // Knowledge-graph memory round-trip (#224): create_entities -> (dupe skip) -> create_relations
  // -> add_observations -> search_nodes -> open_nodes -> read_graph -> delete_* cleanup. These
  // tools forward to the native /mcp endpoint, so this also smokes the wrapper->native proxy.
  const obsToken = `obstoken${runMarker.replace(/[^a-zA-Z0-9]/g, "")}`;
  const createdEntities = await callTool("create_entities", {
    entities: [
      { name: kgEntityA, entityType: "smoke-test", observations: [`created by smoke run ${runMarker}`] },
      { name: kgEntityB, entityType: "smoke-test" }
    ],
    actor: "mcp-smoke"
  });
  kgEntitiesCreated = true;
  if (!Array.isArray(createdEntities) || createdEntities.length !== 2) {
    throw new Error(`create_entities did not return 2 created entities: ${JSON.stringify(createdEntities)}`);
  }
  if (createdEntities[0]?.name !== kgEntityA || createdEntities[0]?.entityType !== "smoke-test") {
    throw new Error(`create_entities returned unexpected entity shape: ${JSON.stringify(createdEntities[0])}`);
  }

  const dupeEntities = await callTool("create_entities", {
    entities: [{ name: kgEntityA, entityType: "smoke-test" }]
  });
  if (!Array.isArray(dupeEntities) || dupeEntities.length !== 0) {
    throw new Error(`create_entities should skip existing names: ${JSON.stringify(dupeEntities)}`);
  }

  const createdRelations = await callTool("create_relations", {
    relations: [{ from: kgEntityA, to: kgEntityB, relationType: "relates_to" }],
    actor: "mcp-smoke"
  });
  if (!Array.isArray(createdRelations) || createdRelations.length !== 1 || createdRelations[0]?.relationType !== "relates_to") {
    throw new Error(`create_relations did not return the created relation: ${JSON.stringify(createdRelations)}`);
  }
  const dupeRelations = await callTool("create_relations", {
    relations: [{ from: kgEntityA, to: kgEntityB, relationType: "relates_to" }]
  });
  if (!Array.isArray(dupeRelations) || dupeRelations.length !== 0) {
    throw new Error(`create_relations should skip exact duplicates: ${JSON.stringify(dupeRelations)}`);
  }

  const additions = await callTool("add_observations", {
    observations: [{ entityName: kgEntityA, contents: [obsToken] }],
    actor: "mcp-smoke"
  });
  if (!Array.isArray(additions) || additions[0]?.entityName !== kgEntityA
    || !Array.isArray(additions[0]?.addedObservations) || !additions[0].addedObservations.includes(obsToken)) {
    throw new Error(`add_observations did not report the added observation: ${JSON.stringify(additions)}`);
  }

  const searchResult = await callTool("search_nodes", { query: obsToken, k: 10 });
  if (!Array.isArray(searchResult?.entities) || !searchResult.entities.some(e => e?.name === kgEntityA)) {
    throw new Error(`search_nodes did not surface the observed entity: ${JSON.stringify(searchResult)}`);
  }

  const opened = await callTool("open_nodes", { names: [kgEntityA, kgEntityB, `missing ${runMarker}`] });
  if (!Array.isArray(opened?.entities) || opened.entities.length !== 2) {
    throw new Error(`open_nodes should return the 2 existing entities and skip the missing name: ${JSON.stringify(opened)}`);
  }
  if (!Array.isArray(opened?.relations)
    || !opened.relations.some(r => r?.from === kgEntityA && r?.to === kgEntityB && r?.relationType === "relates_to")) {
    throw new Error(`open_nodes did not include the relation between opened entities: ${JSON.stringify(opened)}`);
  }

  const wholeGraph = await callTool("read_graph", {});
  if (!Array.isArray(wholeGraph?.entities) || !wholeGraph.entities.some(e => e?.name === kgEntityA)) {
    throw new Error(`read_graph did not include the smoke entity: ${JSON.stringify(wholeGraph?.entities?.length)}`);
  }

  const observationDeletion = await callTool("delete_observations", {
    deletions: [{ entityName: kgEntityA, observations: [obsToken] }]
  });
  if (observationDeletion?.deletedCount !== 1) {
    throw new Error(`delete_observations should report 1 deletion: ${JSON.stringify(observationDeletion)}`);
  }
  const relationDeletion = await callTool("delete_relations", {
    relations: [{ from: kgEntityA, to: kgEntityB, relationType: "relates_to" }]
  });
  if (relationDeletion?.deletedCount !== 1) {
    throw new Error(`delete_relations should report 1 deletion: ${JSON.stringify(relationDeletion)}`);
  }

  const entityDeletion = await callTool("delete_entities", {
    entityNames: [kgEntityA, kgEntityB, `missing ${runMarker}`]
  });
  if (!Array.isArray(entityDeletion?.deleted) || entityDeletion.deleted.length !== 2) {
    throw new Error(`delete_entities should delete both smoke entities and skip the missing name: ${JSON.stringify(entityDeletion)}`);
  }
  kgEntitiesCreated = false;
  const afterDelete = await callTool("open_nodes", { names: [kgEntityA, kgEntityB] });
  if (!Array.isArray(afterDelete?.entities) || afterDelete.entities.length !== 0) {
    throw new Error(`Deleted knowledge-graph entities still open: ${JSON.stringify(afterDelete)}`);
  }

  await callTool("get_next_tasks", { projectId, limit: 3 });
  await callTool("claim_task", { taskId: task.id, actor: "mcp-smoke" });
  const commentText = `MCP smoke test comment ${runMarker}`;
  await callTool("add_task_comment", { taskId: task.id, text: commentText, author: "mcp-smoke", type: "Handoff" });
  const fetchedTaskWithComment = await callTool("get_task", { taskId: task.id });
  if (!Array.isArray(fetchedTaskWithComment.comments) || !fetchedTaskWithComment.comments.some(comment => comment?.text === commentText && comment?.type === "Handoff")) {
    throw new Error(`get_task did not return the newly added comment: ${JSON.stringify(fetchedTaskWithComment)}`);
  }
  await callTool("update_task_status", { taskId: task.id, status: "Review" });
  await callTool("release_task", { taskId: task.id, actor: "mcp-smoke" });

  // Activity spot-check (#168): mirror scripts/smoke-api.ps1 — the workflow above (claim,
  // comment, status change) must surface as real events, not merely "an array". Assert a
  // non-empty feed AND the three expected eventTypes so a silently-empty result fails loudly.
  const taskActivity = await callTool("list_task_activity", { taskId: task.id, limit: 50 });
  if (!Array.isArray(taskActivity) || taskActivity.length === 0) {
    throw new Error(`list_task_activity did not return a non-empty array: ${JSON.stringify(taskActivity)}`);
  }
  const activityEventTypes = new Set(taskActivity.map(event => event?.eventType));
  for (const expectedEventType of ["TaskClaimed", "TaskCommentAdded", "TaskStatusChanged"]) {
    if (!activityEventTypes.has(expectedEventType)) {
      throw new Error(`list_task_activity is missing expected event ${expectedEventType}: got ${JSON.stringify([...activityEventTypes])}`);
    }
  }

  console.log(`MCP smoke tests passed for project ${projectId}, task ${task.id}.`);
} finally {
  try {
    // Knowledge-graph smoke entities: delete_entities is a silent no-op on missing names, so
    // this is safe to run even when the happy path already deleted them.
    if (kgEntitiesCreated) {
      try {
        await callTool("delete_entities", { entityNames: [kgEntityA, kgEntityB] });
        console.log("Cleaned up MCP smoke knowledge-graph entities.");
      } catch (error) {
        console.error(`MCP smoke cleanup failed for knowledge-graph entities: ${error.message}`);
        process.exitCode = 1;
      }
    }
    // Delete the idea before the promoted project so cleanup is deterministic
    // under restrictive FK behavior (the promoted project references the idea).
    if (tempIdeaId !== undefined) {
      try {
        await restJson(`/api/ideas/${tempIdeaId}`, { method: "DELETE", expectNoContent: true });
        console.log(`Cleaned up MCP smoke idea ${tempIdeaId}.`);
      } catch (error) {
        console.error(`MCP smoke cleanup failed for idea ${tempIdeaId}: ${error.message}`);
        process.exitCode = 1;
      }
    }
    if (promotedProjectId !== undefined) {
      try {
        await restJson(`/api/projects/${promotedProjectId}`, { method: "DELETE", expectNoContent: true });
        console.log(`Cleaned up MCP smoke promoted project ${promotedProjectId}.`);
      } catch (error) {
        console.error(`MCP smoke cleanup failed for promoted project ${promotedProjectId}: ${error.message}`);
        process.exitCode = 1;
      }
    }
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
  // Newline-delimited JSON, per the MCP stdio spec (2025-06-18, Transports/stdio).
  // NOTE: this mirrors the server's framing, so it can NOT by itself prove conformance —
  // a matching pair of wrong implementations is exactly how the old Content-Length framing
  // survived. `scripts/framing-ab.mjs` is the INDEPENDENT wire-format gate; this suite
  // tests behaviour on top of a working transport.
  const framed = `${body}\n`;
  if (options.chunked) {
    // Convert to a Buffer first so splitAt is a byte index, not a character index.
    // Splitting a JS string between surrogate pairs corrupts the UTF-8 bytes written
    // by the two separate stdin.write() calls.
    const buf = Buffer.from(framed, "utf8");
    const splitAt = Math.max(1, Math.floor(buf.length / 2));
    server.stdin.write(buf.subarray(0, splitAt));
    setTimeout(() => server.stdin.write(buf.subarray(splitAt)), 10);
    return;
  }

  server.stdin.write(framed);
}

function readMessages() {
  while (buffer.length > 0) {
    const newlineIndex = buffer.indexOf("\n");
    if (newlineIndex === -1) return; // partial message; wait for more input

    const line = buffer.subarray(0, newlineIndex).toString("utf8");
    buffer = buffer.subarray(newlineIndex + 1);

    const rawMessage = line.replace(/\r$/, "").trim();
    if (rawMessage.length === 0) continue;

    // The old Content-Length reader SKIPPED anything that was not a frame. Per-line dispatch
    // has no such filter, so a single stray non-JSON line on the server's stdout would throw
    // out of this stdout handler as an unhandled exception, killing the run with a stack trace
    // instead of a readable assertion failure. Surface it through the same path the server-exit
    // handler uses (reject everything pending) so the suite fails cleanly and says why.
    let message;
    try {
      message = JSON.parse(rawMessage);
    } catch {
      const err = new Error(
        `Non-JSON line on MCP server stdout (violates the stdio contract): ${rawMessage.slice(0, 200)}`
      );
      process.exitCode = 1;
      for (const { reject } of pending.values()) reject(err);
      pending.clear();
      continue;
    }
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
