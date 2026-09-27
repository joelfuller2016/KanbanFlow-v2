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
    IMarkdownRenderer markdownRenderer,
    ILogger<TestRunnerService> logger) : ITestRunnerService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the full API test suite against the running application and collects per-test results.
    /// </summary>
    /// <param name="ct">A cancellation token that can be used to cancel the test run.</param>
    /// <returns>A TestSuiteResult named "API" containing each TestResult and the total elapsed milliseconds.</returns>
    /// <remarks>
    /// The method snapshots existing project IDs at start to avoid affecting pre-existing projects, executes multiple isolated API tests (creating and deleting scratch projects as needed), and performs a best-effort cleanup after completion using CancellationToken.None: a per-run-marker sweep for any fixture whose id was never learned because its create failed for a reason unrelated to ct (HttpClient.Timeout, transport reset, server error) before the id was captured, or whose known-id DELETE failed (#150 AC#2, #541), followed by the orphaned-ActivityEvent purge. Cleanup errors are swallowed so they do not mask test failures or cancellations.
    /// </remarks>
    public async Task<TestSuiteResult> RunApiTestsAsync(CancellationToken ct = default)
    {
        var runStart = DateTime.UtcNow;
        // Per-run marker embedded in the Name/Title of every project and idea this run creates (see NewName);
        // lets SweepRunMarkerAsync find a fixture whose create timed out or failed in transport
        // before its id was learned, or whose known-id delete failed (#150 AC#2, #541).
        var runMarker = NewRunMarker();
        // Snapshot the projects that already exist at run start. The runner uses the live DB,
        // so a user could delete a real, pre-existing project mid-run; excluding these ids from
        // the cleanup keeps that real project's audit trail intact (see TestAuditCleanup).
        List<int> preExistingProjectIds;
        await using (var preDb = await dbFactory.CreateDbContextAsync(ct))
            preExistingProjectIds = await preDb.Projects.Select(p => p.Id).ToListAsync(ct);
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
            var name = NewName("Test-Project", runMarker);
            // CancellationToken.None for the create + its read — see CreateProjectFeatureTaskAsync's
            // comment on the same pattern: the server commits this POST regardless of ct, so ct only
            // ever decides whether we keep what it already committed, checked once id is known.
            var resp = await PostCreateAsync(http, "/api/projects", new { name }, ct);
            if (resp.StatusCode != HttpStatusCode.Created)
                throw new Exception($"POST /api/projects expected 201, got {(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None);
            var id = body.GetProperty("id").GetInt32();
            try
            {
                ct.ThrowIfCancellationRequested();
                var del = await http.DeleteAsync($"/api/projects/{id}", ct);
                if (del.StatusCode != HttpStatusCode.NoContent)
                    throw new Exception($"DELETE /api/projects/{id} expected 204, got {(int)del.StatusCode}");
            }
            catch
            {
                // id is known — clean up immediately rather than leaving this solely to the sweep.
                await TryDeleteProjectAsync(http, id);
                throw;
            }
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
            var (pid, _, _) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            results.Add(await RunTest("Invalid status (numeric) returns 422", "API:Validation", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
                try
                {
                    var resp = await http.PatchAsJsonAsync($"/api/tasks/{taskId}/status", new { status = "99" }, Json, ct);
                    if (resp.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"Expected 422 for numeric status, got {(int)resp.StatusCode} (issue #89)");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            results.Add(await RunTest("Claim then re-claim by same actor renews the claim", "API:Claim", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
                try
                {
                    var first = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim",
                        new { actor = "alice" }, Json, ct);
                    if (!first.IsSuccessStatusCode)
                        throw new Exception($"First claim failed: {(int)first.StatusCode}");
                    var firstClaimedAt = (await first.Content.ReadFromJsonAsync<JsonElement>(Json, ct))
                        .GetProperty("claimedAt").GetDateTime();

                    // The API uses the real clock, whose resolution can be coarser than an HTTP
                    // round-trip on some hosts. Keep the claims in distinct ticks so strict >
                    // proves lease renewal instead of occasionally measuring clock granularity.
                    await Task.Delay(20, ct);

                    var second = await http.PostAsJsonAsync($"/api/tasks/{taskId}/claim",
                        new { actor = "alice" }, Json, ct);
                    if (!second.IsSuccessStatusCode)
                        throw new Exception($"Re-claim by same actor should succeed, got {(int)second.StatusCode}");
                    var secondClaimedAt = (await second.Content.ReadFromJsonAsync<JsonElement>(Json, ct))
                        .GetProperty("claimedAt").GetDateTime();
                    if (secondClaimedAt <= firstClaimedAt)
                        throw new Exception("Re-claim by same actor did not advance ClaimedAt");

                    var activity = await http.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/activity", Json, ct);
                    var renewedEvents = activity.EnumerateArray().Count(
                        ev => ev.GetProperty("eventType").GetString() == "TaskClaimRenewed");
                    if (renewedEvents != 0)
                        throw new Exception($"Fresh same-actor renewal wrote {renewedEvents} TaskClaimRenewed event(s)");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            results.Add(await RunTest("Claim conflict returns 409 for different actor", "API:Claim", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            results.Add(await RunTest("Add comment with valid Type", "API:Comment", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
                try
                {
                    var resp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/comments",
                        new { text = "test note", author = "test-runner", type = "Note" }, Json, ct);
                    if (resp.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Expected 201, got {(int)resp.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            results.Add(await RunTest("Add comment with disallowed Type returns 422", "API:Comment", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
                try
                {
                    var resp = await http.PostAsJsonAsync($"/api/tasks/{taskId}/comments",
                        new { text = "system", author = "x", type = "StatusChange" }, Json, ct);
                    if (resp.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"Expected 422 for system-typed comment, got {(int)resp.StatusCode}");
                }
                finally
                {
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            results.Add(await RunTest("Step context returns rich AI bundle", "API:AI", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                    await TryDeleteProjectAsync(http, innerPid);
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
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                    await TryDeleteProjectAsync(http, innerPid);
                }
            }));

            // ── Added coverage: claim release, dependency blocking, requirements CRUD ──

            results.Add(await RunTest("Claim release frees task for another actor", "API:Claim", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                finally { await TryDeleteProjectAsync(http, innerPid); }
            }));

            results.Add(await RunTest("Dependency blocks next-tasks until blocker is Done", "API:AI", async () =>
            {
                var (innerPid, featureId, blockerId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                finally { await TryDeleteProjectAsync(http, innerPid); }
            }));

            results.Add(await RunTest("Requirement create / assign / delete round-trip", "API:Requirement", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                finally { await TryDeleteProjectAsync(http, innerPid); }
            }));

            // ── Idea CRUD (#180 Phase 2): ideas are not project-scoped, so most tests POST directly ──

            results.Add(await RunTest("Idea create + get round-trip", "API:Idea", async () =>
            {
                int? ideaId = null;
                try
                {
                    // Marker-tagged (see runMarker): if THIS create's response is never received
                    // (timeout, transport failure), ideaId below is never assigned and this finally
                    // cannot clean up by id — SweepRunMarkerAsync is what finds a leak like that (#150 AC#2, #541).
                    var title = $"{runMarker} Round-trip idea";
                    var create = await PostCreateAsync(http, "/api/ideas",
                        new { title, body = "the body", plan = "the plan", tags = "ai,backend", priority = TaskPriority.High.ToString() }, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"POST /api/ideas expected 201, got {(int)create.StatusCode}");
                    var created = await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None);
                    ideaId = created.GetProperty("id").GetInt32();
                    ct.ThrowIfCancellationRequested();
                    if (created.GetProperty("title").GetString() != title)
                        throw new Exception("Created idea title mismatch");
                    if (created.GetProperty("body").GetString() != "the body")
                        throw new Exception("Created idea body mismatch");
                    if (created.GetProperty("plan").GetString() != "the plan")
                        throw new Exception("Created idea plan mismatch");
                    if (created.GetProperty("status").GetString() != IdeaStatus.New.ToString())
                        throw new Exception($"New idea should start status '{IdeaStatus.New}', got '{created.GetProperty("status").GetString()}'");
                    if (created.GetProperty("priority").GetString() != TaskPriority.High.ToString())
                        throw new Exception($"Created idea priority mismatch, got '{created.GetProperty("priority").GetString()}'");

                    var got = await http.GetFromJsonAsync<JsonElement>($"/api/ideas/{ideaId}", Json, ct);
                    if (got.GetProperty("id").GetInt32() != ideaId)
                        throw new Exception("GET /api/ideas/{id} returned the wrong id");
                    if (got.GetProperty("title").GetString() != title)
                        throw new Exception("GET idea title does not match created title");
                    if (got.GetProperty("tags").GetString() != "ai,backend")
                        throw new Exception($"GET idea tags mismatch, got '{got.GetProperty("tags").GetString()}'");
                }
                finally
                {
                    if (ideaId is int iid) await TryDeleteIdeaAsync(http, iid);
                }
            }));

            results.Add(await RunTest("Idea create with blank title returns 422", "API:Idea", async () =>
            {
                var resp = await http.PostAsJsonAsync("/api/ideas", new { title = "  " }, Json, ct);
                if (resp.StatusCode != HttpStatusCode.UnprocessableEntity)
                    throw new Exception($"Expected 422 for blank title, got {(int)resp.StatusCode}");
            }));

            results.Add(await RunTest("Idea list filters by tag (case-insensitive) and q", "API:Idea", async () =>
            {
                int? idA = null;
                int? idB = null;
                try
                {
                    // Marker-tagged titles (see runMarker doc on CreateProjectFeatureTaskAsync) so a
                    // create that fails before idA/idB below is assigned (a timeout, a transport reset,
                    // a server error) is still found by SweepRunMarkerAsync. "kanban" stays intact in
                    // titleA for the ?q= assertion below.
                    var titleA = $"{runMarker} alpha kanban";
                    var titleB = $"{runMarker} beta";
                    var ra = await PostCreateAsync(http, "/api/ideas",
                        new { title = titleA, tags = "ai,backend" }, ct);
                    if (ra.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create idea A expected 201, got {(int)ra.StatusCode}");
                    idA = (await ra.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();

                    var rb = await PostCreateAsync(http, "/api/ideas",
                        new { title = titleB, tags = "frontend" }, ct);
                    if (rb.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create idea B expected 201, got {(int)rb.StatusCode}");
                    idB = (await rb.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
                    ct.ThrowIfCancellationRequested();

                    // Uppercase tag query proves case-insensitive substring matching.
                    var byTag = await http.GetFromJsonAsync<JsonElement>("/api/ideas?tag=BACKEND&limit=500", Json, ct);
                    if (byTag.ValueKind != JsonValueKind.Array)
                        throw new Exception($"Expected JSON array from ?tag, got {byTag.ValueKind}");
                    if (!byTag.EnumerateArray().Any(i => i.GetProperty("id").GetInt32() == idA))
                        throw new Exception("?tag=BACKEND should include idea A (tags 'ai,backend')");
                    if (byTag.EnumerateArray().Any(i => i.GetProperty("id").GetInt32() == idB))
                        throw new Exception("?tag=BACKEND should NOT include idea B (tags 'frontend')");

                    var byQ = await http.GetFromJsonAsync<JsonElement>("/api/ideas?q=kanban&limit=500", Json, ct);
                    if (!byQ.EnumerateArray().Any(i => i.GetProperty("id").GetInt32() == idA))
                        throw new Exception($"?q=kanban should include idea A ('{titleA}')");
                }
                finally
                {
                    if (idA is int a) await TryDeleteIdeaAsync(http, a);
                    if (idB is int b) await TryDeleteIdeaAsync(http, b);
                }
            }));

            results.Add(await RunTest("Idea PATCH updates title + status; unknown id 404", "API:Idea", async () =>
            {
                int? ideaId = null;
                try
                {
                    // Marker-tagged (see runMarker doc on CreateProjectFeatureTaskAsync). The rename
                    // target below is ALSO marker-tagged (#541): the id is already known by
                    // the time the PATCH runs, so this finally's own TryDeleteIdeaAsync is what
                    // recovers a failure there — but if a LATER, unrelated failure (a crash, or that
                    // same delete itself failing) leaves this row behind anyway, SweepRunMarkerAsync
                    // finds a row by its CURRENT title, not its creation title. An unmarked rename
                    // target would make an otherwise-recoverable leak invisible to the sweep.
                    var create = await PostCreateAsync(http, "/api/ideas", new { title = $"{runMarker} before rename" }, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create idea expected 201, got {(int)create.StatusCode}");
                    ideaId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
                    ct.ThrowIfCancellationRequested();

                    var renamedTitle = $"{runMarker} renamed";
                    var patch = await http.PatchAsJsonAsync($"/api/ideas/{ideaId}",
                        new { title = renamedTitle, status = IdeaStatus.Triaging.ToString() }, Json, ct);
                    if (patch.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"PATCH /api/ideas/{ideaId} expected 200, got {(int)patch.StatusCode}");

                    var got = await http.GetFromJsonAsync<JsonElement>($"/api/ideas/{ideaId}", Json, ct);
                    if (got.GetProperty("title").GetString() != renamedTitle)
                        throw new Exception($"PATCH did not persist title, got '{got.GetProperty("title").GetString()}'");
                    if (got.GetProperty("status").GetString() != IdeaStatus.Triaging.ToString())
                        throw new Exception($"PATCH did not persist status, got '{got.GetProperty("status").GetString()}'");

                    var missing = await http.PatchAsJsonAsync("/api/ideas/99999999", new { title = "nope" }, Json, ct);
                    if (missing.StatusCode != HttpStatusCode.NotFound)
                        throw new Exception($"PATCH unknown idea expected 404, got {(int)missing.StatusCode}");
                }
                finally
                {
                    if (ideaId is int iid) await TryDeleteIdeaAsync(http, iid);
                }
            }));

            results.Add(await RunTest("Idea delete returns 204 then 404; unknown id 404", "API:Idea", async () =>
            {
                // Marker-tagged (see runMarker doc on CreateProjectFeatureTaskAsync).
                var create = await PostCreateAsync(http, "/api/ideas", new { title = $"{runMarker} to delete" }, ct);
                if (create.StatusCode != HttpStatusCode.Created)
                    throw new Exception($"Create idea expected 201, got {(int)create.StatusCode}");
                var ideaId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var del = await http.DeleteAsync($"/api/ideas/{ideaId}", ct);
                    if (del.StatusCode != HttpStatusCode.NoContent)
                        throw new Exception($"DELETE /api/ideas/{ideaId} expected 204, got {(int)del.StatusCode}");

                    var gone = await http.GetAsync($"/api/ideas/{ideaId}", ct);
                    if (gone.StatusCode != HttpStatusCode.NotFound)
                        throw new Exception($"GET after delete expected 404, got {(int)gone.StatusCode}");

                    var delMissing = await http.DeleteAsync("/api/ideas/99999999", ct);
                    if (delMissing.StatusCode != HttpStatusCode.NotFound)
                        throw new Exception($"DELETE unknown idea expected 404, got {(int)delMissing.StatusCode}");
                }
                catch
                {
                    // id is known — this test otherwise has no finally to fall back on.
                    await TryDeleteIdeaAsync(http, ideaId);
                    throw;
                }
            }));

            results.Add(await RunTest("Idea promote creates project; re-promote returns 422", "API:Idea", async () =>
            {
                int? ideaId = null;
                int? projectId = null;
                try
                {
                    // Marker-tagged (see runMarker doc on CreateProjectFeatureTaskAsync). PromoteAsync
                    // sets the new Project's Name to the idea's Title, so this also carries the
                    // marker onto the promoted project below — one sweep query catches both.
                    var create = await PostCreateAsync(http, "/api/ideas",
                        new { title = $"{runMarker} to promote", body = "b", plan = "p" }, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create idea expected 201, got {(int)create.StatusCode}");
                    ideaId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();

                    // Promote also creates a Project (PromoteAsync); same CancellationToken.None
                    // treatment for the same reason as every other fixture-create in this file.
                    var promote = await PostCreateAsync(http, $"/api/ideas/{ideaId}/promote", new { actor = "test-runner" }, ct);
                    if (promote.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"POST /api/ideas/{ideaId}/promote expected 200, got {(int)promote.StatusCode}");
                    var promoted = await promote.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None);
                    if (promoted.GetProperty("ideaId").GetInt32() != ideaId)
                        throw new Exception("Promote result ideaId does not match the promoted idea");
                    projectId = promoted.GetProperty("projectId").GetInt32();
                    ct.ThrowIfCancellationRequested();
                    if (projectId <= 0)
                        throw new Exception($"Promote result projectId should be > 0, got {projectId}");

                    var got = await http.GetFromJsonAsync<JsonElement>($"/api/ideas/{ideaId}", Json, ct);
                    if (got.GetProperty("status").GetString() != IdeaStatus.Promoted.ToString())
                        throw new Exception($"Promoted idea status should be '{IdeaStatus.Promoted}', got '{got.GetProperty("status").GetString()}'");
                    if (got.GetProperty("promotedProjectId").GetInt32() != projectId)
                        throw new Exception("Idea.promotedProjectId does not match the new project id");

                    var project = await http.GetAsync($"/api/projects/{projectId}", ct);
                    if (project.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"GET /api/projects/{projectId} expected 200 (project created), got {(int)project.StatusCode}");

                    var rePromote = await http.PostAsJsonAsync($"/api/ideas/{ideaId}/promote", new { actor = "test-runner" }, Json, ct);
                    if (rePromote.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"Re-promote of an already-promoted idea expected 422, got {(int)rePromote.StatusCode}");
                }
                finally
                {
                    if (projectId is int proj) await TryDeleteProjectAsync(http, proj);
                    if (ideaId is int iid) await TryDeleteIdeaAsync(http, iid);
                }
            }));

            results.Add(await RunTest("Idea activity survives delete (#85 audit pattern)", "API:Idea", async () =>
            {
                int? ideaId = null;
                try
                {
                    // Marker-tagged (see runMarker doc on CreateProjectFeatureTaskAsync). The rename
                    // target below is ALSO marker-tagged (#541) so SweepRunMarkerAsync can
                    // still find this row by its CURRENT title if it is ever left behind after the
                    // rename — see the identical note on the "before rename" test.
                    var create = await PostCreateAsync(http, "/api/ideas", new { title = $"{runMarker} audit idea" }, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create idea expected 201, got {(int)create.StatusCode}");
                    ideaId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
                    ct.ThrowIfCancellationRequested();

                    // PATCH writes an IdeaUpdated activity event.
                    var patch = await http.PatchAsJsonAsync($"/api/ideas/{ideaId}", new { title = $"{runMarker} audit idea (edited)" }, Json, ct);
                    if (patch.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"PATCH idea expected 200, got {(int)patch.StatusCode}");

                    var before = await http.GetFromJsonAsync<JsonElement>($"/api/ideas/{ideaId}/activity", Json, ct);
                    if (before.ValueKind != JsonValueKind.Array)
                        throw new Exception($"Activity expected JSON array, got {before.ValueKind}");
                    if (before.GetArrayLength() < 1)
                        throw new Exception("Idea activity should have >= 1 event after a PATCH");

                    var del = await http.DeleteAsync($"/api/ideas/{ideaId}", ct);
                    if (del.StatusCode != HttpStatusCode.NoContent)
                        throw new Exception($"DELETE idea expected 204, got {(int)del.StatusCode}");

                    // The audit trail must survive deletion (events have no idea FK).
                    var after = await http.GetAsync($"/api/ideas/{ideaId}/activity", ct);
                    if (after.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"Activity after delete expected 200 (audit survives), got {(int)after.StatusCode}");
                    var afterBody = await after.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                    if (afterBody.GetArrayLength() < 1)
                        throw new Exception("Idea activity should still have >= 1 event after the idea is deleted");
                }
                finally
                {
                    if (ideaId is int iid) await TryDeleteIdeaAsync(http, iid);
                }
            }));

            results.Add(await RunTest("Idea POST/PATCH with blank enum returns 422", "API:Idea", async () =>
            {
                // A present-but-blank enum body field must be rejected (422), not
                // silently treated as "leave unchanged"/default (CodeRabbit, #185).
                int? ideaId = null;
                try
                {
                    // Marker-tagged even though this is expected to 422 (defensive — do not assume a
                    // validation-only POST can never persist a row).
                    var postBlank = await http.PostAsJsonAsync("/api/ideas", new { title = $"{runMarker} blank enum", priority = " " }, Json, ct);
                    if (postBlank.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"POST blank priority expected 422, got {(int)postBlank.StatusCode}");

                    // Marker-tagged (see runMarker doc on CreateProjectFeatureTaskAsync) — unlike the
                    // blank-priority POST above, this one succeeds and creates a row.
                    var create = await PostCreateAsync(http, "/api/ideas", new { title = $"{runMarker} blank enum patch target" }, ct);
                    if (create.StatusCode != HttpStatusCode.Created)
                        throw new Exception($"Create idea expected 201, got {(int)create.StatusCode}");
                    ideaId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
                    ct.ThrowIfCancellationRequested();

                    var patchBlank = await http.PatchAsJsonAsync($"/api/ideas/{ideaId}", new { status = " " }, Json, ct);
                    if (patchBlank.StatusCode != HttpStatusCode.UnprocessableEntity)
                        throw new Exception($"PATCH blank status expected 422, got {(int)patchBlank.StatusCode}");
                }
                finally
                {
                    if (ideaId is int iid) await TryDeleteIdeaAsync(http, iid);
                }
            }));

            results.Add(await RunTest("Project activity stays queryable after the project is deleted (#85)", "API:Activity", async () =>
            {
                var (innerPid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
                var deleted = false;
                try
                {
                    var patch = await http.PatchAsJsonAsync($"/api/tasks/{taskId}/status",
                        new { status = "InProgress", actor = "test-runner" }, Json, ct);
                    if (!patch.IsSuccessStatusCode) throw new Exception($"Status change failed: {(int)patch.StatusCode}");
                    var before = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{innerPid}/activity", Json, ct);
                    if (before.GetArrayLength() == 0) throw new Exception("Expected activity before deletion");

                    var del = await http.DeleteAsync($"/api/projects/{innerPid}", ct);
                    if (del.StatusCode != HttpStatusCode.NoContent) throw new Exception($"DELETE project expected 204, got {(int)del.StatusCode}");
                    deleted = true;

                    // #85: retained audit must still be retrievable after the project row is gone.
                    var resp = await http.GetAsync($"/api/projects/{innerPid}/activity", ct);
                    if (resp.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"Activity should stay queryable after deletion (#85), got {(int)resp.StatusCode}");
                    var after = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                    if (after.GetArrayLength() == 0)
                        throw new Exception("Retained activity events should still be returned after project deletion");
                }
                finally { if (!deleted) await TryDeleteProjectAsync(http, innerPid); }
            }));
        }
        finally
        {
            if (scratchProjectId is int pid)
                await TryDeleteProjectAsync(http, pid);
        }

        return new TestSuiteResult("API", results, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            await RunEndOfSuiteCleanupAsync(http, runMarker, runStart, preExistingProjectIds, "API");
        }
    }

    /// <summary>
    /// Execute the UI test suite against the running application.
    /// </summary>
    /// <param name="ct">Cancellation token to stop test execution; cleanup is still attempted using <see cref="CancellationToken.None"/> so orphaned test artifacts — including a fixture whose id was never learned because its create failed before the id was captured (#150 AC#2, #541) — are purged even on cancellation.</param>
    /// <returns>A <see cref="TestSuiteResult"/> containing the individual test results and the total elapsed milliseconds for the UI suite.</returns>
    public async Task<TestSuiteResult> RunUiTestsAsync(CancellationToken ct = default)
    {
        var runStart = DateTime.UtcNow;
        // Per-run marker embedded in the Name/Title of every project and idea this run creates (see NewName);
        // lets SweepRunMarkerAsync find a fixture whose create timed out or failed in transport
        // before its id was learned, or whose known-id delete failed (#150 AC#2, #541).
        var runMarker = NewRunMarker();
        // Snapshot the projects that already exist at run start. The runner uses the live DB,
        // so a user could delete a real, pre-existing project mid-run; excluding these ids from
        // the cleanup keeps that real project's audit trail intact (see TestAuditCleanup).
        List<int> preExistingProjectIds;
        await using (var preDb = await dbFactory.CreateDbContextAsync(ct))
            preExistingProjectIds = await preDb.Projects.Select(p => p.Id).ToListAsync(ct);
        var sw = Stopwatch.StartNew();
        using var http = CreateHttpClient();
        var results = new List<TestResult>();

        try
        {
        // NOTE (#284): these tests assert the *page's own rendered structure*, never a string the
        // shared layout emits. The NavMenu renders "KanbanFlow" (brand), "Ideas" and "Settings"
        // (nav links) on EVERY route, and every page emits a <title>. Asserting any of those
        // proved only that the layout rendered — the board could paint zero columns and zero
        // cards and the suite stayed green. Structural, data-backed assertions only.

        results.Add(await RunTest("Home page lists a freshly-created project as a card", "UI:Home", async () =>
        {
            var name = NewName("UiHome", runMarker);
            var create = await PostCreateAsync(http, "/api/projects", new { name }, ct);
            if (create.StatusCode != HttpStatusCode.Created)
                throw new Exception($"POST /api/projects expected 201, got {(int)create.StatusCode}");
            var pid = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
            try
            {
                ct.ThrowIfCancellationRequested();
                var resp = await http.GetAsync("/", ct);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"GET / returned {(int)resp.StatusCode}");
                var html = await resp.Content.ReadAsStringAsync(ct);

                // Home's own <h1> — the layout emits no <h1>, so this proves the page component rendered.
                if (!html.Contains(">Projects</h1>", StringComparison.Ordinal))
                    throw new Exception("Home page HTML missing its 'Projects' <h1> heading");
                // ...and the list actually rendered a card for the project we just created,
                // rather than the empty state or an empty grid.
                if (!html.Contains(name, StringComparison.Ordinal))
                    throw new Exception($"Home page rendered no card for the newly-created project '{name}'");
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("Settings page renders the app-info and test-runner panels", "UI:Settings", async () =>
        {
            var resp = await http.GetAsync("/settings", ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"GET /settings returned {(int)resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync(ct);

            // "Settings" on its own is a layout tautology (the NavMenu's settings link emits it
            // on every route). Assert the page's own panels instead.
            if (!html.Contains(">Settings</h1>", StringComparison.Ordinal))
                throw new Exception("Settings page HTML missing its 'Settings' <h1> heading");
            foreach (var testId in new[] { "settings-app-info", "settings-tests", "settings-test-buttons" })
            {
                if (!html.Contains($"data-testid=\"{testId}\"", StringComparison.Ordinal))
                    throw new Exception($"Settings page missing the '{testId}' panel (data-testid=\"{testId}\")");
            }
            if (!html.Contains("Run Tests", StringComparison.Ordinal))
                throw new Exception("Settings page HTML missing the 'Run Tests' panel header");
        }));

        results.Add(await RunTest("Ideas page renders a seeded idea as an idea card", "UI:Ideas", async () =>
        {
            // IdeaService.ListAsync returns newest-first, so the idea we just created is
            // always on the first page of results regardless of how many ideas exist.
            var title = NewName("UiIdea", runMarker);
            var create = await PostCreateAsync(http, "/api/ideas", new { title }, ct);
            if (create.StatusCode != HttpStatusCode.Created)
                throw new Exception($"POST /api/ideas expected 201, got {(int)create.StatusCode}");
            var ideaId = (await create.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None)).GetProperty("id").GetInt32();
            try
            {
                ct.ThrowIfCancellationRequested();
                var resp = await http.GetAsync("/ideas", ct);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"GET /ideas returned {(int)resp.StatusCode}");
                var html = await resp.Content.ReadAsStringAsync(ct);

                // #280: /ideas is a status board, not a flat grid — `ideas-grid` is gone. Assert the
                // board rendered AND that the card landed in the *New* column, which is a strictly
                // stronger check than the old one: it tests the pipeline (CreateAsync always starts an
                // idea at New, and the board fans it into that column) instead of a wrapper div.
                if (!html.Contains("data-testid=\"kanban-board\"", StringComparison.Ordinal))
                    throw new Exception("Ideas page rendered no board (data-testid=\"kanban-board\" missing — empty state, or the list is broken)");

                var card = $"data-testid=\"idea-card-{ideaId}\"";
                if (!html.Contains(card, StringComparison.Ordinal))
                    throw new Exception($"Idea {ideaId} did not render as an idea card ({card} not found in the page HTML)");
                if (!html.Contains(title, StringComparison.Ordinal))
                    throw new Exception($"Idea card {ideaId} rendered without its title '{title}'");

                // Columns render in IdeaStatus order (New, Triaging, …), so a card belonging to New
                // must appear between New's card container and the next column's root.
                var newColumn = html.IndexOf("data-testid=\"board-column-New-cards\"", StringComparison.Ordinal);
                var nextColumn = html.IndexOf("data-testid=\"board-column-Triaging\"", StringComparison.Ordinal);
                var cardAt = html.IndexOf(card, StringComparison.Ordinal);
                if (newColumn < 0 || nextColumn < 0)
                    throw new Exception("Ideas board rendered without its New/Triaging columns");
                if (cardAt < newColumn || cardAt > nextColumn)
                    throw new Exception($"A newly created idea must render in the New column, but card {ideaId} is outside it");
            }
            finally
            {
                await TryDeleteIdeaAsync(http, ideaId);
            }
        }));

        results.Add(await RunTest("Project view renders all 5 board columns with the seeded task as a card", "UI:ProjectView", async () =>
        {
            // Don't depend on seed project id=1 — it can be deleted by other tests
            // or future migrations. Create a scratch project, hit its route, clean up.
            // CreateProjectFeatureTaskAsync seeds exactly one task, with status Todo.
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
            try
            {
                var resp = await http.GetAsync($"/project/{pid}", ct);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"GET /project/{pid} returned {(int)resp.StatusCode}");
                var html = await resp.Content.ReadAsStringAsync(ct);

                // The board is prerendered by the SSR pass (InteractiveServer prerenders by
                // default), so its full structure is in the initial HTML. Asserting that
                // structure is what lets this test fail when the board breaks.
                foreach (var status in Enum.GetValues<WorkStatus>())
                {
                    if (!html.Contains($"data-testid=\"board-column-{status}\"", StringComparison.Ordinal))
                        throw new Exception($"Board is missing the '{status}' column (data-testid=\"board-column-{status}\")");
                    if (!html.Contains($"data-testid=\"board-column-{status}-cards\"", StringComparison.Ordinal))
                        throw new Exception($"Board column '{status}' is missing its cards container (data-testid=\"board-column-{status}-cards\")");
                }

                // ...and the seeded Todo task must render as a card *inside the Todo column*,
                // not merely somewhere on the page.
                AssertCardRendersInColumn(html, WorkStatus.Todo, taskId);
            }
            finally
            {
                await TryDeleteProjectAsync(http, pid);
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
            await RunEndOfSuiteCleanupAsync(http, runMarker, runStart, preExistingProjectIds, "UI");
        }
    }

    /// <summary>
    /// Executes the service-layer test suite against the DI-registered ITaskService using the live database.
    /// </summary>
    /// <param name="ct">A cancellation token that can be used to cancel the test run.</param>
    /// <returns>A TestSuiteResult containing the suite name ("Service"), the list of individual test results, and the total elapsed milliseconds.</returns>
    /// <remarks>
    /// The method creates isolated scratch data for each test, verifies persistence and activity audit rows, and performs a best-effort cleanup of orphaned test artifacts after the run: a per-run-marker sweep for any fixture whose id was never learned because its create failed before the id was captured, or whose known-id DELETE failed (#150 AC#2, #541), then the orphaned-ActivityEvent purge. Cleanup is attempted with CancellationToken.None and any cleanup exceptions are swallowed to avoid masking test failures or cancellations.
    /// </remarks>
    public async Task<TestSuiteResult> RunServiceTestsAsync(CancellationToken ct = default)
    {
        var runStart = DateTime.UtcNow;
        // Per-run marker embedded in the Name/Title of every project and idea this run creates (see NewName);
        // lets SweepRunMarkerAsync find a fixture whose create timed out or failed in transport
        // before its id was learned, or whose known-id delete failed (#150 AC#2, #541).
        var runMarker = NewRunMarker();
        // Snapshot the projects that already exist at run start. The runner uses the live DB,
        // so a user could delete a real, pre-existing project mid-run; excluding these ids from
        // the cleanup keeps that real project's audit trail intact (see TestAuditCleanup).
        List<int> preExistingProjectIds;
        await using (var preDb = await dbFactory.CreateDbContextAsync(ct))
            preExistingProjectIds = await preDb.Projects.Select(p => p.Id).ToListAsync(ct);
        var sw = Stopwatch.StartNew();
        var results = new List<TestResult>();

        // These tests use the real DI-registered ITaskService against the live DB,
        // wrapping each test in a project we delete on completion so they don't leak.
        using var http = CreateHttpClient();

        try
        {
        results.Add(await RunTest("ChangeStatusAsync writes activity + StatusChange comment", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("ChangeStatusAsync is idempotent for same-status calls", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("ChangeStatusAsync sets CompletedAt on transition to Done", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
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
            var (pid, featureId, seedTaskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("CreateAsync rejects empty name + unknown feature", "Service:Task", async () =>
        {
            var (pid, featureId, _) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("DeleteAsync removes task + children and TaskDeleted audit survives", "Service:Task", async () =>
        {
            var (pid, featureId, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
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

        results.Add(await RunTest("ClaimAsync renews for same actor; conflicts for another", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
            try
            {
                var first = await taskSvc.ClaimAsync(taskId, "alice", ct);
                var firstClaimedAt = first.ClaimedAt;
                if (first.ClaimedBy != "alice" || firstClaimedAt is null)
                    throw new Exception("First claim did not set ClaimedBy/ClaimedAt");

                // Same actor re-claim renews the lease: ClaimedAt advances.
                var second = await taskSvc.ClaimAsync(taskId, "alice", ct);
                if (second.ClaimedAt <= firstClaimedAt)
                    throw new Exception("Re-claim by same actor did not advance ClaimedAt");

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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("ReleaseAsync clears claim + emits audit; conflict for another actor", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("AddCommentAsync writes comment + activity; rejects system type", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("ReassignToFeatureAsync moves within same project", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        results.Add(await RunTest("ReassignToFeatureAsync rejects cross-project (closes #83)", "Service:Task", async () =>
        {
            var (pidA, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
            int? pidB = null;
            try
            {
                var (createdPidB, featureB, _) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pidA);
                if (pidB.HasValue) await TryDeleteProjectAsync(http, pidB.Value);
            }
        }));

        results.Add(await RunTest("UpdateAsync writes activity event", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
                await TryDeleteProjectAsync(http, pid);
            }
        }));

        // ── Added coverage: subtask cascade completion + markdown security ──

        results.Add(await RunTest("ToggleDoneAsync cascades completion to subtasks", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
            finally { await TryDeleteProjectAsync(http, pid); }
        }));

        results.Add(await RunTest("ToggleDoneAsync un-completes subtasks when toggled off Done", "Service:Task", async () =>
        {
            var (pid, _, taskId) = await CreateProjectFeatureTaskAsync(http, runMarker, ct);
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
            finally { await TryDeleteProjectAsync(http, pid); }
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
            await RunEndOfSuiteCleanupAsync(http, runMarker, runStart, preExistingProjectIds, "Service");
        }
    }

    /// <summary>
    /// Runs the API, UI, and Service test suites sequentially and aggregates their results.
    /// </summary>
    /// <param name="ct">A cancellation token used to cancel the test run.</param>
    /// <returns>A TestSuiteResult named "All" containing the combined TestResult items from the API, UI, and Service suites and the total elapsed milliseconds.</returns>
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

    /// <summary>
    /// A fixture name: <c>{prefix}-{runMarker}-</c> followed by exactly 8 lowercase hex characters of a
    /// fresh <see cref="Guid"/> — the shape <see cref="IsFixtureProjectName"/> and
    /// <see cref="IsFixtureIdeaTitle"/> accept, and nothing longer.
    /// </summary>
    // internal, not private: TestRunnerServiceCancellationTests round-trips its real output through the API.
    internal static string NewName(string prefix, string runMarker) =>
        $"{prefix}-{runMarker}-{Guid.NewGuid():N}"[..(prefix.Length + runMarker.Length + 10)];

    /// <summary>
    /// Sends a fixture-create POST this run needs to track. Checks the caller's <paramref name="ct"/>
    /// IMMEDIATELY BEFORE sending — nothing has gone out yet at that point, so honoring cancellation
    /// here can never reopen the race #541's review found (a cancellation landing WHILE a create is
    /// in flight, after CancellationToken.None already committed to sending it). The send and its
    /// caller's later read both still use <see cref="CancellationToken.None"/> unconditionally once
    /// started: a send bound to <see cref="CancellationToken.None"/> never aborts its own request, so the
    /// server's request token never fires on the caller's account, and the create completes with its id
    /// read back. That holds whichever way the route saves. <c>POST /api/projects</c> and the feature
    /// create save with no token at all; <c>POST /api/ideas</c>, promote and the task create pass the
    /// request's token into their save, and an abort of the request would cancel that save part-way.
    /// </summary>
    /// <remarks>
    /// Without this pre-check (#541), a cancelled suite still closed its own race, but it
    /// no longer failed fast: every remaining create-first test still committed a real create and then
    /// issued its own DELETE, because nothing upstream of the send ever looked at <paramref name="ct"/>
    /// again once <see cref="RunTest"/> swallowed the first post-cancellation exception into a failed
    /// result. Measured with this check removed: 21 creates the server still answered with <c>201</c>
    /// after the caller had cancelled, counted by
    /// <c>TestRunnerServiceCancellationTests.RunApiTestsAsync_SendsNoFurtherCreates_OnceTheCallerHasAlreadyCancelled</c>.
    /// That counts only the API suite's top-level <c>POST /api/projects</c> and <c>POST /api/ideas</c>,
    /// not the feature and task creates, nor promote. Each can pay up to <c>HttpClient.Timeout</c> (10s)
    /// if the server is slow, before its own DELETE (another 10s ceiling). One check here, before ANY
    /// of them, is what stops the creates. The suite itself keeps going: <see cref="RunTest"/> still
    /// swallows each exception, so every remaining test runs and fails at its first token check, but
    /// none of them sends another create.
    /// </remarks>
    private static Task<HttpResponseMessage> PostCreateAsync(HttpClient http, string route, object body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return http.PostAsJsonAsync(route, body, Json, CancellationToken.None);
    }

    /// <summary>
    /// A fresh per-run sentinel, minted once at the top of each <c>Run*TestsAsync</c> call and
    /// threaded (never stored on <c>this</c> — see <see cref="CreateProjectFeatureTaskAsync"/>'s
    /// remarks) into every fixture name/title that run creates via <see cref="NewName"/>. 16 hex
    /// characters of a fresh <see cref="Guid"/> gives 60 bits of entropy (one of the 16 is the fixed
    /// v4 version nibble, not random) behind the fixed "kftest" tag — together something a real user
    /// will not type by chance and two runs, concurrent or not, will not share by chance, which is what
    /// lets <see cref="SweepRunMarkerAsync"/> query the live DB for it safely.
    /// </summary>
    // internal, not private: TestRunnerServiceCancellationTests mints real markers for its round-trip test.
    internal static string NewRunMarker() => "kftest" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Asserts that a task renders as a board card (<c>data-testid="board-card-{taskId}"</c>) inside the
    /// given column's card container (<c>data-testid="board-column-{status}-cards"</c>) in the SSR HTML.
    /// </summary>
    /// <remarks>
    /// KanbanBoard renders one KanbanColumn per WorkStatus in enum order, so a column's card region runs
    /// from its cards container up to the next column's wrapper (or to the end of the document for the
    /// last column). Every marker embeds its closing quote, so <c>board-column-Todo"</c> cannot match
    /// <c>board-column-Todo-cards"</c> and <c>board-card-6"</c> cannot match <c>board-card-61"</c>.
    /// Checking containment — not mere presence — is what makes this catch a card rendered into the
    /// wrong column (#284).
    /// </remarks>
    private static void AssertCardRendersInColumn(string html, WorkStatus status, int taskId)
    {
        var card = $"data-testid=\"board-card-{taskId}\"";
        if (!html.Contains(card, StringComparison.Ordinal))
            throw new Exception($"Task {taskId} did not render as a board card ({card} not found in the page HTML)");

        var container = $"data-testid=\"board-column-{status}-cards\"";
        var start = html.IndexOf(container, StringComparison.Ordinal);
        if (start < 0)
            throw new Exception($"Board column '{status}' is missing its cards container ({container})");

        // The next column's wrapper bounds this column's card region.
        var end = html.Length;
        var statuses = Enum.GetValues<WorkStatus>();
        var next = Array.IndexOf(statuses, status) + 1;
        if (next < statuses.Length)
        {
            var nextIndex = html.IndexOf($"data-testid=\"board-column-{statuses[next]}\"", start, StringComparison.Ordinal);
            if (nextIndex >= 0) end = nextIndex;
        }

        if (!html[start..end].Contains(card, StringComparison.Ordinal))
            throw new Exception($"Task {taskId} rendered on the page but NOT inside the '{status}' column ({card} not found between {container} and the next column)");
    }

    /// <summary>
    /// Shared scratch-fixture helper (#150 / #150 AC#2). Instance method — not static — so it can
    /// reuse <see cref="TryDeleteProjectAsync"/>'s logging on the cancellation path below.
    /// <paramref name="runMarker"/> is threaded explicitly (never cached on <c>this</c>) because
    /// <see cref="TestRunnerService"/> is request-scoped but a single instance still runs its three
    /// suites SEQUENTIALLY from <see cref="RunAllTestsAsync"/> — an instance field would work for
    /// that call pattern today, but would silently corrupt the marker (and therefore the sweep) the
    /// day a future caller runs two suites concurrently on one instance. A parameter cannot.
    /// </summary>
    /// <remarks>
    /// Every create POST below (project, feature, task) and its response read use
    /// <see cref="CancellationToken.None"/>, never <paramref name="ct"/>: a send bound to
    /// <see cref="CancellationToken.None"/> never aborts its own request, so the server's request token
    /// never fires on the caller's account and the create completes with its id read back, whichever way
    /// the route saves (<c>POST /api/projects</c> and the feature create pass no token to their save,
    /// <c>ApiEndpoints.cs:492</c> and <c>:1034</c>; the task create passes the request's token). Racing
    /// <paramref name="ct"/> against a create only risked losing the id of a row the server may already
    /// have written. <see cref="PostCreateAsync"/> instead checks
    /// <paramref name="ct"/> BEFORE each send, so cancellation never abandons a create that has
    /// already started, and each id is already captured by the time the next check can fire: once
    /// <c>pid</c> is known, the catch below deletes it with <see cref="CancellationToken.None"/> and
    /// rethrows. What remains uncaught here is a create that fails for a reason unrelated to
    /// <paramref name="ct"/> — <c>HttpClient.Timeout</c> (10s default), a transport reset, or a
    /// server-side error — before <c>pid</c> is captured. <see cref="SweepRunMarkerAsync"/>, called
    /// from each suite's outer <c>finally</c>, covers that case best-effort only: it runs only if the
    /// suite reaches that <c>finally</c>, and it deletes only rows with the exact fixture shapes
    /// (<see cref="IsFixtureProjectName"/>/<see cref="IsFixtureIdeaTitle"/>), by name rather than by id.
    /// </remarks>
    // internal, not private, so TestRunnerServiceCancellationTests can drive it directly with a
    // fault-injecting HttpClient (InternalsVisibleTo is set — see ProjectView.razor for precedent).
    internal async Task<(int ProjectId, int FeatureId, int TaskId)> CreateProjectFeatureTaskAsync(HttpClient http, string runMarker, CancellationToken ct)
    {
        // CancellationToken.None for each create POST below and its response read (independent review
        // of this fix's first version): a send bound to CancellationToken.None never aborts its own
        // request, so the caller's ct can no longer cut a create off part-way. Racing ct against the
        // create meant its id could be lost while the row still landed — POST /api/projects
        // (ApiEndpoints.cs:492) saves with no token at all, so it commits regardless. Now
        // PostCreateAsync checks the caller's ct BEFORE each send, and the send and its read use
        // CancellationToken.None, so cancellation never abandons a create that has already started,
        // and each id is already captured by the time the next check can fire.
        var p = await PostCreateAsync(http, "/api/projects", new { name = NewName("Test", runMarker) }, ct);
        var pBody = await p.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None);
        var pid = pBody.GetProperty("id").GetInt32();

        try
        {
            var f = await PostCreateAsync(http, $"/api/projects/{pid}/features", new { name = "Test feature" }, ct);
            var fBody = await f.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None);
            var fid = fBody.GetProperty("id").GetInt32();

            var t = await PostCreateAsync(http, $"/api/features/{fid}/tasks", new { name = "Test task", status = "Todo", priority = "Medium" }, ct);
            var tBody = await t.Content.ReadFromJsonAsync<JsonElement>(Json, CancellationToken.None);
            var tid = tBody.GetProperty("id").GetInt32();

            return (pid, fid, tid);
        }
        catch
        {
            // pid IS known at this point. This method observes the caller's ct only BEFORE each send
            // (PostCreateAsync's check), so this catch runs when that check fires before the feature
            // or task create, or when either of those creates or its read fails; cascade delete takes
            // the feature/task with it. A cancellation that lands during or after the task create is
            // not observed here: the method returns normally, and the caller's own try/finally cleans
            // that project up. What remains uncaught here is the project create itself failing for a
            // REAL reason with ct never involved (HttpClient.Timeout, a transport reset) before pid is
            // captured. SweepRunMarkerAsync, called from each suite's outer finally, covers that case
            // best-effort only: it runs only if the suite reaches that finally, and it deletes only
            // rows with the exact fixture shapes (IsFixtureProjectName/IsFixtureIdeaTitle), by name
            // rather than by id.
            await TryDeleteProjectAsync(http, pid);
            throw;
        }
    }

    /// <summary>
    /// Best-effort cleanup DELETE for a scratch row this runner seeded. One helper so the three
    /// cleanup rules live in exactly one place:
    /// <list type="number">
    /// <item><description><b>Never the request's <c>CancellationToken</c>.</b> If the caller cancelled,
    /// a <c>ct</c>-bound DELETE is never sent — and the seeded row is left behind in the <b>live user
    /// database</b>. This runner has no test DB: it is driven from <c>/settings</c> and
    /// <c>POST /api/admin/run-tests</c> against the real one, so a skipped cleanup is junk in real user
    /// data. Cancellation stops the <em>tests</em>, not the tidying-up.</description></item>
    /// <item><description><b>Never throws.</b> These calls sit in <c>finally</c> blocks; an exception here
    /// would replace — and so hide — the test's original exception.</description></item>
    /// <item><description><b>Never silent.</b> A swallowed failure means invisible junk in real user data,
    /// so every failure is logged.</description></item>
    /// </list>
    /// A 404 counts as success: the row is already gone, which is the entire goal.
    /// </summary>
    private async Task TryCleanupAsync(HttpClient http, string route)
    {
        try
        {
            // CancellationToken.None is deliberate — see rule 1 above.
            var resp = await http.DeleteAsync(route, CancellationToken.None);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning(
                    "Test cleanup DELETE {Route} returned {StatusCode}; a scratch row may be left behind in the live database.",
                    route, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Test cleanup DELETE {Route} failed; a scratch row may be left behind in the live database.", route);
        }
    }

    private Task TryDeleteProjectAsync(HttpClient http, int projectId) =>
        TryCleanupAsync(http, $"/api/projects/{projectId}");

    private Task TryDeleteIdeaAsync(HttpClient http, int ideaId) =>
        TryCleanupAsync(http, $"/api/ideas/{ideaId}");

    /// <summary>
    /// Both of this runner's best-effort end-of-suite cleanups, run in the ONE order that is safe —
    /// see <see cref="SweepRunMarkerAsync"/>'s <b>Ordering</b> remarks for why sweep must run before
    /// the purge, not after. Called from all three suites' outer <c>finally</c>
    /// (<see cref="RunApiTestsAsync"/>, <see cref="RunUiTestsAsync"/>, <see cref="RunServiceTestsAsync"/>)
    /// so that ordering lives in exactly one place instead of three copies that could drift apart.
    /// </summary>
    /// <param name="suiteName">Only for the log message on a failed purge — e.g. "API", "UI", "Service".</param>
    private async Task RunEndOfSuiteCleanupAsync(
        HttpClient http, string runMarker, DateTime runStart, List<int> preExistingProjectIds, string suiteName)
    {
        await SweepRunMarkerAsync(http, runMarker, preExistingProjectIds);
        // Best-effort: CancellationToken.None runs cleanup even on cancellation;
        // swallowing errors prevents masking the suite's original exception (#85, #150).
        try
        {
            await TestAuditCleanup.PurgeOrphanedSinceAsync(dbFactory, runStart, preExistingProjectIds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort test audit cleanup failed ({Suite} suite).", suiteName);
        }
    }

    /// <summary>
    /// The prefixes this class passes to <see cref="NewName"/> for a PROJECT fixture. See
    /// <see cref="IsFixtureProjectName"/> for the exact names they produce.
    /// </summary>
    // internal, not private (this and the two registries below): TestRunnerServiceCancellationTests pins
    // them against every name this file builds from runMarker, and round-trips each shape through the API.
    internal static readonly string[] FixtureProjectPrefixes = ["Test", "Test-Project", "UiHome"];

    /// <summary>The prefixes this class passes to <see cref="NewName"/> for an IDEA fixture.</summary>
    internal static readonly string[] FixtureIdeaPrefixes = ["UiIdea"];

    /// <summary>
    /// Every text this class puts after <c>"{marker} "</c> in an idea title, including the titles a PATCH
    /// renames an idea to, and "blank enum", whose POST is expected to be rejected (registered anyway, in
    /// case it ever commits a row). <c>PromoteAsync</c> copies an idea's title onto the project it creates,
    /// so "to promote" is a project fixture's name as well.
    /// </summary>
    internal static readonly string[] FixtureLiterals =
    [
        "Round-trip idea", "alpha kanban", "beta", "before rename", "renamed", "to delete", "to promote",
        "audit idea", "audit idea (edited)", "blank enum", "blank enum patch target",
    ];

    /// <summary>
    /// True when <paramref name="name"/> is EXACTLY the name of a project fixture this run created for
    /// <paramref name="marker"/>: <see cref="NewName"/>'s <c>{prefix}-{marker}-{8 lowercase hex}</c> for a
    /// prefix in <see cref="FixtureProjectPrefixes"/>, or <c>{marker} {literal}</c> for a literal in
    /// <see cref="FixtureLiterals"/>, with nothing before or after it (ordinal). A real row that merely
    /// quotes a fixture's name ("Please rename Test-{marker}-a1b2c3d4 to something better"), or begins
    /// with one and goes on ("Test-{marker}-a1b2c3d4 notes"), is not one.
    /// </summary>
    internal static bool IsFixtureProjectName(string name, string marker) =>
        IsNewNameShape(name, marker, FixtureProjectPrefixes) || IsLiteralShape(name, marker);

    /// <summary>
    /// Same as <see cref="IsFixtureProjectName"/> for an idea title: exactly
    /// <c>{prefix}-{marker}-{8 lowercase hex}</c> for a prefix in <see cref="FixtureIdeaPrefixes"/>, or
    /// exactly <c>{marker} {literal}</c>.
    /// </summary>
    internal static bool IsFixtureIdeaTitle(string title, string marker) =>
        IsNewNameShape(title, marker, FixtureIdeaPrefixes) || IsLiteralShape(title, marker);

    private static bool IsNewNameShape(string value, string marker, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            var head = $"{prefix}-{marker}-";
            if (value.Length != head.Length + 8 || !value.StartsWith(head, StringComparison.Ordinal))
                continue;

            var allLowerHex = true;
            foreach (var c in value.AsSpan(head.Length))
                allLowerHex &= char.IsAsciiHexDigitLower(c);
            if (allLowerHex)
                return true;
        }

        return false;
    }

    private static bool IsLiteralShape(string value, string marker)
    {
        foreach (var literal in FixtureLiterals)
        {
            if (string.Equals(value, $"{marker} {literal}", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// End-of-run safety net for the residual cases <see cref="CreateProjectFeatureTaskAsync"/> (and
    /// the other marker-tagged fixture sites in this class) cannot close by catching around an id: a
    /// create that fails for a reason unrelated to the caller's token — <c>HttpClient.Timeout</c> (10s
    /// default), a transport reset, or a server-side error — before its id is captured, or a known-id
    /// DELETE that itself fails. No id is ever recorded for the row in either case, so nothing in this
    /// class can target a DELETE at it by id (#150 AC#2, #541). Every project and idea this run creates
    /// carries <paramref name="runMarker"/> in its Name/Title instead (see <see cref="NewName"/>; a
    /// feature, task or other child row goes with its project by cascade delete), so this
    /// queries the live DB for that marker directly — no id needed — and deletes what it finds through
    /// the same REST routes normal cleanup uses, so cascade delete and audit-event emission stay
    /// exactly as they are for every other cleanup path in this file.
    /// </summary>
    /// <remarks>
    /// <para><b>Safety.</b> A row is deleted only if its Name/Title is EXACTLY a fixture's
    /// (<see cref="IsFixtureProjectName"/> / <see cref="IsFixtureIdeaTitle"/>): the SQL <c>Contains</c>
    /// below only fetches candidates, and a real row that quotes a fixture's name, or begins with one
    /// and goes on, is fetched and then left alone. <paramref name="runMarker"/> is minted fresh per run
    /// by <see cref="NewRunMarker"/> — 60 bits of fresh <see cref="Guid"/> entropy (16 hex characters,
    /// one of which is the fixed v4 version nibble) behind a "kftest" tag — so another run's fixtures,
    /// past or concurrent, do not match. <paramref name="preExistingProjectIds"/> is a second,
    /// independent guard for projects: a project that existed before this run started is skipped even
    /// if its name matches. Ideas need no such snapshot: an idea that existed before this run started
    /// cannot carry the marker, which the run mints only as it starts.</para>
    /// <para><b>Residual.</b> A real row created DURING the run whose Name/Title is exactly a
    /// fixture's is indistinguishable from one by name, and would be deleted. The pre-existing snapshot
    /// confines this to rows created during the run, but cannot protect those, because their ids are new;
    /// only the marker bounds it. Such a name carries this
    /// run's freshly minted 60-bit marker, which appears nowhere but in this run's own fixtures, so short
    /// of someone copying a fixture's name while the run is in progress, a real row cannot have one.</para>
    /// <para><b>Expected to find nothing.</b> On a run where no create or cleanup DELETE fails, every
    /// fixture is already deleted by its own try/finally before this runs, so the sweep matches zero
    /// rows — it exists purely for the residual cases those try/finally blocks cannot reach.</para>
    /// <para><b>Ordering.</b> Callers MUST run this before <see cref="TestAuditCleanup.PurgeOrphanedSinceAsync"/>
    /// in the same <c>finally</c>, not after: the purge only removes an ActivityEvent once its project
    /// no longer exists, so if it ran first, a leaked project's events would still be attached to a
    /// living project and survive; running this sweep first deletes the project (which, like any
    /// other project deletion, is expected to itself emit an audit event), and the purge that follows
    /// then removes every one of that now-gone project's events, landing on delta 0 across both
    /// tables instead of trading a leaked project for a permanently orphaned event. Both calls are
    /// made together, in this order, by <see cref="RunEndOfSuiteCleanupAsync"/> — the one place this
    /// ordering lives, so it cannot drift between the three suites.</para>
    /// <para><b>Never throws.</b> Called from a <c>finally</c> that must run cleanup even when the
    /// suite itself was cancelled — matches <see cref="TryCleanupAsync"/>'s own rule and this file's
    /// existing <c>catch (Exception ex) { logger.LogWarning(...) }</c> convention for that.</para>
    /// </remarks>
    internal async Task SweepRunMarkerAsync(HttpClient http, string runMarker, IReadOnlyCollection<int> preExistingProjectIds)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);

            // Fetch broadly (SQL Contains — SQLite does that filtering), then keep only the exact
            // fixture shapes client-side via IsFixtureProjectName/IsFixtureIdeaTitle: Contains alone
            // would also match a row that quotes the marker, or begins with a fixture's name and goes on.
            var projectCandidates = await db.Projects
                .Where(p => p.Name.Contains(runMarker) && !preExistingProjectIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(CancellationToken.None);
            foreach (var candidate in projectCandidates.Where(p => IsFixtureProjectName(p.Name, runMarker)))
            {
                logger.LogWarning(
                    "Test run {RunMarker} sweep found project {ProjectId} ('{Name}') with no cleanup " +
                    "ever recorded for it — either its create's own id was never learned, or a known-id " +
                    "delete for it already failed; deleting by name.", runMarker, candidate.Id, candidate.Name);
                await TryDeleteProjectAsync(http, candidate.Id);
            }

            var ideaCandidates = await db.Ideas
                .Where(i => i.Title.Contains(runMarker))
                .Select(i => new { i.Id, i.Title })
                .ToListAsync(CancellationToken.None);
            foreach (var candidate in ideaCandidates.Where(i => IsFixtureIdeaTitle(i.Title, runMarker)))
            {
                logger.LogWarning(
                    "Test run {RunMarker} sweep found idea {IdeaId} ('{Title}') with no cleanup ever " +
                    "recorded for it — either its create's own id was never learned, or a known-id " +
                    "delete for it already failed; deleting by title.", runMarker, candidate.Id, candidate.Title);
                await TryDeleteIdeaAsync(http, candidate.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort run-marker sweep failed for run {RunMarker}.", runMarker);
        }
    }
}
