import assert from "node:assert/strict";

export async function projectRequirementLinks(page, api, clickUntil) {
  const project = await api("POST", "/api/projects", { name: "Requirement link regression" });
  try {
    const feature = await api("POST", `/api/projects/${project.id}/features`, { name: "Dashboard" });
    const task = await api("POST", `/api/features/${feature.id}/tasks`, { name: "KPI cards" });
    const endpoint = `/api/projects/${project.id}/requirements`;
    const dual = await api("POST", endpoint, { text: "Both links", featureId: feature.id, taskItemId: task.id });
    const featureOnly = await api("POST", endpoint, { text: "Feature link", featureId: feature.id });
    const taskOnly = await api("POST", endpoint, { text: "Task link", taskItemId: task.id });

    const origin = new URL(page.url()).origin;
    await page.goto(`${origin}/project/${project.id}`);
    const dualRow = page.getByTestId(`requirement-row-${dual.id}`);
    const toggle = page.getByTestId("requirements-toggle");
    await clickUntil(() => toggle.click(), dualRow);

    const featureLabel = "Feature: Dashboard";
    const taskLabel = "Task: KPI cards";
    const labels = (row, label) => row.getByText(label, { exact: true }).locator("..");
    const normalize = (text) => text.replace(/\s+/g, " ").trim();
    const dualLabels = labels(dualRow, featureLabel);
    const raw = await dualLabels.innerText();
    assert.equal(normalize(raw), `${featureLabel} · ${taskLabel}`);
    assert.ok(!raw.includes("\uFFFD"));
    const accessible = await dualLabels.ariaSnapshot();
    assert.match(accessible, /Feature: Dashboard[\s\S]*·[\s\S]*Task: KPI cards/);
    for (const [requirement, text] of [[featureOnly, featureLabel], [taskOnly, taskLabel]]) {
      const row = page.getByTestId(`requirement-row-${requirement.id}`);
      assert.equal(normalize(await labels(row, text).innerText()), text);
    }
  } finally {
    await api("DELETE", `/api/projects/${project.id}`);
  }
}
