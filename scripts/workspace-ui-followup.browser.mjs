// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-followup.browser.mjs
// Disposable local app only. Complete raw entries are compared directly; output reports checks and lengths.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, text) => { if (!condition) throw new Error(text); };
  check(['127.0.0.1', 'localhost', '[::1]'].includes(hostname), 'Use an explicit disposable loopback application.');
  const prefix = `continue-fixed-${Date.now()}`;
  let sequence = 0;
  const checks = [], summary = {}, pageErrors = [];
  const onError = error => pageErrors.push(error.message);
  page.on('pageerror', onError);
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 20000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const context = key => api('GET', `/api/workspaces/${encodeURIComponent(key)}/context`);
  const command = async (key, action, payload, override = {}) => api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, {
    operationId: `${prefix}-${++sequence}`, expectedRevision: (await context(key)).revision,
    action, payload, actor: 'continue-fixed-fixture', ...override,
  });
  const create = title => api('POST', '/api/workspaces', { operationId: `${prefix}-${++sequence}`, actor: 'continue-fixed-fixture', title: `${prefix} ${title}` });
  const add = (key, id) => command(key, 'create_component', { component: { id, type: 'note', title: `${prefix} ${id}`, content: `Saved ${id}` }, placement: { id: `${id}-placement`, x: 900, y: 50, zIndex: 20 } });
  const route = key => `/ideas/workspace/${key.replace(':', '/')}`;
  const ready = key => page.waitForFunction(key => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    return shell?.dataset.workspaceId === key && shell.dataset.ready === 'true';
  }, key, { timeout: 20000 });
  const open = async key => { await page.goto(origin + route(key)); await ready(key); };
  const storage = key => page.evaluate(key => sessionStorage.getItem('kanbanflow.workspace.draft.v1:' + key), key);
  const paste = async (locator, value) => {
    await locator.evaluate((element, value) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error('Full-text paste needs a real textarea.');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(element, value);
      element.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertFromPaste', data: value }));
    }, value);
    check(await locator.inputValue() === value, 'Complete text must remain in the actual textarea.');
  };
  const fullText = label => (`\n  ## ${label} Ω 漢字 🧭\n\n    preserve indentation and trailing spaces  \n`).repeat(650) + '\nFINAL COMPLETE REQUIREMENT Ω  \n';
  const waitStored = async (key, predicate, detail) => {
    for (let n = 0; n < 100; n++) { const raw = await storage(key); if (raw && predicate(JSON.parse(raw))) return raw; await page.waitForTimeout(100); }
    throw new Error('Stored draft did not reach ' + detail);
  };
  const refresh = async key => {
    const current = await context(key);
    await page.getByTestId('workspace-refresh').click();
    await page.waitForFunction(revision => Number(document.querySelector('[data-testid="workspace-shell"]')?.dataset.revision) === revision, current.revision);
  };
  const switchTo = async key => {
    await page.getByTestId('workspace-switcher').selectOption(key);
    await page.waitForURL(origin + route(key));
    await ready(key);
  };
  try {
    // A: real committed receipt; only browser failure/storage boundaries are controlled.
    const a = await create('retry valid stored draft');
    await add(a.workspaceId, 'stored-old');
    await open(a.workspaceId);
    await page.getByTestId('workspace-view-outline').click();
    await page.getByTestId('outline-component-stored-old').click();
    const oldText = fullText('Original stored human draft'), newText = fullText('New edits while identity initialization is blocked');
    await paste(page.getByTestId('component-content'), oldText);
    await waitStored(a.workspaceId, d => d.contentDrafts?.['stored-old']?.content === oldText, 'original full draft');
    const pendingCommand = { operationId: `${prefix}-actual-pending`, expectedRevision: (await context(a.workspaceId)).revision,
      action: 'update_component', payload: { componentId: 'stored-old', title: 'Original pending title', content: oldText }, actor: 'continue-fixed-fixture' };
    const committed = await api('POST', `/api/workspaces/${encodeURIComponent(a.workspaceId)}/commands`, pendingCommand);
    await page.evaluate(({ key, pending }) => {
      const draftKey = 'kanbanflow.workspace.draft.v1:' + key;
      const draft = JSON.parse(sessionStorage.getItem(draftKey));
      draft.pending = { command: pending, draftSection: 'content:stored-old', draftAtSend: draft.contentDrafts['stored-old'] };
      sessionStorage.setItem(draftKey, JSON.stringify(draft));
      sessionStorage.setItem('continue-review-fail-identity-once', 'yes');
    }, { key: a.workspaceId, pending: pendingCommand });
    const beforeRaw = await storage(a.workspaceId);
    await page.addInitScript(() => {
      const original = Storage.prototype.getItem;
      Storage.prototype.getItem = function(key) {
        if (this === localStorage && key === 'kanbanflow.workspace.client.v1' && original.call(sessionStorage, 'continue-review-fail-identity-once') === 'yes') {
          sessionStorage.removeItem('continue-review-fail-identity-once');
          throw new DOMException('Controlled first identity read failure', 'SecurityError');
        }
        return original.call(this, key);
      };
    });
    await page.reload();
    await page.getByTestId('workspace-storage-error').waitFor({ state: 'visible' });
    const firstError = await page.getByTestId('workspace-storage-error').textContent();
    check(await storage(a.workspaceId) === beforeRaw, 'Initial identity failure must retain the complete stored entry.');
    await page.getByTestId('component-title').fill('New title during blocked initialization');
    await paste(page.getByTestId('component-content'), newText);
    const contextBeforeRecovery = JSON.stringify(await context(a.workspaceId));
    await page.getByTestId('workspace-retry-draft').click();
    await page.waitForFunction(first => {
      const error = document.querySelector('[data-testid="workspace-storage-error"]')?.textContent;
      return !!error && error !== first;
    }, firstError);
    check(await storage(a.workspaceId) === beforeRaw, 'Retry must not replace a valid unread draft or its pending command.');
    check(await page.getByTestId('component-content').inputValue() === newText, 'Retry must retain complete new in-memory typing.');
    check(await page.getByTestId('workspace-shell').getAttribute('data-ready') === 'false', 'Conflicting restoration must keep writes paused.');
    checks.push('valid unread stored draft and pending command remain intact through Retry with newer in-memory edits');
    await page.getByTestId('workspace-preserve-draft').click();
    await ready(a.workspaceId);
    const backupKey = await page.getByTestId('workspace-draft-backup-key').textContent();
    const backupRaw = await page.evaluate(key => sessionStorage.getItem(key), backupKey);
    check(backupRaw === beforeRaw && await page.getByTestId('workspace-preserved-draft-raw').textContent() === beforeRaw,
      'Explicit recovery must expose and back up the complete original valid draft and immutable pending envelope.');
    check(await page.getByTestId('component-title').inputValue() === 'New title during blocked initialization'
      && await page.getByTestId('component-content').inputValue() === newText, 'Explicit recovery must retain all current human typing.');
    await waitStored(a.workspaceId, d => d.contentDrafts?.$new?.content === newText, 'retained new draft after explicit recovery');
    const replayed = await api('POST', `/api/workspaces/${encodeURIComponent(a.workspaceId)}/commands`, JSON.parse(backupRaw).pending.command);
    check(replayed.replayed === true && replayed.operationId === pendingCommand.operationId && replayed.activityId === committed.activityId,
      'The preserved exact operation must recover its original committed receipt.');
    check(JSON.stringify(await context(a.workspaceId)) === contextBeforeRecovery, 'Browser recovery and receipt replay must not change saved workspace content.');
    summary.A = { workspaceId: a.workspaceId, originalCharacters: oldText.length, newCharacters: newText.length,
      originalPendingOperation: pendingCommand.operationId, originalRawRetainedThroughRetry: true, explicitBackupExact: true,
      newTypingRetained: true, realReceiptReplay: replayed.replayed, savedWorkspaceUnchanged: true };
    checks.push('explicit recovery backs up original pending intent, preserves complete newer typing and replays the actual receipt without a new write');

    // B: verify external association changes through Refresh, A/B/A cache and full reload.
    const projectA = await api('POST', '/api/projects', { name: `${prefix} project A` });
    const projectB = await api('POST', '/api/projects', { name: `${prefix} project B` });
    const featureA = await api('POST', `/api/projects/${projectA.id}/features`, { name: `${prefix} feature A` });
    const featureB = await api('POST', `/api/projects/${projectB.id}/features`, { name: `${prefix} feature B` });
    const acceptProject = async (project, itemId) => {
      const key = `project:${project.id}`;
      await command(key, 'propose_plan', { title: `${prefix} accepted ${itemId}`, items: [{ id: itemId, title: `${prefix} ${itemId}`, description: 'Complete project-specific scope', acceptanceCriteria: ['Verified'], dependsOn: [] }], expectedProjectPlanRevision: (await context(key)).projectPlanRevision });
      let current = await context(key);
      await command(key, 'accept_plan', { planRevisionId: current.document.planRevisions[current.document.planRevisions.length - 1].id, expectedProjectPlanRevision: current.projectPlanRevision });
      return (await context(key)).acceptedPlan.items[0].id;
    };
    const aItem = await acceptProject(projectA, 'scope-a'), bItem = await acceptProject(projectB, 'scope-b');
    const parking = await create('parking workspace');
    summary.B = [];
    for (const mode of ['refresh', 'cache', 'reload']) {
      const b = await create(`external association ${mode}`);
      await open(b.workspaceId);
      await page.getByTestId('workspace-tab-delivery').click();
      const authoredProjectName = `  Complete authored project name Ω ${prefix} ${mode}  `;
      const authoredFeatureName = mode === 'cache' ? `  Complete proposed feature name 漢字 ${prefix}  ` : '';
      await page.getByTestId('delivery-project-name').fill(authoredProjectName);
      await page.getByTestId('delivery-project-select').selectOption(String(projectA.id));
      await page.getByTestId('delivery-link-project').click();
      await page.waitForFunction(projectId => document.querySelector('[data-testid="delivery-open-project"]')?.getAttribute('href') === `/project/${projectId}`, projectA.id);
      await page.getByTestId(`delivery-select-${aItem}`).uncheck();
      await page.getByTestId(`delivery-select-${aItem}`).check();
      await page.getByTestId('delivery-preview').click();
      await page.getByTestId('delivery-preview-result').waitFor({ state: 'visible' });
      await page.getByTestId('delivery-feature-select').selectOption(String(featureA.id));
      if (authoredFeatureName) await page.getByTestId('delivery-feature-name').fill(authoredFeatureName);
      await waitStored(b.workspaceId, d => d.delivery?.selectedItemIds?.includes(aItem)
        && d.delivery.projectName === authoredProjectName && d.delivery.featureName === authoredFeatureName, 'project A scoped choices and complete authored names');
      if (mode === 'cache') await switchTo(parking.workspaceId);
      await command(b.workspaceId, 'link_project', { projectId: projectB.id, expectedCurrentProjectId: projectA.id });
      if (mode === 'cache') await switchTo(b.workspaceId);
      else if (mode === 'reload') { await page.reload(); await ready(b.workspaceId); }
      else await refresh(b.workspaceId);
      await page.getByTestId('workspace-tab-delivery').click();
      const bAfterRaw = await waitStored(b.workspaceId, d => d.delivery?.selectedItemIds?.length === 0 && d.delivery.featureId == null, 'cleared obsolete project choices');
      const delivery = JSON.parse(bAfterRaw).delivery;
      check(delivery.projectName === authoredProjectName && delivery.featureName === authoredFeatureName, 'Association reconciliation must preserve complete authored project/feature names.');
      check(!await page.getByTestId(`delivery-select-${bItem}`).isChecked(), 'The new project items must require explicit review/selection.');
      check(await page.getByTestId('delivery-preview').isDisabled(), 'Publication preview must not silently include new-project items.');
      check(await page.getByTestId(`delivery-select-${aItem}`).count() === 0, 'The old project item must not be rendered as a current project choice.');
      await page.getByTestId(`delivery-select-${bItem}`).check();
      const beforePreviewContext = JSON.stringify(await context(b.workspaceId));
      await page.getByTestId('delivery-preview').click();
      await page.getByTestId('delivery-preview-result').waitFor({ state: 'visible' });
      check(await page.getByTestId('delivery-error').count() === 0, 'Selecting the current project item must produce a valid preview without hidden obsolete IDs.');
      check((await context(b.workspaceId)).projectId === projectB.id && JSON.stringify(await context(b.workspaceId)) === beforePreviewContext,
        'The valid new-project preview must remain read-only.');
      if (!authoredFeatureName) await page.getByTestId('delivery-feature-select').selectOption(String(featureB.id));
      summary.B.push({ mode, workspaceId: b.workspaceId, oldProject: projectA.id, currentProject: projectB.id,
        obsoleteItemCleared: true, oldFeatureCleared: true, explicitNewSelectionRequired: true,
        authoredNamesExact: true, previewSucceededReadOnly: true });
      checks.push(`external project relink reconciles ${mode} restoration, preserves authored names and permits explicitly reviewed new-project preview`);
    }

    // C: recover an unselected dirty component after remote deletion, through both views and reload.
    const c = await create('orphaned dirty draft');
    await add(c.workspaceId, 'orphan-a'); await add(c.workspaceId, 'orphan-b');
    await open(c.workspaceId);
    await page.getByTestId('component-add').click();
    const independentNewTitle = `${prefix} independent unsaved new card`;
    const independentNewText = fullText('Independent unsaved new card must not be overwritten by recovery');
    await page.getByTestId('component-title').fill(independentNewTitle);
    await paste(page.getByTestId('component-content'), independentNewText);
    await waitStored(c.workspaceId, d => d.contentDrafts?.$new?.content === independentNewText && d.contentDrafts.$new.dirty,
      'independent full new-card draft before orphan recovery');
    await page.getByTestId('workspace-view-outline').click();
    await page.getByTestId('outline-component-orphan-a').click();
    const orphanText = fullText('Unsaved card A must remain reachable');
    await paste(page.getByTestId('component-content'), orphanText);
    await waitStored(c.workspaceId, d => d.contentDrafts?.['orphan-a']?.content === orphanText, 'dirty orphan candidate');
    await page.getByTestId('outline-component-orphan-b').click();
    await command(c.workspaceId, 'remove_component', { componentId: 'orphan-a' });
    await refresh(c.workspaceId);
    await page.getByTestId('workspace-removed-drafts').waitFor({ state: 'visible' });
    check(await page.getByTestId('removed-draft-orphan-a').count() === 1, 'Outline view must expose every removed dirty draft.');
    await page.getByTestId('workspace-view-canvas').click();
    check(await page.getByTestId('removed-draft-orphan-a').isVisible(), 'Canvas view must expose the same removed dirty draft recovery.');
    await page.reload(); await ready(c.workspaceId);
    await page.getByTestId('removed-draft-orphan-a').click();
    await page.waitForFunction(text => document.querySelector('[data-testid="component-content"]')?.value === text, orphanText);
    check(await page.getByTestId('component-content').inputValue() === orphanText, 'Recovered orphan selection must expose every unsaved character.');
    check((await page.getByTestId('component-reapply').textContent()).includes('Keep draft as a new card'), 'The existing explicit keep-as-new action must be reachable.');
    const cBeforeRaw = await storage(c.workspaceId);
    await page.getByTestId('component-reapply').click();
    await page.getByTestId('workspace-error').waitFor({ state: 'visible' });
    const collisionError = await page.getByTestId('workspace-error').textContent();
    check(collisionError.toLowerCase().includes('save') && collisionError.toLowerCase().includes('new card'),
      'A new-card collision must clearly tell the human to save the existing new draft first.');
    const afterBlockedRaw = await storage(c.workspaceId), afterBlocked = JSON.parse(afterBlockedRaw);
    check(afterBlockedRaw === cBeforeRaw, 'Blocking recovery must retain the entire stored draft and any pending operation unchanged.');
    check(afterBlocked.contentDrafts.$new.content === independentNewText && afterBlocked.contentDrafts.$new.title === independentNewTitle
      && afterBlocked.contentDrafts['orphan-a'].content === orphanText && afterBlocked.contentDrafts['orphan-a'].dirty,
      'Blocked keep-as-new must preserve both complete independent dirty drafts.');
    check(await page.getByTestId('component-content').inputValue() === orphanText, 'Keep-as-new must retain every unsaved character.');
    checks.push('keep-as-new collision pauses recovery and preserves both complete dirty drafts until the existing new card is saved');
    await page.getByTestId('component-add').click();
    await page.waitForFunction(({ title, text }) => document.querySelector('[data-testid="component-title"]')?.value === title
      && document.querySelector('[data-testid="component-content"]')?.value === text, { title: independentNewTitle, text: independentNewText });
    check(await page.getByTestId('component-title').inputValue() === independentNewTitle
      && await page.getByTestId('component-content').inputValue() === independentNewText,
      'The existing independent new draft must remain selectable without losing any text.');
    if (await page.getByTestId('component-reapply').count()) await page.getByTestId('component-reapply').click();
    await page.getByTestId('component-save').click();
    let savedIndependent;
    for (let n = 0; n < 100; n++) {
      savedIndependent = (await context(c.workspaceId)).document.components.find(item => item.title === independentNewTitle && item.content === independentNewText);
      if (savedIndependent) break;
      await page.waitForTimeout(100);
    }
    check(!!savedIndependent, 'The independent new card must save with complete original content before orphan recovery continues.');
    await page.getByTestId('removed-draft-orphan-a').click();
    await page.waitForFunction(text => document.querySelector('[data-testid="component-content"]')?.value === text, orphanText);
    await page.getByTestId('component-reapply').click();
    await page.waitForFunction(({ key, text }) => {
      const draft = JSON.parse(sessionStorage.getItem('kanbanflow.workspace.draft.v1:' + key));
      return draft?.selectedComponentId == null && draft.contentDrafts?.$new?.componentId == null && draft.contentDrafts.$new.content === text;
    }, { key: c.workspaceId, text: orphanText });
    check(await page.getByTestId('component-content').inputValue() === orphanText, 'Recovery after resolving the collision must retain the full orphan text.');
    await page.getByTestId('component-save').click();
    let recovered;
    for (let n = 0; n < 100; n++) {
      recovered = (await context(c.workspaceId)).document.components.find(item => item.id !== 'orphan-a' && item.title === `${prefix} orphan-a` && item.content === orphanText);
      if (recovered) break;
      await page.waitForTimeout(100);
    }
    check(!!recovered, 'Explicit recovery must create a distinct saved card with the complete original unsaved content.');
    const cContext = await context(c.workspaceId);
    check(!cContext.document.components.some(item => item.id === 'orphan-a') && cContext.document.components.some(item => item.id === 'orphan-b' && item.content === 'Saved orphan-b'),
      'Recovering the draft must not recreate the deleted identity or change another saved card.');
    check(cContext.document.components.some(item => item.id === savedIndependent.id && item.title === independentNewTitle && item.content === independentNewText),
      'Saving the recovered orphan must leave the independent new card unchanged.');
    summary.C = { workspaceId: c.workspaceId, orphanCharacters: orphanText.length, recoveredComponentId: recovered.id,
      independentNewCharacters: independentNewText.length, independentNewId: savedIndependent.id, collisionBlockedWithoutLoss: true,
      recoveryAvailableInBothViewsAndReload: true, completeTextSaved: true, deletedIdentityNotRecreated: true, otherSavedCardUnchanged: true };
    checks.push('a removed unselected dirty card remains recoverable in canvas/outline and after reload, then saves as a distinct complete card');
    check(pageErrors.length === 0, `Unexpected browser errors: ${pageErrors.join('; ')}`);
    return { result: 'PASS', origin, prefix, checks, summary, pageErrors };
  } finally {
    page.off('pageerror', onError);
  }
}
