export async function revealSelection(items, request, focus) {
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
    } else if (focus && (target.top < 0 || target.bottom > view.innerHeight)) {
        // The desktop outline follows the page. Scroll for an explicit focus request only.
        row.scrollIntoView({ block: 'start', inline: 'nearest', behavior: 'instant' });
    }
    if (focus && !items.closest('[inert]')) {
        row.querySelector('button[aria-current="true"]')?.focus({ preventScroll: true });
    }
}
