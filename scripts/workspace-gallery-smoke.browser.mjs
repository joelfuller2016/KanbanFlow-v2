// Run from the owning repository's ignored scratch directory with a named Playwright CLI session.
async page => {
  const address = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const base = address.origin;
  if (!["localhost", "127.0.0.1", "[::1]"].includes(address.hostname)) throw new Error("Use explicitly disposable loopback data.");
  const prefix = `workspace-gallery-${Date.now()}`;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const api = async (method, path, data, status = 200) => {
    const response = await page.request.fetch(base + path, { method, data });
    check(response.status() === status, `${method} ${path}: ${response.status()} ${await response.text()}`);
    return response.json();
  };
  const capabilities = await api("GET", "/api/workspace-capabilities");
  const pageSize = capabilities.defaultPageSize;
  check(pageSize > 0 && pageSize <= 100, "Keep this live fixture bounded to the actual small default page size.");
  const keys = [];
  for (let i = 0; i < pageSize + 4; i++) {
    const result = await api("POST", "/api/workspaces", { operationId: `${prefix}-${i}`, actor: "gallery-smoke", title: `${prefix} idea ${i}` });
    keys.push(result.workspaceId);
  }
  const project = await api("POST", "/api/projects", { name: `${prefix} standalone` }, 201);
  keys.push(`project:${project.id}`);
  await page.goto(base + "/ideas");
  await page.waitForFunction(() => document.querySelector('[data-testid="workspace-gallery"]')?.dataset.ready === "true");
  await page.getByTestId("workspace-search").fill(prefix);
  await page.getByTestId("workspace-search-submit").click();
  await page.waitForFunction(total => document.querySelector('[data-testid="workspace-total"]')?.textContent.includes(`${total} workspaces`), keys.length);
  const cards = page.locator('[data-testid^="gallery-card-"]');
  check(await cards.count() === pageSize, "The first UI page must show the complete default page.");
  const later = await api("POST", "/api/workspaces", { operationId: `${prefix}-later`, actor: "gallery-smoke", title: `${prefix} late insertion` });
  await page.getByTestId("workspace-load-more").click();
  await page.waitForFunction(count => document.querySelectorAll('[data-testid^="gallery-card-"]').length === count, keys.length);
  const displayed = await cards.evaluateAll(nodes => nodes.map(node => node.getAttribute("data-testid").slice("gallery-card-".length)));
  check(new Set(displayed).size === keys.length && keys.every(key => displayed.includes(key)), "UI continuation must show every initial member once.");
  check(!displayed.includes(later.workspaceId), "Insertion cannot alter an already-started gallery traversal.");
  await page.getByTestId("workspace-search-submit").click();
  await page.waitForFunction(total => document.querySelector('[data-testid="workspace-total"]')?.textContent.includes(`${total} workspaces`), keys.length + 1);
  await page.getByTestId("workspace-kind").selectOption("project");
  await page.getByTestId("workspace-search-submit").click();
  await page.waitForFunction(() => document.querySelector('[data-testid="workspace-total"]')?.textContent.includes("1 workspaces"));
  check(await cards.count() === 1 && await page.getByTestId(`gallery-card-project:${project.id}`).count() === 1, "Project filtering must retain the standalone project.");
  await page.getByTestId("workspace-search").fill(prefix + " nonexistent");
  await page.getByTestId("workspace-search-submit").click();
  await page.waitForFunction(() => document.querySelector('[data-testid="workspace-total"]')?.textContent.includes("0 workspaces"));
  check(await cards.count() === 0, "An empty result must not leave stale cards in the gallery.");
  return { result: "PASS", pageSize, initialMembers: keys.length, snapshotStableAfterInsert: true, projectFilter: true, emptyState: true, fixturePrefix: prefix };
}
