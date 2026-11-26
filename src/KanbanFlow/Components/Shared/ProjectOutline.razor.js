export async function revealSelection(items, request, focus, scroll) {
    // Let the rendered mobile outline's visibility/inert state settle before focusing.
    await new Promise(requestAnimationFrame);
    if (!items?.isConnected || items.dataset.revealRequest !== String(request)) return;
    const row = items.querySelector('.outline-item.is-selected');
    if (!row) return;
    const view = items.ownerDocument.defaultView;
    const target = row.getBoundingClientRect();
    if (view.matchMedia('(max-width: 991.98px)').matches) {
        const bounds = items.getBoundingClientRect();
        items.scrollTo({ top: items.scrollTop + target.top - bounds.top - (items.clientHeight - target.height) / 2,
            behavior: 'instant' });
    } else if (focus || scroll) {
        const header = items.parentElement.querySelector('.project-outline-header');
        const visibleTop = Math.max(0, header?.getBoundingClientRect().bottom ?? 0);
        if (target.top < visibleTop || target.bottom > view.innerHeight) {
            const top = Math.max(visibleTop, (view.innerHeight - target.height) / 2);
            view.scrollBy({ top: target.top - top, behavior: 'instant' });
        }
    }
    if (focus && !items.closest('[inert]')) {
        row.querySelector('button[aria-current="true"]')?.focus({ preventScroll: true });
    }
}
