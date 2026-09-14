import assert from 'node:assert/strict';

// Real keyboard reachability: never focus the destination from the test.
export async function projectSkipNavigation(page, api) {
  const project = await api('POST', '/api/projects', { name: 'Keyboard bypass regression' });
  const originalViewport = page.viewportSize();
  let firstTask;
  const origin = new URL(page.url()).origin;
  const errors = [];
  const onError = (error) => errors.push(error.message);
  page.on('pageerror', onError);
  const focusedId = () => page.evaluate(() => document.activeElement?.id);
  const focusedTestId = () => page.evaluate(() => document.activeElement?.getAttribute('data-testid'));
  const open = async (suffix = '') => {
    await page.goto(`${origin}/project/${project.id}${suffix}`);
    await page.getByTestId('board-filter-search').waitFor();
    // Fresh document navigation gives an actual page-load keyboard starting point.
    await page.waitForFunction(() => window.KanbanFlow?._boardSortables?.some(
      (sortable) => sortable.el?.isConnected));
  };
  try {
    const feature = await api('POST', `/api/projects/${project.id}/features`, { name: 'Expanded outline' });
    for (let i = 0; i < 170; i++) {
      const task = await api('POST', `/api/features/${feature.id}/tasks`, {
        name: `Keyboard task ${String(i).padStart(3, '0')}`, status: 'Backlog', priority: 'Medium',
      });
      firstTask ??= task;
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    // Native fallback must survive the application's <base href="/"> when the module fails.
    const fallbackPage = await page.context().browser().newPage();
    try {
      let blockedModules = 0;
      await fallbackPage.route('**/js/project-skip-links*.js', (route) => {
        blockedModules++;
        return route.abort();
      });
      const filteredUrl = `${origin}/project/${project.id}?q=Keyboard&hideDone=true`;
      for (const testId of ['skip-to-board-search', 'skip-to-board-cards']) {
        await fallbackPage.goto(filteredUrl);
        await fallbackPage.getByTestId('board-filter-search').waitFor();
        assert.ok(blockedModules > 0, 'fallback test must block the skip-link module');
        assert.equal(await fallbackPage.getByTestId(testId).evaluate((el) => el.href),
          `${filteredUrl}#project-content`, 'native fallback must retain project and filter query');
        await fallbackPage.keyboard.press('Tab');
        if (testId === 'skip-to-board-cards') await fallbackPage.keyboard.press('Tab');
        await fallbackPage.keyboard.press('Enter');
        await fallbackPage.waitForURL(`${filteredUrl}#project-content`);
        await fallbackPage.waitForFunction(() => document.activeElement?.id === 'project-content');
      }
    } finally {
      await fallbackPage.close();
    }
    console.log('Native filtered-project fallback PASS (both links; module blocked)');
    await open();
    assert.ok(await page.locator('#project-outline button, #project-outline input').count() > 500,
      'fixture must retain the large, expanded outline; do not remove its keyboard controls');
    await page.keyboard.press('Tab');
    assert.equal(await focusedTestId(), 'skip-to-board-search', 'first Tab must bypass navigation and outline');
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.activeElement?.id === 'board-filter-search');
    assert.equal(await focusedId(), 'board-filter-search');

    await open('?q=Keyboard&hideDone=true');
    const filteredUrl = page.url();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    assert.equal(await focusedId(), 'board-filter-search');
    assert.equal(page.url(), filteredUrl, 'bypass must not change filter/deep-link URL state');
    assert.equal(await page.getByTestId('board-filter-search').inputValue(), 'Keyboard');

    await open();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    assert.equal(await focusedTestId(), 'skip-to-board-cards');
    await page.keyboard.press('Enter');
    assert.equal(await focusedId(), 'kanban-board-heading');
    let tabs = 2;
    while (tabs < 5 && await focusedTestId() !== `board-card-${firstTask.id}-title`) {
      await page.keyboard.press('Tab');
      tabs++;
    }
    assert.equal(await focusedTestId(), `board-card-${firstTask.id}-title`, 'card title must be reachable within 5 Tabs');
    for (const key of ['Enter', 'Space']) {
      await page.keyboard.press(key);
      await page.locator('[data-testid="task-detail-panel"]').waitFor();
      const modalUrl = page.url();
      await page.getByTestId('skip-to-board-search').dispatchEvent('click');
      assert.ok(await page.evaluate(() => document.querySelector('[data-testid="task-detail-panel"]')
        .contains(document.activeElement)), 'bypass must not escape an open modal');
      assert.equal(page.url(), modalUrl);
      await page.keyboard.press('Escape');
      await page.locator('[data-testid="task-detail-panel"]').waitFor({ state: 'hidden' });
      await page.waitForFunction((testId) => document.activeElement?.getAttribute('data-testid') === testId,
        `board-card-${firstTask.id}-title`);
    }

    await page.setViewportSize({ width: 390, height: 844 });
    await open('?q=no-matching-task');
    await page.keyboard.press('Tab');
    const link = page.getByTestId('skip-to-board-search');
    const box = await link.boundingBox();
    assert.ok(box && box.x >= 0 && box.y >= 0 && box.x + box.width <= 390, 'focused link must fit narrow viewport');
    assert.equal(await link.evaluate((el) => getComputedStyle(el).outlineStyle), 'solid');
    await page.keyboard.press('Enter');
    assert.equal(await focusedId(), 'board-filter-search');
    assert.equal(await page.locator('#project-outline').getAttribute('inert'), '');
    await open('?q=no-matching-task');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    assert.equal(await focusedId(), 'kanban-board-heading', 'empty results still have a safe board target');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'no page-wide overflow');

    await page.emulateMedia({ forcedColors: 'active' });
    await open();
    await page.keyboard.press('Tab');
    assert.equal(await link.evaluate((el) => getComputedStyle(el).outlineStyle), 'solid');
    await page.emulateMedia({ forcedColors: 'none' });

    // Exercise Blazor enhanced navigation rather than another full document load.
    await open();
    await page.evaluate(() => { window.skipNavigationDocument = true; });
    await page.getByRole('link', { name: 'KanbanFlow', exact: true }).click();
    const projectOpener = page.locator(`[data-project-id="${project.id}"]`).getByRole('button', { name: 'Open', exact: true });
    for (let attempt = 0; attempt < 3; attempt++) {
      await projectOpener.click();
      try { await page.waitForURL(`${origin}/project/${project.id}`, { timeout: 2500 }); break; }
      catch (error) { if (attempt === 2) throw error; }
    }
    await page.getByTestId('board-filter-search').waitFor();
    assert.equal(await page.evaluate(() => window.skipNavigationDocument), true,
      'fixture must exercise enhanced navigation without replacing the document');
    // Directly activate the existing link after route reuse; keyboard counts are proven above.
    await page.getByTestId('skip-to-board-search').dispatchEvent('click');
    assert.equal(await focusedId(), 'board-filter-search');

    await page.goto(`${origin}/project/2147483647`);
    await page.getByText('Project not found', { exact: true }).waitFor();
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    assert.equal(await focusedId(), 'project-content', 'missing board must focus safe page fallback');
    assert.deepEqual(errors, [], 'no uncaught browser errors');
  } finally {
    page.off('pageerror', onError);
    await page.emulateMedia({ forcedColors: 'none' });
    if (originalViewport) await page.setViewportSize(originalViewport);
    await api('DELETE', `/api/projects/${project.id}`);
  }
}
