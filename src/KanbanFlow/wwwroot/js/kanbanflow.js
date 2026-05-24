window.KanbanFlow = {
    _boardSortables: [],
    _navSortables: [],
    _outlineMediaQuery: null,
    _outlineMediaQueryHandler: null,
    _outlineObserver: null,

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
                    var taskId = parseInt(evt.item.dataset.taskId);
                    var newStatus = evt.to.dataset.status;
                    var newIndex = evt.newIndex;
                    dotNetRef.invokeMethodAsync('OnCardMoved', taskId, newStatus, newIndex);
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
                onEnd: function (evt) {
                    var taskId = parseInt(evt.item.dataset.taskId);
                    var newFeatureId = parseInt(evt.to.dataset.featureId);
                    var newIndex = evt.newIndex;
                    dotNetRef.invokeMethodAsync('OnTaskReordered', taskId, newFeatureId, newIndex);
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
        return Array.prototype.slice.call(dialog.querySelectorAll(
            'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
        )).filter(function (el) { return el.offsetParent !== null; });
    },

    initDialog: function (dialog, dotNetRef) {
        if (!dialog) {
            return;
        }
        // Remember the opener so focus can be restored when the dialog closes.
        dialog._kfOpener = document.activeElement;

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
                    dotNetRef.invokeMethodAsync('CloseFromJs');
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
