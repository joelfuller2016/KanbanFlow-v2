// One delegated handler survives Blazor enhanced navigation without retaining a circuit or DOM node.
// Keep href as a real fallback; focus directly so filters and task deep links never change.
document.addEventListener('click', (event) => {
    const link = event.target.closest?.('a[data-project-skip-target]');
    if (!link || event.defaultPrevented || event.button !== 0 ||
        event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;

    const targetId = link.dataset.projectSkipTarget;
    if (targetId !== 'board-filter-search' && targetId !== 'kanban-board-heading') return;
    const target = document.getElementById(targetId) || document.getElementById('project-content');
    if (!target) return;
    event.preventDefault();

    // Never move behind an open modal (including a task loaded from a deep link).
    const modal = [...document.querySelectorAll('[aria-modal="true"]')]
        .find((element) => element.getClientRects().length > 0 &&
            getComputedStyle(element).visibility === 'visible');
    if (modal && !modal.contains(target)) return;
    target.focus();
}, true);
