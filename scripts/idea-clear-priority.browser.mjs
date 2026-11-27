import assert from "node:assert/strict";

export async function verifyIdeaPriorityCanBeCleared(page, api, clickUntil, ideaId) {
  const modal = page.getByTestId("idea-modal");
  const priority = modal.getByRole("combobox", { name: "Priority" });
  const badge = page.getByTestId(`idea-card-priority-${ideaId}`);
  const openEditor = async () => {
    const edit = page.getByTestId(`idea-card-edit-${ideaId}`);
    await clickUntil(() => page.getByTestId(`idea-card-menu-${ideaId}`).click(), edit);
    await edit.click();
    await modal.getByTestId("idea-save-button").waitFor({ state: "visible" });
    await page.waitForFunction(() => !document.querySelector('[data-testid="idea-save-button"]').disabled);
  };

  assert.equal((await api("GET", `/api/ideas/${ideaId}`)).priority, "High");
  await badge.waitFor({ state: "visible" });
  await openEditor();
  assert.equal(await priority.inputValue(), "2");
  await priority.press("Home");
  assert.equal(await priority.inputValue(), "");
  await modal.getByTestId("idea-save-button").click();
  await modal.waitFor({ state: "hidden" });

  assert.equal((await api("GET", `/api/ideas/${ideaId}`)).priority, null);
  await badge.waitFor({ state: "detached" });
  await openEditor();
  assert.equal(await priority.inputValue(), "");
  await modal.getByTestId("idea-cancel-button").click();
  await modal.waitFor({ state: "hidden" });
}
