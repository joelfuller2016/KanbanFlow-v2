using System.Reflection;
using KanbanFlow.Components.Pages;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceTaskInspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectingAnotherTask_ClearsPreviousDetailsBeforeTheRead_AndNeverShowsThemAfterFailure(bool failRead)
    {
        using var database = new SqliteTestFactory();
        var project = new Project { Name = "Inspection project" };
        var feature = new Feature { Name = "Current feature", Project = project };
        var second = new TaskItem { Name = "Task B", Description = "  Full task B Ω\r\n" + new string('文', 10_000), Feature = feature };
        await using (var db = database.CreateDbContext()) { db.Tasks.Add(second); await db.SaveChangesAsync(); }
        var service = new IdeaWorkspaceService(database);
        var context = await service.GetAsync($"project:{project.Id}");
        var held = new HeldFactory(database, failRead);
        await using var page = new IdeaWorkspace();
        Set(page, "DbFactory", held); Set(page, "context", context); Set(page, "workspaceId", context.WorkspaceId); Set(page, "generation", 3);
        var old = new IdeaWorkspaceTaskReference(999, "Task A", "Old details must disappear", "Done", "Low", null, feature.Id, project.Id, default);
        Set(page, "inspectedTask", old); Set(page, "inspectedTaskId", old.Id);
        var running = (Task)Invoke(page, "InspectPublishedTask", new WorkspaceUiScope(context.WorkspaceId, 3), second.Id)!;
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
            Assert.Equal(second.Id, Field<int?>(page, "inspectedTaskId"));
            Assert.True(Field<bool>(page, "taskBusy"));
            held.Release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Field<bool>(page, "taskBusy"));
            if (failRead)
            {
                Assert.Null(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
                Assert.Contains("controlled read failed", Field<string>(page, "taskError"));
            }
            else
            {
                var shown = Assert.IsType<IdeaWorkspaceTaskReference>(Field<IdeaWorkspaceTaskReference?>(page, "inspectedTask"));
                Assert.Equal(second.Id, shown.Id); Assert.Equal(second.Name, shown.Name); Assert.Equal(second.Description, shown.Description);
                Assert.Null(Field<string?>(page, "taskError"));
            }
        }
        finally { held.Release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Set(object target, string name, object value)
    { var property = target.GetType().GetProperty(name, Flags); if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, Flags)!.SetValue(target, value); }
    private sealed class HeldFactory(SqliteTestFactory inner, bool failRead) : IDbContextFactory<AppDbContext>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AppDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            if (failRead) throw new IOException("controlled read failed");
            return inner.CreateDbContext();
        }
    }
}
