using System.Data.Common;

namespace KanbanFlow.Services;

/// <summary>
/// Raw-SQL helpers for the standalone FTS5 keyword index <c>fts_memory</c> (#223). The table is a
/// SQLite virtual table (not an EF entity), so writes go through ADO.NET on the SAME connection +
/// transaction as the owning operation. Two writers use it: <see cref="MemoryService"/> dual-writes
/// each content mutation (additive, inside the existing audited transaction so an FTS hiccup rolls
/// back with the mutation), and the admin reindex route rebuilds it as a reconciler so a missed
/// dual-write self-heals.
///
/// <para><c>rowid</c> aligns 1:1 with <see cref="Models.MemoryNode.Id"/>. FTS5 has no UPSERT, so an
/// upsert is a DELETE-by-rowid followed by an INSERT — idempotent and safe whether or not a row
/// already exists (a brand-new node's DELETE is simply a no-op). Keeping the table name + column
/// layout here (and in <see cref="Data.SchemaPatcher"/>) is the single source of truth the dual-write
/// and the search reader share.</para>
/// </summary>
internal static class MemoryFts
{
    /// <summary>The FTS5 virtual table name (shared with <see cref="Data.SchemaPatcher"/> and the search reader).</summary>
    public const string Table = "fts_memory";

    /// <summary>
    /// Upsert a node's searchable text: delete any existing row for <paramref name="id"/>, then
    /// insert <paramref name="content"/> under that rowid. Runs on the supplied open
    /// <paramref name="connection"/> enrolled in <paramref name="transaction"/>, so it commits
    /// atomically with the caller's mutation.
    /// </summary>
    public static async Task UpsertAsync(
        DbConnection connection, DbTransaction? transaction, int id, string content, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            $"DELETE FROM {Table} WHERE rowid = $id", ct,
            ("$id", id));
        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {Table}(rowid, content) VALUES ($id, $content)", ct,
            ("$id", id), ("$content", content));
    }

    /// <summary>Delete every row — the reconciler step before re-inserting all current nodes.</summary>
    public static Task DeleteAllAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, $"DELETE FROM {Table}", ct);

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}
