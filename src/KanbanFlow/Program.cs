using System.ClientModel;
using System.ClientModel.Primitives;
using KanbanFlow;
using KanbanFlow.Components;
using KanbanFlow.Data;
using KanbanFlow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;

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
// Ideas inbox service (#180) — same single-audit-boundary rule as ITaskService.
builder.Services.AddScoped<IIdeaService, IdeaService>();
// Bi-temporal memory store (#221) — same single-audit-boundary rule.
builder.Services.AddScoped<IMemoryService, MemoryService>();
// Brute-force cosine vector search over MemoryNode.EmbeddingBlob (#222, retargeted by the #249
// spike): NO sqlite-vec/vec0/native extension — EF + EmbeddingBlob is the source of truth and the
// cosine is computed in C# over the tiny current-node corpus. Same IDbContextFactory pattern as the
// memory store; #223 fuses it with FTS5 + RRF.
builder.Services.AddScoped<IVectorSearch, VectorSearch>();
// Hybrid memory retrieval (#223): fuses FTS5 BM25 + the brute-force cosine IVectorSearch + recency +
// usefulness via Reciprocal Rank Fusion. Still NO sqlite-vec/vec0/SK connector — FTS5 ships with the
// pinned e_sqlite3 (#214). Same IDbContextFactory pattern; consumed by the memory MCP/REST surface (#224).
builder.Services.AddScoped<IMemorySearch, MemorySearch>();

// Real process start time, captured once at app boot. Construct eagerly so the
// timestamp captured by AppStartupInfo's constructor is the boot time, not the
// time of the first request that happens to resolve the singleton lazily.
builder.Services.AddSingleton<IAppStartupInfo>(new AppStartupInfo());

// In-process test runner (Settings panel + /api/admin/run-tests endpoint)
builder.Services.AddHttpClient();
builder.Services.AddScoped<ITestRunnerService, TestRunnerService>();

// Markdown rendering (R-4.2: server-rendered, sanitized)
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

// LLM foundation (#218): provider-agnostic chat completions via ZeroRouter's OpenAI-compatible
// API. Config is read from the environment the same way KANBANFLOW_DB_PATH is above (see
// LlmOptions.FromEnvironment). The IChatClient is constructed once from the OpenAI connector and
// is thread-safe + stateless, so it and the ILlmClient wrapper are singletons — the OpenAI client
// pools connections internally, so we never create a per-call HttpClient (no socket exhaustion).
var llmOptions = LlmOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
            new ApiKeyCredential(llmOptions.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(llmOptions.BaseUrl),
                // LlmClient owns resilience (per-attempt timeout + a single transient retry, #218).
                // Disable the OpenAI SDK's own retry so we don't double-retry: with the default
                // 3 internal retries + backoff, a provider-down ping took ~16s and contradicted the
                // documented single-retry contract. 0 here ⇒ one transport attempt per LlmClient try.
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
            })
        .GetChatClient(llmOptions.Model)
        .AsIChatClient());
builder.Services.AddSingleton<ILlmClient, LlmClient>();

// Embeddings foundation (#219): provider-agnostic text embeddings via the SAME ZeroRouter
// OpenAI-compatible endpoint as chat. EmbeddingOptions reuses LlmOptions' URL/key/timeout binding
// (single-sourced endpoint resolution) and adds the embed model + dimensionality. The
// IEmbeddingGenerator is built once from the OpenAI connector (thread-safe, pools connections) and,
// like the chat client, has the SDK's own retry disabled so EmbeddingService owns resilience
// (per-attempt timeout + a single transient retry). Generator + service are singletons; the
// service's content-hash cache means identical text is embedded once.
var embeddingOptions = EmbeddingOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(embeddingOptions);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
    new OpenAIClient(
            new ApiKeyCredential(embeddingOptions.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(embeddingOptions.BaseUrl),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
            })
        .GetEmbeddingClient(embeddingOptions.Model)
        .AsIEmbeddingGenerator());
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();

// OpenAPI document generation (R-5.1: /openapi.json discovery)
builder.Services.AddOpenApi();

// Native in-process MCP server (prototype) — co-hosted with the REST API + Blazor in
// the same Kestrel process. Tools call the service layer / EF Core DIRECTLY via DI
// (no HTTP hop), unlike the Node wrapper (scripts/mcp-server.mjs), which stays as a
// stdio fallback. Streamable HTTP transport is mapped at /mcp below. Stateless mode
// keeps it simple for local agent clients (no session handshake required).
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

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
    EnsureIdeaTable(db);
    SchemaPatcher.EnsureMemoryTables(db);
    SchemaPatcher.EnsureMemoryFtsTable(db);
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

// Native MCP server (Streamable HTTP), prototype — agents connect at POST /mcp.
// Open access by design: no host/loopback restriction — trust the network (single-user
// local tool), matching the unrestricted REST API + open CORS. Hardening deferred to #84.
app.MapMcp("/mcp");

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

static void EnsureIdeaTable(AppDbContext db)
{
    if (!db.Database.IsSqlite())
        return;

    // Ideas is a new table (#180). EnsureCreated() builds it for fresh databases; this
    // patcher adds it (idempotently) to databases created before the feature landed —
    // same Ensure* pattern as the other tables (adds to the EF-migrations debt, #86).
    const string createIdeasTableSql = """
        CREATE TABLE IF NOT EXISTS Ideas (
            Id INTEGER NOT NULL CONSTRAINT PK_Ideas PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Body TEXT NULL,
            Plan TEXT NULL,
            Status INTEGER NOT NULL,
            Tags TEXT NULL,
            Priority INTEGER NULL,
            PromotedProjectId INTEGER NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            CONSTRAINT FK_Ideas_Projects_PromotedProjectId FOREIGN KEY (PromotedProjectId)
                REFERENCES Projects (Id) ON DELETE SET NULL
        )
        """;

    db.Database.OpenConnection();
    try
    {
        var connection = db.Database.GetDbConnection();

        using (var createTable = connection.CreateCommand())
        {
            createTable.CommandText = createIdeasTableSql;
            createTable.ExecuteNonQuery();
        }

        using var statusIndex = connection.CreateCommand();
        statusIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Ideas_Status_CreatedAt ON Ideas (Status, CreatedAt)";
        statusIndex.ExecuteNonQuery();

        using var fkIndex = connection.CreateCommand();
        fkIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Ideas_PromotedProjectId ON Ideas (PromotedProjectId)";
        fkIndex.ExecuteNonQuery();
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
