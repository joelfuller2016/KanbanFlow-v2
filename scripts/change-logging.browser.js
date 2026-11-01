// Playwright CLI. Open a fresh smoke-workflow board on a disposable local database first.
async page => {
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const entry = await page.evaluate(() => ({ href: location.href, origin: location.origin,
    hostname: location.hostname, port: location.port, requestId: new URLSearchParams(location.search).get('request') }));
  assert(['127.0.0.1', 'localhost'].includes(entry.hostname) && entry.port !== '5123' && entry.requestId,
    'Use a disposable transport fixture, not the normal board.');
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
  const context = await mcp('get_request_context', { requestId: entry.requestId });
  assert(context.title === 'Shared UI and MCP workflow' && context.tasks.length === 1 &&
    context.messages.find(m => m.kind === 'original')?.source === 'transport-smoke', 'Unexpected fixture.');
  const taskId = context.tasks[0].taskId;
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(entry.href + '&task=' + taskId);
  await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
  const panel = page.getByTestId('task-detail-panel');
  await panel.waitFor({ state: 'visible' });
  await page.getByTestId('task-acceptance-criterion-input').fill('Browser audit criterion');
  await page.getByTestId('task-acceptance-criterion-add-button').click();
  const created = panel.locator('[data-kind="AcceptanceCriterionCreated"]').filter({ hasText: 'Browser audit criterion' });
  await created.waitFor();
  const criteria = await mcp('list_acceptance_criteria', { taskId });
  const criterion = criteria.find(c => c.text === 'Browser audit criterion');
  assert(criterion, 'UI save missing from native MCP.');
  await page.getByTestId('task-acceptance-criterion-toggle-' + criterion.id).check();
  const updated = panel.locator('[data-kind="AcceptanceCriterionUpdated"]').first();
  await updated.waitFor();
  assert(/false[\s\S]*true/.test(await updated.innerText()), 'Completion history lacks actual before/after.');
  assert((await updated.innerText()).includes('User'), 'UI actor missing.');
  await page.getByRole('textbox', { name: 'New subtask text', exact: true }).fill('Browser audited subtask');
  await page.getByTestId('task-detail-add-subtask-button').click();
  await panel.locator('[data-kind="SubtaskCreated"]').filter({ hasText: 'Browser audited subtask' }).waitFor();
  await callAndReopen();
  async function callAndReopen() {
    await mcp('mark_acceptance_criterion_done', { acId: criterion.id, completed: false });
    await page.getByTestId('task-detail-close-button').click();
    await page.getByTestId('board-card-' + taskId + '-title').click();
    const reopened = panel.locator('[data-kind="AcceptanceCriterionUpdated"]').first();
    await page.waitForFunction(() => {
      const el = document.querySelector('[data-testid="task-detail-panel"] [data-kind="AcceptanceCriterionUpdated"]');
      return el && /true[\s\S]*false/.test(el.innerText);
    });
    assert((await reopened.innerText()).includes('mcp-agent'), 'MCP attribution not visible on the same card.');
  }
  await panel.screenshot({ path: 'change-history-task.png' });
  await page.getByTestId('task-detail-close-button').click();
  const modal = page.getByTestId('edit-item-modal');
  for (const [kind, opener] of [['Project', page.getByTestId('edit-project-button')],
    ['Feature', page.getByTestId('outline-feature-' + context.featureId)]]) {
    await opener.click(); await modal.waitFor({ state: 'visible' });
    const oldName = await modal.locator('#edit-item-name').inputValue();
    const newName = 'Browser audited ' + kind.toLowerCase();
    await modal.locator('#edit-item-name').fill(newName);
    await page.getByTestId('edit-item-save-button').click(); await modal.waitFor({ state: 'hidden' });
    await opener.click(); await modal.waitFor({ state: 'visible' });
    const history = page.getByTestId('edit-item-history'); await history.locator(':scope > summary').click();
    const saved = history.locator('[data-kind="' + kind + 'Updated"]').first(); await saved.waitFor();
    await saved.getByTestId('activity-saved-details').locator('summary').click();
    const text = await saved.innerText(); assert(text.includes(oldName) && text.includes(newName) && text.includes('User'), kind + ' history is incomplete.');
    await page.screenshot({ path: 'change-history-' + kind.toLowerCase() + '.png' });
    await page.getByTestId('edit-item-close-button').click();
  }
  const retained = await mcp('list_task_activity', { taskId, limit: 500 });
  assert(retained.items.some(e => e.entityType === 'Subtask') && retained.items.some(e => e.entityId === criterion.id && e.entityType === 'AcceptanceCriterion'),
    'Saved UI changes absent from MCP history.');
  assert(await page.getByTestId('activity-timeline-degraded').count() === 0, 'History degraded.');
  console.log(JSON.stringify({ passed: true, taskId, uiSaves: true, nativeMcpReadWrite: true, immediateHistory: true, projectAndFeatureHistory: true }));
}
