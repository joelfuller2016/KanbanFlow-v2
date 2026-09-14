using KanbanFlow.Mcp;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using static KanbanFlow.Tests.WorkflowSqliteTestFactory;

namespace KanbanFlow.Tests.Services.Workflows;

/// <summary>Native tool-to-service integration; actual MCP transport checks are separate.</summary>
public sealed class WorkspaceMcpToolsTests
{
    [Fact]
    public async Task NativeTaskUpdate_RejectsNullWithoutChangingData()
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.PublishedAsync(Draft());
        var taskId = request.WorkItems.Single().TaskItemId;
        var before = await f.Service.ContextAsync(request.Id);
        await using var db = f.CreateDbContext();
        var events = await db.ActivityEvents.CountAsync();
        var error = await Assert.ThrowsAsync<McpException>(() => KanbanMcpTools.UpdateTask(new TaskService(f), taskId, null!));
        Assert.Contains("update payload is required", error.Message);
        var after = await f.Service.ContextAsync(request.Id);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.Tasks.Single().EditToken, after.Tasks.Single().EditToken);
        Assert.Equal(events, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task ProjectAndRepositoryAudit_HaveDistinctEntitiesAndKeepOperationPayloads()
    {
        using var f = new WorkflowSqliteTestFactory();
        var project = await f.Service.CreateProjectAsync("Audited project", "human", Key());
        var path = Path.Combine(f.ScratchDirectory, "audited-repository");
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        var repository = await f.Service.RegisterRepositoryAsync(project, "Repository", path,
            f.ScratchDirectory, "human", Key());
        await using var db = f.CreateDbContext();
        foreach (var (type, id, eventType, operation) in new[] {
            ("Project", project, "WorkspaceProjectCreated", "project.create"),
            ("ProjectRepository", repository, "WorkspaceRepositoryRegistered", "repository.register") })
        {
            var page = await ActivityLogReader.ReadAsync(db, entityType: type, entityId: id);
            var audit = Assert.Single(page.Items, e => e.EventType == eventType);
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), audit.EntityKey);
            using var json = System.Text.Json.JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal(operation, json.RootElement.GetProperty("operation").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Object, json.RootElement.GetProperty("payload").ValueKind);
            Assert.True(json.RootElement.TryGetProperty("result", out _));
        }
        Assert.Empty((await ActivityLogReader.ReadAsync(db, entityType: "WorkRequest")).Items);
    }

    [Fact]
    public async Task NativeReplanCommand_ForwardsReadTokensAndRejectsStaleTaskState()
    {
        using var f = new WorkflowSqliteTestFactory();
        var draft = Draft("Task before review");
        var request = await f.PublishedAsync(draft);
        var read = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var command = Command("save_plan", read);
        command.Tasks = [draft with { Description = "Planner's proposed scope" }];
        var originalTokens = command.TaskBaselines!.ToDictionary();
        var taskId = read.Tasks.Single().TaskId;
        await KanbanMcpTools.UpdateTask(new TaskService(f), taskId, new TaskUpdate(Description: "Human's new scope"), "human");
        var error = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), command));
        Assert.StartsWith("conflict:", error.Message);
        Assert.NotNull(command.TaskBaselines);
        Assert.Equal(originalTokens.OrderBy(t => t.Key), command.TaskBaselines.OrderBy(t => t.Key));
        var after = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(read.Version, after.Version);
        Assert.Single(after.Plans);
        Assert.Equal("Human's new scope", after.Tasks.Single().Description);
        command.TaskBaselines = after.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken);
        command.Tasks = [draft with { Description = after.Tasks.Single().Description! }];
        var saved = await WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), command);
        Assert.Equal(saved, await WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), command));
        after = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(read.Version + 1, after.Version);
        Assert.Equal(saved.Version, after.Version);
        Assert.Equal("Human's new scope", after.Plans.MaxBy(p => p.Number)!.Tasks.Single().Description);
        Assert.Null(after.Plans.MaxBy(p => p.Number)!.PublishedAt);
        Assert.Equal(2, after.Plans.Count);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.WorkflowReceipts.CountAsync(r => r.Key == command.Key));
    }

    [Fact]
    public async Task NativeWorkflowCommands_CompleteTheSameIdeaProjectAndTaskChain()
    {
        using var f = new WorkflowSqliteTestFactory();
        var options = new WorkflowOptions { ProjectsRoot = f.ScratchDirectory };
        Task<WorkflowCommandResult> Send(WorkflowCommandInput input) => WorkspaceMcpTools.WorkflowCommand(f.Service, options, input);
        const string original = " \r\n Preserve the actual request, café 👋\t \n";
        var capture = new WorkflowCommandInput { Operation = "capture", Actor = "agent", Key = "capture", Capture = new CaptureRequest("Shared workflow", original, null, "native-mcp") };
        var captured = await Send(capture);
        Assert.Equal(captured, await Send(capture));
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, captured.RequestId!.Value);
        var originalMessage = Assert.Single(context.Messages);
        Assert.Equal(original, originalMessage.Text);
        Assert.Equal("agent", originalMessage.Author);
        Assert.Equal("native-mcp", originalMessage.Source);
        Assert.Null(context.ProjectId);
        Assert.Equal(context.Id, Assert.Single(await WorkspaceMcpTools.ListRequests(f.Service)).Id);

        // Existing human idea edits and service calls contribute to that same workflow.
        await new IdeaService(f).UpdateAsync(context.IdeaId, new IdeaUpdate(Body: "Human clarified the visible idea notes", ExpectedWorkflowVersion: context.Version), "human");
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        Assert.Contains(context.Messages, m => m.Source == "idea-edit" && m.Author == "human");
        await f.Service.SaveBriefAsync(context.Id, context.Version, Brief("Human approved outcome"), "human", "human-brief");
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        var project = await Send(new WorkflowCommandInput { Operation = "create_project", Actor = "agent", Key = "project", Name = "Shared native board" });
        await Send(Command("attach_project", context, projectId: project.ResourceId));
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        Assert.Equal(project.ResourceId, context.ProjectId);
        Assert.Equal("Promoted", context.IdeaStatus);
        var repositoryPath = Path.Combine(f.ScratchDirectory, "repository");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        var repository = await Send(new WorkflowCommandInput { Operation = "register_repository", Actor = "agent", Key = "repository", ProjectId = project.ResourceId, Name = "Implementation", Path = repositoryPath });
        var draft = Draft("One shared task") with { RepositoryId = repository.ResourceId, FileReferences = "src/Workflow.cs\ndocs/specification.md" };
        var savePlan = Command("save_plan", context); savePlan.Tasks = [draft];
        await Send(savePlan);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        var publish = Command("publish_plan", context); publish.PlanId = context.Plans.Single().Id;
        var published = await Send(publish);
        Assert.Equal(published, await Send(publish));
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        var task = Assert.Single(context.Tasks);
        Assert.Equal(repository.ResourceId, task.Specification.RepositoryId);
        Assert.Equal(draft.FileReferences, task.Specification.FileReferences);
        Assert.Equal(repositoryPath, Assert.Single(context.Repositories).LocalPath);
        var boardTask = Assert.Single((await KanbanMcpTools.GetProjectContext(f, context.ProjectId!.Value)).Tasks);
        Assert.Equal(task.TaskId, boardTask.Id);
        Assert.Equal(task.Status, boardTask.Status);
        Assert.Equal(context.Id, (await WorkspaceMcpTools.GetTaskWorkflow(f.Service, task.TaskId)).Id);

        var start = Command("set_task_state", context); start.ItemId = task.Id; start.Status = WorkStatus.InProgress;
        await Send(start);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        Assert.Equal("InProgress", context.Tasks.Single().Status);
        var update = Command("add_message", context); update.Kind = "progress"; update.Text = "Agent observed the current source";
        await Send(update);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        var report = Command("report_result", context); report.ItemId = task.Id; report.Evidence = new EvidenceDraft("Implemented and inspected", "Executed two specified checks", "src/Workflow.cs at abc123");
        await Send(report);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        task = context.Tasks.Single();
        Assert.Equal("Review", task.Status);
        Assert.Null(task.Evidence.Single().AcceptedAt);
        Assert.True(task.Evidence.Single().Current);
        Assert.Contains(task.Comments, c => c.Type == "Verification" && c.Text.Contains("Executed two specified checks", StringComparison.Ordinal));
        var accept = Command("accept_result", context); accept.Actor = "human"; accept.ItemId = task.Id; accept.EvidenceId = task.Evidence.Single().Id; accept.CheckedCriterionIds = task.AcceptanceCriteria.Select(c => c.Id).ToArray();
        await Send(accept);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        Assert.Equal("Done", context.Tasks.Single().Status);
        Assert.All(context.Tasks.Single().AcceptanceCriteria, c => Assert.True(c.Completed));
        Assert.Equal("human", context.Tasks.Single().Evidence.Single().AcceptedBy);
        var conclude = Command("conclude", context); conclude.Text = "Human verified the requested outcome"; conclude.Limitations = "Checks were local";
        await Send(conclude);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        Assert.Equal(conclude.Text, Assert.Single(context.Results).Conclusion);
        Assert.Equal(conclude.Limitations, context.Results.Single().Limitations);
        Assert.True(context.Results.Single().Current);
        Assert.Equal(original, context.Messages.Single(m => m.Kind == "original").Text);
        var archive = Command("archive", context); archive.Archived = true;
        await Send(archive);
        context = await WorkspaceMcpTools.GetRequestContext(f.Service, context.Id);
        Assert.True(context.Archived);
        var listed = Assert.Single(await WorkspaceMcpTools.ListRequests(f.Service));
        Assert.Equal(context.Id, listed.Id);
        Assert.True(listed.Archived);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.Ideas.CountAsync());
        Assert.Equal(1, await db.Projects.CountAsync());
        Assert.Equal(1, await db.Tasks.CountAsync());
        Assert.Equal(1, await db.TaskEvidence.CountAsync());
        Assert.Equal(1, await db.RequestResults.CountAsync());
    }

    [Fact]
    public async Task NativeCommands_ReportConflictingRetriesAndStaleVersionsWithoutWriting()
    {
        using var f = new WorkflowSqliteTestFactory();
        var options = new WorkflowOptions { ProjectsRoot = f.ScratchDirectory };
        var request = await f.BriefedAsync();
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var original = Command("add_message", context); original.Kind = "clarification"; original.Text = "First answer";
        var applied = await WorkspaceMcpTools.WorkflowCommand(f.Service, options, original);
        Assert.Equal(applied, await WorkspaceMcpTools.WorkflowCommand(f.Service, options, original));
        original.Text = "Changed payload with reused key";
        var collision = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.WorkflowCommand(f.Service, options, original));
        Assert.Contains("conflict", collision.Message, StringComparison.OrdinalIgnoreCase);
        var stale = Command("add_message", context); stale.Kind = "clarification"; stale.Text = "Stale client answer";
        var rejected = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.WorkflowCommand(f.Service, options, stale));
        Assert.Contains("conflict", rejected.Message, StringComparison.OrdinalIgnoreCase);
        var current = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        Assert.Equal(applied.Version, current.Version);
        Assert.Equal("First answer", Assert.Single(current.Messages, m => m.Kind == "clarification").Text);
        await using var db = f.CreateDbContext();
        Assert.Equal(1, await db.WorkflowReceipts.CountAsync(r => r.Operation == "message.add"));
    }

    [Fact]
    public async Task NativeContextReads_MissingOrLegacyTaskDoesNotCreateAWorkflow()
    {
        using var f = new WorkflowSqliteTestFactory();
        await using (var db = f.CreateDbContext())
        {
            db.Tasks.Add(new TaskItem { Name = "Unlinked legacy task", Feature = new Feature { Name = "Legacy feature", Project = new Project { Name = "Legacy project" } } });
            await db.SaveChangesAsync();
        }
        int taskId;
        await using (var db = f.CreateDbContext()) taskId = (await db.Tasks.SingleAsync()).Id;
        Assert.Equal("Unlinked legacy task", (await KanbanMcpTools.GetTask(f, taskId)).Name);
        var legacy = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.GetTaskWorkflow(f.Service, taskId));
        Assert.Contains("no request workflow", legacy.Message);
        await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.GetRequestContext(f.Service, Guid.NewGuid()));
        await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.GetTaskWorkflow(f.Service, int.MaxValue));
        Assert.Empty(await WorkspaceMcpTools.ListRequests(f.Service));
        await using var verify = f.CreateDbContext();
        Assert.Empty(await verify.WorkRequests.ToListAsync());
        Assert.Empty(await verify.Ideas.ToListAsync());
        Assert.Equal(1, await verify.Tasks.CountAsync());
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("input")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("brief")]
    [InlineData("operation")]
    public async Task NativeInvalidCommand_ReturnsActionableValidationWithoutAReceipt(string missing)
    {
        using var f = new WorkflowSqliteTestFactory();
        var request = await f.BriefedAsync();
        var context = await WorkspaceMcpTools.GetRequestContext(f.Service, request.Id);
        var command = Command("save_brief", context);
        command.Brief = Brief();
        switch (missing)
        {
            case "actor": command.Actor = " "; break;
            case "key": command.Key = " "; break;
            case "version": command.ExpectedVersion = null; break;
            case "brief": command.Brief = null; break;
            default: command.Operation = "invented-operation"; break;
        }
        var error = await Assert.ThrowsAsync<McpException>(() => WorkspaceMcpTools.WorkflowCommand(f.Service, new WorkflowOptions(), missing == "input" ? null! : command));
        Assert.StartsWith("invalid:", error.Message);
        var after = await f.LoadAsync(request.Id);
        Assert.Equal(request.Version, after.Version);
        Assert.Single(after.Briefs);
        await using var db = f.CreateDbContext();
        Assert.False(await db.WorkflowReceipts.AnyAsync(r => r.Key == command.Key));
    }

    private static WorkflowCommandInput Command(string operation, WorkflowContext context, int? projectId = null) =>
        new() { Operation = operation, Actor = "agent", Key = Key(), RequestId = context.Id, ExpectedVersion = context.Version, ProjectId = projectId,
            TaskBaselines = context.Tasks.ToDictionary(t => t.TaskId, t => t.EditToken) };
}
