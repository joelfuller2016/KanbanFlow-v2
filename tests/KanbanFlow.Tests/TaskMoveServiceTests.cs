using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Real-SQLite coverage for #307's identity-anchored move contract. A drag may change the moved
/// task's fractional rank, but every non-dragged task must keep its exact persisted SortOrder.
/// </summary>
public sealed class TaskMoveServiceTests
{
    private sealed record BoardSeed(
        int ProjectId,
        int OtherProjectId,
        int HiddenBeforeId,
        int AnchorId,
        int HiddenMiddleId,
        int MovedId,
        int HiddenAfterId,
        int WrongScopeAnchorId);

    private static BoardSeed SeedDenseBoard(SqliteTestFactory factory)
    {
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Board project" };
        var otherProject = new Project { Name = "Other project" };
        db.Projects.AddRange(project, otherProject);
        db.SaveChanges();

        var featureA = new Feature { ProjectId = project.Id, Name = "Feature A" };
        var featureB = new Feature { ProjectId = project.Id, Name = "Feature B" };
        var otherFeature = new Feature { ProjectId = otherProject.Id, Name = "Other feature" };
        db.Features.AddRange(featureA, featureB, otherFeature);
        db.SaveChanges();

        // Alternate features deliberately: board order is project/status scoped, not feature scoped.
        var hiddenBefore = new TaskItem { FeatureId = featureA.Id, Name = "hidden before", Status = WorkStatus.Todo, SortOrder = 0 };
        var anchor = new TaskItem { FeatureId = featureB.Id, Name = "visible anchor", Status = WorkStatus.Todo, SortOrder = 1 };
        var hiddenMiddle = new TaskItem { FeatureId = featureA.Id, Name = "hidden middle", Status = WorkStatus.Todo, SortOrder = 2 };
        var moved = new TaskItem { FeatureId = featureB.Id, Name = "moved visible", Status = WorkStatus.Todo, SortOrder = 3 };
        var hiddenAfter = new TaskItem { FeatureId = featureA.Id, Name = "hidden after", Status = WorkStatus.Todo, SortOrder = 4 };
        var wrongScopeAnchor = new TaskItem { FeatureId = otherFeature.Id, Name = "wrong scope", Status = WorkStatus.Todo, SortOrder = 0 };
        db.Tasks.AddRange(hiddenBefore, anchor, hiddenMiddle, moved, hiddenAfter, wrongScopeAnchor);
        db.SaveChanges();

        return new BoardSeed(
            project.Id, otherProject.Id, hiddenBefore.Id, anchor.Id, hiddenMiddle.Id,
            moved.Id, hiddenAfter.Id, wrongScopeAnchor.Id);
    }

    private static async Task<int[]> BoardOrder(SqliteTestFactory factory, int projectId, WorkStatus status)
    {
        await using var db = factory.CreateDbContext();
        var tasks = await db.Tasks
            .Where(t => t.Feature.ProjectId == projectId && t.Status == status)
            .ToArrayAsync();
        return TaskOrdering.Order(tasks).Select(t => t.Id).ToArray();
    }

    private static async Task<Dictionary<int, double>> TaskRanks(
        SqliteTestFactory factory,
        params int[] taskIds)
    {
        await using var db = factory.CreateDbContext();
        return await db.Tasks
            .Where(t => taskIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.SortOrder);
    }

    private static void AssertBystanderRanksUnchanged(
        IReadOnlyDictionary<int, double> before,
        IReadOnlyDictionary<int, double> after,
        int movedTaskId)
    {
        foreach (var (taskId, sortOrder) in before.Where(pair => pair.Key != movedTaskId))
            Assert.Equal(sortOrder, after[taskId]);
    }

    [Fact]
    public void TaskOrdering_LargeAdjacentIntegralRanks_PreservesLegacyRawOrder()
    {
        var lower = new TaskItem { Id = int.MaxValue, Name = "lower", SortOrder = 4_194_304d };
        var upper = new TaskItem { Id = 1, Name = "upper", SortOrder = 4_194_305d };

        // At this magnitude the virtual Id fraction rounds onto the next integral rank.
        Assert.Equal(TaskOrdering.EffectiveRank(lower), TaskOrdering.EffectiveRank(upper));

        Assert.Equal(new[] { lower, upper }, TaskOrdering.Order(new[] { upper, lower }));
        Assert.True(TaskOrdering.Compare(lower.SortOrder, lower.Id, upper) < 0);
        Assert.True(TaskOrdering.Compare(upper.SortOrder, upper.Id, lower) > 0);
    }

