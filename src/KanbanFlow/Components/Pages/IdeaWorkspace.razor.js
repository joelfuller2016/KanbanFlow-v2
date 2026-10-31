const CLIENT_KEY = 'kanbanflow.workspace.client.v1';
const SESSION_KEY = 'kanbanflow.workspace.session.v1';
const DRAFT_PREFIX = 'kanbanflow.workspace.draft.v1:';
const SESSION_LEASE_PREFIX = 'kanbanflow.workspace.session.lease.v1:';
const SESSION_LEASE_WAIT_MS = 250;
const writeVersions = new Map();
let identityPromise, releaseSessionLease;
let documentEnded = false;

function newId() {
    if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// The lease belongs to the document, including enhanced navigation and new Blazor circuits.
// Keep it for a cached document too; a resumed page must still own its original session.
window.addEventListener('pagehide', event => {
    if (event.persisted) return;
    documentEnded = true;
    releaseSessionLease?.();
    releaseSessionLease = null;
    writeVersions.clear();
});

function reserveSession(sessionId) {
    return new Promise((resolve, reject) => {
        const controller = new AbortController();
        // Briefly allow an outgoing document to release the session during an ordinary reload.
        const timer = setTimeout(() => controller.abort(), SESSION_LEASE_WAIT_MS);
        Promise.resolve().then(() => navigator.locks.request(SESSION_LEASE_PREFIX + sessionId,
            { signal: controller.signal }, () => {
                clearTimeout(timer);
                return new Promise(release => resolve(release));
            })).catch(error => {
                clearTimeout(timer);
                if (controller.signal.aborted && error.name === 'AbortError') resolve(null);
                else reject(error);
            });
    });
}

async function initializeIdentity() {
    if (documentEnded) throw new Error('The workspace document has closed.');
    let clientId = localStorage.getItem(CLIENT_KEY);
    if (!clientId) { clientId = newId(); localStorage.setItem(CLIENT_KEY, clientId); }
    if (!navigator.locks?.request) {
        // Plain HTTP LAN origins have no Web Locks. Give each document a fresh session rather
        // than trusting a copied stored ID. Full drafts and all durable checkpoints survive;
        // a reload starts another checkpoint session, while navigation in this document reuses it.
        const sessionId = newId();
        sessionStorage.setItem(SESSION_KEY, sessionId);
        return { clientId, sessionId };
    }
    let sessionId = sessionStorage.getItem(SESSION_KEY);
    for (let attempt = 0; attempt < 3; attempt++) {
        sessionId ||= newId();
        const release = await reserveSession(sessionId);
        if (documentEnded) { release?.(); throw new Error('The workspace document has closed.'); }
        if (release) {
            try { sessionStorage.setItem(SESSION_KEY, sessionId); }
            catch (error) { release(); throw error; }
            releaseSessionLease = release;
            return { clientId, sessionId };
        }
        // sessionStorage can be copied by window.open or Duplicate tab, even without an opener.
        sessionId = newId();
    }
    throw new Error('A unique workspace tab session could not be reserved. Retry browser draft storage.');
}

export async function initialize(workspaceId) {
    identityPromise ??= initializeIdentity().catch(error => { identityPromise = null; throw error; });
    const identity = await identityPromise;
    return { ...identity, draft: readDraft(workspaceId) };
}

export function readDraft(workspaceId) {
    const value = readStoredDraft(workspaceId);
    return value === null ? null : JSON.parse(value);
}

export function readStoredDraft(workspaceId) {
    return sessionStorage.getItem(DRAFT_PREFIX + workspaceId);
}

// Only an explicit recovery action calls this. Never overwrite the sole copy of an authored
// draft or uncertain pending operation, even when that copy is malformed or incompatible.
export function preserveAndClearDraft(workspaceId) {
    const key = DRAFT_PREFIX + workspaceId;
    const raw = sessionStorage.getItem(key);
    if (raw === null) return { backupKey: null, raw: null };
    const backupKey = DRAFT_PREFIX + 'backup:' + workspaceId + ':' + newId();
    sessionStorage.setItem(backupKey, raw);
    if (sessionStorage.getItem(backupKey) !== raw)
        throw new Error('The complete browser draft backup could not be verified. The active draft was retained.');
    if (sessionStorage.getItem(key) !== raw)
        throw new Error('The browser draft changed during recovery. The active draft was retained.');
    sessionStorage.removeItem(key);
    return { backupKey, raw };
}

export function saveDraft(workspaceId, draft, writerId, version) {
    const key = writerId + ':' + workspaceId;
    if ((writeVersions.get(key) ?? -1) > version) return;
    sessionStorage.setItem(DRAFT_PREFIX + workspaceId, JSON.stringify(draft));
    writeVersions.set(key, version);
}

export function disposeWriter(writerId) {
    for (const key of writeVersions.keys()) if (key.startsWith(writerId + ':')) writeVersions.delete(key);
}
