import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const boardUrl = new URL(process.argv[2] ?? 'http://127.0.0.1:5123/project/2');
assert.equal(boardUrl.hostname, '127.0.0.1', 'Use a local seeded board');

const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.goto(boardUrl.href);
  await page.waitForFunction(() => window.KanbanFlow?._boardSortables?.some(sortable => sortable.el?.isConnected));

  const toolbar = page.getByTestId('project-board-toolbar');
  assert.ok(await toolbar.isVisible(), 'Board commands and filters should share one toolbar');
  assert.ok(await toolbar.getByTestId('board-filter-search').isVisible());
  assert.ok(await toolbar.getByTestId('board-filter-summary').isVisible());
  const requests = toolbar.getByTestId('request-workspace');
  const requestsSummary = requests.locator('summary');
  await requestsSummary.press('Enter');
  await requests.locator('.request-workspace-content').waitFor();
  const requirements = toolbar.getByTestId('requirements-toggle');
  await requirements.click();
  await page.locator('#requirements-content').waitFor();
  await requests.locator('.request-workspace-content').waitFor({ state: 'hidden' });
  await requirements.click();
  await page.locator('#requirements-content').waitFor({ state: 'hidden' });

  const board = page.getByTestId('kanban-board');
  const firstTitle = board.locator('.board-card-title-text').first();
  const longLane = page.getByTestId('board-column-Backlog-cards');
  const box = await firstTitle.boundingBox();
  await page.mouse.move(box.x + 8, box.y + 8);
  await page.mouse.wheel(0, 750);
  await page.waitForFunction(() => window.scrollY >= 400, null, { timeout: 3000 });
  assert.equal(await longLane.evaluate(element => element.scrollTop), 0,
    'Page scrolling should move the project, including the outline and board');
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.mouse.move(1, 1);
  assert.equal(await firstTitle.evaluate(element => getComputedStyle(element).webkitLineClamp), 'none');
  assert.ok(await firstTitle.evaluate(element => element.scrollHeight <= element.clientHeight + 1),
    'The full card title should be visible without hover');
  assert.ok(await board.evaluate(element => element.scrollWidth > element.clientWidth),
    'Columns should retain readable widths and scroll within the board at laptop width');

  const desktopOutlineToggle = toolbar.getByTestId('toggle-desktop-project-outline');
  const outline = page.getByTestId('project-outline');
  await outline.waitFor();
  await desktopOutlineToggle.press('Enter');
  await outline.waitFor({ state: 'hidden' });
  await desktopOutlineToggle.press('Space');
  await outline.waitFor();
  const pane = page.locator('[data-project-panel="outline"]');
  const handle = page.getByTestId('outline-resize-handle');
  const originalOutlineWidth = (await pane.boundingBox()).width;
  assert.ok(originalOutlineWidth >= 360, 'The desktop outline should have space for larger task cards');
  await handle.press('ArrowRight');
  await page.waitForFunction(width => document.querySelector('[data-project-panel="outline"]').getBoundingClientRect().width > width, originalOutlineWidth);
  await pane.getByTestId('dock-project-outline').click();
  await page.waitForFunction(() => document.querySelector('[data-project-panel="outline"]').dataset.dock === 'right');
  await page.reload();
  await page.waitForFunction(() => document.querySelector('[data-project-panel="outline"]')?.dataset.dock === 'right');
  assert.ok((await pane.boundingBox()).width > originalOutlineWidth, 'Width should survive reloading the project');
  await toolbar.getByTestId('reset-project-layout').click();
  await page.waitForFunction(() => document.querySelector('[data-project-panel="outline"]').dataset.dock === 'left');
  assert.equal((await pane.boundingBox()).width, originalOutlineWidth);

  const outlineTitle = outline.locator('button.outline-task-title').first();
  const outlineTitleId = await outlineTitle.getAttribute('data-testid');
  await outlineTitle.click();
  const details = page.getByTestId('task-detail-panel');
  await details.waitFor();
  await details.getByTestId('task-detail-close-button').click();
  await details.waitFor({ state: 'hidden' });
  await page.waitForFunction(testId => document.activeElement?.dataset.testid === testId, outlineTitleId);

  await page.setViewportSize({ width: 1000, height: 768 });
  const firstCard = board.locator('.board-card-title').first();
  await firstCard.click();
  await details.waitFor();
  await handle.press('End');
  await details.getByTestId('details-resize-handle').press('End');
  const mainSize = await page.locator('.project-main').boundingBox();
  const boardSize = await board.boundingBox();
  assert.ok(mainSize.width >= 320 && boardSize.height > 150, 'Both desktop panes must leave usable board space');
  await details.getByTestId('task-detail-close-button').click();
  await details.waitFor({ state: 'hidden' });
  await toolbar.getByTestId('reset-project-layout').click();

  await page.setViewportSize({ width: 390, height: 440 });
  await requestsSummary.click();
  await requests.getByRole('button', { name: 'Refresh saved details' }).click({ trial: true });
  await requests.getByRole('button', { name: 'Close requests & plans' }).click();
  await requests.locator('.request-workspace-content').waitFor({ state: 'hidden' });
  assert.ok(await requestsSummary.evaluate(element => document.activeElement === element));

  await page.setViewportSize({ width: 390, height: 844 });
  await outline.waitFor({ state: 'hidden' });
  const mobileOutlineToggle = toolbar.getByTestId('toggle-project-outline-button');
  await mobileOutlineToggle.press('Enter');
  await outline.waitFor();
  await page.waitForFunction(() => document.activeElement?.dataset.testid === 'close-project-outline-button');
  await page.keyboard.press('Escape');
  await outline.waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.activeElement?.dataset.testid === 'toggle-project-outline-button');
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth), 390,
    'Horizontal scrolling should stay in the board at phone width');
  assert.deepEqual(pageErrors, []);
  console.log('Project board layout browser checks passed');
} finally {
  await browser.close();
}
