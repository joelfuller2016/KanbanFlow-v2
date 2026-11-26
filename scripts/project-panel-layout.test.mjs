import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = new URL("../src/KanbanFlow/wwwroot/js/project-panel-layout.js", import.meta.url);
const { attachResizeHandle, load, reset, save, watchTaskDetails } =
  await import("data:text/javascript;base64," + readFileSync(source).toString("base64"));

const storageData = new Map();
globalThis.localStorage = {
  getItem: key => storageData.get(key) ?? null,
  setItem: (key, value) => storageData.set(key, value),
  removeItem: key => storageData.delete(key),
};

test("stores each project's panel preferences independently and resets only one", () => {
  const first = {
    outline: { dock: "right", width: 380, collapsed: true },
    details: { dock: "left", width: 490, collapsed: false },
  };
  const second = {
    outline: { dock: "left", width: 260, collapsed: false },
    details: { dock: "right", width: null, collapsed: true },
  };
  save(21, first);
  save(22, second);
  assert.deepEqual(load(21), first);
  assert.deepEqual(load(22), second);

  reset(21);
  assert.equal(load(21), null);
  assert.deepEqual(load(22), second);
});

test("normalizes malformed and partially stored panel preferences", () => {
  const first = { outline: { dock: "top", width: -12, collapsed: "true" }, details: { dock: "left", width: 432 } };
  save(23, first);
  assert.deepEqual(load(23), {
    outline: { dock: "left", width: null, collapsed: false },
    details: { dock: "left", width: 432, collapsed: false },
  });

  const key = [...storageData.keys()].find(key => storageData.get(key) === JSON.stringify(first));
  storageData.set(key, "{bad json");
  assert.equal(load(23), null);
});

function resizeBoundary(dock, { preferred = "", panel = "outline", width = 320, compression = 0, viewportWidth = 1280 } = {}) {
  const attrs = new Map();
  const style = new Map();
  const commits = [];
  const pane = {
    dataset: { dock, projectPanel: panel },
    style: { setProperty: (name, value) => style.set(name, value), removeProperty: name => style.delete(name) },
    getBoundingClientRect() {
      const preview = Number.parseFloat(style.get("--project-panel-resize-preview"));
      return { width: Number.isFinite(preview) ? preview - compression : width };
    },
  };
  const view = new EventTarget();
  view.innerWidth = viewportWidth;
  view.getComputedStyle = el => {
    assert.equal(el, pane);
    return { minWidth: "224px", maxWidth: "410px", getPropertyValue: () => preferred };
  };
  view.ResizeObserver = class {
    observe(el) { assert.equal(el, pane); }
    disconnect() {}
  };
  const handle = new EventTarget();
  Object.assign(handle, {
    ownerDocument: { defaultView: view },
    closest: selector => selector === "[data-project-panel]" ? pane : null,
    getClientRects: () => [{ width: 12 }],
    setAttribute: (name, value) => attrs.set(name, value),
    setPointerCapture() {},
    focus() {},
  });
  const reference = {
    async invokeMethodAsync(method, width) {
      assert.equal(method, "CommitWidthAsync");
      commits.push(width);
    },
  };
  const controller = attachResizeHandle(handle, reference);
  const dispatch = (type, properties) => {
    const event = Object.assign(new Event(type, { cancelable: true }), properties);
    handle.dispatchEvent(event);
    return event;
  };
  return { attrs, style, commits, dispatch, controller };
}

test("keyboard resize moves a right dock's divider left and honors its bounds", async () => {
  const resize = resizeBoundary("right");
  assert.equal(resize.attrs.get("aria-valuenow"), "320");
  assert.equal(resize.attrs.get("aria-valuemin"), "224");
  assert.equal(resize.attrs.get("aria-valuemax"), "410");
  const left = resize.dispatch("keydown", { key: "ArrowLeft" });
  assert.equal(left.defaultPrevented, true);
  await Promise.resolve();
  resize.dispatch("keydown", { key: "End" });
  await Promise.resolve();
  resize.dispatch("keydown", { key: "Home" });
  await Promise.resolve();
  assert.deepEqual(resize.commits, [336, 410, 224]);
  const tab = resize.dispatch("keydown", { key: "Tab" });
  assert.equal(tab.defaultPrevented, false);
  resize.controller.dispose();
});

