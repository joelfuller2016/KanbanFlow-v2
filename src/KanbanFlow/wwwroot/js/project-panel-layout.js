const keyFor = projectId => `kanbanflow:project-panel-layout:v1:${projectId}`;
const previewProperty = "--project-panel-resize-preview";

function preference(value, defaultDock) {
    return {
        dock: value?.dock === "left" || value?.dock === "right" ? value.dock : defaultDock,
        width: Number.isInteger(value?.width) && value.width > 0 && value.width <= 2147483647
            ? value.width : null,
        collapsed: value?.collapsed === true,
    };
}

export function load(projectId) {
    try {
        const stored = JSON.parse(localStorage.getItem(keyFor(projectId)));
        if (!stored || typeof stored !== "object") return null;
        return {
            outline: preference(stored.outline, "left"),
            details: preference(stored.details, "right"),
        };
    } catch {
        return null;
    }
}

export function save(projectId, layout) {
    try { localStorage.setItem(keyFor(projectId), JSON.stringify(layout)); }
    catch { /* The in-memory layout still works when browser storage is unavailable. */ }
}

export function reset(projectId) {
    try { localStorage.removeItem(keyFor(projectId)); }
    catch { /* The in-memory layout still resets. */ }
}

export function attachResizeHandle(handle, dotNetRef) {
    const pane = handle.closest("[data-project-panel]");
    const view = handle.ownerDocument.defaultView;
    const listeners = new AbortController();
    let drag;
    let preview;
    let revision = 0;

    function sizes() {
        const width = Math.round(pane.getBoundingClientRect().width);
        const css = view.getComputedStyle(pane);
        const min = Math.ceil(Number.parseFloat(css.minWidth)) || width;
        const max = Math.round(Number.parseFloat(css.maxWidth)) || width;
        const panel = pane.dataset.projectPanel;
        const stored = Number.parseFloat(css.getPropertyValue(`--project-${panel}-width`));
        // Match the responsive default in TaskDetailPanel.razor.css when no width is set.
        const defaultWidth = panel === "details" ? Math.min(view.innerWidth / 2, view.innerWidth - 640) : width;
        const preferred = Number.isFinite(stored) ? stored : defaultWidth;
        return { width, min, max: Math.max(min, max), preferred };
    }

    function updateAria() {
        if (!handle.getClientRects().length) return;
        const { width, min, max } = sizes();
        handle.setAttribute("aria-valuenow", String(width));
        handle.setAttribute("aria-valuemin", String(min));
        handle.setAttribute("aria-valuemax", String(max));
    }

    function showPreview(width) {
        preview = width;
        pane.style.setProperty(previewProperty, `${width}px`);
        updateAria();
    }

    function clearPreview() {
        preview = undefined;
        pane.style.removeProperty(previewProperty);
        updateAria();
    }

    async function commit(width) {
        const request = ++revision;
        showPreview(width);
        try {
            await dotNetRef.invokeMethodAsync("CommitWidthAsync", width);
        } catch {
            // The component can be removed while a resize is being saved.
        } finally {
            if (request === revision && !drag) clearPreview();
        }
    }

    function clamp(width, { min, max }) {
        return Math.max(min, Math.min(max, Math.round(width)));
    }

    function requestedWidth(bounds) {
        // Flexbox can compress a dock to preserve board space. Adjust the requested
        // width in that case so increasing the size never shrinks the visible pane.
        return preview ?? clamp(bounds.preferred, bounds);
    }

    function direction() {
        return pane.dataset.dock === "right" ? -1 : 1;
    }

    function onKeyDown(event) {
        if (drag || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        const bounds = sizes();
        const width = requestedWidth(bounds);
        const proposed = {
            ArrowLeft: width - 16 * direction(),
            ArrowRight: width + 16 * direction(),
            Home: bounds.min,
            End: bounds.max,
        }[event.key];
        const next = clamp(proposed, bounds);
        if (next !== width) void commit(next);
    }

    function onPointerDown(event) {
        if (event.button !== 0 || drag) return;
        event.preventDefault();
        handle.focus({ preventScroll: true });
        handle.setPointerCapture(event.pointerId);
        drag = { id: event.pointerId, x: event.clientX, width: requestedWidth(sizes()), direction: direction() };
    }

    function draggedWidth(event) {
        return clamp(drag.width + (event.clientX - drag.x) * drag.direction, sizes());
    }

    function onPointerMove(event) {
        if (event.pointerId !== drag?.id) return;
        showPreview(draggedWidth(event));
    }

    function onPointerUp(event) {
        if (event.pointerId !== drag?.id) return;
        const width = draggedWidth(event);
        const changed = width !== drag.width;
        drag = undefined;
        if (changed) void commit(width);
        else clearPreview();
    }

    function onPointerCancel(event) {
        if (event.pointerId !== drag?.id) return;
        drag = undefined;
        clearPreview();
    }

    function onViewportResize() {
        if (drag && !handle.getClientRects().length) {
            drag = undefined;
            clearPreview();
        } else {
            updateAria();
        }
    }

    const options = { signal: listeners.signal };
    handle.addEventListener("keydown", onKeyDown, options);
    handle.addEventListener("pointerdown", onPointerDown, options);
    handle.addEventListener("pointermove", onPointerMove, options);
    handle.addEventListener("pointerup", onPointerUp, options);
    handle.addEventListener("pointercancel", onPointerCancel, options);
    handle.addEventListener("lostpointercapture", onPointerCancel, options);
    view.addEventListener("resize", onViewportResize, options);
    const observer = new view.ResizeObserver(updateAria);
    observer.observe(pane);
    updateAria();

    return {
        dispose() {
            ++revision;
            listeners.abort();
            observer.disconnect();
            clearPreview();
        },
    };
}

export function watchTaskDetails(panel, dotNetRef) {
    const view = panel.ownerDocument.defaultView;
    const narrow = view.matchMedia("(max-width: 991.98px)");
    let modal;

    function dismiss() {
        if (modal === undefined) return;
        modal = undefined;
        panel.removeAttribute("aria-modal");
        view.KanbanFlow.destroyDialog(panel);
    }

    function update() {
        if (!panel.classList.contains("show")) {
            dismiss();
            return;
        }

        const nextModal = narrow.matches || panel.dataset.size === "full";
        if (nextModal === modal) return;
        const active = panel.ownerDocument.activeElement;
        const preserveFocus = modal !== undefined && panel.contains(active) && active.getClientRects().length > 0;
        modal = nextModal;
        if (modal) panel.setAttribute("aria-modal", "true");
        else panel.removeAttribute("aria-modal");
        view.KanbanFlow.initDialog(panel, dotNetRef, {
            focusContainer: true,
            documentKeydown: modal,
            trapFocus: modal,
            preserveFocus,
        });
    }

    const observer = new view.MutationObserver(update);
    observer.observe(panel, { attributes: true, attributeFilter: ["class", "data-size"] });
    narrow.addEventListener("change", update);
    update();

    return {
        dismiss,
        dispose() {
            observer.disconnect();
            narrow.removeEventListener("change", update);
            dismiss();
        },
    };
}
