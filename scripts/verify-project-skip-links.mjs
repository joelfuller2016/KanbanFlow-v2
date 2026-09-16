// Standalone Chromium checks of the real module/styles; no app/server/database is started.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const modulePath = process.argv[2] || new URL('../src/KanbanFlow/wwwroot/js/project-skip-links.js', import.meta.url);
const cssPath = process.argv[3] || new URL('../src/KanbanFlow/wwwroot/app.css', import.meta.url);
const script = await readFile(modulePath, 'utf8');
const css = await readFile(cssPath, 'utf8');
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  await page.setContent(`<!doctype html><html><body>
    <a id="skip" class="project-skip-link" href="#board-filter-search"
       data-project-skip-target="board-filter-search">Skip to search</a>
    <main id="project-content" tabindex="-1"><input id="board-filter-search"></main>
    <div id="dialog" aria-modal="true" style="position:fixed;left:300px;top:100px">
      <button id="inside">Dialog action</button>
    </div></body></html>`);
  await page.addStyleTag({ content: css });
  await page.addScriptTag({ content: script });
  await page.locator('#inside').focus();
  await page.locator('#skip').click(); // Real pointerdown/default focus/click, not dispatchEvent.
  assert.equal(await page.evaluate(() => document.activeElement.id), 'inside',
    'pointer activation must not steal focus from a visible custom dialog');
  assert.equal(new URL(page.url()).hash, '');
  console.log('PASS: real pointer activation stays within a visible dialog.');

  await page.evaluate(() => {
    document.querySelector('#skip').focus();
    const backdrop = document.createElement('div');
    backdrop.style.cssText = 'position:fixed;inset:0;z-index:1040;background:rgba(0,0,0,.2)';
    backdrop.id = 'backdrop';
    document.body.append(backdrop);
  });
  const point = await page.locator('#skip').boundingBox();
  assert.ok(point);
  assert.equal(await page.evaluate(({x,y}) => document.elementFromPoint(x + 2,y + 2)?.id, point), 'backdrop',
    'Bootstrap modal/offcanvas backdrops must cover even a focused skip link');
  await page.locator('#inside').focus();
  await page.mouse.click(point.x + 2, point.y + 2);
  assert.notEqual(await page.evaluate(() => document.activeElement.id), 'skip');
  console.log('PASS: skip links remain below the modal backdrop.');

  await page.evaluate(() => {
    document.querySelector('#backdrop').remove();
    document.querySelector('#dialog').style.display = 'none';
  });
  await page.locator('#skip').click();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'board-filter-search');
  assert.equal(new URL(page.url()).hash, '');
  await page.evaluate(() => document.querySelector('#board-filter-search').remove());
  await page.locator('#skip').click();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'project-content');
  console.log('PASS: normal activation and missing-target main fallback.');
} finally {
  await browser.close();
}
