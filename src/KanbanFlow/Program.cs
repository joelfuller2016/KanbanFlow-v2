using KanbanFlow;
using KanbanFlow.Components;
using KanbanFlow.Data;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database
var dbPath = Environment.GetEnvironmentVariable("KANBANFLOW_DB_PATH");
if (string.IsNullOrWhiteSpace(dbPath))
{
    dbPath = Path.Combine(Environment.CurrentDirectory, "kanbanflow.db");
}
else
{
    dbPath = Path.GetFullPath(dbPath);
    var dbDirectory = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrWhiteSpace(dbDirectory))
        Directory.CreateDirectory(dbDirectory);
}

builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

// Audit-aware service layer (issue #88: route REST + UI mutations through one place)
builder.Services.AddScoped<ITaskService, TaskService>();

// In-process test runner (Settings panel + /api/admin/run-tests endpoint)
builder.Services.AddHttpClient();
builder.Services.AddScoped<ITestRunnerService, TestRunnerService>();

// Markdown rendering (R-4.2: server-rendered, sanitized)
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

// OpenAPI document generation (R-5.1: /openapi.json discovery)
builder.Services.AddOpenApi();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// CORS for AI agents
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Create DB + seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    EnsureTaskClaimColumns(db);
    EnsureTaskDependencyTable(db);
    EnsureAcceptanceCriteriaTable(db);
    EnsureActivityEventTable(db);
    SeedData.Initialize(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseCors();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// OpenAPI: serve at /openapi/v1.json (default) and redirect /openapi.json → /openapi/v1.json
// so the README-documented path works for agents doing discovery.
app.MapOpenApi();
app.MapGet("/openapi.json", (HttpContext ctx) =>
{
    ctx.Response.Redirect("/openapi/v1.json", permanent: false);
    return Task.CompletedTask;
});

app.MapApiEndpoints();
app.MapAdminEndpoints();

app.Run();

static void EnsureTaskDependencyTable(AppDbContext db)
{
    if (!db.Database.IsSqlite())
        return;

    db.Database.OpenConnection();
    try
    {
        var connection = db.Database.GetDbConnection();
        using var createTable = connection.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS TaskDependencies (
                Id INTEGER NOT NULL CONSTRAINT PK_TaskDependencies PRIMARY KEY AUTOINCREMENT,
                ProjectId INTEGER NOT NULL,
                TaskItemId INTEGER NOT NULL,
                DependsOnTaskItemId INTEGER NOT NULL,
                Type INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_TaskDependencies_Projects_ProjectId FOREIGN KEY (ProjectId) REFERENCES Projects (Id) ON DELETE CASCADE,
                CONSTRAINT FK_TaskDependencies_Tasks_DependsOnTaskItemId FOREIGN KEY (DependsOnTaskItemId) REFERENCES Tasks (Id) ON DELETE CASCADE,
                CONSTRAINT FK_TaskDependencies_Tasks_TaskItemId FOREIGN KEY (TaskItemId) REFERENCES Tasks (Id) ON DELETE CASCADE
            )
            """;
        createTable.ExecuteNonQuery();

        using var createUniqueIndex = connection.CreateCommand();
        createUniqueIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskDependencies_TaskItemId_DependsOnTaskItemId ON TaskDependencies (TaskItemId, DependsOnTaskItemId)";
        createUniqueIndex.ExecuteNonQuery();

        using var createProjectIndex = connection.CreateCommand();
        createProjectIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_TaskDependencies_ProjectId ON TaskDependencies (ProjectId)";
        createProjectIndex.ExecuteNonQuery();

        using var createDependsOnIndex = connection.CreateCommand();
        createDependsOnIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_TaskDependencies_DependsOnTaskItemId ON TaskDependencies (DependsOnTaskItemId)";
        createDependsOnIndex.ExecuteNonQuery();
    }
    finally
    {
        db.Database.CloseConnection();
    }
}
static void EnsureAcceptanceCriteriaTable(AppDbContext db)
{
    if (!db.Database.IsSqlite())
        return;

    db.Database.OpenConnection();
    try
    {
        var connection = db.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS AcceptanceCriteria (
    Id INTEGER NOT NULL CONSTRAINT PK_AcceptanceCriteria PRIMARY KEY AUTOINCREMENT,
    TaskItemId INTEGER NOT NULL,
    Text TEXT NOT NULL,
    Completed INTEGER NOT NULL,
    SortOrder INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CONSTRAINT FK_AcceptanceCriteria_Tasks_TaskItemId FOREIGN KEY (TaskItemId) REFERENCES Tasks (Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_AcceptanceCriteria_TaskItemId ON AcceptanceCriteria (TaskItemId);";
        command.ExecuteNonQuery();
    }
    finally
    {
        db.Database.CloseConnection();
    }
}

static void EnsureActivityEventTable(AppDbContext db)
{
    if (!db.Database.IsSqlite())
        return;

    const string createActivityEventsTableSql = """
        CREATE TABLE IF NOT EXISTS ActivityEvents (
            Id INTEGER NOT NULL CONSTRAINT PK_ActivityEvents PRIMARY KEY AUTOINCREMENT,
            ProjectId INTEGER NOT NULL,
            EntityType TEXT NOT NULL,
            EntityId INTEGER NOT NULL,
            Actor TEXT NULL,
            EventType TEXT NOT NULL,
            BeforeJson TEXT NULL,
            AfterJson TEXT NULL,
            Message TEXT NULL,
            CreatedAt TEXT NOT NULL
        )
        """;

    db.Database.OpenConnection();
    try
    {
        var connection = db.Database.GetDbConnection();
        var activityEventsTableExists = false;
        using (var tableExists = connection.CreateCommand())
        {
            tableExists.CommandText = """
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = 'ActivityEvents'
                LIMIT 1
                """;
            activityEventsTableExists = tableExists.ExecuteScalar() is not null;
        }

        if (!activityEventsTableExists)
        {
            using var createTable = connection.CreateCommand();
            createTable.CommandText = createActivityEventsTableSql;
            createTable.ExecuteNonQuery();
        }
        else if (ActivityEventsHasProjectForeignKey(connection))
        {
            using var migrateTable = connection.CreateCommand();
            migrateTable.CommandText = """
                BEGIN TRANSACTION;

                CREATE TABLE ActivityEvents__new (
                    Id INTEGER NOT NULL CONSTRAINT PK_ActivityEvents PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL,
                    EntityType TEXT NOT NULL,
                    EntityId INTEGER NOT NULL,
                    Actor TEXT NULL,
                    EventType TEXT NOT NULL,
                    BeforeJson TEXT NULL,
                    AfterJson TEXT NULL,
                    Message TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );

                INSERT INTO ActivityEvents__new (Id, ProjectId, EntityType, EntityId, Actor, EventType, BeforeJson, AfterJson, Message, CreatedAt)
                SELECT Id, ProjectId, EntityType, EntityId, Actor, EventType, BeforeJson, AfterJson, Message, CreatedAt
                FROM ActivityEvents;

                DROP TABLE ActivityEvents;
                ALTER TABLE ActivityEvents__new RENAME TO ActivityEvents;

                COMMIT;
                """;
            migrateTable.ExecuteNonQuery();
        }

        using var projectIndex = connection.CreateCommand();
        projectIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_ActivityEvents_ProjectId_CreatedAt ON ActivityEvents (ProjectId, CreatedAt)";
        projectIndex.ExecuteNonQuery();

        using var entityIndex = connection.CreateCommand();
        entityIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_ActivityEvents_EntityType_EntityId_CreatedAt ON ActivityEvents (EntityType, EntityId, CreatedAt)";
        entityIndex.ExecuteNonQuery();
    }
    finally
    {
        db.Database.CloseConnection();
    }
}

static bool ActivityEventsHasProjectForeignKey(System.Data.Common.DbConnection connection)
{
    using var foreignKeys = connection.CreateCommand();
    foreignKeys.CommandText = "PRAGMA foreign_key_list('ActivityEvents')";
    using var reader = foreignKeys.ExecuteReader();
    while (reader.Read())
    {
        var referencedTable = reader.GetString(2);
        if (string.Equals(referencedTable, "Projects", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static void EnsureTaskClaimColumns(AppDbContext db)
{
    if (!db.Database.IsSqlite())
        return;

    db.Database.OpenConnection();
    try
    {
        var connection = db.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('Tasks')";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("ClaimedBy"))
        {
            using var addClaimedBy = connection.CreateCommand();
            addClaimedBy.CommandText = "ALTER TABLE Tasks ADD COLUMN ClaimedBy TEXT";
            addClaimedBy.ExecuteNonQuery();
        }

        if (!columns.Contains("ClaimedAt"))
        {
            using var addClaimedAt = connection.CreateCommand();
            addClaimedAt.CommandText = "ALTER TABLE Tasks ADD COLUMN ClaimedAt TEXT";
            addClaimedAt.ExecuteNonQuery();
        }
    }
    finally
    {
        db.Database.CloseConnection();
    }
}
