import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { Script, createContext } from "node:vm";

// Run the collocated module, modeling only the DOM/event and async .NET callback boundaries.
const sourceUrl = new URL("../src/KanbanFlow/Components/Workspaces/WorkspaceCanvas.razor.js", import.meta.url);
const script = new Script(readFileSync(sourceUrl, "utf8").replace(/^export /gm, "")
  + "\nglobalThis.canvasModule = { initialize, dispose };", { filename: sourceUrl.pathname });

function canvasBoundary(callback, placements = ["card-position", "second-position"]) {
  const handlers = new Map();
  const error = { hidden: true, textContent: "" };
  const viewportHandlers = new Map();
  const viewport = { scrollLeft: 0, scrollTop: 0, addEventListener: (name, handler) => viewportHandlers.set(name, handler) };
  const boundary = { attached: true, hasError: true, error, handlers, viewport, placementQueries: 0, parentReads: 0, previewOrder: [] };
  const root = {
    dataset: { workspaceId: "idea:11", revision: "8", busy: "false" },
    addEventListener: (name, handler) => handlers.set(name, handler),
    closest: selector => selector === ".workspace-canvas-viewport" ? viewport
      : boundary.attached ? { querySelector: () => boundary.hasError ? error : null } : null,
    querySelectorAll: () => { boundary.placementQueries++; return boundary.cards; },
  };
  boundary.cards = placements.map(value => {
    const { placementId, parentId } = typeof value === "string" ? { placementId: value } : value;
    const properties = new Map();
    const dataset = { placementId, localX: "30", localY: "40", width: "320", height: "220" };
    Object.defineProperty(dataset, "parentId", { get() { boundary.parentReads++; return parentId; } });
    return { dataset, properties, style: {
      setProperty: (name, value) => { boundary.previewOrder.push([placementId, name]); properties.set(name, value); },
      removeProperty: name => properties.delete(name),
    } };
  });
  const handles = boundary.cards.map(card => ({
    captured: [], resized: false,
    closest(selector) { return selector === "[data-placement-id]" ? card : this; },
    classList: { contains: name => name === "workspace-resize-handle" && handles[boundary.cards.indexOf(card)].resized },
    setPointerCapture(pointerId) { this.captured.push(pointerId); },
  }));
  const context = createContext({ AbortController });
  script.runInContext(context);
  context.canvasModule.initialize(root, { invokeMethodAsync: callback });
  boundary.handles = handles;
  boundary.key = () => handlers.get("keydown")({ target: handles[0], key: "ArrowRight", shiftKey: false, preventDefault() {} });
  boundary.pointer = (name, pointerId, index = 0, clientX = 0, clientY = 0) => handlers.get(name)({
    target: handles[index], pointerId, clientX, clientY, button: 0, preventDefault() {},
  });
  boundary.scroll = (left, top, notify = true) => {
    viewport.scrollLeft = left;
    viewport.scrollTop = top;
    if (notify) viewportHandlers.get("scroll")?.();
  };
  boundary.dispose = () => context.canvasModule.dispose(root);
  return boundary;
}

for (const detach of ["section", "error element"]) {
  for (const fails of [false, true]) {
    test(`a ${fails ? "failed" : "successful"} callback settles safely after its ${detach} is removed`, async () => {
      let finish;
      const pending = new Promise((resolve, reject) => { finish = () => fails ? reject(new Error("transport disconnected")) : resolve(); });
      const calls = [];
      const canvas = canvasBoundary(async (...args) => { calls.push(args); await pending; });
      const sent = canvas.key();
      if (detach === "section") canvas.attached = false;
      else canvas.hasError = false;
      finish();

      await assert.doesNotReject(sent);
      assert.equal(calls.length, 1);
      assert.equal(calls[0][0], "ApplyCanvasLayout");
      assert.equal(calls[0][1], "idea:11");
      assert.equal(calls[0][2][0].x, 40);
      assert.equal(calls[0][3], 8);
      assert.equal(canvas.error.hidden, true);
      assert.equal(canvas.error.textContent, "");
      canvas.dispose();
    });
  }
}

