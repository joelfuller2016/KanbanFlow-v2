const APPLY_LAYOUT = 'ApplyCanvasLayout';
const controllers = new WeakMap();

class CanvasController {
    #root;
    #callback;
    #abort = new AbortController();
    #gesture;
    #pending = false;

    constructor(root, callback) {
        this.#root = root;
        this.#callback = callback;
        const options = { signal: this.#abort.signal };
        root.addEventListener('pointerdown', event => this.#start(event), options);
        root.addEventListener('pointermove', event => this.#move(event), options);
        root.addEventListener('pointerup', event => this.#finish(event), options);
        root.addEventListener('pointercancel', event => { if (event.pointerId === this.#gesture?.pointerId) this.#reset(); }, options);
        root.addEventListener('keydown', event => this.#key(event), options);
    }

    #available() { return !this.#pending && !this.#gesture && this.#root.dataset.busy !== 'true'; }
    #start(event) {
        const handle = event.target.closest('.workspace-drag-handle, .workspace-resize-handle');
        const card = handle?.closest('[data-placement-id]');
        if (!card || event.button !== 0 || !this.#available()) return;
        const allPlacements = [...this.#root.querySelectorAll('[data-placement-id]')];
        const parentMap = new Map(allPlacements.map(node => [node.dataset.placementId, node.dataset.parentId]));
        const descendants = allPlacements.filter(candidate => {
            const seen = new Set();
            let parent = candidate.dataset.parentId;
            while (parent && !seen.has(parent)) {
                if (parent === card.dataset.placementId) return true;
                seen.add(parent);
                parent = parentMap.get(parent);
            }
            return false;
        });
        this.#gesture = { card, handle, descendants, pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY,
            x: Number(card.dataset.localX), y: Number(card.dataset.localY), width: Number(card.dataset.width), height: Number(card.dataset.height),
            resize: handle.classList.contains('workspace-resize-handle'), workspaceId: this.#root.dataset.workspaceId, revision: Number(this.#root.dataset.revision), dx: 0, dy: 0 };
        handle.setPointerCapture(event.pointerId);
        event.preventDefault();
        handle.focus?.({ preventScroll: true });
    }
    #move(event) {
        const gesture = this.#gesture;
        if (!gesture || event.pointerId !== gesture.pointerId) return;
        gesture.dx = event.clientX - gesture.clientX;
        gesture.dy = event.clientY - gesture.clientY;
        if (gesture.resize) {
            gesture.card.style.setProperty('--preview-width', `${Math.max(1, gesture.width + gesture.dx)}px`);
            gesture.card.style.setProperty('--preview-height', `${Math.max(1, gesture.height + gesture.dy)}px`);
        } else {
            for (const node of [gesture.card, ...gesture.descendants]) {
                node.style.setProperty('--preview-dx', `${gesture.dx}px`);
                node.style.setProperty('--preview-dy', `${gesture.dy}px`);
            }
        }
    }
    async #finish(event) {
        const gesture = this.#gesture;
        if (!gesture || event.pointerId !== gesture.pointerId) return;
        const changed = Math.abs(gesture.dx) + Math.abs(gesture.dy) >= 2;
        const update = gesture.resize
            ? { placementId: gesture.card.dataset.placementId, width: Math.max(1, gesture.width + gesture.dx), height: Math.max(1, gesture.height + gesture.dy) }
            : { placementId: gesture.card.dataset.placementId, x: gesture.x + gesture.dx, y: gesture.y + gesture.dy };
        this.#reset();
        if (changed) await this.#commit(update, gesture.revision, gesture.workspaceId);
    }
    async #key(event) {
        if (!event.target.closest('.workspace-drag-handle') || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        if (!this.#available()) return;
        const card = event.target.closest('[data-placement-id]');
        const dx = event.key === 'ArrowLeft' ? -10 : event.key === 'ArrowRight' ? 10 : 0;
        const dy = event.key === 'ArrowUp' ? -10 : event.key === 'ArrowDown' ? 10 : 0;
        const update = event.shiftKey
            ? { placementId: card.dataset.placementId, width: Math.max(1, Number(card.dataset.width) + dx), height: Math.max(1, Number(card.dataset.height) + dy) }
            : { placementId: card.dataset.placementId, x: Number(card.dataset.localX) + dx, y: Number(card.dataset.localY) + dy };
        await this.#commit(update, Number(this.#root.dataset.revision), this.#root.dataset.workspaceId);
    }
    async #commit(update, revision, workspaceId) {
        this.#pending = true;
        try {
            await this.#callback.invokeMethodAsync(APPLY_LAYOUT, workspaceId, [update], revision);
            const error = this.#root.closest('section')?.querySelector('[data-testid="canvas-input-error"]');
            if (error) {
                error.hidden = true;
                error.textContent = '';
            }
        }
        catch {
            const error = this.#root.closest('section')?.querySelector('[data-testid="canvas-input-error"]');
            if (error) {
                error.textContent = 'The layout could not be sent. The canvas returned to its saved position. Reconnect and retry the move or use the layout fields.';
                error.hidden = false;
            }
        }
        finally { this.#pending = false; }
    }
    #reset() {
        const gesture = this.#gesture;
        if (!gesture) return;
        for (const node of [gesture.card, ...gesture.descendants]) {
            for (const property of ['--preview-dx', '--preview-dy', '--preview-width', '--preview-height']) node.style.removeProperty(property);
        }
        this.#gesture = undefined;
    }
    dispose() { this.#reset(); this.#abort.abort(); }
}

export function initialize(root, callback) {
    controllers.get(root)?.dispose();
    controllers.set(root, new CanvasController(root, callback));
    root.dataset.ready = 'true';
}
export function dispose(root) { controllers.get(root)?.dispose(); controllers.delete(root); }
