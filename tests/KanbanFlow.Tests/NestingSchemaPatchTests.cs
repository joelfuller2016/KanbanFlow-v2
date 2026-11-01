using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Databases created before bd1c855 ("recursive nesting for features and subtasks"). That commit
/// added <c>Projects.Readme</c>, <c>Features.ParentFeatureId</c>, <c>Subtasks.ParentSubtaskId</c>
/// and the <c>Requirements</c> and <c>TaskComments</c> tables without a startup patcher, and
/// EnsureCreated() does nothing to an existing database, so such a board answered
/// <c>GET /api/projects</c> with a 500 ("no such column: p.Readme").
///
/// <para><see cref="LegacySchemaAndRows"/> is the exact DDL EnsureCreated() produced at b1fe367
/// (bd1c855~1), captured by booting that build once, plus a few rows. The schema assertions compare
/// the patched legacy database with a fresh EnsureCreated() one, so the patcher is held to the
/// current EF model rather than to a second hand-written copy of it.</para>
/// </summary>
public sealed class NestingSchemaPatchTests
{
    private static readonly string[] PatchedTables = ["Projects", "Features", "Subtasks", "Requirements", "TaskComments"];

    private const string LegacySchemaAndRows = """
        CREATE TABLE "Projects" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Description" TEXT NULL,
            "Goals" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        CREATE TABLE "Features" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Features" PRIMARY KEY AUTOINCREMENT,
            "ProjectId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "Description" TEXT NULL,
            "SortOrder" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_Features_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
        );
        CREATE TABLE "Tasks" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Tasks" PRIMARY KEY AUTOINCREMENT,
            "FeatureId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "Description" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "Priority" INTEGER NOT NULL,
            "Assignee" TEXT NULL,
            "SortOrder" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "CompletedAt" TEXT NULL,
            CONSTRAINT "FK_Tasks_Features_FeatureId" FOREIGN KEY ("FeatureId") REFERENCES "Features" ("Id") ON DELETE CASCADE
        );
        CREATE TABLE "Subtasks" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Subtasks" PRIMARY KEY AUTOINCREMENT,
            "TaskItemId" INTEGER NOT NULL,
            "Text" TEXT NOT NULL,
            "Completed" INTEGER NOT NULL,
            "SortOrder" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            CONSTRAINT "FK_Subtasks_Tasks_TaskItemId" FOREIGN KEY ("TaskItemId") REFERENCES "Tasks" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX "IX_Features_ProjectId" ON "Features" ("ProjectId");
        CREATE INDEX "IX_Subtasks_TaskItemId" ON "Subtasks" ("TaskItemId");
        CREATE INDEX "IX_Tasks_FeatureId" ON "Tasks" ("FeatureId");

        INSERT INTO Projects (Id, Name, Description, Goals, CreatedAt, UpdatedAt)
            VALUES (1, 'Legacy board', 'created before bd1c855', NULL, '2026-04-01 10:00:00', '2026-04-02 11:00:00');
        INSERT INTO Features (Id, ProjectId, Name, Description, SortOrder, CreatedAt, UpdatedAt)
            VALUES (1, 1, 'Parent feature', NULL, 0, '2026-04-01 10:00:00', '2026-04-01 10:00:00'),
                   (2, 1, 'Second feature', 'kept as is', 1, '2026-04-01 10:05:00', '2026-04-01 10:05:00');
        INSERT INTO Tasks (Id, FeatureId, Name, Description, Status, Priority, Assignee, SortOrder, CreatedAt, UpdatedAt, CompletedAt)
            VALUES (1, 1, 'Legacy task', NULL, 0, 1, NULL, 0, '2026-04-01 10:10:00', '2026-04-01 10:10:00', NULL);
        INSERT INTO Subtasks (Id, TaskItemId, Text, Completed, SortOrder, CreatedAt)
            VALUES (1, 1, 'Legacy subtask', 0, 0, '2026-04-01 10:15:00');
        """;

    /// <summary>The rows above, read back through the legacy columns only.</summary>
    private static readonly string[] LegacyRowQueries =
    [
        "SELECT Id, Name, Description, Goals, CreatedAt, UpdatedAt FROM Projects ORDER BY Id",
        "SELECT Id, ProjectId, Name, Description, SortOrder, CreatedAt, UpdatedAt FROM Features ORDER BY Id",
        "SELECT Id, FeatureId, Name, Description, Status, Priority, Assignee, SortOrder, CreatedAt, UpdatedAt, CompletedAt FROM Tasks ORDER BY Id",
        "SELECT Id, TaskItemId, Text, Completed, SortOrder, CreatedAt FROM Subtasks ORDER BY Id",
    ];

    [Fact]
    public async Task LegacyDatabase_PatchedTwice_MatchesFreshColumnsIndexesAndForeignKeys()
    {
        await using var legacy = await OpenLegacyAsync();
        foreach (var (table, column) in new[] { ("Projects", "Readme"), ("Features", "ParentFeatureId"), ("Subtasks", "ParentSubtaskId") })
            Assert.DoesNotContain(await DescribeAsync(legacy, table), line => line.StartsWith($"col:{column}|", StringComparison.Ordinal));
        Assert.Empty(await DescribeAsync(legacy, "Requirements"));
        Assert.Empty(await DescribeAsync(legacy, "TaskComments"));

        SchemaPatcher.EnsureNestingSchema(legacy);
        SchemaPatcher.EnsureNestingSchema(legacy);

        await using var fresh = await OpenFreshAsync();
        foreach (var table in PatchedTables)
            Assert.Equal(await DescribeAsync(fresh, table), await DescribeAsync(legacy, table));
    }

