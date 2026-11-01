// Named Playwright CLI session; disposable loopback application only.
// Real API receipts and native pointer input. Only A controls a browser Storage failure.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(['127.0.0.1', 'localhost', '[::1]'].includes(hostname), 'Use an explicitly disposable loopback app.');
  const prefix = `workspace-convergence-${Date.now()}`;
  const originalViewport = page.viewportSize(), checks = [], pageErrors = [], summary = { A: [], B: [], C: [], D: {} };
  let sequence = 0, stage = 'fixtures';
  const onError = error => pageErrors.push(error.message);
  page.on('pageerror', onError);
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const context = key => api('GET', `/api/workspaces/${encodeURIComponent(key)}/context`);
  const create = title => api('POST', '/api/workspaces', { operationId: `${prefix}-${++sequence}`, actor: 'convergence-fixture', title: `${prefix} ${title}` });
  const envelope = async (key, action, payload) => ({ operationId: `${prefix}-${++sequence}`, expectedRevision: (await context(key)).revision, action, payload, actor: 'convergence-fixture' });
  const send = (key, value) => api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, value);
  const command = async (key, action, payload) => send(key, await envelope(key, action, payload));
  const add = (key, id, title, content, placement = {}) => command(key, 'create_component', {
    component: { id, type: 'note', title, content }, placement: { id: `${id}-placement`, x: 900, y: 80, zIndex: 30, ...placement }
  });
  const full = label => (`\n  ## ${label} Ω 漢字 🧭 <em>HTML & "quotes"</em> \\ literal\n\n    Preserve every final space  \n`).repeat(450) + '\nFINAL COMPLETE CONVERGENCE REQUIREMENT Ω  \n';
  const raw = (target, key) => target.evaluate(key => sessionStorage.getItem('kanbanflow.workspace.draft.v1:' + key), key);
  const waitStored = async (target, key, predicate, detail) => {
    for (let n = 0; n < 100; n++) {
      const value = await raw(target, key);
      if (value && predicate(JSON.parse(value))) return JSON.parse(value);
      await target.waitForTimeout(100);
    }
    throw new Error('Stored draft did not reach ' + detail);
  };
  const ready = (target, key) => target.waitForFunction(key => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    return shell?.dataset.workspaceId === key && shell.dataset.ready === 'true';
  }, key, { timeout: 15000 });
  const open = async (target, key, view = 'outline') => {
    await target.goto(origin + `/ideas/workspace/${key.replace(':', '/')}?view=${view}`); await ready(target, key);
    await target.evaluate(stage => { document.body.dataset.workspaceTestStage = stage; }, stage);
  };
  const editor = (target, title, content) => target.waitForFunction(({ title, content }) =>
    document.querySelector('[data-testid="component-title"]')?.value === title
      && document.querySelector('[data-testid="component-content"]')?.value === content, { title, content }, { timeout: 15000 });
  const paste = async (target, field, value) => {
    await target.getByTestId(field).evaluate((element, value) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error('Complete input requires a real textarea.');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(element, value);
      element.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertFromPaste', data: value }));
    }, value);
    check(await target.getByTestId(field).inputValue() === value, 'The actual textarea must retain every pasted character.');
  };
  const newDraft = async (target, key, title, text) => {
    await target.getByTestId('component-add').click();
    await target.waitForFunction(() => document.querySelector('[data-testid="workspace-component-editor"] > h2')?.textContent.trim() === 'Add a component');
    await target.getByTestId('component-title').fill(title); await paste(target, 'component-content', text);
    return waitStored(target, key, d => d.contentDrafts?.$new?.title === title && d.contentDrafts.$new.content === text, 'complete independent new draft');
  };
  const settled = (target, key, revision) => target.waitForFunction(({ key, revision }) => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    return shell?.dataset.workspaceId === key && Number(shell.dataset.revision) === revision
      && !document.querySelector('[data-testid="workspace-pending-command"]')
      && !document.querySelector('[data-testid="component-add"]')?.disabled;
  }, { key, revision }, { timeout: 15000 });
  const saved = async (target, key, predicate, detail) => {
    for (let n = 0; n < 100; n++) {
      const value = await context(key);
      if (predicate(value)) { await settled(target, key, value.revision); return value; }
      await target.waitForTimeout(100);
    }
    throw new Error('Saved context did not reach ' + detail);
  };
  const retry = async (target, key, committed) => {
    await target.getByTestId('workspace-retry-pending').click(); await settled(target, key, committed.revision);
    check((await target.getByTestId('workspace-notice').textContent()).includes('recovered prior result'), 'UI retry must acknowledge the actual original receipt.');
  };
  try {
    // A: navigation alone creates clean cache state; it must not compete with authored storage.
    for (const navigation of ['refresh', 'select']) {
      stage = `A navigation-only ${navigation}`;
      const created = await create(stage), key = created.workspaceId;
      await add(key, 'stored-card', 'Stored original card', 'Original saved card.');
      await add(key, 'other-card', 'Other clean card', 'Other clean saved content.');
      const target = await page.context().newPage();
      target.on('pageerror', onError);
      try {
        await open(target, key);
        const independentText = full(`A ${navigation} independent draft`), storedText = full(`A ${navigation} original stored draft`);
        await newDraft(target, key, 'Independent original new card', independentText);
        await target.getByTestId('outline-component-stored-card').click(); await editor(target, 'Stored original card', 'Original saved card.');
        await paste(target, 'component-content', storedText);
        const original = await waitStored(target, key, d => d.contentDrafts?.['stored-card']?.content === storedText, 'original full authored storage');
        const pending = await envelope(key, 'update_component', { componentId: 'stored-card', title: 'Stored original card', content: storedText });
        const committed = await send(key, pending);
        await target.evaluate(({ key, pending }) => {
          const storageKey = 'kanbanflow.workspace.draft.v1:' + key, d = JSON.parse(sessionStorage.getItem(storageKey));
          d.pending = { command: pending, draftSection: 'content:stored-card', draftAtSend: d.contentDrafts['stored-card'] };
          sessionStorage.setItem(storageKey, JSON.stringify(d)); sessionStorage.setItem('convergence-fail-identity-once', 'yes');
        }, { key, pending });
        const originalRaw = await raw(target, key);
        await target.addInitScript(() => {
          const get = Storage.prototype.getItem;
          Storage.prototype.getItem = function(key) {
            if (this === localStorage && key === 'kanbanflow.workspace.client.v1' && get.call(sessionStorage, 'convergence-fail-identity-once') === 'yes') {
              sessionStorage.removeItem('convergence-fail-identity-once'); throw new DOMException('Controlled identity read failure', 'SecurityError');
            }
            return get.call(this, key);
          };
        });
        await target.reload(); await target.getByTestId('workspace-storage-error').waitFor({ state: 'visible' });
        if (navigation === 'refresh') {
          await target.getByTestId('workspace-refresh').click();
          await target.getByTestId('workspace-notice').waitFor({ state: 'visible' });
        } else {
          await target.getByTestId('outline-component-other-card').click(); await editor(target, 'Other clean card', 'Other clean saved content.');
        }
        check(await raw(target, key) === originalRaw, 'Navigation while identity is blocked must retain the complete original stored entry.');
        await target.getByTestId('workspace-retry-draft').click(); await ready(target, key);
        await editor(target, 'Stored original card', storedText);
        const restored = JSON.parse(await raw(target, key));
        check(restored.pending?.command.operationId === pending.operationId && restored.pending.command.payload.content === storedText
          && JSON.stringify(restored.contentDrafts.$new) === JSON.stringify(original.contentDrafts.$new)
          && await target.getByTestId('workspace-storage-error').count() === 0
          && await target.getByTestId('workspace-preserved-draft').count() === 0,
          'Navigation-only cache state must restore the authored draft and original Pending without forcing backup recovery.');
        const beforeReplay = JSON.stringify(await context(key));
        await retry(target, key, committed);
        const replay = await send(key, pending), after = JSON.parse(await raw(target, key));
        check(replay.replayed && replay.activityId === committed.activityId && JSON.stringify(await context(key)) === beforeReplay
          && after.pending == null && after.contentDrafts.$new.content === independentText,
          'Restored Pending must replay its original receipt without a server mutation or loss of the independent full draft.');
        checks.push(`navigation-only ${navigation} after identity failure restores stored authored state and replays Pending without forced backup`);
        summary.A.push({ navigation, workspaceId: key, originalCharacters: storedText.length, independentCharacters: independentText.length,
          activityId: committed.activityId, noForcedBackup: true, realReceiptReplayed: true });
      } catch (error) {
        await page.evaluate(evidence => { globalThis.__workspaceConvergenceFailure = evidence; }, { stage, key, raw: await raw(target, key), context: await context(key), message: error.message });
        throw error;
      } finally { target.off('pageerror', onError); await target.close(); }
    }

    // B: browser JSON round-trips may escape Unicode/HTML differently from .NET serialization.
    stage = 'B Unicode content acknowledgement';
    const unicode = await create(stage), unicodeKey = unicode.workspaceId;
    await add(unicodeKey, 'unicode-card', 'Original Unicode card', 'Original Unicode saved content.');
    await open(page, unicodeKey);
    const independentTitle = 'Independent Unicode draft Ω <b>&</b>', independentText = full('B independent new buffer');
    const independent = await newDraft(page, unicodeKey, independentTitle, independentText);
    await page.getByTestId('outline-component-unicode-card').click(); await editor(page, 'Original Unicode card', 'Original Unicode saved content.');
    const unicodeTitle = 'Exact Ω 漢字 <b>& "quoted" \\</b>', unicodeText = full('B Unicode content Ω <em>&</em>');
    await page.getByTestId('component-title').fill(unicodeTitle); await paste(page, 'component-content', unicodeText);
    await waitStored(page, unicodeKey, d => d.contentDrafts?.['unicode-card']?.content === unicodeText, 'exact Unicode content draft');
    const update = await envelope(unicodeKey, 'update_component', { componentId: 'unicode-card', title: unicodeTitle, content: unicodeText });
    const updated = await send(unicodeKey, update);
    await page.evaluate(({ key, pending }) => {
      const storageKey = 'kanbanflow.workspace.draft.v1:' + key, d = JSON.parse(sessionStorage.getItem(storageKey));
      d.pending = { command: pending, draftSection: 'content:unicode-card', draftAtSend: d.contentDrafts['unicode-card'] };
      sessionStorage.setItem(storageKey, JSON.stringify(JSON.parse(JSON.stringify(d))));
    }, { key: unicodeKey, pending: update });
    check((await raw(page, unicodeKey)).includes('漢字') && (await raw(page, unicodeKey)).includes('<em>'), 'The actual browser snapshot must use its literal Unicode/HTML JSON encoding.');
    const beforeContentReplay = JSON.stringify(await context(unicodeKey));
    await page.reload(); await ready(page, unicodeKey); await editor(page, unicodeTitle, unicodeText);
    await retry(page, unicodeKey, updated);
    const contentAck = await waitStored(page, unicodeKey, d => d.pending == null && d.contentDrafts?.['unicode-card']?.dirty === false,
      'matched Unicode content snapshot cleared to a clean saved buffer');
    const updateReplay = await send(unicodeKey, update);
    check(contentAck.contentDrafts['unicode-card'].baseRevision === updated.revision && contentAck.contentDrafts['unicode-card'].content === unicodeText
      && JSON.stringify(contentAck.contentDrafts.$new) === JSON.stringify(independent.contentDrafts.$new)
      && updateReplay.replayed && updateReplay.activityId === updated.activityId && JSON.stringify(await context(unicodeKey)) === beforeContentReplay,
      'Structurally equal escaped content must acknowledge the matched section, retain the independent buffer, and replay without another write.');
    checks.push('Unicode and HTML content snapshot survives browser JSON escaping and clears only the matched acknowledged section');
    summary.B.push({ section: 'content', workspaceId: unicodeKey, characters: unicodeText.length, activityId: updated.activityId, cleanAfterAcknowledgement: true });

    stage = 'B Unicode comment acknowledgement';
    const otherDirty = full('B independent dirty existing buffer');
    await paste(page, 'component-content', otherDirty);
    await page.getByTestId('layout-x').fill('808'); await page.getByTestId('layout-x').press('Tab');
    await waitStored(page, unicodeKey, d => d.contentDrafts?.['unicode-card']?.content === otherDirty && d.layoutDrafts?.['unicode-card-placement']?.x === 808,
      'independent existing content and layout');
    await page.getByTestId('workspace-tab-discussion').click();
    const commentText = full('B comment Ω 漢字 <em>HTML & "quotes"</em>');
    await page.getByTestId('comment-target').selectOption('unicode-card'); await paste(page, 'comment-text', commentText);
    const discussion = await waitStored(page, unicodeKey, d => d.discussion?.text === commentText, 'exact Unicode comment draft');
    const independentDiscussionBuffers = value => JSON.stringify({ content: value.contentDrafts['unicode-card'],
      fresh: value.contentDrafts.$new, layout: value.layoutDrafts['unicode-card-placement'] });
    const pendingComment = await envelope(unicodeKey, 'add_comment', { text: commentText, componentId: 'unicode-card', replyToId: null });
    const commented = await send(unicodeKey, pendingComment);
    await page.evaluate(({ key, pending }) => {
      const storageKey = 'kanbanflow.workspace.draft.v1:' + key, d = JSON.parse(sessionStorage.getItem(storageKey));
      d.pending = { command: pending, draftSection: 'discussion', draftAtSend: d.discussion };
      sessionStorage.setItem(storageKey, JSON.stringify(JSON.parse(JSON.stringify(d))));
    }, { key: unicodeKey, pending: pendingComment });
    const beforeCommentReplay = JSON.stringify(await context(unicodeKey));
    await page.reload(); await ready(page, unicodeKey); await page.getByTestId('workspace-tab-discussion').click();
    await retry(page, unicodeKey, commented);
    const commentAck = await waitStored(page, unicodeKey, d => d.pending == null && d.discussion?.text === '', 'matched Unicode discussion snapshot cleared');
    const commentReplay = await send(unicodeKey, pendingComment), commentContext = await context(unicodeKey);
    check(await page.getByTestId('comment-text').inputValue() === '' && commentContext.document.comments.filter(item => item.text === commentText).length === 1
      && independentDiscussionBuffers(commentAck) === independentDiscussionBuffers(discussion)
      && commentReplay.replayed && commentReplay.activityId === commented.activityId && JSON.stringify(commentContext) === beforeCommentReplay,
      'Escaped comment acknowledgement must clear its own unchanged section, preserve independent full drafts/layout, and create no duplicate comment.');
    checks.push('Unicode and HTML comment snapshot clears its matched section after actual receipt replay without clearing independent buffers or duplicating the comment');
    summary.B.push({ section: 'discussion', workspaceId: unicodeKey, characters: commentText.length, activityId: commented.activityId, commentsWithExactText: 1 });

    // C: native pointer capture with real scrolling, including no pointermove after scrolling.
    stage = 'C canvas viewport scrolling';
    await page.setViewportSize({ width: 1440, height: 1000 });
    const scrolling = await create(stage), scrollKey = scrolling.workspaceId, placementId = 'scroll-card-placement';
    await add(scrollKey, 'scroll-card', 'Scroll gesture card', 'Preserve this card through native scroll gestures.', { x: 180, y: 180, width: 260, height: 180, zIndex: 50 });
    await open(page, scrollKey, 'canvas');
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-canvas"]')?.dataset.ready === 'true');
    for (const [kind, pointerDelta] of [['drag', 10], ['drag', 0], ['resize', 10], ['resize', 0]]) {
      stage = `C ${kind} scroll70 pointer${pointerDelta}`;
      const before = await context(scrollKey), placement = before.document.placements.find(item => item.id === placementId);
      const viewport = page.getByTestId('workspace-canvas-viewport');
      await viewport.evaluate(element => { element.scrollLeft = 0; element.scrollTop = 0; });
      const handle = page.getByTestId(`${kind === 'drag' ? 'canvas-select' : 'canvas-resize'}-${placementId}`);
      await handle.scrollIntoViewIfNeeded();
      const box = await handle.boundingBox(); check(box, 'A native canvas gesture needs a visible pointer target.');
      const start = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
      await page.mouse.move(start.x, start.y); await page.mouse.down();
      try {
        const scroll = await viewport.evaluate(element => {
          const x = element.scrollLeft, y = element.scrollTop;
          element.scrollLeft += 70; element.scrollTop += 70;
          return { dx: element.scrollLeft - x, dy: element.scrollTop - y };
        });
        check(scroll.dx === 70 && scroll.dy === 70, 'The fixture must actually scroll both viewport axes by 70px.');
        if (pointerDelta) await page.mouse.move(start.x + pointerDelta, start.y + pointerDelta, { steps: 2 });
        // For pointerDelta=0 no mouse move occurs between the actual scroll and pointerup.
        await page.mouse.up();
        const delta = 70 + pointerDelta;
        const current = await saved(page, scrollKey, value => {
          const result = value.document.placements.find(item => item.id === placementId);
          return kind === 'drag' ? result?.x === placement.x + delta && result.y === placement.y + delta
            : result?.width === placement.width + delta && result.height === placement.height + delta;
        }, `${kind} includes actual viewport scroll in its persisted delta`);
        const result = current.document.placements.find(item => item.id === placementId);
        check(kind === 'drag' ? result.width === placement.width && result.height === placement.height
          : result.x === placement.x && result.y === placement.y, 'The gesture must preserve the unrelated placement dimensions.');
        check(await page.getByTestId(`canvas-placement-${placementId}`).evaluate(element =>
          ['--preview-dx', '--preview-dy', '--preview-width', '--preview-height'].every(name => element.style.getPropertyValue(name) === '')),
          'A persisted gesture must clear all temporary preview properties.');
        checks.push(`native ${kind} persists the70px viewport scroll plus${pointerDelta}px pointer movement on both axes`);
        summary.C.push({ kind, pointerDelta, scrollDelta: 70, persistedDelta: delta, noPointerMoveAfterScroll: pointerDelta === 0,
          x: result.x, y: result.y, width: result.width, height: result.height });
      } finally { await page.mouse.up(); }
    }

    // D: independent choices must remain useful even when no default publication preview can succeed.
    stage = 'D missing prior delivery feature';
    const project = await api('POST', '/api/projects', { name: `${prefix} delivery project` });
    const projectKey = `project:${project.id}`;
    const oldFeature = await api('POST', `/api/projects/${project.id}/features`, { name: `${prefix} original feature` });
    const movedFeature = await api('POST', `/api/projects/${project.id}/features`, { name: `${prefix} moved task feature` });
    const proposeAndAccept = async (title, items) => {
      await command(projectKey, 'propose_plan', { title, items, expectedProjectPlanRevision: (await context(projectKey)).projectPlanRevision });
      const proposed = await context(projectKey), revision = proposed.document.planRevisions[proposed.document.planRevisions.length - 1];
      await command(projectKey, 'accept_plan', { planRevisionId: revision.id, expectedProjectPlanRevision: proposed.projectPlanRevision });
      return context(projectKey);
    };
    let canonical = await proposeAndAccept(`${prefix} first plan`, [{ id: 'first-item', title: 'Originally published task', description: 'Original complete published specification.', acceptanceCriteria: ['Preserve original task'], dependsOn: [] }]);
    const firstItem = canonical.acceptedPlan.items[0];
    await command(projectKey, 'publish_plan', { expectedProjectPlanRevision: canonical.projectPlanRevision, itemIds: [firstItem.id], featureId: oldFeature.id });
    canonical = await context(projectKey);
    const mapping = canonical.document.publications.find(item => item.canonicalItemId === firstItem.id);
    check(mapping, 'Initial publication must produce a durable task mapping.');
    await api('PATCH', `/api/tasks/${mapping.taskId}/feature`, { featureId: movedFeature.id, actor: 'convergence-human-reassignment' });
    await api('DELETE', `/api/features/${oldFeature.id}`);
    check((await page.request.get(origin + `/api/features/${oldFeature.id}`)).status() === 404, 'The emptied original publication feature must really be deleted.');
    const taskSignature = value => JSON.stringify({ id: value.id, featureId: value.featureId, name: value.name, description: value.description,
      status: value.status, priority: value.priority, assignee: value.assignee, sortOrder: value.sortOrder, updatedAt: value.updatedAt,
      completedAt: value.completedAt, acceptanceCriteria: value.acceptanceCriteria });
    const preservedTask = taskSignature(await api('GET', `/api/tasks/${mapping.taskId}`));
    canonical = await proposeAndAccept(`${prefix} expanded plan`, [
      { id: firstItem.id, title: firstItem.title, description: firstItem.description, acceptanceCriteria: firstItem.acceptanceCriteria,
        dependsOn: firstItem.dependsOn, provenance: firstItem.provenance },
      { id: 'new-item', title: 'Explicit recovery expansion', description: full('D complete newly published scope'), acceptanceCriteria: ['Publish only the new task'], dependsOn: [] }
    ]);
    const newItem = canonical.acceptedPlan.items.find(item => item.title === 'Explicit recovery expansion');
    await open(page, projectKey, 'delivery');
    await page.waitForFunction(featureId => [...document.querySelector('[data-testid="delivery-feature-select"]').options].some(option => option.value === String(featureId)), movedFeature.id);
    const beforePreview = JSON.stringify({ context: await context(projectKey), tasks: await api('GET', `/api/projects/${project.id}/tasks`), features: await api('GET', `/api/projects/${project.id}/features`) });
    await page.getByTestId('delivery-preview').click();
    await page.getByTestId('delivery-error').waitFor({ state: 'visible' });
    check((await page.getByTestId('delivery-error').textContent()).includes('publication_target_missing')
      && await page.getByTestId('delivery-preview-result').count() === 0
      && await page.getByTestId('delivery-feature-select').locator(`option[value="${movedFeature.id}"]`).count() === 1,
      'An unavailable default publication target must retain independently loaded valid feature choices.');
    check(JSON.stringify({ context: await context(projectKey), tasks: await api('GET', `/api/projects/${project.id}/tasks`), features: await api('GET', `/api/projects/${project.id}/features`) }) === beforePreview,
      'The failed first preview must not mutate the project, task or feature state.');
    checks.push('first default preview reports the deleted publication feature while current independently loaded feature choices remain available read only');
    const explicitFeature = await api('POST', `/api/projects/${project.id}/features`, { name: `${prefix} explicit recovery target` });
    await page.getByTestId('delivery-reload-features').click();
    await page.waitForFunction(featureId => [...document.querySelector('[data-testid="delivery-feature-select"]').options].some(option => option.value === String(featureId)), explicitFeature.id);
    await page.getByTestId('delivery-feature-select').selectOption(String(explicitFeature.id));
    await page.getByTestId('delivery-preview').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="delivery-new-count"]')?.textContent.trim() === '1 new tasks'
      && document.querySelector('[data-testid="delivery-existing-count"]')?.textContent.trim() === '1 already linked');
    check(await page.getByTestId('delivery-error').count() === 0 && await page.getByTestId('delivery-publish').isEnabled(),
      'An explicitly chosen current feature must enable a valid reviewed recovery publication.');
    checks.push('Reload feature choices reads a newly added real feature and explicit selection enables a valid publication preview');
    const afterPreviewFeature = await api('POST', `/api/projects/${project.id}/features`, { name: `${prefix} feature added after successful preview` });
    await page.getByTestId('delivery-reload-features').click();
    await page.waitForFunction(featureId => [...document.querySelector('[data-testid="delivery-feature-select"]').options].some(option => option.value === String(featureId)), afterPreviewFeature.id);
    await page.getByTestId('workspace-view-outline').click();
    await page.getByTestId('outline-component-frame-brief').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="outline-component-frame-brief"]')?.getAttribute('aria-pressed') === 'true');
    check(await page.getByTestId('delivery-feature-select').locator(`option[value="${afterPreviewFeature.id}"]`).count() === 1
      && await page.getByTestId('delivery-feature-select').inputValue() === String(explicitFeature.id)
      && await page.getByTestId('delivery-preview-result').isVisible() && await page.getByTestId('delivery-publish').isEnabled(),
      'A parent selection rerender with the same successful preview must retain independently refreshed feature choices and the reviewed selection.');
    checks.push('reloaded feature choices survive a parent selection rerender while the same successful preview remains active');
    await page.getByTestId('delivery-publish').click();
    const published = await saved(page, projectKey, value => value.document.publications.some(item => item.canonicalItemId === newItem.id), 'new item published to the explicit recovery feature');
    const newMapping = published.document.publications.find(item => item.canonicalItemId === newItem.id);
    const actualTasks = await api('GET', `/api/projects/${project.id}/tasks`), newTask = await api('GET', `/api/tasks/${newMapping.taskId}`);
    check(published.document.publications.length === 2 && actualTasks.length === 2 && newMapping.taskId !== mapping.taskId
      && newMapping.featureId === explicitFeature.id && newTask.featureId === explicitFeature.id
      && taskSignature(await api('GET', `/api/tasks/${mapping.taskId}`)) === preservedTask
      && (await page.request.get(origin + `/api/features/${oldFeature.id}`)).status() === 404,
      'Recovery must create only the new task in the explicit feature, preserve the moved original task, and keep the deleted feature absent.');
    checks.push('explicit recovery publication creates only the new task and preserves the moved task and deleted original feature');
    summary.D = { projectId: project.id, workspaceId: projectKey, deletedFeatureId: oldFeature.id, movedFeatureId: movedFeature.id,
      explicitFeatureId: explicitFeature.id, afterPreviewFeatureId: afterPreviewFeature.id, originalTaskId: mapping.taskId, newTaskId: newMapping.taskId, taskCount: actualTasks.length,
      originalTaskUnchanged: true, deletedFeatureNotRecreated: true };
    check(pageErrors.length === 0, `Unexpected browser errors: ${pageErrors.join('; ')}`);
    return { result: 'PASS', prefix, checks, summary, pageErrors };
  } catch (error) { throw new Error(`${stage}: ${error.message}`); }
  finally { page.off('pageerror', onError); if (originalViewport) await page.setViewportSize(originalViewport); }
}
