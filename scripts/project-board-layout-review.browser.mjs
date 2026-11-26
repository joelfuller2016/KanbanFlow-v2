import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const url = new URL(process.argv[2]);
assert.equal(url.hostname, '127.0.0.1', 'Use a disposable seeded board');
assert.notEqual(url.port, '5123', 'Do not run with the normal board');
assert.match(url.pathname, /^\/project\/\d+$/);

const browser = await chromium.launch();
const failures = [];

async function test(name, viewport, run) {
  const page = await browser.newPage({ viewport });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(url.href);
    await page.waitForFunction(() => window.KanbanFlow?._boardSortables?.some(sortable => sortable.el?.isConnected));
    await run(page);
    assert.deepEqual(errors, []);
    console.log(`PASS ${name}`);
  } catch (error) {
    failures.push(error);
    console.error(`FAIL ${name}: ${error.message}`);
  } finally {
    await page.close();
  }
}

async function closeDetails(page) {
  const details = page.getByTestId('task-detail-panel');
  await details.getByTestId('task-detail-close-button').click();
  await details.waitFor({ state: 'hidden' });
}

function isInViewport(element) {
  const bounds = element.getBoundingClientRect();
  return bounds.top >= 0 && bounds.top < window.innerHeight && bounds.bottom > 0;
}

async function waitForReveal(page, locator) {
  await page.waitForFunction(isInViewport, await locator.elementHandle(), { timeout: 3000 });
}

try {
  await test('Outline history reveals offscreen rows and retains visible control focus', { width: 1280, height: 800 }, async page => {
    const outline = page.getByTestId('project-outline');
    const titles = outline.locator('button.outline-task-title');
    const first = titles.first();
    const deep = titles.last();
    await deep.click();
    await closeDetails(page);
    await first.click();
    await closeDetails(page);
    assert.equal(await deep.evaluate(isInViewport), false, 'The Previous target must start outside the viewport');

    const previous = outline.getByTestId('outline-back-button');
    const next = outline.getByTestId('outline-forward-button');
    await previous.press('Enter');
    await deep.locator('xpath=self::*[@aria-current="true"]').waitFor();
    await waitForReveal(page, deep);
    assert.equal(await previous.evaluate(element => element === document.activeElement), true,
      'Previous lost focus when it reached the end of the history');
    assert.equal(await previous.evaluate(isInViewport), true, 'The focused history button scrolled out of view');

    await next.press('Enter');
    await first.locator('xpath=self::*[@aria-current="true"]').waitFor();
    await waitForReveal(page, first);
    assert.equal(await next.evaluate(element => element === document.activeElement), true,
      'Next lost focus when it reached the end of the history');
  });

  await test('Details owns the mobile drawer after crossing the desktop breakpoint', { width: 390, height: 844 }, async page => {
    const outline = page.getByTestId('project-outline');
    await page.getByTestId('toggle-project-outline-button').press('Enter');
    await page.waitForFunction(() => document.activeElement?.dataset.testid === 'close-project-outline-button');
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.waitForFunction(() => !document.getElementById('project-outline').hasAttribute('aria-modal'));

    const card = page.getByTestId('kanban-board').locator('.board-card-title').first();
    const cardId = await card.getAttribute('data-testid');
    await card.click();
    const details = page.getByTestId('task-detail-panel');
    await details.waitFor();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForFunction(() => document.getElementById('task-details').getAttribute('aria-modal') === 'true');
    assert.equal(await outline.isVisible(), false, 'The stale Outline drawer reappeared behind Details');
    assert.equal(await outline.getAttribute('aria-modal'), null);

    await page.keyboard.press('Tab');
    assert.equal(await details.evaluate(element => element.contains(document.activeElement)), true);
    await page.keyboard.press('Escape');
    await details.waitFor({ state: 'hidden' });
    await page.waitForFunction(id => document.activeElement?.dataset.testid === id, cardId);
  });

  await test('A long, full title leaves usable Details fields at 390 by 440', { width: 390, height: 440 }, async page => {
    const card = page.getByTestId('kanban-board').locator('.board-card-title');
    const { testId, title } = await card.evaluateAll(items => {
      const longest = items.reduce((first, second) => first.title.length >= second.title.length ? first : second);
      return { testId: longest.dataset.testid, title: longest.title };
    });
    assert.ok(title.length > 150, 'Use a seeded project with a long task title');
    await page.getByTestId(testId).click();
    const details = page.getByTestId('task-detail-panel');
    await details.waitFor();
    await page.waitForFunction(() => document.getElementById('task-details').getAttribute('aria-modal') === 'true');
    assert.equal((await details.locator('#task-detail-title').textContent()).trim(), title);
    const body = details.locator('#task-detail-content');
    const bodyHeight = await body.evaluate(element => element.clientHeight);
    assert.ok(bodyHeight >= 180, `A long title left just ${bodyHeight}px for the Details fields`);

    const navigation = details.getByRole('navigation', { name: 'Task sections' });
    await navigation.getByRole('button', { name: 'Description' }).click();
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Description');
    await navigation.getByRole('button', { name: 'Discussion' }).click();
    await page.waitForFunction(() => document.activeElement?.dataset.testid === 'task-comments-section');
    const comment = details.getByTestId('task-comment-input');
    await comment.fill('A short viewport still accepts a discussion draft');
    assert.equal(await comment.evaluate(isInViewport), true);
    await page.keyboard.press('Escape');
    await details.waitFor({ state: 'hidden' });
  });
} finally {
  await browser.close();
}

assert.equal(failures.length, 0, 'Board layout review scenarios failed');
