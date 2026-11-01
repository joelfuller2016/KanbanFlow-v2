// Run through a named Playwright CLI session against an explicitly disposable loopback app.
// Only the browser's actual canvas-module response is intercepted; all saves use the real API.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(['127.0.0.1', 'localhost', '[::1]'].includes(hostname), 'Use a disposable loopback application.');
  const prefix = `canvas-recovery-${Date.now()}`;
  const modulePattern = '**/Components/Workspaces/WorkspaceCanvas*.razor.js*';
  const checks = [], summary = [], pageErrors = [];
  let sequence = 0;
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const context = key => api('GET', `/api/workspaces/${encodeURIComponent(key)}/context`);
  const command = async (key, action, payload) => api('POST', `/api/workspaces/${encodeURIComponent(key)}/commands`, {
    operationId: `${prefix}-${++sequence}`, expectedRevision: (await context(key)).revision,
    actor: 'canvas-recovery-browser-fixture', action, payload
  });
  const full = label => (`\n  ## ${label} Ω 漢字 🧭\n\n    Retain every indentation and final space  \n`).repeat(650) + '\nFINAL COMPLETE CANVAS RECOVERY REQUIREMENT Ω  \n';
  const raw = (target, key) => target.evaluate(key => sessionStorage.getItem('kanbanflow.workspace.draft.v1:' + key), key);
  const waitStored = async (target, key, predicate, detail) => {
    for (let n = 0; n < 100; n++) {
      const value = await raw(target, key);
      if (value && predicate(JSON.parse(value))) return JSON.parse(value);
      await target.waitForTimeout(100);
    }
    throw new Error('Browser draft did not reach ' + detail);
  };
  const waitEditor = (target, title, content) => target.waitForFunction(({ title, content }) =>
    document.querySelector('[data-testid="component-title"]')?.value === title
      && document.querySelector('[data-testid="component-content"]')?.value === content, { title, content }, { timeout: 15000 });
  const paste = async (target, content) => {
    await target.getByTestId('component-content').evaluate((element, content) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error('A real textarea is required for complete-content paste.');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(element, content);
      element.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertFromPaste', data: content }));
    }, content);
    check(await target.getByTestId('component-content').inputValue() === content, 'The real editor must retain every pasted character.');
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
  const circuitHealthy = target => target.evaluate(() => {
    const error = document.getElementById('blazor-error-ui');
    return !error || getComputedStyle(error).display === 'none';
  });

  for (const mode of ['blocked-import', 'initialize-throws']) {
    const created = await api('POST', '/api/workspaces', {
      operationId: `${prefix}-${++sequence}`, actor: 'canvas-recovery-browser-fixture', title: `${prefix} ${mode}`
    });
    const key = created.workspaceId;
    const componentId = `${mode}-card`, placementId = `${mode}-placement`;
    const title = `${prefix} ${mode} saved card`, initial = 'Saved content before the canvas failure.';
    await command(key, 'create_component', {
      component: { id: componentId, type: 'note', title, content: initial },
      placement: { id: placementId, x: 30, y: 40, width: 320, height: 220, zIndex: 10 }
    });
    const initialComponentCount = (await context(key)).document.components.length;
    const target = await page.context().newPage();
    const onError = error => pageErrors.push(`${mode}: ${error.message}`);
    target.on('pageerror', onError);
    const routeErrors = [];
    let intercepted = 0;
    const intercept = async route => {
      intercepted++;
      if (mode === 'blocked-import') { await route.abort('failed'); return; }
      try {
        const response = await route.fetch();
        check(response.ok(), 'Fetch the actual canvas module before wrapping its initialize function.');
        let source = await response.text();
        check(source.includes('export function initialize(root, callback) {') && source.includes('export function dispose(root)'),
          'The initialize-failure probe must wrap the actual exported canvas functions.');
        source = source.replace('export function initialize(root, callback) {', 'function actualInitialize(root, callback) {')
          .replace('export function dispose(root)', 'function actualDispose(root)');
        source += `\nconst recoveryProbe = globalThis.__kanbanCanvasRecoveryProbe = { initializations: 0, disposals: 0 };
export function initialize(root, callback) {
  recoveryProbe.initializations++;
  actualInitialize(root, callback);
  if (recoveryProbe.initializations === 1) throw new Error('Controlled canvas initialize failure after actual controller setup.');
}
export function dispose(root) { recoveryProbe.disposals++; actualDispose(root); }
`;
        await route.fulfill({ response, contentType: 'text/javascript', body: source });
      } catch (error) { routeErrors.push(error.message); await route.abort('failed'); }
    };
    try {
      await target.route(modulePattern, intercept);
      await target.goto(origin + `/ideas/workspace/${key.replace(':', '/')}`);
      await target.getByTestId('canvas-init-error').waitFor({ state: 'visible', timeout: 15000 });
      check(intercepted > 0 && routeErrors.length === 0, `The actual module must reach the intended failure boundary: ${routeErrors.join('; ')}`);
      const fallbackMessage = await target.getByTestId('canvas-init-error').textContent();
      check(fallbackMessage.includes('layout fields') && await circuitHealthy(target), 'A friendly canvas warning must leave the editor circuit healthy.');
      if (mode === 'initialize-throws') {
        const cleanup = await target.evaluate(() => globalThis.__kanbanCanvasRecoveryProbe);
        check(cleanup.initializations === 1 && cleanup.disposals >= 1, 'A partially initialized actual controller must be disposed before fallback is shown.');
      }
      await target.getByTestId('workspace-view-outline').click();
      await target.getByTestId(`outline-component-${componentId}`).click();
      await waitEditor(target, title, initial);
      checks.push(`${mode}: friendly initialization warning leaves the circuit and Outline component selection working`);

      const savedTitle = `${title} edited in fallback`, savedText = full(`${mode} complete content saved in Outline`);
      await target.getByTestId('component-title').fill(savedTitle); await paste(target, savedText);
      await waitStored(target, key, draft => draft.contentDrafts?.[componentId]?.content === savedText, 'complete fallback edit');
      await target.getByTestId('component-save').click();
      await saved(target, key, value => value.document.components.find(item => item.id === componentId)?.content === savedText, 'complete real content save during canvas failure');
      for (const [name, value] of Object.entries({ x: 117.5, y: 83.25, width: 401.5, height: 299.25 })) {
        await target.getByTestId(`layout-${name}`).fill(String(value));
        await target.getByTestId(`layout-${name}`).press('Tab');
      }
      await target.getByTestId('layout-apply').click();
      let current = await saved(target, key, value => {
        const placement = value.document.placements.find(item => item.id === placementId);
        return placement?.x === 117.5 && placement.y === 83.25 && placement.width === 401.5 && placement.height === 299.25;
      }, 'precise layout fields saved while canvas initialization is unavailable');
      check(current.document.components.find(item => item.id === componentId).content === savedText,
        'Layout fallback must preserve complete independently saved component content.');
      checks.push(`${mode}: Outline saves complete authored content and precise layout fields through the real command API`);

      const newTitle = `${prefix} ${mode} independent unsaved card`, newText = full(`${mode} independent new card must remain complete`);
      await target.getByTestId('component-add').click();
      await target.waitForFunction(() => document.querySelector('[data-testid="workspace-component-editor"] > h2')?.textContent.trim() === 'Add a component');
      await target.getByTestId('component-title').fill(newTitle); await paste(target, newText);
      await waitStored(target, key, draft => draft.contentDrafts?.$new?.title === newTitle && draft.contentDrafts.$new.content === newText, 'complete independent unsaved card');
      await target.getByTestId(`outline-component-${componentId}`).click(); await waitEditor(target, savedTitle, savedText);
      const dirtyText = full(`${mode} dirty existing content must survive canvas retry`);
      await paste(target, dirtyText);
      const beforeRetry = await waitStored(target, key, draft => draft.contentDrafts?.[componentId]?.content === dirtyText
        && draft.contentDrafts?.$new?.content === newText, 'both complete unsaved buffers before retry');
      await target.unroute(modulePattern, intercept);
      await target.getByTestId('workspace-view-canvas').click();
      await target.waitForFunction(() => document.querySelector('[data-testid="workspace-canvas"]')?.dataset.ready === 'true'
        && !document.querySelector('[data-testid="canvas-init-error"]'), null, { timeout: 15000 });
      const afterRetry = JSON.parse(await raw(target, key));
      check(JSON.stringify(afterRetry.contentDrafts) === JSON.stringify(beforeRetry.contentDrafts)
        && await circuitHealthy(target), 'Retrying the canvas must retain both complete browser drafts exactly.');
      if (mode === 'initialize-throws') check((await target.evaluate(() => globalThis.__kanbanCanvasRecoveryProbe)).initializations === 2,
        'Canvas remount must retry the original actual controller after the one controlled initialization failure.');
      checks.push(`${mode}: removing interception and returning to Canvas retries initialization without changing either complete draft`);

      await target.getByTestId(`canvas-select-${placementId}`).focus();
      await target.getByTestId(`canvas-select-${placementId}`).press('ArrowRight');
      current = await saved(target, key, value => value.document.placements.find(item => item.id === placementId)?.x === 127.5,
        'actual keyboard canvas callback after recovery');
      const afterKeyboard = JSON.parse(await raw(target, key));
      check(current.document.components.length === initialComponentCount
        && current.document.components.find(item => item.id === componentId)?.content === savedText
        && afterKeyboard.contentDrafts[componentId].content === dirtyText && afterKeyboard.contentDrafts.$new.content === newText,
        'Recovered canvas interaction must move the saved placement while preserving independent unsaved content.');
      await target.getByTestId('workspace-view-outline').click();
      await target.getByTestId(`outline-component-${componentId}`).click(); await waitEditor(target, savedTitle, dirtyText);
      await target.getByTestId('component-add').click(); await waitEditor(target, newTitle, newText);
      check(await circuitHealthy(target), 'The editor circuit must remain available after retry and real keyboard movement.');
      checks.push(`${mode}: recovered canvas keyboard movement persists and both full drafts remain reachable through the editor`);
      summary.push({ mode, workspaceId: key, interceptedModuleRequests: intercepted, savedCharacters: savedText.length,
        existingDraftCharacters: dirtyText.length, newDraftCharacters: newText.length,
        exactDraftsAfterRetry: true, recoveredKeyboardX: 127.5, savedComponents: current.document.components.length });
    } finally {
      await target.unroute(modulePattern, intercept);
      target.off('pageerror', onError);
      await target.close();
    }
  }
  check(pageErrors.length === 0, `Unexpected browser errors: ${pageErrors.join('; ')}`);
  return { result: 'PASS', prefix, checks, summary, pageErrors };
}
