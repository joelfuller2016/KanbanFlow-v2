using KanbanFlow;
using KanbanFlow.Components;
using KanbanFlow.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database
var dbPath = Path.Combine(Environment.CurrentDirectory, "kanbanflow.db");
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

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

app.MapApiEndpoints();

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
