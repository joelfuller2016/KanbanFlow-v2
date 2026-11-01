import test from "node:test";
import assert from "node:assert/strict";
import { randomUUID, webcrypto } from "node:crypto";
import { readFileSync } from "node:fs";
import { Script, createContext } from "node:vm";

// Execute the actual collocated browser module in independent document globals. Only browser
// storage, document lifetime and the origin-wide native lock boundary are modeled here.
const moduleUrl = new URL("../src/KanbanFlow/Components/Pages/IdeaWorkspace.razor.js", import.meta.url);
const moduleSource = readFileSync(moduleUrl, "utf8").replace(/^export /gm, "")
  + "\nglobalThis.workspaceModule = { initialize, readDraft, readStoredDraft, preserveAndClearDraft, saveDraft, disposeWriter };";
const moduleScript = new Script(moduleSource, { filename: moduleUrl.pathname });
const CLIENT_KEY = "kanbanflow.workspace.client.v1";
const SESSION_KEY = "kanbanflow.workspace.session.v1";
const DRAFT_KEY = "kanbanflow.workspace.draft.v1:idea:11";
const flush = () => new Promise(resolve => setImmediate(resolve));

class StorageBoundary {
  constructor(values = []) { this.values = new Map(values); }
  getItem(key) { return this.values.get(key) ?? null; }
  setItem(key, value) {
    if (key === this.rejectKey || this.rejectBackups && key.includes(":backup:")) throw new Error("session storage quota boundary");
    this.values.set(key, String(value));
  }
  removeItem(key) { this.values.delete(key); }
  copy() { return new StorageBoundary(this.values); }
}

// A shared lock manager with exclusive FIFO grants, document-held callback promises and native
// AbortSignal behavior. It contains no workspace identity/draft decisions.
class OriginLocks {
  held = new Map();
  pending = new Map();
  requests = 0;
  request(name, { signal }, callback) {
    this.requests++;
    return new Promise((resolve, reject) => {
      const entry = { callback, resolve, reject, signal };
      entry.abort = () => {
        this.pending.set(name, (this.pending.get(name) ?? []).filter(item => item !== entry));
        const error = new Error("The lock request was aborted.");
        error.name = "AbortError";
        reject(error);
      };
      if (signal.aborted) return entry.abort();
      signal.addEventListener("abort", entry.abort, { once: true });
      this.pending.set(name, [...(this.pending.get(name) ?? []), entry]);
      this.grant(name);
    });
  }
  grant(name) {
    if (this.held.has(name)) return;
    const entry = this.pending.get(name)?.shift();
    if (!entry) return;
    this.held.set(name, entry);
    entry.signal.removeEventListener("abort", entry.abort);
    Promise.resolve().then(() => entry.callback({ name, mode: "exclusive" })).then(
      value => { this.held.delete(name); entry.resolve(value); this.grant(name); },
      error => { this.held.delete(name); entry.reject(error); this.grant(name); });
  }
  get pendingCount() { return [...this.pending.values()].reduce((count, queue) => count + queue.length, 0); }
}

function browserOrigin(t) {
  const origin = { localStorage: new StorageBoundary([[CLIENT_KEY, "shared-browser-client"]]), locks: new OriginLocks(), documents: [] };
  t.after(async () => { for (const document of origin.documents) await document.close(); });
  return origin;
}

function documentFor(origin, sessionStorage = new StorageBoundary(), crypto = { randomUUID }) {
  const events = new Map();
  const navigator = { locks: origin.locks };
  const context = createContext({
    localStorage: origin.localStorage, sessionStorage, navigator, crypto,
    AbortController, setTimeout, clearTimeout,
    window: { addEventListener: (name, callback) => events.set(name, [...(events.get(name) ?? []), callback]) }
  });
  moduleScript.runInContext(context);
  const document = {
    module: context.workspaceModule, navigator, sessionStorage,
    async hide(persisted) { for (const listener of events.get("pagehide") ?? []) listener({ persisted }); await flush(); },
    async close() { await this.hide(false); }
  };
  origin.documents.push(document);
  return document;
}

function completeDraft() {
  const text = "\r\n  ## Complete local draft Ω 漢字 🧭  \r\n\r\n```text\r\n    keep final spaces  \r\n```\r\n".repeat(650);
  assert.ok(text.length > 40_000);
  return { contentDrafts: { "card-a": { content: text, dirty: true, baseRevision: 8 } },
    pending: { command: { operationId: "immutable-operation", expectedRevision: 8, payload: { content: text } } } };
}

