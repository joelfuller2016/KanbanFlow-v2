namespace KanbanFlow.Services;

/// <summary>
/// In-process test runner. Exposed via the Settings panel and the
/// <c>/api/admin/run-tests</c> endpoint so an agent or human can verify
/// the live app from inside it. Closes the "no automated tests inside the
/// running app" gap from the 2026-05-09 review (smoke-api.ps1 still exists
/// for cold-boot CI; this is the warm in-process companion).
/// </summary>
public interface ITestRunnerService
{
    /// <summary>HTTP-level smoke covering the most important REST endpoints with isolated test data.</summary>
    Task<TestSuiteResult> RunApiTestsAsync(CancellationToken ct = default);

    /// <summary>Render-only smoke for each Razor page route — verifies SSR pipeline produces a 200 with expected markup.</summary>
    Task<TestSuiteResult> RunUiTestsAsync(CancellationToken ct = default);

    /// <summary>Direct invocations of <see cref="ITaskService"/> against an isolated SQLite file. Verifies audit/comment/exception contracts.</summary>
    Task<TestSuiteResult> RunServiceTestsAsync(CancellationToken ct = default);

    /// <summary>Runs all three suites sequentially and returns a combined result.</summary>
    Task<TestSuiteResult> RunAllTestsAsync(CancellationToken ct = default);
}

public sealed record TestResult(
    string Name,
    string Category,
    bool Passed,
    double DurationMs,
    string? Error = null);

public sealed record TestSuiteResult(
    string Suite,
    IReadOnlyList<TestResult> Tests,
    double TotalDurationMs)
{
    public int Passed => Tests.Count(t => t.Passed);
    public int Failed => Tests.Count(t => !t.Passed);
    public int Total => Tests.Count;
    public bool AllPassed => Failed == 0 && Total > 0;
}
