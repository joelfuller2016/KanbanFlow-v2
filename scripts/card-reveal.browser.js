// Playwright CLI: open a disposable KanbanFlow instance, then run-code --filename=<this file>.
// Creates a labeled fixture project in that instance. Never run this on the normal board.
async (page) => {
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const origin = await page.evaluate(() => location.origin);
  check(/^http:\/\/127\.0\.0\.1:\d+$/.test(origin), 'Use a local disposable app.');
  check(!origin.endsWith(':5123'), 'The normal board is excluded from fixture writes.');
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    check(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`);
    return response.status() === 204 ? null : response.json();
  };
  const marker = `PR487 browser ${Date.now()}`;
  const project = await api('POST', '/api/projects', { name: marker });
  const feature = await api('POST', `/api/projects/${project.id}/features`, { name: 'Card context' });
  const fullTitle = 'Review the complete GitHub issue and its linked task before changing the plan: ' + 'LongUnbrokenContextToken'.repeat(8);
  const issueUrl = 'https://github.com/joelfuller2016/KanbanFlow-v2/issues/487?filter=(open)#issuecomment-9.';
  const body = 'Opening context includes __init__, a negative -5 value, and the requested outcome.\n\n'
    + 'Complete description remains accessible inside this board. '.repeat(200) + '\n\nPR487-END-OF-COMPLETE-CONTENT';
  const description = '<!-- kanbanflow-sync -->\n**Issue:** ' + issueUrl + '\n**State:** OPEN\n'
    + '**Open PR:** none\n<!-- /kanbanflow-sync -->\n\n' + body;
  const task = await api('POST', `/api/features/${feature.id}/tasks`, {
    name: fullTitle, description, status: 'Todo', priority: 'High', actor: 'PR487 browser verification'
  });
  const blocker = await api('POST', `/api/features/${feature.id}/tasks`, {
    name: 'Required predecessor', status: 'Todo', actor: 'PR487 browser verification'
  });
  await api('POST', `/api/tasks/${task.id}/dependencies`, { dependsOnTaskItemId: blocker.id });
  const cardSelector = `[data-testid="board-card-${task.id}"]`;
  const titleSelector = `[data-testid="board-card-${task.id}-title"]`;
  const githubSelector = `[data-testid="board-card-${task.id}-github-link"]`;
  const revealSelector = `${cardSelector} .board-card-reveal`;
  const readMetrics = async (targetPage) => targetPage.locator(cardSelector).evaluate(card => {
    const title = card.querySelector('.board-card-title-text');
    const reveal = card.querySelector('.board-card-reveal');
    const badge = card.querySelector('.board-card-blocked-badge');
    const link = card.querySelector('.board-card-gh');
    const rect = element => { const box = element.getBoundingClientRect(); return { x: box.x, y: box.y, right: box.right, bottom: box.bottom, width: box.width, height: box.height }; };
    return {
      card: rect(card), title: rect(title), titleScrollHeight: title.scrollHeight,
      lineHeight: parseFloat(getComputedStyle(title).lineHeight), clamp: getComputedStyle(title).webkitLineClamp,
      reveal: rect(reveal), opacity: getComputedStyle(reveal).opacity, transition: getComputedStyle(reveal).transitionDuration,
      badge: rect(badge), link: rect(link), outlineWidth: getComputedStyle(link).outlineWidth,
      outlineOffset: getComputedStyle(link).outlineOffset,
      hasHover: matchMedia('(hover: hover)').matches, hasNoHover: matchMedia('(hover: none)').matches
    };
  });
  const metrics = async (targetPage) => {
    // InteractiveServer can replace the prerendered element after waitFor(visible) resolved.
    // Reacquire that element rather than interpreting a detached node's empty computed style.
    for (let attempt = 0; attempt < 20; attempt++) {
      const value = await readMetrics(targetPage);
      if (value.card.width > 0 && value.lineHeight > 0) return value;
      await targetPage.waitForTimeout(50);
    }
    throw new Error('Card did not settle into a connected, styled render.');
  };
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`${origin}/project/${project.id}`);
  await page.locator(cardSelector).waitFor({ state: 'visible' });
  await page.mouse.move(1, 1);
  await page.evaluate(() => document.activeElement?.blur());
  await page.waitForFunction(selector => document.querySelector(selector)?.getBoundingClientRect().height < 1, revealSelector);
  const resting = await metrics(page);
  check(resting.hasHover && resting.clamp === '2', 'Desktop title must start at two lines.');
  check(Math.abs(resting.title.height - 2 * resting.lineHeight) < 2, 'The title must actually occupy two lines.');
  check(resting.titleScrollHeight > resting.title.height, 'The fixture must exercise a longer title.');
  check(resting.reveal.height < 1 && resting.opacity === '0', 'Desktop preview must collapse fully at rest.');
  await page.locator(titleSelector).hover();
  await page.waitForFunction(selector => getComputedStyle(document.querySelector(selector)).opacity === '1', revealSelector);
  const hovered = await metrics(page);
  check(hovered.title.height > resting.title.height && hovered.clamp !== '2', 'Hover must expose the complete title.');
  check(hovered.titleScrollHeight <= hovered.title.height + 1, 'Every title line must fit in the visible hover box.');
  check(hovered.reveal.height > 20, 'Hover must expose the description preview.');
  check(hovered.badge.right <= hovered.card.right && hovered.badge.width > 0, 'Long tokens must keep the Blocked badge inside the card.');
  const excerpt = await page.locator(`[data-testid="board-card-${task.id}-excerpt"]`).innerText();
  check(excerpt.startsWith('Opening context includes __init__, a negative -5 value'), 'Preview must show body context without corrupting identifiers.');
  check(!excerpt.includes('kanbanflow-sync') && !excerpt.includes('PR487-END-OF-COMPLETE-CONTENT'), 'Preview must omit metadata while leaving details accessible.');
  check(await page.locator(githubSelector).getAttribute('href') === issueUrl, 'Card href must retain query and fragment.');
  check(await page.locator(cardSelector).locator('a[href^="https://github.com/"]').count() === 1,
    'The existing shared badge must be the card’s only GitHub control.');

  await page.mouse.move(1, 1);
  await page.locator(titleSelector).focus();
  const focused = await metrics(page);
  check(focused.opacity === '1' && focused.transition === '0s', 'Keyboard focus must reveal content immediately.');
  await page.keyboard.press('Tab');
  check(await page.locator(githubSelector).evaluate(link => link === document.activeElement), 'Tab after the title must reach the GitHub link.');
  const keyboard = await metrics(page);
  check(keyboard.outlineWidth === '2px' && keyboard.outlineOffset === '-2px', 'The focus ring must be drawn within the clipped link.');
  check(keyboard.link.x >= keyboard.card.x && keyboard.link.bottom <= keyboard.card.bottom, 'The focused link must remain inside the visible card.');
  await page.context().route('https://github.com/**', route => route.fulfill({ contentType: 'text/html', body: '<title>GitHub navigation fixture</title>' }));
  const popupPromise = page.waitForEvent('popup');
  await page.keyboard.press('Enter');
  const popup = await popupPromise;
  await popup.waitForLoadState();
  check(popup.url() === issueUrl, 'Keyboard activation must navigate to the exact GitHub destination.');
  await popup.close();
  await page.waitForTimeout(300);
  check(!await page.getByTestId('task-detail-panel').isVisible(), 'Activating the link must not open task details.');

  // This fixture class tests the global CSS state independently of pointer timing.
  await page.locator(cardSelector).evaluate(card => card.classList.add('kanban-ghost'));
  const desktopDrag = await metrics(page);
  check(desktopDrag.reveal.height < 1 && desktopDrag.clamp === '2' && desktopDrag.transition === '0s', 'Desktop drag suppression must immediately fix geometry.');
  await page.locator(cardSelector).evaluate(card => card.classList.remove('kanban-ghost'));

  // A real click proves the InteractiveServer callback and complete detail view still work.
  for (let attempt = 0; attempt < 10 && !await page.getByTestId('task-detail-panel').isVisible(); attempt++) {
    await page.locator(titleSelector).click();
    // An opening offcanvas can display its backdrop before its panel becomes visible. Wait for
    // the real transition so a second click cannot be intercepted by that opening backdrop.
    try { await page.getByTestId('task-detail-panel').waitFor({ state: 'visible', timeout: 2000 }); }
    catch (error) { if (attempt === 9) throw error; }
  }
  check(await page.getByTestId('task-detail-panel').isVisible(), 'Card title must open task details.');
  const detailText = await page.getByTestId('task-detail-description-read').innerText();
  check(detailText.includes('PR487-END-OF-COMPLETE-CONTENT'), 'Detail view must contain the full description tail.');
  check((await api('GET', `/api/tasks/${task.id}`)).description === description, 'Card rendering and navigation must preserve the complete stored description.');
  await page.getByTestId('task-detail-close-button').click();
  await page.getByTestId('task-detail-panel').waitFor({ state: 'hidden' });

  const outlineTitle = page.locator(`[data-testid="outline-task-${task.id}"] .outline-task-title-text`);
  if (!await outlineTitle.isVisible()) await page.getByTestId(`outline-feature-${feature.id}-toggle`).click();
  await outlineTitle.waitFor({ state: 'visible' });
  const outline = await outlineTitle.evaluate(title => ({
    height: title.getBoundingClientRect().height,
    lineHeight: parseFloat(getComputedStyle(title).lineHeight),
    fullText: title.textContent,
    tooltip: title.parentElement.title
  }));
  check(Math.abs(outline.height - 2 * outline.lineHeight) < 2, 'The outline title must also actually clamp to two lines.');
  check(outline.fullText === fullTitle && outline.tooltip === fullTitle, 'The outline must retain the complete task title.');
  await outlineTitle.click();
  // This branch's outline opens the existing edit modal; #511 routes it through task detail.
  // In either integration, assert the actual task/content and record which editor was used.
  await page.waitForFunction(() => ['edit-item-modal', 'task-detail-panel'].some(id => {
    const element = document.querySelector(`[data-testid="${id}"]`);
    return element && getComputedStyle(element).display !== 'none' && getComputedStyle(element).visibility !== 'hidden';
  }));
  const outlineEditor = await page.getByTestId('edit-item-modal').isVisible() ? 'modal' : 'task-detail';
  if (outlineEditor === 'modal') {
    check(await page.locator('#edit-item-name').inputValue() === fullTitle, 'The outline must open the same full task.');
    check((await page.getByTestId('edit-item-description-read').innerText()).includes('PR487-END-OF-COMPLETE-CONTENT'), 'The outline editor must preserve complete content.');
    check(await page.getByTestId('edit-item-github-link').getAttribute('href') === issueUrl, 'The modal chip must preserve the same full destination.');
    await page.getByTestId('edit-item-close-button').click();
    await page.getByTestId('edit-item-modal').waitFor({ state: 'hidden' });
  } else {
    check((await page.locator('#task-detail-title').innerText()) === fullTitle, 'The outline must open the same full task.');
    await page.getByTestId('task-detail-close-button').click();
    await page.getByTestId('task-detail-panel').waitFor({ state: 'hidden' });
  }

  const touchContext = await page.context().browser().newContext({ hasTouch: true, viewport: { width: 1440, height: 1000 } });
  let touchResting;
  let touchDrag;
  try {
    const touchPage = await touchContext.newPage();
    await touchPage.goto(`${origin}/project/${project.id}`);
    await touchPage.locator(cardSelector).waitFor({ state: 'visible' });
    touchResting = await metrics(touchPage);
    check(touchResting.hasNoHover, 'The touch case must execute in an actual no-hover media context.');
    check(touchResting.opacity === '1' && touchResting.reveal.height > 20 && touchResting.clamp !== '2', `Touch readers must see the title, preview and link without hovering: ${JSON.stringify(touchResting)}`);
    check(touchResting.titleScrollHeight <= touchResting.title.height + 1, 'Every title line must fit in the visible touch box.');
    check(touchResting.link.height >= 24, 'Touch link must have at least a 24px target.');
    await touchPage.locator(cardSelector).evaluate(card => card.classList.add('kanban-ghost'));
    touchDrag = await metrics(touchPage);
    check(Math.abs(touchResting.card.height - touchDrag.card.height) < 3, `Starting a touch drag must preserve card height apart from its existing 2px drag border: ${touchResting.card.height} -> ${touchDrag.card.height}`);
    check(Math.abs(touchResting.reveal.height - touchDrag.reveal.height) < 1, 'Touch drag must preserve the already-visible preview.');
  } finally {
    await touchContext.close();
  }
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.locator(titleSelector).hover();
  const reduced = await metrics(page);
  check(reduced.transition === '0s', 'Reduced motion must disable preview animation.');

  // Drive SortableJS with real pointer movement, then verify the persisted destination.
  await page.mouse.move(1, 1);
  await page.evaluate(() => document.activeElement?.blur());
  await page.waitForFunction(selector => document.querySelector(selector)?.getBoundingClientRect().height < 1, revealSelector);
  await page.locator(cardSelector).scrollIntoViewIfNeeded();
  const source = await page.locator(cardSelector).boundingBox();
  const target = await page.locator('.board-column-cards[data-status="Backlog"]').boundingBox();
  check(source && target, 'Drag source and target must be rendered.');
  const start = { x: source.x + source.width - 5, y: source.y + 8 };
  const end = { x: target.x + target.width / 2, y: target.y + Math.min(35, target.height / 2) };
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  let sawRealGhost = false;
  for (let step = 1; step <= 12; step++) {
    await page.mouse.move(start.x + (end.x - start.x) * step / 12, start.y + (end.y - start.y) * step / 12, { steps: 2 });
    sawRealGhost ||= await page.locator('.kanban-ghost').count() > 0;
    await page.waitForTimeout(25);
  }
  await page.waitForTimeout(60);
  await page.mouse.up();
  check(sawRealGhost, 'The pointer gesture must start an actual SortableJS drag.');
  await page.locator(`.board-column-cards[data-status="Backlog"] ${cardSelector}`).waitFor({ state: 'visible' });
  check((await api('GET', `/api/tasks/${task.id}`)).status === 'Backlog', 'The actual drag must persist the intended task status.');
  check((await api('GET', `/api/tasks/${task.id}`)).description === description, 'All display interactions must leave complete storage unchanged.');
  return { projectId: project.id, taskId: task.id, descriptionLength: description.length, resting, hovered, focused, keyboard, desktopDrag, outline, outlineEditor, touchResting, touchDrag, reducedMotion: reduced.transition, completeDetails: true, exactLinkNavigation: true, realDragPersisted: true };
}
