// Playwright CLI run-code file. Start on a disposable seeded project board.
async page => {
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const url = new URL(page.url());
  assert(['127.0.0.1', 'localhost'].includes(url.hostname) && url.port !== '5123' && /\/project\/\d+/.test(url.pathname),
    'Open a disposable seeded project board.');

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.locator('[data-testid="request-workspace"][data-interactive="true"]').waitFor();
  await page.getByTestId('reset-project-layout').click();
  const cards = page.locator('[data-testid^="board-card-"][data-testid$="-title"]');
  await cards.first().waitFor();
  const deepCardId = await cards.evaluateAll(items => items.reduce((deepest, item) =>
    item.getBoundingClientRect().top > deepest.getBoundingClientRect().top ? item : deepest).dataset.testid);
  const card = page.getByTestId(deepCardId);
  const expectedTitle = await card.getAttribute('title');
  await card.scrollIntoViewIfNeeded();
  const cardScroll = await page.evaluate(() => scrollY);
  assert(cardScroll > 100, 'A seeded card must start below the opening viewport.');
  await card.click();

  const panel = page.getByTestId('task-detail-panel');
  const body = panel.locator('#task-detail-content');
  const navigation = panel.getByRole('navigation', { name: 'Task sections' });
  const comment = panel.getByTestId('task-comment-input');
  const commentType = panel.getByTestId('task-comment-type-select');
  const draft = 'Unsaved layout verification draft';
  await panel.waitFor();
  await page.waitForFunction(previousY => {
    const header = document.querySelector('#task-details .task-detail-header').getBoundingClientRect();
    return scrollY < previousY && header.top >= -1 && header.bottom <= innerHeight;
  }, cardScroll);
  assert((await panel.locator('#task-detail-title').textContent()).trim() === expectedTitle, 'Task title changed.');
  assert(await panel.locator('.task-detail-identity').getByText(/^Task #\d+$/).isVisible(), 'Task ID is missing.');
  assert(await body.evaluate(el => {
    const primary = el.querySelector('.task-detail-primary').getBoundingClientRect();
    const rail = el.querySelector('.task-detail-rail').getBoundingClientRect();
    return rail.left >= primary.right && Math.abs(rail.top - primary.top) < 2 && getComputedStyle(el).overflowY === 'visible';
  }), 'Wide desktop dock does not share document scroll with a supporting rail.');
  assert(await panel.getByTestId('task-detail-description-read').evaluate(el =>
    getComputedStyle(el).maxHeight === 'none' && el.scrollHeight <= el.clientHeight + 1
  ), 'The description must use the work item scroll surface.');

  const beforeNavigation = page.url();
  await navigation.getByRole('button', { name: 'Fields' }).click();
  await page.waitForFunction(() => document.activeElement.getAttribute('aria-labelledby') === 'task-detail-fields-heading');
  assert(await panel.getByLabel('Status', { exact: true }).isVisible(), 'Task status editor is missing.');
  await navigation.getByRole('button', { name: 'Discussion' }).click();
  await page.waitForFunction(() => document.activeElement.dataset.testid === 'task-comments-section');
  assert(page.url() === beforeNavigation, 'Task section navigation changed the route.');

  await comment.fill(draft);
  await commentType.selectOption('Handoff');
  const railIsStacked = overflow => body.evaluate((el, expectedOverflow) => {
    const primary = el.querySelector('.task-detail-primary').getBoundingClientRect();
    const rail = el.querySelector('.task-detail-rail').getBoundingClientRect();
    return rail.top >= primary.bottom && Math.abs(rail.left - primary.left) < 2 && getComputedStyle(el).overflowY === expectedOverflow;
  }, overflow);

  await page.getByTestId('details-resize-handle').press('Home');
  await page.waitForFunction(() => document.querySelector('#task-details').dataset.size === 'custom');
  assert(await railIsStacked('visible'), 'A narrow dock does not stack the supporting rail with document scroll.');
  assert(await comment.inputValue() === draft, 'Dock resizing lost the discussion draft.');

  await panel.getByTestId('task-detail-full-page-button').click();
  await page.waitForFunction(() => document.querySelector('#task-details').getAttribute('aria-modal') === 'true');
  assert(await body.evaluate(el => getComputedStyle(el).overflowY === 'auto'), 'Full-page card lost its internal scroll.');
  assert(await comment.inputValue() === draft && await commentType.inputValue() === 'Handoff', 'Full view lost the discussion draft.');

  await page.setViewportSize({ width: 390, height: 844 });
  await panel.getByTestId('task-detail-half-page-button').click();
  assert(await railIsStacked('auto'), 'Mobile drawer does not stack the supporting rail below the discussion.');
  assert(await comment.inputValue() === draft && await commentType.inputValue() === 'Handoff', 'Mobile stacking lost the discussion draft.');

  await page.keyboard.press('Escape');
  await panel.waitFor({ state: 'hidden' });
  return { passed: true, revealedDock: true, responsiveRail: true, sectionFocus: true, draftPreserved: true, scrollModes: true };
}
