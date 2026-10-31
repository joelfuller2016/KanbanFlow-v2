const CLIENT_KEY = 'kanbanflow.workspace.client.v1';
const SESSION_KEY = 'kanbanflow.workspace.session.v1';
const DRAFT_PREFIX = 'kanbanflow.workspace.draft.v1:';
const writeVersions = new Map();

export function initialize(workspaceId) {
    let clientId = localStorage.getItem(CLIENT_KEY);
    if (!clientId) { clientId = crypto.randomUUID(); localStorage.setItem(CLIENT_KEY, clientId); }
    let sessionId = sessionStorage.getItem(SESSION_KEY);
    if (!sessionId) { sessionId = crypto.randomUUID(); sessionStorage.setItem(SESSION_KEY, sessionId); }
    return { clientId, sessionId, draft: readDraft(workspaceId) };
}

export function readDraft(workspaceId) {
    const value = sessionStorage.getItem(DRAFT_PREFIX + workspaceId);
    return value === null ? null : JSON.parse(value);
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
