// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-workflow-integration.browser.mjs
// Writes labeled fixtures only in an explicitly disposable local app/database.
async basePage => {
  const site = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname, port: location.port }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(site.hostname) || site.port === "5123")
    throw new Error("Use an explicitly disposable loopback app for workflow/workspace integration.");
  const page = await basePage.context().newPage();
  const prefix = `workspace-workflow-${Date.now()}`;
  const checks = [], fixtures = [];
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data, expected) => {
    const response = await page.request.fetch(site.origin + path, { method, data, timeout: 20000 });
    const text = await response.text();
    check(expected ? response.status() === expected : response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  let rpcId = 0;
  const native = async (name, args) => {
    const response = await page.request.post(site.origin + "/mcp", { timeout: 20000,
      headers: { Accept: "application/json, text/event-stream", "MCP-Protocol-Version": "2025-06-18" },
      data: { jsonrpc: "2.0", id: ++rpcId, method: "tools/call", params: { name, arguments: args } } });
    check(response.ok(), `Native ${name}: HTTP ${response.status()}`);
    const body = await response.text(), data = body.split(/\r?\n/).find(line => line.startsWith("data: "));
    const payload = JSON.parse(data ? data.slice(6) : body);
    check(!payload.error && !payload.result?.isError, `Native ${name}: ${JSON.stringify(payload)}`);
    return payload.result.structuredContent ?? JSON.parse(payload.result.content.find(value => value.type === "text").text);
  };
  const context = key => api("GET", `/api/workspaces/${encodeURIComponent(key)}/context`);
  const workflow = ideaId => api("GET", `/api/workflows/idea/${ideaId}`);
  const ready = key => page.waitForFunction(key => {
    const shell = document.querySelector('[data-testid="workspace-shell"]');
    return shell?.dataset.ready === "true" && shell.dataset.workspaceId === key;
  }, key);
  const open = async (key, view) => { await page.goto(site.origin + `/ideas/workspace/${key.replace(":", "/")}?view=${view}`); await ready(key); };
  const waitWorkflow = async (ideaId, projectId) => {
    for (let attempt = 0; attempt < 100; attempt++) {
      const value = await workflow(ideaId);
      if (value.projectId === projectId) return value;
      await page.waitForTimeout(100);
    }
    throw new Error(`Workflow for idea ${ideaId} did not acquire project ${projectId}.`);
  };
  try {
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(site.origin + "/ideas");
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-gallery"]')?.dataset.ready === "true");
    checks.push("plain /ideas retains the interactive card gallery");
    await page.getByTestId("workspace-create-toggle").click();
    const title = `  ${prefix} gallery Ω  `, body = "  # Gallery original Ω\n\n    exact indentation  \n" + "文".repeat(12000) + "\ncomplete final line  ";
    await page.getByTestId("workspace-create-title").fill(title);
    await page.getByTestId("workspace-create-body").evaluate((element, value) => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(element, value);
      element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertFromPaste", data: value }));
    }, body);
    await page.getByTestId("workspace-create-submit").click();
    await page.waitForFunction(() => document.querySelector('[data-testid="workspace-shell"]')?.dataset.ready === "true");
    const key = await page.getByTestId("workspace-shell").getAttribute("data-workspace-id");
    fixtures.push({ channel: "gallery", workspaceId: key, ideaId: Number(key.split(":")[1]), title, body, actor: "browser" });
    for (const channel of ["rest", "native"]) {
      const input = { operationId: `${prefix}-${channel}`, actor: `${prefix}-${channel}-author`, title: `  ${prefix} ${channel} Ω  `,
        body: "  # Exact original Ω\r\n\r\n    indentation  \r\n" + "文".repeat(12000) + "\r\ncomplete final line  " };
      const created = channel === "rest" ? await api("POST", "/api/workspaces", input) : await native("create_workspace", input);
      const before = await context(created.workspaceId), beforeWorkflow = await workflow(created.ideaId);
      const replay = channel === "rest" ? await api("POST", "/api/workspaces", input) : await native("create_workspace", input);
      const after = await context(created.workspaceId), afterWorkflow = await workflow(created.ideaId);
      check(replay.replayed && replay.workspaceId === created.workspaceId && replay.ideaId === created.ideaId, `${channel}: exact creation receipt must replay.`);
      check(before.revision === after.revision && before.lastActivityId === after.lastActivityId
        && JSON.stringify(beforeWorkflow) === JSON.stringify(afterWorkflow), `${channel}: receipt replay must not change either family.`);
      fixtures.push({ channel, workspaceId: created.workspaceId, ideaId: created.ideaId, title: input.title, body: input.body, actor: input.actor });
    }
    checks.push("REST and native creation receipts replay without changing workspace or workflow context");
    for (const fixture of fixtures) {
      const rest = await workflow(fixture.ideaId), current = await native("get_request_context", { requestId: rest.id });
      const original = current.messages.filter(value => value.kind === "original");
      check(original.length === 1 && original[0].text === fixture.body && original[0].author === fixture.actor
        && original[0].source === "idea-workspace", `${fixture.channel}: exact original and truthful author/source required.`);
      check(current.ideaUrl === `/ideas/board?idea=${fixture.ideaId}` && current.id === rest.id, "Native workflow must expose the actual board link for the same request.");
      fixture.requestId = current.id; fixture.originalVersion = current.version;
      await page.goto(site.origin + "/ideas/board");
      const card = page.getByTestId(`idea-request-link-${fixture.ideaId}`);
      await card.waitFor({ state: "visible" });
      check(await card.getAttribute("href") === current.ideaUrl, "Actual board card and native context must link to the same destination.");
      await card.click();
      await page.waitForURL(site.origin + current.ideaUrl);
      const panel = page.getByTestId("request-workspace");
      await page.waitForFunction(id => {
        const panel = document.querySelector('[data-testid="request-workspace"]');
        return panel?.dataset.interactive === "true" && panel.textContent.includes(`Idea #${id} ·`);
      }, fixture.ideaId);
      check((await panel.locator("pre").first().textContent()).replace(/\r\n/g, "\n") === fixture.body.replace(/\r\n/g, "\n"), "Actual request panel must display the complete selected original.");
      await panel.getByRole("button", { name: "Brief", exact: true }).click();
      check(await panel.locator("#brief-outcome").isEnabled(), "Selected workflow brief editor must be usable.");
      await panel.locator("#brief-outcome").fill(`${prefix} unsaved brief ${fixture.channel}`);
      await page.goto(site.origin + current.ideaUrl);
      await page.waitForFunction(id => document.querySelector('[data-testid="request-workspace"]')?.textContent.includes(`Idea #${id} ·`), fixture.ideaId);
      checks.push(`${fixture.channel} capture preserves the exact original; actual card and native-context links open its usable request editor`);
    }
    const creator = fixtures.find(value => value.channel === "rest"), linker = fixtures.find(value => value.channel === "native");
    await open(creator.workspaceId, "delivery");
    await page.getByTestId("delivery-project-name").fill(`${prefix} combined project`);
    await page.getByTestId("delivery-create-project").click();
    let projectId;
    for (let attempt = 0; attempt < 100; attempt++) {
      projectId = (await context(creator.workspaceId)).projectId;
      if (projectId !== null) break;
      await page.waitForTimeout(100);
    }
    check(projectId > 0, "UI must create its project.");
    const createdWorkflow = await waitWorkflow(creator.ideaId, projectId);
    check(createdWorkflow.version === creator.originalVersion + 1, "Creating a project must advance the linked workflow version exactly once.");
    await open(linker.workspaceId, "delivery");
    await page.getByTestId("delivery-project-select").selectOption(String(projectId));
    await page.getByTestId("delivery-link-project").click();
    const linkedWorkflow = await waitWorkflow(linker.ideaId, projectId);
    check(linkedWorkflow.version === linker.originalVersion + 1, "Linking a project must advance the linked workflow version exactly once.");
    check((await context(linker.workspaceId)).projectId === projectId
      && (await api("GET", `/api/ideas/${linker.ideaId}`)).promotedProjectId === projectId, "Idea, workspace and workflow must agree after the actual UI association.");
    const sourceBeforeConflict = await api("GET", `/api/ideas/${linker.ideaId}`);
    await api("PATCH", `/api/ideas/${linker.ideaId}`, { body: "Must not replace exact original", actor: "stale integration client", expectedWorkflowVersion: linker.originalVersion }, 409);
    check(JSON.stringify(await api("GET", `/api/ideas/${linker.ideaId}`)) === JSON.stringify(sourceBeforeConflict), "A stale pre-association version must not edit the source idea.");
    checks.push("UI project creation and linking synchronize all three records and advance workflow versions; a stale source edit remains rejected");
    await page.goto(site.origin + "/ideas");
    await page.getByTestId("workspace-gallery").waitFor({ state: "visible" });
    return { passed: true, checks, projectId, fixtures: fixtures.map(({ body, ...value }) => ({ ...value, originalCharacters: body.length })) };
  } finally { await page.close(); }
}
