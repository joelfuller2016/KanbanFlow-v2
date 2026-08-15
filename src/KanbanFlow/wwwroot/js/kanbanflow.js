window.KanbanFlow = {
    _boardSortables: [],
    _navSortables: [],
    _outlineMediaQuery: null,
    _outlineMediaQueryHandler: null,
    _outlineObserver: null,

    // ── SortableJS ↔ Blazor identity contract (#307) ──
    // Board containers are `.board-column-cards[data-status]`; outline containers are
    // `.feature-tasks[data-feature-id]`; every draggable root owns a positive `data-task-id`.
    // Both .NET callbacks keep their three-argument shapes, but argument 3 is the id of the next
    // TASK after the drop (`0` = append), never Sortable's rendered-child index. A container may
    // render a filtered subset, and the outline also renders non-task subtask siblings, so a DOM
    // position can never be treated as a database position.

    _nextTaskId: function (item) {
        var sibling = item.nextElementSibling;
        while (sibling) {
            var candidate = Number(sibling.dataset && sibling.dataset.taskId);
            if (Number.isInteger(candidate) && candidate > 0) {
                return candidate;
            }
            sibling = sibling.nextElementSibling;
        }
        return 0;
    },

    updateProjectOutlineAccessibility: function () {
        var shell = document.querySelector('.project-shell');
        var outline = document.getElementById('project-outline');
        if (!shell || !outline) {
            return;
        }

        var isMobile = window.matchMedia('(max-width: 991.98px)').matches;
        var isOpen = shell.classList.contains('outline-open');
        var shouldHideFromA11y = isMobile && !isOpen;

        if (shouldHideFromA11y) {
            outline.setAttribute('aria-hidden', 'true');
            outline.setAttribute('inert', '');
        } else {
            outline.removeAttribute('aria-hidden');
            outline.removeAttribute('inert');
        }
    },

    initProjectOutlineAccessibility: function () {
        this.updateProjectOutlineAccessibility();

        if (!this._outlineMediaQuery) {
            this._outlineMediaQuery = window.matchMedia('(max-width: 991.98px)');
            this._outlineMediaQueryHandler = this.updateProjectOutlineAccessibility.bind(this);

            if (this._outlineMediaQuery.addEventListener) {
                this._outlineMediaQuery.addEventListener('change', this._outlineMediaQueryHandler);
            } else if (this._outlineMediaQuery.addListener) {
                this._outlineMediaQuery.addListener(this._outlineMediaQueryHandler);
            }
        }

        if (!this._outlineObserver) {
            var shell = document.querySelector('.project-shell');
            if (shell) {
                this._outlineObserver = new MutationObserver(this.updateProjectOutlineAccessibility.bind(this));
                this._outlineObserver.observe(shell, { attributes: true, attributeFilter: ['class'] });
            }
        }
    },

    // ── #294: hand the DOM back to Blazor before telling it anything ──
    //
    // SortableJS physically relocates the dragged node the instant you let go — but every node on
    // these boards is BLAZOR's. Blazor's renderer keeps its own logical-children array per
    // container and never re-reads the DOM, so the moment Sortable moves a card, Blazor's model of
    // the page and the page itself disagree. Its next re-render then patches by that stale model
    // and lands the edits on the wrong nodes: KanbanColumn draws its cards as
    // `@foreach (var item in Items) { @CardTemplate(item) }`, which compiles to one same-sequence
    // REGION per card, so the diff pairs cards POSITIONALLY — card N+1's content is written into
    // the component instance that owns card N's node, and that node is now in another column. Drag
    // one card and a second, untouched card visually teleports after it, while the database stays
    // perfectly correct and a refresh heals the display. Nothing throws. Every server-side test
    // stays green. (Reproduced on BOTH boards — the task board only looked immune because a column
    // with one card is the self-healing case.)
    //
    // So put the node back exactly where Blazor last rendered it, and let the re-render from server
    // truth perform the move. Blazor is the single source of truth and its re-render IS the
    // snap-back — which is also the only way an in-column drop can snap back to a DERIVED order
    // (an Idea has no SortOrder; its column is ordered `Priority desc, CreatedAt desc`, an order
    // the server cannot adopt from a drop). Same approach as vuedraggable/react-sortablejs.
    //
    // Remove the node FIRST, then read `from.children[oldIndex]`: with it out of the way that index
    // is exactly the sibling it originally sat in front of (`undefined` => append at the end). Doing
    // it in that order is what makes a same-container reorder correct too — there the node is still
    // among `from.children` and would skew the index.
    _revertDraggedNode: function (evt) {
        var item = evt.item;
        if (item.parentNode) {
            item.parentNode.removeChild(item);
        }
        evt.from.insertBefore(item, evt.from.children[evt.oldIndex] || null);
    },

    initBoardDragDrop: function (dotNetRef) {
        // Destroy any existing board sortables first
        this._boardSortables.forEach(function (s) { s.destroy(); });
        this._boardSortables = [];

        document.querySelectorAll('.board-column-cards').forEach(function (column) {
            var sortable = new Sortable(column, {
                group: 'board',
                animation: 150,
                ghostClass: 'kanban-ghost',
                dragClass: 'shadow-lg',
                chosenClass: 'kanban-chosen',
                fallbackOnBody: true,
                swapThreshold: 0.65,
                onEnd: function (evt) {
                    // Read the drop BEFORE undoing it — the revert moves the node, so `evt.to` is
                    // the only record of where the user actually dropped it.
                    var taskId = Number(evt.item.dataset.taskId);
                    var newStatus = evt.to.dataset.status;
                    var beforeTaskId = window.KanbanFlow._nextTaskId(evt.item);
                    window.KanbanFlow._revertDraggedNode(evt);
                    if (!Number.isInteger(taskId) || taskId <= 0 || !newStatus) {
                        return;
                    }
                    dotNetRef.invokeMethodAsync('OnCardMoved', taskId, newStatus, beforeTaskId)
                        .catch(function () { /* component/circuit disposed */ });
                }
            });
            this._boardSortables.push(sortable);
        }.bind(this));
    },

    initLeftNavDragDrop: function (dotNetRef) {
        // Destroy any existing nav sortables first
        this._navSortables.forEach(function (s) { s.destroy(); });
        this._navSortables = [];

        document.querySelectorAll('.feature-tasks').forEach(function (container) {
            var sortable = new Sortable(container, {
                group: 'tasks',
                animation: 150,
                ghostClass: 'kanban-ghost',
                chosenClass: 'kanban-chosen',
                fallbackOnBody: true,
                // `.feature-tasks` also contains expanded subtask siblings. Only task roots
                // participate in this Sortable; _nextTaskId likewise skips non-task siblings.
                draggable: '[data-task-id]',
                onEnd: function (evt) {
                    // Same contract as the board (#294): the outline's task lists are Blazor's DOM
                    // too, and OnTaskReordered re-renders them from the server. Hand the node back
                    // before invoking .NET.
                    var taskId = Number(evt.item.dataset.taskId);
                    var newFeatureId = Number(evt.to.dataset.featureId);
                    var beforeTaskId = window.KanbanFlow._nextTaskId(evt.item);
                    window.KanbanFlow._revertDraggedNode(evt);
                    if (!Number.isInteger(taskId) || taskId <= 0
                        || !Number.isInteger(newFeatureId) || newFeatureId <= 0) {
                        return;
                    }
                    dotNetRef.invokeMethodAsync('OnTaskReordered', taskId, newFeatureId, beforeTaskId)
                        .catch(function () { /* component/circuit disposed */ });
                }
            });
            this._navSortables.push(sortable);
        }.bind(this));
    },

    destroySortables: function () {
        this._boardSortables.forEach(function (s) { s.destroy(); });
        this._navSortables.forEach(function (s) { s.destroy(); });
        this._boardSortables = [];
        this._navSortables = [];
    },

    initTaskDetailDialog: function (panel) {
        if (!panel) {
            return;
        }

        if (panel._kanbanFlowTaskDetailKeydown) {
            panel.removeEventListener('keydown', panel._kanbanFlowTaskDetailKeydown);
        }

        panel._kanbanFlowTaskDetailKeydown = function (event) {
            if (event.key !== 'Escape') {
                return;
            }

            var target = event.target;
            var isTextEntry = target && (
                target.isContentEditable ||
                target.matches('input, textarea, select')
            );

            if (isTextEntry) {
                return;
            }

            var closeButton = panel.querySelector('[data-testid="task-detail-close-button"]');
            if (closeButton) {
                event.preventDefault();
                closeButton.click();
            }
        };

        panel.addEventListener('keydown', panel._kanbanFlowTaskDetailKeydown);
        panel.focus({ preventScroll: true });
    },

    focusTaskCard: function (taskId) {
        var selector = '[data-testid="board-card-' + taskId + '-title"]';
        var taskButton = document.querySelector(selector);
        if (taskButton) {
            taskButton.focus({ preventScroll: true });
            return;
        }

        var board = document.querySelector('[data-testid="kanban-board"], .kanban-board, .board-column-cards');
        if (board) {
            if (!board.hasAttribute('tabindex')) {
                board.setAttribute('tabindex', '-1');
            }
            board.focus({ preventScroll: true });
        }
    },

    destroyProjectOutlineAccessibility: function () {
        if (this._outlineObserver) {
            this._outlineObserver.disconnect();
            this._outlineObserver = null;
        }

        if (this._outlineMediaQuery && this._outlineMediaQueryHandler) {
            if (this._outlineMediaQuery.removeEventListener) {
                this._outlineMediaQuery.removeEventListener('change', this._outlineMediaQueryHandler);
            } else if (this._outlineMediaQuery.removeListener) {
                this._outlineMediaQuery.removeListener(this._outlineMediaQueryHandler);
            }
        }

        this._outlineMediaQuery = null;
        this._outlineMediaQueryHandler = null;
    },

    // ── Accessible modal dialogs (#166): focus-trap + Esc + focus restore ──
    _dialogFocusables: function (dialog) {
        // Visible-and-focusable filter. Use getClientRects() rather than offsetParent:
        // offsetParent is null for position:fixed elements (the .modal is fixed), which would
        // wrongly exclude them. getClientRects() is empty only when the element isn't rendered
        // (e.g. display:none while the dialog is closed).
        return Array.prototype.slice.call(dialog.querySelectorAll(
            'a[href]:not([tabindex="-1"]), button:not([disabled]):not([tabindex="-1"]), input:not([disabled]):not([tabindex="-1"]), select:not([disabled]):not([tabindex="-1"]), textarea:not([disabled]):not([tabindex="-1"]), [tabindex]:not([tabindex="-1"])'
        )).filter(function (el) { return el.getClientRects().length > 0; });
    },

    initDialog: function (dialog, dotNetRef) {
        if (!dialog) {
            return;
        }
        // Remember the opener so focus can be restored when the dialog closes. Capture only
        // once per open (cleared in destroyDialog) so a re-init can't overwrite it with an
        // element inside the dialog.
        if (!dialog._kfOpener) {
            dialog._kfOpener = document.activeElement;
        }

        var preferred = dialog.querySelector('[data-dialog-autofocus]');
        var focusables = this._dialogFocusables(dialog);
        (preferred || focusables[0] || dialog).focus({ preventScroll: true });

        if (dialog._kfKeydown) {
            dialog.removeEventListener('keydown', dialog._kfKeydown);
        }
        dialog._kfKeydown = function (event) {
            if (event.key === 'Escape') {
                event.preventDefault();
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('CloseFromJs').catch(function () { /* component/circuit disposed */ });
                }
                return;
            }
            if (event.key !== 'Tab') {
                return;
            }
            // Trap Tab within the dialog (wrap at the first/last focusable).
            var f = window.KanbanFlow._dialogFocusables(dialog);
            if (f.length === 0) {
                event.preventDefault();
                return;
            }
            var first = f[0];
            var last = f[f.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        };
        dialog.addEventListener('keydown', dialog._kfKeydown);
    },

    destroyDialog: function (dialog) {
        if (!dialog) {
            return;
        }
        if (dialog._kfKeydown) {
            dialog.removeEventListener('keydown', dialog._kfKeydown);
            dialog._kfKeydown = null;
        }
        var opener = dialog._kfOpener;
        dialog._kfOpener = null;
        if (opener && typeof opener.focus === 'function') {
            opener.focus({ preventScroll: true });
        }
    }
};
