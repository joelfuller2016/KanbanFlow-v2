// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-restoration.browser.mjs
// Run only against a disposable local app. Uses real APIs, browser draft storage and UI handlers.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(["127.0.0.1", "localhost", "[::1]"].includes(hostname), "A disposable loopback app is required.");
  const prefix = `workspace-ui-restoration-${Date.now()}`;
  let captureSequence = 0;
  const componentId = "restoration-component", placementId = "restoration-placement";
  const checks = [], errors = [], opened = [];
  const onError = error => errors.push(error.message);
  page.on("pageerror", onError);
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text.length ? JSON.parse(text) : null;
  };
  const context = key => api("GET", `/api/workspaces/${encodeURIComponent(key)}/context`);
  const route = key => `/ideas/workspace/${key.replace(":", "/")}`;
  const ready = (doc, key) => doc.waitForFunction(key => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    return shell?.dataset.workspaceId === key && shell.dataset.ready === "true";
  }, key, { timeout: 15000 });
  const revision = (doc, value) => doc.waitForFunction(value =>
    Number(document.querySelector('[data-testid="workspace-shell"]')?.dataset.revision) === value, value, { timeout: 15000 });
  const switchTo = async key => {
    await page.getByTestId("workspace-switcher").selectOption(key);
    await page.waitForURL(origin + route(key));
    await ready(page, key);
  };
  const identity = (doc, key) => doc.evaluate(async key => {
    const module = await import("/Components/Pages/IdeaWorkspace.razor.js");
    const session = await module.initialize(key);
    return { clientId: session.clientId, sessionId: session.sessionId, opener: !!window.opener };
  }, key);
  const create = title => api("POST", "/api/workspaces", {
    operationId: `${prefix}-create-${++captureSequence}`, actor: "browser-restoration-fixture", title: `${prefix} ${title}`, body: "Disposable browser fixture"
  });
  const command = async (key, action, payload) => api("POST", `/api/workspaces/${encodeURIComponent(key)}/commands`, {
    operationId: `${prefix}-${action}-${Date.now()}-${Math.random()}`, expectedRevision: (await context(key)).revision,
    actor: "other-api-client", action, payload
  });
  const addComponent = async (key, id, placement, text) => command(key, "create_component", {
    component: { id, type: "note", title: `Title ${id}`, content: text },
    placement: { id: placement, x: 30, y: 40, width: 320, height: 220 }
  });
  const externalUpdate = async (key, text, x) => {
    await command(key, "update_component", { componentId, title: `Saved ${text}`, content: text });
    await command(key, "move_components", { placements: [{ placementId, x, y: x + 10, width: 410, height: 270, parentId: null, order: 2, zIndex: 3 }] });
    return context(key);
  };
  const observe = async key => {
    await ready(page, key);
    const server = await context(key);
    const values = await page.evaluate(({ componentId, placementId }) => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      const draft = JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + shell.dataset.workspaceId));
      const card = document.querySelector(`[data-placement-id="${placementId}"]`);
      return {
        headerRevision: Number(shell.dataset.revision), editorContent: document.querySelector('[data-testid="component-content"]')?.value,
        editorTitle: document.querySelector('[data-testid="component-title"]')?.value,
        layoutX: Number(document.querySelector('[data-testid="layout-x"]')?.value),
        layoutY: Number(document.querySelector('[data-testid="layout-y"]')?.value),
        canvasContent: card?.querySelector(".workspace-card-preview")?.textContent, canvasX: Number(card?.dataset.localX),
        contentDraft: draft?.contentDrafts?.[componentId], layoutDraft: draft?.layoutDrafts?.[placementId],
        conflictCount: document.querySelectorAll('[data-testid="component-draft-conflict"], [data-testid="layout-reapply"]').length,
        storageError: document.querySelector('[data-testid="workspace-storage-error"]')?.textContent ?? null
      };
    }, { componentId, placementId });
    return { ...values, server };
  };
  const cleanMatches = (observed, text, x) => {
    check(observed.headerRevision === observed.server.revision && observed.editorContent === text && observed.editorTitle === `Saved ${text}`,
      "Restored clean editor must match the current saved content and revision.");
    check(observed.contentDraft?.dirty === false && observed.contentDraft.baseRevision === observed.server.revision
      && observed.layoutDraft?.dirty === false && observed.layoutDraft.baseRevision === observed.server.revision,
      "Clean content and layout buffers must be reconciled to the loaded server revision.");
    check(observed.layoutX === x && observed.layoutY === x + 10 && observed.canvasX === x
      && observed.canvasContent.includes(text) && observed.conflictCount === 0 && observed.storageError === null,
      "Restored clean layout/editor/canvas must agree without conflicts or storage errors.");
  };
  const paste = async (locator, value) => {
    await locator.waitFor({ state: "visible", timeout: 15000 });
    await locator.evaluate((element, value) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error("A textarea is required.");
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(element, value);
      element.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertFromPaste", data: value }));
    }, value);
    check(await locator.inputValue() === value, "The real textarea paste must retain the entire authored buffer.");
  };
  const completeText = label => `\n  ## ${label} Ω 漢字 🧭\n\n\`\`\`text\n    keep indentation and trailing spaces  \n\`\`\`\n`.repeat(650) + "\nFINAL COMPLETE REQUIREMENT Ω  \n";
  const saveCheckpoint = async (doc, key, expectedIdentity, objective, nextActions = "") => {
    await doc.getByTestId("workspace-tab-resume").click();
    if (objective.length > 4096) await paste(doc.getByTestId("checkpoint-objective"), objective);
    else await doc.getByTestId("checkpoint-objective").fill(objective);
    if (nextActions.length > 4096) await paste(doc.getByTestId("checkpoint-next"), nextActions);
    else await doc.getByTestId("checkpoint-next").fill(nextActions);
    await doc.getByTestId("checkpoint-save").click();
    let current;
    for (let attempt = 0; attempt < 100; attempt++) {
      current = await context(key);
      const saved = current.document.checkpoints.find(item => item.clientId === expectedIdentity.clientId && item.sessionId === expectedIdentity.sessionId);
      if (saved?.objective === objective && saved.nextActions.join("\n") === nextActions) {
        await revision(doc, current.revision);
        return current;
      }
      await doc.waitForTimeout(100);
    }
    throw new Error("Actual checkpoint UI save did not retain the complete objective and next actions under this tab's identity.");
  };
  let a, b, removed;
  try {
    a = await create("A"); b = await create("B");
    await addComponent(a.workspaceId, componentId, placementId, "old clean content");
    await page.goto(origin + route(a.workspaceId));
    await ready(page, a.workspaceId);
    await page.getByTestId(`canvas-select-${placementId}`).click();
    await page.waitForFunction(() => document.querySelector('[data-testid="component-content"]')?.value === "old clean content");
    const cached = await observe(a.workspaceId);
    check(cached.contentDraft?.dirty === false && cached.layoutDraft?.dirty === false, "Fixture must cache real clean content and layout buffers.");
    const firstIdentity = await identity(page, a.workspaceId);

    await switchTo(b.workspaceId);
    await externalUpdate(a.workspaceId, "new clean content after navigation", 150);
    await switchTo(a.workspaceId);
    cleanMatches(await observe(a.workspaceId), "new clean content after navigation", 150);
    check((await identity(page, a.workspaceId)).sessionId === firstIdentity.sessionId, "Enhanced navigation must retain the tab identity.");
    checks.push("A/B/A refreshes clean cached content and layout from the current saved context");

    await externalUpdate(a.workspaceId, "new clean content after full reload", 250);
    await page.reload();
    cleanMatches(await observe(a.workspaceId), "new clean content after full reload", 250);
    check((await identity(page, a.workspaceId)).sessionId === firstIdentity.sessionId, "Ordinary reload must retain the session identity.");
    checks.push("full reload refreshes clean stored content and layout and retains session identity");

    const dirtyText = completeText("Unsaved content survives external replacement");
    check(dirtyText.length > 40_000, "The draft fixture must exceed 40,000 characters.");
    await paste(page.getByTestId("component-content"), dirtyText);
    await page.getByTestId("layout-x").fill("808");
    await page.getByTestId("layout-x").press("Tab");
    await page.waitForFunction(({ key, componentId, placementId, text }) => {
      const draft = JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key));
      return draft?.contentDrafts?.[componentId]?.content === text && draft.contentDrafts[componentId].dirty
        && draft?.layoutDrafts?.[placementId]?.x === 808 && draft.layoutDrafts[placementId].dirty;
    }, { key: a.workspaceId, componentId, placementId, text: dirtyText });
    await switchTo(b.workspaceId);
    const external = await externalUpdate(a.workspaceId, "collaborator saved replacement", 350);
    await switchTo(a.workspaceId);
    const dirtyMatches = observed => {
      check(observed.headerRevision === external.revision && observed.server.revision === external.revision,
        "Dirty restoration must still load the current saved context.");
      check(observed.editorContent === dirtyText && observed.contentDraft?.content === dirtyText && observed.contentDraft.dirty
        && observed.contentDraft.baseRevision < external.revision && observed.layoutX === 808 && observed.layoutDraft?.dirty
        && observed.layoutDraft.baseRevision < external.revision && observed.conflictCount === 2,
        "Restoration must retain full dirty content/layout buffers and expose both conflicts.");
      check(observed.canvasX === 350 && observed.canvasContent.includes("collaborator saved replacement"),
        "The canvas must retain current saved content separately from unsaved editor buffers.");
    };
    dirtyMatches(await observe(a.workspaceId));
    await page.reload();
    dirtyMatches(await observe(a.workspaceId));
    check((await identity(page, a.workspaceId)).sessionId === firstIdentity.sessionId, "Dirty reload must retain the same tab session.");
    checks.push("A/B/A and reload preserve complete dirty content/layout buffers with explicit conflicts");

    const popupPromise = page.waitForEvent("popup");
    await page.evaluate(url => window.open(url, "_blank"), origin + route(a.workspaceId));
    const popup = await popupPromise; opened.push(popup);
    await ready(popup, a.workspaceId);
    const popupIdentity = await identity(popup, a.workspaceId);
    check(await popup.opener() === page && popupIdentity.opener, "Fixture must create a real opener-linked second document.");
    check(firstIdentity.clientId === popupIdentity.clientId && firstIdentity.sessionId !== popupIdentity.sessionId,
      "An opener-created copied session must share the browser client and reserve its own live tab session.");
    check(await popup.getByTestId("component-content").inputValue() === dirtyText, "Splitting copied identity must preserve the complete inherited draft.");

    let current = await saveCheckpoint(page, a.workspaceId, firstIdentity, "original tab objective Ω");
    check(current.document.checkpoints.length === 1, "First actual UI save must create one checkpoint.");
    await popup.getByTestId("workspace-refresh").click(); await revision(popup, current.revision);
    current = await saveCheckpoint(popup, a.workspaceId, popupIdentity, "duplicated tab objective 漢字");
    check(current.document.checkpoints.length === 2, "The copied tab's actual UI save must create an independent checkpoint.");
    await page.getByTestId("workspace-refresh").click(); await revision(page, current.revision);
    current = await saveCheckpoint(page, a.workspaceId, firstIdentity, "original tab objective again 🧭");
    check(current.document.checkpoints.length === 2
      && current.document.checkpoints.some(item => item.sessionId === popupIdentity.sessionId && item.objective === "duplicated tab objective 漢字"),
      "Alternating actual UI saves must retain both tabs' objectives.");
    await popup.reload(); await ready(popup, a.workspaceId);
    check((await identity(popup, a.workspaceId)).sessionId === popupIdentity.sessionId, "The copied tab must also retain its new identity on reload.");
    checks.push("window.open splits copied session identity and alternating actual UI checkpoint saves preserve both tabs");

    const copiedStorage = await page.evaluate(() => Object.entries(sessionStorage));
    const withoutOpener = await page.context().newPage(); opened.push(withoutOpener);
    await withoutOpener.addInitScript(entries => {
      if (!sessionStorage.getItem("kanbanflow.workspace.session.v1"))
        for (const [key, value] of entries) sessionStorage.setItem(key, value);
    }, copiedStorage);
    await withoutOpener.goto(origin + route(a.workspaceId)); await ready(withoutOpener, a.workspaceId);
    const independentIdentity = await identity(withoutOpener, a.workspaceId);
    check(!independentIdentity.opener && independentIdentity.clientId === firstIdentity.clientId
      && independentIdentity.sessionId !== firstIdentity.sessionId && independentIdentity.sessionId !== popupIdentity.sessionId,
      "Copied storage without any opener must also reserve a distinct live session.");
    checks.push("a copied-storage document without an opener reserves a distinct live session");

    removed = await create("removed focus");
    const missingId = "removed-focus-component";
    await addComponent(removed.workspaceId, missingId, "removed-focus-placement", "Focus before removal");
    await page.goto(origin + route(removed.workspaceId)); await ready(page, removed.workspaceId);
    const resumeIdentity = await identity(page, removed.workspaceId);
    await page.getByTestId("workspace-tab-resume").click();
    await page.getByTestId(`checkpoint-focus-${missingId}`).check();
    const originalObjective = completeText("Saved checkpoint before focused component removal");
    const originalNext = completeText("Saved next actions before removal");
    await saveCheckpoint(page, removed.workspaceId, resumeIdentity, originalObjective, originalNext);
    await command(removed.workspaceId, "remove_component", { componentId: missingId });
    current = await context(removed.workspaceId);
    await page.getByTestId("workspace-refresh").click(); await revision(page, current.revision);
    await page.locator('[data-testid="workspace-checkpoints"] details summary').click();
    await page.getByTestId(`checkpoint-resume-${resumeIdentity.clientId}-${resumeIdentity.sessionId}`).click();
    const missing = page.getByTestId(`checkpoint-missing-focus-${missingId}`);
    await missing.waitFor({ state: "visible" });
    check(await missing.isChecked(), "The removed focus must remain explicitly selected until the human clears it.");
    check((await missing.locator("..").textContent()).includes("Removed component"), "Missing focus must have a clear removed-component label.");
    check(await page.getByTestId("checkpoint-objective").inputValue() === originalObjective
      && await page.getByTestId("checkpoint-next").inputValue() === originalNext,
      "Using the saved resume context after component removal must preserve every objective and next-action character.");
    await missing.uncheck();
    const revisedObjective = originalObjective + "\n  Human reviewed and removed the stale focus Ω  \n";
    const revisedNext = originalNext + "\n  Continue without the removed component 漢字  \n";
    current = await saveCheckpoint(page, removed.workspaceId, resumeIdentity, revisedObjective, revisedNext);
    const saved = current.document.checkpoints.find(item => item.clientId === resumeIdentity.clientId && item.sessionId === resumeIdentity.sessionId);
    check(saved.focusedComponentIds.length === 0 && saved.objective === revisedObjective && saved.nextActions.join("\n") === revisedNext,
      "Explicitly clearing removed focus must allow a checkpoint save with complete revised text.");
    check(await page.getByTestId("workspace-error").count() === 0, "The resolved missing focus must not leave a component_not_found failure.");
    checks.push("removed checkpoint focus remains visible and explicitly removable, then full objective/next actions save successfully");

    for (const [label, raw] of [
      ["malformed", "{invalid browser draft\n" + completeText("Original malformed pending operation")],
      ["incompatible", JSON.stringify({ contentDrafts: null, futureAuthoredSource: completeText("Future schema text"), pending: { command: { payload: completeText("Exact uncertain operation") } } })],
    ]) {
      await page.goto(origin + route(b.workspaceId)); await ready(page, b.workspaceId);
      const savedBeforeRecovery = JSON.stringify(await context(b.workspaceId));
      await page.evaluate(({ key, raw }) => sessionStorage.setItem("kanbanflow.workspace.draft.v1:" + key, raw), { key: b.workspaceId, raw });
      await page.reload();
      await page.getByTestId("workspace-draft-recovery").waitFor({ state: "visible" });
      check(await page.getByTestId("workspace-stored-draft-raw").textContent() === raw, "Recovery must expose the complete original raw browser draft.");
      check(await page.evaluate(key => sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key), b.workspaceId) === raw,
        "An unreadable draft must remain unchanged before an explicit recovery choice.");
      const recoveryMemoryText = completeText("Human edits while stored draft loading is blocked");
      if (label === "malformed") {
        await page.getByTestId("component-title").fill("Current typing must survive recovery");
        await paste(page.getByTestId("component-content"), recoveryMemoryText);
      }
      await page.getByTestId("workspace-preserve-draft").click();
      await ready(page, b.workspaceId);
      const backupKey = await page.getByTestId("workspace-draft-backup-key").textContent();
      check(await page.evaluate(key => sessionStorage.getItem(key), backupKey) === raw
        && await page.getByTestId("workspace-preserved-draft-raw").textContent() === raw,
        "Explicit recovery must retain every original character in both the verified backup and review UI.");
      check(JSON.stringify(await context(b.workspaceId)) === savedBeforeRecovery,
        "Browser recovery must not mutate saved workspace state or replay the uncertain original operation.");
      if (label === "malformed") {
        check(await page.getByTestId("component-title").inputValue() === "Current typing must survive recovery"
          && await page.getByTestId("component-content").inputValue() === recoveryMemoryText,
          "Recovery must preserve every current in-memory edit made while stored draft loading was blocked.");
        await page.waitForFunction(({ key, text }) => JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key))?.contentDrafts?.$new?.content === text,
          { key: b.workspaceId, text: recoveryMemoryText });
      }
      checks.push(`${label} browser draft remains intact until explicit full-backup recovery, then saves become available`);
    }

    const dangling = await create("removed clean selection");
    await addComponent(dangling.workspaceId, "clean-selection", "clean-selection-placement", "Original clean selection");
    await page.goto(origin + route(dangling.workspaceId)); await ready(page, dangling.workspaceId);
    await page.getByTestId("workspace-view-outline").click();
    await page.getByTestId("outline-component-clean-selection").click();
    await page.waitForFunction(key => JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key))?.selectedComponentId === "clean-selection", dangling.workspaceId);
    await command(dangling.workspaceId, "remove_component", { componentId: "clean-selection" });
    await page.reload(); await ready(page, dangling.workspaceId);
    const replacementText = completeText("Typing after collaborator removed the selected clean card");
    await page.getByTestId("component-title").fill("Replacement card after removal");
    await paste(page.getByTestId("component-content"), replacementText);
    await page.waitForFunction(({ key, text }) => {
      const draft = JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key));
      return draft?.selectedComponentId == null && draft?.contentDrafts?.$new?.title === "Replacement card after removal"
        && draft.contentDrafts.$new.content === text && draft.contentDrafts.$new.dirty;
    }, { key: dangling.workspaceId, text: replacementText });
    check(await page.getByTestId("component-title").inputValue() === "Replacement card after removal"
      && await page.getByTestId("component-content").inputValue() === replacementText,
      "A clean removed selection must consistently use the new-card key and retain all subsequent typing.");
    checks.push("restoring a removed clean selection keeps subsequent new-card typing and full stored content");

    const planning = await create("plan copy confirmation");
    const savedSpecification = completeText("Saved plan specification");
    await command(planning.workspaceId, "propose_plan", { title: "Saved plan to copy", items: [
      { id: "saved-item", title: "Saved item", description: savedSpecification, acceptanceCriteria: ["Keep full original"], dependsOn: [] }
    ] });
    const planningContext = await context(planning.workspaceId);
    const savedPlan = planningContext.document.planRevisions[0];
    await page.goto(origin + route(planning.workspaceId)); await ready(page, planning.workspaceId);
    await page.getByTestId("workspace-tab-plan").click();
    await page.getByTestId("plan-title").fill("My unsaved plan must survive");
    await page.getByTestId("plan-add-item").click();
    const unsavedField = page.locator('fieldset[data-testid^="plan-item-"]').first();
    await unsavedField.locator('input').first().fill("Unsaved item");
    const unsavedSpecification = completeText("Unpublished work before copying a plan");
    await paste(unsavedField.locator('textarea').first(), unsavedSpecification);
    await page.waitForFunction(({ key, text }) => JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key))?.plan?.items?.[0]?.description === text,
      { key: planning.workspaceId, text: unsavedSpecification });
    const beforeCopy = await page.evaluate(key => JSON.stringify(JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key)).plan), planning.workspaceId);
    await page.getByTestId(`plan-version-${savedPlan.id}`).locator('summary').click();
    await page.getByTestId(`plan-copy-${savedPlan.id}`).click();
    await page.getByTestId("plan-copy-confirm").waitFor({ state: "visible" });
    check(await unsavedField.locator('textarea').first().inputValue() === unsavedSpecification, "Requesting a copy must preserve the current full draft.");
    await page.getByTestId("plan-copy-cancel").click();
    check(await page.evaluate(key => JSON.stringify(JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key)).plan), planning.workspaceId) === beforeCopy,
      "Cancelling replacement must leave the entire stored unsaved plan unchanged.");
    await page.getByTestId(`plan-copy-${savedPlan.id}`).click();
    await page.getByTestId("plan-copy-confirm").click();
    await page.waitForFunction(({ key, text }) => {
      const plan = JSON.parse(sessionStorage.getItem("kanbanflow.workspace.draft.v1:" + key))?.plan;
      return plan?.title === "Saved plan to copy" && plan.items[0]?.description === text;
    }, { key: planning.workspaceId, text: savedSpecification });
    check((await context(planning.workspaceId)).revision === planningContext.revision,
      "Copy confirmation changes the browser draft only, without saving a new server plan.");
    checks.push("copying a saved plan preserves unsaved text until confirmation and cancel retains the exact stored draft");

    check(errors.length === 0, `Browser errors: ${errors.join("; ")}`);
    return { origin, fixtureWorkspaces: [a.workspaceId, b.workspaceId, removed.workspaceId], checks,
      identities: { first: firstIdentity, copied: popupIdentity, withoutOpener: independentIdentity }, pageErrors: errors };
  } finally {
    for (const doc of opened.reverse()) await doc.close();
    page.off("pageerror", onError);
  }
}
