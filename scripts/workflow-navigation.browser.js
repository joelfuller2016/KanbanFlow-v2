// Playwright CLI run-code file. Open a disposable smoke-workflow.mjs board URL first.
// Exercises real links and debounced filters in the existing UI; does not approve usability.
async page => {
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const entry = await page.evaluate(() => ({ href: location.href, origin: location.origin,
    hostname: location.hostname, port: location.port, requestId: new URLSearchParams(location.search).get('request') }));
  assert(['127.0.0.1', 'localhost'].includes(entry.hostname) && entry.port !== '5123',
    'Use the disposable verification app, not the normal board on port 5123.');
  assert(entry.requestId, 'Open the board URL printed by smoke-workflow.mjs.');
  let rpcId = 0;
  async function mcp(name, args) {
    const response = await page.request.post(entry.origin + '/mcp', {
      timeout: 20000,
      headers: { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
      data: { jsonrpc: '2.0', id: ++rpcId, method: 'tools/call', params: { name, arguments: args } }
    });
    assert(response.ok(), 'Native MCP request failed: ' + response.status());
    const body = await response.text();
    const data = body.split(/\r?\n/).find(line => line.startsWith('data: '));
    const payload = JSON.parse(data ? data.slice(6) : body);
    if (payload.error || payload.result?.isError) throw new Error(JSON.stringify(payload));
    return payload.result.structuredContent ?? JSON.parse(payload.result.content.find(c => c.type === 'text').text);
  }
  const context = await mcp('get_request_context', { requestId: entry.requestId });
  assert(context.title === 'Shared UI and MCP workflow' && context.tasks.length === 1 &&
    context.messages.find(m => m.kind === 'original')?.source === 'transport-smoke',
    'Refusing a non-smoke request.');
  const taskA = context.tasks[0];
  const taskB = await mcp('create_task', { featureId: context.featureId, name: 'Navigation verification second task',
    actor: 'Navigation verification client' });
  const otherProject = (await mcp('list_projects', {})).find(p => p.id !== context.projectId && p.taskCount > 0);
  assert(otherProject, 'Expected another populated project for membership check.');
  const seed = await mcp('get_project_context', { projectId: otherProject.id });
  assert(seed.id !== context.projectId && seed.tasks.length > 0, 'Expected another seeded project for membership check.');
  const foreignTaskId = seed.tasks[0].id;
  const boardPath = '/project/' + context.projectId + '?request=' + context.id;
  const link = taskId => entry.origin + boardPath + (taskId === null ? '' : '&task=' + taskId);
  await mcp('workflow_command', { input: {
    actor: 'Navigation verification client', key: 'navigation-' + Date.now(),
    requestId: context.id, expectedVersion: context.version, operation: 'add_message', kind: 'progress',
    text: '[Open task A](' + link(taskA.taskId) + ')\n\n[Open task B](' + link(taskB.id) +
      ')\n\n[Return to request](' + link(null) + ')\n\n[Unknown task](' + link(2147483647) +
      ')\n\n[Task from another project](' + link(foreignTaskId) +
      ')\n\n[Canonical task A](' + entry.origin + '/project/' + context.projectId + '?task=' + taskA.taskId + ')'
  } });
  const workspace = page.getByTestId('request-workspace');
  const panel = page.getByTestId('task-detail-panel');
  const filter = page.getByTestId('board-filter-search');
  const currentQuery = () => page.evaluate(() => Object.fromEntries(new URLSearchParams(location.search)));
  async function interactive() {
    await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
  }
  async function showTask(name) {
    await panel.waitFor();
    await panel.getByLabel('Edit task name ' + name, { exact: true }).waitFor();
    assert((await panel.locator('#task-detail-title').innerText()).includes(name), 'Wrong task opened.');
  }
  async function closeTask() {
    await page.getByTestId('task-detail-close-button').click();
    await panel.waitFor({ state: 'hidden' });
    await page.locator('.offcanvas-backdrop').waitFor({ state: 'hidden' });
  }
  async function follow(name, taskId) {
    await workspace.getByRole('link', { name, exact: true }).click();
    await page.waitForURL(url => url.searchParams.get('task') === (taskId === null ? null : String(taskId)));
  }

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(link(taskA.taskId));
  await interactive();
  await showTask(taskA.name);
  await closeTask();
  await workspace.getByRole('button', { name: 'Refresh saved details', exact: true }).click();
  await filter.fill('verification');
  await page.waitForURL(url => url.searchParams.get('q') === 'verification');
  assert(!(await panel.isVisible()), 'An unchanged task query re-opened a dismissed panel.');
  let query = await currentQuery();
  assert(query.task === String(taskA.taskId) && query.request === context.id, 'Filter removed task or request context.');
  await page.getByRole('button', { name: 'Clear filters', exact: true }).click();
  await page.waitForURL(url => !url.searchParams.has('q'));
  query = await currentQuery();
  assert(query.task === String(taskA.taskId) && query.request === context.id, 'Clear removed task or request context.');
  assert(!(await panel.isVisible()), 'Clear re-opened a dismissed panel.');

  await workspace.getByRole('button', { name: 'Updates', exact: true }).click();
  await follow('Open task B', taskB.id);
  await showTask(taskB.name);
  await closeTask();
  await follow('Return to request', null);
  await follow('Open task A', taskA.taskId);
  await showTask(taskA.name);
  await closeTask();
  await follow('Return to request', null);
  await follow('Open task A', taskA.taskId);
  await showTask(taskA.name);
  await closeTask();
  await follow('Unknown task', 2147483647);
  assert(!(await panel.isVisible()), 'Unknown task opened a panel.');
  await follow('Task from another project', foreignTaskId);
  assert(!(await panel.isVisible()), 'Cross-project task opened a panel.');
  await follow('Return to request', null);
  await workspace.getByRole('button', { name: 'Brief', exact: true }).click();
  const draft = 'Retain this unsaved brief when the filter URL changes.';
  await workspace.getByLabel('Desired outcome', { exact: true }).fill(draft);
  await filter.fill('verification');
  await page.waitForURL(url => url.searchParams.get('q') === 'verification');
  assert((await currentQuery()).request === context.id, 'Filtering cleared the selected request.');
  assert(await workspace.getByLabel('Desired outcome', { exact: true }).inputValue() === draft,
    'Filtering discarded the unsaved brief.');
  await page.screenshot({ path: 'navigation-request-preserved.png', fullPage: true });
  await workspace.getByRole('button', { name: 'Updates', exact: true }).click();
  await follow('Canonical task A', taskA.taskId);
  assert(!(await currentQuery()).request, 'Canonical task-only link retained request unexpectedly.');
  await showTask(taskA.name);
  await closeTask();
  return { passed: true, requestId: context.id, initialDeepLink: true, sameProjectTaskLinks: true,
    taskQueryTransitions: true, dismissedPanelStaysClosed: true, requestAndDraftSurviveFilter: true,
    unknownAndForeignTaskIgnored: true, humanUsabilityApproval: 'pending' };
}
