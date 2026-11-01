// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-races.browser.mjs
// Use an explicitly disposable local app/database. Fixtures remain for audit inspection.
async basePage => {
  const { origin, hostname } = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(hostname))
    throw new Error("Run workspace race checks against a disposable loopback app.");
  const page = await basePage.context().newPage();
  const prefix = `workspace-races-${Date.now()}`;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const id = value => page.getByTestId(value);
  let workspaceId;
  const context = () => api("GET", `/api/workspaces/${encodeURIComponent(workspaceId)}/context`);
  const setDelay = value => page.evaluate(delay => { window.__workspaceRaceTransport.delay = delay; }, value);
  const results = { typing: [], selection: null, layout: null, contrast: [] };
  try {
    await page.addInitScript(() => {
      // Delay delivery of actual server messages, leaving application handlers/results intact.
      window.__workspaceRaceTransport = { delay: 0, messages: 0 };
      const NativeSocket = window.WebSocket;
      window.WebSocket = class extends NativeSocket {
        set onmessage(handler) {
          super.onmessage = handler ? event => {
            const transport = window.__workspaceRaceTransport;
            transport.messages++;
            if (transport.delay) window.setTimeout(() => handler.call(this, event), transport.delay);
            else handler.call(this, event);
          } : null;
        }
        get onmessage() { return super.onmessage; }
      };
    });
    const created = await api("POST", "/api/workspaces", {
      operationId: prefix, actor: "workspace-race-smoke", title: prefix, body: "Disposable race fixture"
    });
    workspaceId = created.workspaceId;
    for (const label of ["A", "B"]) {
      const current = await context();
      await api("POST", `/api/workspaces/${encodeURIComponent(workspaceId)}/commands`, {
        operationId: `${prefix}-${label}`, actor: "workspace-race-smoke", expectedRevision: current.revision,
        action: "create_component", payload: { component: { type: "note", title: `Card ${label}`, content: `Saved ${label}` } }
      });
    }
    const initial = await context();
    const a = initial.document.components.find(component => component.title === "Card A");
    const b = initial.document.components.find(component => component.title === "Card B");
    const firstAppearance = initial.document.placements.find(placement => placement.componentId === a.id);
    const secondAppearanceId = `${prefix}-second`;
    await api("POST", `/api/workspaces/${encodeURIComponent(workspaceId)}/commands`, {
      operationId: `${prefix}-appearance`, actor: "workspace-race-smoke", expectedRevision: initial.revision,
      action: "add_placement", payload: { placement: { id: secondAppearanceId, componentId: a.id,
        x: 700, y: 90, width: 320, height: 220, zIndex: 10 } }
    });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(origin + `/ideas/workspace/${workspaceId.replace(":", "/")}`);
    await page.waitForFunction(key => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return shell?.dataset.ready === "true" && shell.dataset.workspaceId === key;
    }, workspaceId);
    await id("workspace-view-outline").click();
    await id(`outline-component-${a.id}`).click();
    await page.waitForFunction(() => document.querySelector('[data-testid="component-title"]')?.value === "Card A");
    await id("layout-placement").focus();
    await id("layout-placement").press("End");
    await page.waitForFunction(placementId => document.querySelector('[data-testid="layout-placement"]')?.value === placementId, secondAppearanceId);
    await page.waitForTimeout(400);
    check(await id("layout-placement").evaluate(element => element === document.activeElement), "Changing an appearance with the keyboard must retain selector focus.");
    await id("layout-placement").selectOption(firstAppearance.id);
    await page.waitForFunction(x => Number(document.querySelector('[data-testid="layout-x"]')?.value) === x, firstAppearance.x);
    await setDelay(800);
    await id("layout-placement").selectOption(secondAppearanceId);
    await page.waitForTimeout(180);
    check(Number(await id("layout-x").inputValue()) === firstAppearance.x, "The stale-layout fixture must still display the first appearance.");
    await id("layout-x").fill(String(firstAppearance.x + 7));
    await id("layout-x").press("Tab");
    await page.waitForTimeout(2400);
    await setDelay(0);
    check(Number(await id("layout-x").inputValue()) === 700, "An old appearance control must not modify the newly selected appearance draft.");
    results.layout = { keyboardFocusRetained: true, staleFieldIgnored: true };
    const expected = "The complete requirement must retain every typed character 0123456789.";
    for (const delay of [0, 150, 300]) {
      await setDelay(0);
      await id("component-content").fill("");
      await page.waitForTimeout(500);
      await setDelay(delay);
      await id("component-content").pressSequentially(expected, { delay: 25 });
      const afterTyping = await id("component-content").inputValue();
      await page.waitForTimeout(1800);
      const settled = await id("component-content").inputValue();
      check(afterTyping === expected && settled === expected, `Sequential typing lost text with ${delay}ms server delivery delay: ${JSON.stringify({ afterTyping, settled })}`);
      results.typing.push({ delayMs: delay, keyDelayMs: 25, characters: expected.length, exact: true });
    }
    await setDelay(0);
    await page.waitForTimeout(500);
    await id("component-save").click();
    let saved;
    for (let attempt = 0; attempt < 100; attempt++) {
      saved = await context();
      if (saved.document.components.find(component => component.id === a.id)?.content === expected) break;
      await page.waitForTimeout(100);
    }
    check(saved.document.components.find(component => component.id === a.id)?.content === expected, "The exact typed content must be committed, not only displayed.");
    await page.waitForFunction(revision => Number(document.querySelector('[data-testid="workspace-shell"]')?.dataset.revision) === revision
      && document.querySelector('[data-testid="component-save"]')?.disabled === true
      && document.querySelector('[data-testid="component-remove"]')?.disabled === false, saved.revision);
    await setDelay(800);
    await id(`outline-component-${b.id}`).click();
    await page.waitForTimeout(180);
    check(await id("component-title").inputValue() === "Card A", "The queued-action fixture must still display A before the B render arrives.");
    await id("component-remove").click();
    await page.waitForTimeout(2500);
    await setDelay(0);
    const afterStaleAction = await context();
    check(afterStaleAction.revision === saved.revision
      && afterStaleAction.document.components.some(component => component.id === a.id)
      && afterStaleAction.document.components.some(component => component.id === b.id),
    "A stale A inspector control must not remove A or B or advance the saved revision.");
    await id(`outline-component-${a.id}`).click();
    await page.waitForFunction(expected => document.querySelector('[data-testid="component-content"]')?.value === expected, expected);
    results.selection = { staleControlIgnored: true, bothCardsRetained: true, exactSavedText: true };

    await api("DELETE", `/api/ideas/${workspaceId.split(":")[1]}`);
    await page.reload();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-shell"]')?.dataset.ready === "true");
    await id("workspace-tab-source").click();
    const warning = page.locator('[data-testid="workspace-source-documents"] .text-warning-emphasis');
    await warning.waitFor({ state: "visible" });
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => document.documentElement.setAttribute("data-bs-theme", theme), theme);
      const colors = await warning.evaluate(element => ({ foreground: getComputedStyle(element).color,
        background: getComputedStyle(element.closest(".workspace-panel")).backgroundColor }));
      const luminance = color => {
        const rgb = color.match(/[\d.]+/g).slice(0, 3).map(Number).map(value => {
          const normalized = value / 255;
          return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
        });
        return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
      };
      const values = [luminance(colors.foreground), luminance(colors.background)].sort((a, b) => b - a);
      const ratio = (values[0] + 0.05) / (values[1] + 0.05);
      check(ratio >= 4.5, `The actual ${theme} warning contrast must meet WCAG AA: ${ratio}`);
      results.contrast.push({ theme, ...colors, ratio });
    }
    return { result: "PASS", workspaceId, ...results };
  } finally { await page.close(); }
}
