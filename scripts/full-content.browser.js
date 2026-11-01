// Playwright CLI. Open a smoke-workflow request on a disposable local board first.
async page => {
  page.setDefaultTimeout(15000);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const entry = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname,
    port: location.port, requestId: new URLSearchParams(location.search).get('request') }));
  assert(['127.0.0.1', 'localhost'].includes(entry.hostname) && entry.port !== '5123' && entry.requestId,
    'Use a disposable smoke-workflow board, never the normal board.');
  let rpcId = 0;
  async function mcp(name, args) {
    const response = await page.request.post(entry.origin + '/mcp', {
      headers: { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
      data: { jsonrpc: '2.0', id: ++rpcId, method: 'tools/call', params: { name, arguments: args } }
    });
    const body = await response.text(); const line = body.split(/\r?\n/).find(x => x.startsWith('data: '));
    const payload = JSON.parse(line ? line.slice(6) : body);
    if (!response.ok() || payload.error || payload.result?.isError) throw new Error(body);
    return payload.result.structuredContent ?? JSON.parse(payload.result.content.find(x => x.type === 'text').text);
  }
  async function rest(path) {
    const response = await page.request.get(entry.origin + path);
    assert(response.ok(), 'REST read failed: ' + path);
    return response.json();
  }
  const context = await mcp('get_request_context', { requestId: entry.requestId });
  assert(context.title === 'Shared UI and MCP workflow' &&
    context.messages.find(m => m.kind === 'original')?.source === 'transport-smoke', 'Unexpected fixture.');
  const prior = '    Prior code indentation\nA hard break  \n';
  const task = await mcp('create_task', { featureId: context.featureId, name: 'Full content browser fixture',
    description: prior, actor: 'full-content-browser' });
  const baseline = await rest(`/api/tasks/${task.id}`);
  const description = '    Keep this indentation\n\n# Complete description\n\n```json\n' +
    '{"path":"C:\\\\work", "text":"漢字🙂 café"}\n'.repeat(3200) +
    '```\n\nFINAL DESCRIPTION REQUIREMENT  \n\n\t';
  const comment = '  Complete comment\n\n```text\n' + 'keep 漢字🙂 \\ " all text\n'.repeat(3000) +
    '```\n\nFINAL COMMENT REQUIREMENT  \n\t';
  assert(description.length > 32768 && comment.length > 32768, 'Fixtures must exceed the old transport limit.');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`${entry.origin}/project/${context.projectId}?request=${entry.requestId}&task=${task.id}`);
  await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
  const panel = page.getByTestId('task-detail-panel');
  await panel.waitFor({ state: 'visible' });
  await page.getByTestId('task-detail-description-edit-control').click();
  const editor = page.getByTestId('task-detail-description-edit');
  assert(await editor.inputValue() === prior, 'Initial MCP text changed before editing.');
  await editor.focus();
  await editor.press('ControlOrMeta+A');
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.evaluate(value => navigator.clipboard.writeText(value), description);
  await editor.press('ControlOrMeta+V');
  // Real oninput and blur traverse the Blazor SignalR circuit; REST seeding cannot prove this.
  await page.getByTestId('task-detail-full-page-button').click();
  await page.getByTestId('task-detail-description-read').waitFor();
  const saved = await rest(`/api/tasks/${task.id}`);
  assert(saved.description === description, 'The browser description save lost content or Markdown whitespace.');
  assert((await mcp('get_task', { taskId: task.id })).description === description, 'Native MCP did not read the exact browser save.');
  assert(await panel.getAttribute('data-size') === 'full', 'Circuit stopped responding after the large save.');
  await page.getByTestId('task-detail-half-page-button').click();
  const commentEditor = page.getByTestId('task-comment-input');
  await commentEditor.focus();
  await page.evaluate(value => navigator.clipboard.writeText(value), comment);
  await commentEditor.press('ControlOrMeta+V');
  await page.getByTestId('task-comment-add-button').click();
  await page.waitForFunction(() => document.querySelector('[data-testid="task-comment-input"]')?.value === '');
  const comments = await rest(`/api/tasks/${task.id}/comments`);
  assert(comments.some(c => c.text === comment && c.author === 'User'), 'The browser comment save lost content.');
  const activity = await mcp('list_task_activity', { taskId: task.id, limit: 500 });
  const changed = activity.items.find(e => e.eventType === 'TaskUpdated' && e.actor === 'User');
  assert(changed && JSON.parse(changed.beforeJson).description === prior &&
    JSON.parse(changed.afterJson).description === description, 'History lacks exact complete before/after descriptions.');
  const added = activity.items.find(e => e.eventType === 'TaskCommentAdded' && e.actor === 'User');
  assert(added && JSON.parse(added.afterJson).text === comment, 'History lost part of the comment.');
  for (const field of ['id', 'name', 'status', 'priority', 'assignee', 'featureId', 'sortOrder'])
    assert(saved[field] === baseline[field], 'Description editing unexpectedly changed ' + field);
  await page.getByTestId('task-detail-close-button').click();
  await page.getByTestId(`board-card-${task.id}-title`).click();
  await page.getByTestId('task-detail-description-edit-control').click();
  assert(await editor.inputValue() === description, 'Reopened editor is missing saved content.');
  await editor.press('Escape');
  const tail = page.getByTestId('task-detail-description-read').getByText('FINAL DESCRIPTION REQUIREMENT', { exact: true });
  await tail.scrollIntoViewIfNeeded();
  assert(await tail.isVisible(), 'A human cannot reach the final description requirement.');
  await page.screenshot({ path: 'full-content-description-tail.png' });
  assert(await page.locator('#blazor-error-ui').isVisible() === false, 'Blazor reported an error.');
  return { passed: true, taskId: task.id, descriptionCharacters: description.length,
    commentCharacters: comment.length, exactWhitespace: true, realSignalRInput: true,
    restAndNativeReadBack: true, fullAuditValues: true, reopenAndTailVisible: true };
}
