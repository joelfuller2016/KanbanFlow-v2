using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

/// <summary>
/// Idempotent, SQLite-only schema patchers for tables added after the original
/// <see cref="DbContext"/>.EnsureCreated() baseline. <see cref="AppDbContext"/>.EnsureCreated()
/// builds these tables for a fresh database; a patcher adds them (idempotently) to a database
/// created before the feature landed — the same <c>Ensure*Table</c> pattern Program.cs uses for
/// Ideas/ActivityEvents (adds to the EF-migrations debt, #86).
///
/// Memory schema lives here (not inline in Program.cs) so the same code path that runs at startup
/// is exercised directly by the tests, including the "pre-existing DB without memory tables"
/// idempotency case (#221).
/// </summary>
public static class SchemaPatcher
{
    /// <summary>
    /// Ensure the memory data model tables (<c>MemoryNodes</c>, <c>Observations</c>,
    /// <c>MemoryRelations</c>) and their indexes exist. No-op on non-SQLite providers. Opens and
    /// closes the context connection like the other startup patchers.
    /// </summary>
    public static void EnsureMemoryTables(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsSqlite())
            return;

        db.Database.OpenConnection();
        Exception? patchException = null;
        try
        {
            EnsureMemoryTables(db.Database.GetDbConnection());
        }
        catch (Exception ex)
        {
            patchException = ex;
            throw;
        }
        finally
        {
            try
            {
                db.Database.CloseConnection();
            }
            catch when (patchException is not null)
            {
                // Preserve the patch failure; cleanup is best-effort only on failure path.
            }
        }
    }

    /// <summary>
    /// Connection-level worker: runs the <c>CREATE TABLE/INDEX IF NOT EXISTS</c> batch against an
    /// already-open connection. Public so tests can drive it directly against a raw SQLite
    /// connection that has no memory tables (a pre-feature database). Safe to call repeatedly —
    /// every statement is <c>IF NOT EXISTS</c>. Table/column/index names match what
    /// <see cref="AppDbContext"/>.EnsureCreated() produces so a patched DB and a fresh DB converge.
    /// </summary>
    public static void EnsureMemoryTables(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        // MemoryNodes first — Observations references it. Column types mirror the EF model:
        // enums + ints → INTEGER, DateTime → TEXT, double? → REAL, byte[]? → BLOB.
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS MemoryNodes (
                Id INTEGER NOT NULL CONSTRAINT PK_MemoryNodes PRIMARY KEY AUTOINCREMENT,
                Type INTEGER NOT NULL,
                Scope INTEGER NOT NULL,
                ScopeKey TEXT NOT NULL,
                Content TEXT NOT NULL,
                Tags TEXT NULL,
                Source TEXT NULL,
                Confidence REAL NULL,
                CreatedAt TEXT NOT NULL,
                ValidFrom TEXT NOT NULL,
                ValidTo TEXT NULL,
                SupersededById INTEGER NULL,
                AccessCount INTEGER NOT NULL,
                LastAccessedAt TEXT NULL,
                EmbeddingBlob BLOB NULL,
                EmbeddingDim INTEGER NULL
            )
            """);

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Observations (
                Id INTEGER NOT NULL CONSTRAINT PK_Observations PRIMARY KEY AUTOINCREMENT,
                MemoryNodeId INTEGER NOT NULL,
                Content TEXT NOT NULL,
                Source TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_Observations_MemoryNodes_MemoryNodeId FOREIGN KEY (MemoryNodeId)
                    REFERENCES MemoryNodes (Id) ON DELETE CASCADE
            )
            """);

        // No-FK discriminator edges (like ActivityEvent) — endpoints are (Type, Id) pairs.
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS MemoryRelations (
                Id INTEGER NOT NULL CONSTRAINT PK_MemoryRelations PRIMARY KEY AUTOINCREMENT,
                FromType TEXT NOT NULL,
                FromId INTEGER NOT NULL,
                RelationType TEXT NOT NULL,
                ToType TEXT NOT NULL,
                ToId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            )
            """);

        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_MemoryNodes_Scope_ScopeKey_Type ON MemoryNodes (Scope, ScopeKey, Type)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_MemoryNodes_SupersededById ON MemoryNodes (SupersededById)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Observations_MemoryNodeId ON Observations (MemoryNodeId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_MemoryRelations_FromType_FromId ON MemoryRelations (FromType, FromId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_MemoryRelations_ToType_ToId ON MemoryRelations (ToType, ToId)");

        transaction.Commit();
    }

    private static void Execute(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
