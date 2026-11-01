// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-preview-target.browser.mjs
// Use only an explicitly disposable loopback application/database. Fixtures remain for audit inspection.
async basePage => {
  const { origin, hostname } = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(hostname)) throw new Error("A disposable loopback app is required.");
  const page = await basePage.context().newPage();
  const prefix = `reviewed-target-${Date.now()}`;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  let phase = "fixture";
  try {
    const project = await api("POST", "/api/projects", { name: prefix, description: "Disposable reviewed publication destination proof" });
    const key = `project:${project.id}`;
    const path = `/api/workspaces/${encodeURIComponent(key)}`;
    const context = () => api("GET", `${path}/context`);
    let operation = 0;
    const command = async (action, payload) => {
      const current = await context();
      return api("POST", `${path}/commands`, { operationId: `${prefix}-${++operation}`, actor: "workspace-reviewed-target-smoke",
        expectedRevision: current.revision, action, payload: { ...payload, expectedProjectPlanRevision: current.projectPlanRevision } });
    };
    const first = await api("POST", `/api/projects/${project.id}/features`, { name: `${prefix} reviewed A`, sortOrder: 1 });
    const second = await api("POST", `/api/projects/${project.id}/features`, { name: `${prefix} alternative B`, sortOrder: 2 });
    await command("propose_plan", { title: "Reviewed destination plan", items: ["First published", "Second published", "Unpublished"].map((title, index) => ({
      id: `item-${index}`, title, description: `Complete requirements for ${title}`, acceptanceCriteria: ["Verified"], dependsOn: [] })) });
    await command("accept_plan", { planRevisionId: (await context()).document.planRevisions.at(-1).id });
    const accepted = (await context()).acceptedPlan.items;
    const firstItem = accepted.find(item => item.title === "First published");
    const secondItem = accepted.find(item => item.title === "Second published");
    const unpublished = accepted.find(item => item.title === "Unpublished");
    const firstPublication = await command("publish_plan", { itemIds: [firstItem.id], featureId: first.id });
    await command("publish_plan", { itemIds: [secondItem.id], featureId: second.id });
    const before = await context();
    check(before.document.publications.length === 2, "Two distinct historical feature destinations are required.");
    await page.goto(origin + `/ideas/workspace/project/${project.id}?view=delivery`);
    await page.waitForFunction(key => document.querySelector('[data-testid="workspace-shell"]')?.dataset.ready === "true"
      && document.querySelector('[data-testid="workspace-shell"]')?.dataset.workspaceId === key, key);
    const id = value => page.getByTestId(value);
    phase = "first preview";
    await id(`delivery-select-${unpublished.id}`).check();
    await id("delivery-preview").click();
    await id("delivery-preview-result").waitFor({ state: "visible" });
    check((await id("delivery-preview-result").innerText()).includes(first.name), "The actual reviewed destination must be feature A.");
    const firstTaskId = firstPublication.publication.items[0].taskId;
    await api("PATCH", `/api/tasks/${firstTaskId}/feature`, { featureId: second.id, actor: "workspace-reviewed-target-other" });
    await api("DELETE", `/api/features/${first.id}`);
    const afterLegacyChange = await context();
    check(afterLegacyChange.revision === before.revision && afterLegacyChange.projectPlanRevision === before.projectPlanRevision,
      "The counterexample must retain both reviewed workspace revision tokens.");
    phase = "reject deleted reviewed target";
    await id("delivery-publish").click();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-error"]')?.textContent.includes("publication_target_missing"));
    const refused = await context();
    check(refused.document.publications.length === 2 && refused.revision === before.revision,
      "Deleting the reviewed destination must refuse publication without creating tasks elsewhere.");
    check(!refused.document.publications.some(mapping => mapping.canonicalItemId === unpublished.id), "The unpublished item must remain unpublished.");
    // A known 409 uses the existing explicit context-adoption gate before new commands.
    phase = "review latest context";
    await id("workspace-load-latest").click();
    await id("workspace-new-changes").waitFor({ state: "hidden" });
    phase = "new explicit preview";
    await id("delivery-feature-select").selectOption(String(second.id));
    await id(`delivery-select-${unpublished.id}`).check();
    await id("delivery-preview").click();
    await page.waitForFunction(name => document.querySelector('[data-testid="delivery-preview-result"]')?.textContent.includes(name), second.name);
    await id("delivery-publish").click();
    let recovered;
    for (let attempt = 0; attempt < 100; attempt++) {
      recovered = await context();
      if (recovered.document.publications.length === 3) break;
      await page.waitForTimeout(100);
    }
    const newMapping = recovered.document.publications.find(mapping => mapping.canonicalItemId === unpublished.id);
    check(newMapping?.featureId === second.id, "Only an explicit new preview may authorize publication into feature B.");
    const task = await api("GET", `/api/tasks/${newMapping.taskId}`);
    check(task.featureId === second.id && task.name === "Unpublished", "The recovered task must exist in the newly reviewed target.");
    return { result: "PASS", workspaceId: key, reviewedFeatureId: first.id, refusedRetarget: true,
      revisionTokensUnchangedByLegacyEdit: true, noTaskCreatedOnFailure: true, explicitNewPreviewPublishedTask: task.id, recoveredFeatureId: second.id };
  } catch (error) {
    const state = await page.evaluate(() => ({
      text: document.querySelector('[data-testid="workspace-shell"]')?.textContent?.slice(0, 8000),
      revision: document.querySelector('[data-testid="workspace-shell"]')?.dataset.revision,
      selections: Array.from(document.querySelectorAll('[data-testid^="delivery-select-"]')).map(element => ({ id: element.dataset.testid, checked: element.checked }))
    })).catch(() => null);
    throw new Error(`${phase}: ${error.message}\nReviewed target fixture state: ${JSON.stringify(state)}`);
  } finally { await page.close(); }
}
