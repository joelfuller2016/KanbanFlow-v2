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
        var sw = Stopwatch.StartNew();
        using var http = CreateHttpClient();
        var results = new List<TestResult>();

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
        }
        finally
        {
            if (scratchProjectId is int pid)
                await TryDeleteProjectAsync(http, pid, ct);
        }

        return new TestSuiteResult("API", results, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<TestSuiteResult> RunUiTestsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var http = CreateHttpClient();
        var results = new List<TestResult>();

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

        results.Add(await RunTest("Project view (id=1, seed data) renders 200", "UI:ProjectView", async () =>
        {
            var resp = await http.GetAsync("/project/1", ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"GET /project/1 returned {(int)resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync(ct);
            // The page renders even before the InteractiveServer circuit fully boots.
            if (!html.Contains("KanbanFlow", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Project view HTML missing brand marker");
        }));

        results.Add(await RunTest("Unknown route returns 404 (or app shell)", "UI:Routing", async () =>
        {
            var resp = await http.GetAsync("/this-route-does-not-exist", ct);
            // Blazor server can return either 404 (no matching component) or 200 with a default
            // routing component. Both are acceptable; what we want is that the server doesn't 5xx.
            if ((int)resp.StatusCode >= 500)
                throw new Exception($"Server errored on unknown route: {(int)resp.StatusCode}");
        }));

        return new TestSuiteResult("UI", results, sw.Elapsed.TotalMilliseconds);
    }

    public async Task<TestSuiteResult> RunServiceTestsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<TestResult>();

        // These tests use the real DI-registered ITaskService against the live DB,
        // wrapping each test in a project we delete on completion so they don't leak.
        using var http = CreateHttpClient();

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

        // Now that ReassignToFeatureAsync + UpdateAsync are implemented (this PR), only
        // DeleteAsync remains stubbed.
        results.Add(await RunTest("Stubbed service methods throw NotImplementedException", "Service:Task", async () =>
        {
            try
            {
                await taskSvc.DeleteAsync(1, "x", ct);
                throw new Exception("DeleteAsync should throw NotImplementedException (stubbed)");
            }
            catch (NotImplementedException) { /* ok */ }
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

        return new TestSuiteResult("Service", results, sw.Elapsed.TotalMilliseconds);
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
