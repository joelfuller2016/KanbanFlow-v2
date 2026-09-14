// Playwright CLI run-code. Use a disposable local app; the fixture project is deleted in finally.
async page => {
  const base = await page.evaluate(() => location.origin);
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data, expected = 200) => {
    const response = await page.request.fetch(base + path, { method, data });
    check(response.status() === expected, `${method} ${path}: ${response.status()} ${await response.text()}`);
    return expected === 204 ? null : response.json();
  };
  const project = await api('POST', '/api/projects', { name: `Issue507 browser fixture ${Date.now()}` }, 201);
  try {
    const feature = await api('POST', `/api/projects/${project.id}/features`, { name: 'Atomic imports' }, 201);
    const description = '\r\n    Complete user context Ω\r\n\r\n```text\r\n  preserve this  \r\n```\r\n\r\n';
    const request = { name: 'Original imported title', description, repositoryExternalId: `R_browser507_${project.id}`,
      repositoryOwner: 'octo', repositoryName: 'board', externalNodeId: `I_browser507_${project.id}`,
      externalNumber: 42, url: 'https://github.com/octo/board/issues/42', actor: 'browser-fixture' };
    const path = `/api/features/${feature.id}/external-work-item-projections/github`;
    const raw = await Promise.all([page.request.post(base + path, { data: request }), page.request.post(base + path, { data: request })]);
    check(raw.map(r => r.status()).sort().join(',') === '200,201', 'Concurrent HTTP calls must create once and converge');
    const results = await Promise.all(raw.map(r => r.json()));
    check(results[0].task.id === results[1].task.id && results[0].link.id === results[1].link.id, 'Both callers must return the same task/link');
    const id = results[0].task.id;
    await page.goto(`${base}/project/${project.id}`);
    const title = page.getByTestId(`board-card-${id}-title`);
    await title.waitFor({ state: 'visible' });
    check(await page.locator('.board-card[data-task-id]').count() === 1, 'Exactly one visible board card');
    check(await page.getByTestId(`board-card-${id}-github-link`).getAttribute('href') === request.url, 'The existing card displays the verified issue link');
    for (let attempt = 0; attempt < 4; attempt++) {
      await title.click();
      try { await page.getByTestId('task-detail-panel').waitFor({ state: 'visible', timeout: 1500 }); break; }
      catch (error) { if (attempt === 3) throw error; }
    }
    check(await page.getByTestId('task-detail-github-link').getAttribute('href') === request.url, 'Detail opens the same identity');
    await page.locator('h5#task-detail-title').click();
    const edit = page.locator('input#task-detail-title');
    await edit.fill('Human redirected this card');
    await edit.press('Tab');
    let current;
    for (let attempt = 0; attempt < 40; attempt++) {
      current = await api('GET', `/api/tasks/${id}`);
      if (current.name === 'Human redirected this card') break;
      await page.waitForTimeout(100);
    }
    check(current.name === 'Human redirected this card', 'The human edit must persist before retry');
    const replay = await api('POST', path, request);
    check(!replay.created && replay.task.name === 'Human redirected this card', 'Retry must retain the human decision');
    check(replay.task.description === description, 'Complete Markdown whitespace must survive create and human name edit');
    const activity = await api('GET', `/api/tasks/${id}/activity`);
    check(activity.length === 3, 'Creation pair plus one human edit, with no replay audit');
    check(activity.filter(event => event.eventType === 'TaskCreated').length === 1, 'One task creation audit');
    check(activity.filter(event => event.eventType === 'ExternalWorkItemLinked').length === 1, 'One link creation audit');
    const humanEdit = activity.find(event => event.eventType === 'TaskUpdated');
    check(humanEdit && JSON.parse(humanEdit.afterJson).name === 'Human redirected this card', 'The human title decision must be audited');
    await page.reload();
    await page.getByTestId(`board-card-${id}-title`).waitFor({ state: 'visible' });
    check((await page.getByTestId(`board-card-${id}-title`).innerText()).includes('Human redirected this card'), 'Same card shows the human title after reload');
    return { passed: true, concurrentOneCard: true, cardAndDetailIdentity: true, humanEditRetainedOnReplay: true, humanEditAudited: true, completeDescription: true };
  } finally {
    await page.goto(base);
    await api('DELETE', `/api/projects/${project.id}`, undefined, 204);
  }
}