    [Fact]
    public async Task MoveOnBoard_BeforeVisibleAnchor_UsesProjectStatusIdentityOrder()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseBoard(factory);
        var service = new TaskService(factory);
        var taskIds = new[]
        {
            seed.HiddenBeforeId, seed.AnchorId, seed.HiddenMiddleId, seed.MovedId, seed.HiddenAfterId
        };
        var ranksBefore = await TaskRanks(factory, taskIds);

        await service.MoveOnBoardAsync(
            seed.MovedId, WorkStatus.Todo, seed.ProjectId, seed.AnchorId, "board-user");

        var actual = await BoardOrder(factory, seed.ProjectId, WorkStatus.Todo);
        var expected = new[]
        {
            seed.HiddenBeforeId, seed.MovedId, seed.AnchorId, seed.HiddenMiddleId, seed.HiddenAfterId
        };
        Assert.Equal(expected, actual);

        var ranksAfter = await TaskRanks(factory, taskIds);
        Assert.Equal(0.5d, ranksAfter[seed.MovedId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, seed.MovedId);

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
        Assert.Equal(0, await db.TaskComments.CountAsync());
    }

    [Fact]
    public async Task MoveOnBoard_DuplicatePerFeatureRanks_InsertsBetweenLegacyIdentities()
    {
        using var factory = new SqliteTestFactory();
        int projectId;
        int featureAFirstId;
        int visibleAnchorId;
        int featureASecondId;
        int movedVisibleId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "Duplicate-rank project" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var featureA = new Feature { ProjectId = project.Id, Name = "Feature A" };
            var featureB = new Feature { ProjectId = project.Id, Name = "Feature B" };
            db.Features.AddRange(featureA, featureB);
            await db.SaveChangesAsync();

            // This is the normal CreateAsync shape: each feature starts at 0, then advances to 1.
            // Insert interleaved so the former SortOrder/Id board order is A0, B0, A1, B1.
            var featureAFirst = new TaskItem { FeatureId = featureA.Id, Name = "A0", Status = WorkStatus.Todo, SortOrder = 0 };
            var visibleAnchor = new TaskItem { FeatureId = featureB.Id, Name = "B0", Status = WorkStatus.Todo, SortOrder = 0 };
            var featureASecond = new TaskItem { FeatureId = featureA.Id, Name = "A1", Status = WorkStatus.Todo, SortOrder = 1 };
            var movedVisible = new TaskItem { FeatureId = featureB.Id, Name = "B1", Status = WorkStatus.Todo, SortOrder = 1 };
            db.Tasks.AddRange(featureAFirst, visibleAnchor, featureASecond, movedVisible);
            await db.SaveChangesAsync();

            projectId = project.Id;
            featureAFirstId = featureAFirst.Id;
            visibleAnchorId = visibleAnchor.Id;
            featureASecondId = featureASecond.Id;
            movedVisibleId = movedVisible.Id;
        }

        var taskIds = new[] { featureAFirstId, visibleAnchorId, featureASecondId, movedVisibleId };
        var ranksBefore = await TaskRanks(factory, taskIds);
        var service = new TaskService(factory);

        await service.MoveOnBoardAsync(
            movedVisibleId, WorkStatus.Todo, projectId, visibleAnchorId, "board-user");