test("copied sessionStorage in two live documents splits session identity and preserves complete drafts and pending operations", async t => {
  const origin = browserOrigin(t);
  const original = documentFor(origin);
  const first = await original.module.initialize("idea:11");
  const draft = completeDraft();
  original.module.saveDraft("idea:11", draft, "original-writer", 1);
  const duplicate = documentFor(origin, original.sessionStorage.copy());
  const second = await duplicate.module.initialize("idea:11");

  assert.equal(first.clientId, second.clientId);
  assert.notEqual(first.sessionId, second.sessionId);
  assert.equal(original.sessionStorage.getItem(SESSION_KEY), first.sessionId);
  assert.equal(duplicate.sessionStorage.getItem(SESSION_KEY), second.sessionId);
  assert.equal(JSON.stringify(second.draft), JSON.stringify(draft));
  assert.equal((await original.module.initialize("idea:11")).sessionId, first.sessionId);
  assert.equal(origin.locks.held.size, 2);
});

test("simultaneous documents with copied storage and no opener reserve distinct live sessions", async t => {
  const origin = browserOrigin(t);
  const copied = new StorageBoundary([[SESSION_KEY, "copied-unowned-session"]]);
  const first = documentFor(origin, copied.copy());
  const second = documentFor(origin, copied.copy());
  const identities = await Promise.all([first.module.initialize("idea:11"), second.module.initialize("idea:11")]);

  assert.notEqual(identities[0].sessionId, identities[1].sessionId);
  assert.equal(identities.filter(identity => identity.sessionId === "copied-unowned-session").length, 1);
  assert.equal(origin.locks.held.size, 2);
  assert.equal(origin.locks.pendingCount, 0);
});

test("same-document initialization and writer disposal retain identity across workspace navigation", async t => {
  const origin = browserOrigin(t);
  const document = documentFor(origin);
  const [first, parallel] = await Promise.all([document.module.initialize("idea:11"), document.module.initialize("idea:12")]);
  assert.equal(first.sessionId, parallel.sessionId);
  document.module.disposeWriter("disposed-component");
  assert.equal((await document.module.initialize("idea:11")).sessionId, first.sessionId);
  assert.equal(origin.locks.requests, 1);
  assert.equal(origin.locks.held.size, 1);
});

test("ordinary reload releases the outgoing document lease and restores the same identity and full buffer", async t => {
  const origin = browserOrigin(t);
  const outgoing = documentFor(origin);
  const first = await outgoing.module.initialize("idea:11");
  const draft = completeDraft();
  outgoing.module.saveDraft("idea:11", draft, "writer", 1);
  const reloaded = documentFor(origin, outgoing.sessionStorage);
  // Start while the old document is still closing; the native request must wait briefly for it.
  const restoring = reloaded.module.initialize("idea:11");
  await flush();
  assert.equal(origin.locks.pendingCount, 1);
  await outgoing.close();
  const restored = await restoring;

  assert.equal(restored.sessionId, first.sessionId);
  assert.equal(restored.clientId, first.clientId);
  assert.equal(JSON.stringify(restored.draft), JSON.stringify(draft));
  assert.equal(origin.locks.held.size, 1);
  assert.equal(origin.locks.pendingCount, 0);
});

test("a cached live document retains its session lease until its final pagehide", async t => {
  const origin = browserOrigin(t);
  const cached = documentFor(origin);
  const first = await cached.module.initialize("idea:11");
  await cached.hide(true);
  const duplicate = documentFor(origin, cached.sessionStorage.copy());
  const second = await duplicate.module.initialize("idea:11");
  assert.notEqual(first.sessionId, second.sessionId);
  assert.equal((await cached.module.initialize("idea:11")).sessionId, first.sessionId);
  await cached.close();
  assert.equal(origin.locks.held.size, 1);
});

test("without native locks copied documents and reload use distinct document sessions while preserving complete drafts", async t => {
  const origin = browserOrigin(t);
  const stored = new StorageBoundary([[SESSION_KEY, "retained-session"], [DRAFT_KEY, JSON.stringify(completeDraft())]]);
  const first = documentFor(origin, stored);
  first.navigator.locks = undefined;
  const a = await first.module.initialize("idea:11");
  assert.notEqual(a.sessionId, "retained-session");
  assert.equal((await first.module.initialize("idea:12")).sessionId, a.sessionId);
  const copied = documentFor(origin, stored.copy());
  copied.navigator.locks = undefined;
  const b = await copied.module.initialize("idea:11");
  const reload = documentFor(origin, stored);
  reload.navigator.locks = undefined;
  const c = await reload.module.initialize("idea:11");
  assert.equal(new Set([a.sessionId, b.sessionId, c.sessionId]).size, 3);
  for (const identity of [a, b, c]) assert.equal(JSON.stringify(identity.draft), stored.getItem(DRAFT_KEY));
  assert.equal(origin.locks.requests, 0);
});

