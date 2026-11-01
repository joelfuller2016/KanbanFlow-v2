// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-ack-selection.browser.mjs
// Recover actual committed receipts against a disposable loopback database. No application handlers are replaced.
async basePage => {
  const site = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname, port: location.port }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(site.hostname) || site.port === "5123")
    throw new Error("Use an explicitly disposable loopback app for receipt selection checks.");
  const page = await basePage.context().newPage();
  const prefix = `ack-selection-${Date.now()}`;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(site.origin + path, { method, data, timeout: 15000 });
    const text = await response.text(); check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const results = [];
  try {
    for (const action of ["update_component", "remove_component"]) {
      const key = (await api("POST", "/api/workspaces", { operationId: `${prefix}-${action}`, actor: "ack-selection-browser", title: action, body: "Disposable receipt fixture" })).workspaceId;
      const context = () => api("GET", `/api/workspaces/${encodeURIComponent(key)}/context`);
      for (const label of ["a", "b"]) await api("POST", `/api/workspaces/${encodeURIComponent(key)}/commands`, {
        operationId: `${prefix}-${action}-${label}`, expectedRevision: (await context()).revision, actor: "ack-selection-browser", action: "create_component",
        payload: { component: { id: `card-${label}`, type: "note", title: `Saved ${label}`, content: `Saved complete ${label}` }, placement: { id: `place-${label}`, x: 40, y: 40, width: 280, height: 180 } }
      });
      await page.goto(site.origin + `/ideas/workspace/${key.replace(":", "/")}?view=outline`);
      await page.waitForFunction(key => document.querySelector('[data-testid="workspace-shell"]')?.dataset.workspaceId === key
        && document.querySelector('[data-testid="workspace-shell"]')?.dataset.ready === "true", key);
      await page.getByTestId("outline-component-card-a").click();
      await page.waitForFunction(key => JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key))?.selectedComponentId === "card-a", key);
      const selectionAtSend = await page.evaluate(key => {
        const draft = JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key));
        return { componentId: draft.selectedComponentId, placementId: draft.selectedPlacementId, sequence: draft.selectionSequence };
      }, key);
      await page.getByTestId("outline-component-card-b").click();
      await page.waitForFunction(() => document.querySelector('[data-testid="component-title"]')?.value === "Saved b");
      const authored = "  Complete unsaved B Ω\n" + "文".repeat(12000) + "\nretain last line  ";
      await page.getByTestId("component-content").fill(authored); await page.getByTestId("component-title").focus();
      await page.waitForFunction(({ key, authored }) => JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key))?.contentDrafts?.["card-b"]?.content === authored, { key, authored });
      const before = await context();
      const command = { operationId: `${prefix}-${action}-receipt`, expectedRevision: before.revision, actor: "ack-selection-browser", action,
        payload: action === "remove_component" ? { componentId: "card-a" } : { componentId: "card-a", title: "Saved by A" } };
      await api("POST", `/api/workspaces/${encodeURIComponent(key)}/commands`, command);
      const committed = await context();
      await page.evaluate(({ key, command, selectionAtSend }) => {
        const storageKey = "kanbanflow.workspace.draft.v1:" + key;
        const draft = JSON.parse(sessionStorage.getItem(storageKey));
        draft.pending = { command, draftSection: null,
          selectionAtSend: { componentId: selectionAtSend.componentId, placementId: selectionAtSend.placementId }, selectionSequenceAtSend: selectionAtSend.sequence };
        sessionStorage.setItem(storageKey, JSON.stringify(draft));
      }, { key, command, selectionAtSend });
      await page.reload();
      await page.getByTestId("workspace-retry-pending").waitFor({ state: "visible" });
      await page.getByTestId("workspace-retry-pending").click();
      await page.getByTestId("workspace-pending-command").waitFor({ state: "detached" });
      await page.waitForFunction(({ key, authored }) => {
        const draft = JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key));
        return draft?.pending == null && draft.selectedComponentId === "card-b" && draft.selectedPlacementId === "place-b"
          && draft.contentDrafts?.["card-b"]?.content === authored;
      }, { key, authored });
      check(await page.getByTestId("component-content").inputValue() === authored, `${action}: receipt must retain complete B editor contents.`);
      check((await context()).revision === committed.revision, `${action}: receipt retry must not create another mutation.`);
      results.push({ action, workspaceId: key, revision: committed.revision, selectedComponentId: "card-b", selectedPlacementId: "place-b", retainedCharacters: authored.length, identicalReceiptReplayed: true });
    }
    return { passed: results.length, cases: results };
  } finally { await page.close(); }
}