        Assert.Equal(
            new[] { featureAFirstId, movedVisibleId, visibleAnchorId, featureASecondId },
            await BoardOrder(factory, projectId, WorkStatus.Todo));
        var ranksAfter = await TaskRanks(factory, taskIds);
        Assert.True(ranksAfter[movedVisibleId] > 0d && ranksAfter[movedVisibleId] < 1d);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, movedVisibleId);
    }

    [Fact]
    public async Task MoveOnBoard_ZeroAnchor_Appends()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseBoard(factory);
        var service = new TaskService(factory);
        var taskIds = new[]
        {
            seed.HiddenBeforeId, seed.AnchorId, seed.HiddenMiddleId, seed.MovedId, seed.HiddenAfterId
        };
        var ranksBefore = await TaskRanks(factory, taskIds);

        await service.MoveOnBoardAsync(
            seed.HiddenBeforeId, WorkStatus.Todo, seed.ProjectId, beforeTaskId: 0, actor: "board-user");

        Assert.Equal(
            new[] { seed.AnchorId, seed.HiddenMiddleId, seed.MovedId, seed.HiddenAfterId, seed.HiddenBeforeId },
            await BoardOrder(factory, seed.ProjectId, WorkStatus.Todo));

        var ranksAfter = await TaskRanks(factory, taskIds);
        Assert.Equal(5d, ranksAfter[seed.HiddenBeforeId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, seed.HiddenBeforeId);
    }

    [Fact]
    public async Task MoveOnBoard_BeforeFirstTask_PrependsWithoutChangingBystanders()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseBoard(factory);
        var service = new TaskService(factory);
        var taskIds = new[]
        {
            seed.HiddenBeforeId, seed.AnchorId, seed.HiddenMiddleId, seed.MovedId, seed.HiddenAfterId
        };
        var ranksBefore = await TaskRanks(factory, taskIds);

        await service.MoveOnBoardAsync(
            seed.HiddenAfterId, WorkStatus.Todo, seed.ProjectId, seed.HiddenBeforeId, "board-user");

        Assert.Equal(
            new[] { seed.HiddenAfterId, seed.HiddenBeforeId, seed.AnchorId, seed.HiddenMiddleId, seed.MovedId },
            await BoardOrder(factory, seed.ProjectId, WorkStatus.Todo));
        var ranksAfter = await TaskRanks(factory, taskIds);
        Assert.Equal(-1d, ranksAfter[seed.HiddenAfterId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, seed.HiddenAfterId);
    }

    [Fact]
    public async Task MoveOnBoard_StatusChangeAndOrderPersistTogether_WithOneAuditTrail()
    {
        using var factory = new SqliteTestFactory();
        int projectId;
        int movingId;
        int beforeId;
        int firstId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "Status project" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var feature = new Feature { ProjectId = project.Id, Name = "Feature" };
            db.Features.Add(feature);
            await db.SaveChangesAsync();
            var moving = new TaskItem { FeatureId = feature.Id, Name = "moving", Status = WorkStatus.Backlog, SortOrder = 9 };
            var first = new TaskItem { FeatureId = feature.Id, Name = "first", Status = WorkStatus.Done, SortOrder = 0 };
            var before = new TaskItem { FeatureId = feature.Id, Name = "anchor", Status = WorkStatus.Done, SortOrder = 1 };
            db.Tasks.AddRange(moving, first, before);
            await db.SaveChangesAsync();
            projectId = project.Id;
            movingId = moving.Id;
            beforeId = before.Id;
            firstId = first.Id;
        }

        var service = new TaskService(factory);
        var ranksBefore = await TaskRanks(factory, firstId, beforeId, movingId);
        var moved = await service.MoveOnBoardAsync(
            movingId, WorkStatus.Done, projectId, beforeId, "board-user");

        Assert.Equal(WorkStatus.Done, moved.Status);
        Assert.NotNull(moved.CompletedAt);
        Assert.Equal(
            new[] { firstId, movingId, beforeId },
            await BoardOrder(factory, projectId, WorkStatus.Done));
        var ranksAfter = await TaskRanks(factory, firstId, beforeId, movingId);
        Assert.Equal(0.5d, ranksAfter[movingId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, movingId);

        await using var check = factory.CreateDbContext();
        Assert.Equal(1, await check.ActivityEvents.CountAsync(e =>
            e.EntityId == movingId && e.EventType == "TaskStatusChanged" && e.Actor == "board-user"));
        Assert.Equal(1, await check.TaskComments.CountAsync(c =>
            c.TaskItemId == movingId && c.Type == CommentType.StatusChange && c.Author == "board-user"));
    }

    [Fact]
    public async Task MoveOnBoard_AnchorOutsideDestinationScope_RejectsWithoutWrites()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseBoard(factory);
        var service = new TaskService(factory);
        var before = await BoardOrder(factory, seed.ProjectId, WorkStatus.Todo);

        await Assert.ThrowsAsync<ValidationException>(() => service.MoveOnBoardAsync(
            seed.MovedId, WorkStatus.Todo, seed.ProjectId, seed.WrongScopeAnchorId, "board-user"));

        Assert.Equal(before, await BoardOrder(factory, seed.ProjectId, WorkStatus.Todo));
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
        Assert.Equal(0, await db.TaskComments.CountAsync());
    }

    private sealed record OutlineSeed(
        int SourceFeatureId,
        int DestinationFeatureId,
        int OtherProjectFeatureId,
        int MovedId,
        int SourceBystanderId,
        int DestinationFirstId,
        int DestinationAnchorId,
        int DestinationLastId);

    private static OutlineSeed SeedDenseOutline(SqliteTestFactory factory)
    {
        using var db = factory.CreateDbContext();
        var project = new Project { Name = "Outline project" };
        var otherProject = new Project { Name = "Other outline project" };
        db.Projects.AddRange(project, otherProject);
        db.SaveChanges();
        var source = new Feature { ProjectId = project.Id, Name = "Source" };
        var destination = new Feature { ProjectId = project.Id, Name = "Destination" };
        var otherDestination = new Feature { ProjectId = otherProject.Id, Name = "Other destination" };
        db.Features.AddRange(source, destination, otherDestination);
        db.SaveChanges();

        var sourceBystander = new TaskItem { FeatureId = source.Id, Name = "source bystander", Status = WorkStatus.Todo, SortOrder = 0 };
        var moved = new TaskItem { FeatureId = source.Id, Name = "moved", Status = WorkStatus.Backlog, SortOrder = 1 };
        var first = new TaskItem { FeatureId = destination.Id, Name = "first", Status = WorkStatus.Todo, SortOrder = 0 };
        // Different status deliberately: outline order is feature scoped, not status scoped.
        var anchor = new TaskItem { FeatureId = destination.Id, Name = "anchor", Status = WorkStatus.Review, SortOrder = 1 };
        var last = new TaskItem { FeatureId = destination.Id, Name = "last", Status = WorkStatus.Done, SortOrder = 2 };
        db.Tasks.AddRange(sourceBystander, moved, first, anchor, last);
        db.SaveChanges();

        return new OutlineSeed(
            source.Id, destination.Id, otherDestination.Id, moved.Id, sourceBystander.Id,
            first.Id, anchor.Id, last.Id);
    }

    private static async Task<int[]> FeatureOrder(SqliteTestFactory factory, int featureId)
    {
        await using var db = factory.CreateDbContext();
        var tasks = await db.Tasks
            .Where(t => t.FeatureId == featureId)
            .ToArrayAsync();
        return TaskOrdering.Order(tasks).Select(t => t.Id).ToArray();
    }

    [Fact]
    public async Task MoveInOutline_CrossFeatureAnchor_AcceptsDifferentStatusAndAuditsOnce()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseOutline(factory);
        var service = new TaskService(factory);
        var taskIds = new[]
        {
            seed.SourceBystanderId, seed.MovedId, seed.DestinationFirstId,
            seed.DestinationAnchorId, seed.DestinationLastId
        };
        var ranksBefore = await TaskRanks(factory, taskIds);

        var moved = await service.MoveInOutlineAsync(
            seed.MovedId, seed.DestinationFeatureId, seed.DestinationAnchorId, "outline-user");

        Assert.Equal(seed.DestinationFeatureId, moved.FeatureId);
        Assert.Equal(
            new[] { seed.DestinationFirstId, seed.MovedId, seed.DestinationAnchorId, seed.DestinationLastId },
            await FeatureOrder(factory, seed.DestinationFeatureId));
        Assert.Equal(new[] { seed.SourceBystanderId }, await FeatureOrder(factory, seed.SourceFeatureId));
        var ranksAfter = await TaskRanks(factory, taskIds);
        Assert.Equal(0.5d, ranksAfter[seed.MovedId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, seed.MovedId);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.ActivityEvents.CountAsync(e =>
            e.EntityId == seed.MovedId && e.EventType == "TaskFeatureChanged" && e.Actor == "outline-user"));
        Assert.Equal(1, await db.TaskComments.CountAsync(c =>
            c.TaskItemId == seed.MovedId && c.Type == CommentType.FeatureChange && c.Author == "outline-user"));
    }

    [Fact]
    public async Task MoveInOutline_ZeroAnchor_AppendsWithinSameFeatureWithoutAudit()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseOutline(factory);
        var service = new TaskService(factory);
        var taskIds = new[]
        {
            seed.DestinationFirstId, seed.DestinationAnchorId, seed.DestinationLastId
        };
        var ranksBefore = await TaskRanks(factory, taskIds);

        await service.MoveInOutlineAsync(
            seed.DestinationFirstId, seed.DestinationFeatureId, beforeTaskId: 0, actor: "outline-user");

        Assert.Equal(
            new[] { seed.DestinationAnchorId, seed.DestinationLastId, seed.DestinationFirstId },
            await FeatureOrder(factory, seed.DestinationFeatureId));
        var ranksAfter = await TaskRanks(factory, taskIds);
        Assert.Equal(3d, ranksAfter[seed.DestinationFirstId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, seed.DestinationFirstId);
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
        Assert.Equal(0, await db.TaskComments.CountAsync());
    }

    [Fact]
    public async Task MoveInOutline_CrossProjectDestination_RejectsWithoutWrites()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseOutline(factory);
        var service = new TaskService(factory);

        await Assert.ThrowsAsync<CrossProjectReassignmentException>(() => service.MoveInOutlineAsync(
            seed.MovedId, seed.OtherProjectFeatureId, beforeTaskId: 0, actor: "outline-user"));

        Assert.Equal(
            new[] { seed.SourceBystanderId, seed.MovedId },
            await FeatureOrder(factory, seed.SourceFeatureId));
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
        Assert.Equal(0, await db.TaskComments.CountAsync());
    }

    [Fact]
    public async Task MoveInOutline_SaveFailure_RollsBackFeatureAndOrderTogether()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseOutline(factory);
        var service = new TaskService(new ThrowOnSaveFactory(factory));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveInOutlineAsync(
            seed.MovedId, seed.DestinationFeatureId, seed.DestinationAnchorId, "outline-user"));

        Assert.Equal(
            new[] { seed.SourceBystanderId, seed.MovedId },
            await FeatureOrder(factory, seed.SourceFeatureId));
        Assert.Equal(
            new[] { seed.DestinationFirstId, seed.DestinationAnchorId, seed.DestinationLastId },
            await FeatureOrder(factory, seed.DestinationFeatureId));
        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.ActivityEvents.CountAsync());
        Assert.Equal(0, await db.TaskComments.CountAsync());
    }

    [Fact]
    public async Task MoveInOutline_PersistsFeatureRankAndAuditWithOneSave()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseOutline(factory);
        var countingFactory = new CountingSaveFactory(factory);
        var service = new TaskService(countingFactory);

        await service.MoveInOutlineAsync(
            seed.MovedId, seed.DestinationFeatureId, seed.DestinationAnchorId, "outline-user");

        Assert.Equal(1, countingFactory.SaveCount);
        Assert.Equal(
            new[] { seed.DestinationFirstId, seed.MovedId, seed.DestinationAnchorId, seed.DestinationLastId },
            await FeatureOrder(factory, seed.DestinationFeatureId));
    }

    [Fact]
    public async Task MoveOnBoard_NoRepresentableRankAndUnsafeIdTies_RejectsWithoutWrites()
    {
        using var factory = new SqliteTestFactory();
        int projectId;
        int upperId;
        int movingId;
        int lowerId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "Precision project" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var feature = new Feature { ProjectId = project.Id, Name = "Precision feature" };
            db.Features.Add(feature);
            await db.SaveChangesAsync();

            // Ids are deliberately upper < moving < lower. That makes neither equal-rank
            // tie capable of representing lower, moving, upper once the adjacent doubles
            // leave no distinct midpoint.
            var upper = new TaskItem
            {
                FeatureId = feature.Id,
                Name = "upper",
                Status = WorkStatus.Todo,
                SortOrder = Math.BitIncrement(1.5d)
            };
            var moving = new TaskItem
            {
                FeatureId = feature.Id,
                Name = "moving",
                Status = WorkStatus.Todo,
                SortOrder = 10d
            };
            var lower = new TaskItem
            {
                FeatureId = feature.Id,
                Name = "lower",
                Status = WorkStatus.Todo,
                SortOrder = 1.5d
            };
            db.Tasks.AddRange(upper, moving, lower);
            await db.SaveChangesAsync();
            Assert.True(upper.Id < moving.Id && moving.Id < lower.Id);

            projectId = project.Id;
            upperId = upper.Id;
            movingId = moving.Id;
            lowerId = lower.Id;
        }

        var ranksBefore = await TaskRanks(factory, lowerId, movingId, upperId);
        var service = new TaskService(factory);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.MoveOnBoardAsync(
            movingId, WorkStatus.Todo, projectId, upperId, "board-user"));

        Assert.Contains("No ordering space remains", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ranksBefore, await TaskRanks(factory, lowerId, movingId, upperId));
        Assert.Equal(new[] { lowerId, upperId, movingId },
            await BoardOrder(factory, projectId, WorkStatus.Todo));
        await using var check = factory.CreateDbContext();
        Assert.Equal(0, await check.ActivityEvents.CountAsync());
        Assert.Equal(0, await check.TaskComments.CountAsync());
    }

    [Fact]
    public async Task MoveOnBoard_EqualFractionalRanks_UsesSafeIdTieWithoutChangingBystanders()
    {
        using var factory = new SqliteTestFactory();
        int projectId;
        int lowerId;
        int movingId;
        int upperId;
        await using (var db = factory.CreateDbContext())
        {
            var project = new Project { Name = "Safe tie project" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var feature = new Feature { ProjectId = project.Id, Name = "Safe tie feature" };
            db.Features.Add(feature);
            await db.SaveChangesAsync();
            var lower = new TaskItem { FeatureId = feature.Id, Name = "lower", Status = WorkStatus.Todo, SortOrder = 1.5d };
            var moving = new TaskItem { FeatureId = feature.Id, Name = "moving", Status = WorkStatus.Todo, SortOrder = 10d };
            var upper = new TaskItem { FeatureId = feature.Id, Name = "upper", Status = WorkStatus.Todo, SortOrder = 1.5d };
            db.Tasks.AddRange(lower, moving, upper);
            await db.SaveChangesAsync();
            projectId = project.Id;
            lowerId = lower.Id;
            movingId = moving.Id;
            upperId = upper.Id;
        }

        var ranksBefore = await TaskRanks(factory, lowerId, movingId, upperId);
        var service = new TaskService(factory);

        await service.MoveOnBoardAsync(
            movingId, WorkStatus.Todo, projectId, upperId, "board-user");

        Assert.Equal(new[] { lowerId, movingId, upperId },
            await BoardOrder(factory, projectId, WorkStatus.Todo));
        var ranksAfter = await TaskRanks(factory, lowerId, movingId, upperId);
        Assert.Equal(1.5d, ranksAfter[movingId]);
        AssertBystanderRanksUnchanged(ranksBefore, ranksAfter, movingId);
    }

    [Fact]
    public async Task CreateAndUpdate_RejectNonFiniteSortOrderWithoutWrites()
    {
        using var factory = new SqliteTestFactory();
        var board = SeedDenseBoard(factory);
        var outline = SeedDenseOutline(factory);
        var service = new TaskService(factory);
        var originalRank = (await TaskRanks(factory, board.MovedId))[board.MovedId];

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(
            board.MovedId,
            new TaskUpdate(SortOrder: double.NaN),
            "api-user"));
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(
            new TaskCreate(
                outline.DestinationFeatureId,
                "invalid rank",
                SortOrder: double.PositiveInfinity),
            "api-user"));

        Assert.Equal(originalRank, (await TaskRanks(factory, board.MovedId))[board.MovedId]);
        await using var check = factory.CreateDbContext();
        Assert.False(await check.Tasks.AnyAsync(t => t.Name == "invalid rank"));
    }

    [Fact]
    public async Task MoveMethods_RejectSelfAnchorAndMissingTask()
    {
        using var factory = new SqliteTestFactory();
        var seed = SeedDenseBoard(factory);
        var service = new TaskService(factory);

        await Assert.ThrowsAsync<ValidationException>(() => service.MoveOnBoardAsync(
            seed.MovedId, WorkStatus.Todo, seed.ProjectId, seed.MovedId, "board-user"));
        await Assert.ThrowsAsync<TaskNotFoundException>(() => service.MoveOnBoardAsync(
            999_999, WorkStatus.Todo, seed.ProjectId, beforeTaskId: 0, actor: "board-user"));
    }

    private sealed class ThrowOnSaveFactory(SqliteTestFactory inner) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new ThrowOnSaveContext(inner.Options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<AppDbContext>(new ThrowOnSaveContext(inner.Options));
    }

    private sealed class ThrowOnSaveContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated move save failure");
    }

    private sealed class CountingSaveFactory(SqliteTestFactory inner) : IDbContextFactory<AppDbContext>
    {
        private int saveCount;

        public int SaveCount => saveCount;

        public AppDbContext CreateDbContext()
            => new CountingSaveContext(inner.Options, () => Interlocked.Increment(ref saveCount));

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<AppDbContext>(CreateDbContext());
    }

    private sealed class CountingSaveContext(
        DbContextOptions<AppDbContext> options,
        Action onSave) : AppDbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            onSave();
            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
