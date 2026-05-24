using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KanbanFlow.Data;
using KanbanFlow.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;

namespace KanbanFlow.Services;

public sealed class TestRunnerService(
    IHttpClientFactory httpClientFactory,
    IServer server,
    ITaskService taskSvc,
    IDbContextFactory<AppDbContext> dbFactory,
    IMarkdownRenderer markdownRenderer) : ITestRunnerService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TestSuiteResult> RunApiTestsAsync(CancellationToken ct = default)
    {
        var runStart = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        using var http = CreateHttpClient();
        var results = new List<TestResult>();

        try
        {
        // Each test creates an isolated test project (named with a random GUID prefix)
        // and deletes it on completion. This means tests can run in parallel against the
        // live DB without interfering with the user's real projects.

        results.Add(await RunTest("List projects returns an array", "API:Project", async () =>
        {
            var projects = await http.GetFromJsonAsync<JsonElement>("/api/projects", Json, ct);
            if (projects.ValueKind != JsonValueKind.Array)
                throw new Exception($"Expected JSON array, got {projects.ValueKind}");
        }));

        results.Add(await RunTest("Create + delete project", "API:Project", async () =>
        {
            var name = NewName("Test-Project");
            var resp = await http.PostAsJsonAsync("/api/projects", new { name }, Json, ct);
            if (resp.StatusCode != HttpStatusCode.Created)
                throw new Exception($"POST /api/projects expected 201, got {(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var id = body.GetProperty("id").GetInt32();
            var del = await http.DeleteAsync($"/api/projects/{id}", ct);
            if (del.StatusCode != HttpStatusCode.NoContent)
                throw new Exception($"DELETE /api/projects/{id} expected 204, got {(int)del.StatusCode}");
        }));

        results.Add(await RunTest("Create project with empty name returns 422", "API:Project", async () =>
        {
            var resp = await http.PostAsJsonAsync("/api/projects", new { name = "" }, Json, ct);
            if (resp.StatusCode != HttpStatusCode.UnprocessableEntity)
                throw new Exception($"Expected 422, got {(int)resp.StatusCode}");
        }));

        // For the rest of the suite, create a scratch project we'll reuse + delete at the end.
        int? scratchProjectId = null;
        try
        {
            var (pid, _, _) = await CreateProjectFeatureTaskAsync(http, ct);
            scratchProjectId = pid;

            results.Add(await RunTest("Get project by id returns full shape", "API:Project", async () =>
            {
                var body = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}", Json, ct);
                if (!body.TryGetProperty("id", out _) || !body.TryGetProperty("features", out _))
                    throw new Exception("Missing id or features field on response");
            }));

            results.Add(await RunTest("Project context returns AI-shaped bundle", "API:AI", async () =>
            {
                var body = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/context", Json, ct);
                foreach (var key in new[] { "project", "summary", "features" })
                {
                    if (!body.TryGetProperty(key, out _))
                        throw new Exception($"Context missing '{key}' field");
                }
            }));

            results.Add(await RunTest("Project board returns 5 status columns", "API:Board", async () =>
            {
                var body = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/board", Json, ct);
                foreach (var status in new[] { "Backlog", "Todo", "InProgress", "Review", "Done" })
                {
                    if (!body.TryGetProperty(status, out _))
                        throw new Exception($"Board missing '{status}' column");
                }
            }));

            results.Add(await RunTest("Next-tasks returns recommendations with reason", "API:AI", async () =>
            {
                var body = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}/next-tasks?limit=3", Json, ct);
                if (body.ValueKind != JsonValueKind.Object)
                    throw new Exception($"Expected object, got {body.ValueKind}");
                foreach (var key in new[] { "projectId", "recommendations", "blockedTasks" })
                {
                    if (!body.TryGetProperty(key, out _))
                        throw new Exception($"Response missing '{key}' field");
                }
                var recs = body.GetProperty("recommendations");
                if (recs.ValueKind != JsonValueKind.Array)
                    throw new Exception($"recommendations is not an array (got {recs.ValueKind})");
                if (recs.GetArrayLength() == 0) return; // empty recommendations is valid for this scratch project
                var first = recs[0];
                if (!first.TryGetProperty("reason", out var reason) || string.IsNullOrWhiteSpace(reason.GetString()))
                    throw new Exception("Top recommendation missing 'reason'");
            }));

            results.Add(await RunTest("Status change writes activity event + comment", "API:Service-layer", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var resp = await http.PatchAsJsonAsync($"/api/tasks/{taskId}/status",
                        new { status = "InProgress", actor = "test-runner" }, Json, ct);
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception($"PATCH /api/tasks/{taskId}/status returned {(int)resp.StatusCode}");

                    var activity = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/activity", Json, ct);
                    var hasStatusChanged = false;
                    var actorMatches = false;
                    foreach (var ev in activity.EnumerateArray())
                    {
                        if (ev.GetProperty("eventType").GetString() == "TaskStatusChanged")
                        {
                            hasStatusChanged = true;
                            actorMatches = ev.GetProperty("actor").GetString() == "test-runner";
                        }
                    }
                    if (!hasStatusChanged) throw new Exception("Activity log missing TaskStatusChanged event");
                    if (!actorMatches) throw new Exception("TaskStatusChanged event has wrong Actor");

                    var comments = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/comments", Json, ct);
                    var hasStatusComment = false;
                    foreach (var c in comments.EnumerateArray())
                    {
                        if (c.GetProperty("type").GetString() == "StatusChange")
                            hasStatusComment = true;
                    }
                    if (!hasStatusComment) throw new Exception("StatusChange comment not created (issue #88 audit-trail contract)");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Invalid status (numeric) returns 422", "API:Validation", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var resp = await http.PatchAsJsonAsync($"/api/tasks/{taskId}/status", new { status = "99" }, Json, ct);
                    if (resp.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"Expected 422 for numeric status, got {(int)resp.StatusCode} (issue #89)");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Claim then re-claim by same actor is idempotent", "API:Claim", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var first = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim",
                        new { actor = "alice" }, Json, ct);
                    if (!first.IsSuccessStatusCode)
                        throw new Exception($"First claim failed: {(int)first.StatusCode}");
                    var second = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim",
                        new { actor = "alice" }, Json, ct);
                    if (!second.IsSuccessStatusCode)
                        throw new Exception($"Re-claim by same actor should succeed, got {(int)second.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Claim conflict returns 409 for different actor", "API:Claim", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim", new { actor = "alice" }, Json, ct);
                    var second = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim",
                        new { actor = "bob" }, Json, ct);
                    if (second.StatusCode != HttpStatusCode.Conflict)
                        throw new Exception($"Expected 409 for cross-actor claim, got {(int)second.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Add comment with valid Type", "API:Comment", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var resp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/comments",
                        new { text = "test note", author = "test-runner", type = "Note" }, Json, ct);
                    if (resp.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Expected 201, got {(int)resp.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Add comment with disallowed Type returns 422", "API:Comment", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var resp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/comments",
                        new { text = "system", author = "x", type = "StatusChange" }, Json, ct);
                    if (resp.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"Expected 422 for system-typed comment, got {(int)resp.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Step context returns rich AI bundle", "API:AI", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var body = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/context", Json, ct);
                    foreach (var key in new[] { "task", "feature", "acceptanceCriteria", "subtasks", "dependencies", "openBlockerCount", "isBlocked", "recentComments", "relatedRequirements", "recentActivity" })
                    {
                        if (!body.TryGetProperty(key, out _))
                            throw new Exception($"Step context missing '{key}' field");
                    }
                    if (body.GetProperty("task").GetProperty("id").GetInt32() != taskId)
                        throw new Exception("Step context's task.id does not match requested taskId");
                    if (body.GetProperty("feature").GetProperty("projectId").GetInt32() != innerPid)
                        throw new Exception("Step context's feature.projectId does not match parent project");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            results.Add(await RunTest("Step context for unknown task returns 404", "API:AI", async () =>
            {
                var resp = await http.GetAsync("/api/tasks/99999999/context", ct);
                if (resp.StatusCode != HttpStatusCode.NotFound)
                    throw new Exception($"Expected 404 for unknown task, got {(int)resp.StatusCode}");
            }));

            results.Add(await RunTest("Acceptance criterion CRUD round-trip", "API:AC", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var create = await http.PostAsJsonAsync($"/api/tasks/{taskId}/acceptance-criteria",
                        new { text = "Done means..." }, Json, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Expected 201, got {(int)create.StatusCode}");
                    var body = await create.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                    var acId = body.GetProperty("id").GetInt32();

                    var update = await http.PutAsJsonAsync($"/api/acceptance-criteria/{acId}",
                        new { text = "Done means... updated", completed = true }, Json, ct);
                    if (!update.IsSuccessStatusCode)
                        throw new Exception($"PUT failed: {(int)update.StatusCode}");

                    // Verify the round-trip actually persisted both fields. The prior version
                    // sent `met` instead of `completed`; JSON silently dropped the unknown field
                    // so the test was passing while testing nothing about the update.
                    var refreshed = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/acceptance-criteria", Json, ct);
                    JsonElement? roundTripped = null;
                    foreach (var item in refreshed.EnumerateArray())
                    {
                        if (item.GetProperty("id").GetInt32() == acId)
                        {
                            roundTripped = item;
                            break;
                        }
                    }
                    if (roundTripped is null)
                        throw new Exception("Updated criterion not returned by GET");
                    if (roundTripped.Value.GetProperty("text").GetString() != "Done means... updated")
                        throw new Exception($"Text not persisted, got: {roundTripped.Value.GetProperty("text").GetString()}");
                    if (!roundTripped.Value.GetProperty("completed").GetBoolean())
                        throw new Exception("Completed flag not persisted as true");

                    var del = await http.DeleteAsync($"/api/acceptance-criteria/{acId}", ct);
                    if (del.StatusCode != HttpStatusCode.NoContent)
                        throw new Exception($"DELETE failed: {(int)del.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid, ct);
                }
            }));

            // ── Added coverage: claim release, dependency blocking, requirements CRUD ──

            results.Add(await RunTest("Claim release frees task for another actor", "API:Claim", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var claim = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim", new { actor = "alice" }, Json, ct);
                    if (!claim.IsSuccessStatusCode)
                        throw new Exception($"Claim by alice failed: {(int)claim.StatusCode}");

                    // bob is locked out while alice holds the claim
                    var bobBlocked = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim", new { actor = "bob" }, Json, ct);
                    if (bobBlocked.StatusCode != HttpStatusCode.Conflict)
                        throw new Exception($"Expected 409 for bob before release, got {(int)bobBlocked.StatusCode}");

                    var release = await http.PostAsJsonAsync($"/api/tasks/{taskId}/release", new { actor = "alice" }, Json, ct);
                    if (!release.IsSuccessStatusCode)
                        throw new Exception($"Release by alice failed: {(int)release.StatusCode}");

                    // after release the task is claimable by a different actor
                    var bobClaim = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim", new { actor = "bob" }, Json, ct);
                    if (!bobClaim.IsSuccessStatusCode)
                        throw new Exception($"Bob should claim after release, got {(int)bobClaim.StatusCode}");

                    var task = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}", Json, ct);
                    if (!task.TryGetProperty("claimedBy", out var cb) || cb.ValueKind != JsonValueKind.String)
                        throw new Exception("Expected claimedBy to be a string after re-claim (task not claimed?)");
                    if (cb.GetString() != "bob")
                        throw new Exception($"Expected claimedBy 'bob' after re-claim, got '{cb.GetString()}'");
                }
                finally { await TryDeleteProjectAsync(http, innerPid, ct); }
            }));

            results.Add(await RunTest("Dependency blocks next-tasks until blocker is Done", "API:AI", async () =>
            {
                var (innerPid, featureId, blockerId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var t2 = await http.PostAsJsonAsync($"/api/features/{featureId}/tasks",
                        new { name = "Blocked task", status = "Todo", priority = "Medium" }, Json, ct);
                    if (t2.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create blocked task expected 201, got {(int)t2.StatusCode}");
                    var blockedId = (await t2.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("id").GetInt32();

                    // blockedId depends on blockerId; blocker is not Done yet
                    var dep = await http.PostAsJsonAsync($"/api/tasks/{blockedId}/dependencies",
                        new { dependsOnTaskItemId = blockerId, type = "DependsOn" }, Json, ct);
                    if (dep.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create dependency expected 201, got {(int)dep.StatusCode}");

                    bool RecsContain(JsonElement body, int id) =>
                        body.GetProperty("recommendations").EnumerateArray().Any(r => r.GetProperty("id").GetInt32() == id);
                    bool BlockedContain(JsonElement body, int id) =>
                        body.GetProperty("blockedTasks").EnumerateArray().Any(r => r.GetProperty("id").GetInt32() == id);

                    var nt = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{innerPid}/next-tasks?limit=25", Json, ct);
                    if (RecsContain(nt, blockedId)) throw new Exception("Blocked task should NOT be recommended while blocker is open");
                    if (!BlockedContain(nt, blockedId)) throw new Exception("Blocked task should appear in blockedTasks");
                    if (!RecsContain(nt, blockerId)) throw new Exception("Blocker task should be recommendable");

                    var blockedCtx = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{blockedId}/context", Json, ct);
                    if (!blockedCtx.GetProperty("isBlocked").GetBoolean()) throw new Exception("context.isBlocked should be true while blocker open");
                    if (blockedCtx.GetProperty("openBlockerCount").GetInt32() < 1) throw new Exception("context.openBlockerCount should be >= 1");

                    var patch = await http.PatchAsJsonAsync($"/api/tasks/{blockerId}/status",
                        new { status = "Done", actor = "test-runner" }, Json, ct);
                    if (!patch.IsSuccessStatusCode) throw new Exception($"Mark blocker Done failed: {(int)patch.StatusCode}");

                    var nt2 = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{innerPid}/next-tasks?limit=25", Json, ct);
                    if (!RecsContain(nt2, blockedId)) throw new Exception("Blocked task should be recommended after blocker Done");
                    if (BlockedContain(nt2, blockedId)) throw new Exception("Blocked task should no longer be in blockedTasks after blocker Done");
                    var blockedCtx2 = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{blockedId}/context", Json, ct);
                    if (blockedCtx2.GetProperty("isBlocked").GetBoolean()) throw new Exception("context.isBlocked should be false after blocker Done");
                    if (blockedCtx2.GetProperty("openBlockerCount").GetInt32() != 0) throw new Exception("context.openBlockerCount should be 0 after blocker Done");
                }
                finally { await TryDeleteProjectAsync(http, innerPid, ct); }
            }));

            results.Add(await RunTest("Requirement create / assign / delete round-trip", "API:Requirement", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
                try
                {
                    var create = await http.PostAsJsonAsync($"/api/projects/{innerPid}/requirements",
                        new { text = "Must support export", priority = "P1" }, Json, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create requirement expected 201, got {(int)create.StatusCode}");
                    var reqId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("id").GetInt32();

                    var list = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{innerPid}/requirements", Json, ct);
                    if (!list.EnumerateArray().Any(r => r.GetProperty("id").GetInt32() == reqId))
                        throw new Exception("Created requirement not returned by GET list");

                    var assign = await http.PatchAsJsonAsync($"/api/requirements/{reqId}/assign",
                        new { taskItemId = taskId }, Json, ct);
                    if (!assign.IsSuccessStatusCode)
                        throw new Exception($"Assign requirement failed: {(int)assign.StatusCode}");

                    var tctx = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/context", Json, ct);
                    if (!tctx.GetProperty("relatedRequirements").EnumerateArray().Any(r => r.GetProperty("id").GetInt32() == reqId))
                        throw new Exception("Assigned requirement not linked to task in step context");

                    var del = await http.DeleteAsync($"/api/requirements/{reqId}", ct);
                    if (del.StatusCode != HttpStatusCode.NoContent)
                        throw new Exception($"Delete requirement expected 204, got {(int)del.StatusCode}");
                    var list2 = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{innerPid}/requirements", Json, ct);
                    if (list2.EnumerateArray().Any(r => r.GetProperty("id").GetInt32() == reqId))
                        throw new Exception("Deleted requirement still present in GET list");
                }
                finally { await TryDeleteProjectAsync(http, innerPid, ct); }
            }));
        }
        finally
        {
            if (scratchProjectId is int pid)
                await TryDeleteProjectAsync(http, pid, ct);
        }

        return new TestSuiteResult("API", results, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            // Purge only this run's orphaned audit rows (test-project droppings),
            // preserving real deleted-project audit history (#85). Fixes #150.
            await TestAuditCleanup.PurgeOrphanedSinceAsync(dbFactory, runStart, ct);
        }
    }

    public async Task<TestSuiteResult> RunUiTestsAsync(CancellationToken ct = default)
    {
        var runStart = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        using var http = CreateHttpClient();
        var results = new List<TestResult>();

        try
        {
        results.Add(await RunTest("Home page renders 200 with PageTitle", "UI:Home", async () =>
        {
            var resp = await http.GetAsync("/", ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"GET / returned {(int)resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync(ct);
            if (!html.Contains("KanbanFlow", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Home page HTML missing brand string 'KanbanFlow'");
            // The home page is rendered via the Blazor SSR pipeline and includes the
            // <PageTitle> in <head> via @rendermode InteractiveServer.
            if (!html.Contains("<title", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Home page HTML missing <title> tag");
        }));

        results.Add(await RunTest("Settings page renders 200 with section markers", "UI:Settings", async () =>
        {
            var resp = await http.GetAsync("/settings", ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"GET /settings returned {(int)resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync(ct);
            if (!html.Contains("Settings", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Settings page HTML missing 'Settings' marker");
            if (!html.Contains("Run Tests", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Settings page HTML missing 'Run Tests' marker");
        }));

        results.Add(await RunTest("Project view renders 200 for a freshly-created project", "UI:ProjectView", async () =>
        {
            // Don't depend on seed project id=1 — it can be deleted by other tests
            // or future migrations. Create a scratch project, hit its route, clean up.
            var (pid, _, _) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var resp = await http.GetAsync($"/project/{pid}", ct);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"GET /project/{pid} returned {(int)resp.StatusCode}");
                var html = await resp.Content.ReadAsStringAsync(ct);
                // The page renders even before the InteractiveServer circuit fully boots.
                if (!html.Contains("KanbanFlow", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Project view HTML missing brand marker");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("Unknown route returns 404 (or app shell)", "UI:Routing", async () =>
        {
            var resp = await http.GetAsync("/this-route-does-not-exist", ct);
            // Blazor server can return either 404 (no matching component) or 200 with a default
            // routing component. Both are acceptable; what we want is that the server doesn't 5xx.
            if ((int)resp.StatusCode >= 500)
                throw new Exception($"Server errored on unknown route: {(int)resp.StatusCode}");
        }));

        results.Add(await RunTest("OpenAPI document is published at /openapi/v1.json", "UI:Discovery", async () =>
        {
            var resp = await http.GetAsync("/openapi/v1.json", ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"GET /openapi/v1.json returned {(int)resp.StatusCode}");

            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (doc.ValueKind != JsonValueKind.Object)
                throw new Exception($"Expected JSON object, got {doc.ValueKind}");

            if (!doc.TryGetProperty("openapi", out var version) || version.ValueKind != JsonValueKind.String)
                throw new Exception("Missing or non-string top-level 'openapi' field");
            var versionString = version.GetString();
            if (string.IsNullOrEmpty(versionString) || !versionString.StartsWith("3.", StringComparison.Ordinal))
                throw new Exception($"Expected OpenAPI 3.x, got '{versionString}'");

            if (!doc.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                throw new Exception("Missing 'paths' object on OpenAPI document");

            var hasApiRoute = false;
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Name.StartsWith("/api/", StringComparison.Ordinal))
                {
                    hasApiRoute = true;
                    break;
                }
            }
            if (!hasApiRoute)
                throw new Exception("OpenAPI document has no /api/* paths");

            // /openapi.json should redirect (302) to /openapi/v1.json. Use a one-off
            // HttpClient with AllowAutoRedirect = false so we can observe the 302 itself.
            using var noRedirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(GetBaseUrl()),
                Timeout = TimeSpan.FromSeconds(10)
            };
            var redirectResp = await noRedirectClient.GetAsync("/openapi.json", ct);
            if (redirectResp.StatusCode != HttpStatusCode.Found)
                throw new Exception($"Expected 302 from /openapi.json, got {(int)redirectResp.StatusCode}");
            var location = redirectResp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location) || !location.Contains("/openapi/v1.json", StringComparison.Ordinal))
                throw new Exception($"Expected Location to point at /openapi/v1.json, got '{location}'");
        }));

        return new TestSuiteResult("UI", results, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            // Purge only this run's orphaned audit rows (test-project droppings),
            // preserving real deleted-project audit history (#85). Fixes #150.
            await TestAuditCleanup.PurgeOrphanedSinceAsync(dbFactory, runStart, ct);
        }
    }

    public async Task<TestSuiteResult> RunServiceTestsAsync(CancellationToken ct = default)
    {
        var runStart = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var results = new List<TestResult>();

        // These tests use the real DI-registered ITaskService against the live DB,
        // wrapping each test in a project we delete on completion so they don't leak.
        using var http = CreateHttpClient();

        try
        {
        results.Add(await RunTest("ChangeStatusAsync writes activity + StatusChange comment", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                await taskSvc.ChangeStatusAsync(taskId, WorkStatus.InProgress, actor: "service-test", ct);
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var hasEvent = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                        && e.EventType == "TaskStatusChanged" && e.Actor == "service-test", ct);
                if (!hasEvent) throw new Exception("Activity event missing or actor not propagated");
                var hasComment = await db.TaskComments
                    .AnyAsync(c => c.TaskItemId == taskId && c.Type == CommentType.StatusChange, ct);
                if (!hasComment) throw new Exception("StatusChange comment not created");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("ChangeStatusAsync is idempotent for same-status calls", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                await taskSvc.ChangeStatusAsync(taskId, WorkStatus.InProgress, "x", ct);
                await using var db1 = await dbFactory.CreateDbContextAsync(ct);
                var eventsBefore = await db1.ActivityEvents.CountAsync(
                    e => e.EntityId == taskId && e.EventType == "TaskStatusChanged", ct);
                // Calling again with the same status should not create a duplicate event.
                await taskSvc.ChangeStatusAsync(taskId, WorkStatus.InProgress, "x", ct);
                await using var db2 = await dbFactory.CreateDbContextAsync(ct);
                var eventsAfter = await db2.ActivityEvents.CountAsync(
                    e => e.EntityId == taskId && e.EventType == "TaskStatusChanged", ct);
                if (eventsAfter != eventsBefore)
                    throw new Exception($"Idempotent same-status call wrote a duplicate event ({eventsBefore} → {eventsAfter})");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("ChangeStatusAsync sets CompletedAt on transition to Done", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var task = await taskSvc.ChangeStatusAsync(taskId, WorkStatus.Done, "x", ct);
                if (task.CompletedAt is null)
                    throw new Exception("CompletedAt should be set on transition to Done");
                var task2 = await taskSvc.ChangeStatusAsync(taskId, WorkStatus.Todo, "x", ct);
                if (task2.CompletedAt is not null)
                    throw new Exception("CompletedAt should be null when transitioning away from Done");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("ChangeStatusAsync throws TaskNotFoundException for missing id", "Service:Task", async () =>
        {
            try
            {
                await taskSvc.ChangeStatusAsync(taskId: 99999999, WorkStatus.Todo, "x", ct);
            }
            catch (TaskNotFoundException ex)
            {
                if (ex.TaskId != 99999999)
                    throw new Exception("Exception's TaskId property is wrong");
                return;
            }
            throw new Exception("Expected TaskNotFoundException, got success");
        }));

        results.Add(await RunTest("MarkdownRenderer renders bold and italic", "Service:Markdown", () =>
        {
            var html = markdownRenderer.RenderToSafeHtml("**bold** and *italic*");
            if (!html.Contains("<strong>bold</strong>", StringComparison.Ordinal))
                throw new Exception($"Expected '<strong>bold</strong>' in output, got: {html}");
            if (!html.Contains("<em>italic</em>", StringComparison.Ordinal))
                throw new Exception($"Expected '<em>italic</em>' in output, got: {html}");
            return Task.CompletedTask;
        }));

        results.Add(await RunTest("MarkdownRenderer sanitizes script tags", "Service:Markdown", () =>
        {
            var html = markdownRenderer.RenderToSafeHtml("<script>alert(1)</script>hello");
            if (html.Contains("<script", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Output should not contain <script>, got: {html}");
            if (!html.Contains("hello", StringComparison.Ordinal))
                throw new Exception($"Output should still contain 'hello', got: {html}");
            return Task.CompletedTask;
        }));

        results.Add(await RunTest("MarkdownRenderer handles empty/null", "Service:Markdown", () =>
        {
            var fromNull = markdownRenderer.RenderToSafeHtml(null);
            if (fromNull != string.Empty)
                throw new Exception($"Null input should return empty string, got: '{fromNull}'");
            var fromEmpty = markdownRenderer.RenderToSafeHtml(string.Empty);
            if (fromEmpty != string.Empty)
                throw new Exception($"Empty input should return empty string, got: '{fromEmpty}'");
            return Task.CompletedTask;
        }));

        // CreateAsync, DeleteAsync, ClaimAsync, ReleaseAsync, and AddCommentAsync are now
        // implemented (service-layer extraction wave 1). The DeleteAsync test below used to
        // assert NotImplementedException; it now asserts real deletion + audit emission.

        results.Add(await RunTest("CreateAsync writes Created comment + TaskCreated activity", "Service:Task", async () =>
        {
            // Create only the project+feature via REST; the task is authored by the service
            // under test so we exercise CreateAsync directly (not the REST create path).
            var (pid, featureId, seedTaskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var created = await taskSvc.CreateAsync(
                    new TaskCreate(featureId, "Service-created task", Priority: TaskPriority.High),
                    actor: "svc-test", ct);
                if (created.Id == seedTaskId)
                    throw new Exception("CreateAsync returned the seed task instead of a new row");
                if (created.Priority != TaskPriority.High)
                    throw new Exception($"Priority not honored (expected High, got {created.Priority})");
                if (created.FeatureId != featureId)
                    throw new Exception("Created task has the wrong FeatureId");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var rowExists = await db.Tasks.AnyAsync(t => t.Id == created.Id, ct);
                if (!rowExists) throw new Exception("Created task row not persisted");
                var hasCreatedComment = await db.TaskComments
                    .AnyAsync(c => c.TaskItemId == created.Id && c.Type == CommentType.Created, ct);
                if (!hasCreatedComment) throw new Exception("Created comment not written");
                var hasEvent = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == created.Id
                        && e.EventType == "TaskCreated" && e.Actor == "svc-test", ct);
                if (!hasEvent) throw new Exception("TaskCreated activity event missing or actor not propagated");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("CreateAsync rejects empty name + unknown feature", "Service:Task", async () =>
        {
            var (pid, featureId, _) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                try
                {
                    await taskSvc.CreateAsync(new TaskCreate(featureId, "   "), "x", ct);
                    throw new Exception("Expected ValidationException for empty name, got success");
                }
                catch (ValidationException) { /* ok */ }

                try
                {
                    await taskSvc.CreateAsync(new TaskCreate(99999999, "orphan"), "x", ct);
                    throw new Exception("Expected FeatureNotFoundException for unknown feature, got success");
                }
                catch (FeatureNotFoundException ex)
                {
                    if (ex.FeatureId != 99999999)
                        throw new Exception("FeatureNotFoundException carries the wrong FeatureId");
                }
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("DeleteAsync removes task + children and TaskDeleted audit survives", "Service:Task", async () =>
        {
            var (pid, featureId, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                // Build a 2-level subtask tree (parent → child) so the leaf-first delete
                // path (Subtask.ChildSubtasks Restrict FK) is actually exercised — see R1.
                var parentResp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/subtasks",
                    new { text = "parent subtask" }, Json, ct);
                if (parentResp.StatusCode != HttpStatusCode.Created)
                    throw new Exception($"Parent subtask create failed: {(int)parentResp.StatusCode}");
                var parentId = (await parentResp.Content.ReadFromJsonAsync<JsonElement>(Json, ct))
                    .GetProperty("id").GetInt32();
                var childResp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/subtasks",
                    new { text = "child subtask", parentSubtaskId = parentId }, Json, ct);
                if (childResp.StatusCode != HttpStatusCode.Created)
                    throw new Exception($"Child subtask create failed: {(int)childResp.StatusCode}");

                // Acceptance criterion (Cascade) + user comment (Cascade).
                await http.PostAsJsonAsync($"/api/tasks/{taskId}/acceptance-criteria",
                    new { text = "AC for delete test" }, Json, ct);
                await http.PostAsJsonAsync($"/api/tasks/{taskId}/comments",
                    new { text = "comment for delete test", author = "x", type = "Note" }, Json, ct);

                // A dependency on a second task in the same project (Cascade on both directions).
                var t2Resp = await http.PostAsJsonAsync($"/api/features/{featureId}/tasks",
                    new { name = "dependency target" }, Json, ct);
                var depTargetId = (await t2Resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct))
                    .GetProperty("id").GetInt32();
                var depResp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/dependencies",
                    new { dependsOnTaskItemId = depTargetId, type = "DependsOn" }, Json, ct);
                if (depResp.StatusCode != HttpStatusCode.Created)
                    throw new Exception($"Dependency create failed: {(int)depResp.StatusCode}");

                await taskSvc.DeleteAsync(taskId, actor: "svc-test", ct);

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                if (await db.Tasks.AnyAsync(t => t.Id == taskId, ct))
                    throw new Exception("Task row still present after DeleteAsync");
                if (await db.Subtasks.AnyAsync(s => s.TaskItemId == taskId, ct))
                    throw new Exception("Subtasks not cascade-deleted");
                if (await db.AcceptanceCriteria.AnyAsync(c => c.TaskItemId == taskId, ct))
                    throw new Exception("Acceptance criteria not deleted");
                if (await db.TaskComments.AnyAsync(c => c.TaskItemId == taskId, ct))
                    throw new Exception("Comments not cascade-deleted");
                if (await db.TaskDependencies.AnyAsync(d => d.TaskItemId == taskId || d.DependsOnTaskItemId == taskId, ct))
                    throw new Exception("Dependencies not cascade-deleted");

                // Audit row must SURVIVE the delete (ActivityEvent has no task FK).
                var auditSurvives = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                        && e.EventType == "TaskDeleted" && e.Actor == "svc-test", ct);
                if (!auditSurvives) throw new Exception("TaskDeleted audit event missing after deletion");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("DeleteAsync throws TaskNotFoundException for missing id", "Service:Task", async () =>
        {
            try
            {
                await taskSvc.DeleteAsync(99999999, "x", ct);
            }
            catch (TaskNotFoundException ex)
            {
                if (ex.TaskId != 99999999)
                    throw new Exception("TaskNotFoundException carries the wrong TaskId");
                return;
            }
            throw new Exception("Expected TaskNotFoundException, got success");
        }));

        results.Add(await RunTest("ClaimAsync idempotent for same actor; conflict for another", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var first = await taskSvc.ClaimAsync(taskId, "alice", ct);
                var firstClaimedAt = first.ClaimedAt;
                if (first.ClaimedBy != "alice" || firstClaimedAt is null)
                    throw new Exception("First claim did not set ClaimedBy/ClaimedAt");

                // Same actor re-claim is idempotent: ClaimedAt is preserved.
                var second = await taskSvc.ClaimAsync(taskId, "alice", ct);
                if (second.ClaimedAt != firstClaimedAt)
                    throw new Exception("Re-claim by same actor changed ClaimedAt (not idempotent)");

                try
                {
                    await taskSvc.ClaimAsync(taskId, "bob", ct);
                    throw new Exception("Expected TaskClaimConflictException for second actor, got success");
                }
                catch (TaskClaimConflictException ex)
                {
                    if (ex.TaskId != taskId || !string.Equals(ex.ClaimedBy, "alice", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("TaskClaimConflictException carries the wrong task/claimant");
                }

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var claimedEvents = await db.ActivityEvents.CountAsync(
                    e => e.EntityId == taskId && e.EventType == "TaskClaimed", ct);
                if (claimedEvents != 1)
                    throw new Exception($"Expected exactly 1 TaskClaimed event, got {claimedEvents}");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("ReleaseAsync clears claim + emits audit; conflict for another actor", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                await taskSvc.ClaimAsync(taskId, "alice", ct);

                // A different actor cannot release someone else's claim.
                try
                {
                    await taskSvc.ReleaseAsync(taskId, "bob", ct);
                    throw new Exception("Expected TaskClaimConflictException releasing another actor's claim");
                }
                catch (TaskClaimConflictException) { /* ok */ }

                var released = await taskSvc.ReleaseAsync(taskId, "alice", ct);
                if (released.ClaimedBy is not null || released.ClaimedAt is not null)
                    throw new Exception("ReleaseAsync did not clear ClaimedBy/ClaimedAt");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var hasReleaseEvent = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                        && e.EventType == "TaskReleased" && e.Actor == "alice", ct);
                if (!hasReleaseEvent) throw new Exception("TaskReleased activity event missing");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("AddCommentAsync writes comment + activity; rejects system type", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var comment = await taskSvc.AddCommentAsync(
                    taskId, "a real note", author: "svc-test", type: CommentType.Note, actor: "svc-test", ct);
                if (comment.Id == 0 || comment.Type != CommentType.Note)
                    throw new Exception("AddCommentAsync did not return the persisted comment");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var hasComment = await db.TaskComments
                    .AnyAsync(c => c.Id == comment.Id && c.TaskItemId == taskId && c.Type == CommentType.Note, ct);
                if (!hasComment) throw new Exception("Comment row not persisted");
                var hasEvent = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                        && e.EventType == "TaskCommentAdded" && e.Actor == "svc-test", ct);
                if (!hasEvent) throw new Exception("TaskCommentAdded activity event missing");

                // System comment types must be rejected (only the service emits those).
                try
                {
                    await taskSvc.AddCommentAsync(taskId, "nope", "x", CommentType.StatusChange, "x", ct);
                    throw new Exception("Expected ValidationException for system comment type, got success");
                }
                catch (ValidationException) { /* ok */ }
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("ReassignToFeatureAsync moves within same project", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var f = await http.PostAsJsonAsync($"/api/projects/{pid}/features", new { name = "Second feature" }, Json, ct);
                var fBody = await f.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                var featureB = fBody.GetProperty("id").GetInt32();

                var updated = await taskSvc.ReassignToFeatureAsync(taskId, featureB, "service-test", ct);
                if (updated.FeatureId != featureB)
                    throw new Exception($"FeatureId not updated (expected {featureB}, got {updated.FeatureId})");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var hasEvent = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                        && e.EventType == "TaskFeatureChanged" && e.Actor == "service-test", ct);
                if (!hasEvent) throw new Exception("TaskFeatureChanged activity event missing");
                var hasComment = await db.TaskComments
                    .AnyAsync(c => c.TaskItemId == taskId && c.Type == CommentType.FeatureChange, ct);
                if (!hasComment) throw new Exception("FeatureChange comment not created");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        results.Add(await RunTest("ReassignToFeatureAsync rejects cross-project (closes #83)", "Service:Task", async () =>
        {
            var (pidA, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            int? pidB = null;
            try
            {
                var (createdPidB, featureB, _) = await CreateProjectFeatureTaskAsync(http, ct);
                pidB = createdPidB;

                try
                {
                    await taskSvc.ReassignToFeatureAsync(taskId, featureB, "x", ct);
                    throw new Exception("Expected CrossProjectReassignmentException, got success");
                }
                catch (CrossProjectReassignmentException ex)
                {
                    if (ex.TaskId != taskId)
                        throw new Exception($"Exception's TaskId property is wrong (expected {taskId}, got {ex.TaskId})");
                }
            }
            finally
            {
                await TryDeleteProjectAsync(http, pidA, ct);
                if (pidB.HasValue) await TryDeleteProjectAsync(http, pidB.Value, ct);
            }
        }));

        results.Add(await RunTest("UpdateAsync writes activity event", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var updated = await taskSvc.UpdateAsync(
                    taskId, new TaskUpdate(Name: "new name", Priority: TaskPriority.High), "service-test", ct);
                if (updated.Name != "new name") throw new Exception("Name not updated");
                if (updated.Priority != TaskPriority.High) throw new Exception("Priority not updated");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var hasEvent = await db.ActivityEvents
                    .AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                        && e.EventType == "TaskUpdated" && e.Actor == "service-test", ct);
                if (!hasEvent) throw new Exception("TaskUpdated activity event missing");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid, ct);
            }
        }));

        // ── Added coverage: subtask cascade completion + markdown security ──

        results.Add(await RunTest("ToggleDoneAsync cascades completion to subtasks", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                foreach (var text in new[] { "subtask A", "subtask B" })
                {
                    var s = await http.PostAsJsonAsync($"/api/tasks/{taskId}/subtasks", new { text }, Json, ct);
                    if (s.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create subtask expected 201, got {(int)s.StatusCode}");
                }

                var task = await taskSvc.ToggleDoneAsync(taskId, "service-test", ct);
                if (task.Status != WorkStatus.Done) throw new Exception("Task should be Done after toggle");
                if (task.CompletedAt is null) throw new Exception("CompletedAt should be set when toggled to Done");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var withSubs = await db.Tasks.Include(t => t.Subtasks).AsNoTracking().FirstAsync(t => t.Id == taskId, ct);
                if (withSubs.Subtasks.Count != 2) throw new Exception($"Expected 2 subtasks, found {withSubs.Subtasks.Count}");
                if (withSubs.Subtasks.Any(s => !s.Completed)) throw new Exception("All subtasks should be Completed after task toggled Done");
                var hasComment = await db.TaskComments.AnyAsync(c => c.TaskItemId == taskId && c.Type == CommentType.StatusChange, ct);
                if (!hasComment) throw new Exception("ToggleDone should write a StatusChange comment");
                var hasEvent = await db.ActivityEvents.AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId && e.EventType == "TaskStatusChanged", ct);
                if (!hasEvent) throw new Exception("ToggleDone should write a TaskStatusChanged event");
            }
            finally { await TryDeleteProjectAsync(http, pid, ct); }
        }));

        results.Add(await RunTest("ToggleDoneAsync un-completes subtasks when toggled off Done", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, ct);
            try
            {
                var s = await http.PostAsJsonAsync($"/api/tasks/{taskId}/subtasks", new { text = "only sub" }, Json, ct);
                if (s.StatusCode != HttpStatusCode.Created) throw new Exception($"Create subtask failed: {(int)s.StatusCode}");

                await taskSvc.ToggleDoneAsync(taskId, "x", ct);            // Todo -> Done (subtask completed)
                var task = await taskSvc.ToggleDoneAsync(taskId, "x", ct); // Done -> Todo (subtask un-completed)
                if (task.Status == WorkStatus.Done) throw new Exception("Second toggle should leave task not Done");
                if (task.CompletedAt is not null) throw new Exception("CompletedAt should be null when toggled off Done");

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var withSubs = await db.Tasks.Include(t => t.Subtasks).AsNoTracking().FirstAsync(t => t.Id == taskId, ct);
                if (withSubs.Subtasks.Any(x => x.Completed)) throw new Exception("Subtasks should be un-completed after toggling off Done");
            }
            finally { await TryDeleteProjectAsync(http, pid, ct); }
        }));

        results.Add(await RunTest("MarkdownRenderer strips javascript: links", "Service:Markdown", () =>
        {
            var html = markdownRenderer.RenderToSafeHtml("[click me](javascript:alert(1))");
            if (html.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"javascript: URL should be sanitized, got: {html}");
            return Task.CompletedTask;
        }));

        results.Add(await RunTest("MarkdownRenderer strips event-handler attributes", "Service:Markdown", () =>
        {
            var html = markdownRenderer.RenderToSafeHtml("<a href=\"https://x.com\" onclick=\"steal()\">x</a>");
            if (html.Contains("onclick", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"onclick handler should be stripped, got: {html}");
            return Task.CompletedTask;
        }));

        results.Add(await RunTest("MarkdownRenderer renders links and tables", "Service:Markdown", () =>
        {
            var link = markdownRenderer.RenderToSafeHtml("[ok](https://example.com)");
            if (!link.Contains("href=\"https://example.com\"", StringComparison.Ordinal))
                throw new Exception($"Expected anchor with href, got: {link}");
            var table = markdownRenderer.RenderToSafeHtml("| A | B |\n| - | - |\n| 1 | 2 |");
            if (!table.Contains("<table", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Expected <table> from pipe-table markdown, got: {table}");
            return Task.CompletedTask;
        }));

        return new TestSuiteResult("Service", results, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            // Purge only this run's orphaned audit rows (test-project droppings),
            // preserving real deleted-project audit history (#85). Fixes #150.
            await TestAuditCleanup.PurgeOrphanedSinceAsync(dbFactory, runStart, ct);
        }
    }

    public async Task<TestSuiteResult> RunAllTestsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var api = await RunApiTestsAsync(ct);
        var ui = await RunUiTestsAsync(ct);
        var svc = await RunServiceTestsAsync(ct);

        var combined = new List<TestResult>();
        combined.AddRange(api.Tests);
        combined.AddRange(ui.Tests);
        combined.AddRange(svc.Tests);

        return new TestSuiteResult("All", combined, sw.Elapsed.TotalMilliseconds);
    }

    // ── Helpers ──

    private HttpClient CreateHttpClient()
    {
        var http = httpClientFactory.CreateClient("test-runner");
        http.BaseAddress = new Uri(GetBaseUrl());
        http.Timeout = TimeSpan.FromSeconds(10);
        return http;
    }

    private string GetBaseUrl()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var url = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.Ordinal));
        // Some hosts return "http://[::]:5123" — replace with a loopback we can actually call.
        if (url is not null && (url.Contains("[::]") || url.Contains("0.0.0.0")))
            url = url.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return url ?? "http://127.0.0.1:5123";
    }

    private static async Task<TestResult> RunTest(string name, string category, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            return new TestResult(name, category, true, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return new TestResult(name, category, false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(64, prefix.Length + 1 + 8)];

    private static async Task<(int ProjectId, int FeatureId, int TaskId)> CreateProjectFeatureTaskAsync(HttpClient http, CancellationToken ct)
    {
        var p = await http.PostAsJsonAsync("/api/projects", new { name = NewName("Test") }, Json, ct);
        var pBody = await p.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var pid = pBody.GetProperty("id").GetInt32();

        var f = await http.PostAsJsonAsync($"/api/projects/{pid}/features", new { name = "Test feature" }, Json, ct);
        var fBody = await f.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var fid = fBody.GetProperty("id").GetInt32();

        var t = await http.PostAsJsonAsync($"/api/features/{fid}/tasks", new { name = "Test task", status = "Todo", priority = "Medium" }, Json, ct);
        var tBody = await t.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var tid = tBody.GetProperty("id").GetInt32();

        return (pid, fid, tid);
    }

    private static async Task TryDeleteProjectAsync(HttpClient http, int projectId, CancellationToken ct)
    {
        try { await http.DeleteAsync($"/api/projects/{projectId}", ct); } catch { /* best-effort cleanup */ }
    }
}
