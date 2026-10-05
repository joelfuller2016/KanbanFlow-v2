using KanbanFlow.Models;
using KanbanFlow.Data;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace KanbanFlow.Tests;

/// <summary>Real SQLite schema, a new non-pooled connection per context, and repository-local scratch files.</summary>
internal sealed class WorkflowSqliteTestFactory : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    public const string Actor = "workspace-test-author";
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 6, 14, 0, 0, TimeSpan.Zero));
    public WorkspaceService Service { get; }
    public string ScratchDirectory { get; } = CreateScratchDirectory();
    public string ConnectionString { get; }

    public WorkflowSqliteTestFactory(params IInterceptor[] interceptors)
    {
        ConnectionString = $"Data Source={Path.Combine(ScratchDirectory, "workspace.db")};Pooling=False;Foreign Keys=True;Default Timeout=15";
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString);
        using (var db = new AppDbContext(builder.Options)) db.Database.EnsureCreated();
        if (interceptors.Length != 0) builder.AddInterceptors(interceptors);
        _options = builder.Options;
        Service = new WorkspaceService(this, Clock);
    }

    public AppDbContext CreateDbContext() => new(_options);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    public static string Key() => Guid.NewGuid().ToString("N");
    public static BriefDraft Brief(string outcome = "Preserve request intent") => new(outcome, "Manual workflow", "Local only", "Verified results remain attached");
    public static TaskDraft Draft(string title = "Implement request", params Guid[] dependencies) =>
        new(Guid.NewGuid(), title, "Detailed specification", "First check\nSecond check", null, "", TaskPriority.High, "developer", dependencies);

    public async Task<WorkRequest> BriefedAsync()
    {
        var projectId = await Service.CreateProjectAsync("Workspace tests", Actor, Key());
        var captured = await Service.CaptureAsync(new CaptureRequest("Request", "Original request", projectId), Actor, Key());
        await Service.SaveBriefAsync(captured.RequestId, captured.Version, Brief(), Actor, Key());
        return await LoadAsync(captured.RequestId);
    }

    public async Task<WorkRequest> PlannedAsync(params TaskDraft[] drafts)
    {
        var request = await BriefedAsync();
        await Service.SavePlanAsync(request.Id, request.Version, drafts, Actor, Key());
        return await LoadAsync(request.Id);
    }

    public async Task<WorkRequest> PublishedAsync(params TaskDraft[] drafts)
    {
        var request = await PlannedAsync(drafts);
        await Service.PublishPlanAsync(request.Id, request.Version, request.Plans.Single().Id, Actor, Key());
        return await LoadAsync(request.Id);
    }

    public async Task<WorkRequest> LoadAsync(Guid id) => await Service.GetAsync(id) ?? throw new InvalidOperationException("Expected persisted request.");

    public async Task<IReadOnlyDictionary<int, string>> ReadTaskTokensAsync(Guid requestId) =>
        (await Service.ContextAsync(requestId))!.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken);

    public async Task<WorkRequest> ReportAsync(WorkRequest request, Guid itemId)
    {
        await Service.RecordEvidenceAsync(request.Id, request.Version, itemId,
            new EvidenceDraft("Implemented requested behavior", "Both checks passed", "src/Workflow.cs at abc123"), Actor, Key());
        return await LoadAsync(request.Id);
    }

    public async Task<WorkRequest> AcceptAsync(WorkRequest request, Guid itemId)
    {
        var item = request.WorkItems.Single(w => w.Id == itemId);
        var context = (await Service.ContextAsync(request.Id))!;
        var evidence = context.Tasks.Single(t => t.Id == itemId).Evidence.Single(e => e.Current && e.AcceptedAt is null);
        await Service.AcceptEvidenceAsync(request.Id, request.Version, item.Id, evidence.Id,
            item.Task.AcceptanceCriteria.Select(c => c.Id).ToArray(), "reviewer", Key());
        return await LoadAsync(request.Id);
    }

    public static async Task<WorkflowException> RejectAsync(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<WorkflowException>(action);
        Assert.Equal(code, error.Code);
        return error;
    }

    public static string CreateScratchDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "KanbanFlow.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Cannot locate owning repository for test scratch data.");
        var path = Path.Combine(repository.FullName, "_sandbox", "workflow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(ScratchDirectory)) Directory.Delete(ScratchDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
