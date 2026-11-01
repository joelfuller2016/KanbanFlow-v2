// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-button-contrast.browser.mjs
// Requires an explicitly disposable loopback application/database. Fixtures are retained for audit inspection.
async basePage => {
  const { origin, hostname } = await basePage.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  if (!["127.0.0.1", "localhost", "[::1]"].includes(hostname)) throw new Error("A disposable loopback app is required.");
  const page = await basePage.context().newPage();
  const prefix = `workspace-buttons-${Date.now()}`;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.ok(), `${method} ${path}: ${response.status()} ${text}`);
    return text ? JSON.parse(text) : null;
  };
  const contrast = (a, b) => {
    const luminance = color => {
      const rgb = color.match(/[\d.]+/g).slice(0, 3).map(Number).map(value => {
        const normalized = value / 255;
        return normalized <= .04045 ? normalized / 12.92 : ((normalized + .055) / 1.055) ** 2.4;
      });
      return rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722;
    };
    const values = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (values[0] + .05) / (values[1] + .05);
  };
  const results = [];
  try {
    const project = await api("POST", "/api/projects", { name: prefix, description: "Disposable outline-button contrast proof" });
    const key = `project:${project.id}`;
    const path = `/api/workspaces/${encodeURIComponent(key)}`;
    const context = () => api("GET", `${path}/context`);
    let current = await context();
    await api("POST", `${path}/commands`, { operationId: `${prefix}-card`, actor: "workspace-contrast-smoke",
      expectedRevision: current.revision, action: "create_component", payload: { component: { type: "note", title: "Contrast card", content: "Saved content" } } });
    current = await context();
    const card = current.document.components.find(component => component.title === "Contrast card");
    await page.goto(origin + `/ideas/workspace/project/${project.id}?view=outline`);
    await page.waitForFunction(key => document.querySelector('[data-testid="workspace-shell"]')?.dataset.ready === "true"
      && document.querySelector('[data-testid="workspace-shell"]')?.dataset.workspaceId === key, key);
    await page.getByTestId("workspace-view-outline").click();
    await page.getByTestId(`outline-component-${card.id}`).click();
    await page.getByTestId("component-content").fill("Unsaved complete recovery draft");
    await page.waitForFunction(() => document.querySelector('[data-testid="component-content"]')?.value === "Unsaved complete recovery draft"
      && document.querySelector('[data-testid="component-save"]')?.disabled === false
      && document.querySelector('[data-testid="workspace-draft-count"]')?.textContent.includes("1 unsaved"));
    await api("POST", `${path}/commands`, { operationId: `${prefix}-concurrent`, actor: "workspace-contrast-other",
      expectedRevision: current.revision, action: "create_component", payload: { component: { type: "note", title: "Concurrent card", content: "Other saved content" } } });
    await page.getByTestId("workspace-refresh").click();
    await page.getByTestId("component-reapply").waitFor({ state: "visible" });
    const targets = [
      ["primary", "workspace-kanban-link"],
      ["secondary", "workspace-refresh"],
      ["danger", "component-remove"],
      ["recovery", "component-reapply"]
    ];
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => document.documentElement.setAttribute("data-bs-theme", theme), theme);
      for (const [variant, testId] of targets) {
        const target = page.getByTestId(testId);
        await target.scrollIntoViewIfNeeded();
        await page.mouse.move(0, 0);
        await page.waitForTimeout(180);
        for (const state of ["normal", "hover"]) {
          if (state === "hover") { await target.hover(); await page.waitForTimeout(180); }
          const colors = await target.evaluate(element => {
            const style = getComputedStyle(element);
            let parent = element;
            let background;
            while (parent) {
              const value = getComputedStyle(parent).backgroundColor;
              const alpha = value.match(/[\d.]+/g)?.map(Number);
              if (alpha && (alpha.length < 4 || alpha[3] > 0)) { background = value; break; }
              parent = parent.parentElement;
            }
            return { foreground: style.color, background, border: style.borderTopColor, disabled: element.disabled === true };
          });
          check(!colors.disabled, `Contrast fixture ${testId} must be enabled.`);
          check(Boolean(colors.background), `An opaque background must be found for ${testId}.`);
          const ratio = contrast(colors.foreground, colors.background);
          check(ratio >= 4.5, `${theme} ${variant} ${state} text contrast ${ratio} is below AA.`);
          if (variant === "recovery" && state === "normal")
            check(contrast(colors.border, colors.background) >= 3, `${theme} recovery border is not visible.`);
          results.push({ theme, variant, state, ...colors, ratio });
        }
      }
    }
    check(await page.getByTestId("component-content").inputValue() === "Unsaved complete recovery draft", "Inspecting theme states must preserve the recovery draft.");
    return { result: "PASS", workspaceId: key, measurements: results };
  } catch (error) {
    const state = await page.evaluate(() => ({
      text: document.querySelector('[data-testid="workspace-shell"]')?.textContent?.slice(0, 6000),
      editor: document.querySelector('[data-testid="component-content"]')?.value,
      revision: document.querySelector('[data-testid="workspace-shell"]')?.dataset.revision
    })).catch(() => null);
    throw new Error(`${error.message}\nContrast fixture state: ${JSON.stringify(state)}`);
  } finally { await page.close(); }
}
