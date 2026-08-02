using KanbanFlow.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

/// <summary>
/// Compatibility guards for changing <c>TaskItem.SortOrder</c> from <c>int</c> to
/// <c>double</c>. Existing KanbanFlow databases declare the column as INTEGER;
/// SQLite's numeric affinity must still let the real EF model read those rows and
/// persist fractional positions without rebuilding the Tasks table.
/// </summary>
public sealed class TaskSortOrderSqliteCompatibilityTests
{
    [Fact]
    public async Task LegacyIntegerColumn_ReadsIntegralRows_PersistsFraction_AndOrdersNumerically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(connection, LegacySchemaAndRows);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        // This is intentionally a hand-built legacy schema, not EnsureCreated: the
        // declaration must remain INTEGER while the current EF model is double.
        Assert.Equal("INTEGER", await ScalarStringAsync(connection,
            "SELECT type FROM pragma_table_info('Tasks') WHERE name = 'SortOrder'"));

        await using (var read = new AppDbContext(options))
        {
            var integralValues = await read.Tasks
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .Select(t => t.SortOrder)
                .ToListAsync();

            Assert.Equal(new[] { 0d, 1d, 2d }, integralValues);
        }

        await using (var write = new AppDbContext(options))
        {
            var moved = await write.Tasks.SingleAsync(t => t.Id == 3);
            moved.SortOrder = 0.5d;
            await write.SaveChangesAsync();
        }

        await using (var verify = new AppDbContext(options))
        {
            var ordered = await verify.Tasks
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Id)
                .Select(t => new { t.Id, t.SortOrder })
                .ToListAsync();

            Assert.Equal(new[] { 1, 3, 2 }, ordered.Select(t => t.Id));
            Assert.Equal(new[] { 0d, 0.5d, 1d }, ordered.Select(t => t.SortOrder));
        }

        // INTEGER affinity keeps whole numbers as SQLite integers but preserves a
        // non-integral double using SQLite's per-value REAL storage class.
        Assert.Equal("real", await ScalarStringAsync(connection,
            "SELECT typeof(SortOrder) FROM Tasks WHERE Id = 3"));
        Assert.Equal("INTEGER", await ScalarStringAsync(connection,
            "SELECT type FROM pragma_table_info('Tasks') WHERE name = 'SortOrder'"));
    }

    [Fact]
    public async Task FreshEnsureCreatedSchema_DeclaresTaskSortOrderAsReal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        Assert.Equal("REAL", await ScalarStringAsync(connection,
            "SELECT type FROM pragma_table_info('Tasks') WHERE name = 'SortOrder'"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private const string LegacySchemaAndRows = """
        CREATE TABLE Projects (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Description TEXT NULL,
            Goals TEXT NULL,
            Readme TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE Features (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ProjectId INTEGER NOT NULL,
            ParentFeatureId INTEGER NULL,
            Name TEXT NOT NULL,
            Description TEXT NULL,
            SortOrder INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE Tasks (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            FeatureId INTEGER NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NULL,
            Status INTEGER NOT NULL,
            Priority INTEGER NOT NULL,
            Assignee TEXT NULL,
            ClaimedBy TEXT NULL,
            ClaimedAt TEXT NULL,
            SortOrder INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            CompletedAt TEXT NULL
        );

        INSERT INTO Projects (Id, Name, CreatedAt, UpdatedAt)
        VALUES (1, 'Legacy project', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

        INSERT INTO Features (Id, ProjectId, Name, SortOrder, CreatedAt, UpdatedAt)
        VALUES (1, 1, 'Legacy feature', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

        INSERT INTO Tasks (Id, FeatureId, Name, Status, Priority, SortOrder, CreatedAt, UpdatedAt)
        VALUES
            (1, 1, 'Zero', 1, 0, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
            (2, 1, 'One', 1, 0, 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'),
            (3, 1, 'Two', 1, 0, 2, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
        """;
}
