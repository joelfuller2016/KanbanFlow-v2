// Playwright CLI run-code file. Start from a fresh disposable smoke-workflow.mjs board URL.
// Writes one labeled fixture comment. Does not record actual human usability approval.
async page => {
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const entry = await page.evaluate(() => ({ href: location.href, origin: location.origin,
    hostname: location.hostname, port: location.port, requestId: new URLSearchParams(location.search).get('request') }));
  assert(['127.0.0.1', 'localhost'].includes(entry.hostname) && entry.port !== '5123' && entry.requestId,
    'Open a disposable transport fixture, not the normal board.');
  let rpcId = 0;
  async function mcp(name, args) {
    const response = await page.request.post(entry.origin + '/mcp', {
      headers: { Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': '2025-06-18' },
      data: { jsonrpc: '2.0', id: ++rpcId, method: 'tools/call', params: { name, arguments: args } }
    });
    assert(response.ok(), 'MCP request failed: ' + response.status());
    const body = await response.text();
    const line = body.split(/\r?\n/).find(x => x.startsWith('data: '));
    const payload = JSON.parse(line ? line.slice(6) : body);
    if (payload.error || payload.result?.isError) throw new Error(JSON.stringify(payload));
    return payload.result.structuredContent ?? JSON.parse(payload.result.content.find(x => x.type === 'text').text);
  }
  const read = () => mcp('get_request_context', { requestId: entry.requestId });
  const before = await read();
  assert(before.title === 'Shared UI and MCP workflow' && before.tasks.length === 1 &&
    before.messages.find(m => m.kind === 'original')?.source === 'transport-smoke',
    'Refusing a non-smoke request.');
  const taskId = before.tasks[0].taskId;
  const panel = page.getByTestId('task-detail-panel');
  const half = page.getByRole('button', { name: 'Half page', exact: true });
  const full = page.getByRole('button', { name: 'Full page', exact: true });
  const close = page.getByTestId('task-detail-close-button');
  const card = page.getByTestId('board-card-' + taskId + '-title');
  async function board(width = 1280, height = 900) {
    await page.setViewportSize({ width, height });
    await page.goto(entry.href);
    await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
    await page.getByTestId('request-workspace').locator('summary').first().click();
    await card.waitFor();
  }
  async function size(mode, ratio) {
    await page.waitForFunction(({ mode, ratio }) => {
      const el = document.querySelector('[data-testid="task-detail-panel"]');
      const rect = el?.getBoundingClientRect();
      return el?.dataset.size === mode && Math.abs(rect.width - innerWidth * ratio) < 2 &&
        Math.abs(rect.right - innerWidth) < 2 && Math.abs(rect.height - innerHeight) < 2;
    }, { mode, ratio });
    assert(await half.getAttribute('aria-pressed') === String(mode === 'half'), 'Half-page state is incorrect.');
    assert(await full.getAttribute('aria-pressed') === String(mode === 'full'), 'Full-page state is incorrect.');
    assert((await panel.locator('#task-detail-title').innerText()).trim() === before.tasks[0].name,
      'Resizing changed the selected task.');
    const bounds = await panel.evaluate(el => {
      const heading = el.querySelector('.task-detail-heading').getBoundingClientRect();
      const controls = el.querySelector('.task-detail-controls').getBoundingClientRect();
      const header = el.querySelector('.offcanvas-header').getBoundingClientRect();
      return { noOverlap: heading.right <= controls.left, inHeader: controls.top >= header.top &&
        controls.bottom <= header.bottom && controls.right <= innerWidth, topRight: innerWidth - controls.right < 40 };
    });
    assert(bounds.noOverlap && bounds.inHeader && bounds.topRight, 'Top-right controls overlap the title or leave the viewport.');
  }
  async function closed() {
    await panel.waitFor({ state: 'hidden' });
    await page.locator('.offcanvas-backdrop').waitFor({ state: 'hidden' });
    assert(await card.evaluate(el => el === document.activeElement || el.closest('[data-task-id]')?.contains(document.activeElement)),
      'Closing did not return keyboard focus to the card.');
  }

  await board();
  await card.focus();
  await page.keyboard.press('Enter');
  await size('half', .5);
  await page.screenshot({ path: 'card-half-page.png', fullPage: true, animations: 'disabled' });
  const draft = 'Display verification: preserve this unsent handoff across size changes.';
  const comment = page.getByTestId('task-comment-input');
  const type = page.getByTestId('task-comment-type-select');
  await comment.fill(draft);
  await type.selectOption('Handoff');
  await full.click();
  await size('full', 1);
  assert(await comment.inputValue() === draft && await type.inputValue() === 'Handoff',
    'Expanding lost the unsent comment or its type.');
  await page.screenshot({ path: 'card-full-page.png', fullPage: true, animations: 'disabled' });
  await half.focus();
  await page.keyboard.press('Space');
  await size('half', .5);
  assert(await comment.inputValue() === draft && await type.inputValue() === 'Handoff',
    'Restoring half page lost the draft.');
  const afterResize = await read();
  assert(afterResize.version === before.version && afterResize.tasks[0].editToken === before.tasks[0].editToken &&
    afterResize.tasks[0].comments.length === before.tasks[0].comments.length,
    'A display-only action mutated task or workflow data.');
  await full.focus();
  await page.keyboard.press('Enter');
  await size('full', 1);
  await page.keyboard.press('Tab');
  assert(await panel.evaluate(el => el.contains(document.activeElement)), 'Keyboard focus escaped the open card.');
  await page.getByTestId('task-comment-add-button').click();
  await panel.getByText(draft, { exact: true }).first().waitFor();
  const saved = await read();
  assert(saved.tasks[0].comments.some(c => c.text === draft && c.type === 'Handoff'),
    'Saving from full page did not reach the same task through MCP.');
  await close.click();
  await closed();
  await card.click();
  await size('half', .5);
  await full.click();
  await size('full', 1);
  await page.keyboard.press('Escape');
  await closed();
  const filter = page.getByTestId('board-filter-search');
  await filter.fill('Verify');
  await page.waitForURL(url => url.searchParams.get('q') === 'Verify');
  await card.waitFor();

  await board(390, 844);
  await card.click();
  await size('half', 1);
  await full.click();
  await size('full', 1);
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile page overflows horizontally.');
  await page.screenshot({ path: 'card-mobile.png', fullPage: true, animations: 'disabled' });
  await half.click();
  await size('half', 1);
  await close.click();
  await closed();
  await page.setViewportSize({ width: 1280, height: 900 });
  return { passed: true, taskId, desktopHalfAndFull: true, topRightControls: true,
    draftAndTaskPreserved: true, savedFromFullPageVisibleToMcp: true, keyboardAndFocusReturn: true,
    closeAndEscape: true, mobileFits: true, humanUsabilityApproval: 'pending' };
}
