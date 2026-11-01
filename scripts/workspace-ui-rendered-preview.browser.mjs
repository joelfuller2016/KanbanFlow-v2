// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-rendered-preview.browser.mjs
// Run only against an explicitly disposable loopback app/database. Actual server frames are delayed;
// application handlers, preview results, browser storage and domain writes remain unmodified.
async basePage => {
  const site = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname, port: location.port }));
  if (site.hostname !== "127.0.0.1" || site.port === "5123") throw new Error("An owned disposable loopback app is required.");
  const page = await basePage.context().newPage();
  const prefix = "rendered-preview-" + Date.now();
  const results = [];
  const check = (value, message) => { if (!value) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(site.origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  let phase = "disposable fixture";
  try {
    const health = await api("GET", "/api/admin/health");
    check(health.db.source === "env" && health.db.path.replaceAll("\\", "/").includes("/_sandbox/"), "An explicit scratch database is required.");
    await page.addInitScript(() => {
      window.__reviewedPreviewFrames = { delay: 0, queued: 0, messages: 0, saves: 0, marker: "", matches: 0 };
      const NativeSocket = window.WebSocket;
      window.WebSocket = class extends NativeSocket {
        set onmessage(handler) {
          super.onmessage = handler ? event => {
            const state = window.__reviewedPreviewFrames;
            state.messages++;
            const decoded = typeof event.data === "string" ? event.data : new TextDecoder().decode(event.data);
            if (decoded.includes("saveDraft")) state.saves++;
            if (state.marker && decoded.includes(state.marker)) state.matches++;
            if (state.delay) {
              state.queued++;
              window.setTimeout(() => { try { handler.call(this, event); } finally { state.queued--; } }, state.delay);
            } else handler.call(this, event);
          } : null;
        }
        get onmessage() { return super.onmessage; }
      };
    });
    await page.setViewportSize({ width: 1440, height: 1100 });
    for (const replacement of ["target", "content"]) {
      phase = replacement + ": fixture";
      const project = await api("POST", "/api/projects", { name: prefix + " " + replacement });
      const key = "project:" + project.id;
      const path = "/api/workspaces/" + encodeURIComponent(key);
      const context = () => api("GET", path + "/context");
      let operation = 0;
      const command = async (action, payload) => {
        const current = await context();
        return api("POST", path + "/commands", { operationId: prefix + "-" + replacement + "-" + (++operation),
          actor: "rendered-preview-browser", expectedRevision: current.revision, action,
          payload: { ...payload, expectedProjectPlanRevision: current.projectPlanRevision } });
      };
      const first = await api("POST", `/api/projects/${project.id}/features`, { name: prefix + " feature A", sortOrder: 1 });
      const second = await api("POST", `/api/projects/${project.id}/features`, { name: prefix + " feature B", sortOrder: 2 });
      const description = label => `\r\n  ## ${prefix} content ${label} Ω\r\n` + "文".repeat(2000) + `\r\nCOMPLETE ${label}  \r\n`;
      await command("propose_plan", { title: "Review exactly this publication", items: ["A", "B"].map(label => ({
        id: "item-" + label, title: "Task " + label, description: description(label),
        acceptanceCriteria: [`  Complete criterion ${label} Ω  `], dependsOn: [] })) });
      await command("accept_plan", { planRevisionId: (await context()).document.planRevisions.at(-1).id });
      const initial = await context();
      const itemA = initial.acceptedPlan.items.find(item => item.title === "Task A");
      const itemB = initial.acceptedPlan.items.find(item => item.title === "Task B");
      const id = value => page.getByTestId(value);
      await page.goto(site.origin + `/ideas/workspace/project/${project.id}?view=delivery`);
      await page.waitForFunction(key => { const shell = document.querySelector('[data-testid="workspace-shell"]');
        return shell?.dataset.workspaceId === key && shell.dataset.ready === "true"; }, key);
      await id("delivery-select-" + itemA.id).check();
      await id("delivery-select-" + itemB.id).uncheck();
      await id("delivery-feature-select").selectOption(String(first.id));
      await id("delivery-preview").click();
      await id("delivery-preview-result").waitFor({ state: "visible" });
      check((await id("delivery-preview-result").innerText()).includes(first.name), "Preview A must display its feature.");
      check(await id("delivery-item-" + itemA.id).count() === 1 && await id("delivery-item-" + itemB.id).count() === 0,
        "Preview A must display only item A.");
      const before = { context: await context(), tasks: await api("GET", `/api/projects/${project.id}/tasks`),
        changes: await api("GET", path + "/changes?afterActivityId=0&limit=100") };
      phase = replacement + ": actual replacement frames while A remains displayed";
      const saves = await page.evaluate(() => { const state = window.__reviewedPreviewFrames; state.delay = 2500; return state.saves; });
      if (replacement === "target") await id("delivery-feature-select").selectOption(String(second.id));
      else {
        await id("delivery-select-" + itemA.id).uncheck();
        await id("delivery-select-" + itemB.id).check();
      }
      await page.waitForFunction(saves => window.__reviewedPreviewFrames.saves > saves, saves, { timeout: 1800 });
      const marker = replacement === "target" ? second.name : prefix + " content B";
      await page.evaluate(marker => { const state = window.__reviewedPreviewFrames; state.marker = marker; state.matches = 0; }, marker);
      await id("delivery-preview").click({ timeout: 1800 });
      await page.waitForFunction(() => window.__reviewedPreviewFrames.matches > 0, null, { timeout: 1800 });
      check((await id("delivery-preview-result").innerText()).includes(first.name), "The old feature A preview must still be displayed.");
      check(await id("delivery-item-" + itemA.id).count() === 1 && await id("delivery-item-" + itemB.id).count() === 0,
        "The old item A preview must still be displayed.");
      await id("delivery-publish").click({ timeout: 1800 });
      await page.waitForFunction(() => window.__reviewedPreviewFrames.queued === 0, null, { timeout: 20000 });
      const frames = await page.evaluate(() => { const state = window.__reviewedPreviewFrames; state.delay = 0;
        return { receivedMessages: state.messages, replacementMarkerFrames: state.matches, delayedFramesRemaining: state.queued }; });
      await page.waitForFunction(() => document.querySelector('[data-testid="delivery-error"]')?.textContent.includes("preview changed"));
      const after = { context: await context(), tasks: await api("GET", `/api/projects/${project.id}/tasks`),
        changes: await api("GET", path + "/changes?afterActivityId=0&limit=100") };
      check(JSON.stringify(after) === JSON.stringify(before), "Clicking the old rendered preview must save no task, revision, mapping or audit event.");
      const staleMessage = (await id("delivery-error").innerText()).trim();
      const expectedItem = replacement === "content" ? itemB : itemA;
      const expectedFeature = replacement === "target" ? second : first;
      check((await id("delivery-preview-result").innerText()).includes(expectedFeature.name), "Replacement B must now be visible for review.");
      check(await id("delivery-item-" + expectedItem.id).count() === 1, "The replacement's selected content must be displayed.");
      phase = replacement + ": fresh B publication";
      await id("delivery-publish").click();
      let published;
      for (let attempt = 0; attempt < 100; attempt++) {
        published = await context();
        if (published.document.publications.length === 1) break;
        await page.waitForTimeout(100);
      }
      const mapping = published.document.publications[0];
      check(published.document.publications.length === 1 && mapping.canonicalItemId === expectedItem.id && mapping.featureId === expectedFeature.id,
        "Fresh B must publish only its reviewed item into its reviewed feature.");
      const task = await api("GET", "/api/tasks/" + mapping.taskId);
      check(task.description === expectedItem.description && task.name === expectedItem.title && task.featureId === expectedFeature.id,
        "Fresh B must preserve its full exact description, title and feature.");
      check(JSON.stringify(task.acceptanceCriteria.map(item => item.text)) === JSON.stringify(expectedItem.acceptanceCriteria),
        "Fresh B must preserve complete criteria.");
      results.push({ replacement, workspaceId: key, workspaceRevision: initial.revision, projectPlanRevision: initial.projectPlanRevision,
        staleMessage, staleClickWroteNothing: true, oldPreviewStillDisplayedAfterBReceived: true, freshTaskId: task.id,
        freshCanonicalItemId: expectedItem.id, freshFeatureId: expectedFeature.id, exactDescriptionCharacters: task.description.length, frames });
    }
    return { passed: true, staleCases: results.length, freshCases: results.length, actualServerFramesDelayed: true, fixtures: results, humanApproval: "pending" };
  } catch (error) {
    const state = await page.evaluate(() => ({ text: document.body.innerText, frames: window.__reviewedPreviewFrames })).catch(() => null);
    throw new Error(`${phase}: ${error.message}\n${JSON.stringify(state)}`);
  } finally {
    await page.waitForFunction(() => !window.__reviewedPreviewFrames || window.__reviewedPreviewFrames.queued === 0, null, { timeout: 20000 }).catch(() => {});
    await page.close();
  }
}