test("a later successful layout clears the prior transport failure and permits another commit", async () => {
  let fails = true;
  let attempts = 0;
  const canvas = canvasBoundary(async () => { attempts++; if (fails) throw new Error("transport disconnected"); });
  await canvas.key();
  assert.equal(canvas.error.hidden, false);
  assert.match(canvas.error.textContent, /layout could not be sent/);
  fails = false;
  await canvas.key();
  assert.equal(canvas.error.hidden, true);
  assert.equal(canvas.error.textContent, "");
  await assert.doesNotReject(canvas.key());
  assert.equal(attempts, 3);
  canvas.dispose();
});

test("a second pointer cannot replace an active drag or leave either card offset", async () => {
  const calls = [];
  const canvas = canvasBoundary(async (...args) => calls.push(args));
  canvas.pointer("pointerdown", 1);
  canvas.pointer("pointermove", 1, 0, 25, 15);
  canvas.pointer("pointerdown", 2, 1);
  canvas.pointer("pointermove", 2, 1, 70, 80);

  assert.deepEqual(canvas.handles[0].captured, [1]);
  assert.deepEqual(canvas.handles[1].captured, []);
  assert.equal(canvas.cards[0].properties.get("--preview-dx"), "25px");
  assert.equal(canvas.cards[1].properties.size, 0);
  await canvas.pointer("pointerup", 2, 1, 70, 80);
  assert.equal(calls.length, 0);
  await canvas.pointer("pointerup", 1, 0, 25, 15);

  assert.equal(calls.length, 1);
  assert.equal(calls[0][2][0].placementId, "card-position");
  assert.equal(calls[0][2][0].x, 55);
  assert.equal(calls[0][2][0].y, 55);
  assert.equal(canvas.cards[0].properties.size, 0);
  assert.equal(canvas.cards[1].properties.size, 0);
  canvas.dispose();
});

test("canceling a foreign pointer preserves the active drag until its own pointer completes", async () => {
  const calls = [];
  const canvas = canvasBoundary(async (...args) => calls.push(args));
  canvas.pointer("pointerdown", 1);
  canvas.pointer("pointermove", 1, 0, 25, 15);
  canvas.pointer("pointercancel", 2, 1);
  assert.equal(canvas.cards[0].properties.get("--preview-dx"), "25px");
  await canvas.pointer("pointerup", 1, 0, 25, 15);
  assert.equal(calls.length, 1);
  assert.equal(calls[0][2][0].x, 55);
  assert.equal(canvas.cards[0].properties.size, 0);
  canvas.dispose();
});

test("keyboard layout commands wait until the active pointer gesture finishes", async () => {
  const calls = [];
  const canvas = canvasBoundary(async (...args) => calls.push(args));
  canvas.pointer("pointerdown", 1);
  canvas.pointer("pointermove", 1, 0, 25, 15);
  await canvas.key();
  assert.equal(calls.length, 0);
  assert.equal(canvas.cards[0].properties.get("--preview-dx"), "25px");
  await canvas.pointer("pointerup", 1, 0, 25, 15);
  await canvas.key();
  assert.equal(calls.length, 2);
  assert.equal(calls[0][2][0].x, 55);
  assert.equal(calls[1][2][0].x, 40);
  canvas.dispose();
});

test("cancel and disposal clear the owning pointer preview without publishing it", async () => {
  const calls = [];
  const canvas = canvasBoundary(async (...args) => calls.push(args));
  canvas.handles[0].resized = true;
  canvas.pointer("pointerdown", 1);
  canvas.pointer("pointermove", 1, 0, 25, 15);
  assert.equal(canvas.cards[0].properties.get("--preview-width"), "345px");
  canvas.pointer("pointercancel", 1);
  assert.equal(canvas.cards[0].properties.size, 0);
  canvas.pointer("pointerdown", 2);
  canvas.pointer("pointermove", 2, 0, 10, 20);
  canvas.dispose();
  assert.equal(canvas.cards[0].properties.size, 0);
  assert.equal(calls.length, 0);
});

