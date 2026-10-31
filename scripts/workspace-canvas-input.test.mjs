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
  };
  const card = { dataset: { placementId: "card-position", localX: "30", localY: "40", width: "320", height: "220" } };
  const handle = { closest: selector => selector === "[data-placement-id]" ? card : handle };
  const context = createContext({ AbortController });
  script.runInContext(context);
  context.canvasModule.initialize(root, { invokeMethodAsync: callback });
  boundary.key = () => handlers.get("keydown")({ target: handle, key: "ArrowRight", shiftKey: false, preventDefault() {} });
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
