import assert from "node:assert/strict";

export async function projectOutlineStaleTarget(page, api) {
  const project = await api("POST", "/api/projects", { name: "Stale outline target regression" });
  const origin = new URL(page.url()).origin;
  const originalViewport = page.viewportSize();
  try {
    const feature = await api("POST", `/api/projects/${project.id}/features`, { name: "Outline feature" });
    for (let index = 0; index < 12; index++) {
      await api("POST", `/api/features/${feature.id}/tasks`, { name: `Outline scroll fixture ${index + 1}` });
    }
    const task = await api("POST", `/api/features/${feature.id}/tasks`, { name: "Task removed by another client" });
    const remaining = await api("POST", `/api/features/${feature.id}/tasks`, { name: "Remaining task" });

    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${origin}/project/${project.id}`);
    await page.waitForFunction(() => window.KanbanFlow?._boardSortables?.some(sortable => sortable.el?.isConnected));
    const card = page.getByTestId(`board-card-${task.id}-title`);
    await card.scrollIntoViewIfNeeded();
    assert.ok(await card.evaluate(button => window.scrollY > 0 || button.closest(".board-column-cards")?.scrollTop > 0),
      "The fixture must open the stale card from a scrolled board.");
    await card.click();
    const panel = page.getByTestId("task-detail-panel");
    await panel.waitFor({ state: "visible" });

    // Simulate another client removing the selected task while its details are still open.
    await api("DELETE", `/api/tasks/${task.id}`);
    await panel.getByTestId("task-detail-show-in-outline-button").click();
    await panel.waitFor({ state: "hidden" });
    await page.getByTestId(`board-card-${task.id}-title`).waitFor({ state: "detached" });
    assert.equal(await page.locator("#project-outline .outline-item.is-selected").count(), 0);

    const alert = page.getByRole("alert").filter({ hasText: "This item is no longer available in the outline." });
    await alert.waitFor({ state: "visible", timeout: 5000 });
    await page.waitForFunction(() => {
      const notice = document.activeElement;
      if (notice?.getAttribute("role") !== "alert") return false;
      const { top, bottom } = notice.getBoundingClientRect();
      return top >= 0 && bottom <= window.innerHeight;
    }, null, { timeout: 5000 });
    await alert.getByRole("button", { name: "Dismiss outline notice" }).click();
    await alert.waitFor({ state: "hidden" });

    await page.getByTestId(`board-card-${remaining.id}-title`).click();
    await panel.waitFor({ state: "visible" });
    await panel.getByTestId("task-detail-show-in-outline-button").click();
    await panel.waitFor({ state: "hidden" });
    await page.getByTestId(`outline-task-${remaining.id}`).waitFor({ state: "visible" });
    await page.waitForFunction(id =>
      document.activeElement.closest("#project-outline .outline-item")?.dataset.testid === `outline-task-${id}`, remaining.id);
  } finally {
    if (originalViewport) await page.setViewportSize(originalViewport);
    await api("DELETE", `/api/projects/${project.id}`);
  }
}