test("non-secure-context UUID fallback creates valid independent version-four IDs from native random bytes", async t => {
  const origin = browserOrigin(t);
  origin.localStorage = new StorageBoundary();
  const document = documentFor(origin, new StorageBoundary(), { getRandomValues: values => webcrypto.getRandomValues(values) });
  document.navigator.locks = undefined;
  const identity = await document.module.initialize("idea:11");
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  assert.match(identity.clientId, uuid);
  assert.match(identity.sessionId, uuid);
  assert.notEqual(identity.clientId, identity.sessionId);
});

test("a document closed while waiting releases a later granted lease without changing its copied identity", async t => {
  const origin = browserOrigin(t);
  const owner = documentFor(origin);
  const identity = await owner.module.initialize("idea:11");
  const waiting = documentFor(origin, owner.sessionStorage.copy());
  const restoring = waiting.module.initialize("idea:11");
  const rejected = assert.rejects(restoring, /document has closed/);
  await flush();
  await waiting.close();
  await owner.close();
  await rejected;
  await flush();
  assert.equal(waiting.sessionStorage.getItem(SESSION_KEY), identity.sessionId);
  assert.equal(origin.locks.held.size, 0);
  assert.equal(origin.locks.pendingCount, 0);
});

for (const raw of ['{invalid JSON including complete pending text', JSON.stringify({ contentDrafts: null, pending: { payload: completeDraft() } })]) {
  test(`explicit recovery preserves and verifies the entire ${raw.startsWith('{invalid') ? 'malformed' : 'incompatible'} draft before clearing its active key`, async t => {
    const origin = browserOrigin(t);
    const stored = new StorageBoundary([[DRAFT_KEY, raw]]);
    const document = documentFor(origin, stored);
    assert.equal(document.module.readStoredDraft("idea:11"), raw);
    assert.equal(stored.getItem(DRAFT_KEY), raw);
    const result = document.module.preserveAndClearDraft("idea:11");
    assert.equal(result.raw, raw);
    assert.equal(stored.getItem(result.backupKey), raw);
    assert.equal(stored.getItem(DRAFT_KEY), null);
    assert.equal((await document.module.initialize("idea:11")).draft, null);
  });
}

test("backup storage failure retains the sole authored draft and pending envelope byte-for-byte", t => {
  const origin = browserOrigin(t);
  const raw = JSON.stringify(completeDraft());
  const stored = new StorageBoundary([[DRAFT_KEY, raw]]);
  stored.rejectBackups = true;
  const document = documentFor(origin, stored);
  assert.throws(() => document.module.preserveAndClearDraft("idea:11"), /quota boundary/);
  assert.equal(stored.getItem(DRAFT_KEY), raw);
  assert.equal(stored.values.size, 1);
});

test("native lock denial fails explicitly and leaves stored session and complete drafts recoverable", async t => {
  const origin = browserOrigin(t);
  const stored = new StorageBoundary([[SESSION_KEY, "retained-session"], [DRAFT_KEY, JSON.stringify(completeDraft())]]);
  const document = documentFor(origin, stored);
  document.navigator.locks = { request: () => Promise.reject(new Error("native session lease denied")) };
  const before = [...stored.values];
  await assert.rejects(document.module.initialize("idea:11"), /native session lease denied/);
  assert.deepEqual([...stored.values], before);
  document.navigator.locks = origin.locks;
  assert.equal((await document.module.initialize("idea:11")).sessionId, "retained-session");
});

test("sessionStorage write failure releases the new lease and preserves drafts for explicit retry", async t => {
  const origin = browserOrigin(t);
  const stored = new StorageBoundary([[SESSION_KEY, "retained-session"], [DRAFT_KEY, JSON.stringify(completeDraft())]]);
  const document = documentFor(origin, stored);
  stored.rejectKey = SESSION_KEY;
  await assert.rejects(document.module.initialize("idea:11"), /session storage quota boundary/);
  await flush();
  assert.equal(origin.locks.held.size, 0);
  assert.equal(stored.getItem(SESSION_KEY), "retained-session");
  stored.rejectKey = null;
  const restored = await document.module.initialize("idea:11");
  assert.equal(restored.sessionId, "retained-session");
  assert.equal(JSON.stringify(restored.draft), stored.getItem(DRAFT_KEY));
});
