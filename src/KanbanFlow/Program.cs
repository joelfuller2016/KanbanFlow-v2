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
