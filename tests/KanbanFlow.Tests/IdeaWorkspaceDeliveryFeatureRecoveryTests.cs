using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using KanbanFlow.Components.Workspaces;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services.IdeaWorkspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceDeliveryFeatureRecoveryTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task MissingOriginalPublicationFeature_StillLoadsChoices_AndPublishesAnExpansionIntoTheSelectedExistingFeature()
    {
        using var factory = new SqliteTestFactory();
        var service = new IdeaWorkspaceService(factory);
        var project = new Project { Name = "Delivery recovery" }; var other = new Project { Name = "Other project" };
        await using (var db = factory.CreateDbContext()) { db.Projects.AddRange(project, other); await db.SaveChangesAsync(); }
        var created = await service.CreateAsync(new("capture", "test", Title: "Delivery idea"));
        var key = created.WorkspaceId;
        await Change(service, key, "link_project", new { projectId = project.Id });
        await ProposeAndAccept(service, key, new[] { new { id = "first", title = "First task", description = "Original complete task", acceptanceCriteria = new[] { "Verified" }, dependsOn = Array.Empty<string>(), provenance = (object?)null } });
        await Change(service, key, "publish_plan", new { expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision, featureName = "Original feature" });
        var publication = Assert.Single((await service.GetAsync($"project:{project.Id}")).Document.Publications);
        var recovery = new Feature { ProjectId = project.Id, Name = "Existing recovery feature" };
        var foreign = new Feature { ProjectId = other.Id, Name = "Foreign feature" };
        await using (var db = factory.CreateDbContext())
        {
            db.Features.AddRange(recovery, foreign); await db.SaveChangesAsync();
            var task = await db.Tasks.SingleAsync(value => value.Id == publication.TaskId);
            task.FeatureId = recovery.Id; task.Name = "Human-edited moved task"; await db.SaveChangesAsync();
            db.Features.Remove(await db.Features.SingleAsync(value => value.Id == publication.FeatureId)); await db.SaveChangesAsync();
        }
        var accepted = (await service.GetAsync(key)).AcceptedPlan!.Items.Single();
        await ProposeAndAccept(service, key, new[] {
            new { id = accepted.Id, title = accepted.Title, description = accepted.Description, acceptanceCriteria = accepted.AcceptanceCriteria.ToArray(), dependsOn = accepted.DependsOn.ToArray(), provenance = (object?)accepted.Provenance },
            new { id = "next", title = "Next task", description = "A complete new expansion", acceptanceCriteria = new[] { "New work verified" }, dependsOn = Array.Empty<string>(), provenance = (object?)null } });
        var context = await service.GetAsync(key);
        var missing = await Assert.ThrowsAsync<IdeaWorkspaceException>(() => service.PreviewPublicationAsync(key, new(context.Revision, context.ProjectPlanRevision!.Value)));
        Assert.Equal("publication_target_missing", missing.Code);
        using var panel = Panel(factory, context);
        WorkspaceDeliveryDraft? changed = null; IdeaWorkspacePublicationPreviewRequest? request = null;
        Set(panel, "OnDraftChanged", EventCallback.Factory.Create<WorkspaceDeliveryDraft>(this, value => changed = value));
        Set(panel, "OnPreview", EventCallback.Factory.Create<IdeaWorkspacePublicationPreviewRequest>(this, value => request = value));

        await Call(panel, "OnParametersSetAsync");

        Assert.Equal([recovery.Id], Features(panel).Select(value => value.Id));
        Assert.Null(Field<string?>(panel, "featuresError"));
        Assert.Null(panel.Preview);
        await Call(panel, "FeatureChanged", new ChangeEventArgs { Value = recovery.Id.ToString() });
        Assert.Equal(recovery.Id, changed!.FeatureId);
        await Call(panel, "PreviewSelection");
        var preview = await service.PreviewPublicationAsync(key, request!);
        Assert.Equal(recovery.Id, preview.FeatureId);
        Assert.Equal(1, preview.NewTaskCount); Assert.Equal(1, preview.ExistingTaskCount);
        await Change(service, key, "publish_plan", new { expectedProjectPlanRevision = request!.ExpectedProjectPlanRevision, itemIds = request.ItemIds, featureId = request.FeatureId });
        await using var saved = factory.CreateDbContext();
        Assert.Equal(2, await saved.Tasks.CountAsync());
        Assert.Equal(2, await saved.Tasks.CountAsync(value => value.FeatureId == recovery.Id));
        Assert.Equal("Human-edited moved task", (await saved.Tasks.SingleAsync(value => value.Id == publication.TaskId)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeatureRead_FromAnOlderProject_CannotReplaceCurrentChoicesOrReportAnObsoleteFailure(bool failOldRead)
    {
        using var factory = new SqliteTestFactory();
        var (a, b, first, second) = await SeedChoices(factory);
        var held = new HeldFactory(factory) { HoldNext = true };
        using var panel = Panel(held, Context(a.Id));
        var reading = Call(panel, "OnParametersSetAsync");
        try
        {
            await held.Entered.Task.WaitAsync(Deadline);
            Set(panel, "Context", Context(b.Id)); Set(panel, "Draft", new WorkspaceDeliveryDraft { ProjectName = "Retain B project", FeatureName = "Retain B feature" });
            Invoke(panel, "OnParametersSet"); await Call(panel, "OnParametersSetAsync");
            Assert.Equal([second.Id], Features(panel).Select(value => value.Id));
            if (failOldRead) held.Failure = new IOException("Obsolete feature read failed.");
            held.Release.TrySetResult(); await reading.WaitAsync(Deadline);

            Assert.Equal([second.Id], Features(panel).Select(value => value.Id));
            Assert.Null(Field<string?>(panel, "featuresError"));
            Assert.Equal("Retain B feature", Editing(panel).FeatureName);
            await Call(panel, "FeatureChanged", new ChangeEventArgs { Value = first.Id.ToString() });
            Assert.Null(Editing(panel).FeatureId);
        }
        finally { held.Release.TrySetResult(); await reading.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task FailedIndependentFeatureLoad_CanReloadWithoutAPreview_AndRetainsAuthoredChoices()
    {
        using var factory = new SqliteTestFactory();
        var (a, _, first, _) = await SeedChoices(factory);
        foreach (var failure in new Exception[] { new IOException("Feature read failed."), new OperationCanceledException("Unrelated cancellation.") })
        {
            var held = new HeldFactory(factory) { FailNext = true, Failure = failure };
            using var panel = Panel(held, Context(a.Id));
            var original = Editing(panel);

            await Call(panel, "OnParametersSetAsync");
            Assert.Empty(Features(panel));
            Assert.Equal("Project feature choices could not be loaded. Reload the choices to retry.", Field<string?>(panel, "featuresError"));
            await Call(panel, "ReloadFeatures");

            Assert.Equal([first.Id], Features(panel).Select(value => value.Id));
            Assert.Null(Field<string?>(panel, "featuresError"));
            Assert.Same(original, Editing(panel)); Assert.Null(panel.Preview);
        }
    }

    [Fact]
    public async Task DisposingDuringFeatureRead_CancelsFactoryAndQuery_AndRejectsLateChoicesAndErrors()
    {
        using var factory = new SqliteTestFactory();
        var (a, _, _, _) = await SeedChoices(factory);
        foreach (var stage in new[] { "factory", "query", "late failure" })
        {
            var query = stage == "query" ? new HeldFeatureQuery() : null;
            var held = new HeldFactory(factory) { HoldNext = query is null, IgnoreCancellation = stage == "late failure", QueryInterceptor = query };
            var panel = Panel(held, Context(a.Id));
            var reading = Call(panel, "OnParametersSetAsync");
            try
            {
                await (query?.Entered.Task ?? held.Entered.Task).WaitAsync(Deadline);
                var cancellationToken = held.CreationToken;
                Assert.True(cancellationToken.CanBeCanceled);
                if (query is not null) Assert.Equal(cancellationToken, query.Token);
                panel.Dispose(); panel.Dispose();
                Assert.True(cancellationToken.IsCancellationRequested);
                if (stage == "late failure")
                {
                    held.Failure = new IOException("Disposed read failed."); held.Release.TrySetResult();
                }
                await reading.WaitAsync(Deadline);
                Assert.Empty(Features(panel)); Assert.Null(Field<string?>(panel, "featuresError"));
            }
            finally
            {
                held.Release.TrySetResult(); query?.Release.TrySetResult();
                await reading.WaitAsync(Deadline); panel.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingTwice_CancelsTheActualFactoryOrQueryRead_WithoutPublishingAnError(bool holdQuery)
    {
        using var factory = new SqliteTestFactory();
        var (project, _, _, _) = await SeedChoices(factory);
        var heldFactory = new HeldFactory(factory) { HoldNext = !holdQuery };
        var heldQuery = new HeldFeatureQuery();
        IDbContextFactory<AppDbContext> reader = holdQuery ? new QueryFactory(factory, heldQuery) : heldFactory;
        var panel = Panel(reader, Context(project.Id));
        var reading = Call(panel, "OnParametersSetAsync");
        try
        {
            await (holdQuery ? heldQuery.Entered.Task : heldFactory.Entered.Task).WaitAsync(Deadline);
            var token = holdQuery ? heldQuery.ReadToken : heldFactory.ReadToken;
            Assert.True(token.CanBeCanceled);
            panel.Dispose(); panel.Dispose();
            await reading.WaitAsync(Deadline);
            Assert.True(token.IsCancellationRequested);
            Assert.Empty(Features(panel)); Assert.Null(Field<string?>(panel, "featuresError"));
            await Call(panel, "ReloadFeatures");
            await Call(panel, "OnParametersSetAsync");
        }
        finally
        {
            heldFactory.Release.TrySetResult(); heldQuery.Release.TrySetResult();
            await reading.WaitAsync(Deadline); panel.Dispose();
        }
    }

    [Fact]
    public async Task CancellationFromAnotherOwner_IsReportedAsARetryableFeatureReadFailure()
    {
        using var factory = new SqliteTestFactory();
        var (project, _, _, _) = await SeedChoices(factory);
        var held = new HeldFactory(factory) { HoldNext = true, Failure = new OperationCanceledException(new CancellationToken(true)) };
        using var panel = Panel(held, Context(project.Id));
        held.Release.TrySetResult(); await Call(panel, "OnParametersSetAsync");
        Assert.NotNull(Field<string?>(panel, "featuresError"));
        Assert.Empty(Features(panel));
        await Call(panel, "ReloadFeatures");
        Assert.Single(Features(panel)); Assert.Null(Field<string?>(panel, "featuresError"));
    }

    [Fact]
    public async Task ReloadedFeatureChoices_SurviveParentRerender_WithTheSameSuccessfulPreview()
    {
        using var factory = new SqliteTestFactory();
        var (project, _, first, _) = await SeedChoices(factory);
        var context = Context(project.Id);
        using var panel = Panel(factory, context);
        var preview = Preview(context, first);
        Set(panel, "Preview", preview); Invoke(panel, "OnParametersSet");
        var original = Editing(panel);
        var added = new Feature { ProjectId = project.Id, Name = "Added after the preview" };
        await using (var db = factory.CreateDbContext()) { db.Features.Add(added); await db.SaveChangesAsync(); }

        await Call(panel, "ReloadFeatures");
        Assert.Contains(Features(panel), feature => feature.Id == added.Id);
        Invoke(panel, "OnParametersSet");
        await Call(panel, "OnParametersSetAsync");

        Assert.Equal(2, Features(panel).Count);
        Assert.Contains(Features(panel), feature => feature.Id == added.Id);
        Assert.Same(preview, panel.Preview);
        Assert.Same(original, Editing(panel));
    }

    [Fact]
    public async Task ParentRerender_DuringFeatureReload_DoesNotInvalidateItOrReapplyTheSamePreview()
    {
        using var factory = new SqliteTestFactory();
        var (project, _, first, _) = await SeedChoices(factory);
        var context = Context(project.Id);
        var held = new HeldFactory(factory) { HoldNext = true };
        using var panel = Panel(held, context);
        var preview = Preview(context, first);
        Set(panel, "Preview", preview); Invoke(panel, "OnParametersSet");
        var added = new Feature { ProjectId = project.Id, Name = "Added after the preview" };
        await using (var db = factory.CreateDbContext()) { db.Features.Add(added); await db.SaveChangesAsync(); }
        var reading = Call(panel, "ReloadFeatures");
        Task? parameterUpdate = null;
        try
        {
            await held.Entered.Task.WaitAsync(Deadline);
            Invoke(panel, "OnParametersSet");
            parameterUpdate = Call(panel, "OnParametersSetAsync");
            Assert.True(Field<bool>(panel, "featuresBusy"));
            held.Release.TrySetResult();
            await Task.WhenAll(reading, parameterUpdate).WaitAsync(Deadline);

            Assert.Equal(2, Features(panel).Count);
            Assert.Contains(Features(panel), feature => feature.Id == added.Id);
            Assert.False(Field<bool>(panel, "featuresBusy"));
            Assert.Null(Field<string?>(panel, "featuresError"));
            Assert.Same(preview, panel.Preview);
        }
        finally
        {
            held.Release.TrySetResult();
            await reading.WaitAsync(Deadline);
            if (parameterUpdate is not null) await parameterUpdate.WaitAsync(Deadline);
        }
    }

    private static IdeaWorkspacePublicationPreview Preview(IdeaWorkspaceContext context, Feature feature)
        => new(context.WorkspaceId, context.Revision, context.ProjectId!.Value, context.ProjectPlanRevision!.Value,
            "accepted-plan", "Existing plan", feature.Id, feature.Name, [new(feature.Id, feature.Name)], [], 0, 0);

    private WorkspaceDeliveryPanel Panel(IDbContextFactory<AppDbContext> factory, IdeaWorkspaceContext context)
    {
        var panel = new WorkspaceDeliveryPanel(); Set(panel, "DbFactory", factory); Set(panel, "Context", context);
        Set(panel, "Draft", new WorkspaceDeliveryDraft { ProjectName = "Authored project", FeatureName = "" }); Invoke(panel, "OnParametersSet"); return panel;
    }
    private static async Task<(Project A, Project B, Feature First, Feature Second)> SeedChoices(SqliteTestFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var a = new Project { Name = "A" }; var b = new Project { Name = "B" }; db.Projects.AddRange(a, b); await db.SaveChangesAsync();
        var first = new Feature { ProjectId = a.Id, Name = "A feature" }; var second = new Feature { ProjectId = b.Id, Name = "B feature" }; db.Features.AddRange(first, second); await db.SaveChangesAsync();
        return (a, b, first, second);
    }
    private static async Task ProposeAndAccept(IdeaWorkspaceService service, string key, object items)
    {
        await Change(service, key, "propose_plan", new { title = "Recovery plan", expectedProjectPlanRevision = (await service.GetAsync(key)).ProjectPlanRevision, items });
        var current = await service.GetAsync(key);
        await Change(service, key, "accept_plan", new { planRevisionId = current.Document.PlanRevisions.Last().Id, expectedProjectPlanRevision = current.ProjectPlanRevision });
    }
    private static async Task Change(IdeaWorkspaceService service, string key, string action, object payload)
        => await service.ExecuteAsync(key, new(Guid.NewGuid().ToString("N"), (await service.GetAsync(key)).Revision, action, JsonSerializer.SerializeToElement(payload, Json), "test"));
    private static IdeaWorkspaceContext Context(int projectId)
    {
        var source = new IdeaWorkspaceSource("idea", 1, "Idea", "Source", null, null, null, null, null, projectId, default, default);
        return new("idea:1", "idea", 1, source.Title, 12, source, source, false, projectId, false, "/ideas/workspace/idea/1", null, null, default, 0, new()) { ProjectPlanRevision = 7 };
    }
    private static IReadOnlyList<IdeaWorkspaceFeatureOption> Features(WorkspaceDeliveryPanel panel) => Field<IReadOnlyList<IdeaWorkspaceFeatureOption>>(panel, "features");
    private static WorkspaceDeliveryDraft Editing(WorkspaceDeliveryPanel panel) => (WorkspaceDeliveryDraft)panel.GetType().GetProperty("editing", Flags)!.GetValue(panel)!;
    private static Task Call(object target, string name, params object?[] args) => (Task)Invoke(target, name, args)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags)!.SetValue(target, value);
    private sealed class HeldFactory(SqliteTestFactory inner) : IDbContextFactory<AppDbContext>
    {
        public bool HoldNext { get; set; }
        public bool FailNext { get; set; }
        public bool IgnoreCancellation { get; set; }
        public DbCommandInterceptor? QueryInterceptor { get; set; }
        public CancellationToken CreationToken { get; private set; }
        public Exception? Failure { get; set; }
        public CancellationToken ReadToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AppDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            ReadToken = CreationToken = cancellationToken;
            if (FailNext) { FailNext = false; throw Failure ?? new IOException("First feature read failed."); }
            if (HoldNext)
            {
                HoldNext = false; Entered.TrySetResult(); await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
                if (Failure is not null) throw Failure;
            }
            return QueryInterceptor is null ? inner.CreateDbContext()
                : new(new DbContextOptionsBuilder<AppDbContext>(inner.Options).AddInterceptors(QueryInterceptor).Options);
        }
    }

    private sealed class QueryFactory(SqliteTestFactory inner, HeldFeatureQuery interceptor) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>(inner.Options).AddInterceptors(interceptor).Options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class HeldFeatureQuery : DbCommandInterceptor
    {
        public CancellationToken ReadToken { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return result;
        }
    }
}
