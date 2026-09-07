#!/usr/bin/env node
// Creates a labeled test project. Run against a disposable board/database; see docs/request-workflow.md.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { workflowTools } from './workflow-tools.mjs';
import { verifyAuditTransports } from './audit-transport-checks.mjs';

const base = process.argv[2];
if (!base) throw new Error('Usage: node scripts/smoke-workflow.mjs http://127.0.0.1:5126 (disposable board)');
const wrapper = spawn(process.execPath, [fileURLToPath(new URL('./mcp-server.mjs', import.meta.url))],
  { env: { ...process.env, KANBANFLOW_API_URL: base, KANBANFLOW_USAGE_LOG: 'off' }, stdio: ['pipe', 'pipe', 'inherit'] });
let nextId = 0;
const pending = new Map();
const lines = createInterface({ input: wrapper.stdout });
lines.on('line', line => {
  const message = JSON.parse(line); const waiting = pending.get(message.id);
  if (!waiting) return;
  pending.delete(message.id); clearTimeout(waiting.timeout);
  if (message.error) waiting.reject(new Error(message.error.message)); else waiting.resolve(message.result);
});
function stdio(method, params) {
  const id = ++nextId;
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 20000);
    pending.set(id, { resolve, reject, timeout });
    wrapper.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
async function native(method, params) {
  const response = await fetch(`${base}/mcp`, { method: 'POST', headers: { 'Content-Type': 'application/json',
    Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
    body: JSON.stringify({ jsonrpc: '2.0', id: ++nextId, method, params }), signal: AbortSignal.timeout(20000) });
  assert.equal(response.status, 200);
  const body = await response.text();
  const payload = response.headers.get('content-type').includes('text/event-stream')
    ? JSON.parse(body.split(/\r?\n/).find(line => line.startsWith('data: ')).slice(6)) : JSON.parse(body);
  if (payload.error) throw new Error(payload.error.message);
  return payload.result;
}
function unpack(result) {
  if (result.isError) throw new Error(result.content?.map(c => c.text).join('\n'));
  return result.structuredContent ?? JSON.parse(result.content.find(c => c.type === 'text').text);
}
const call = async (transport, name, args) => unpack(await transport('tools/call', { name, arguments: args }));
const actor = 'workflow-smoke';
const command = (transport, input) => call(transport, 'workflow_command', { input: { actor, key: randomUUID(), ...input } });
try {
  await stdio('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: actor, version: '1' } });
  wrapper.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const nativeList = await native('tools/list', {}); const wrapperList = await stdio('tools/list', {});
  for (const schema of workflowTools) {
    assert.deepEqual(nativeList.tools.find(t => t.name === schema.name)?.inputSchema, schema.inputSchema);
    assert.deepEqual(wrapperList.tools.find(t => t.name === schema.name)?.inputSchema, schema.inputSchema);
  }
  const project = await command(native, { operation: 'create_project', name: `Workflow verification ${new Date().toISOString()}` });
  const original = '  Preserve this actual request.\r\nBuild it on the existing board — café.  ';
  const input = { operation: 'capture', actor, key: randomUUID(), capture: { title: 'Shared UI and MCP workflow', original, projectId: project.resourceId, source: 'transport-smoke' } };
  let receipt = await command(native, input);
  assert.deepEqual(await command(stdio, input), receipt, 'Retry across transports must not create another idea/request');
  const id = receipt.requestId;
  let context = await call(stdio, 'get_request_context', { requestId: id });
  assert.equal(context.messages.find(m => m.kind === 'original').text, original);
  receipt = await command(stdio, { operation: 'save_brief', requestId: id, expectedVersion: context.version,
    brief: { outcome: 'One shared board card', scope: 'UI and native/stdio MCP', constraints: 'Preserve existing task services', acceptance: 'Human and AI read/write the same task' } });
  await assert.rejects(command(native, { operation: 'add_message', requestId: id, expectedVersion: context.version, kind: 'progress', text: 'stale write' }), /conflict/i);
  const draft = { id: randomUUID(), title: 'Verify shared task identity', description: 'Read this task from both MCP paths and the existing board.',
    acceptance: 'The existing board shows the task\nBoth MCP clients return the same task id', repositoryId: null, fileReferences: '', priority: 'Medium', assignee: 'Human', dependencies: [] };
  receipt = await command(native, { operation: 'save_plan', requestId: id, expectedVersion: receipt.version, tasks: [draft] });
  context = await call(native, 'get_request_context', { requestId: id });
  receipt = await command(stdio, { operation: 'publish_plan', requestId: id, expectedVersion: receipt.version, planId: context.plans.at(-1).id });
  context = await call(native, 'get_task_workflow', { taskId: (await call(stdio, 'get_request_context', { requestId: id })).tasks[0].taskId });
  const task = context.tasks[0];
  assert.equal(context.id, id); assert.equal(task.status, 'Todo'); assert.ok(task.comments.some(c => c.type === 'Created'));
  const evidence = { summary: 'Transport checks passed', checks: 'Native and stdio returned identical task identity; original whitespace preserved.', codeReference: 'scripts/smoke-workflow.mjs' };
  receipt = await command(native, { operation: 'report_result', requestId: id, expectedVersion: receipt.version, itemId: task.id, evidence });
  context = await call(stdio, 'get_request_context', { requestId: id });
  assert.equal(context.tasks[0].status, 'Review'); assert.equal(context.tasks[0].evidence[0].current, true);
  const taskBaselines = Object.fromEntries(context.tasks.map(t => [t.taskId, t.editToken]));
  await call(native, 'update_task', { taskId: task.taskId, update: { description: 'Updated through native MCP; review this live card in the browser.' }, actor });
  await assert.rejects(command(stdio, { operation: 'save_plan', requestId: id, expectedVersion: context.version,
    tasks: [{...draft, priority:'High'}], taskBaselines }), /conflict/i, 'A board edit after reading the plan must not be overwritten');
  context = await call(stdio, 'get_request_context', { requestId: id });
  assert.equal(context.tasks[0].evidence[0].current, false);
  await assert.rejects(command(native, { operation: 'accept_result', requestId: id, expectedVersion: context.version, itemId: task.id,
    evidenceId: context.tasks[0].evidence[0].id, checkedCriterionIds: task.acceptanceCriteria.map(c => c.id) }), /older specification/i);
  const normalTask = await call(native, 'get_task', { taskId: task.taskId });
  assert.ok(normalTask.comments.some(c => c.type === 'Verification'));
  const rest = await fetch(`${base}/api/workflows/${id}`, { signal: AbortSignal.timeout(20000) }); assert.equal(rest.status, 200);
  assert.equal((await rest.json()).tasks[0].taskId, task.taskId);
  const audit = await verifyAuditTransports(base, call, native, stdio);
  console.log(JSON.stringify({ passed: true, audit, nativeTools: nativeList.tools.length, wrapperTools: wrapperList.tools.length,
    requestId: id, ideaId: context.ideaId, projectId: context.projectId, taskId: task.taskId,
    boardUrl: `${base}${context.boardUrl}`, taskUrl: `${base}${task.url}` }, null, 2));
} finally {
  for (const waiting of pending.values()) clearTimeout(waiting.timeout);
  lines.close(); wrapper.stdin.end(); wrapper.kill();
}
