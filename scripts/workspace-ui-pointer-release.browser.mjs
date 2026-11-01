// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-pointer-release.browser.mjs
// Requires Chrome/CDP and an explicitly disposable local app/database; fixtures remain for inspection.
async basePage => {
  const site = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname, port: location.port }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(site.hostname) || site.port === "5123")
    throw new Error("Use an explicitly disposable loopback app for pointer release checks.");
  const page = await basePage.context().newPage();
  const prefix = `pointer-release-${Date.now()}`;
  const results = [];
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(site.origin + path, { method, data, timeout: 15000 });
    const text = await response.text(); check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const context = key => api("GET", `/api/workspaces/${encodeURIComponent(key)}/context`);
  let cdp;
  try {
    await page.setViewportSize({ width: 1440, height: 1000 });
    cdp = await page.context().newCDPSession(page);
    for (const resize of [false, true]) for (const moves of [false, true]) {
      const label = `${resize ? "resize" : "drag"}-${moves ? "move-then-newer-release" : "release-only"}`;
      const key = (await api("POST", "/api/workspaces", { operationId: `${prefix}-${label}`, actor: "pointer-release-browser", title: label, body: "Disposable trusted pointer fixture" })).workspaceId;
      const beforeCreate = await context(key);
      const placementId = "release-placement";
      await api("POST", `/api/workspaces/${encodeURIComponent(key)}/commands`, {
        operationId: `${prefix}-${label}-card`, actor: "pointer-release-browser", expectedRevision: beforeCreate.revision,
        action: "create_component", payload: { component: { id: "release-card", type: "note", title: label, content: "Release geometry" },
          placement: { id: placementId, componentId: "release-card", x: 100, y: 100, width: 260, height: 180, zIndex: 999 } }
      });
      await page.goto(site.origin + `/ideas/workspace/${key.replace(":", "/")}?view=canvas`);
      await page.waitForFunction(key => {
        const shell = document.querySelector('[data-testid="workspace-shell"]');
        return shell?.dataset.workspaceId === key && shell.dataset.ready === "true"
          && document.querySelector('[data-testid="workspace-canvas"]')?.dataset.ready === "true";
      }, key);
      const select = page.getByTestId(`canvas-select-${placementId}`);
      await select.scrollIntoViewIfNeeded(); await select.click();
      await page.waitForFunction(id => document.querySelector(`[data-testid="canvas-select-${id}"]`)?.getAttribute("aria-pressed") === "true", placementId);
      const handle = resize ? page.getByTestId(`canvas-resize-${placementId}`) : select;
      await handle.scrollIntoViewIfNeeded();
      const box = await handle.boundingBox(); check(box && box.width > 0 && box.height > 0, "Visible pointer handle required.");
      const before = await context(key), initial = before.document.placements.find(value => value.id === placementId);
      const down = { x: Math.round(box.x + box.width / 2), y: Math.round(box.y + box.height / 2) };
      const moved = { x: down.x + 10, y: down.y + 5 }, released = { x: down.x + 40, y: down.y + 30 };
      await page.evaluate(() => {
        window.__pointerReleaseEvents = [];
        for (const type of ["pointerdown", "pointermove", "pointerup"]) document.addEventListener(type, event => {
          window.__pointerReleaseEvents.push({ type, isTrusted: event.isTrusted, pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY, buttons: event.buttons });
        }, { capture: true });
      });
      await cdp.send("Input.dispatchMouseEvent", { type: "mouseMoved", ...down, buttons: 0 });
      await cdp.send("Input.dispatchMouseEvent", { type: "mousePressed", ...down, button: "left", buttons: 1, clickCount: 1 });
      if (moves) await cdp.send("Input.dispatchMouseEvent", { type: "mouseMoved", ...moved, buttons: 1 });
      await cdp.send("Input.dispatchMouseEvent", { type: "mouseReleased", ...released, button: "left", buttons: 0, clickCount: 1 });
      let after;
      for (let attempt = 0; attempt < 100; attempt++) {
        after = await context(key);
        if (after.revision > before.revision) break;
        await page.waitForTimeout(100);
      }
      const saved = after.document.placements.find(value => value.id === placementId);
      const events = await page.evaluate(() => window.__pointerReleaseEvents);
      const pressed = events.find(value => value.type === "pointerdown" && value.isTrusted);
      const release = events.find(value => value.type === "pointerup" && value.pointerId === pressed?.pointerId);
      const owningMoves = events.filter(value => value.type === "pointermove" && value.pointerId === pressed?.pointerId && value.buttons === 1);
      check(pressed && release?.isTrusted, `${label}: trusted owning pointerdown/up required.`);
      check(release.clientX === released.x && release.clientY === released.y, `${label}: raw release must have newer requested coordinates.`);
      check(owningMoves.length === (moves ? 1 : 0), `${label}: expected exact move-event sequence.`);
      check(after.revision === before.revision + 1, `${label}: exactly one geometry commit required.`);
      const delta = resize ? { x: saved.width - initial.width, y: saved.height - initial.height } : { x: saved.x - initial.x, y: saved.y - initial.y };
      check(delta.x === 40 && delta.y === 30, `${label}: release delta must persist (actual ${JSON.stringify(delta)}).`);
      results.push({ label, workspaceId: key, beforeRevision: before.revision, afterRevision: after.revision, down, lastMove: moves ? moved : null, released, persistedDelta: delta, events });
    }
    return { passed: results.length, cases: results };
  } finally { if (cdp) await cdp.detach().catch(() => {}); await page.close(); }
}
