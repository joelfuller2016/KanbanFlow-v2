// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-smoke.browser.mjs
// Use an explicitly disposable local app. Labeled fixtures remain for transport comparisons.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(["127.0.0.1", "localhost", "[::1]"].includes(hostname),
    "Run the workspace UI smoke against an explicitly disposable loopback app.");
  const prefix = `workspace-ui-${Date.now()}`;
  const actor = "workspace-ui-smoke";
  const originalViewport = page.viewportSize();
  const checks = [];
  const errors = [];
  const onError = error => errors.push(error.message);
  page.on("pageerror", onError);
  let stage = "fixture creation";
  let activeWorkspaceId = null;
  const markStage = () => page.evaluate(value => { document.body.dataset.workspaceTestStage = value; document.body.dataset.workspaceTestSuite = "main"; }, stage);
  const waitState = (predicate, value) => page.waitForFunction(predicate, value, { timeout: 15000 });
  const step = async (name, action) => {
    stage = name;
    await markStage();
    await action();
    checks.push(name);
  };
  const api = async (method, path, data, expected = 200) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.status() === expected, `${method} ${path}: expected ${expected}, received ${response.status()}: ${text}`);
    return text.length === 0 ? null : JSON.parse(text);
  };
  const context = key => api("GET", `/api/workspaces/${encodeURIComponent(key)}/context`);
  // Blazor saves over its circuit; poll the real read boundary rather than a toast or HTTP write event.
  const saved = async (key, predicate, description) => {
    const deadline = Date.now() + 15000;
    do {
      const value = await context(key);
      if (predicate(value)) return value;
      await page.waitForTimeout(100);
    } while (Date.now() < deadline);
    throw new Error(`Persisted state did not satisfy: ${description}`);
  };
  const id = value => page.getByTestId(value);
  const longPasteInputs = [];
  // CLI fill expands large values into thousands of events. Long fixtures model one user paste
  // through the native textarea setter and the real bubbling Blazor input handler.
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
    check(observed.value === value, "One paste input must preserve the complete authored textarea value.");
    check(observed.inputEvents === 1 && observed.inputType === "insertFromPaste" && observed.dataMatches,
      "The long-text entry action must emit one complete paste input event through the real DOM handler.");
    longPasteInputs.push({ testId: observed.testId, characters: value.length, inputEvents: observed.inputEvents, inputType: observed.inputType });
  };
  const route = key => `/ideas/workspace/${key.replace(":", "/")}`;
  const ready = async (testId, expectedWorkspaceId = activeWorkspaceId) => {
    check(testId !== "workspace-shell" || expectedWorkspaceId, "Workspace readiness requires an explicit target identity.");
    await waitState(({ testId, expectedWorkspaceId }) => {
      const surface = document.querySelector(`[data-testid="${testId}"]`);
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return surface?.dataset.ready === "true" && (!expectedWorkspaceId
        || shell?.dataset.ready === "true" && shell?.dataset.workspaceId === expectedWorkspaceId);
    }, { testId, expectedWorkspaceId });
    if (expectedWorkspaceId) activeWorkspaceId = expectedWorkspaceId;
    await markStage();
  };
  const open = async key => {
    await page.goto(origin + route(key), { timeout: 15000 });
    await ready("workspace-shell", key);
  };
  const select = async component => {
    const restoreCanvas = await id("workspace-canvas").count() > 0;
    if (restoreCanvas) await id("workspace-view-outline").click();
    await id(`outline-component-${component}`).click();
    await waitState(component => {
      const selected = document.querySelector(`[data-testid="outline-component-${component}"]`);
      return selected?.classList.contains("btn-primary");
    }, component);
    if (restoreCanvas) {
      await id("workspace-view-canvas").click();
      await ready("workspace-canvas");
    }
  };
  const createComponent = async (key, type, title, content, url) => {
    await ready("workspace-shell", key);
    await id("component-add").click();
    await waitState(() => document.querySelector('[data-testid="component-save"]')?.textContent.trim() === "Add to canvas"
      && document.querySelector('[data-testid="component-title"]')?.value === "");
    await id("component-type").selectOption(type);
    await id("component-title").fill(title);
    if (url !== undefined) await id("component-url").fill(url);
    await fillOrPasteText(id("component-content"), content);
    check(await id("component-content").inputValue() === content, "The component editor must retain complete authored text before saving.");
    await id("component-save").click();
    const value = await saved(key, current => current.document.components.some(item => item.title === title && item.content === content), `saved ${type} component`);
    const component = value.document.components.find(item => item.title === title);
    const placement = value.document.placements.find(item => item.componentId === component.id);
    check(component.type === type && placement, "A typed component must have an independent persisted appearance.");
    await waitState(placement => document.querySelector('[data-testid="layout-placement"]')?.value === placement, placement.id);
    return { component, placement };
  };
  const applyLayout = async fields => {
    for (const [name, value] of Object.entries(fields)) {
      if (name === "parent") await id("layout-parent").selectOption(value);
      else {
        await id(`layout-${name}`).fill(String(value));
        await id(`layout-${name}`).press("Tab");
      }
    }
    await id("layout-apply").click();
  };
  const canvasValue = async (placement, attribute, expected) => {
    await waitState(({ placement, attribute, expected }) => {
      const card = document.querySelector(`[data-testid="canvas-placement-${placement}"]`);
      const canvas = document.querySelector('[data-testid="workspace-canvas"]');
      return card?.getAttribute(attribute) === String(expected) && canvas?.dataset.busy !== "true";
    }, { placement, attribute, expected });
  };
  const screenshots = [];
  const screenshot = async name => {
    const path = `_sandbox/579/${prefix}-${name}.png`;
    await page.screenshot({ path, fullPage: true });
    screenshots.push(path);
  };
  const switchTo = async key => {
    await id("workspace-switcher").selectOption(key);
    await page.waitForURL(origin + route(key), { timeout: 15000 });
    await ready("workspace-shell", key);
  };
  const saveCheckpoint = async (key, objective, focusedComponent) => {
    await id("workspace-tab-resume").click();
    const identity = await page.evaluate(() => ({
      clientId: localStorage.getItem("kanbanflow.workspace.client.v1"),
      sessionId: sessionStorage.getItem("kanbanflow.workspace.session.v1"),
    }));
    const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    check(guid.test(identity.clientId) && guid.test(identity.sessionId), "The actual browser must have GUID client and session identities before saving a checkpoint.");
    await id("checkpoint-objective").fill(objective);
    await id("checkpoint-completed").fill("Read complete source Ω\nArrange reusable components");
    await id("checkpoint-questions").fill("  Which approach should we explore next?  ");
    await id("checkpoint-next").fill("Review evidence Ω\nExpand the plan  ");
    if (focusedComponent) {
      const current = await context(key);
      const title = current.document.components.find(item => item.id === focusedComponent).title;
      await id("workspace-checkpoints").getByRole("checkbox", { name: title, exact: true }).check();
    }
    await id("checkpoint-save").click();
    const current = await saved(key, value => value.document.checkpoints.some(item => item.objective === objective), "attributed resume checkpoint");
    const checkpoint = current.document.checkpoints.find(item => item.objective === objective);
    check(checkpoint.clientId && checkpoint.sessionId && checkpoint.actor, "Checkpoints must retain client, session and actor identities.");
    check(checkpoint.clientId === identity.clientId && checkpoint.sessionId === identity.sessionId
      && checkpoint.clientId !== "clientId" && checkpoint.sessionId !== "sessionId",
      "Saved checkpoint identities must equal the actual browser local/session storage GUIDs.");
    const renderedIdentities = await id("workspace-checkpoints").locator("code").allTextContents();
    check(renderedIdentities.includes(identity.clientId) && renderedIdentities.includes(identity.sessionId),
      "The visible resume panel must render the actual browser client and session identities.");
    check(JSON.stringify(checkpoint.completedChanges) === JSON.stringify(["Read complete source Ω", "Arrange reusable components"])
      && JSON.stringify(checkpoint.openQuestions) === JSON.stringify(["  Which approach should we explore next?  "])
      && JSON.stringify(checkpoint.nextActions) === JSON.stringify(["Review evidence Ω", "Expand the plan  "]),
    "Checkpoint lists must preserve complete authored lines and whitespace.");
    check(checkpoint.lastAcknowledgedActivityId > 0 && checkpoint.lastAcknowledgedActivityId <= current.lastActivityId,
      "Checkpoint activity acknowledgement must refer to saved workspace history.");
    if (focusedComponent) check(checkpoint.focusedComponentIds.includes(focusedComponent), "The saved checkpoint must retain the selected focus component.");
    const article = id(`saved-checkpoint-${identity.clientId}-${identity.sessionId}`);
    await article.waitFor({ state: "attached", timeout: 15000 });
    if (!await article.isVisible()) await id("workspace-checkpoints").locator("details > summary").click();
    await article.waitFor({ state: "visible", timeout: 15000 });
    const renderedCheckpoint = await article.innerText();
    check(renderedCheckpoint.includes(objective) && renderedCheckpoint.includes("Review evidence Ω")
      && renderedCheckpoint.includes("Expand the plan"), "The saved checkpoint must visibly render its actual objective and next actions.");
    return checkpoint;
  };
  const sourceBody = "\n  # Original idea Ω\n\n    keep this indentation  \n\n";
  const sourcePlan = "\n  ## Original plan\n\n- Keep the complete brief  \n";
  // LF is intentional: textarea normalizes CRLF by the HTML standard; no trimming or rewriting is allowed.
  const fullContent = "\n  ## Research Ω 漢字 🧭\n\n```text\n    significant indentation and trailing spaces  \n```\n".repeat(650)
    + "\nFINAL COMPLETE REQUIREMENT Ω  \n";
  check(fullContent.length > 40000, "The authored content fixture must exceed 40,000 characters.");
  let a, b, project, captured, componentId, placementId, frameId, framePlacementId, bComponentId, acceptedPlanId;
  let expectedContent = fullContent;
  try {
    a = await api("POST", "/api/workspaces", {
      operationId: `${prefix}-a`, actor, title: `${prefix} idea A`, body: sourceBody, plan: sourcePlan,
    });
    b = await api("POST", "/api/workspaces", {
      operationId: `${prefix}-b`, actor, title: `${prefix} idea B`, body: "\n  Independent B source Ω  \n", plan: "B plan",
    });
    project = await api("POST", "/api/projects", {
      name: `${prefix} standalone project`, description: "Standalone description", goals: "Standalone goals", readme: "# Standalone readme\n\n    complete project context",
    }, 201);
    const projectKey = `project:${project.id}`;
    await page.setViewportSize({ width: 1440, height: 1000 });

    await step("gallery discovers idea and standalone project cards", async () => {
      await page.goto(`${origin}/ideas`, { timeout: 15000 });
      await ready("workspace-gallery", null);
      await id("workspace-search").fill(prefix);
      await id("workspace-search-submit").click();
      await waitState(() => {
        const total = document.querySelector('[data-testid="workspace-total"]')?.textContent.replace(/\s+/g, " ").trim();
        return total === "3 workspaces · 3 shown"
          && document.querySelectorAll('[data-testid^="gallery-card-"]').length === 3;
      });
      for (const key of [a.workspaceId, b.workspaceId, projectKey])
        await id(`gallery-open-${key}`).waitFor({ state: "visible", timeout: 15000 });
      check((await id("workspace-total").innerText()).replace(/\s+/g, " ").trim() === "3 workspaces · 3 shown",
        "The filtered gallery must contain exactly three fixture workspaces.");
      for (const key of [a.workspaceId, b.workspaceId, projectKey])
        check(await id(`gallery-card-${key}`).count() === 1, "The complete filtered gallery must show each fixture key exactly once.");
      check(await id(`gallery-card-${projectKey}`).getByRole("link", { name: "Kanban ↗", exact: true }).getAttribute("href") === `/project/${project.id}`,
        "A standalone project card must link to its real Kanban board.");
    });

    await step("gallery UI captures complete original Markdown through one paste input", async () => {
      await id("workspace-create-toggle").click();
      const title = `${prefix} captured in UI`;
      await id("workspace-create-title").fill(title);
      await fillOrPasteText(id("workspace-create-body"), fullContent);
      await id("workspace-create-plan").fill(sourcePlan);
      check(await id("workspace-create-body").inputValue() === fullContent, "The capture textarea must retain every authored character.");
      await id("workspace-create-toggle").click();
      await id("workspace-create-title").waitFor({ state: "hidden", timeout: 15000 });
      await id("workspace-create-toggle").click();
      await id("workspace-create-title").waitFor({ state: "visible", timeout: 15000 });
      check(await id("workspace-create-title").inputValue() === title
        && await id("workspace-create-body").inputValue() === fullContent
        && await id("workspace-create-plan").inputValue() === sourcePlan,
        "Hiding and reopening the capture form must preserve its title, complete body and plan before creation.");
      await id("workspace-create-submit").click();
      await page.waitForURL(/\/ideas\/workspace\/idea\/\d+$/, { timeout: 15000 });
      const key = `idea:${(await page.evaluate(() => location.pathname)).split("/").at(-1)}`;
      await ready("workspace-shell", key);
      captured = await context(key);
      check(captured.source.body === fullContent && captured.originalSource.body === fullContent,
        "UI capture must preserve complete current and original source text.");
      check(captured.source.plan === sourcePlan && captured.originalSource.plan === sourcePlan,
        "UI capture must preserve exact initial plan whitespace.");
    });

    await step("UI creates typed cards with one full-content paste and independent placements", async () => {
      await open(a.workspaceId);
      const frame = await createComponent(a.workspaceId, "frame", `${prefix} working frame`, "A shared exploration area");
      frameId = frame.component.id;
      framePlacementId = frame.placement.id;
      await applyLayout({ x: 850, y: 650, width: 720, height: 480, order: 7, layer: 1 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === framePlacementId && item.x === 850 && item.y === 650 && item.width === 720 && item.height === 480 && item.order === 7 && item.zIndex === 1), "frame geometry/order/layer");
      const research = await createComponent(a.workspaceId, "research", `${prefix} complete research`, fullContent, "https://example.com/research");
      componentId = research.component.id;
      placementId = research.placement.id;
      check(research.component.url === "https://example.com/research", "The research source URL must be persisted.");
      const detail = await api("GET", `/api/workspaces/${a.workspaceId}/components/${componentId}`);
      check(detail.component.content === fullContent, "Full component detail must contain every Unicode/Markdown character saved through the UI.");
      const renderedCard = id(`canvas-placement-${placementId}`).locator(".workspace-card-preview .markdown-body");
      await renderedCard.getByRole("heading", { name: "Research Ω 漢字 🧭", exact: true }).first().waitFor({ state: "visible", timeout: 15000 });
      const renderedMarkdown = await renderedCard.innerText();
      check(renderedMarkdown.includes("significant indentation and trailing spaces")
        && renderedMarkdown.includes("FINAL COMPLETE REQUIREMENT Ω"), "The actual canvas Markdown must render the authored research and its final complete-content marker.");
      const current = await context(a.workspaceId);
      check(current.source.body === sourceBody && current.originalSource.body === sourceBody && current.source.plan === sourcePlan,
        "Adding canvas content must retain the complete original/current legacy idea documents.");
    });

    await step("source panels render the actual brief plan and original snapshot", async () => {
      await id("workspace-tab-source").click();
      const documents = id("workspace-source-documents").locator(":scope > details");
      for (const position of [0, 1]) {
        const document = documents.nth(position);
        await document.locator(":scope > summary").click();
        const bodies = document.locator(".markdown-body");
        await bodies.nth(0).getByRole("heading", { name: "Original idea Ω", exact: true }).waitFor({ state: "visible", timeout: 15000 });
        check((await bodies.nth(0).innerText()).includes("keep this indentation")
          && (await bodies.nth(1).innerText()).includes("Keep the complete brief"), "Current and original source panels must visibly render the actual authored brief and plan.");
      }
    });

    await step("layout fields persist grouping geometry reading order and layer", async () => {
      await select(componentId);
      await applyLayout({ x: 35, y: 75, width: 360, height: 240, parent: framePlacementId, order: 3, layer: 4 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.parentId === framePlacementId && item.x === 35 && item.y === 75 && item.width === 360 && item.height === 240 && item.order === 3 && item.zIndex === 4), "grouped child layout");
      await canvasValue(placementId, "data-parent-id", framePlacementId);
      check(await id(`canvas-placement-${placementId}`).evaluate(element => parseFloat(getComputedStyle(element).getPropertyValue("--card-x"))
        - Number(element.closest('[data-testid="workspace-canvas"]').dataset.originX)) === 885,
        "The canvas must render a child's world position from its parent plus local X.");
    });

    await step("keyboard frame movement preserves child offsets and keyboard resize persists", async () => {
      await ready("workspace-canvas");
      await id(`canvas-select-${framePlacementId}`).focus();
      await page.keyboard.press("ArrowRight");
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === framePlacementId && item.x === 860), "keyboard frame move");
      await canvasValue(framePlacementId, "data-local-x", 860);
      let current = await context(a.workspaceId);
      let child = current.document.placements.find(item => item.id === placementId);
      check(child.x === 35 && child.y === 75 && child.parentId === framePlacementId, "Moving a frame must preserve its child's local coordinates.");
      check(await id(`canvas-placement-${placementId}`).evaluate(element => parseFloat(getComputedStyle(element).getPropertyValue("--card-x"))
        - Number(element.closest('[data-testid="workspace-canvas"]').dataset.originX)) === 895,
        "Moving the frame must move its rendered child by the same amount.");
      await id(`canvas-select-${placementId}`).focus();
      await page.keyboard.press("ArrowDown");
      await saved(a.workspaceId, value => value.document.placements.some(item => item.id === placementId && item.y === 85), "keyboard child move");
      await canvasValue(placementId, "data-local-y", 85);
      await id(`canvas-select-${placementId}`).focus();
      await page.keyboard.press("Shift+ArrowRight");
      await saved(a.workspaceId, value => value.document.placements.some(item => item.id === placementId && item.width === 370), "keyboard child resize");
      await canvasValue(placementId, "data-width", 370);
      current = await context(a.workspaceId);
      child = current.document.placements.find(item => item.id === placementId);
      check(child.x === 35 && child.height === 240, "Keyboard resize must preserve the other dimensions and local position.");
    });

    await step("pointer drag and corner resize persist through the shared layout boundary", async () => {
      const handle = id(`canvas-select-${placementId}`);
      await handle.scrollIntoViewIfNeeded();
      let box = await handle.boundingBox();
      check(box, "The drag handle must have a visible hit area.");
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(box.x + box.width / 2 + 30, box.y + box.height / 2 + 20, { steps: 4 });
      await page.mouse.up();
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.x === 65 && item.y === 105), "pointer movement");
      await canvasValue(placementId, "data-local-x", 65);
      const corner = id(`canvas-resize-${placementId}`);
      await corner.scrollIntoViewIfNeeded();
      box = await corner.boundingBox();
      check(box, "The resize corner must have a visible hit area.");
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.mouse.move(box.x + box.width / 2 + 40, box.y + box.height / 2 + 30, { steps: 4 });
      await page.mouse.up();
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.width === 410 && item.height === 270), "pointer resize");
      await canvasValue(placementId, "data-width", 410);
      check((await api("GET", `/api/workspaces/${a.workspaceId}/components/${componentId}`)).component.content === fullContent,
        "Moving and resizing an appearance must preserve its complete underlying content.");
      await screenshot("desktop");
    });

    await step("adding and removing an appearance preserves shared component content", async () => {
      await select(componentId);
      await id("layout-add-placement").click();
      const doubled = await saved(a.workspaceId, current => current.document.placements.filter(item => item.componentId === componentId).length === 2, "second appearance");
      const second = doubled.document.placements.find(item => item.componentId === componentId && item.id !== placementId);
      check(doubled.document.components.filter(item => item.id === componentId).length === 1, "A second appearance must reuse one content record.");
      await id("layout-placement").selectOption(second.id);
      await id("layout-remove").click();
      const removed = await saved(a.workspaceId, current => !current.document.placements.some(item => item.id === second.id), "remove only second appearance");
      check(removed.document.components.find(item => item.id === componentId)?.content === fullContent
        && removed.document.placements.some(item => item.id === placementId), "Removing an appearance must preserve the original appearance and complete component content.");
    });

    await step("negative canvas coordinates remain reachable through real pointer input", async () => {
      await select(componentId);
      await applyLayout({ parent: "", x: -250, y: -160, width: 220, height: 140, layer: 1 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.parentId === null && item.x === -250 && item.y === -160), "negative root coordinates");
      await canvasValue(placementId, "data-local-x", -250);
      const handle = id(`canvas-select-${placementId}`);
      await handle.scrollIntoViewIfNeeded();
      check(await handle.evaluate(element => {
        const box = element.getBoundingClientRect();
        const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
        return box.width > 0 && box.height > 0 && element.contains(hit);
      }), "A card saved at negative X/Y must have a reachable visible pointer target inside the canvas.");
      await handle.click();
      check(await id("component-content").inputValue() === fullContent, "Selecting a negative-position card must retain its full editable content.");
    });

    await step("children remain pointer reachable above higher-layer parent frames", async () => {
      await select(frameId);
      await applyLayout({ x: -180, y: -140, width: 400, height: 300, layer: 100 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === framePlacementId && item.x === -180 && item.y === -140 && item.zIndex === 100), "negative high-layer parent frame");
      await select(componentId);
      await applyLayout({ parent: framePlacementId, x: 20, y: 50, width: 200, height: 120, layer: 1 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.parentId === framePlacementId && item.zIndex === 1 && item.x === 20 && item.y === 50), "child of higher-layer frame");
      await canvasValue(placementId, "data-parent-id", framePlacementId);
      const handle = id(`canvas-select-${placementId}`);
      await handle.scrollIntoViewIfNeeded();
      check(await handle.evaluate(element => {
        const box = element.getBoundingClientRect();
        return element.contains(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2));
      }), "A parent's higher requested layer must not occlude its child card's selectable title.");
      await handle.click();
      check(await id("component-content").inputValue() === fullContent, "The child must remain selectable and editable above its frame.");
      await screenshot("negative-group");
    });

    await step("keyboard shrinking preserves valid sizes below eighty pixels", async () => {
      await select(componentId);
      await applyLayout({ width: 20 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.width === 20 && item.height === 120), "valid twenty-pixel width");
      await canvasValue(placementId, "data-width", 20);
      await id(`canvas-select-${placementId}`).focus();
      await page.keyboard.press("Shift+ArrowLeft");
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.width === 10 && item.height === 120), "twenty-pixel width shrinks to ten");
      await canvasValue(placementId, "data-width", 10);
      check((await context(a.workspaceId)).document.components.find(item => item.id === componentId).content === fullContent,
        "Shrinking a small valid appearance must preserve all component content.");
      // Restore the larger scene for the remaining collaboration and narrow-layout checks.
      await select(frameId);
      await applyLayout({ x: 860, y: 650, width: 720, height: 480, layer: 1 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === framePlacementId && item.x === 860 && item.y === 650), "restore frame layout");
      await select(componentId);
      await applyLayout({ parent: framePlacementId, x: 65, y: 105, width: 410, height: 270, layer: 4 });
      await saved(a.workspaceId, current => current.document.placements.some(item => item.id === placementId && item.width === 410 && item.height === 270 && item.x === 65 && item.y === 105 && item.zIndex === 4), "restore research layout");
    });

    await step("component discussion retains attributed comments and reply anchors", async () => {
      await id("workspace-tab-discussion").click();
      const text = "\n  Collaboration note Ω\n\n    preserve this reasoning and its final spaces  \n";
      await id("comment-target").selectOption(componentId);
      await id("comment-text").fill(text);
      await id("comment-submit").click();
      let current = await saved(a.workspaceId, value => value.document.comments.some(item => item.text === text), "complete component comment");
      const comment = current.document.comments.find(item => item.text === text);
      check(comment.componentId === componentId && comment.actor && !comment.replyToId, "The comment must retain its component anchor and attribution.");
      const renderedComment = id(`workspace-comment-${comment.id}`).locator(".markdown-body");
      await renderedComment.getByText("Collaboration note Ω", { exact: true }).waitFor({ state: "visible", timeout: 15000 });
      check((await renderedComment.innerText()).includes("preserve this reasoning and its final spaces"),
        "The discussion must visibly render the actual comment's complete reasoning.");
      await id(`comment-reply-${comment.id}`).click();
      const replyText = "\n  Agreed; keep this follow-up Ω  \n";
      await id("comment-text").fill(replyText);
      await id("comment-submit").click();
      current = await saved(a.workspaceId, value => value.document.comments.some(item => item.text === replyText), "threaded reply");
      const reply = current.document.comments.find(item => item.text === replyText);
      check(reply.replyToId === comment.id && reply.componentId === componentId && reply.actor,
        "A reply must retain its parent's thread/component identity and complete text.");
      await id(`workspace-comment-${reply.id}`).getByText("Agreed; keep this follow-up Ω", { exact: true }).waitFor({ state: "visible", timeout: 15000 });
    });

    await step("semantic connectors link existing components without duplicating content", async () => {
      await id("workspace-tab-connections").click();
      await id("connector-from").selectOption(componentId);
      await id("connector-to").selectOption(frameId);
      await id("connector-label").fill("supports");
      await id("connector-submit").click();
      let current = await saved(a.workspaceId, value => value.document.connectors.some(item => item.fromComponentId === componentId && item.toComponentId === frameId && item.label === "supports"), "semantic connector");
      const connector = current.document.connectors.find(item => item.fromComponentId === componentId && item.toComponentId === frameId);
      await id(`connector-remove-${connector.id}`).click();
      current = await saved(a.workspaceId, value => !value.document.connectors.some(item => item.id === connector.id), "explicit connector removal");
      check(current.document.components.find(item => item.id === componentId)?.content === fullContent
        && current.document.components.some(item => item.id === frameId), "Removing a connector must preserve both content records.");
    });

    await step("UI expands and accepts a versioned plan with complete criteria and dependencies", async () => {
      await id("workspace-tab-plan").click();
      const title = `${prefix} expanded plan`;
      const firstTitle = "Explore the complete concept Ω";
      const description = "\n  ## Detailed plan item Ω\n\n    keep the full rationale  \n".repeat(30);
      const criteria = "  Keep complete authored source Ω  \nPreserve the final requirement  ";
      await id("plan-title").fill(title);
      await id("plan-add-item").click();
      let fields = page.locator('fieldset[data-testid^="plan-item-"]');
      await fields.nth(0).getByRole("textbox", { name: "Title", exact: true }).fill(firstTitle);
      await fields.nth(0).getByRole("textbox", { name: "Description / Markdown", exact: true }).fill(description);
      await fields.nth(0).getByRole("textbox", { name: "Acceptance criteria (one per line)", exact: true }).fill(criteria);
      await id("plan-add-item").click();
      await fields.nth(1).getByRole("textbox", { name: "Title", exact: true }).fill("Develop the next increment");
      await fields.nth(1).getByRole("textbox", { name: "Description / Markdown", exact: true }).fill("\n  Build from the research  \n");
      await fields.nth(1).getByRole("checkbox", { name: firstTitle, exact: true }).check();
      await id("plan-propose").click();
      let current = await saved(a.workspaceId, value => value.document.planRevisions.some(item => item.title === title), "full proposed plan version");
      const plan = current.document.planRevisions.find(item => item.title === title);
      check(plan.status === "Draft" && plan.items.length === 2 && plan.items[0].description === description
        && JSON.stringify(plan.items[0].acceptanceCriteria) === JSON.stringify(criteria.split("\n"))
        && plan.items[1].dependsOn.includes(plan.items[0].id), "The proposed plan must preserve stable item IDs, full Markdown, criteria and dependency direction.");
      await id(`plan-version-${plan.id}`).locator("summary").click();
      await id(`plan-accept-${plan.id}`).click();
      current = await saved(a.workspaceId, value => value.document.acceptedPlanRevisionId === plan.id, "accepted standalone plan version");
      acceptedPlanId = plan.id;
      check(current.acceptedPlan?.id === plan.id && current.acceptedPlan.items[0].description === description,
        "Context must expose the complete accepted plan after explicit UI acceptance.");
      const renderedAccepted = id("workspace-plan").locator(":scope > div.mt-3")
        .filter({ has: page.getByRole("heading", { name: title, exact: true }) });
      await renderedAccepted.getByRole("heading", { name: "Detailed plan item Ω", exact: true }).first().waitFor({ state: "visible", timeout: 15000 });
      const acceptedText = await renderedAccepted.innerText();
      check(acceptedText.includes("keep the full rationale") && acceptedText.includes("Keep complete authored source Ω")
        && acceptedText.includes(`Depends on: ${firstTitle}`), "The accepted plan UI must visibly render actual item Markdown, acceptance criteria and the named dependency.");
      const tasks = await api("GET", `/api/projects/${project.id}/tasks`);
      check(tasks.length === 0, "Developing and accepting an idea plan must not publish tasks into an unrelated standalone project.");
    });

    await step("external changes preserve the pasted full draft until explicit compare and reapply", async () => {
      await select(componentId);
      const draft = fullContent + "\n  LOCAL UNSAVED APPROACH Ω  \n";
      const external = "External collaborator saved this approach Ω\n\n" + fullContent;
      await fillOrPasteText(id("component-content"), draft);
      await waitState(() => document.querySelector('[data-testid="component-save"]')?.disabled === false);
      const before = await context(a.workspaceId);
      await api("POST", `/api/workspaces/${a.workspaceId}/commands`, {
        operationId: `${prefix}-external-edit`, expectedRevision: before.revision, action: "update_component",
        payload: { componentId, content: external }, actor: "external-collaborator",
      });
      check(await id("component-content").inputValue() === draft, "An external save must not silently replace a dirty UI draft.");
      // Refresh is explicit and remains available even if background polling has already noticed the newer revision.
      await id("workspace-refresh").click();
      await id("component-draft-conflict").waitFor({ state: "visible", timeout: 15000 });
      check(await id("component-content").inputValue() === draft, "Loading a newer revision for comparison must retain the entire local draft.");
      check((await context(a.workspaceId)).document.components.find(item => item.id === componentId).content === external,
        "Comparing revisions must leave the collaborator's saved content intact.");
      check(await id("component-save").isDisabled(), "A conflicting draft must require an explicit revision decision before saving.");
      await id("component-draft-conflict").locator("summary").click();
      check(await id("component-current-content").textContent() === external, "The conflict comparison must reveal complete current saved content.");
      await screenshot("conflict");
      await id("component-reapply").click();
      check(await id("component-content").inputValue() === draft, "Explicit reapply must retain the draft exactly.");
      check((await context(a.workspaceId)).document.components.find(item => item.id === componentId).content === external,
        "Reapplying a draft to a loaded revision must wait for the user's save before replacing persisted content.");
      await id("component-save").click();
      await saved(a.workspaceId, value => value.document.components.some(item => item.id === componentId && item.content === draft), "explicitly reapplied full draft");
      expectedContent = draft;
    });

    const objectiveA = `Continue idea A Ω ${prefix}`;
    const objectiveB = `Continue idea B Ω ${prefix}`;
    await step("resume checkpoints and pasted drafts survive A to B to A and reload independently", async () => {
      await saveCheckpoint(a.workspaceId, objectiveA, componentId);
      await select(componentId);
      const draft = expectedContent + "\n  PRESERVED ACROSS WORKSPACE SWITCH AND RELOAD Ω  \n";
      await fillOrPasteText(id("component-content"), draft);
      await waitState(() => document.querySelector('[data-testid="component-save"]')?.disabled === false);
      await switchTo(b.workspaceId);
      const second = await createComponent(b.workspaceId, "question", `${prefix} independent B question`, "\n  What can idea B explore independently? Ω  \n");
      bComponentId = second.component.id;
      await saveCheckpoint(b.workspaceId, objectiveB, bComponentId);
      await switchTo(a.workspaceId);
      await select(componentId);
      check(await id("component-content").inputValue() === draft, "Returning from B must restore A's complete unsaved component draft.");
      check((await context(a.workspaceId)).document.components.find(item => item.id === componentId).content === expectedContent,
        "Switching away must not implicitly publish A's draft.");
      await page.reload({ timeout: 15000 });
      await ready("workspace-shell", a.workspaceId);
      await select(componentId);
      check(await id("component-content").inputValue() === draft, "Reload must restore A's complete saved browser draft.");
      await id("component-save").click();
      await saved(a.workspaceId, value => value.document.components.some(item => item.id === componentId && item.content === draft), "explicit save after draft restore");
      expectedContent = draft;
      const [currentA, currentB] = await Promise.all([context(a.workspaceId), context(b.workspaceId)]);
      check(currentA.document.checkpoints.some(item => item.objective === objectiveA)
        && !currentA.document.checkpoints.some(item => item.objective === objectiveB)
        && currentB.document.checkpoints.some(item => item.objective === objectiveB)
        && !currentB.document.checkpoints.some(item => item.objective === objectiveA), "Workspace switches must preserve independent resume checkpoints.");
      check(!currentB.document.components.some(item => item.id === componentId)
        && !currentA.document.components.some(item => item.id === bComponentId), "Workspace switching must not copy components between ideas.");
    });

    await step("outline and narrow-screen controls remain reachable by keyboard", async () => {
      await id("workspace-view-outline").click();
      await id("workspace-outline").waitFor({ state: "visible", timeout: 15000 });
      const current = await context(a.workspaceId);
      check(await page.locator('[data-testid^="outline-component-"]').count() === current.document.components.length,
        "The outline must expose every content component.");
      await id(`outline-component-${componentId}`).focus();
      await page.keyboard.press("Enter");
      check(await id("component-content").inputValue() === expectedContent, "Outline keyboard selection must open complete component content.");
      await page.setViewportSize({ width: 390, height: 844 });
      await id("workspace-tab-resume").click();
      for (const testId of ["workspace-view-outline", "workspace-switcher", "workspace-refresh", "component-title", "component-save", "layout-x", "checkpoint-save"]) {
        await id(testId).scrollIntoViewIfNeeded();
        const box = await id(testId).boundingBox();
        check(box && box.x >= -1 && box.x + box.width <= 391, `${testId} must fit the narrow viewport and remain reachable.`);
      }
      check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), "The narrow layout must not create page-wide horizontal overflow.");
      const title = `${prefix} research edited by keyboard`;
      await id("component-title").fill(title);
      await waitState(() => document.querySelector('[data-testid="component-save"]')?.disabled === false);
      await id("component-title").focus();
      let reached = false;
      for (let count = 0; count < 8; count++) {
        await page.keyboard.press("Tab");
        if (await page.evaluate(() => document.activeElement?.getAttribute("data-testid")) === "component-save") { reached = true; break; }
      }
      check(reached, "The save action must be reachable from the title by normal Tab navigation.");
      await page.keyboard.press("Enter");
      await saved(a.workspaceId, value => value.document.components.some(item => item.id === componentId && item.title === title && item.content === expectedContent), "narrow-screen keyboard save");
      await screenshot("narrow");
    });

    await step("standalone projects open their workspace and the legacy status board remains available", async () => {
      await page.setViewportSize({ width: 1440, height: 1000 });
      await switchTo(projectKey);
      const current = await context(projectKey);
      check(current.kind === "project" && current.source.id === project.id && current.kanbanUrl === `/project/${project.id}`
        && current.source.readme === project.readme, "A standalone project must retain its real source and Kanban identity.");
      check(await id("workspace-kanban-link").getAttribute("href") === `/project/${project.id}`, "The project workspace UI must provide its real Kanban link.");
      await id("workspace-tab-source").click();
      const projectSource = id("workspace-source-documents").locator(":scope > details").first();
      await projectSource.locator(":scope > summary").click();
      const renderedProjectDocuments = projectSource.locator(".markdown-body");
      await renderedProjectDocuments.getByRole("heading", { name: "Standalone readme", exact: true }).waitFor({ state: "visible", timeout: 15000 });
      const renderedProjectText = await projectSource.innerText();
      check(renderedProjectText.includes("Standalone description") && renderedProjectText.includes("Standalone goals")
        && renderedProjectText.includes("complete project context"), "The project source panel must visibly render real Description, Goals and Readme content.");
      await page.goto(`${origin}/ideas/board`, { timeout: 15000 });
      await markStage();
      await id("idea-search").waitFor({ state: "visible", timeout: 15000 });
      await id(`idea-card-${a.ideaId}`).waitFor({ state: "visible", timeout: 15000 });
      await id(`idea-card-${b.ideaId}`).waitFor({ state: "visible", timeout: 15000 });
      await id("new-idea-button").waitFor({ state: "visible", timeout: 15000 });
      check(await page.evaluate(() => location.pathname) === "/ideas/board", "The original Ideas board must remain accessible at its compatibility route.");
      check(errors.length === 0, `The browser must have no uncaught errors: ${errors.join(" | ")}`);
    });

    return { passed: true, prefix, checks, workspaceA: a.workspaceId, workspaceB: b.workspaceId,
      projectWorkspace: projectKey, capturedWorkspace: captured.workspaceId, componentId, placementId,
      frameId, framePlacementId, bComponentId, acceptedPlanId, contentCharacters: expectedContent.length, longPasteInputs, screenshots };
  } catch (error) {
    await page.screenshot({ path: `_sandbox/579/${prefix}-failure.png`, fullPage: true }).catch(() => {});
    throw new Error(`${stage}: ${error.message}`);
  } finally {
    page.off("pageerror", onError);
    if (originalViewport) await page.setViewportSize(originalViewport);
  }
}
