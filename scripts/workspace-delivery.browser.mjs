// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-delivery.browser.mjs
// Disposable loopback app only. This script leaves labeled fixtures for transport comparisons.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(["127.0.0.1", "localhost", "[::1]"].includes(hostname), "Use an explicitly disposable local workspace app.");
  const prefix = `workspace-delivery-${Date.now()}`;
  const id = value => page.getByTestId(value);
  const waitState = (predicate, value) => page.waitForFunction(predicate, value, { timeout: 15000 });
  const originalViewport = page.viewportSize();
  const checks = [], screenshots = [], longPasteInputs = [], errors = [];
  const onError = error => errors.push(error.message);
  page.on("pageerror", onError);
  let stage = "fixture creation", a, c, project, createdProjectId, firstTaskId, secondTaskId, thirdTaskId;
  let firstCanonicalId, secondCanonicalId, initialMappings, humanTaskSignature;
  const markStage = () => page.evaluate(value => {
    document.body.dataset.workspaceTestStage = value;
    document.body.dataset.workspaceTestSuite = "delivery";
  }, stage);
  const step = async (name, action) => { stage = name; await markStage(); await action(); checks.push(name); };
  const api = async (method, path, data, expected = 200) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.status() === expected, `${method} ${path}: expected ${expected}, received ${response.status()}: ${text}`);
    return text.length === 0 ? null : JSON.parse(text);
  };
  const context = key => api("GET", `/api/workspaces/${key}/context`);
  const tasks = () => api("GET", `/api/projects/${project.id}/tasks`);
  const task = taskId => api("GET", `/api/tasks/${taskId}`);
  const projectKey = () => `project:${project.id}`;
  const saved = async (read, predicate, description) => {
    const deadline = Date.now() + 15000;
    do {
      const value = await read();
      if (predicate(value)) return value;
      await page.waitForTimeout(100);
    } while (Date.now() < deadline);
    throw new Error(`Persisted state did not satisfy: ${description}`);
  };
  const ready = async key => {
    await waitState(expected => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return shell?.dataset.workspaceId === expected && shell?.dataset.ready === "true";
    }, key);
    await markStage();
  };
  const tab = async name => {
    await id(`workspace-tab-${name}`).click();
    await waitState(value => document.querySelector(`[data-testid="workspace-tab-${value}"]`)
      ?.getAttribute("aria-pressed")?.toLowerCase() === "true", name);
    await id(name === "plan" ? "workspace-plan" : "workspace-delivery").waitFor({ state: "visible", timeout: 15000 });
  };
  const open = async (key, view = "plan") => {
    await page.goto(`${origin}/ideas/workspace/${key.replace(":", "/")}?view=${view}`, { timeout: 15000 });
    await ready(key);
    await waitState(value => document.querySelector(`[data-testid="workspace-tab-${value}"]`)
      ?.getAttribute("aria-pressed")?.toLowerCase() === "true", view);
  };
  // The verified CLI expands long fill values into keystrokes. Model one paste through the real DOM input handler.
  const fillOrPasteText = async (locator, value) => {
    if (value.length <= 4096) return locator.fill(value);
    await locator.waitFor({ state: "visible", timeout: 15000 });
    await locator.focus();
    const observed = await locator.evaluate((element, value) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error("Long text paste requires a real textarea.");
      let inputEvents = 0, inputType = "", dataMatches = false;
      const listener = event => { inputEvents++; inputType = event.inputType; dataMatches = event.data === value; };
      element.addEventListener("input", listener, { passive: true });
      try {
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(element, value);
        element.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertFromPaste", data: value }));
        return { value: element.value, inputEvents, inputType, dataMatches, testId: element.getAttribute("data-testid") };
      } finally { element.removeEventListener("input", listener); }
    }, value);
    check(observed.value === value && observed.inputEvents === 1 && observed.inputType === "insertFromPaste" && observed.dataMatches,
      "Long plan text must enter intact through one real DOM paste input event.");
    longPasteInputs.push({ field: observed.testId, characters: value.length, inputEvents: observed.inputEvents });
  };
  const snapshot = async () => {
    const [source, canonical, currentTasks, sourceChanges, canonicalChanges] = await Promise.all([
      context(a.workspaceId), context(projectKey()), tasks(),
      api("GET", `/api/workspaces/${a.workspaceId}/changes`), api("GET", `/api/workspaces/${projectKey()}/changes`),
    ]);
    return JSON.stringify({ source, canonical, currentTasks, sourceChanges, canonicalChanges });
  };
  const taskSignature = value => JSON.stringify({ id: value.id, featureId: value.featureId, name: value.name,
    description: value.description, status: value.status, priority: value.priority, assignee: value.assignee,
    sortOrder: value.sortOrder, updatedAt: value.updatedAt, completedAt: value.completedAt, acceptanceCriteria: value.acceptanceCriteria });
  const externalProjectEdit = async suffix => {
    const current = await context(projectKey());
    await api("POST", `/api/workspaces/${projectKey()}/commands`, {
      operationId: `${prefix}-${suffix}`, expectedRevision: current.revision, action: "add_comment",
      payload: { text: `External collaborator advanced the shared project: ${suffix}` }, actor: "external-collaborator",
    });
  };
  const revealVersion = async planId => {
    const version = id(`plan-version-${planId}`);
    await version.waitFor({ state: "attached", timeout: 15000 });
    if (!await version.evaluate(element => element.open)) await version.locator(":scope > summary").click();
    return version;
  };
  const previewCounts = async (created, existing) => {
    await id("delivery-preview-result").waitFor({ state: "visible", timeout: 15000 });
    await waitState(({ created, existing }) => document.querySelector('[data-testid="delivery-new-count"]')?.textContent.trim() === `${created} new tasks`
      && document.querySelector('[data-testid="delivery-existing-count"]')?.textContent.trim() === `${existing} already linked`, { created, existing });
  };
  const screenshot = async name => {
    const path = `_sandbox/579/${prefix}-${name}.png`;
    await page.screenshot({ path, fullPage: true, timeout: 15000 });
    screenshots.push(path);
  };
  const bodyA = "\n  # Existing-project idea Ω\n\n    Preserve this source narrative  \n".repeat(80) + "\nFINAL IDEA A SOURCE Ω  \n";
  const bodyC = "\n  # New-project idea 漢字\n\n    Preserve the whole original brief  \n".repeat(90) + "\nFINAL IDEA C SOURCE Ω  \n";
  const planC = "\n  ## Starting project plan Ω\n\n- Preserve significant final spaces  \n";
  const specification = "\n  ## Delivery rationale Ω\n\n" + "Complete evidence, context and decisions 漢字 🧭. ".repeat(150) + "\n\nFINAL DELIVERY SPECIFICATION Ω  \n";
  const firstTitle = `${prefix} first task`;
  const secondTitle = `${prefix} dependent task`;
  const thirdTitle = `${prefix} expanded task`;
  const criteria = "  Complete original evidence Ω  \nPreserve the final delivery requirement  ";
  check(specification.length > 4096, "The plan specification must exercise the single-paste path.");
  try {
    a = await api("POST", "/api/workspaces", { operationId: `${prefix}-a`, actor: "delivery-browser-fixture",
      title: `${prefix} idea A`, body: bodyA, plan: "\n  Original A plan Ω  \n" });
    c = await api("POST", "/api/workspaces", { operationId: `${prefix}-c`, actor: "delivery-browser-fixture",
      title: `${prefix} idea C`, body: bodyC, plan: planC });
    project = await api("POST", "/api/projects", { name: `${prefix} existing project`, description: "Existing project description Ω",
      goals: "Existing project goals", readme: "# Existing project readme\n\n    Preserve target project documents Ω" }, 201);
    await page.setViewportSize({ width: 1440, height: 1000 });

    await step("UI links an idea to an existing project without replacing either source", async () => {
      await open(a.workspaceId, "delivery");
      await id("delivery-project-select").selectOption(String(project.id));
      await id("delivery-link-project").click();
      const linked = await saved(() => context(a.workspaceId), value => value.projectId === project.id, "UI association");
      check(linked.originalSource.body === bodyA && linked.source.body === bodyA, "Linking must retain the complete original/current idea narrative.");
      check(linked.originalProjectSource.readme === project.readme && linked.projectSource.body === project.description,
        "Linking must snapshot the actual target documents without overwriting them.");
      await id("delivery-open-project").waitFor({ state: "visible", timeout: 15000 });
      check(await id("delivery-open-project").getAttribute("href") === `/project/${project.id}`, "The association must expose the real project identity.");
    });

    await step("UI creates one project from the other idea with complete source documents", async () => {
      await open(c.workspaceId, "delivery");
      await id("delivery-project-name").fill(`${prefix} project created from C`);
      await id("delivery-create-project").click();
      const current = await saved(() => context(c.workspaceId), value => value.projectId !== null, "UI-created project");
      createdProjectId = current.projectId;
      check(createdProjectId !== project.id && current.workspaceId === c.workspaceId, "Creating C's project must preserve its idea identity and use a distinct project.");
      const created = await api("GET", `/api/projects/${createdProjectId}`);
      check(created.description === bodyC && created.goals === planC && created.readme.includes(bodyC) && created.readme.includes(planC),
        "The UI-created project must retain the whole source brief, plan and meaningful Markdown whitespace.");
      check(current.originalSource.body === bodyC && current.originalProjectSource.readme === created.readme,
        "Both original source and created-project snapshots must remain complete.");
    });

    let proposal;
    await step("one pasted plan draft survives a stale shared revision and explicit reapply", async () => {
      await open(a.workspaceId, "plan");
      await id("plan-title").fill(`${prefix} initial delivery plan`);
      await id("plan-add-item").click();
      const fields = page.locator('fieldset[data-testid^="plan-item-"]');
      await fields.nth(0).getByRole("textbox", { name: "Title", exact: true }).fill(firstTitle);
      await fillOrPasteText(fields.nth(0).getByRole("textbox", { name: "Description / Markdown", exact: true }), specification);
      await fields.nth(0).getByRole("textbox", { name: "Acceptance criteria (one per line)", exact: true }).fill(criteria);
      await id("plan-add-item").click();
      await fields.nth(1).getByRole("textbox", { name: "Title", exact: true }).fill(secondTitle);
      await fields.nth(1).getByRole("textbox", { name: "Description / Markdown", exact: true }).fill("\n  Builds on the first task Ω  \n");
      await fields.nth(1).getByRole("checkbox", { name: firstTitle, exact: true }).check();
      await waitState(() => document.querySelector('[data-testid="plan-propose"]')?.disabled === false);
      await externalProjectEdit("draft-became-stale");
      await id("workspace-refresh").click();
      await id("plan-reapply").waitFor({ state: "visible", timeout: 15000 });
      check(await fields.nth(0).getByRole("textbox", { name: "Description / Markdown", exact: true }).inputValue() === specification,
        "Refreshing a stale shared plan must retain the complete pasted draft.");
      check(await id("plan-propose").isDisabled(), "A stale plan draft must require explicit shared-revision review.");
      check((await tasks()).length === 0, "Developing a draft must not create Kanban tasks.");
      await id("plan-reapply").click();
      await id("plan-propose").click();
      const current = await saved(() => context(a.workspaceId), value => value.document.planRevisions.some(item => item.title === `${prefix} initial delivery plan`), "saved linked proposal");
      proposal = current.document.planRevisions.find(item => item.title === `${prefix} initial delivery plan`);
      check(proposal.expectedProjectPlanRevision === current.projectPlanRevision && proposal.items[0].description === specification,
        "The saved proposal must retain the deliberately reviewed canonical baseline and complete text.");
    });

    await step("a saved stale proposal requires explicit acceptance of the reviewed canonical revision", async () => {
      await externalProjectEdit("acceptance-became-stale");
      await id("workspace-refresh").click();
      await revealVersion(proposal.id);
      await id(`plan-rebase-accept-${proposal.id}`).waitFor({ state: "visible", timeout: 15000 });
      check(await id(`plan-accept-${proposal.id}`).isDisabled(), "A stale saved proposal must not silently restamp its acceptance baseline.");
      check((await id(`plan-version-${proposal.id}`).innerText()).includes("FINAL DELIVERY SPECIFICATION Ω"),
        "The full saved proposal must remain visible while its canonical revision is stale.");
      await id(`plan-rebase-accept-${proposal.id}`).click();
      const accepted = await saved(() => context(a.workspaceId), value => value.document.planRevisions.some(item => item.id === proposal.id && item.status === "Accepted") && value.acceptedPlan?.items.length === 2, "explicit shared-plan acceptance");
      const first = accepted.acceptedPlan.items.find(item => item.title === firstTitle);
      const second = accepted.acceptedPlan.items.find(item => item.title === secondTitle);
      firstCanonicalId = first.id;
      secondCanonicalId = second.id;
      check(first.provenance.sourceWorkspaceId === a.workspaceId && first.provenance.canonicalItemId === first.id
        && second.dependsOn.includes(first.id), "Acceptance must expose actual canonical identities and remapped dependency provenance.");
      check((await tasks()).length === 0, "Accepting a plan must wait for explicit task publication.");
    });

    await step("rendered full publication preview is read only and selection changes invalidate review", async () => {
      await tab("delivery");
      await id("delivery-feature-name").fill(`${prefix} reviewed feature`);
      const before = await snapshot();
      await id("delivery-preview").click();
      await previewCounts(2, 0);
      const preview = id(`delivery-item-${firstCanonicalId}`);
      await preview.getByRole("heading", { name: "Delivery rationale Ω", exact: true }).waitFor({ state: "visible", timeout: 15000 });
      check((await preview.innerText()).includes("FINAL DELIVERY SPECIFICATION Ω")
        && (await preview.innerText()).includes("Preserve the final delivery requirement"), "The reviewed preview must visibly render complete item Markdown and real acceptance criteria.");
      check(await snapshot() === before && (await tasks()).length === 0, "Read-only preview must not create tasks, revisions, mappings or activity.");
      await id(`delivery-select-${firstCanonicalId}`).uncheck();
      await id("delivery-preview-result").waitFor({ state: "hidden", timeout: 15000 });
      check(await id("delivery-publish").count() === 0, "Changing the reviewed item selection must remove the old publication action.");
      await id("delivery-preview").click();
      await id("delivery-error").waitFor({ state: "visible", timeout: 15000 });
      check((await id("delivery-error").innerText()).includes("missing_publication_dependency"), "A dependent-only selection must fail with the actionable dependency code.");
      check(!await id(`delivery-select-${firstCanonicalId}`).isChecked() && await id(`delivery-select-${secondCanonicalId}`).isChecked(),
        "A refused preview must preserve the user's exact selected subset.");
      check(await snapshot() === before, "A refused preview must leave all real saved state unchanged.");
      await id(`delivery-select-${firstCanonicalId}`).check();
      await id("delivery-error").waitFor({ state: "hidden", timeout: 15000 });
      await id("delivery-preview").click();
      await previewCounts(2, 0);
    });

    await step("a stale reviewed preview preserves selection and requires another explicit review", async () => {
      await externalProjectEdit("reviewed-preview-became-stale");
      const beforeRefresh = await snapshot();
      await id("workspace-refresh").click();
      await id("delivery-preview-result").getByText("The saved context advanced. Preview again before publishing.", { exact: true }).waitFor({ state: "visible", timeout: 15000 });
      check(await id("delivery-publish").isDisabled(), "Publishing must be disabled for an obsolete reviewed canonical revision.");
      check(await id(`delivery-select-${firstCanonicalId}`).isChecked() && await id(`delivery-select-${secondCanonicalId}`).isChecked()
        && await id("delivery-feature-name").inputValue() === `${prefix} reviewed feature`, "Refreshing a stale preview must preserve selection and feature intent.");
      check(await snapshot() === beforeRefresh, "Refreshing stale review context must not publish any work.");
      await id("delivery-preview").click();
      await previewCounts(2, 0);
      await waitState(() => document.querySelector('[data-testid="delivery-publish"]')?.disabled === false);
      await screenshot("reviewed-preview");
    });

    await step("explicit UI publication creates real tasks and durable canonical mappings", async () => {
      await id("delivery-publish").click();
      const canonical = await saved(() => context(projectKey()), value => value.document.publications.length === 2, "first task mappings");
      const createdTasks = await saved(tasks, value => value.length === 2, "first actual Kanban tasks");
      initialMappings = canonical.document.publications;
      firstTaskId = initialMappings.find(item => item.canonicalItemId === firstCanonicalId).taskId;
      secondTaskId = initialMappings.find(item => item.canonicalItemId === secondCanonicalId).taskId;
      check(firstTaskId !== secondTaskId && createdTasks.some(item => item.id === firstTaskId) && createdTasks.some(item => item.id === secondTaskId),
        "Published canonical items must map to two distinct real task identities.");
      const first = await task(firstTaskId);
      check(first.description === specification && JSON.stringify(first.acceptanceCriteria.map(item => item.text)) === JSON.stringify(criteria.split("\n")),
        "Published task detail must preserve every authored description character and acceptance line.");
      check(initialMappings.every(item => item.projectId === project.id && item.sourceWorkspaceId === a.workspaceId),
        "Durable mappings must retain the selected project and originating idea identity.");
      await id("delivery-preview-result").waitFor({ state: "hidden", timeout: 15000 });
    });

    await step("Kanban UI status changes appear through current task inspection and the return workspace link", async () => {
      await tab("plan");
      await id(`plan-inspect-task-${firstTaskId}`).click();
      await id("workspace-task-status").waitFor({ state: "visible", timeout: 15000 });
      check((await id("workspace-task-status").innerText()).includes("Backlog"), "The inspector must read the real initial task status.");
      check((await id("workspace-task-inspector").innerText()).includes("FINAL DELIVERY SPECIFICATION Ω"), "Current task inspection must visibly render the complete real description.");
      await id("workspace-task-kanban").click();
      await page.waitForURL(`${origin}/project/${project.id}`, { timeout: 15000 });
      await waitState(() => window.KanbanFlow?._boardSortables?.some(sortable => sortable.el?.isConnected));
      await markStage();
      await id(`board-card-${firstTaskId}-title`).click();
      await id("task-detail-panel").waitFor({ state: "visible", timeout: 15000 });
      await page.locator("#task-detail-status").selectOption("2");
      const current = await saved(() => task(firstTaskId), value => value.status === "InProgress", "actual Kanban status edit");
      humanTaskSignature = taskSignature(current);
      await id("task-detail-close-button").click();
      await id("task-detail-panel").waitFor({ state: "hidden", timeout: 15000 });
      check(await id("project-workspace-link").getAttribute("href") === `/ideas/workspace/project/${project.id}?view=plan`, "The Kanban header must link to its primary project workspace and plan view.");
      await id("project-workspace-link").click();
      await page.waitForURL(`${origin}/ideas/workspace/project/${project.id}?view=plan`, { timeout: 15000 });
      await ready(projectKey());
      await id("workspace-plan").waitFor({ state: "visible", timeout: 15000 });
      await id(`plan-inspect-task-${firstTaskId}`).click();
      await waitState(() => document.querySelector('[data-testid="workspace-task-status"]')?.textContent.includes("InProgress"));
      await id("workspace-task-refresh").click();
      await waitState(() => document.querySelector('[data-testid="workspace-task-status"]')?.textContent.includes("InProgress"));
      check((await id("workspace-task-inspector").innerText()).includes(`#${firstTaskId}`), "The return workspace must inspect the same real task identity.");
    });

    await step("copying the canonical plan preserves provenance and adds one new authored item", async () => {
      await open(a.workspaceId, "plan");
      await id("plan-copy-accepted").click();
      await waitState(() => document.querySelectorAll('fieldset[data-testid^="plan-item-"]').length === 2);
      const fields = page.locator('fieldset[data-testid^="plan-item-"]');
      check(await fields.nth(0).getByRole("textbox", { name: "Description / Markdown", exact: true }).inputValue() === specification,
        "Copying the canonical plan must retain the complete existing specification.");
      check((await fields.nth(0).innerText()).includes(`Existing canonical item from ${a.workspaceId}`), "Copied publication provenance must be visible and retained.");
      await id("plan-title").fill(`${prefix} expanded delivery plan`);
      await id("plan-add-item").click();
      await fields.nth(2).getByRole("textbox", { name: "Title", exact: true }).fill(thirdTitle);
      await fields.nth(2).getByRole("textbox", { name: "Description / Markdown", exact: true }).fill("\n  A new increment after human Kanban edits Ω  \n");
      await fields.nth(2).getByRole("textbox", { name: "Acceptance criteria (one per line)", exact: true }).fill("  Preserve the already published work Ω  ");
      await id("plan-propose").click();
      const proposed = await saved(() => context(a.workspaceId), value => value.document.planRevisions.some(item => item.title === `${prefix} expanded delivery plan`), "expanded proposal");
      const draft = proposed.document.planRevisions.find(item => item.title === `${prefix} expanded delivery plan`);
      check(draft.items.length === 3 && draft.items.find(item => item.id === firstCanonicalId).provenance.canonicalItemId === firstCanonicalId,
        "The expanded draft must retain copied canonical identities and include one distinct new local item.");
      await revealVersion(draft.id);
      await id(`plan-accept-${draft.id}`).click();
      await saved(() => context(a.workspaceId), value => value.acceptedPlan?.items.length === 3 && value.document.planRevisions.some(item => item.id === draft.id && item.status === "Accepted"), "expanded shared acceptance");
    });

    await step("narrow keyboard publication adds only new work and preserves the human-edited task", async () => {
      await tab("delivery");
      await page.setViewportSize({ width: 390, height: 844 });
      for (const testId of ["workspace-tab-delivery", "delivery-feature-select", "delivery-feature-name", "delivery-preview"]) {
        await id(testId).scrollIntoViewIfNeeded();
        const box = await id(testId).boundingBox();
        check(box && box.x >= -1 && box.x + box.width <= 391, `${testId} must fit the narrow viewport.`);
      }
      await id("delivery-preview").click();
      await previewCounts(1, 2);
      check((await id(`delivery-item-${firstCanonicalId}`).innerText()).includes("InProgress"), "The expanded preview must show current human task status.");
      check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), "Delivery controls and full preview must not cause page-wide overflow at390px.");
      await screenshot("narrow-preview");
      await id("delivery-publish").focus();
      await page.keyboard.press("Enter");
      const current = await saved(() => context(projectKey()), value => value.document.publications.length === 3, "expanded task mappings");
      const currentTasks = await saved(tasks, value => value.length === 3, "only one additional task");
      check(current.document.publications.find(item => item.canonicalItemId === firstCanonicalId).taskId === firstTaskId
        && current.document.publications.find(item => item.canonicalItemId === secondCanonicalId).taskId === secondTaskId,
        "Canonical expansion must retain both previously published task identities.");
      thirdTaskId = currentTasks.find(item => item.name === thirdTitle).id;
      check(thirdTaskId !== firstTaskId && thirdTaskId !== secondTaskId, "Only the new canonical item may create a new task identity.");
      check(taskSignature(await task(firstTaskId)) === humanTaskSignature, "Publishing the expansion must preserve the real human-edited task status, content, ordering and criteria.");
      check(current.document.publications.every(item => item.featureId === initialMappings[0].featureId), "Default publication must reuse the reviewed durable feature.");
      const sourceC = await context(c.workspaceId);
      check(sourceC.projectId === createdProjectId && sourceC.originalSource.body === bodyC && sourceC.source.plan === planC,
        "Working on A's delivery must preserve C's independent project and complete documents.");
      check((await api("GET", `/api/projects/${createdProjectId}/tasks`)).length === 0, "Publishing A's accepted items must not create tasks in C's project.");
      check(errors.length === 0, `Delivery flow must have no uncaught browser errors: ${errors.join(" | ")}`);
    });

    await step("captured source disclosures retain original and later observed full project documents after deletion", async () => {
      await page.setViewportSize({ width: 1440, height: 1000 });
      const historyKey = `project:${createdProjectId}`;
      const original = await context(historyKey);
      const newDescription = "# Changed C description Ω\n\n" + "Complete later description 漢字. ".repeat(180)
        + "\n\n    keep internal indentation  \n\nFINAL CHANGED C DESCRIPTION";
      const newGoals = "## Changed C goals Ω\n\n- Preserve the later goals  \n- FINAL CHANGED C GOAL";
      const newReadme = "# Changed C readme Ω\n\n" + "Complete later project evidence 🧭. ".repeat(220)
        + "\n\n```text\n    keep exact readme indentation and spaces  \n```\n\nFINAL CHANGED C README";
      const edited = await api("PUT", `/api/projects/${createdProjectId}`, { name: original.source.title,
        description: newDescription, goals: newGoals, readme: newReadme });
      check(edited.description === newDescription && edited.goals === newGoals && edited.readme === newReadme,
        "The fixture's actual legacy edit must preserve the complete requested documents before observation.");
      const observedContext = await context(historyKey);
      await api("POST", `/api/workspaces/${historyKey}/commands`, {
        operationId: `${prefix}-observe-source-history`, expectedRevision: observedContext.revision,
        action: "add_comment", payload: { text: "Module write observes later complete project documents" }, actor: "delivery-browser-history",
      });
      const captured = await context(c.workspaceId);
      const observed = captured.projectSourceSnapshots.find(item => item.source.readme === newReadme);
      check(observed && observed.source.body === newDescription && observed.source.plan === newGoals,
        "An audited module write must retain the complete later source as a captured version.");
      check(captured.originalProjectSource.readme === original.originalSource.readme,
        "Capturing a later source must preserve the earliest full project snapshot.");
      await api("DELETE", `/api/projects/${createdProjectId}`, undefined, 204);
      await open(c.workspaceId, "source");
      await id("workspace-source-documents").waitFor({ state: "visible", timeout: 15000 });
      const missing = await context(c.workspaceId);
      check(missing.projectMissing && missing.projectId === createdProjectId && missing.originalProjectSource.readme === original.originalSource.readme,
        "Deleting C's empty project must retain its identity and original source snapshot.");
      check(missing.projectSourceSnapshots.some(item => item.id === observed.id && item.source.readme === newReadme),
        "Deleting the source must retain its complete later observed version.");
      const originals = id("workspace-source-documents").locator(":scope > details");
      const originalDisclosure = originals.filter({ hasText: `Original linked project snapshot · ${original.originalSource.title}` });
      await originalDisclosure.locator(":scope > summary").click();
      check((await originalDisclosure.innerText()).includes("FINAL IDEA C SOURCE Ω"),
        "The source panel must visibly retain the original project's complete brief after deletion.");
      const version = id(`workspace-source-snapshot-${observed.id}`);
      await version.waitFor({ state: "visible", timeout: 15000 });
      check((await version.innerText()).includes("Captured by delivery-browser-history"), "The captured source UI must retain actual capture attribution.");
      await version.locator(":scope > details > summary").click();
      for (const title of ["Exact source text", "Exact plan text", "Exact readme text"])
        await version.locator("details > summary").filter({ hasText: new RegExp(`^${title}$`) }).click();
      const exact = version.locator("pre.workspace-source-text");
      for (let index = 0; index < 3; index++) await exact.nth(index).waitFor({ state: "visible", timeout: 15000 });
      check(await exact.nth(0).textContent() === newDescription && await exact.nth(1).textContent() === newGoals
        && await exact.nth(2).textContent() === newReadme, "Expanded visible source disclosures must retain every character of later Description, Goals and Readme.");
      check((await version.innerText()).includes("FINAL CHANGED C README"), "The captured source display must render its actual final readme marker.");
      check((await tasks()).length === 3 && taskSignature(await task(firstTaskId)) === humanTaskSignature,
        "Deleting only C's empty fixture project must preserve A's published tasks and current human status.");
      check(errors.length === 0, `Source history must have no uncaught browser errors: ${errors.join(" | ")}`);
      await screenshot("retained-source-history");
    });

    return { passed: true, prefix, checks, sourceWorkspace: a.workspaceId, independentWorkspace: c.workspaceId,
      projectWorkspace: projectKey(), createdProjectId, firstCanonicalId, secondCanonicalId,
      taskIds: [firstTaskId, secondTaskId, thirdTaskId], specificationCharacters: specification.length, longPasteInputs, screenshots };
  } catch (error) {
    await page.screenshot({ path: `_sandbox/579/${prefix}-failure.png`, fullPage: true, timeout: 15000 }).catch(() => {});
    throw new Error(`${stage}: ${error.message}`);
  } finally {
    page.off("pageerror", onError);
    if (originalViewport) await page.setViewportSize(originalViewport);
  }
}
