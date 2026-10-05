// Playwright CLI run-code file. Use a fresh disposable smoke-workflow.mjs board URL.
// Creates labeled hierarchy/drag fixtures and deletes only those ordinary fixture tasks.
async page => {
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const entry = await page.evaluate(() => ({ href: location.href, origin: location.origin,
    hostname: location.hostname, port: location.port, requestId: new URLSearchParams(location.search).get('request') }));
  assert(['127.0.0.1', 'localhost'].includes(entry.hostname) && entry.port !== '5123' && entry.requestId,
    'Open a fresh disposable transport fixture, not the normal board.');
  let rpcId = 0;
  async function mcp(name, args) {
    const response = await page.request.post(entry.origin + '/mcp', {
      headers: { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
      data: { jsonrpc: '2.0', id: ++rpcId, method: 'tools/call', params: { name, arguments: args } }
    });
    assert(response.ok(), 'MCP failed: ' + response.status());
    const body = await response.text();
    const line = body.split(/\r?\n/).find(x => x.startsWith('data: '));
    const payload = JSON.parse(line ? line.slice(6) : body);
    if (payload.error || payload.result?.isError) throw new Error(JSON.stringify(payload));
    return payload.result.structuredContent ?? JSON.parse(payload.result.content.find(x => x.type === 'text').text);
  }
  async function api(method, path, data) {
    const response = await page.request.fetch(entry.origin + path, { method, data });
    assert(response.ok(), method + ' ' + path + ' failed: ' + response.status());
    return response.status() === 204 ? null : response.json();
  }
  const read = () => mcp('get_request_context', { requestId: entry.requestId });
  const context = await read();
  assert(context.title === 'Shared UI and MCP workflow' && context.tasks.length === 1 &&
    context.messages.find(m => m.kind === 'original')?.source === 'transport-smoke', 'Refusing a non-smoke request.');
  const projectBefore = await mcp('get_project_context', { projectId: context.projectId });
  assert(projectBefore.features.length === 1, 'Use a fresh fixture for each run.');
  const taskA = { id: context.tasks[0].taskId, name: context.tasks[0].name };
  for (let i = 0; i < 20; i++)
    await api('POST', '/api/projects/' + context.projectId + '/features', { name: 'Outline spacer ' + i });
  const root = await api('POST', '/api/projects/' + context.projectId + '/features', { name: 'Outline parent' });
  const child = await api('POST', '/api/projects/' + context.projectId + '/features',
    { name: 'Outline nested feature', parentFeatureId: root.id });
  const taskB = await mcp('create_task', { featureId: child.id, name: 'Outline navigation B', status: 'Todo', actor: 'Outline verification' });
  const taskC = await mcp('create_task', { featureId: child.id, name: 'Outline navigation C', status: 'Review', actor: 'Outline verification' });
  const subtask = await api('POST', '/api/tasks/' + taskB.id + '/subtasks', { text: 'Outline subtask' });
  const nested = await api('POST', '/api/tasks/' + taskB.id + '/subtasks', { text: 'Outline nested subtask', parentSubtaskId: subtask.id });
  const otherProject = (await mcp('list_projects', {})).find(p => p.id !== context.projectId);
  assert(otherProject, 'Expected another project for navigation.');
  const seed = await mcp('get_project_context', { projectId: otherProject.id });
  assert(seed.id !== context.projectId, 'Expected a separate seeded project.');
  await mcp('workflow_command', { input: { actor: 'Outline verification', key: 'outline-' + Date.now(),
    requestId: context.id, expectedVersion: context.version, operation: 'add_message', kind: 'progress',
    text: '[Outline verification other project](' + entry.origin + '/project/' + seed.id + ')' } });
  const baseline = await read();
  const taskBefore = await mcp('get_task', { taskId: taskB.id });
  const outline = page.getByTestId('project-outline');
  const items = page.getByTestId('outline-items');
  const header = outline.locator('.project-outline-header');
  const back = page.getByTestId('outline-back-button');
  const forward = page.getByTestId('outline-forward-button');
  const panel = page.getByTestId('task-detail-panel');
  const editor = page.getByTestId('edit-item-modal');
  const workspace = page.getByTestId('request-workspace');
  const row = (kind, id) => page.getByTestId('outline-' + kind + '-' + id);
  async function board(width = 1280) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(entry.href);
    await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
    await workspace.locator('summary').first().click();
  }
  async function selected(kind, id) {
    await page.waitForFunction(testId => {
      const chosen = document.querySelectorAll('#project-outline .outline-item.is-selected');
      if (chosen.length !== 1 || chosen[0].dataset.testid !== testId) return false;
      const row = chosen[0].getBoundingClientRect();
      const items = document.querySelector('[data-testid="outline-items"]').getBoundingClientRect();
      return row.top >= items.top - 1 && row.bottom <= items.bottom + 1 &&
        chosen[0].querySelector('button[aria-current="true"]');
    }, 'outline-' + kind + '-' + id);
  }
  async function showCard(task) {
    await page.getByTestId('board-filter-search').fill(task.name);
    await page.waitForURL(url => url.searchParams.get('q') === task.name);
    await page.getByTestId('board-card-' + task.id + '-title').click();
    await panel.waitFor();
    await selected('task', task.id);
  }
  async function showOutline(kind, id) {
    await page.getByTestId('task-detail-show-in-outline-button').click();
    await panel.waitFor({ state: 'hidden' });
    await page.locator('.offcanvas-backdrop').waitFor({ state: 'hidden' });
    await selected(kind, id);
    await page.waitForFunction(testId => document.querySelector('[data-testid="' + testId + '"] button[aria-current="true"]') === document.activeElement,
      'outline-' + kind + '-' + id);
  }
  async function openOutline(kind, id, expectedName) {
    const label = kind === 'feature' ? 'Edit feature ' : kind === 'subtask' ? 'Open parent task details for ' : 'Open task details for ';
    const name = kind === 'subtask' ? 'Outline nested subtask' : expectedName;
    await row(kind, id).getByRole('button', { name: label + name, exact: true }).click();
    await editor.waitFor();
    assert(await page.locator('#edit-item-name').inputValue() === expectedName, 'Outline opened the wrong existing editor.');
    await page.getByTestId('edit-item-close-button').click();
    await editor.waitFor({ state: 'hidden' });
    await selected(kind, id);
  }
  async function history(button, kind, id, keyboard = false) {
    if (keyboard) { await button.focus(); await page.keyboard.press('Enter'); }
    else await button.click();
    await selected(kind, id);
    assert(!(await panel.isVisible()) && !(await editor.isVisible()), 'History reopened a detail panel.');
  }
  const positions = () => page.evaluate(() => ({ x: scrollX, y: scrollY,
    board: [...document.querySelectorAll('.project-main, [data-testid^="board-column-"]')].map(e => [e.scrollLeft, e.scrollTop]) }));

  await board();
  assert(await back.isDisabled() && await forward.isDisabled(), 'New history should be empty.');
  assert(await outline.locator('.is-selected').count() === 0, 'New visit selected an arbitrary item.');
  const frozen = await header.boundingBox();
  await page.getByTestId('outline-feature-' + root.id + '-toggle').click();
  await row('task', taskB.id).waitFor({ state: 'detached' });
  await showCard(taskB);
  const beforeScroll = await positions();
  await page.getByTestId('task-detail-full-page-button').click();
  await showOutline('task', taskB.id);
  assert(JSON.stringify(await positions()) === JSON.stringify(beforeScroll),
    'Revealing the outline moved the board/page: ' + JSON.stringify({ beforeScroll, after: await positions() }));
  assert(Math.abs((await header.boundingBox()).y - frozen.y) < 1, 'Outline header scrolled with its items.');
  assert(await items.evaluate(e => e.scrollTop > 200), 'Long outline did not scroll to the selected task.');
  assert(await back.isDisabled() && await forward.isDisabled(), 'Show in outline recorded a duplicate visit.');
  await page.screenshot({ path: 'outline-desktop.png', fullPage: true, animations: 'disabled' });

  await showCard(taskA); await showOutline('task', taskA.id);
  await history(back, 'task', taskB.id, true);
  assert(await back.isDisabled(), 'First history item should disable Back.');
  await history(forward, 'task', taskA.id, true);
  await showCard(taskA); await showOutline('task', taskA.id);
  await history(back, 'task', taskB.id);
  assert(await back.isDisabled(), 'Reselecting the current card recorded a duplicate.');
  await history(forward, 'task', taskA.id);
  await openOutline('feature', child.id, child.name);
  await openOutline('subtask', nested.id, taskB.name);
  await page.getByTestId('outline-subtask-' + nested.id + '-add-button').click();
  await panel.waitFor();
  await showOutline('subtask', nested.id);
  await page.getByTestId('outline-task-' + taskB.id + '-toggle').click();
  await page.getByTestId('outline-feature-' + root.id + '-toggle').click();
  await history(back, 'feature', child.id);
  await history(forward, 'subtask', nested.id);
  await history(back, 'feature', child.id);
  await openOutline('task', taskA.id, taskA.name);
  assert(await forward.isDisabled(), 'A new visit after Back did not discard the forward branch.');
  await history(back, 'feature', child.id);
  await history(forward, 'task', taskA.id);
  const afterNavigation = await read();
  assert(afterNavigation.version === baseline.version && afterNavigation.tasks[0].editToken === baseline.tasks[0].editToken,
    'Outline navigation mutated the workflow or task token.');
  assert(JSON.stringify(await mcp('get_task', { taskId: taskB.id })) === JSON.stringify(taskBefore), 'Navigation mutated ordinary task data.');

  // A real drag after ancestor expansion must still anchor to task IDs around subtask siblings.
  await openOutline('task', taskB.id, taskB.name);
  await page.waitForFunction(id => window.Sortable?.get(document.querySelector('[data-testid="outline-feature-' + id + '-tasks"]')), child.id);
  const source = await row('task', taskC.id).boundingBox();
  const target = await row('task', taskB.id).boundingBox();
  await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
  await page.mouse.down();
  for (let step = 1; step <= 12; step++) {
    await page.mouse.move(source.x + source.width / 2,
      source.y + source.height / 2 + (target.y + 5 - source.y - source.height / 2) * step / 12, { steps: 2 });
    await page.waitForTimeout(25);
  }
  await page.mouse.up();
  await page.waitForFunction(async ({ featureId, firstId }) => {
    const tasks = await (await fetch('/api/features/' + featureId + '/tasks')).json();
    return tasks[0]?.id === firstId;
  }, { featureId: child.id, firstId: taskC.id });
  const moved = await mcp('get_task', { taskId: taskC.id });
  assert(moved.featureId === child.id && moved.status === 'Review', 'Outline drag changed membership/status.');

  // Deleted entries are skipped, including when the current item itself disappears.
  await board();
  await openOutline('task', taskA.id, taskA.name);
  await openOutline('task', taskC.id, taskC.name);
  await openOutline('task', taskB.id, taskB.name);
  await mcp('get_task', { taskId: taskC.id });
  await mcp('delete_task', { taskId: taskC.id, actor: 'Outline fixture cleanup' });
  await workspace.locator('summary').first().click();
  await workspace.getByRole('button', { name: 'Refresh saved details', exact: true }).click();
  await row('task', taskC.id).waitFor({ state: 'detached' });
  await history(back, 'task', taskA.id);
  await history(forward, 'task', taskB.id);
  await mcp('get_task', { taskId: taskB.id });
  await mcp('delete_task', { taskId: taskB.id, actor: 'Outline fixture cleanup' });
  await workspace.getByRole('button', { name: 'Refresh saved details', exact: true }).click();
  await row('task', taskB.id).waitFor({ state: 'detached' });
  assert(await outline.locator('.is-selected').count() === 0, 'Deleted current item left a stale highlight.');
  await history(back, 'task', taskA.id);
  assert(await forward.isDisabled(), 'Forward still enabled for deleted items only.');

  await workspace.getByRole('button', { name: 'Updates', exact: true }).click();
  await workspace.getByRole('link', { name: 'Outline verification other project', exact: true }).click();
  await page.waitForURL(url => url.pathname === '/project/' + seed.id);
  await outline.getByRole('heading', { name: seed.name, exact: true }).waitFor();
  assert(await back.isDisabled() && await forward.isDisabled() && await outline.locator('.is-selected').count() === 0,
    'Another project inherited outline history or selection.');
  assert(await row('task', taskA.id).count() === 0, 'Another project displayed the old project task.');

  await board(390);
  await showCard(taskA);
  await showOutline('task', taskA.id);
  assert(await outline.getAttribute('aria-hidden') !== 'true' && !(await outline.evaluate(e => e.inert)), 'Mobile outline stayed hidden/inert.');
  const mobileHeader = await header.boundingBox();
  await openOutline('feature', child.id, child.name);
  await history(back, 'task', taskA.id, true);
  await history(forward, 'feature', child.id, true);
  assert(Math.abs((await header.boundingBox()).y - mobileHeader.y) < 1, 'Mobile header moved while items scrolled.');
  const bounds = await header.boundingBox();
  assert(bounds.x >= 0 && bounds.x + bounds.width <= 390 && await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    'Outline/header overflowed the mobile viewport.');
  await page.screenshot({ path: 'outline-mobile.png', fullPage: true, animations: 'disabled' });
  await page.getByTestId('close-project-outline-button').click();
  await page.waitForFunction(() => document.querySelector('#project-outline').inert);
  console.log(JSON.stringify({ passed: true, selection: 'feature/task/nested subtask', history: 'boundaries, duplicates, branching, deletion, project reset',
    reveal: 'collapsed ancestors, fixed header, scoped scroll, focus, desktop/full panel/mobile', drag: 'task identity and status preserved',
    mutations: 'navigation left workflow and task data unchanged', humanApproval: 'pending' }));
}
