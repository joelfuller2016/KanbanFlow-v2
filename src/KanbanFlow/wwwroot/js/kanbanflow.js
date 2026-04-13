window.KanbanFlow = {
    _boardSortables: [],
    _navSortables: [],

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
    }
};
