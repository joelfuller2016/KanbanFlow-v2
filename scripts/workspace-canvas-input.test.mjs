import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { Script, createContext } from "node:vm";

// Run the collocated module, modeling only the DOM/event and async .NET callback boundaries.
const sourceUrl = new URL("../src/KanbanFlow/Components/Workspaces/WorkspaceCanvas.razor.js", import.meta.url);
const script = new Script(readFileSync(sourceUrl, "utf8").replace(/^export /gm, "")
  + "\nglobalThis.canvasModule = { initialize, dispose };", { filename: sourceUrl.pathname });

function canvasBoundary(callback) {
  const handlers = new Map();
  const error = { hidden: true, textContent: "" };
  const boundary = { attached: true, hasError: true, error, handlers };
  const root = {
    dataset: { workspaceId: "idea:11", revision: "8", busy: "false" },
    addEventListener: (name, handler) => handlers.set(name, handler),
    closest: () => boundary.attached ? { querySelector: () => boundary.hasError ? error : null } : null,
    querySelectorAll: () => boundary.cards,
  };
  boundary.cards = ["card-position", "second-position"].map(placementId => {
    const properties = new Map();
    return { dataset: { placementId, localX: "30", localY: "40", width: "320", height: "220" },
      properties, style: { setProperty: (name, value) => properties.set(name, value), removeProperty: name => properties.delete(name) } };
  });
  const handles = boundary.cards.map(card => ({
    captured: [], resized: false, listeners: new Map(),
    closest(selector) { return selector === "[data-placement-id]" ? card : this; },
    classList: { contains: name => name === "workspace-resize-handle" && handles[boundary.cards.indexOf(card)].resized },
    setPointerCapture(pointerId) { this.captured.push(pointerId); },
    addEventListener(name, handler) { this.listeners.set(name, handler); },
    focus() {},
  }));
  const context = createContext({ AbortController });
  script.runInContext(context);
  context.canvasModule.initialize(root, { invokeMethodAsync: callback });
  boundary.handles = handles;
  boundary.lostCapture = (index = 0) => handles[index].listeners.get("lostpointercapture")?.();
  boundary.key = () => handlers.get("keydown")({ target: handles[0], key: "ArrowRight", shiftKey: false, preventDefault() {} });
  boundary.pointer = (name, pointerId, index = 0, clientX = 0, clientY = 0) => handlers.get(name)({
    target: handles[index], pointerId, clientX, clientY, button: 0, preventDefault() {},
  });
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
