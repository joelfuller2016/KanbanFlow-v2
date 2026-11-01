using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

/// <summary>Additive startup schema for existing databases; no source-row foreign keys or destructive migration.</summary>
public static class IdeaWorkspaceSchema
{
    public static void EnsureTables(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!db.Database.IsSqlite())
            return;
        var wasOpen = db.Database.GetDbConnection().State == ConnectionState.Open;
        if (!wasOpen)
            db.Database.OpenConnection();
        try
        {
            EnsureTables(db.Database.GetDbConnection());
        }
        finally
        {
            if (!wasOpen)
                db.Database.CloseConnection();
        }
    }

    public static void EnsureTables(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS IdeaWorkspaceStates (
                WorkspaceId TEXT NOT NULL CONSTRAINT PK_IdeaWorkspaceStates PRIMARY KEY,
                Kind TEXT NOT NULL,
                SourceId INTEGER NOT NULL,
                LinkedProjectId INTEGER NULL,
                Revision INTEGER NOT NULL,
                OriginalSourceJson TEXT NOT NULL,
                DocumentJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )
            """);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS IdeaWorkspaceReceipts (
                WorkspaceId TEXT NOT NULL,
                OperationId TEXT NOT NULL,
                RequestHash TEXT NOT NULL,
                ResultJson TEXT NOT NULL,
                Actor TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT PK_IdeaWorkspaceReceipts PRIMARY KEY (WorkspaceId, OperationId)
            )
            """);
        Execute(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS IX_IdeaWorkspaceStates_Kind_SourceId ON IdeaWorkspaceStates (Kind, SourceId)");
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_IdeaWorkspaceStates_LinkedProjectId ON IdeaWorkspaceStates (LinkedProjectId)");
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
