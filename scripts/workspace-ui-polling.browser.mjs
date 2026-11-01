// Disposable loopback application only. Real API changes and the unmodified 12-second poll.
// This checks the positive polling workflow; controlled async tests cover late stale-read races.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(['127.0.0.1', 'localhost', '[::1]'].includes(hostname), 'Use an explicitly disposable loopback application.');
  const prefix = `workspace-polling-${Date.now()}`;
  const checks = [], pageErrors = [];
  let sequence = 0;
  const onError = error => pageErrors.push(error.message);
  page.on('pageerror', onError);
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const context = key => api('GET', `/api/workspaces/${encodeURIComponent(key)}/context`);
  const command = async (key, action, payload) => api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, {
    operationId: `${prefix}-${++sequence}`, expectedRevision: (await context(key)).revision,
    actor: 'other-polling-api-caller', action, payload
  });
  const full = label => (`\n  ## ${label} Ω 漢字 🧭\n\n    Preserve full indentation and trailing spaces  \n`).repeat(650) + '\nFINAL COMPLETE POLLING REQUIREMENT Ω  \n';
  const stored = key => page.evaluate(key => JSON.parse(sessionStorage.getItem('kanbanflow.workspace.draft.v1:' + key)), key);
  const waitStored = async (key, predicate, detail) => {
    for (let n = 0; n < 100; n++) {
      const value = await stored(key);
      if (value && predicate(value)) return value;
      await page.waitForTimeout(100);
    }
    throw new Error('Browser draft did not reach ' + detail);
  };
  const waitEditor = (title, content) => page.waitForFunction(({ title, content }) =>
    document.querySelector('[data-testid="component-title"]')?.value === title
      && document.querySelector('[data-testid="component-content"]')?.value === content, { title, content }, { timeout: 15000 });
  const paste = async content => {
    await page.getByTestId('component-content').evaluate((element, content) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error('Complete text needs a real textarea.');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(element, content);
      element.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertFromPaste', data: content }));
    }, content);
    check(await page.getByTestId('component-content').inputValue() === content, 'The editor must retain the complete pasted buffer.');
  };
  const noticeAt = revision => page.waitForFunction(revision =>
    document.querySelector('[data-testid="workspace-new-changes"]')?.textContent.includes(`revision ${revision}.`), revision, { timeout: 30000 });
  const adoptedAt = revision => page.waitForFunction(revision =>
    Number(document.querySelector('[data-testid="workspace-shell"]')?.dataset.revision) === revision
      && !document.querySelector('[data-testid="workspace-new-changes"]'), revision, { timeout: 15000 });
  const currentSourceText = () => page.getByTestId('workspace-source-documents').locator(':scope > details').first().locator('pre.workspace-source-text').first().textContent();
  try {
    const originalSource = 'Original source body Ω\n    Keep source indentation and final spaces  \n';
    const created = await api('POST', '/api/workspaces', {
      operationId: `${prefix}-${++sequence}`, actor: 'polling-browser-fixture', title: prefix, body: originalSource
    });
    const key = created.workspaceId;
    const dirtyId = 'polling-dirty-card', dirtyPlacement = 'polling-dirty-placement';
    const cleanId = 'polling-clean-card', cleanPlacement = 'polling-clean-placement';
    for (const [id, placementId, title, content, x] of [
      [dirtyId, dirtyPlacement, 'Original dirty-capable card', 'Original saved dirty-capable content.', 30],
      [cleanId, cleanPlacement, 'Original clean card', 'Original clean saved content.', 70]
    ]) await command(key, 'create_component', { component: { id, type: 'note', title, content }, placement: { id: placementId, x, y: x + 10, width: 320, height: 220, zIndex: 20 } });
    const secondDirtyPlacement = 'polling-dirty-second-appearance';
    await command(key, 'add_placement', { placement: { id: secondDirtyPlacement, componentId: dirtyId, x: 900, y: 100, width: 320, height: 220, zIndex: 21 } });
    await page.goto(origin + `/ideas/workspace/${key.replace(':', '/')}?view=outline`);
    await page.waitForFunction(key => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return shell?.dataset.workspaceId === key && shell.dataset.ready === 'true';
    }, key, { timeout: 15000 });
    await page.getByTestId('workspace-view-canvas').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-canvas"]')?.dataset.ready === 'true');
    await page.getByTestId(`canvas-select-${dirtyPlacement}`).click();
    await waitEditor('Original dirty-capable card', 'Original saved dirty-capable content.');
    const activeAppearance = (active, inactive) => page.waitForFunction(({ active, inactive }) =>
      document.querySelector(`[data-testid="canvas-select-${active}"]`)?.getAttribute('aria-pressed') === 'true'
        && document.querySelector(`[data-testid="canvas-select-${inactive}"]`)?.getAttribute('aria-pressed') === 'false'
        && document.querySelectorAll('[data-testid^="canvas-select-"][aria-pressed="true"]').length === 1,
    { active, inactive }, { timeout: 15000 });
    await activeAppearance(dirtyPlacement, secondDirtyPlacement);
    checks.push('canvas selection exposes aria-pressed on exactly the active appearance of a component with two appearances');
    await page.getByTestId(`canvas-select-${secondDirtyPlacement}`).click();
    await page.waitForFunction(placement => document.querySelector('[data-testid="layout-placement"]')?.value === placement, secondDirtyPlacement);
    await activeAppearance(secondDirtyPlacement, dirtyPlacement);
    checks.push('selecting another appearance moves the pressed state without pressing both appearances of the shared component');
    await page.getByTestId('workspace-view-outline').click();
    await page.waitForFunction(({ dirtyId, cleanId }) =>
      document.querySelector(`[data-testid="outline-component-${dirtyId}"]`)?.getAttribute('aria-pressed') === 'true'
        && document.querySelector(`[data-testid="outline-component-${cleanId}"]`)?.getAttribute('aria-pressed') === 'false', { dirtyId, cleanId });
    await page.getByTestId(`outline-component-${cleanId}`).click();
    await waitEditor('Original clean card', 'Original clean saved content.');
    await page.waitForFunction(({ dirtyId, cleanId }) =>
      document.querySelector(`[data-testid="outline-component-${dirtyId}"]`)?.getAttribute('aria-pressed') === 'false'
        && document.querySelector(`[data-testid="outline-component-${cleanId}"]`)?.getAttribute('aria-pressed') === 'true'
        && document.querySelectorAll('[data-testid^="outline-component-"][aria-pressed="true"]').length === 1, { dirtyId, cleanId });
    await page.getByTestId('workspace-view-canvas').click(); await activeAppearance(cleanPlacement, dirtyPlacement);
    check(await page.getByTestId(`canvas-select-${secondDirtyPlacement}`).getAttribute('aria-pressed') === 'false',
      'Switching components must clear the pressed state of every appearance of the previous component.');
    await page.getByTestId('workspace-view-outline').click();
    checks.push('Outline announces only its selected component and selection stays consistent when returning to Canvas');
    check(await page.getByTestId('component-add').isEnabled(), 'The initial workspace must be writable before external changes.');
    await page.getByTestId(`outline-component-${dirtyId}`).click();
    await waitEditor('Original dirty-capable card', 'Original saved dirty-capable content.');
    const dirtyTitle = 'Complete unsaved existing card title', dirtyText = full('Unsaved existing card survives actual polling');
    await page.getByTestId('component-title').fill(dirtyTitle); await paste(dirtyText);
    await page.getByTestId('layout-x').fill('808'); await page.getByTestId('layout-x').press('Tab');
    await waitStored(key, value => value.contentDrafts?.[dirtyId]?.content === dirtyText && value.layoutDrafts?.[dirtyPlacement]?.x === 808,
      'complete existing content and unsaved layout');
    const newTitle = 'Complete independent unsaved new card', newText = full('Independent new card survives both automatic polls');
    await page.getByTestId('component-add').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-component-editor"] > h2')?.textContent.trim() === 'Add a component');
    await page.getByTestId('component-title').fill(newTitle); await paste(newText);
    await waitStored(key, value => value.contentDrafts?.$new?.content === newText, 'complete independent new card');
    await page.getByTestId(`outline-component-${cleanId}`).click();
    await waitEditor('Original clean card', 'Original clean saved content.');
    const baseline = await context(key), beforePoll = await stored(key);
    const exactDirty = value => JSON.stringify({ content: value.contentDrafts[dirtyId], fresh: value.contentDrafts.$new, layout: value.layoutDrafts[dirtyPlacement] });
    const dirtySnapshot = exactDirty(beforePoll);
    const cleanTitle = 'Collaborator saved clean card', cleanText = full('Current collaborator clean content');
    const remoteDirtyText = 'Collaborator saved a different existing-card version.';
    await command(key, 'update_component', { componentId: dirtyId, title: 'Collaborator dirty-card replacement', content: remoteDirtyText });
    await command(key, 'update_component', { componentId: cleanId, title: cleanTitle, content: cleanText });
    await command(key, 'move_components', { placements: [
      { placementId: dirtyPlacement, x: 350, y: 360 }, { placementId: cleanPlacement, x: 650, y: 660 }
    ] });
    const remote = await context(key), firstPollStarted = Date.now();
    await noticeAt(remote.revision);
    const firstNoticeMilliseconds = Date.now() - firstPollStarted;
    check(Number(await page.getByTestId('workspace-shell').getAttribute('data-revision')) === baseline.revision
      && await page.getByTestId('component-add').isDisabled()
      && exactDirty(await stored(key)) === dirtySnapshot, 'Automatic notice must leave the loaded revision and every dirty buffer unchanged until adoption.');
    await waitEditor('Original clean card', 'Original clean saved content.');
    checks.push('real automatic polling discovers external API changes and requires explicit adoption without replacing the loaded context or drafts');

    await page.getByTestId('workspace-load-latest').click(); await adoptedAt(remote.revision);
    await waitEditor(cleanTitle, cleanText);
    const afterAdopt = await stored(key);
    check(afterAdopt.contentDrafts[cleanId].dirty === false && afterAdopt.contentDrafts[cleanId].baseRevision === remote.revision
      && afterAdopt.contentDrafts[cleanId].content === cleanText
      && Number(await page.getByTestId('layout-x').inputValue()) === 650
      && afterAdopt.layoutDrafts[cleanPlacement].baseRevision === remote.revision,
      'Adoption must update clean content and layout from the current saved context.');
    checks.push('Load latest updates the complete clean editor and layout to the remotely saved revision');
    check(exactDirty(afterAdopt) === dirtySnapshot, 'Adoption must retain complete dirty content, independent new-card content, and dirty layout metadata exactly.');
    await page.getByTestId(`outline-component-${dirtyId}`).click(); await waitEditor(dirtyTitle, dirtyText);
    check(Number(await page.getByTestId('layout-x').inputValue()) === 808
      && await page.getByTestId('component-draft-conflict').isVisible()
      && await page.getByTestId('layout-reapply').isVisible(), 'Older dirty content and layout must remain reachable with explicit conflicts.');
    await page.getByTestId('component-add').click(); await waitEditor(newTitle, newText);
    check(JSON.stringify((await context(key)).document) === JSON.stringify(remote.document), 'Adopting external changes must not write or recreate any server component.');
    checks.push('complete dirty existing/new-card buffers and dirty layout remain reachable with explicit revision conflicts and no server write');

    await page.getByTestId('workspace-tab-source').click();
    check(await currentSourceText() === originalSource, 'The loaded current source must match the original body before the legacy edit.');
    const changedSource = 'Changed legacy source body Ω\n' + full('Complete legacy body changed without a workspace command') + 'END COMPLETE SOURCE Ω';
    await api('PATCH', `/api/ideas/${key.split(':')[1]}`, { body: changedSource, actor: 'other-legacy-api-caller' });
    const changed = await context(key);
    check(changed.revision === remote.revision && changed.source.body === changedSource
      && JSON.stringify(changed.document) === JSON.stringify(remote.document), 'Legacy body edit must change Source without changing workspace revision or document.');
    const sourcePollStarted = Date.now();
    await noticeAt(changed.revision);
    const sourceNoticeMilliseconds = Date.now() - sourcePollStarted;
    check(await currentSourceText() === originalSource && exactDirty(await stored(key)) === dirtySnapshot,
      'Same-revision Source polling must announce the new source while retaining the loaded source and complete dirty drafts.');
    checks.push('automatic polling detects a legacy Source.Body change even when workspace revision stays the same');
    await page.getByTestId('workspace-load-latest').click(); await adoptedAt(changed.revision);
    await page.waitForFunction(text => document.querySelector('[data-testid="workspace-source-documents"] > details pre.workspace-source-text')?.textContent === text,
      changedSource, { timeout: 15000 });
    check(exactDirty(await stored(key)) === dirtySnapshot && JSON.stringify((await context(key)).document) === JSON.stringify(remote.document),
      'Same-revision source adoption must preserve every dirty buffer and must not rewrite the workspace document.');
    await waitEditor(newTitle, newText);
    check(pageErrors.length === 0, `Unexpected browser errors: ${pageErrors.join('; ')}`);
    checks.push('adopting the same-revision source displays its complete new body while preserving both full drafts and unsaved layout');

    // Restore a valid older Pending envelope which predates comparison-snapshot metadata.
    // Its original command really committed; newer human typing must survive acknowledgement.
    await page.getByTestId(`outline-component-${dirtyId}`).click(); await waitEditor(dirtyTitle, dirtyText);
    const pendingText = full('Older committed update whose browser acknowledgement was interrupted');
    const pendingCommand = {
      operationId: `${prefix}-legacy-pending-${++sequence}`, expectedRevision: (await context(key)).revision,
      action: 'update_component', payload: { componentId: dirtyId, title: 'Older committed pending title', content: pendingText },
      actor: 'legacy-pending-browser-fixture'
    };
    const committed = await api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, pendingCommand);
    const immutablePending = JSON.stringify(pendingCommand);
    const newerText = full('New human edits made after the older pending update committed');
    await paste(newerText);
    const beforeLegacyReload = await waitStored(key, value => value.contentDrafts?.[dirtyId]?.content === newerText
      && value.contentDrafts?.$new?.content === newText && value.layoutDrafts?.[dirtyPlacement]?.x === 808,
    'complete newer edit with independent new-card and layout buffers');
    const newerSnapshot = exactDirty(beforeLegacyReload);
    await page.evaluate(({ key, command, componentId }) => {
      const storageKey = 'kanbanflow.workspace.draft.v1:' + key;
      const value = JSON.parse(sessionStorage.getItem(storageKey));
      if (value.pending != null) throw new Error('Legacy fixture must not replace an existing pending operation.');
      value.pending = { command, draftSection: 'content:' + componentId };
      sessionStorage.setItem(storageKey, JSON.stringify(value));
    }, { key, command: pendingCommand, componentId: dirtyId });
    await page.reload();
    await page.waitForFunction(({ key, revision }) => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return shell?.dataset.workspaceId === key && shell.dataset.ready === 'true' && Number(shell.dataset.revision) === revision;
    }, { key, revision: committed.revision }, { timeout: 15000 });
    await waitEditor(dirtyTitle, newerText);
    const restoredPending = await stored(key);
    check(JSON.stringify(restoredPending.pending.command) === immutablePending && restoredPending.pending.draftAtSend == null
      && restoredPending.pending.draftSection === 'content:' + dirtyId && exactDirty(restoredPending) === newerSnapshot,
      'Reload must retain the immutable legacy operation without inventing acknowledgement comparison metadata or changing newer full buffers.');
    checks.push('legacy Pending without DraftAtSend restores its exact command alongside complete newer active/new-card/layout buffers');
    const savedBeforeAcknowledgement = JSON.stringify(await context(key));
    await page.getByTestId('workspace-retry-pending').click();
    await page.waitForFunction(() => !document.querySelector('[data-testid="workspace-pending-command"]'), null, { timeout: 15000 });
    const acknowledged = await waitStored(key, value => value.pending == null && value.contentDrafts?.[dirtyId]?.content === newerText,
      'legacy acknowledgement retaining the complete newer active buffer');
    await waitEditor(dirtyTitle, newerText);
    const replay = await api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, pendingCommand);
    check(exactDirty(acknowledged) === newerSnapshot && JSON.stringify(await context(key)) === savedBeforeAcknowledgement
      && replay.replayed && replay.activityId === committed.activityId,
      'Acknowledging a legacy operation with no snapshot must preserve all newer buffers and replay the original receipt without another server change.');
    check(pageErrors.length === 0, `Unexpected browser errors: ${pageErrors.join('; ')}`);
    checks.push('retrying the legacy Pending replays its original real receipt without a server mutation or loss of newer complete drafts');
    return { result: 'PASS', prefix, checks, pageErrors, summary: {
      workspaceId: key, baselineRevision: baseline.revision, remoteRevision: remote.revision, legacyBodyRevision: changed.revision,
      firstNoticeMilliseconds, sourceNoticeMilliseconds, cleanCharacters: cleanText.length,
      dirtyExistingCharacters: dirtyText.length, dirtyNewCharacters: newText.length, changedSourceCharacters: changedSource.length,
      exactDirtyBuffersRetained: true, savedDocumentUnchangedByAdoption: true,
      legacyPending: { operationId: pendingCommand.operationId, originalActivityId: committed.activityId,
        replayActivityId: replay.activityId, olderCommittedCharacters: pendingText.length, newerActiveCharacters: newerText.length,
        missingSnapshotRetained: true, exactNewerBuffersAfterAcknowledgement: true, serverContextUnchangedByReplay: true }
    } };
  } finally { page.off('pageerror', onError); }
}