test("a nested group is discovered with one placement query and one parent read per node", () => {
  const placements = Array.from({ length: 80 }, (_, index) => ({ placementId: `node-${index}`, parentId: index === 0 ? undefined : `node-${index - 1}` }));
  const canvas = canvasBoundary(async () => {}, placements);
  canvas.pointer("pointerdown", 1);
  canvas.pointer("pointermove", 1, 0, 20, 10);
  assert.equal(canvas.placementQueries, 1);
  assert.equal(canvas.parentReads, placements.length);
  assert.ok(canvas.cards.every(card => card.properties.get("--preview-dx") === "20px"));
  assert.deepEqual(canvas.previewOrder.filter(([, name]) => name === "--preview-dx").map(([id]) => id), placements.map(p => p.placementId));
  canvas.dispose();
  assert.ok(canvas.cards.every(card => card.properties.size === 0));
});

test("group traversal preserves DOM order while terminating cycles and excluding unrelated placements", () => {
  const placements = [
    { placementId: "root", parentId: "cycle-child" },
    { placementId: "grandchild", parentId: "child" },
    { placementId: "other" },
    { placementId: "child", parentId: "root" },
    { placementId: "cycle-child", parentId: "root" },
    { placementId: "unrelated-cycle-a", parentId: "unrelated-cycle-b" },
    { placementId: "unrelated-cycle-b", parentId: "unrelated-cycle-a" },
    { placementId: "missing-parent", parentId: "missing" },
  ];
  const canvas = canvasBoundary(async () => {}, placements);
  canvas.pointer("pointerdown", 1);
  canvas.pointer("pointermove", 1, 0, 20, 10);
  assert.equal(canvas.placementQueries, 1);
  assert.deepEqual(canvas.previewOrder.filter(([, name]) => name === "--preview-dx").map(([id]) => id), ["root", "grandchild", "child", "cycle-child"]);
  assert.equal(canvas.cards[2].properties.size, 0);
  assert.ok(canvas.cards.slice(5).every(card => card.properties.size === 0));
  canvas.dispose();
});

for (const resize of [false, true]) {
  test(`viewport scroll combines with pointer movement for ${resize ? "resize" : "drag"} preview and publication`, async () => {
    const calls = [];
    const canvas = canvasBoundary(async (...args) => calls.push(args));
    canvas.handles[0].resized = resize;
    canvas.scroll(100, 50);
    canvas.pointer("pointerdown", 1, 0, 200, 100);
    canvas.scroll(140, 80);
    canvas.pointer("pointermove", 1, 0, 215, 125);
    assert.equal(canvas.cards[0].properties.get(resize ? "--preview-width" : "--preview-dx"), resize ? "375px" : "55px");
    assert.equal(canvas.cards[0].properties.get(resize ? "--preview-height" : "--preview-dy"), resize ? "275px" : "55px");
    // Pointer-up may omit coordinates in clients dispatching synthetic input events.
    await canvas.pointer("pointerup", 1);
    assert.equal(calls.length, 1);
    const update = calls[0][2][0];
    assert.equal(update[resize ? "width" : "x"], resize ? 375 : 85);
    assert.equal(update[resize ? "height" : "y"], resize ? 275 : 95);
    assert.equal(canvas.cards[0].properties.size, 0);
    canvas.dispose();
  });

  test(`scroll-only changes before pointer-up are saved for ${resize ? "resize" : "drag"}`, async () => {
    const calls = [];
    const canvas = canvasBoundary(async (...args) => calls.push(args));
    canvas.handles[0].resized = resize;
    canvas.scroll(100, 50);
    canvas.pointer("pointerdown", 1, 0, 200, 100);
    canvas.scroll(125, 40);
    assert.equal(canvas.cards[0].properties.get(resize ? "--preview-width" : "--preview-dx"), resize ? "345px" : "25px");
    canvas.scroll(140, 30, false); // Final offset is observed even before a scroll notification or pointermove.
    await canvas.pointer("pointerup", 1);
    assert.equal(calls.length, 1);
    const update = calls[0][2][0];
    assert.equal(update[resize ? "width" : "x"], resize ? 360 : 70);
    assert.equal(update[resize ? "height" : "y"], resize ? 200 : 20);
    canvas.dispose();
  });
}