test("pointer resize previews movement and commits the owning pointer's bounded release", async () => {
  const resize = resizeBoundary("left");
  const pointer = (name, pointerId, clientX) => resize.dispatch(name, { pointerId, clientX, button: 0 });
  pointer("pointerdown", 1, 300);
  pointer("pointermove", 1, 340);
  assert.equal(resize.style.get("--project-panel-resize-preview"), "360px");
  pointer("pointerup", 2, 370);
  assert.deepEqual(resize.commits, []);
  pointer("pointerup", 1, 420);
  await Promise.resolve();
  assert.deepEqual(resize.commits, [410]);
  assert.equal(resize.style.size, 0);
  resize.controller.dispose();
});

test("resizing a compressed dock increases its requested width", async () => {
  const resize = resizeBoundary("left", { preferred: "380px" });
  resize.dispatch("keydown", { key: "ArrowRight" });
  await Promise.resolve();
  assert.deepEqual(resize.commits, [396]);
  resize.controller.dispose();
});

test("resizing a compressed default-width outline grows it from its CSS width", async () => {
  const resize = resizeBoundary("left", { preferred: "320px", width: 290, compression: 30 });
  resize.dispatch("keydown", { key: "ArrowRight" });
  assert.equal(resize.attrs.get("aria-valuenow"), "306");
  await Promise.resolve();
  assert.deepEqual(resize.commits, [336]);
  resize.controller.dispose();
});

test("resizing a compressed default-width task details pane uses its responsive default", async () => {
  const resize = resizeBoundary("right", { panel: "details", width: 330, compression: 30, viewportWidth: 1000 });
  resize.dispatch("keydown", { key: "ArrowLeft" });
  assert.equal(resize.attrs.get("aria-valuenow"), "346");
  await Promise.resolve();
  assert.deepEqual(resize.commits, [376]);
  resize.controller.dispose();
});

test("losing pointer capture clears the preview without saving a width", () => {
  const resize = resizeBoundary("left");
  const pointer = (name, clientX) => resize.dispatch(name, { pointerId: 1, clientX, button: 0 });
  pointer("pointerdown", 300);
  pointer("pointermove", 330);
  pointer("lostpointercapture");
  assert.equal(resize.style.size, 0);
  assert.deepEqual(resize.commits, []);
  resize.controller.dispose();
});

test("a fresh desktop dock reveals its header without scrolling on mode changes", () => {
  const narrow = new EventTarget();
  narrow.matches = false;
  const scrolls = [];
  let shown = false;
  let update;
  const view = {
    matchMedia: () => narrow,
    KanbanFlow: { initDialog() {}, destroyDialog() {} },
    MutationObserver: class {
      constructor(callback) { update = callback; }
      observe() {}
      disconnect() {}
    },
  };
  const panel = {
    ownerDocument: { defaultView: view, activeElement: null },
    classList: { contains: () => shown },
    dataset: { size: "half" },
    contains: () => false,
    setAttribute() {},
    removeAttribute() {},
    querySelector: selector => selector === ".task-detail-header"
      ? { scrollIntoView: options => scrolls.push(options) }
      : null,
  };
  const controller = watchTaskDetails(panel, {});
  shown = true;
  update();
  assert.deepEqual(scrolls, [{ block: "nearest", inline: "nearest" }]);

  panel.dataset.size = "full";
  update();
  panel.dataset.size = "half";
  update();
  assert.equal(scrolls.length, 1);
  controller.dispose();
});
