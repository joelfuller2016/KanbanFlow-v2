import assert from 'node:assert/strict';

// Runs inside smoke-workflow's real native/stdio clients on the disposable database.
export async function verifyFullContentTransports(base, call, native, stdio) {
  const url = new URL(base);
  assert.ok(['127.0.0.1', 'localhost'].includes(url.hostname) && url.port !== '5123');
  const project = await call(stdio, 'create_project', { name: 'Full content transport fixture', actor: 'full-content-stdio' });
  const feature = await call(stdio, 'create_feature', { projectId: project.id, name: 'Full sources', actor: 'full-content-stdio' });
  const description = '  \r\n# Exact source\r\n```text\r\n' + '漢字🙂 \\ " full content\r\n'.repeat(6000) + '```\r\nFINAL SOURCE CHECK  \r\n\t';
  const task = await call(stdio, 'create_task', { featureId: feature.id, name: 'Complete imported content', description, actor: 'full-content-stdio' });
  assert.equal(task.description, description);
  assert.equal((await call(native, 'get_task', { taskId: task.id })).description, description);
  const changed = description + '\r\nSECOND SOURCE END  \r\n';
  await call(native, 'update_task', { taskId: task.id, update: { description: changed }, actor: 'full-content-native' });
  assert.equal((await call(stdio, 'get_task', { taskId: task.id })).description, changed);
  const comment = '  Full comment\r\n' + 'comment café 漢🙂\r\n'.repeat(7000) + 'FINAL COMMENT CHECK  \r\n\t';
  await call(stdio, 'add_task_comment', { taskId: task.id, text: comment, author: 'full-content-stdio' });
  for (const transport of [native, stdio]) {
    const result = await call(transport, 'get_task', { taskId: task.id });
    assert.equal(result.description, changed);
    assert.ok(result.comments.some(c => c.text === comment));
    const history = await call(transport, 'list_task_activity', { taskId: task.id, limit: 500, ...(transport === stdio ? { includePage: true } : {}) });
    const event = history.items.find(e => e.eventType === 'TaskUpdated');
    assert.equal(JSON.parse(event.beforeJson).description, description);
    assert.equal(JSON.parse(event.afterJson).description, changed);
    const note = history.items.find(e => e.eventType === 'TaskCommentAdded');
    assert.equal(JSON.parse(note.afterJson).text, comment);
  }
  const response = await fetch(`${base}/api/tasks/${task.id}`, { signal: AbortSignal.timeout(20000) });
  assert.equal(response.status, 200);
  assert.equal((await response.json()).description, changed);
  return { taskId: task.id, descriptionCharacters: changed.length, commentCharacters: comment.length,
    nativeAndStdioExact: true, restExact: true, completeAuditValues: true };
}
