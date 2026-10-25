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
    /// Ensure the durable external-work-item link and write-journal tables added by #336 exist on
    /// databases created before those entities joined the EF model. No-op on non-SQLite providers.
    /// </summary>
    public static void EnsureExternalWorkItemTables(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsSqlite())
            return;

        db.Database.OpenConnection();
        Exception? patchException = null;
        try
        {
            EnsureExternalWorkItemTables(db.Database.GetDbConnection());
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
    /// Connection-level worker for <see cref="EnsureExternalWorkItemTables(AppDbContext)"/>. The
    /// column and index names intentionally match EF's fresh-database schema and every statement is
    /// idempotent.
    /// </summary>
    public static void EnsureExternalWorkItemTables(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS ExternalWorkItemLinks (
                Id INTEGER NOT NULL CONSTRAINT PK_ExternalWorkItemLinks PRIMARY KEY AUTOINCREMENT,
                TaskId INTEGER NOT NULL,
                Provider INTEGER NOT NULL,
                State INTEGER NOT NULL,
                RepositoryExternalId TEXT NOT NULL,
                RepositoryOwner TEXT NOT NULL,
                RepositoryName TEXT NOT NULL,
                ExternalNodeId TEXT NULL,
                ExternalNumber INTEGER NULL,
                Url TEXT NULL,
                QuarantineReason TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CONSTRAINT FK_ExternalWorkItemLinks_Tasks_TaskId FOREIGN KEY (TaskId)
                    REFERENCES Tasks (Id) ON DELETE CASCADE
            )
            """);

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS ExternalWriteOperations (
                Id INTEGER NOT NULL CONSTRAINT PK_ExternalWriteOperations PRIMARY KEY AUTOINCREMENT,
                OperationId TEXT NOT NULL,
                TaskId INTEGER NOT NULL,
                ProjectId INTEGER NOT NULL,
                Provider INTEGER NOT NULL,
                RepositoryExternalId TEXT NOT NULL,
                RepositoryOwner TEXT NOT NULL,
                RepositoryName TEXT NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL,
                MilestoneTitle TEXT NULL,
                Label TEXT NULL,
                RequestHash TEXT NOT NULL,
                ExpectedTaskUpdatedAt TEXT NOT NULL,
                State INTEGER NOT NULL,
                LeaseToken TEXT NULL,
                LeaseExpiresAt TEXT NULL,
                AttemptCount INTEGER NOT NULL,
                LastAttemptAt TEXT NULL,
                DispatchedAt TEXT NULL,
                LastError TEXT NULL,
                ResultingExternalNodeId TEXT NULL,
                ResultingExternalNumber INTEGER NULL,
                ResultingUrl TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CompletedAt TEXT NULL
            )
            """);

        Execute(connection, transaction,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalWorkItemLinks_Provider_ExternalNodeId ON ExternalWorkItemLinks (Provider, ExternalNodeId) WHERE \"ExternalNodeId\" IS NOT NULL");
        Execute(connection, transaction,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalWorkItemLinks_TaskId_Provider ON ExternalWorkItemLinks (TaskId, Provider)");
        Execute(connection, transaction,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalWorkItemLinks_Provider_RepositoryExternalId_ExternalNumber ON ExternalWorkItemLinks (Provider, RepositoryExternalId, ExternalNumber) WHERE \"State\" = 0 AND \"ExternalNumber\" IS NOT NULL");
        Execute(connection, transaction,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalWriteOperations_OperationId ON ExternalWriteOperations (OperationId)");
        Execute(connection, transaction,
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalWriteOperations_TaskId_Provider ON ExternalWriteOperations (TaskId, Provider)");

        transaction.Commit();
    }

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
        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_MemoryNodes_CurrentProjectContext
            ON MemoryNodes (Scope, ScopeKey, CreatedAt DESC, Id DESC)
            WHERE ValidTo IS NULL AND SupersededById IS NULL
            """);
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Observations_MemoryNodeId ON Observations (MemoryNodeId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_MemoryRelations_FromType_FromId ON MemoryRelations (FromType, FromId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_MemoryRelations_ToType_ToId ON MemoryRelations (ToType, ToId)");

        transaction.Commit();
    }

    /// <summary>
    /// Ensure the FTS5 keyword index <c>fts_memory</c> exists. No-op on non-SQLite providers. This is
    /// a SQLite virtual table (FTS5, built into the pinned <c>SQLitePCLRaw.lib.e_sqlite3</c>, #214 — no
    /// new dependency), so it is NOT part of <see cref="AppDbContext"/>.EnsureCreated()'s EF model and
    /// must be patched in separately (alongside <see cref="EnsureMemoryTables(AppDbContext)"/>). Opens
    /// and closes the context connection like the other startup patchers.
    /// </summary>
    public static void EnsureMemoryFtsTable(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsSqlite())
            return;

        db.Database.OpenConnection();
        Exception? patchException = null;
        try
        {
            EnsureMemoryFtsTable(db.Database.GetDbConnection());
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
    /// Connection-level worker: creates the standalone FTS5 table <c>fts_memory(content)</c> against
    /// an already-open connection. Public so tests can drive it directly against a raw SQLite
    /// connection. Safe to call repeatedly (<c>IF NOT EXISTS</c>). The single <c>content</c> column is
    /// indexed by <c>rowid</c>, which the dual-write (<see cref="Services.MemoryService"/>) and the
    /// reconciler keep aligned 1:1 with <see cref="Models.MemoryNode.Id"/>. The <c>unicode61</c>
    /// tokenizer with <c>remove_diacritics 2</c> gives accent-insensitive matching.
    /// </summary>
    public static void EnsureMemoryFtsTable(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction,
            "CREATE VIRTUAL TABLE IF NOT EXISTS fts_memory USING fts5(content, tokenize='unicode61 remove_diacritics 2')");

        transaction.Commit();
    }

    /// <summary>
    /// Ensure the schema added by bd1c855 ("recursive nesting for features and subtasks") exists on
    /// databases created before it: the <c>Projects.Readme</c>, <c>Features.ParentFeatureId</c> and
    /// <c>Subtasks.ParentSubtaskId</c> columns and the <c>Requirements</c> and <c>TaskComments</c>
    /// tables. That commit shipped without a patcher, and EnsureCreated() is a no-op on an existing
    /// database, so a board created before it answered <c>GET /api/projects</c> with a 500 ("no such
    /// column: p.Readme"). No-op on non-SQLite providers.
    /// </summary>
    public static void EnsureNestingSchema(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsSqlite())
            return;

        db.Database.OpenConnection();
        Exception? patchException = null;
        try
        {
            EnsureNestingSchema(db.Database.GetDbConnection());
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
    /// Connection-level worker for <see cref="EnsureNestingSchema(AppDbContext)"/>. Column types,
    /// nullability, foreign keys (with their ON DELETE behavior) and index names match what
    /// <see cref="AppDbContext"/>.EnsureCreated() produces, so a patched database and a fresh one
    /// converge. An added column lands at the end of its table (SQLite <c>ALTER TABLE ADD COLUMN</c>),
    /// which EF does not depend on. Every statement is idempotent and no existing row is modified.
    /// </summary>
    public static void EnsureNestingSchema(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        AddColumnIfMissing(connection, transaction, "Projects", "Readme", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "Features", "ParentFeatureId",
            "INTEGER NULL REFERENCES Features (Id) ON DELETE RESTRICT");
        AddColumnIfMissing(connection, transaction, "Subtasks", "ParentSubtaskId",
            "INTEGER NULL REFERENCES Subtasks (Id) ON DELETE RESTRICT");

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Requirements (
                Id INTEGER NOT NULL CONSTRAINT PK_Requirements PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL,
                Text TEXT NOT NULL,
                Description TEXT NULL,
                Priority INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                FeatureId INTEGER NULL,
                TaskItemId INTEGER NULL,
                SortOrder INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CONSTRAINT FK_Requirements_Features_FeatureId FOREIGN KEY (FeatureId)
                    REFERENCES Features (Id) ON DELETE SET NULL,
                CONSTRAINT FK_Requirements_Projects_ProjectId FOREIGN KEY (ProjectId)
                    REFERENCES Projects (Id) ON DELETE CASCADE,
                CONSTRAINT FK_Requirements_Tasks_TaskItemId FOREIGN KEY (TaskItemId)
                    REFERENCES Tasks (Id) ON DELETE SET NULL
            )
            """);

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS TaskComments (
                Id INTEGER NOT NULL CONSTRAINT PK_TaskComments PRIMARY KEY AUTOINCREMENT,
                TaskItemId INTEGER NOT NULL,
                Text TEXT NOT NULL,
                Author TEXT NULL,
                Type INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_TaskComments_Tasks_TaskItemId FOREIGN KEY (TaskItemId)
                    REFERENCES Tasks (Id) ON DELETE CASCADE
            )
            """);

        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Features_ParentFeatureId ON Features (ParentFeatureId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Subtasks_ParentSubtaskId ON Subtasks (ParentSubtaskId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Requirements_FeatureId ON Requirements (FeatureId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Requirements_ProjectId ON Requirements (ProjectId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_Requirements_TaskItemId ON Requirements (TaskItemId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_TaskComments_TaskItemId ON TaskComments (TaskItemId)");

        transaction.Commit();
    }

    /// <summary>
    /// Adds <paramref name="column"/> to <paramref name="table"/> unless <c>PRAGMA table_info</c>
    /// already lists it, so the patch is a no-op on fresh and already-patched databases.
    /// </summary>
    private static void AddColumnIfMissing(
        DbConnection connection, DbTransaction transaction, string table, string column, string definition)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = $"PRAGMA table_info('{table}')";
            using var reader = probe.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void Execute(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
