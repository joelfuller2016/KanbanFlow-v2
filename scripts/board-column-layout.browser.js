// Playwright CLI run-code. Open a disposable KanbanFlow instance first.
async page => {
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const origin = await page.evaluate(() => location.origin);
  check(/^http:\/\/127\.0\.0\.1:\d+$/.test(origin) && !origin.endsWith(':5123'), 'Use a disposable local app.');
  async function api(method, path, data) {
    const response = await page.request.fetch(origin + path, { method, data });
    check(response.ok(), `${method} ${path} failed: ${response.status()}`);
    return response.status() === 204 ? null : response.json();
  }

  const project = await api('POST', '/api/projects', { name: `Board column layout ${Date.now()}` });
  try {
    const feature = await api('POST', `/api/projects/${project.id}/features`, { name: 'Lane checks' });
    const source = await api('POST', `/api/features/${feature.id}/tasks`, { name: 'Move me', status: 'Backlog' });
    const existing = await api('POST', `/api/features/${feature.id}/tasks`, { name: 'Already here', status: 'Todo' });
    const board = page.getByTestId('kanban-board');
    const lane = status => page.getByTestId(`board-column-${status}`);
    const toggle = status => page.getByTestId(`board-column-${status}-collapse-toggle`);
    const cards = status => page.getByTestId(`board-column-${status}-cards`);

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`${origin}/project/${project.id}`);
    await page.waitForFunction(() => {
      const container = document.querySelector('[data-testid="board-column-Todo-cards"]');
      return container && window.Sortable?.get(container);
    });

    for (const status of ['Backlog', 'Todo', 'InProgress', 'Review', 'Done']) {
      check(await toggle(status).count() === 1, `${status} needs a collapse button.`);
      check(await toggle(status).getAttribute('aria-expanded') === 'true', `${status} should start expanded.`);
      check(await toggle(status).getAttribute('aria-controls') === await cards(status).getAttribute('id'),
        `${status} disclosure should identify its cards.`);
      check((await lane(status).boundingBox()).width >= 250, `${status} should have a readable desktop width.`);
    }

    await board.focus();
    const headingLeft = (await page.locator('#kanban-board-heading').boundingBox()).x;
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(() => document.querySelector('[data-testid="kanban-board"]').scrollLeft > 0);
    check((await page.locator('#kanban-board-heading').boundingBox()).x === headingLeft, 'Horizontal scroll should leave the heading in place.');

    const todoToggle = toggle('Todo');
    const card = page.getByTestId(`board-card-${existing.id}`);
    const expanded = (await lane('Todo').boundingBox()).width;
    async function pressToggle(key, expanded) {
      await todoToggle.focus();
      await page.keyboard.press(key);
      await todoToggle.and(page.locator(`[aria-expanded="${expanded}"]`)).waitFor();
      check(await todoToggle.evaluate(button => button === document.activeElement), 'Disclosure should retain keyboard focus.');
    }
    await pressToggle('Enter', false);
    check((await lane('Todo').boundingBox()).width < expanded / 2, 'A collapsed lane should reclaim most of its width.');
    check(await lane('Todo').getByRole('heading', { name: 'To Do column, 1 item' }).isVisible(),
      'The collapsed heading should retain its status and card count.');
    check(await card.count() === 0 && await page.getByTestId('add-task-Todo-button').count() === 0,
      'Collapsed cards and the add action should leave the lane and tab order.');
    check(await cards('Todo').evaluate(container => window.Sortable.get(container) != null), 'The rail should remain a drag destination.');
    await pressToggle('Space', true);
    await card.waitFor();
    await pressToggle('Enter', false);

    // Move the Backlog card by pointer so both Sortable's rail binding and the server move are exercised.
    await board.evaluate(element => { element.scrollLeft = 0; });
    const sourceBox = await page.getByTestId(`board-card-${source.id}`).boundingBox();
    const targetBox = await cards('Todo').boundingBox();
    await page.mouse.move(sourceBox.x + sourceBox.width - 6, sourceBox.y + 8);
    await page.mouse.down();
    await page.mouse.move(targetBox.x + targetBox.width / 2, targetBox.y + Math.min(35, targetBox.height / 2), { steps: 24 });
    await page.locator('.kanban-ghost').waitFor();
    await page.mouse.up();
    await cards('Todo').getByTestId(`board-card-${source.id}`).waitFor();
    check(await todoToggle.getAttribute('aria-expanded') === 'true', 'A successful move should reveal the collapsed destination.');
    check((await api('GET', `/api/tasks/${source.id}`)).status === 'Todo', 'The real drop should persist the selected lane.');

    await pressToggle('Enter', false);
    const destination = await cards('Todo').elementHandle();
    await page.getByTestId('reset-project-layout').click();
    await todoToggle.and(page.locator('[aria-expanded="true"]')).waitFor();
    check(await destination.evaluate(container => container.isConnected && window.Sortable.get(container) != null),
      'Resetting the column layout should keep the existing drop target bound.');

    await page.setViewportSize({ width: 390, height: 844 });
    await board.evaluate(element => { element.scrollLeft = 0; });
    check(await board.evaluate(element => element.scrollWidth > element.clientWidth &&
      document.documentElement.scrollWidth === document.documentElement.clientWidth),
      'The narrow layout should scroll the board without scrolling the whole page.');
    await toggle('Done').focus();
    check(await board.evaluate(element => element.scrollLeft > 0), 'Keyboard focus should bring the last lane into view.');
    return { projectId: project.id, desktopWidth: expanded, keyboardCollapse: true, collapsedDropPersisted: true, mobileBoardScroll: true };
  } finally {
    await api('DELETE', `/api/projects/${project.id}`);
  }
}
