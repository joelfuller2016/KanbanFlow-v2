using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KanbanFlow.Data;
using KanbanFlow.Models;
using KanbanFlow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanFlow.Tests;

/// <summary>
/// Real HTTP + real SQLite tests for the fixture-leak-on-cancel defect (F1 / issue #150 AC#2, #541):
/// <c>TestRunnerService</c>'s tracked fixture creates — every project and idea it expects to create, and
/// the scratch project's feature and task — use <c>CancellationToken.None</c>, so a caller-token
/// cancellation can no longer abort a response read mid-flight (child rows a test adds under a scratch
/// project stay bound to the caller's token: cascade delete removes them with it), but an
/// <c>HttpClient.Timeout</c>, transport reset, or server error before the id is
/// captured can still leave a committed row whose id the runner never learned — no <c>finally</c>
/// in <c>TestRunnerService</c> can target such a row with a DELETE by id.
///
/// <para>These boot a REAL Kestrel host (like <see cref="AdminHealthEndpointTests"/>'s <c>HealthHost</c>)
/// with <see cref="ApiEndpoints.MapApiEndpoints"/> mapped over a real SQLite file, and drive
/// <see cref="TestRunnerService"/> with a real <see cref="HttpClient"/> whose transport is wrapped in
/// <see cref="FaultInjectingHandler"/>. That handler lets the REAL request reach the REAL server (so the
/// server-side commit genuinely happens, exactly as rev-api's raw-socket <c>abort_run.py</c> proved
/// against a live instance) and only then throws — deterministically reproducing a create whose response
/// is lost after the server committed it (before the fix, the caller's token firing mid-read did exactly
/// that; since it, a timeout or a transport reset still can) instead of racing a real clock.
/// A real race (a raw-socket abort at a wall-clock delay) is what proves the bug exists on a live
/// process; it cannot be the regression test itself because it cannot be made to land on one exact
/// line reliably on every run. This handler is that same fault, made to land on a chosen line every
/// time.</para>
/// </summary>
public sealed class TestRunnerServiceCancellationTests
{
    [Fact]
    public async Task CreateProjectFeatureTaskAsync_HappyPath_NameCarriesRunMarker_AndCleansUpNormally()
    {
        await using var host = await ApiHost.StartAsync();
        var runner = host.NewRunner(new FaultInjectingHandler(host.BaseAddress, _ => false, faultOnNthMatch: 0));
        const string marker = "kftestHAPPYPATH0000001";

        var (pid, fid, tid) = await runner.CreateProjectFeatureTaskAsync(host.RealClient, marker, CancellationToken.None);

        Assert.True(pid > 0);
        Assert.True(fid > 0);
        Assert.True(tid > 0);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == pid);
            Assert.Contains(marker, project.Name, StringComparison.Ordinal);
        }

        // Ordinary cleanup (what every real call site's own try/finally does) removes it completely.
        var del = await host.RealClient.DeleteAsync($"/api/projects/{pid}");
        Assert.True(del.IsSuccessStatusCode);
        await using (var db = await host.DbFactory.CreateDbContextAsync())
            Assert.False(await db.Projects.AnyAsync(p => p.Id == pid));
    }

    [Fact]
    public async Task RunApiTestsAsync_SendsNoFurtherCreates_OnceTheCallerHasAlreadyCancelled()
    {
        // #541: before PostCreateAsync's pre-send check, NOTHING looked at the caller's ct
        // before a create's SEND — only after (via ThrowIfCancellationRequested placed AFTER id
        // capture), and RunTest swallows that post-capture throw into a failed TestResult. So once ct
        // fired, RunApiTestsAsync kept walking every remaining create-first test, sending a REAL
        // create (CancellationToken.None, unconditional) and issuing a REAL delete for each. With the
        // check, the remaining wrapped tests still run (RunTest still swallows each exception), failing at
        // their first token check, until the scratch-project setup's create, outside any RunTest wrapper,
        // throws out of the suite; none of them sends another create, which is what this counts.
        //
        // Cancelling ct is deferred until AFTER the suite's first real request completes — not done
        // upfront — because RunApiTestsAsync's OWN setup (the preExistingProjectIds snapshot) is
        // itself ct-bound and runs before any test does; cancelling upfront makes the method throw
        // there instead, before reaching a single create, which would make this test pass for the
        // wrong reason regardless of PostCreateAsync (measured: an earlier draft of this test did
        // exactly that and stayed green even with the pre-send check removed).
        //
        // Counts creates the SERVER actually answered with 201 — not "POSTs attempted". Counting
        // attempts would overcount: a request whose ct is already cancelled by the time HttpClient
        // looks at it can throw before ever reaching the wire, so an attempt-count includes sends
        // that never happened.
        var cts = new CancellationTokenSource();
        await using var host = await ApiHost.StartAsync();
        var counter = new CancelAfterFirstThenCountCreatesHandler(
            host.BaseAddress,
            cts,
            req => req.Method == HttpMethod.Post &&
                   (req.RequestUri!.AbsolutePath == "/api/projects" || req.RequestUri.AbsolutePath == "/api/ideas"));
        var runner = host.NewRunner(counter);

        // Deliberately not asserting HOW the method finishes: whether it throws depends on which
        // call site's own post-capture check happens to fire first once ct is cancelled, and that is
        // not what this test is about (measured: it can differ between the fixed and sabotaged code,
        // since a fix that centralizes every check into PostCreateAsync also removes several
        // formerly-redundant individual checks — sabotaging the shared helper can leave a suite that
        // runs to normal completion instead of throwing, rather than the reverse). The count below is
        // what actually distinguishes "stopped" from "kept going".
        try { await runner.RunApiTestsAsync(cts.Token); }
        catch (OperationCanceledException) { /* acceptable outcome; see above */ }
        cts.Dispose();

        Assert.Equal(0, counter.SuccessfulCreateCountAfterCancel);
    }

    [Fact]
    public async Task RunApiTestsAsync_LeavesNoPermanentProject_WhenCancellationRacesTheServersOwnCommit()
    {
        // The narrower residual window an independent review of this fix found: FaultInjectingHandler
        // (below) can only simulate the CLIENT losing interest after the server has ALREADY fully
        // responded — it cannot reach the case where the server is still genuinely inside its own
        // SaveChangesAsync (ApiEndpoints.cs:492, no token — the server cannot cancel its own commit)
        // when the client's ct fires. If the sweep's own SELECT happens to run in that exact gap, it
        // finds nothing (the row is not committed yet) and the row becomes permanent once it lands
        // moments later, unwatched. BlockFirstMatchingProjectSaveInterceptor pins the server exactly
        // inside that gap so this is deterministic instead of a wall-clock race — the same technique
        // TaskMoveConcurrencyTests/TaskDependencyConcurrencyTests use for their own EF interceptors.
        var interceptor = new BlockFirstMatchingProjectSaveInterceptor("Test-Project-");
        await using var host = await ApiHost.StartAsync(interceptor);
        // No transport fault here — this race is entirely server-side.
        var runner = host.NewRunner(new FaultInjectingHandler(host.BaseAddress, _ => false, faultOnNthMatch: 0));
        using var cts = new CancellationTokenSource();

        var run = runner.RunApiTestsAsync(cts.Token); // not awaited — the server-side block starts inside it
        try
        {
            // Bounded against `run` itself, not just a timer: if RunApiTestsAsync faults or returns
            // before ever reaching the blocked save (e.g. an unrelated startup error), Entered would
            // otherwise never complete and this await would hang the whole xUnit run forever.
            await Task.WhenAny(interceptor.Entered, run);
            Assert.True(interceptor.Entered.IsCompletedSuccessfully,
                "RunApiTestsAsync finished before ever reaching the blocked save — this test proves " +
                "nothing about the race it targets until the interceptor actually engages.");
            cts.Cancel(); // the client observes cancellation WHILE the server is still mid-commit

            // Pre-fix: the client's ct-bound create fails fast without waiting for the server, so
            // RunApiTestsAsync's own cleanup runs (and finds nothing yet) well inside this window —
            // `run` completes on its own. Post-fix: the client's CancellationToken.None-bound create
            // keeps waiting for the real response, so `run` cannot possibly finish until we release
            // it below — the delay wins instead. Which of the two finishes first is what tells them apart.
            var first = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(run, first); // post-fix: the None-bound create must still be waiting on the blocked server save
        }
        finally
        {
            // MUST run even if an assertion above throws — otherwise the server stays blocked forever
            // and host.DisposeAsync()'s StopAsync() hangs on the in-flight request.
            interceptor.Release();
        }

        // The server's own save completes asynchronously relative to Release() unblocking it (measured:
        // a fixed post-release delay is not reliable). Waiting for Settled — signalled from
        // SavedChangesAsync/SaveChangesFailedAsync — makes checking the database afterward
        // deterministic instead of racing the server's own remaining async work.
        await Task.WhenAny(interceptor.Settled, Task.Delay(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(host.BaselineProjectCount, await db.Projects.CountAsync());
    }

    [Fact]
    public async Task CreateProjectFeatureTaskAsync_DeletesTheProject_WhenTheFeatureCreateResponseIsLost()
    {
        // Drives CreateProjectFeatureTaskAsync DIRECTLY — never through RunApiTestsAsync — so this
        // isolates the HELPER's own catch/delete from SweepRunMarkerAsync's blanket end-of-run
        // coverage (which matches by marker regardless of whether pid was ever known, and would
        // otherwise mask a sabotaged helper: measured — see the mutation step in the PR). Faults the
        // feature-creation POST; the project POST just above it succeeds normally, so pid IS known
        // by the time this fires. That is exactly the window the helper's own try/catch closes by
        // deleting the already-known project id.
        // No token is cancelled here: ct is CancellationToken.None, and the exception below is
        // FaultInjectingHandler's simulated transport failure — the feature create commits server-side
        // and its response is lost, the residual window left once ct can no longer strike a create.
        await using var host = await ApiHost.StartAsync();
        var handler = new FaultInjectingHandler(
            host.BaseAddress,
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal)
                                                  && req.RequestUri.AbsolutePath.EndsWith("/features", StringComparison.Ordinal),
            faultOnNthMatch: 1);
        var runner = host.NewRunner(handler);
        var client = host.CreateFaultingClient(handler);
        const string marker = "kftestHELPERCATCH000001";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.CreateProjectFeatureTaskAsync(client, marker, CancellationToken.None));

        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.False(
            await db.Projects.AnyAsync(p => p.Name.Contains(marker)),
            "the helper's own catch should have deleted the project it just created, with no sweep involved");
    }

    [Fact]
    public async Task RunApiTestsAsync_LeavesNoOrphanedProject_WhenAFeatureCreateResponseIsLost()
    {
        // The same fault point as the direct helper test above, but driven through the full suite
        // this time — end-to-end confirmation that the helper fix and the outer sweep compose
        // correctly (either one alone is enough here; the direct test above is what actually proves
        // the helper's own catch is what does it, since this path lets the sweep cover for it too).
        // cts.Token is never cancelled here: this is FaultInjectingHandler's simulated transport
        // failure after the project id is known — the feature create's response is lost.
        await using var host = await ApiHost.StartAsync();
        var handler = new FaultInjectingHandler(
            host.BaseAddress,
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal)
                                                  && req.RequestUri.AbsolutePath.EndsWith("/features", StringComparison.Ordinal),
            faultOnNthMatch: 1);
        var runner = host.NewRunner(handler);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunApiTestsAsync(cts.Token));

        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(host.BaselineProjectCount, await db.Projects.CountAsync());
        Assert.Equal(host.BaselineFeatureCount, await db.Features.CountAsync());
    }

    [Fact]
    public async Task RunApiTestsAsync_SweepFindsAndDeletesTheProject_WhenTheProjectsOwnCreateResponseIsLost()
    {
        // Faults the 3rd POST to exactly "/api/projects": #1 is the "Create + delete project" test,
        // #2 is the "empty name" 422 test (no row committed), #3 is CreateProjectFeatureTaskAsync's
        // OWN project POST for RunApiTestsAsync's unwrapped scratch-project setup. Faulting there
        // means pid is NEVER captured — the residual window CreateProjectFeatureTaskAsync's try/catch
        // structurally cannot reach, because there is no id yet to delete by. Only a marker-based
        // sweep that finds the row without knowing its id beforehand can close this one.
        // This count is a claim about the suite's own call order, not an assumption: if it were
        // wrong and this fired on POST #1 instead (which IS RunTest-wrapped), the exception would be
        // swallowed into a failed TestResult, RunApiTestsAsync would return normally, and the
        // ThrowsAsync below would itself fail — the pin is self-verifying every time this test runs.
        // cts.Token is never cancelled here: the project create's response is lost to
        // FaultInjectingHandler's simulated transport failure before pid is captured — the residual
        // window left once ct can no longer strike a create, and the one SweepRunMarkerAsync's
        // best-effort sweep exists for.
        await using var host = await ApiHost.StartAsync();
        var handler = new FaultInjectingHandler(
            host.BaseAddress,
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/projects",
            faultOnNthMatch: 3);
        var runner = host.NewRunner(handler);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunApiTestsAsync(cts.Token));

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var survivors = await db.Projects.Select(p => p.Name).ToListAsync();
        Assert.Equal(host.BaselineProjectCount, survivors.Count);
        Assert.DoesNotContain(survivors, name => name.StartsWith("Test-", StringComparison.Ordinal));
        // Pins RunEndOfSuiteCleanupAsync's sweep-then-purge order: the leaked project here has no
        // feature/task, so the ONLY event this scenario can ever produce is the sweep's own delete's
        // ProjectDeleted (DELETE /api/projects/{id} routes through ITaskService.DeleteProjectAsync,
        // which audits it — ApiEndpoints.cs:556-558). Swapping the order leaves that event orphaned
        // (the purge already ran before the project existed to be deleted) instead of purging it.
        Assert.Equal(host.BaselineEventCount, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task RunApiTestsAsync_SweepFindsAndDeletesTheProject_WhenTheLeakingCreateIsInsideARunTestWrapper()
    {
        // Reproduces the EXACT row this fix's own pre-fix abort-harness run found on a live process
        // (port 5341, suite=all, a raw-socket abort): a bare "Test-Project-<guid>" row with no
        // feature/task, from the "Create + delete project" test's OWN inline create at line ~60 —
        // NOT one of the ~30 CreateProjectFeatureTaskAsync call sites F1's original evidence named.
        // That create sits INSIDE a RunTest(...) wrapper, which catches every Exception (including
        // TaskCanceledException) into a failed TestResult instead of letting it propagate — so unlike
        // tests 2/3 above, RunApiTestsAsync does NOT throw directly from this fault. Cancelling the
        // real token before throwing makes every later RunTest-wrapped test in the loop fail fast on
        // its own first ct-bound await (matching F1's own account of a live abort), until the
        // cascade reaches CreateProjectFeatureTaskAsync's unwrapped call and THAT throws outward.
        // Only the marker sweep — which does not depend on any single call site's control flow —
        // catches the earlier, swallowed leak.
        await using var host = await ApiHost.StartAsync();
        using var cts = new CancellationTokenSource();
        var handler = new FaultInjectingHandler(
            host.BaseAddress,
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/projects",
            faultOnNthMatch: 1,
            cancelBeforeThrow: cts);
        var runner = host.NewRunner(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunApiTestsAsync(cts.Token));

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var survivors = await db.Projects.Select(p => p.Name).ToListAsync();
        Assert.Equal(host.BaselineProjectCount, survivors.Count);
        Assert.DoesNotContain(survivors, name => name.StartsWith("Test-Project-", StringComparison.Ordinal));
        // See the previous test's identical assertion for why this pins the sweep-then-purge order.
        Assert.Equal(host.BaselineEventCount, await db.ActivityEvents.CountAsync());
    }

    [Fact]
    public async Task SweepRunMarkerAsync_DeletesOnlyMarkedNonPreExistingRows_LeavesEverythingElseAlone()
    {
        await using var host = await ApiHost.StartAsync();
        var runner = host.NewRunner(new FaultInjectingHandler(host.BaseAddress, _ => false, faultOnNthMatch: 0));
        const string marker = "kftestSWEEPUNITTEST0001";

        const string otherMarker = "kftestOTHERRUN00000000";

        int leakedProjectId, controlProjectId, preExistingButMarkedProjectId, otherRunProjectId,
            leakedIdeaId, controlIdeaId, otherRunIdeaId;
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            // (1) What a real leak looks like: the marker landed in the Name because NewName put it
            // there, but nothing ever came back to record or delete this row by id.
            var leaked = new Project { Name = $"Test-{marker}-a1b2c3d4" };
            // (2) Negative control: ordinary data with no marker. Must never be touched.
            var control = new Project { Name = "Real User Project" };
            // (4) Negative control for the marker's OWN specificity, not the pre-existing-id guard:
            // a fixture-SHAPED row from a DIFFERENT run's marker. Contains(marker) already rejects
            // this today (the two marker strings share no substring), but mutating that predicate to
            // the weaker Contains("kftest") would match it — this is what makes that mutation visible.
            var otherRun = new Project { Name = $"Test-{otherMarker}-a1b2c3d4" };
            db.Projects.AddRange(leaked, control, otherRun);
            await db.SaveChangesAsync();
            leakedProjectId = leaked.Id;
            controlProjectId = control.Id;
            otherRunProjectId = otherRun.Id;

            // (3) Negative control for the second, structural guard: a project whose name is EXACTLY
            // a fixture's, but which is listed as pre-existing, must still survive. Real fixtures are
            // never pre-existing by construction (they are created after the snapshot is taken), so it
            // takes a reused marker (or a 60-bit coincidence) for this to happen — the exclusion must
            // hold even then. Its name is a genuine NewName fixture name, so the pre-existing guard is
            // the only thing that can save it.
            var preExistingButMarked = new Project { Name = TestRunnerService.NewName("Test", marker) };
            db.Projects.Add(preExistingButMarked);
            await db.SaveChangesAsync();
            preExistingButMarkedProjectId = preExistingButMarked.Id;

            var leakedIdea = new Idea { Title = $"{marker} Round-trip idea" }; // a registered literal title
            var controlIdea = new Idea { Title = "Real user idea" };
            var otherRunIdea = new Idea { Title = $"{otherMarker} idea" };
            db.Ideas.AddRange(leakedIdea, controlIdea, otherRunIdea);
            await db.SaveChangesAsync();
            leakedIdeaId = leakedIdea.Id;
            controlIdeaId = controlIdea.Id;
            otherRunIdeaId = otherRunIdea.Id;
        }

        await runner.SweepRunMarkerAsync(host.RealClient, marker, preExistingProjectIds: [preExistingButMarkedProjectId]);

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.False(await verify.Projects.AnyAsync(p => p.Id == leakedProjectId), "sweep should delete the marker-tagged leaked project");
        Assert.True(await verify.Projects.AnyAsync(p => p.Id == controlProjectId), "sweep must not touch a project without the marker");
        Assert.True(await verify.Projects.AnyAsync(p => p.Id == preExistingButMarkedProjectId), "sweep must not touch a marker-matching project excluded as pre-existing");
        Assert.True(await verify.Projects.AnyAsync(p => p.Id == otherRunProjectId), "sweep must not touch a DIFFERENT run's fixture");
        Assert.False(await verify.Ideas.AnyAsync(i => i.Id == leakedIdeaId), "sweep should delete the marker-tagged leaked idea");
        Assert.True(await verify.Ideas.AnyAsync(i => i.Id == controlIdeaId), "sweep must not touch an idea without the marker");
        Assert.True(await verify.Ideas.AnyAsync(i => i.Id == otherRunIdeaId), "sweep must not touch a DIFFERENT run's fixture");
    }

    [Fact]
    public async Task SweepRunMarkerAsync_DoesNotDeleteARealRowThatMerelyQuotesAFixtureName()
    {
        // Hardening: Name/Title.Contains(marker) alone would ALSO match a real row that happens to
        // mention or quote a fixture by name — e.g. someone writing a note about a run's output.
        // IsFixtureProjectName/IsFixtureIdeaTitle require the exact shape a fixture actually has;
        // this is what proves a merely-quoting row survives (fails today against Contains alone).
        await using var host = await ApiHost.StartAsync();
        var runner = host.NewRunner(new FaultInjectingHandler(host.BaseAddress, _ => false, faultOnNthMatch: 0));
        const string marker = "kftestQUOTINGROW00000001";

        int quotingProjectId, quotingIdeaId;
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var quotingProject = new Project { Name = $"Please rename Test-{marker}-a1b2c3d4 to something better" };
            var quotingIdea = new Idea { Title = $"Remember to check the {marker} run's output" };
            db.Projects.Add(quotingProject);
            db.Ideas.Add(quotingIdea);
            await db.SaveChangesAsync();
            quotingProjectId = quotingProject.Id;
            quotingIdeaId = quotingIdea.Id;
        }

        await runner.SweepRunMarkerAsync(host.RealClient, marker, preExistingProjectIds: []);

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.True(await verify.Projects.AnyAsync(p => p.Id == quotingProjectId), "sweep must not delete a row that merely quotes a fixture name");
        Assert.True(await verify.Ideas.AnyAsync(i => i.Id == quotingIdeaId), "sweep must not delete a row that merely mentions a run marker");
    }

    [Fact]
    public async Task SweepRunMarkerAsync_DeletesExactFixtureNames_ButNotRealRowsThatBeginWithOne()
    {
        // #541: a predicate that checks only a fixture name's PREFIX also deletes a real row created
        // during the run whose name begins with a fixture's full name and goes on. Both groups below
        // are seeded after the pre-existing snapshot, so only the exact-shape check stands between the
        // second group and the sweep. The first group is the positive control: a predicate that
        // rejected everything would leave it behind, and fail here too.
        await using var host = await ApiHost.StartAsync();
        var runner = host.NewRunner(new FaultInjectingHandler(host.BaseAddress, _ => false, faultOnNthMatch: 0));
        var marker = TestRunnerService.NewRunMarker();

        List<int> preExisting;
        await using (var db = await host.DbFactory.CreateDbContextAsync())
            preExisting = await db.Projects.Select(p => p.Id).ToListAsync();

        // (i) One genuine fixture of each kind; (ii) a real row that begins with each one's full name.
        var fixtureProjects = TestRunnerService.FixtureProjectPrefixes
            .Select(prefix => TestRunnerService.NewName(prefix, marker))
            .Append($"{marker} to promote")
            .ToList();
        var fixtureIdeas = new List<string> { TestRunnerService.NewName("UiIdea", marker), $"{marker} Round-trip idea" };
        var realProjects = fixtureProjects.Select(name => name + " notes").ToList();
        var realIdeas = fixtureIdeas.Select(title => title + " (copy)").ToList();

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Projects.AddRange(fixtureProjects.Concat(realProjects).Select(name => new Project { Name = name }));
            db.Ideas.AddRange(fixtureIdeas.Concat(realIdeas).Select(title => new Idea { Title = title }));
            await db.SaveChangesAsync();
        }

        await runner.SweepRunMarkerAsync(host.RealClient, marker, preExisting);

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        var projects = await verify.Projects.Select(p => p.Name).ToListAsync();
        var ideas = await verify.Ideas.Select(i => i.Title).ToListAsync();
        var deletedRealRows = realProjects.Except(projects).Concat(realIdeas.Except(ideas)).ToList();
        Assert.True(deletedRealRows.Count == 0,
            $"the sweep deleted real rows that only BEGIN with a fixture's name: {string.Join("; ", deletedRealRows)}");
        var survivingFixtures = fixtureProjects.Intersect(projects).Concat(fixtureIdeas.Intersect(ideas)).ToList();
        Assert.True(survivingFixtures.Count == 0,
            $"the sweep left genuine fixtures behind: {string.Join("; ", survivingFixtures)}");
    }

    [Fact]
    public async Task EveryRegisteredFixtureShape_SurvivesTheRealApi_AndIsStillRecognizedWhenReadBack()
    {
        // #541: the sweep matches a fixture's Name/Title EXACTLY, so any server-side normalization (a
        // trim, a truncation, a promote that decorates the name) would silently un-sweep that fixture.
        // Each registered prefix and literal is created through the real routes the way the runner creates
        // it (projects for the project prefixes, ideas for the idea prefix and the literals, plus the
        // promoted project) and read back FROM THE DATABASE; the predicate runs on what was stored, never
        // on the string that was sent.
        await using var host = await ApiHost.StartAsync();
        var http = host.RealClient;
        var marker = TestRunnerService.NewRunMarker();
        var unrecognized = new List<string>();

        async Task<int> CreatedIdAsync(Task<HttpResponseMessage> send)
        {
            using var response = await send;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }

        async Task CheckProjectAsync(string what, int projectId)
        {
            await using var db = await host.DbFactory.CreateDbContextAsync();
            var stored = (await db.Projects.SingleAsync(p => p.Id == projectId)).Name;
            if (!TestRunnerService.IsFixtureProjectName(stored, marker))
                unrecognized.Add($"{what}: project stored as '{stored}'");
        }

        async Task CheckIdeaAsync(string what, int ideaId)
        {
            await using var db = await host.DbFactory.CreateDbContextAsync();
            var stored = (await db.Ideas.SingleAsync(i => i.Id == ideaId)).Title;
            if (!TestRunnerService.IsFixtureIdeaTitle(stored, marker))
                unrecognized.Add($"{what}: idea stored as '{stored}'");
        }

        foreach (var prefix in TestRunnerService.FixtureProjectPrefixes)
            await CheckProjectAsync($"NewName(\"{prefix}\")", await CreatedIdAsync(
                http.PostAsJsonAsync("/api/projects", new { name = TestRunnerService.NewName(prefix, marker) })));
        foreach (var prefix in TestRunnerService.FixtureIdeaPrefixes)
            await CheckIdeaAsync($"NewName(\"{prefix}\")", await CreatedIdAsync(
                http.PostAsJsonAsync("/api/ideas", new { title = TestRunnerService.NewName(prefix, marker) })));
        foreach (var literal in TestRunnerService.FixtureLiterals)
            await CheckIdeaAsync($"\"{literal}\"", await CreatedIdAsync(
                http.PostAsJsonAsync("/api/ideas", new { title = $"{marker} {literal}" })));

        // The two titles the runner reaches only by PATCH: the stored value after the rename is what counts.
        foreach (var (original, renamed) in new[] { ("before rename", "renamed"), ("audit idea", "audit idea (edited)") })
        {
            var ideaId = await CreatedIdAsync(http.PostAsJsonAsync("/api/ideas", new { title = $"{marker} {original}" }));
            using var patch = await http.PatchAsJsonAsync($"/api/ideas/{ideaId}", new { title = $"{marker} {renamed}" });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            await CheckIdeaAsync($"\"{original}\" PATCHed to \"{renamed}\"", ideaId);
        }

        // The promoted project: PromoteAsync names it after the idea, as the runner's promote test does.
        var ideaToPromote = await CreatedIdAsync(
            http.PostAsJsonAsync("/api/ideas", new { title = $"{marker} to promote", body = "b", plan = "p" }));
        using (var promote = await http.PostAsJsonAsync($"/api/ideas/{ideaToPromote}/promote", new { actor = "test-runner" }))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
            var projectId = (await promote.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetInt32();
            await CheckProjectAsync("the project promoted from \"to promote\"", projectId);
        }

        // "blank enum" exactly as the runner sends it, with a blank priority. Measured here rather than
        // assumed: it is rejected before any row is written, so it never commits one.
        async Task<int> BlankEnumRowsAsync()
        {
            await using var db = await host.DbFactory.CreateDbContextAsync();
            return await db.Ideas.CountAsync(i => i.Title == $"{marker} blank enum");
        }

        var blankEnumRowsBefore = await BlankEnumRowsAsync();
        using (var blank = await http.PostAsJsonAsync("/api/ideas", new { title = $"{marker} blank enum", priority = " " }))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(blankEnumRowsBefore, await BlankEnumRowsAsync());

        Assert.True(unrecognized.Count == 0,
            $"a registered fixture shape is no longer recognized once stored: {string.Join("; ", unrecognized)}. " +
            "The server (or NewName) changed what is written, and the sweep would silently stop finding that fixture.");
    }

    [Fact]
    public void IsFixtureShapeChecks_RecognizeEveryPrefixNewNameActuallyProduces()
    {
        // #541: pins the registered prefixes (TestRunnerService.FixtureProjectPrefixes/
        // FixtureIdeaPrefixes) against every prefix the file ACTUALLY passes to NewName, by reading
        // the real source rather than hand-listing prefixes here a second time, and runs each through
        // the real NewName and the sweep's own predicates. A future NewName("SomeNewPrefix", ...) call
        // site added without registering its prefix fails this test — that drift is exactly what would
        // make SweepRunMarkerAsync silently stop covering that fixture's residual-window leaks.
        var source = RepositorySource.Read("src/KanbanFlow/Services/TestRunnerService.cs");
        // The patterns below allow whitespace before "(" (NewName ("X", ...) is still a call), so
        // full-line comments are blanked first: prose such as "passes to NewName (#541)" is not one.
        var code = Regex.Replace(source, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
        // The literal must be followed by its comma: NewName("UiHome" + "Board", ...) runs with the
        // prefix UiHomeBoard, so reading only its first literal would check the wrong prefix.
        var literalSites = Regex.Matches(code, @"NewName\s*\(""([^""]+)""\s*,");
        var prefixes = literalSites
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // Guards the guard: if the regex stops matching at all (NewName renamed, quoting style
        // changed), this must fail loudly rather than silently passing over zero prefixes.
        Assert.True(
            prefixes.Count >= 4,
            $"expected to find at least the 4 known NewName(...) prefixes by reading the source; " +
            $"found {prefixes.Count}: {string.Join(", ", prefixes)}. Either the file changed in a way " +
            "this regex no longer parses, or the source path below is stale.");

        // ...and the floor above cannot notice a call site the regex never read. It reads only a
        // STRING-LITERAL prefix, so NewName(nameof(HttpClient), runMarker) — or a const, or a local —
        // would be checked against neither shape function below while the count stays >= 4. Every
        // NewName( call site must be one the regex read; the definition is subtracted as found, not
        // assumed.
        var definitions = Regex.Matches(code, @"\bstring\s+NewName\s*\(").Count;
        var callSites = Regex.Matches(code, @"\bNewName\s*\(").Count - definitions;
        Assert.True(
            literalSites.Count == callSites,
            $"found {callSites} NewName( call site(s) besides its {definitions} definition(s), but only " +
            $"{literalSites.Count} with a string-literal prefix this test can read. A prefix it cannot " +
            "read is never checked against IsFixtureProjectName/IsFixtureIdeaTitle — pass NewName a " +
            "string literal, or teach this test the new form.");

        const string marker = "kftestN4PIN00000000001";
        foreach (var prefix in prefixes)
        {
            var produced = TestRunnerService.NewName(prefix, marker); // the real producer, not a copy of its format
            var recognized = TestRunnerService.IsFixtureProjectName(produced, marker)
                           || TestRunnerService.IsFixtureIdeaTitle(produced, marker);
            Assert.True(recognized,
                $"NewName(\"{prefix}\", ...) produces '{produced}', which neither IsFixtureProjectName " +
                "nor IsFixtureIdeaTitle recognizes — SweepRunMarkerAsync would silently stop finding " +
                "this fixture's residual-window leaks. Register the prefix in FixtureProjectPrefixes or " +
                "FixtureIdeaPrefixes.");
        }
    }

    [Fact]
    public void EveryUseOfRunMarker_IsARegisteredFixtureShape_OrAKnownPassThrough()
    {
        // #541: SweepRunMarkerAsync deletes a row only when its Name/Title is EXACTLY a registered
        // fixture shape (TestRunnerService.FixtureProjectPrefixes/FixtureIdeaPrefixes/FixtureLiterals),
        // so every name this file builds from runMarker must be one — an unregistered one would leak past
        // the sweep unseen. This accounts for EVERY use of runMarker in the source: a use it does not
        // recognize fails rather than being skipped, so a concatenation, a string.Format, or a differently
        // shaped interpolation cannot slip past a pattern that never looked for it.
        var source = RepositorySource.Read("src/KanbanFlow/Services/TestRunnerService.cs");
        var code = Regex.Replace(source, @"^[ \t]*//.*$", "", RegexOptions.Multiline); // doc and line comments
        var registeredPrefixes = TestRunnerService.FixtureProjectPrefixes
            .Concat(TestRunnerService.FixtureIdeaPrefixes)
            .ToHashSet(StringComparer.Ordinal);
        var registeredLiterals = TestRunnerService.FixtureLiterals.ToHashSet(StringComparer.Ordinal);

        var uses = Regex.Matches(code, @"\brunMarker\b").Select(m => m.Index).ToList();
        var accountedFor = new HashSet<int>();
        int LineOf(int index) => code[..index].Count(c => c == '\n') + 1;
        MatchCollection Account(string pattern)
        {
            var matches = Regex.Matches(code, pattern);
            foreach (Match m in matches)
                accountedFor.UnionWith(uses.Where(i => i >= m.Index && i < m.Index + m.Length));
            return matches;
        }

        // The constructions: each must carry a registered prefix or literal.
        var newNameCalls = Account(@"\bNewName\s*\(\s*""(?<value>[^""\\]*)""\s*,\s*runMarker\s*\)");
        var literalTitles = Account(@"\$""\{runMarker\} (?<value>[^""{}\\]*)""");
        var violations = newNameCalls
            .Where(m => !registeredPrefixes.Contains(m.Groups["value"].Value))
            .Select(m => $"line {LineOf(m.Index)}: unregistered NewName prefix \"{m.Groups["value"].Value}\"")
            .Concat(literalTitles
                .Where(m => !registeredLiterals.Contains(m.Groups["value"].Value))
                .Select(m => $"line {LineOf(m.Index)}: unregistered literal title \"{m.Groups["value"].Value}\""))
            .ToList();

        // Every other use this file is known to make: NewName's own definition (the one producer of the
        // {prefix}-{marker}-{hex} shape), the declarations, pass-throughs to the helpers that carry it
        // on, and the sweep's reads — its candidate filter, its shape checks and its log arguments.
        Account(@"\$""\{prefix\}-\{runMarker\}-\{Guid\.NewGuid\(\):N\}""\[\.\.\(prefix\.Length \+ runMarker\.Length \+ 10\)\]");
        Account(@"\bvar\s+runMarker\s*=\s*NewRunMarker\(\)\s*;");
        Account(@"\bstring\s+runMarker\b");
        Account(@"\b(?:CreateProjectFeatureTaskAsync|RunEndOfSuiteCleanupAsync|SweepRunMarkerAsync)\s*\(\s*http\s*,\s*runMarker\s*,");
        Account(@"\.Contains\(\s*runMarker\s*\)");
        Account(@"\bIsFixture(?:ProjectName|IdeaTitle)\(\s*\w+\.\w+\s*,\s*runMarker\s*\)");
        Account(@"\blogger\.Log\w+\((?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!))\)");

        var lines = code.Split('\n');
        violations.AddRange(uses.Where(i => !accountedFor.Contains(i)).Select(i =>
            $"line {LineOf(i)}: unrecognized use of runMarker: {lines[LineOf(i) - 1].Trim()}"));

        // NON-VACUITY: the patterns really read this file (at least the 4 NewName calls and 11 literal
        // titles it is known to build), and every registry entry is one it actually produces.
        Assert.True(newNameCalls.Count >= 4 && literalTitles.Count >= 11,
            $"found only {newNameCalls.Count} NewName call(s) and {literalTitles.Count} literal title(s) built " +
            "from runMarker, fewer than TestRunnerService.cs is known to have: the patterns no longer read it.");
        var producedPrefixes = newNameCalls.Select(m => m.Groups["value"].Value).ToHashSet(StringComparer.Ordinal);
        var producedLiterals = literalTitles.Select(m => m.Groups["value"].Value).ToHashSet(StringComparer.Ordinal);
        var dead = registeredPrefixes.Where(p => !producedPrefixes.Contains(p))
            .Concat(registeredLiterals.Where(l => !producedLiterals.Contains(l)))
            .ToList();
        Assert.True(dead.Count == 0,
            $"registered but never produced by TestRunnerService.cs: {string.Join(", ", dead)}. Remove it: " +
            "the sweep tries to delete every run-created row whose name the registry accepts, so an entry the " +
            "runner never produces only widens what it would delete.");

        Assert.True(violations.Count == 0,
            "TestRunnerService.cs builds a name from runMarker that the sweep's registry does not cover: " +
            $"{string.Join("; ", violations)}. " +
            "Use NewName with a prefix registered in FixtureProjectPrefixes or FixtureIdeaPrefixes, or " +
            "$\"{runMarker} <literal>\" with the literal registered in FixtureLiterals.");
    }

    [Fact]
    public void EveryPostNotBoundToTheCallersToken_IsPostCreateAsyncsOwnSend()
    {
        // #541 pin. Every tracked fixture create — each project and idea, and the scratch project's
        // feature and task — sends with CancellationToken.None (see PostCreateAsync's remarks); the child
        // rows a test adds under a scratch project stay bound to ct, and go with their project by cascade
        // delete. A None-bound send is one HttpClient no longer refuses by itself once ct has fired:
        // PostCreateAsync's check just before the send is the only thing that does, so a POST that is
        // neither bound to ct nor PostCreateAsync's own goes out regardless. The behaviour test above
        // cannot see such a site. It drives only RunApiTestsAsync, and once ct fires that throws at
        // its first create outside a RunTest wrapper (the scratch-project setup), so no create after
        // that point is ever reached (measured: reverting only the round-trip idea create to a raw
        // PostAsJsonAsync(..., CancellationToken.None) leaves every other test in this file green).
        // So this reads the source instead.
        var source = RepositorySource.Read("src/KanbanFlow/Services/TestRunnerService.cs");

        // Every POST-capable HttpClient send, with its WHOLE argument list: parens are balanced (.NET
        // balancing groups), so a multi-line `new { ... }` body or a nested call such as
        // TaskPriority.High.ToString() cannot end a match early. Whitespace before the "(" is allowed:
        // .PostAsJsonAsync ("/api/ideas", ...) is still a send.
        const string sendCall = @"\.(?:PostAsJsonAsync|PostAsync|SendAsync)(?:<[^<>()]*>)?\s*\(";
        var sends = Regex.Matches(source, sendCall + @"(?<args>(?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!)))\)")
            .ToList();
        // NON-VACUITY: every send the bare pattern finds began a balanced match of its own, so none was
        // swallowed by a neighbour's match running long — as a string literal holding an unbalanced
        // paren can make one do.
        Assert.Equal(Regex.Matches(source, sendCall).Count, sends.Count);

        // A send whose last argument is ct is one HttpClient refuses by itself once ct has fired.
        // Anything else (CancellationToken.None, default, or no token at all) goes out regardless.
        var unbound = sends.Where(m => !Regex.IsMatch(m.Groups["args"].Value, @",\s*ct\s*$")).ToList();

        var definition = Regex.Match(source, @"Task<HttpResponseMessage>\s+PostCreateAsync\(");
        var body = Regex.Match(source[definition.Index..], @"\{(?>[^{}]+|\{(?<depth>)|\}(?<-depth>))*(?(depth)(?!))\}");
        // NON-VACUITY: the matcher finds PostCreateAsync, and inside it the one legitimate unbound send.
        Assert.True(definition.Success && body.Success, "PostCreateAsync's definition or body was not found in TestRunnerService.cs.");
        var bodyStart = definition.Index + body.Index;
        bool InsidePostCreateAsync(Match send) => send.Index >= bodyStart && send.Index < bodyStart + body.Length;
        var legitimate = Assert.Single(unbound, InsidePostCreateAsync);
        Assert.Contains("CancellationToken.None", legitimate.Groups["args"].Value, StringComparison.Ordinal);

        var bypasses = unbound
            .Where(send => !InsidePostCreateAsync(send))
            .Select(send => $"line {source[..send.Index].Count(c => c == '\n') + 1}: {Regex.Replace(send.Value, @"\s+", " ")}")
            .ToList();
        Assert.True(bypasses.Count == 0,
            $"TestRunnerService.cs sends a POST not bound to ct outside PostCreateAsync: {string.Join("; ", bypasses)}. "
            + "It skips PostCreateAsync's pre-send check, so a cancelled suite keeps sending it (and its DELETE) "
            + "to the live database instead of sending no further create (#541). Send a create through PostCreateAsync; "
            + "bind any other POST to ct.");
    }

    /// <summary>
    /// What each transport handler below must refuse: the three ports the user's real app and other
    /// sandboxes may be bound to (<c>http://127.0.0.1:5123</c> is exactly <c>TestRunnerService.GetBaseUrl</c>'s
    /// fallback), a non-loopback host, and a loopback port that is neither forbidden nor the handler's own
    /// host (5402) — own-host-only means exactly that. Written out rather than read from
    /// <see cref="ForbiddenPorts"/>, so deleting a port from that list fails this test instead of quietly
    /// shrinking it.
    /// </summary>
    public static TheoryData<string, string> RefusedDestinations()
    {
        var data = new TheoryData<string, string>();
        foreach (var handler in new[] { nameof(CancelAfterFirstThenCountCreatesHandler), nameof(FaultInjectingHandler) })
        {
            data.Add(handler, "http://127.0.0.1:5123/api/projects");
            data.Add(handler, "http://127.0.0.1:5148/api/projects");
            data.Add(handler, "http://127.0.0.1:5198/api/projects");
            data.Add(handler, "http://example.invalid/api/projects");
            data.Add(handler, "http://127.0.0.1:5402/api/projects");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedDestinations))]
    public async Task TransportHandlers_RefuseAnythingButThisTestsOwnLoopbackHost_BeforeTheInnerHandlerSeesIt(string handler, string url)
    {
        // #541: outside this test, both handlers wrap a REAL SocketsHttpHandler, so a handler
        // without this refusal is one fallback away from the user's real board. If the server ever
        // reported no http:// address, GetBaseUrl would hand the suite http://127.0.0.1:5123, and every
        // create and DELETE the suite sent would land there. The inner handler HERE is a recording stub
        // that never opens a socket, which is what makes aiming at those ports on purpose safe.
        var stub = new RecordingStubHandler();
        using var cts = new CancellationTokenSource();
        using var client = new HttpClient(Wrap(handler, stub, cts));

        var refused = await Record.ExceptionAsync(() => client.PostAsJsonAsync(url, new { name = "never sent" }));
        Assert.True(refused is InvalidOperationException && refused.Message.Contains("refused to send", StringComparison.Ordinal),
            $"{handler} did not refuse {url} ({refused?.GetType().Name ?? "no exception"}); " +
            $"the inner handler was reached {stub.Calls} time(s).");
        Assert.Equal(0, stub.Calls);

        // NON-VACUITY: the same handler over the same stub does pass its own host's address (the
        // stub's http://127.0.0.1:80) through, so the 0 above is the refusal, not a stub that never
        // counts or a handler that refuses everything.
        using var allowed = await client.PostAsJsonAsync("http://127.0.0.1/api/projects", new { name = "stub only" });
        Assert.Equal(1, stub.Calls);
    }

    /// <summary>The own host the refusal theory builds its handlers for: the only address their guard lets through to the stub.</summary>
    private static readonly Uri StubHost = new("http://127.0.0.1:80");

    /// <summary>The named transport handler, wrapped around <paramref name="inner"/> instead of a real socket handler.</summary>
    private static DelegatingHandler Wrap(string handler, HttpMessageHandler inner, CancellationTokenSource cts) => handler switch
    {
        nameof(CancelAfterFirstThenCountCreatesHandler) => new CancelAfterFirstThenCountCreatesHandler(StubHost, cts, _ => false, inner),
        nameof(FaultInjectingHandler) => new FaultInjectingHandler(StubHost, _ => false, faultOnNthMatch: 0, inner: inner),
        _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, "not a transport handler in this file"),
    };

    /// <summary>
    /// Ports the user's real app / other implementers' sandboxes may be bound to. No transport handler
    /// here may ever reach them, regardless of what a test configures it to match.
    /// </summary>
    private static readonly int[] ForbiddenPorts = [5123, 5148, 5198];

    /// <summary>
    /// The refusal both transport handlers below make before sending anything: the request must go to
    /// <paramref name="ownHost"/> — the test host's own base address — with exactly its scheme, host and
    /// port, and be loopback; a port in <see cref="ForbiddenPorts"/> is refused even then, as defence in
    /// depth. HttpClient resolves a relative request URI against its BaseAddress before any handler sees
    /// it, so one that is still relative, or missing, is refused too. One copy, so the two handlers
    /// cannot drift apart on what they refuse.
    /// </summary>
    private static void RefuseUnlessOwnLoopbackHost(HttpRequestMessage request, Uri ownHost, string handlerName)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || !uri.IsLoopback
            || !string.Equals(uri.Scheme, ownHost.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, ownHost.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != ownHost.Port
            || Array.IndexOf(ForbiddenPorts, uri.Port) >= 0)
            throw new InvalidOperationException(
                $"{handlerName} refused to send to {uri} — must stay on this test's own host {ownHost}.");
    }

    /// <summary>
    /// Lets the suite's FIRST request (of any kind) complete normally, then cancels <paramref
    /// name="cts"/> right after — simulating cancellation landing once a suite is genuinely
    /// underway, not before it starts. From that point on, counts requests matching <paramref
    /// name="match"/> that the REAL server still answers with <c>201 Created</c> — never "requests
    /// attempted": letting the real request go through (rather than intercepting it) and gating on
    /// the STATUS CODE is what makes the count trustworthy, since a request whose token is already
    /// cancelled by the time HttpClient looks at it can throw before ever writing a byte to the wire,
    /// which would make an attempt-count include sends that never happened. It sends only to
    /// <paramref name="ownHost"/> (see <see cref="RefuseUnlessOwnLoopbackHost"/>). <paramref name="inner"/>
    /// defaults to a real <see cref="SocketsHttpHandler"/>; only the refusal test swaps in a stub.
    /// </summary>
    private sealed class CancelAfterFirstThenCountCreatesHandler(
        Uri ownHost, CancellationTokenSource cts, Func<HttpRequestMessage, bool> match, HttpMessageHandler? inner = null)
        : DelegatingHandler(inner ?? new SocketsHttpHandler())
    {
        private bool _cancelled;
        private int _successfulCreatesAfterCancel;
        public int SuccessfulCreateCountAfterCancel => _successfulCreatesAfterCancel;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RefuseUnlessOwnLoopbackHost(request, ownHost, nameof(CancelAfterFirstThenCountCreatesHandler));

            var response = await base.SendAsync(request, cancellationToken);

            if (!_cancelled)
            {
                _cancelled = true;
                cts.Cancel();
            }
            else if (match(request) && response.StatusCode == HttpStatusCode.Created)
            {
                Interlocked.Increment(ref _successfulCreatesAfterCancel);
            }

            return response;
        }
    }

    /// <summary>
    /// Simulates a create whose response is lost after the server committed it — before #541's fix, the
    /// caller's token firing mid-read; since it, a timeout or a transport reset: lets the REAL request
    /// reach the REAL server (so any server-side effect genuinely happens), then — only on the
    /// configured Nth request matching <paramref name="match"/> —
    /// discards the real response and throws instead of returning it. When <paramref name="cancelBeforeThrow"/>
    /// is supplied, it is cancelled first, so every LATER call on the same token fails fast for real —
    /// matching F1's own account of a live abort ("every subsequent test in the loop fails fast on its
    /// own first ct-bound await with nothing yet sent, so at most one leak per aborted connection")
    /// instead of letting the rest of the suite run to completion behind a fault that only fires once.
    /// It sends only to <paramref name="ownHost"/> (see <see cref="RefuseUnlessOwnLoopbackHost"/>).
    /// <paramref name="inner"/> defaults to a real <see cref="SocketsHttpHandler"/>; only the refusal
    /// test swaps in a stub.
    /// </summary>
    private sealed class FaultInjectingHandler(
        Uri ownHost, Func<HttpRequestMessage, bool> match, int faultOnNthMatch, CancellationTokenSource? cancelBeforeThrow = null,
        HttpMessageHandler? inner = null)
        : DelegatingHandler(inner ?? new SocketsHttpHandler())
    {
        private int _remaining = faultOnNthMatch;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RefuseUnlessOwnLoopbackHost(request, ownHost, nameof(FaultInjectingHandler));

            var response = await base.SendAsync(request, cancellationToken);

            if (match(request) && Interlocked.Decrement(ref _remaining) == 0)
            {
                response.Dispose();
                cancelBeforeThrow?.Cancel();
                // TaskCanceledException because that is what HttpClient throws when HttpClient.Timeout fires.
                throw new TaskCanceledException(
                    $"simulated transport failure: the response to {request.Method} {request.RequestUri} " +
                    "was lost after the server committed it, before it was read");
            }

            return response;
        }
    }

    /// <summary>
    /// An inner handler that never opens a socket: counts every request that reaches it and answers
    /// <c>201 Created</c>. What lets the refusal test aim at the user's real ports on purpose.
    /// </summary>
    private sealed class RecordingStubHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { RequestMessage = request });
        }
    }

    /// <summary>
    /// Pins the server exactly inside the window the independent review of this fix's first version
    /// found: <c>POST /api/projects</c> (<c>ApiEndpoints.cs:492</c>, <c>SaveChangesAsync</c> with no
    /// token) commits once the server has read the request, regardless of what the CLIENT's token
    /// does — so a cancellation racing that commit is a real, server-side timing question, not
    /// something any client-side fault injection (like <see cref="FaultInjectingHandler"/>) can
    /// reach. <see cref="SavingChangesAsync"/> runs BEFORE any command executes, so blocking there
    /// means the row genuinely does not exist yet — a SELECT run while blocked sees nothing, exactly
    /// as it would against the real race. Fires at most once, on the first `Added` <see cref="Project"/>
    /// whose Name starts with <paramref name="namePrefix"/>; every later save passes straight through.
    /// </summary>
    private sealed class BlockFirstMatchingProjectSaveInterceptor(string namePrefix) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _armed = true;

        /// <summary>Completes once the matching save has been intercepted and is blocked.</summary>
        public Task Entered => _entered.Task;

        /// <summary>
        /// Completes once the blocked save has fully finished, successfully or not — measured, the
        /// actual database write lands asynchronously relative to <see cref="Release"/> returning (a
        /// fixed post-release delay was not reliable), so a caller must await this, not merely call
        /// <see cref="Release"/>, before checking the database for what the save did.
        /// </summary>
        public Task Settled => _settled.Task;

        /// <summary>Lets the blocked save proceed. Safe to call more than once or if never entered.</summary>
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed && eventData.Context is { } context &&
                context.ChangeTracker.Entries<Project>().Any(e =>
                    e.State == EntityState.Added && e.Entity.Name.StartsWith(namePrefix, StringComparison.Ordinal)))
            {
                _armed = false; // only ever block once — later saves in the same run pass straight through
                _entered.TrySetResult();
                // Bounded independently of any test's own timing: this must never be the reason a
                // stuck test hangs forever if a test above forgets to call Release().
                await Task.WhenAny(_release.Task, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            }

            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            _settled.TrySetResult();
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            _settled.TrySetResult();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>A real Kestrel host on an ephemeral loopback port with the full REST API mapped over a real SQLite file.</summary>
    private sealed class ApiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _dbPath;

        private ApiHost(
            WebApplication app, string dbPath, HttpClient realClient,
            int baselineProjectCount, int baselineFeatureCount, int baselineEventCount)
        {
            _app = app;
            _dbPath = dbPath;
            RealClient = realClient;
            BaselineProjectCount = baselineProjectCount;
            BaselineFeatureCount = baselineFeatureCount;
            BaselineEventCount = baselineEventCount;
        }

        /// <summary>An ordinary, non-faulting client for setup/cleanup calls a test makes directly.</summary>
        public HttpClient RealClient { get; }

        /// <summary>This host's own base address — the only address a transport handler built with it will send to.</summary>
        public Uri BaseAddress => RealClient.BaseAddress!;

        public IDbContextFactory<AppDbContext> DbFactory => _app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

        /// <summary>Project/Feature/ActivityEvent counts before any test runs anything — this host seeds nothing, so all three are 0, but read rather than assumed (the "run the instrument against a known answer" habit).</summary>
        public int BaselineProjectCount { get; }
        public int BaselineFeatureCount { get; }
        public int BaselineEventCount { get; }

        /// <param name="interceptor">
        /// Opt-in per test, never global: registering it unconditionally would also pause test 6's
        /// direct EF-seeded "leaked fixture" rows, which are added outside any suite run and must not
        /// be affected by a gate meant only for a real <c>SaveChangesAsync</c> made through the API.
        /// </param>
        public static async Task<ApiHost> StartAsync(IInterceptor? interceptor = null)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"kanbanflow-cancel-{Guid.NewGuid():N}.db");
            using (var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                       .UseSqlite($"Data Source={dbPath};Pooling=False").Options))
            {
                seed.Database.EnsureCreated();
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral port: never the user's real app

            builder.Services.AddDbContextFactory<AppDbContext>(o =>
            {
                o.UseSqlite($"Data Source={dbPath};Pooling=False");
                if (interceptor is not null) o.AddInterceptors(interceptor);
            });
            builder.Services.AddSingleton(new ClaimTtlSetting(ClaimPolicy.DefaultTtl));
            builder.Services.AddScoped<ITaskService>(sp => new TaskService(sp.GetRequiredService<IDbContextFactory<AppDbContext>>()));
            builder.Services.AddScoped<ITaskDependencyService, TaskDependencyService>();
            builder.Services.AddScoped<IIdeaService, IdeaService>();

            // Routes this host never calls, so a throwing stub is enough to satisfy the endpoint
            // table's first-request metadata build (see AdminHealthEndpointTests for the same trick
            // and why it is needed: an unregistered handler-parameter type on ANY mapped route can
            // fail metadata building for the whole app, not just that one route).
            foreach (var offRoute in new[] { typeof(IMemoryService), typeof(IMemorySearch), typeof(IExternalWorkItemService) })
            {
                builder.Services.AddSingleton(offRoute, (Func<IServiceProvider, object>)(_ =>
                    throw new NotSupportedException(
                        "this test host maps the full API surface (ApiEndpoints.MapApiEndpoints has no " +
                        "narrower entry point) but only exercises project/feature/task/idea routes")));
            }

            var app = builder.Build();
            try
            {
                app.MapApiEndpoints();
                await app.StartAsync();

                var address = app.Urls.First();
                var uri = new Uri(address);
                // Hard safety gate, independent of FaultInjectingHandler's own: this host itself must
                // never be the user's real app or another implementer's sandbox.
                if (uri.Host != "127.0.0.1" || uri.Port is 5123 or 5148 or 5198)
                    throw new InvalidOperationException($"test host bound an unexpected address {address} — refusing to proceed");

                var realClient = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10) };

                var dbFactory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync();
                var baselineProjects = await db.Projects.CountAsync();
                var baselineFeatures = await db.Features.CountAsync();
                var baselineEvents = await db.ActivityEvents.CountAsync();

                return new ApiHost(app, dbPath, realClient, baselineProjects, baselineFeatures, baselineEvents);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <summary>An HttpClient pointed at this host's real address, routed through <paramref name="handler"/> — for tests that drive an internal helper directly rather than through a Run*TestsAsync suite (which builds its own via <see cref="NewRunner"/>/<see cref="FaultingHttpClientFactory"/>).</summary>
        public HttpClient CreateFaultingClient(DelegatingHandler handler) =>
            new(handler, disposeHandler: false) { BaseAddress = RealClient.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>A <see cref="TestRunnerService"/> wired to this host's real server + real db, with its outbound HttpClient routed through <paramref name="handler"/>.</summary>
        public TestRunnerService NewRunner(DelegatingHandler handler) => new(
            new FaultingHttpClientFactory(handler),
            _app.Services.GetRequiredService<IServer>(),
            new TaskService(DbFactory),
            DbFactory,
            new MarkdownRenderer(),
            NullLogger<TestRunnerService>.Instance);

        private sealed class FaultingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            // disposeHandler: false — one handler instance is shared across every CreateClient call a
            // single test makes (TestRunnerService calls this once per suite run), and its own
            // per-request state (the fault countdown) must survive across that suite's whole HttpClient.
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async ValueTask DisposeAsync()
        {
            RealClient.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                try { File.Delete(_dbPath + suffix); }
                catch (IOException) { /* a host still holding the handle is not a test failure */ }
            }
        }
    }
}
