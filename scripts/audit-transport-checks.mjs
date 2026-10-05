import assert from 'node:assert/strict';

// Called by smoke-workflow against its disposable local app. Keeps its workflow fixture intact.
export async function verifyAuditTransports(base, call, native, stdio) {
  const url = new URL(base);
  assert.ok(['127.0.0.1', 'localhost'].includes(url.hostname) && url.port !== '5123');
  async function rest(path, method = 'GET', body) {
    const response = await fetch(base + path, { method, headers: {
      'Content-Type': 'application/json', 'X-KanbanFlow-Actor': 'audit-rest'
    }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000) });
    assert.ok(response.ok, `${method} ${path}: ${response.status} ${await response.clone().text()}`);
    return response.status === 204 ? null : response.json();
  }
  const project = await rest('/api/projects', 'POST', { name: 'Audit transport fixture' });
  const feature = await rest(`/api/projects/${project.id}/features`, 'POST', { name: 'Before feature' });
  await rest(`/api/features/${feature.id}`, 'PUT', { name: 'After feature' });
  const task = await call(stdio, 'create_task', { featureId: feature.id, name: 'Audit parent task', actor: 'audit-wrapper' });
  const blocker = await call(native, 'create_task', { featureId: feature.id, name: 'Audit blocking task', actor: 'audit-native' });
  const subtask = await rest(`/api/tasks/${task.id}/subtasks`, 'POST', { text: 'Audit subtask' });
  const child = await rest(`/api/tasks/${task.id}/subtasks`, 'POST', { text: 'Audit nested child', parentSubtaskId: subtask.id });
  await rest(`/api/subtasks/${child.id}`, 'PUT', { completed: true });
  const criterion = await call(native, 'create_acceptance_criterion', { taskId: task.id, text: 'Audit criterion' });
  await call(native, 'mark_acceptance_criterion_done', { acId: criterion.id });
  const requirement = await rest(`/api/projects/${project.id}/requirements`, 'POST', { text: 'Audit requirement', taskItemId: task.id });
  const dependency = await rest(`/api/tasks/${task.id}/dependencies`, 'POST', { dependsOnTaskItemId: blocker.id });
  const readAll = async (transport, name, args) => {
    return readActivityPages(beforeId => call(transport, name, {
      ...args, limit: 2, ...(transport === stdio ? { includePage: true } : {}), ...(beforeId ? { beforeId } : {})
    }));
  };
  const nativeEvents = await readAll(native, 'list_task_activity', { taskId: task.id });
  const wrapperEvents = await readAll(stdio, 'list_task_activity', { taskId: task.id });
  assert.deepEqual(wrapperEvents.map(e => e.id), nativeEvents.map(e => e.id));
  const complete = nativeEvents.find(e => e.eventType === 'AcceptanceCriterionUpdated');
  assert.equal(JSON.parse(complete.beforeJson).completed, false);
  assert.equal(JSON.parse(complete.afterJson).completed, true); assert.ok(complete.actor);
  assert.ok(nativeEvents.some(e => e.entityType === 'Subtask' && e.entityId === child.id && e.actor === 'audit-rest'));
  const blockerEvents = await readAll(native, 'list_task_activity', { taskId: blocker.id });
  assert.ok(blockerEvents.some(e => e.entityType === 'TaskDependency' && e.entityId === dependency.id));
  await assert.rejects(call(native, 'list_task_activity', { taskId: blocker.id, beforeId: complete.id }), /cursor/i);
  await rest(`/api/tasks/${task.id}`, 'DELETE');
  const deleted = await readAll(stdio, 'list_task_activity', { taskId: task.id });
  for (const [type, id] of [['Task', task.id], ['Subtask', subtask.id], ['Subtask', child.id],
    ['AcceptanceCriterion', criterion.id], ['TaskDependency', dependency.id]]) {
    const event = deleted.find(e => e.entityType === type && e.entityId === id && e.eventType === type + 'Deleted');
    assert.ok(event?.beforeJson && !event.afterJson, `Missing retained deletion: ${type} ${id}`);
  }
  const unlink = deleted.find(e => e.entityType === 'Requirement' && e.entityId === requirement.id && e.eventType === 'RequirementUpdated');
  assert.equal(JSON.parse(unlink.beforeJson).taskItemId, task.id);
  assert.equal(JSON.parse(unlink.afterJson).taskItemId, null);
  await rest(`/api/projects/${project.id}`, 'DELETE');
  const history = await readAll(native, 'list_project_activity', { projectId: project.id });
  for (const name of ['ProjectCreated', 'ProjectDeleted', 'FeatureCreated', 'FeatureUpdated', 'FeatureDeleted', 'RequirementDeleted'])
    assert.ok(history.some(e => e.eventType === name), `Missing ${name}`);
  assert.ok(history.every(e => e.projectId === project.id));
  const last = await call(native, 'list_project_activity', { projectId: project.id, beforeId: history.at(-1).id });
  assert.equal(last.items.length, 0); assert.equal(last.total, history.length);
  return { projectId: project.id, retainedEvents: history.length, nativeAndStdioPaging: true, cascadingDeletes: true };
}

// Cursors identify time-ordered rows; IDs need not decrease after historical imports.
export async function readActivityPages(readPage, maxPages = 10000) {
  const events = [], cursors = new Set(), ids = new Set();
  let beforeId, total;
  for (let pageNumber = 0; pageNumber < maxPages; pageNumber++) {
    const page = await readPage(beforeId);
    assert.ok(Number.isInteger(page.total) && page.total >= 0, 'Invalid activity total');
    total ??= page.total;
    assert.equal(page.total, total, 'Activity changed during paging verification');
    for (const event of page.items) {
      assert.ok(!ids.has(event.id), 'Paging duplicated an event');
      ids.add(event.id); events.push(event);
    }
    const next = page.nextBeforeId;
    if (next == null) {
      assert.equal(events.length, total, 'Paging omitted events');
      return events;
    }
    assert.ok(page.items.length > 0, 'Activity cursor continued without any events');
    assert.ok(Number.isInteger(next) && next > 0 && !cursors.has(next), 'Activity cursor did not advance');
    cursors.add(next); beforeId = next;
  }
  throw new Error(`Activity paging exceeded ${maxPages} pages`);
}