    [Fact]
    public async Task LegacyDatabase_AfterPatch_KeepsItsRows_AndServesTheEntitiesThatFailed()
    {
        await using var legacy = await OpenLegacyAsync();
        var rowsBefore = await DumpAsync(legacy, LegacyRowQueries);

        SchemaPatcher.EnsureNestingSchema(legacy);

        Assert.Equal(rowsBefore, await DumpAsync(legacy, LegacyRowQueries));

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(legacy).Options;
        await using (var db = new AppDbContext(options))
        {
            // The reads behind the 500s: p.Readme, f.ParentFeatureId, s.ParentSubtaskId, Requirements, TaskComments.
            Assert.Null((await db.Projects.SingleAsync()).Readme);
            Assert.All(await db.Features.ToListAsync(), feature => Assert.Null(feature.ParentFeatureId));
            Assert.Null((await db.Subtasks.SingleAsync()).ParentSubtaskId);
            Assert.Empty(await db.Requirements.ToListAsync());
            Assert.Empty(await db.TaskComments.ToListAsync());

            // The new relationships can be written through the real model on the patched database.
            db.Features.Add(new Feature { ProjectId = 1, ParentFeatureId = 1, Name = "Child feature" });
            db.Subtasks.Add(new Subtask { TaskItemId = 1, ParentSubtaskId = 1, Text = "Child subtask" });
            db.Requirements.Add(new Requirement { ProjectId = 1, FeatureId = 1, TaskItemId = 1, Text = "A requirement" });
            db.TaskComments.Add(new TaskComment { TaskItemId = 1, Text = "A comment" });
            (await db.Projects.SingleAsync()).Readme = "# Readme";
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            Assert.Equal("# Readme", (await db.Projects.SingleAsync()).Readme);
            Assert.Equal("Child feature", (await db.Features.SingleAsync(f => f.ParentFeatureId == 1)).Name);
            Assert.Equal("Child subtask", (await db.Subtasks.SingleAsync(s => s.ParentSubtaskId == 1)).Text);
            Assert.Equal("A requirement", (await db.Requirements.SingleAsync()).Text);
            Assert.Equal("A comment", (await db.TaskComments.SingleAsync()).Text);
        }
    }

    [Fact]
    public async Task FreshDatabaseWithData_PatchedTwice_IsANoOp()
    {
        await using var fresh = await OpenFreshAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(fresh).Options;
        await using (var db = new AppDbContext(options))
        {
            var project = new Project { Name = "Current board", Readme = "keep me" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var parent = new Feature { ProjectId = project.Id, Name = "Parent" };
            db.Features.Add(parent);
            await db.SaveChangesAsync();
            db.Features.Add(new Feature { ProjectId = project.Id, ParentFeatureId = parent.Id, Name = "Child" });
            db.Requirements.Add(new Requirement { ProjectId = project.Id, FeatureId = parent.Id, Text = "Requirement" });
            await db.SaveChangesAsync();
        }

        var schemaBefore = await DumpAsync(fresh, ["SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name"]);
        var rowsBefore = await DumpAsync(fresh, PatchedTables.Select(table => $"SELECT * FROM {table} ORDER BY Id").ToArray());

        SchemaPatcher.EnsureNestingSchema(fresh);
        SchemaPatcher.EnsureNestingSchema(fresh);

        Assert.Equal(schemaBefore, await DumpAsync(fresh, ["SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name"]));
        Assert.Equal(rowsBefore, await DumpAsync(fresh, PatchedTables.Select(table => $"SELECT * FROM {table} ORDER BY Id").ToArray()));
    }

    private static async Task<SqliteConnection> OpenLegacyAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = LegacySchemaAndRows;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<SqliteConnection> OpenFreshAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();
        return connection;
    }

    /// <summary>
    /// Columns (name, type, not-null, default, primary key), indexes (name, unique, origin, partial,
    /// columns) and foreign keys (target, columns, ON UPDATE, ON DELETE, MATCH) of one table, sorted so
    /// that a column appended by ALTER TABLE compares equal to the same column created in place.
    /// </summary>
    private static async Task<string[]> DescribeAsync(SqliteConnection connection, string table)
    {
        var lines = new List<string>();
        foreach (var row in await QueryAsync(connection, $"PRAGMA table_info(\"{table}\")"))
            lines.Add($"col:{row[1]}|{row[2]}|{row[3]}|{row[4]}|{row[5]}");
        foreach (var row in await QueryAsync(connection, $"PRAGMA index_list(\"{table}\")"))
        {
            var name = (string)row[1]!;
            var columns = (await QueryAsync(connection, $"PRAGMA index_info(\"{name}\")")).Select(info => info[2]);
            lines.Add($"idx:{name}|{row[2]}|{row[3]}|{row[4]}|{string.Join(",", columns)}");
        }
        foreach (var row in await QueryAsync(connection, $"PRAGMA foreign_key_list(\"{table}\")"))
            lines.Add($"fk:{row[2]}|{row[3]}|{row[4]}|{row[5]}|{row[6]}|{row[7]}");
        lines.Sort(StringComparer.Ordinal);
        return [.. lines];
    }

    private static async Task<string[]> DumpAsync(SqliteConnection connection, string[] queries)
    {
        var lines = new List<string>();
        foreach (var query in queries)
        {
            lines.Add($"-- {query}");
            foreach (var row in await QueryAsync(connection, query))
                lines.Add(string.Join("|", row.Select(value => value?.ToString() ?? "NULL")));
        }
        return [.. lines];
    }

    private static async Task<List<object?[]>> QueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        return rows;
    }
}
