using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Models.Workflows;
using KanbanFlow.Services.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace KanbanFlow.Tests.Data;

public sealed class WorkflowSchemaTests
{
    private sealed class ExistingDatabaseFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True").Options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task UpgradeFailure_RollsBackAllAddedTablesAndLeavesLegacyDataUsableForRetry()
    {
        using var scratch = new WorkflowSqliteTestFactory();
        var repositoryRoot = new DirectoryInfo(scratch.ScratchDirectory).Parent!.Parent!.Parent!.FullName;
        var copy = Path.Combine(scratch.ScratchDirectory, "blocked-upgrade.db");
        File.Copy(Path.Combine(repositoryRoot, "seed", "kanbanflow.seed.db"), copy);
        var factory = new ExistingDatabaseFactory(copy);
        await using var db = factory.CreateDbContext();
        // SQLite refuses an index whose name is already occupied by a table. The
        // obstruction fails after the upgrade has executed its CREATE TABLE statements.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IX_WorkRequests_IdeaId (Marker TEXT NOT NULL);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO IX_WorkRequests_IdeaId VALUES ('existing object remains');");
        var before = await FingerprintsAsync(db);
        var failure = Assert.Throws<SqliteException>(() => WorkflowSchema.EnsureTables(db));
        Assert.Equal(1, failure.SqliteErrorCode);
        Assert.Contains("IX_WorkRequests_IdeaId", failure.Message);
        var after = await FingerprintsAsync(db);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var table in before) Assert.Equal(table.Value, after[table.Key]);
        Assert.DoesNotContain("WorkRequests", after.Keys);
        await AssertForeignKeysValidAsync(db);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IX_WorkRequests_IdeaId;");
        WorkflowSchema.EnsureTables(db);
        Assert.Empty(await db.WorkRequests.ToListAsync());
        Assert.True(await db.Projects.AnyAsync());
        Assert.True(await db.Tasks.AnyAsync());
        await AssertForeignKeysValidAsync(db);
    }

    [Fact]
    public async Task FreshSchema_AndRepeatedUpgradePreserveOneWorkflowPerIdea()
    {
        using var f = new WorkflowSqliteTestFactory();
        await using (var db = f.CreateDbContext())
        {
            WorkflowSchema.EnsureTables(db);
            WorkflowSchema.EnsureTables(db);
        }
        var captured = await f.Service.CaptureAsync(new CaptureRequest("Idea", "Original", null), "author", "capture");
        await f.Service.SaveBriefAsync(captured.RequestId, captured.Version, WorkflowSqliteTestFactory.Brief(), "author", "brief");
        var request = await f.LoadAsync(captured.RequestId);
        await using (var db = f.CreateDbContext())
        {
            WorkflowSchema.EnsureTables(db);
            db.WorkRequests.Add(new WorkRequest { IdeaId = request.IdeaId, Title = "Duplicate workflow" });
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var sqlite = Assert.IsType<SqliteException>(failure.InnerException);
            Assert.Equal(19, sqlite.SqliteErrorCode); // SQLITE_CONSTRAINT
            Assert.Contains("UNIQUE constraint failed: WorkRequests.IdeaId", sqlite.Message, StringComparison.Ordinal);
        }
        var reloaded = await f.LoadAsync(request.Id);
        Assert.Equal(request.IdeaId, reloaded.IdeaId);
        Assert.Equal("Original", Assert.Single(reloaded.Messages).Text);
        Assert.Equal(request.Briefs.Single().Outcome, Assert.Single(reloaded.Briefs).Outcome);
        await using var verify = f.CreateDbContext();
        Assert.Equal(1, await verify.WorkRequests.CountAsync());
        await AssertForeignKeysValidAsync(verify);
    }

    [Fact]
    public async Task PopulatedLegacySeed_UpgradePreservesEveryExistingRowAndMemorySearchOnRepeat()
    {
        using var scratch = new WorkflowSqliteTestFactory();
        var repositoryRoot = new DirectoryInfo(scratch.ScratchDirectory).Parent!.Parent!.Parent!.FullName;
        var copy = Path.Combine(scratch.ScratchDirectory, "legacy-upgrade.db");
        File.Copy(Path.Combine(repositoryRoot, "seed", "kanbanflow.seed.db"), copy);
        var factory = new ExistingDatabaseFactory(copy);
        Dictionary<string, string> before;
        int memoryId;
        await using (var db = factory.CreateDbContext())
        {
            SchemaPatcher.EnsureMemoryTables(db);
            SchemaPatcher.EnsureMemoryFtsTable(db);
            var memory = new MemoryNode { ScopeKey = "workflow-schema-witness", Content = "WorkflowUpgradePreservationWitness café", Type = MemoryType.Decision,
                EmbeddingBlob = [1, 2, 3, 4], EmbeddingDim = 1 };
            db.MemoryNodes.Add(memory);
            await db.SaveChangesAsync();
            memoryId = memory.Id;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO fts_memory(rowid, content) VALUES ({memoryId}, {memory.Content})");
            before = await FingerprintsAsync(db);
            Assert.Contains("Projects", before.Keys);
            Assert.Contains("Tasks", before.Keys);
            Assert.DoesNotContain("WorkRequests", before.Keys);
            Assert.True(await db.Projects.AnyAsync());
            Assert.True(await db.Tasks.AnyAsync());
            WorkflowSchema.EnsureTables(db);
            WorkflowSchema.EnsureTables(db);
            var after = await FingerprintsAsync(db);
            foreach (var table in before) Assert.Equal(table.Value, after[table.Key]);
            Assert.Contains("WorkRequests", after.Keys);
            Assert.Contains("PlannedTasks", after.Keys);
            Assert.Empty(await db.WorkRequests.ToListAsync());
            await AssertForeignKeysValidAsync(db);
        }

        // The upgraded database supports the real workflow services and survives reopening.
        var service = new WorkspaceService(factory, scratch.Clock);
        var captured = await service.CaptureAsync(new CaptureRequest("New integrated idea", "New exact original", null), "author", "capture");
        await using (var reopened = factory.CreateDbContext())
        {
            WorkflowSchema.EnsureTables(reopened);
            Assert.Equal(1, await reopened.WorkRequests.CountAsync());
            Assert.Equal(captured.RequestId, (await reopened.WorkRequests.SingleAsync()).Id);
            var memory = await reopened.MemoryNodes.FindAsync(memoryId);
            Assert.Equal("WorkflowUpgradePreservationWitness café", memory!.Content);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, memory.EmbeddingBlob);
            await reopened.Database.OpenConnectionAsync();
            await using var command = reopened.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT rowid FROM fts_memory WHERE fts_memory MATCH 'WorkflowUpgradePreservationWitness';";
            Assert.Equal((long)memoryId, await command.ExecuteScalarAsync());
            await AssertForeignKeysValidAsync(reopened);
        }
    }

    private static async Task<Dictionary<string, string>> FingerprintsAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        var result = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\";";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < row.Length; index++) row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(row);
            }
            var orderedRows = rows.Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal);
            result[table] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', orderedRows))));
        }
        return result;
    }

    internal static async Task AssertForeignKeysValidAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }
}
