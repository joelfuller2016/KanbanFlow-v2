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
    }
};
