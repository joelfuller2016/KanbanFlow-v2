// Playwright CLI: open a disposable local app, then run-code --filename=<this file>.
// Creates a labeled fixture project, tests the real rendered controls, and deletes only that project.
async page => {
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const base = await page.evaluate(() => location.origin);
  const api = async (method, path, data, expected = 200) => {
    const response = await page.request.fetch(base + path, { method, data });
    check(response.status() === expected, `${method} ${path}: ${response.status()} ${await response.text()}`);
    return expected === 204 ? null : response.json();
  };
  const open = async (trigger, target) => {
    for (let attempt = 0; attempt < 4; attempt++) {
      await trigger.click();
      try { await target.waitFor({ state: 'visible', timeout: 2000 }); return; }
      catch (error) { if (attempt === 3) throw error; }
    }
  };
  const stamp = Date.now();
  const description = '<!-- kanbanflow-sync -->\n**Issue:** https://github.com/octo/board/issues/42\n**Open PR:** https://github.com/octo/board/pull/43\n<!-- /kanbanflow-sync -->';
  const project = await api('POST', '/api/projects', { name: `PR506 browser fixture ${stamp}`, description }, 201);
  try {
    const feature = await api('POST', `/api/projects/${project.id}/features`, { name: 'Review fixtures', description }, 201);
    const tasks = [];
    for (const name of ['Legacy', 'Quarantined', 'Verified']) {
      tasks.push(await api('POST', `/api/features/${feature.id}/tasks`, { name: `${name} issue fixture`, description, status: 'Todo', actor: 'browser-fixture' }, 201));
    }
    const [legacy, quarantined, verified] = tasks;
    const identity = { provider: 'GitHub', repositoryExternalId: `R_browser_${stamp}`, repositoryOwner: 'octo', repositoryName: 'board', actor: 'browser-fixture' };
    await api('POST', `/api/tasks/${quarantined.id}/external-work-item-quarantines/github`, { ...identity, candidateNumber: 42, candidateUrl: 'https://github.com/octo/board/issues/42', reason: 'Ambiguous identity requires a human decision.' });
    await api('PUT', `/api/tasks/${verified.id}/external-work-item-links/github`, { ...identity, externalNodeId: `I_browser_${stamp}`, externalNumber: 44, url: 'https://github.com/octo/board/issues/44' });
    const go = () => page.goto(`${base}/project/${project.id}`);
    const link = async (locator, number, surface) => {
      await locator.waitFor({ state: 'visible' });
      check(await locator.getAttribute('href') === `https://github.com/octo/board/issues/${number}`, `${surface}: canonical identity URL`);
      check(await locator.getAttribute('target') === '_blank', `${surface}: open in a new tab`);
      check(await locator.getAttribute('rel') === 'noopener noreferrer', `${surface}: opener isolation`);
    };
    await go();
    await page.getByTestId(`board-card-${verified.id}`).waitFor({ state: 'visible' });
    await link(page.getByTestId(`board-card-${verified.id}-github-link`), 44, 'verified card');
    await link(page.getByTestId(`board-card-${legacy.id}-github-link`), 42, 'legacy card');
    check(await page.getByTestId(`board-card-${quarantined.id}-github-link`).count() === 0, 'quarantine card must suppress the legacy fallback');
    for (const [task, expectedNumber] of [[legacy, 42], [quarantined, null], [verified, 44]]) {
      await go();
      await open(page.getByTestId(`board-card-${task.id}-title`), page.getByTestId('task-detail-panel'));
      if (expectedNumber === null) {
        check(await page.getByTestId('task-detail-github-link').count() === 0, 'quarantine detail must suppress the legacy fallback');
        check((await page.getByTestId('task-detail-description-read').textContent()).includes('https://github.com/octo/board/issues/42'), 'quarantine retains the full source description');
      } else {
        await link(page.getByTestId('task-detail-github-link'), expectedNumber, `${task.name} detail`);
      }
      await go();
      await open(page.getByTestId(`outline-task-${task.id}`), page.getByTestId('edit-item-modal'));
      if (expectedNumber === null) {
        check(await page.getByTestId('edit-item-github-link').count() === 0, 'quarantine editor must suppress the legacy fallback');
      } else {
        await link(page.getByTestId('edit-item-github-link'), expectedNumber, `${task.name} editor`);
      }
    }
    check(await page.getByTestId('edit-item-github-pr-link').getAttribute('href') === 'https://github.com/octo/board/pull/43', 'task PR positive control');
    for (const [testId, mode] of [['edit-project-button', 'Project'], [`outline-feature-${feature.id}`, 'Feature']]) {
      await go();
      await open(page.getByTestId(testId), page.getByTestId('edit-item-modal'));
      check(await page.getByTestId('edit-item-github-link').count() === 0, `${mode} must not render a task issue control`);
      check(await page.getByTestId('edit-item-github-pr-link').count() === 0, `${mode} must not render a task PR control`);
    }
    return { passed: true, legacyCardDetailEditor: true, verifiedCardDetailEditor: true, quarantinedCardDetailEditor: true, sourceRetained: true, projectFeatureControls: true };
  } finally {
    await page.goto(base);
    await api('DELETE', `/api/projects/${project.id}`, undefined, 204);
  }
}
