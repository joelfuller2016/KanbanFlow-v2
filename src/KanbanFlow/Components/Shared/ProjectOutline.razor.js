export async function revealSelection(items, request, focus) {
    // Let the rendered mobile outline's visibility/inert state settle before focusing.
    await new Promise(requestAnimationFrame);
    if (!items?.isConnected || items.dataset.revealRequest !== String(request)) return;
    const row = items.querySelector('.outline-item.is-selected');
    if (!row) return;
    const bounds = items.getBoundingClientRect();
    const target = row.getBoundingClientRect();
    // Scroll this container only. scrollIntoView can also move the board and page.
    items.scrollTo({ top: items.scrollTop + target.top - bounds.top - (items.clientHeight - target.height) / 2,
        behavior: 'instant' });
    if (focus && !items.closest('[inert]')) {
        row.querySelector('button[aria-current="true"]')?.focus({ preventScroll: true });
    }
}
