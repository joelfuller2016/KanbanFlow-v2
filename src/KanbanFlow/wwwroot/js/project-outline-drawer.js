export function watchProjectOutlineDrawer(outline, dotNetRef) {
    const doc = outline.ownerDocument;
    const view = doc.defaultView;
    const shell = outline.closest(".project-shell");
    const main = shell.querySelector(":scope > .project-main");
    const toggle = main.querySelector('[data-testid="toggle-project-outline-button"]');
    const narrow = view.matchMedia("(max-width: 991.98px)");
    let active = false;
    let madeMainInert = false;

    function otherModalOpen() {
        return Array.from(doc.querySelectorAll('[aria-modal="true"]')).some(element =>
            element !== outline && element.getClientRects().length > 0 &&
            view.getComputedStyle(element).visibility === "visible");
    }

    function open() {
        active = true;
        // The shell's outline accessibility watcher also removes these attributes on open.
        // Clear them here so autofocus works regardless of observer registration order.
        outline.inert = false;
        outline.removeAttribute("aria-hidden");
        outline.setAttribute("role", "dialog");
        outline.setAttribute("aria-modal", "true");
        outline.setAttribute("aria-label", "Project outline");

        // Keep keydown scoped to this drawer. A separate Edit or Add modal manages its own trap.
        view.KanbanFlow.initDialog(outline, dotNetRef, {
            preserveFocus: outline.contains(doc.activeElement) || otherModalOpen(),
        });
        madeMainInert = !main.inert;
        if (madeMainInert) main.inert = true;
    }

    function close(restoreFocus) {
        if (!active) return;
        active = false;
        if (madeMainInert) main.inert = false;
        outline.removeAttribute("role");
        outline.removeAttribute("aria-modal");
        outline.removeAttribute("aria-label");
        view.KanbanFlow.destroyDialog(outline);
        if (restoreFocus && !otherModalOpen()) toggle?.focus({ preventScroll: true });
    }

    function update() {
        const shouldBeOpen = narrow.matches && shell.classList.contains("outline-open");
        if (shouldBeOpen && !active) open();
        else if (!shouldBeOpen) close(narrow.matches);
    }

    const observer = new view.MutationObserver(update);
    observer.observe(shell, { attributes: true, attributeFilter: ["class"] });
    narrow.addEventListener("change", update);
    update();

    return {
        dispose() {
            observer.disconnect();
            narrow.removeEventListener("change", update);
            close(false);
        },
    };
}
