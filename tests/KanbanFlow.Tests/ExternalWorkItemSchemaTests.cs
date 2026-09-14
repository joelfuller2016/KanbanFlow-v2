using KanbanFlow.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class ExternalWorkItemSchemaTests
{
    private static readonly string[] LinkIndexes =
    [
        "IX_ExternalWorkItemLinks_Provider_ExternalNodeId",
        "IX_ExternalWorkItemLinks_Provider_RepositoryExternalId_ExternalNumber",
        "IX_ExternalWorkItemLinks_TaskId_Provider",
    ];

    private static readonly string[] OperationIndexes =
    [
        "IX_ExternalWriteOperations_OperationId",
        "IX_ExternalWriteOperations_TaskId_Provider",
    ];

    [Fact]
    public async Task FreshEnsureCreated_ContainsDurableLinkAndOperationSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        Assert.Equal(
            ["ExternalWorkItemLinks", "ExternalWriteOperations"],
            await GetObjectNamesAsync(connection, "table", "External%"));
        Assert.Equal(LinkIndexes, await GetObjectNamesAsync(connection, "index", "IX_ExternalWorkItemLinks_%"));
        Assert.Equal(OperationIndexes, await GetObjectNamesAsync(connection, "index", "IX_ExternalWriteOperations_%"));

        var foreignKeys = await GetForeignKeysAsync(connection, "ExternalWorkItemLinks");
        Assert.Contains(foreignKeys, fk => fk.Table == "Tasks" && fk.From == "TaskId" && fk.OnDelete == "CASCADE");

        var linkIndexSql = await GetIndexSqlAsync(connection, "IX_ExternalWorkItemLinks_Provider_ExternalNodeId");
        Assert.Contains("WHERE \"ExternalNodeId\" IS NOT NULL", linkIndexSql, StringComparison.OrdinalIgnoreCase);
        var numberIndexSql = await GetIndexSqlAsync(connection, "IX_ExternalWorkItemLinks_Provider_RepositoryExternalId_ExternalNumber");
        Assert.Contains("WHERE \"State\" = 0", numberIndexSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyPatch_CanRunTwice_AndMatchesFreshColumnsAndIndexes()
    {
        await using var legacy = new SqliteConnection("Data Source=:memory:");
        await legacy.OpenAsync();
        await ExecuteAsync(legacy, """
            CREATE TABLE Tasks (
                Id INTEGER NOT NULL CONSTRAINT PK_Tasks PRIMARY KEY AUTOINCREMENT
            )
            """);

        SchemaPatcher.EnsureExternalWorkItemTables(legacy);
        SchemaPatcher.EnsureExternalWorkItemTables(legacy);

        await using var fresh = new SqliteConnection("Data Source=:memory:");
        await fresh.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(fresh).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        foreach (var table in new[] { "ExternalWorkItemLinks", "ExternalWriteOperations" })
        {
            Assert.Equal(await GetColumnsAsync(fresh, table), await GetColumnsAsync(legacy, table));
        }

        Assert.Equal(LinkIndexes, await GetObjectNamesAsync(legacy, "index", "IX_ExternalWorkItemLinks_%"));
        Assert.Equal(OperationIndexes, await GetObjectNamesAsync(legacy, "index", "IX_ExternalWriteOperations_%"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> GetObjectNamesAsync(SqliteConnection connection, string type, string like)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $like ORDER BY name";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$like", like);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return [.. values];
    }

    private static async Task<string[]> GetColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add($"{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}|{reader.GetInt32(5)}");
        }
        return [.. values];
    }

    private static async Task<string> GetIndexSqlAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", index);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException($"Missing index {index}"));
    }

    private static async Task<List<(string Table, string From, string OnDelete)>> GetForeignKeysAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\")";
        var values = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add((reader.GetString(2), reader.GetString(3), reader.GetString(6)));
        return values;
    }
}
