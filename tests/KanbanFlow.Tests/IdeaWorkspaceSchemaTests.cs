using KanbanFlow.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Tests;

public sealed class IdeaWorkspaceSchemaTests
{
    [Fact]
    public async Task FreshSchema_CreatesOnlyTheTwoPrefixedWorkspaceTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        Assert.Equal(
            ["IdeaWorkspaceReceipts", "IdeaWorkspaceStates"],
            await ReadNames(connection, "table", "IdeaWorkspace%"));
    }

    [Fact]
    public async Task LegacyPatch_RunsTwice_PreservesExistingContent_AndMatchesFreshSchema()
    {
        await using var legacy = new SqliteConnection("Data Source=:memory:");
        await legacy.OpenAsync();
        await using (var create = legacy.CreateCommand())
        {
            create.CommandText = "CREATE TABLE LegacyContent (Id INTEGER PRIMARY KEY, Content TEXT NOT NULL)";
            await create.ExecuteNonQueryAsync();
            create.CommandText = "INSERT INTO LegacyContent (Id, Content) VALUES (7, $content)";
            create.Parameters.AddWithValue("$content", "  original\r\n\r\n    legacy content\r\n");
            await create.ExecuteNonQueryAsync();
        }

        var legacyOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(legacy).Options;
        await using (var db = new AppDbContext(legacyOptions))
        {
            IdeaWorkspaceSchema.EnsureTables(db);
            IdeaWorkspaceSchema.EnsureTables(db);
        }

        await using var fresh = new SqliteConnection("Data Source=:memory:");
        await fresh.OpenAsync();
        var freshOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(fresh).Options;
        await using (var db = new AppDbContext(freshOptions))
            await db.Database.EnsureCreatedAsync();

        Assert.Equal(
            ["IdeaWorkspaceReceipts", "IdeaWorkspaceStates"],
            await ReadNames(legacy, "table", "IdeaWorkspace%"));
        foreach (var table in new[] { "IdeaWorkspaceStates", "IdeaWorkspaceReceipts" })
            Assert.Equal(await ReadColumns(fresh, table), await ReadColumns(legacy, table));
        Assert.Equal(
            await ReadIndexes(fresh),
            await ReadIndexes(legacy));

        await using var read = legacy.CreateCommand();
        read.CommandText = "SELECT Content FROM LegacyContent WHERE Id = 7";
        Assert.Equal("  original\r\n\r\n    legacy content\r\n", await read.ExecuteScalarAsync());
        Assert.Equal(
            ["IdeaWorkspaceReceipts", "IdeaWorkspaceStates", "LegacyContent"],
            (await ReadNames(legacy, "table", "%")).Where(name => name != "sqlite_sequence").ToArray());
    }

    private static async Task<string[]> ReadNames(SqliteConnection connection, string type, string pattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $pattern ORDER BY name";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$pattern", pattern);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return [.. values];
    }

    private static async Task<string[]> ReadColumns(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        // table is an allowlisted constant from the two workspace table names above.
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add($"{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}|{reader.GetInt32(5)}");
        return [.. values];
    }

    private static async Task<string[]> ReadIndexes(SqliteConnection connection)
    {
        var values = new List<string>();
        foreach (var table in new[] { "IdeaWorkspaceStates", "IdeaWorkspaceReceipts" })
        {
            var indexes = new List<(string Name, int Unique, string Origin, int Partial)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    indexes.Add((reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4)));
            }
            foreach (var index in indexes)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA index_info(\"{index.Name}\")";
                var columns = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(2));
                values.Add($"{index.Name}|{index.Unique}|{index.Origin}|{index.Partial}|{string.Join(',', columns)}");
            }
        }
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
}
