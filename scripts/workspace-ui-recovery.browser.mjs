// playwright-cli -s=<named-session> run-code --filename=scripts/workspace-ui-recovery.browser.mjs
// Disposable local app only. Injects a browser storage boundary failure; the service remains real.
async page => {
  const { origin, hostname } = await page.evaluate(() => ({ origin: location.origin, hostname: location.hostname }));
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  check(["127.0.0.1", "localhost", "[::1]"].includes(hostname),
    "Run browser draft recovery only against an explicitly disposable loopback app.");
  const prefix = `workspace-ui-recovery-${Date.now()}`;
  const storagePrefix = "kanbanflow.workspace.draft.v1:";
  const id = testId => page.getByTestId(testId);
  const longPasteInputs = [];
  // Model one user paste through the actual DOM input handler, avoiding CLI's long-fill keystroke expansion.
  const fillOrPasteText = async (locator, value) => {
    if (value.length <= 4096) return locator.fill(value);
    await locator.waitFor({ state: "visible", timeout: 15000 });
    await locator.focus();
    const observed = await locator.evaluate((element, value) => {
      if (!(element instanceof HTMLTextAreaElement)) throw new Error("Long text paste requires a real textarea.");
      let inputEvents = 0, inputType = "", dataMatches = false;
      const listener = event => { inputEvents++; inputType = event.inputType; dataMatches = event.data === value; };
      element.addEventListener("input", listener, { passive: true });
      try {
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(element, value);
        element.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertFromPaste", data: value }));
        return { value: element.value, inputEvents, inputType, dataMatches, testId: element.getAttribute("data-testid") };
      } finally { element.removeEventListener("input", listener); }
    }, value);
    check(observed.value === value, "One paste input must retain every authored Unicode/Markdown character.");
    check(observed.inputEvents === 1 && observed.inputType === "insertFromPaste" && observed.dataMatches,
      "The recovery fixture must enter its complete buffer through one real DOM paste input event.");
    longPasteInputs.push({ testId: observed.testId, characters: value.length, inputEvents: observed.inputEvents, inputType: observed.inputType });
  };
  const checks = [];
  const errors = [];
  const onError = error => errors.push(error.message);
  page.on("pageerror", onError);
  const originalViewport = page.viewportSize();
  let stage = "fixture setup";
  let key, componentId;
  const markStage = () => page.evaluate(value => { document.body.dataset.workspaceTestStage = value; document.body.dataset.workspaceTestSuite = "recovery"; }, stage);
  const waitState = (predicate, value) => page.waitForFunction(predicate, value, { timeout: 15000 });
  const step = async (name, action) => { stage = name; await markStage(); await action(); checks.push(name); };
  const api = async (method, path, data, expected = 200) => {
    const response = await page.request.fetch(origin + path, { method, data, timeout: 15000 });
    const text = await response.text();
    check(response.status() === expected, `${method} ${path}: expected ${expected}, received ${response.status()}: ${text}`);
    return text.length === 0 ? null : JSON.parse(text);
  };
  const context = () => api("GET", `/api/workspaces/${key}/context`);
  const serviceState = async () => {
    const [current, changes] = await Promise.all([context(), api("GET", `/api/workspaces/${key}/changes`)]);
    return { current, changes };
  };
  const unchanged = async (before, message) => {
    const after = await serviceState();
    check(after.current.revision === before.current.revision && after.current.lastActivityId === before.current.lastActivityId
      && JSON.stringify(after.current.document) === JSON.stringify(before.current.document)
      && after.changes.total === before.changes.total
      && JSON.stringify(after.changes.items) === JSON.stringify(before.changes.items), message);
    return after;
  };
  const saved = async (text, expectedRevision) => {
    const deadline = Date.now() + 15000;
    do {
      const current = await context();
      if (current.revision === expectedRevision && current.document.components.some(item => item.id === componentId && item.content === text)) return current;
      await page.waitForTimeout(100);
    } while (Date.now() < deadline);
    throw new Error("Explicit storage recovery did not persist the complete draft at the expected revision.");
  };
  const faultState = () => page.evaluate(() => {
    const state = window.__kanbanflowWorkspaceStorageFault.state;
    return { failures: state.failures, lastCommand: state.lastCommand, commands: state.commands };
  });
  const failStorage = enabled => page.evaluate(value => {
    window.__kanbanflowWorkspaceStorageFault.state.enabled = value;
  }, enabled);
  const failureRendered = async after => {
    await id("workspace-storage-error").waitFor({ state: "visible", timeout: 15000 });
    await waitState(previous => {
      const failures = window.__kanbanflowWorkspaceStorageFault.state.failures;
      const text = document.querySelector('[data-testid="workspace-storage-error"]')?.textContent ?? "";
      const rendered = text.match(/workspace draft quota boundary attempt (\d+)/);
      return failures > previous && rendered !== null && Number(rendered[1]) > previous && Number(rendered[1]) <= failures;
    }, after);
    return (await faultState()).failures;
  };
  const replayReceipt = async command => {
    check(command?.operationId && command.action === "update_component" && command.payload.componentId === componentId,
      "The browser storage boundary must expose the actual immutable update operation envelope.");
    const before = await serviceState();
    const replay = await api("POST", `/api/workspaces/${key}/commands`, command);
    check(replay.replayed === true && replay.operationId === command.operationId && replay.revision === before.current.revision,
      "Replaying the exact recovered UI operation must return its committed receipt.");
    await unchanged(before, "Receipt replay must not add another revision, activity, comment or component change.");
    return command.operationId;
  };
  const text = "\n  ## Complete unsaved buffer Ω 漢字 🧭\n\n```text\n    keep indentation and trailing spaces  \n```\n".repeat(550)
    + "\nFINAL RECOVERY REQUIREMENT Ω  \n";
  check(text.length > 40000, "The unsaved storage recovery fixture must exceed 40,000 characters.");
  const secondText = text + "\n  SECOND IMMUTABLE PENDING OPERATION Ω  \n";
  const operationIds = [];
  try {
    const created = await api("POST", "/api/workspaces", {
      operationId: `${prefix}-capture`, actor: "browser-recovery-fixture", title: prefix,
      body: "\n  Original source remains complete Ω  \n", plan: "Original recovery plan",
    });
    key = created.workspaceId;
    const added = await api("POST", `/api/workspaces/${key}/commands`, {
      operationId: `${prefix}-component`, expectedRevision: created.revision, actor: "browser-recovery-fixture",
      action: "create_component", payload: { component: { type: "markdown", title: "Storage recovery buffer", content: "Saved baseline Ω" } },
    });
    componentId = added.componentId;
    await page.setViewportSize({ width: 1280, height: 1000 });
    await page.goto(`${origin}/ideas/workspace/${key.replace(":", "/")}`, { timeout: 15000 });
    await waitState(expected => {
      const shell = document.querySelector('[data-testid="workspace-shell"]');
      return shell?.dataset.ready === "true" && shell?.dataset.workspaceId === expected;
    }, key);
    await markStage();
    await id("workspace-view-outline").click();
    await id(`outline-component-${componentId}`).click();
    await waitState(() => document.querySelector('[data-testid="component-content"]')?.value === "Saved baseline Ω");
    const baseline = await serviceState();

    // This replaces only the browser setItem boundary for this module's session drafts.
    // Every successful call still invokes the native Storage implementation; no API is intercepted.
    await page.evaluate(prefix => {
      if (window.__kanbanflowWorkspaceStorageFault) throw new Error("A previous workspace storage fault was not restored.");
      const original = Storage.prototype.setItem;
      const state = { enabled: true, failures: 0, lastCommand: null, commands: [] };
      Storage.prototype.setItem = function (key, value) {
        if (this === window.sessionStorage && String(key).startsWith(prefix)) {
          const pending = JSON.parse(String(value)).pending?.command;
          if (pending) {
            state.lastCommand = pending;
            state.commands.push(pending);
          }
          if (state.enabled) {
            state.failures++;
            throw new DOMException(`workspace draft quota boundary attempt ${state.failures}`, "QuotaExceededError");
          }
        }
        return original.call(this, key, value);
      };
      window.__kanbanflowWorkspaceStorageFault = { state, restore: () => { Storage.prototype.setItem = original; } };
    }, storagePrefix);

    await step("one full-text paste storage failure retains the buffer and circuit", async () => {
      await fillOrPasteText(id("component-content"), text);
      await failureRendered(0);
      check(await id("component-content").inputValue() === text, "A storage failure must retain the complete unsaved Unicode/Markdown buffer.");
      check(await id("component-save").isDisabled(), "Save must pause while its browser retry storage is unhealthy.");
      await id("workspace-view-canvas").click();
      await waitState(() => document.querySelector('[data-testid="workspace-canvas"]')?.dataset.ready === "true");
      await id("workspace-view-outline").click();
      await id("workspace-outline").waitFor({ state: "visible", timeout: 15000 });
      check(await id("component-content").inputValue() === text, "Server-handled view changes must work without losing the unsaved buffer after storage failure.");
      await unchanged(baseline, "Typing and changing views during browser storage failure must not change the real service.");
    });

    await step("retrying failed draft storage cannot send a service write", async () => {
      const beforeFailures = (await faultState()).failures;
      await id("workspace-retry-draft").click();
      await failureRendered(beforeFailures);
      check(await id("component-content").inputValue() === text && await id("component-save").isDisabled(),
        "A failed explicit browser storage retry must keep the full buffer and keep Save paused.");
      await unchanged(baseline, "Save blocked by storage failure and retry-draft failure must leave revision, document, and actual activity unchanged.");
    });

    await step("restored storage and explicit retry save the original buffer once", async () => {
      await failStorage(false);
      await id("workspace-retry-draft").click();
      await id("workspace-storage-error").waitFor({ state: "hidden", timeout: 15000 });
      check(await id("component-content").inputValue() === text && await id("component-save").isEnabled(),
        "Restoring storage must enable an explicit save without retyping or altering the draft.");
      await unchanged(baseline, "Retrying draft storage after restoration must not itself publish component changes.");
      await id("component-save").click();
      await saved(text, baseline.current.revision + 1);
      await id("workspace-pending-command").waitFor({ state: "hidden", timeout: 15000 });
      const after = await serviceState();
      check(after.changes.total === baseline.changes.total + 1, "Recovering one UI save must append exactly one workspace activity.");
      operationIds.push(await replayReceipt((await faultState()).lastCommand));
    });

    const beforePending = await serviceState();
    await step("a pasted pending save and retry must persist the same envelope before sending", async () => {
      await fillOrPasteText(id("component-content"), secondText);
      await waitState(({ prefix, key, componentId, text }) => {
        const stored = sessionStorage.getItem(prefix + key);
        return stored !== null && JSON.parse(stored).contentDrafts?.[componentId]?.content === text;
      }, { prefix: storagePrefix, key, componentId, text: secondText });
      await waitState(() => document.querySelector('[data-testid="component-save"]')?.disabled === false);
      await page.evaluate(() => { const state = window.__kanbanflowWorkspaceStorageFault.state; state.lastCommand = null; state.commands = []; });
      await failStorage(true);
      let failures = (await faultState()).failures;
      await id("component-save").click();
      failures = await failureRendered(failures);
      await id("workspace-pending-command").waitFor({ state: "visible", timeout: 15000 });
      const pending = (await faultState()).lastCommand;
      check(pending?.payload.content === secondText && pending.expectedRevision === beforePending.current.revision,
        "A failed save must retain its actual full command and expected revision for recovery.");
      await unchanged(beforePending, "Failure to persist the initial pending envelope must send no mutation to the real service.");
      await id("workspace-retry-pending").click();
      await failureRendered(failures);
      const captured = await faultState();
      check(captured.commands.length >= 2 && captured.commands.every(command => JSON.stringify(command) === JSON.stringify(pending)),
        "Every attempted pending retry must retain the identical operation ID, actor, revision, action and full payload.");
      check(await id("component-content").inputValue() === secondText, "Pending save/retry failures must retain the full editor buffer.");
      await unchanged(beforePending, "Retry-pending while storage fails must not change revision, document, activity or service content.");
    });

    await step("restoring storage recovers the exact pending operation and durable receipt", async () => {
      const pending = (await faultState()).lastCommand;
      await failStorage(false);
      await id("workspace-retry-pending").click();
      await saved(secondText, beforePending.current.revision + 1);
      await id("workspace-pending-command").waitFor({ state: "hidden", timeout: 15000 });
      await id("workspace-storage-error").waitFor({ state: "hidden", timeout: 15000 });
      const after = await serviceState();
      check(after.changes.total === beforePending.changes.total + 1, "Recovering the pending operation must append exactly one workspace activity.");
      check(await id("component-content").inputValue() === secondText, "Successful pending recovery must retain the complete saved text in the editor.");
      operationIds.push(await replayReceipt(pending));
      check(errors.length === 0, `Browser storage failures must not terminate the circuit with uncaught errors: ${errors.join(" | ")}`);
    });

    const screenshot = `_sandbox/579/${prefix}-recovered.png`;
    await page.screenshot({ path: screenshot, fullPage: true });
    return { passed: true, prefix, checks, workspaceId: key, componentId, contentCharacters: secondText.length,
      storageFailureKind: "controlled sessionStorage.setItem QuotaExceededError for workspace draft prefix only",
      failedStorageAttempts: (await faultState()).failures, recoveredOperationIds: operationIds,
      receiptReplaysVerified: operationIds.length, longPasteInputs, screenshot };
  } catch (error) {
    await page.screenshot({ path: `_sandbox/579/${prefix}-failure.png`, fullPage: true }).catch(() => {});
    throw new Error(`${stage}: ${error.message}`);
  } finally {
    await page.evaluate(() => {
      window.__kanbanflowWorkspaceStorageFault?.restore();
      delete window.__kanbanflowWorkspaceStorageFault;
    }).catch(() => {});
    page.off("pageerror", onError);
    if (originalViewport) await page.setViewportSize(originalViewport);
  }
}
