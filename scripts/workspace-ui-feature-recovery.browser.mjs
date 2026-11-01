// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-feature-recovery.browser.mjs
// Disposable loopback app/database only. Keeps labeled fixtures for audit inspection.
async basePage => {
  const { origin, hostname } = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(hostname)) throw new Error("A disposable loopback app is required.");
  const page = await basePage.context().newPage();
  const prefix = `feature-recovery-${Date.now()}`;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  try {
    const project = await api("POST", "/api/projects", { name: prefix, description: "Disposable feature-replacement proof" });
    const key = `project:${project.id}`;
    const path = `/api/workspaces/${encodeURIComponent(key)}`;
    const context = () => api("GET", `${path}/context`);
    let sequence = 0;
    const command = async (action, payload) => {
      const current = await context();
      return api("POST", `${path}/commands`, { operationId: `${prefix}-${++sequence}`, actor: "workspace-feature-ui-smoke",
        expectedRevision: current.revision, action, payload: { ...payload, expectedProjectPlanRevision: current.projectPlanRevision } });
    };
    const item = (id, title) => ({ id, title, description: `Complete scope for ${title}`, acceptanceCriteria: ["Verified"], dependsOn: [] });
    const accept = async items => {
      await command("propose_plan", { title: "Recovery plan", items });
      const proposed = await context();
      await command("accept_plan", { planRevisionId: proposed.document.planRevisions.at(-1).id });
    };
    await accept([item("original", "Original task")]);
    const publication = await command("publish_plan", {});
    const mapped = publication.publication.items[0];
    const prior = (await context()).acceptedPlan.items[0];
    const replacement = await api("POST", `/api/projects/${project.id}/features`, { name: "Existing replacement", sortOrder: 4 });
    await api("PATCH", `/api/tasks/${mapped.taskId}/feature`, { featureId: replacement.id, actor: "workspace-feature-ui-smoke" });
    await api("DELETE", `/api/features/${mapped.featureId}`);
    await accept([{ id: prior.id, title: prior.title, description: prior.description,
      acceptanceCriteria: prior.acceptanceCriteria, dependsOn: prior.dependsOn, provenance: prior.provenance }, item("additional", "New unpublished task")]);
    const before = await context();
    await page.goto(origin + `/ideas/workspace/project/${project.id}?view=delivery`);
    await page.waitForFunction(key => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return shell?.dataset.ready === "true" && shell.dataset.workspaceId === key;
    }, key);
    const id = value => page.getByTestId(value);
    await id("delivery-feature-select").locator(`option[value="${replacement.id}"]`).waitFor({ state: "attached" });
    check(await id("delivery-preview-result").count() === 0, "Existing choices must be available before any preview succeeds.");
    await id("delivery-preview").click();
    await page.waitForFunction(() => document.querySelector('[data-testid="delivery-error"]')?.textContent.includes("publication_target_missing"));
    await id("delivery-feature-select").selectOption(String(replacement.id));
    await id("delivery-preview").click();
    await id("delivery-preview-result").waitFor({ state: "visible" });
    check((await id("delivery-new-count").innerText()).startsWith("1 new tasks"), "Selecting the existing replacement must preview one new task.");
    check((await context()).revision === before.revision, "The failed and recovered previews must perform no workspace writes.");
    await id("delivery-publish").click();
    let after;
    for (let attempt = 0; attempt < 100; attempt++) {
      after = await context();
      if (after.document.publications.length === 2) break;
      await page.waitForTimeout(100);
    }
    check(after.document.publications.length === 2, "The recovered UI publication must commit its new mapping.");
    const added = after.document.publications.find(mapping => mapping.taskId !== mapped.taskId);
    const task = await api("GET", `/api/tasks/${added.taskId}`);
    check(task.featureId === replacement.id && task.name === "New unpublished task", "The new real task must use the selected existing replacement feature.");
    return { result: "PASS", workspaceId: key, replacementFeatureId: replacement.id, newTaskId: task.id,
      choicesBeforePreview: true, defaultPreviewRejected: true, recoveredPreviewReadonly: true, uiPublicationUsedExistingFeature: true };
  } finally { await page.close(); }
}
