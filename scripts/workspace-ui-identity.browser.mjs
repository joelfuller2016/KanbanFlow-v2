// Disposable loopback app only. Real API/UI checks for caller-created IDs and browser draft identity.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(['127.0.0.1', 'localhost', '[::1]'].includes(hostname), 'Use an explicit disposable loopback application.');
  const prefix = `workspace-identity-${Date.now()}`;
  let sequence = 0;
  const checks = [], summary = {}, pageErrors = [];
  const onError = error => pageErrors.push(error.message);
  page.on('pageerror', onError);
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const raw = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${raw}`);
    return raw ? JSON.parse(raw) : null;
  };
  const context = key => api('GET', `/api/workspaces/${encodeURIComponent(key)}/context`);
  const create = title => api('POST', '/api/workspaces', { operationId: `${prefix}-${++sequence}`, actor: 'identity-browser-fixture', title: `${prefix} ${title}` });
  const envelope = async (key, action, payload) => ({ operationId: `${prefix}-${++sequence}`, expectedRevision: (await context(key)).revision, action, payload, actor: 'identity-browser-fixture' });
  const send = (key, command) => api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, command);
  const command = async (key, action, payload) => send(key, await envelope(key, action, payload));
  const add = (key, id, title, content) => command(key, 'create_component', {
    component: { id, type: 'note', title, content }, placement: { id: `${prefix}-placement-${++sequence}`, x: 1000, y: 50, zIndex: 30 }
  });
  const raw = key => page.evaluate(key => sessionStorage.getItem('kanbanflow.workspace.draft.v1:' + key), key);
  const waitStored = async (key, predicate, message) => {
    for (let n = 0; n < 100; n++) { const value = await raw(key); if (value && predicate(JSON.parse(value))) return value; await page.waitForTimeout(100); }
    throw new Error('Stored draft did not reach ' + message);
  };
  const ready = key => page.waitForFunction(key => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    return shell?.dataset.workspaceId === key && shell.dataset.ready === 'true';
  }, key, { timeout: 20000 });
  const open = async key => { await page.goto(origin + `/ideas/workspace/${key.replace(':', '/')}`); await ready(key); };
  const waitEditor = (title, content) => page.waitForFunction(({ title, content }) =>
    document.querySelector('[data-testid="component-title"]')?.value === title
      && document.querySelector('[data-testid="component-content"]')?.value === content, { title, content });
  const select = async (id, title, content) => {
    await page.getByTestId('workspace-view-outline').click();
    await page.getByTestId(`outline-component-${id}`).click();
    await waitEditor(title, content);
  };
  const paste = async content => page.getByTestId('component-content').evaluate((element, content) => {
    if (!(element instanceof HTMLTextAreaElement)) throw new Error('Full content needs a real textarea.');
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(element, content);
    element.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertFromPaste', data: content }));
  }, content);
  const settled = (key, revision) => page.waitForFunction(({ key, revision }) => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    const add = document.querySelector('[data-testid="component-add"]');
    return shell?.dataset.workspaceId === key && Number(shell.dataset.revision) === revision && !add?.disabled
      && !document.querySelector('[data-testid="workspace-pending-command"]');
  }, { key, revision }, { timeout: 20000 });
  const saved = async (key, predicate, message) => {
    for (let n = 0; n < 100; n++) { const value = await context(key); if (predicate(value)) { await settled(key, value.revision); return value; } await page.waitForTimeout(100); }
    throw new Error('Saved workspace did not reach ' + message);
  };
  const full = label => (`\n  ## ${label} Ω 漢字 🧭\n\n    Keep complete indentation and final spaces  \n`).repeat(650) + '\nFINAL COMPLETE IDENTITY REQUIREMENT Ω  \n';
  const editorHeading = () => page.getByTestId('workspace-component-editor').locator(':scope > h2').textContent();
  try {
    const primary = await create('literal and independent new card');
    const key = primary.workspaceId;
    const literalTitle = `${prefix} literal saved $new`, literalText = full('Saved literal $new');
    const newTitle = `${prefix} independent new card`, newText = full('Independent new-card buffer');
    await add(key, '$new', literalTitle, literalText);
    const ordinary = ['ordinary', '$new-like', 'content:$new'].map(id => ({ id, title: `${prefix} saved ${id}`, content: `\n  Complete caller-owned ${id} Ω  \n` }));
    for (const item of ordinary) await add(key, item.id, item.title, item.content);
    const originalCount = (await context(key)).document.components.length;
    await open(key);
    await select('$new', literalTitle, literalText);
    check((await editorHeading()).trim() === 'Edit component', 'A real saved $new must open as an existing component.');
    await waitStored(key, d => d.contentDrafts?.['']?.componentId === '$new' && d.contentDrafts[''].content === literalText, 'private saved-literal buffer');
    await page.getByTestId('component-add').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-component-editor"] > h2')?.textContent.trim() === 'Add a component');
    await page.getByTestId('component-title').fill(newTitle); await paste(newText);
    await waitStored(key, d => d.contentDrafts?.$new?.componentId == null && d.contentDrafts.$new.content === newText, 'independent unsaved new card');
    await select('$new', literalTitle, literalText);
    const editedTitle = `${literalTitle} edited`, editedText = full('Edited literal saved $new only');
    await page.getByTestId('component-title').fill(editedTitle); await paste(editedText);
    await waitStored(key, d => d.contentDrafts?.['']?.content === editedText && d.contentDrafts?.$new?.content === newText, 'both complete independent buffers');
    await page.getByTestId('workspace-refresh').click(); await waitEditor(editedTitle, editedText);
    await page.reload(); await ready(key); await waitEditor(editedTitle, editedText);
    const both = JSON.parse(await raw(key));
    check(both.contentDrafts[''].key === '' && both.contentDrafts[''].componentId === '$new'
      && both.contentDrafts.$new.key === '$new' && both.contentDrafts.$new.componentId == null
      && both.contentDrafts.$new.content === newText, 'Saved literal and unsaved new-card identities must stay distinct across Refresh/reload.');
    checks.push('literal saved $new and independent new-card buffers remain distinct with complete text across Refresh and reload');
    await page.getByTestId('component-save').click();
    await saved(key, c => c.document.components.find(item => item.id === '$new')?.content === editedText, 'saved literal update');
    check((await context(key)).document.components.length === originalCount && JSON.parse(await raw(key)).contentDrafts.$new.content === newText,
      'Updating the literal saved record must not create a card or clear the independent new draft.');
    await page.getByTestId('component-add').click(); await waitEditor(newTitle, newText);
    if (await page.getByTestId('component-reapply').count()) await page.getByTestId('component-reapply').click();
    await page.getByTestId('component-save').click();
    let current = await saved(key, c => c.document.components.some(item => item.id !== '$new' && item.title === newTitle && item.content === newText), 'distinct independent new card');
    const newCard = current.document.components.find(item => item.id !== '$new' && item.title === newTitle);
    check(current.document.components.length === originalCount + 1 && current.document.components.find(item => item.id === '$new').content === editedText,
      'Adding the retained new draft must create a distinct record and preserve the caller-created $new.');
    checks.push('saving a literal $new updates only its existing record while adding the retained new draft creates a distinct card');
    for (const item of ordinary) {
      await select(item.id, item.title, item.content);
      const updated = item.content + `Updated ${item.id} without draft identity collision.  \n`;
      await paste(updated); await page.getByTestId('component-save').click();
      await saved(key, c => c.document.components.find(component => component.id === item.id)?.content === updated, `unchanged caller ID ${item.id}`);
      item.content = updated;
    }
    current = await context(key);
    check(current.document.components.find(item => item.id === '$new').content === editedText
      && current.document.components.find(item => item.id === newCard.id).content === newText, 'Editing ordinary or sentinel-like IDs must preserve both identity-sensitive records.');
    checks.push('ordinary and sentinel-like API component IDs stay unchanged and editable without occupying the new-card buffer');

    await select('$new', editedTitle, editedText);
    const orphanText = full('Dirty literal $new remains recoverable after removal');
    await paste(orphanText); await waitStored(key, d => d.contentDrafts?.['']?.content === orphanText, 'dirty literal orphan candidate');
    await select(ordinary[0].id, ordinary[0].title, ordinary[0].content);
    await command(key, 'remove_component', { componentId: '$new' });
    await page.getByTestId('workspace-refresh').click();
    await page.getByTestId('removed-draft-').waitFor({ state: 'visible' });
    await page.getByTestId('removed-draft-').click(); await waitEditor(editedTitle, orphanText);
    await page.getByTestId('component-reapply').click();
    await waitStored(key, d => d.selectedComponentId == null && d.contentDrafts?.$new?.content === orphanText, 'literal orphan kept as new');
    await page.getByTestId('component-save').click();
    current = await saved(key, c => c.document.components.some(item => item.id !== '$new' && item.title === editedTitle && item.content === orphanText), 'recovered literal orphan');
    check(!current.document.components.some(item => item.id === '$new'), 'Recovery must not recreate the deleted caller-owned identity.');
    checks.push('a deleted literal $new dirty draft remains selectable and saves as a distinct complete card');
    summary.primary = { workspaceId: key, originalLiteralCharacters: literalText.length, independentNewCharacters: newText.length,
      literalUpdateCharacters: editedText.length, recoveredOrphanCharacters: orphanText.length, independentNewId: newCard.id, callerIds: ordinary.map(item => item.id) };

    // Emulate an older valid browser snapshot while retaining a real committed update request.
    const legacy = await create('legacy literal update metadata');
    const legacyKey = legacy.workspaceId;
    await add(legacyKey, '$new', 'Legacy literal saved record', literalText);
    await open(legacyKey); await select('$new', 'Legacy literal saved record', literalText);
    const legacyTitle = 'Legacy literal update full title', legacyText = full('Legacy pending update literal $new');
    await page.getByTestId('component-title').fill(legacyTitle); await paste(legacyText);
    await waitStored(legacyKey, d => d.contentDrafts?.['']?.content === legacyText, 'legacy candidate full dirty buffer');
    const pendingUpdate = await envelope(legacyKey, 'update_component', { componentId: '$new', title: legacyTitle, content: legacyText });
    const committedUpdate = await send(legacyKey, pendingUpdate);
    const immutableUpdate = JSON.stringify(pendingUpdate);
    await page.evaluate(({ key, pending }) => {
      const storageKey = 'kanbanflow.workspace.draft.v1:' + key;
      const draft = JSON.parse(sessionStorage.getItem(storageKey));
      const saved = draft.contentDrafts[''];
      if (!saved || saved.componentId !== '$new' || draft.contentDrafts.$new?.dirty) throw new Error('Legacy fixture would overwrite another authored buffer.');
      delete draft.contentDrafts[''];
      saved.key = '$new'; draft.contentDrafts.$new = saved;
      draft.pending = { command: pending, draftSection: 'content:$new', draftAtSend: saved };
      sessionStorage.setItem(storageKey, JSON.stringify(draft));
    }, { key: legacyKey, pending: pendingUpdate });
    await page.reload(); await ready(legacyKey); await waitEditor(legacyTitle, legacyText);
    let migrated = JSON.parse(await waitStored(legacyKey, d => d.contentDrafts?.['']?.content === legacyText && d.pending?.draftSection === 'content:', 'migrated legacy saved scope'));
    check(migrated.contentDrafts[''].key === '' && migrated.contentDrafts[''].componentId === '$new'
      && migrated.pending.draftAtSend.key === '' && JSON.stringify(migrated.pending.command) === immutableUpdate,
      'Legacy migration may change only local saved-buffer metadata, preserving every command field and authored character.');
    checks.push('legacy saved-$new buffers migrate to the private saved key without changing complete text or the immutable pending update');
    const coexistTitle = 'New draft alongside migrated pending update', coexistText = full('New draft must survive saved pending acknowledgement');
    await page.evaluate(({ key, title, text, revision }) => {
      const storageKey = 'kanbanflow.workspace.draft.v1:' + key;
      const draft = JSON.parse(sessionStorage.getItem(storageKey));
      draft.contentDrafts.$new = { key: '$new', componentId: null, baseRevision: revision, type: 'note', title, content: text,
        url: null, taskId: null, originalTitle: '', originalContent: '', dirty: true };
      sessionStorage.setItem(storageKey, JSON.stringify(draft));
    }, { key: legacyKey, title: coexistTitle, text: coexistText, revision: (await context(legacyKey)).revision });
    await page.reload(); await ready(legacyKey); await waitEditor(legacyTitle, legacyText);
    const beforeUpdateReplay = JSON.stringify(await context(legacyKey));
    await page.getByTestId('workspace-retry-pending').click();
    await page.waitForFunction(() => !document.querySelector('[data-testid="workspace-pending-command"]'));
    await settled(legacyKey, committedUpdate.revision);
    const replayUpdateDraft = JSON.parse(await raw(legacyKey));
    check(replayUpdateDraft.pending == null && replayUpdateDraft.contentDrafts.$new.content === coexistText
      && JSON.stringify(await context(legacyKey)) === beforeUpdateReplay, 'Acknowledging a migrated pending update must preserve the separate new draft without applying another server write.');
    const updateReplay = await send(legacyKey, pendingUpdate);
    check(updateReplay.replayed && updateReplay.activityId === committedUpdate.activityId, 'The immutable migrated request must still retrieve its exact original receipt.');
    checks.push('replaying a migrated saved update acknowledges the original receipt and preserves the independent new-card draft');
    summary.legacyUpdate = { workspaceId: legacyKey, pendingOperation: pendingUpdate.operationId,
      originalActivityId: committedUpdate.activityId, replayActivityId: updateReplay.activityId, completeCharacters: legacyText.length, independentNewCharacters: coexistText.length };

    // A create pending under the legacy new-card key must retain its original scope and receipt.
    const capture = await create('legacy new create pending');
    const captureKey = capture.workspaceId;
    await add(captureKey, '$new', 'Saved literal beside pending create', literalText);
    await open(captureKey); await select('$new', 'Saved literal beside pending create', literalText);
    await page.getByTestId('component-add').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-component-editor"] > h2')?.textContent.trim() === 'Add a component');
    const captureTitle = 'Actual pending independent creation', captureText = full('Legacy new pending creation complete content');
    await page.getByTestId('component-title').fill(captureTitle); await paste(captureText);
    await waitStored(captureKey, d => d.contentDrafts?.$new?.content === captureText, 'full new-card pending candidate');
    const pendingCreate = await envelope(captureKey, 'create_component', { component: { type: 'note', title: captureTitle, content: captureText }, placement: { x: 1000, y: 50, zIndex: 40 } });
    const committedCreate = await send(captureKey, pendingCreate);
    const immutableCreate = JSON.stringify(pendingCreate);
    await page.evaluate(({ key, pending }) => {
      const storageKey = 'kanbanflow.workspace.draft.v1:' + key;
      const draft = JSON.parse(sessionStorage.getItem(storageKey));
      draft.pending = { command: pending, draftSection: 'content:$new', draftAtSend: draft.contentDrafts.$new };
      sessionStorage.setItem(storageKey, JSON.stringify(draft));
    }, { key: captureKey, pending: pendingCreate });
    await page.reload(); await ready(captureKey); await waitEditor(captureTitle, captureText);
    const createPendingDraft = JSON.parse(await raw(captureKey));
    check(createPendingDraft.pending.draftSection === 'content:$new' && createPendingDraft.pending.draftAtSend.key === '$new'
      && JSON.stringify(createPendingDraft.pending.command) === immutableCreate,
      'A pending new-card create must not be reclassified as a saved literal-$new update.');
    const beforeCreateReplay = JSON.stringify(await context(captureKey));
    await page.getByTestId('workspace-retry-pending').click();
    await page.waitForFunction(() => !document.querySelector('[data-testid="workspace-pending-command"]'));
    await settled(captureKey, committedCreate.revision);
    current = await context(captureKey);
    const createReplay = await send(captureKey, pendingCreate);
    const finalCreateDraft = JSON.parse(await raw(captureKey));
    check(JSON.stringify(current) === beforeCreateReplay && createReplay.replayed && createReplay.activityId === committedCreate.activityId
      && current.document.components.find(item => item.id === '$new').content === literalText
      && current.document.components.find(item => item.id === committedCreate.componentId)?.content === captureText
      && finalCreateDraft.contentDrafts[''].componentId === '$new' && finalCreateDraft.contentDrafts[''].content === literalText,
      'Pending create replay must create no duplicate and preserve the saved literal record and separate browser buffer.');
    checks.push('legacy pending creates retain their new-card scope and original receipt without replacing the saved literal $new');
    summary.legacyCreate = { workspaceId: captureKey, pendingOperation: pendingCreate.operationId,
      createdComponentId: committedCreate.componentId, originalActivityId: committedCreate.activityId,
      replayActivityId: createReplay.activityId, completeCharacters: captureText.length };

    // Caller-authored checkpoint arrays are structured strings, not text lines to split on save.
    const resume = await create('checkpoint array preservation');
    const resumeKey = resume.workspaceId;
    summary.checkpoints = [];
    for (const source of [
      { label: 'rich', completedChanges: ['  One completed entry\r\nwith embedded CRLF  ', '', 'Final completed Ω  '], openQuestions: [], nextActions: [''] },
      { label: 'empty', completedChanges: [], openQuestions: [], nextActions: [] }
    ]) {
      const sourceClient = `${prefix}-checkpoint-${source.label}`, sourceSession = `source-${source.label}`;
      const objective = `  Original ${source.label} checkpoint Ω\r\nPreserve complete objective  `;
      await command(resumeKey, 'save_checkpoint', { clientId: sourceClient, sessionId: sourceSession,
        objective, focusedComponentIds: [], completedChanges: source.completedChanges, openQuestions: source.openQuestions,
        nextActions: source.nextActions, lastAcknowledgedActivityId: (await context(resumeKey)).lastActivityId });
      if (source.label === 'rich') await open(resumeKey);
      else { await page.getByTestId('workspace-refresh').click(); await ready(resumeKey); }
      await page.getByTestId('workspace-tab-resume').click();
      const disclosure = page.getByTestId('workspace-checkpoints').locator(':scope > details');
      if (!await disclosure.evaluate(element => element.open)) await disclosure.locator(':scope > summary').click();
      await page.getByTestId(`checkpoint-resume-${sourceClient}-${sourceSession}`).click();
      await waitStored(resumeKey, d => d.checkpoint?.objective === objective, 'exact caller checkpoint loaded into browser draft');
      const identity = await page.evaluate(async key => {
        const module = await import('/Components/Pages/IdeaWorkspace.razor.js');
        const session = await module.initialize(key);
        return { clientId: session.clientId, sessionId: session.sessionId };
      }, resumeKey);
      await page.getByTestId('checkpoint-save').click();
      const checkpointContext = await saved(resumeKey, c => {
        const checkpoint = c.document.checkpoints.find(item => item.clientId === identity.clientId && item.sessionId === identity.sessionId);
        return checkpoint?.objective === objective && JSON.stringify(checkpoint.completedChanges) === JSON.stringify(source.completedChanges)
          && JSON.stringify(checkpoint.openQuestions) === JSON.stringify(source.openQuestions) && JSON.stringify(checkpoint.nextActions) === JSON.stringify(source.nextActions);
      }, `exact ${source.label} checkpoint arrays after actual UI Resume/save`);
      const original = checkpointContext.document.checkpoints.find(item => item.clientId === sourceClient && item.sessionId === sourceSession);
      check(original?.objective === objective && JSON.stringify(original.completedChanges) === JSON.stringify(source.completedChanges)
        && JSON.stringify(original.openQuestions) === JSON.stringify(source.openQuestions) && JSON.stringify(original.nextActions) === JSON.stringify(source.nextActions),
        'Saving a resumed checkpoint must preserve the original caller checkpoint as well.');
      checks.push(source.label === 'rich'
        ? 'actual Resume/save preserves embedded CRLF and blank checkpoint entries while keeping an empty array distinct'
        : 'actual Resume/save preserves all empty checkpoint arrays without inventing blank entries');
      summary.checkpoints.push({ label: source.label, workspaceId: resumeKey, clientId: identity.clientId, sessionId: identity.sessionId,
        completedEntries: source.completedChanges.length, questionEntries: source.openQuestions.length, nextEntries: source.nextActions.length,
        exactStructuredArrays: true, originalCallerPreserved: true });
    }
    check(pageErrors.length === 0, `Unexpected browser errors: ${pageErrors.join('; ')}`);
    return { result: 'PASS', prefix, checks, summary, pageErrors };
  } finally {
    page.off('pageerror', onError);
  }
}
