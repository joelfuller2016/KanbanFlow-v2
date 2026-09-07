using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Data;

/// <summary>Additive upgrade for the existing SQLite board. Never rebuilds existing tables.</summary>
public static class WorkflowSchema
{
    private static readonly HashSet<string> Tables = ["WorkRequests", "RequestMessages", "BriefRevisions",
        "PlanVersions", "PlannedTasks", "TaskEvidence", "RequestResults", "ProjectRepositories", "WorkflowReceipts"];

    public static void EnsureTables(AppDbContext db)
    {
        if (!db.Database.IsSqlite()) return;
        // DDL comes from the local EF model, never from requests or configuration. Restrict it
        // to this feature's new tables and indexes so legacy tables/FTS triggers are untouched.
        var statements = db.Database.GenerateCreateScript().Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var selected = new List<string>();
        var found = new HashSet<string>();
        foreach (var statement in statements)
        {
            var table = Regex.Match(statement, "^CREATE TABLE \"([^\"]+)\"");
            if (table.Success && Tables.Contains(table.Groups[1].Value))
            {
                found.Add(table.Groups[1].Value);
                selected.Add(statement.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
                continue;
            }
            var index = Regex.Match(statement, "^CREATE (?:UNIQUE )?INDEX \"[^\"]+\" ON \"([^\"]+)\"");
            if (index.Success && Tables.Contains(index.Groups[1].Value))
                selected.Add(statement.Replace("INDEX ", "INDEX IF NOT EXISTS ", StringComparison.Ordinal));
        }
        if (!found.SetEquals(Tables)) throw new InvalidOperationException("The workflow schema does not match the expected additive tables.");
        using var transaction = db.Database.BeginTransaction();
        foreach (var statement in selected)
        {
#pragma warning disable EF1002 // Allowlisted DDL generated exclusively from the application's EF model.
            db.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
        transaction.Commit();
    }
}
