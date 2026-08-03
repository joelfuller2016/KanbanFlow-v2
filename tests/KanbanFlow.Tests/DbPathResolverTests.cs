using System.Runtime.CompilerServices;
using KanbanFlow.Data;

namespace KanbanFlow.Tests;

/// <summary>
/// Regression tests for <see cref="DbPathResolver"/> — which database file the app opens (#354).
///
/// <para>The defect these encode: the path was built from <c>Environment.CurrentDirectory</c>, so the
/// same build opened a different file depending on the directory it was launched from, and
/// <c>EnsureCreated()</c> silently produced a fresh, empty database instead of finding the real one.
/// The load-bearing assertions are therefore the ones that resolve twice under DIFFERENT launch
/// directories and demand the SAME path back.</para>
///
/// <para>Real filesystem, real temp directories, no mocks. The launch directory is passed in rather
/// than set on the process, so the cases stay deterministic under xunit's parallel runner — the
/// production entry point feeds <c>Environment.CurrentDirectory</c> into this exact overload.
/// Because the host derives its content root from the launch directory (verified empirically: under
/// <c>dotnet run</c> and when executing the built assembly, <c>ContentRootPath</c> equalled
/// <c>CurrentDirectory</c> in every case), the tests move BOTH together. Pinning the content root
/// while varying only the launch directory would let a content-root-based default pass while still
/// being launch-dependent in production.</para>
/// </summary>
public sealed class DbPathResolverTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    private string NewDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kf354-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private static string WriteDatabase(string directory)
    {
        var path = Path.Combine(directory, DbPathResolver.FileName);
        File.WriteAllText(path, "not really SQLite — the resolver only asks whether the file exists");
        return path;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }

    // ── KANBANFLOW_DB_PATH: the escape hatch every harness depends on. Behaviour is unchanged. ──

    [Fact]
    public void EnvironmentVariable_Wins_OverEveryOtherCandidate()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        WriteDatabase(launch);   // both would otherwise be probed and hit
        WriteDatabase(app);
        var requested = Path.Combine(NewDirectory("explicit"), "chosen.db");

        var resolution = DbPathResolver.Resolve(requested, launch, launch, app);

        Assert.Equal(DbPathResolver.SourceEnvironment, resolution.Source);
        Assert.Equal(requested, resolution.Path);
        Assert.Empty(resolution.ShadowedPaths);
    }

    [Fact]
    public void EnvironmentVariable_IsAnchoredToAnAbsolutePath_AndItsParentDirectoryIsCreated()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var parent = Path.Combine(NewDirectory("explicit"), "nested", "deeper");
        var requested = Path.Combine(parent, "chosen.db");

        Assert.False(Directory.Exists(parent));

        var resolution = DbPathResolver.Resolve(requested, launch, launch, app);

        Assert.True(Path.IsPathFullyQualified(resolution.Path));
        Assert.Equal(requested, resolution.Path);
        Assert.True(Directory.Exists(parent), "the parent directory of the configured path must be created");
        Assert.False(resolution.Existed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnvironmentVariable_BlankIsTreatedAsUnset(string? configured)
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        WriteDatabase(app);

        var resolution = DbPathResolver.Resolve(configured, launch, launch, app);

        // Only the claim in the name: a blank override does not take the env branch. WHERE the
        // fall-through lands is owned by the probe/default cases below — asserting it here as well
        // would make this case go red for a probe-ordering change that has nothing to do with it.
        Assert.NotEqual(DbPathResolver.SourceEnvironment, resolution.Source);
    }

    [Fact]
    public void EnvironmentVariable_ReportsWhetherTheConfiguredFileAlreadyExists()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var existing = WriteDatabase(NewDirectory("explicit"));

        Assert.True(DbPathResolver.Resolve(existing, launch, launch, app).Existed);
        Assert.False(DbPathResolver.Resolve(Path.Combine(app, "absent.db"), launch, launch, app).Existed);
    }

    // ── Probe: an existing database is FOUND, never shadowed by a new empty one. ──

    [Fact]
    public void Probe_PrefersTheLaunchDirectory_PreservingTheHistoricalBehaviour()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var expected = WriteDatabase(launch);

        var resolution = DbPathResolver.Resolve(null, launch, launch, app);

        Assert.Equal(DbPathResolver.SourceProbe, resolution.Source);
        Assert.Equal(expected, resolution.Path);
        Assert.True(resolution.Existed);
    }

    [Fact]
    public void Probe_FindsTheDatabaseBesideTheApp_WhenTheLaunchDirectoryHasNone()
    {
        // The real-world failure: started from some unrelated directory, with the actual database
        // sitting next to the application. Before the fix this created a second, empty database in
        // the launch directory and reported nothing.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var expected = WriteDatabase(app);

        var resolution = DbPathResolver.Resolve(null, launch, launch, app);

        Assert.Equal(DbPathResolver.SourceProbe, resolution.Source);
        Assert.Equal(expected, resolution.Path);
        Assert.True(resolution.Existed);
        Assert.False(File.Exists(Path.Combine(launch, DbPathResolver.FileName)));
    }

    [Fact]
    public void Probe_ResolvesToTheSameFile_FromTwoDifferentLaunchDirectories()
    {
        // AC1, at the unit level: same build, two launch directories, ONE database.
        var app = NewDirectory("app");
        var expected = WriteDatabase(app);
        var launchA = NewDirectory("launchA");
        var launchB = NewDirectory("launchB");

        var fromA = DbPathResolver.Resolve(null, launchA, launchA, app);
        var fromB = DbPathResolver.Resolve(null, launchB, launchB, app);

        Assert.Equal(expected, fromA.Path);
        Assert.Equal(fromA.Path, fromB.Path);
    }

    [Fact]
    public void Probe_ReportsASecondDatabaseThatWasNotOpened()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var opened = WriteDatabase(launch);
        var shadowed = WriteDatabase(app);

        var resolution = DbPathResolver.Resolve(null, launch, launch, app);

        Assert.Equal(opened, resolution.Path);
        Assert.Equal(new[] { shadowed }, resolution.ShadowedPaths);
    }

    [Fact]
    public void Probe_ReportsNoShadowing_WhenOnlyOneDatabaseExists()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        WriteDatabase(app);

        Assert.Empty(DbPathResolver.Resolve(null, launch, launch, app).ShadowedPaths);
    }

    [Fact]
    public void Probe_DoesNotReportTheContentRootAsShadowing_WhenItIsTheLaunchDirectory()
    {
        // Duplicate candidates must collapse: the host derives the content root from the launch
        // directory, so without de-duplication every single-database run would warn about itself.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        WriteDatabase(launch);

        var resolution = DbPathResolver.Resolve(null, launch, launch, app);

        Assert.Empty(resolution.ShadowedPaths);
    }

    [Fact]
    public void Probe_ChecksTheContentRoot_WhenItDiffersFromTheLaunchDirectory()
    {
        var launch = NewDirectory("launch");
        var contentRoot = NewDirectory("contentroot");
        var app = NewDirectory("app");
        var expected = WriteDatabase(contentRoot);

        var resolution = DbPathResolver.Resolve(null, launch, contentRoot, app);

        Assert.Equal(DbPathResolver.SourceProbe, resolution.Source);
        Assert.Equal(expected, resolution.Path);
    }

    // ── Default: nothing exists anywhere. THIS is the assertion that encodes the bug. ──

    [Fact]
    public void Default_IsIdenticalAcrossTwoDifferentLaunchDirectories()
    {
        // The bug, stated as an equality. Before the fix each launch directory produced its own
        // path — and its own silently-created, empty database.
        var app = NewDirectory("app");
        var launchA = NewDirectory("launchA");
        var launchB = NewDirectory("launchB");

        var fromA = DbPathResolver.Resolve(null, launchA, launchA, app);
        var fromB = DbPathResolver.Resolve(null, launchB, launchB, app);

        Assert.Equal(DbPathResolver.SourceDefault, fromA.Source);
        Assert.Equal(DbPathResolver.SourceDefault, fromB.Source);
        Assert.Equal(fromA.Path, fromB.Path);
        Assert.False(fromA.Existed);
    }

    [Fact]
    public void Default_LandsBesideTheApp_NotInTheLaunchDirectory()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");

        var resolution = DbPathResolver.Resolve(null, launch, launch, app);

        Assert.Equal(Path.Combine(app, DbPathResolver.FileName), resolution.Path);
        Assert.NotEqual(Path.Combine(launch, DbPathResolver.FileName), resolution.Path);
    }

    [Fact]
    public void Default_CreatesNoDatabaseFile_ItOnlyNamesOne()
    {
        // Creating the file is EnsureCreated()'s job (#86 owns schema); the resolver must not
        // pre-create an empty file, which would make every later probe report Existed=true.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");

        var resolution = DbPathResolver.Resolve(null, launch, launch, app);

        Assert.False(File.Exists(resolution.Path));
    }

    // ── The launch-independent anchor itself. ──

    [Fact]
    public void AppAnchor_StripsTheDevelopmentBuildLayout_SoDotnetCleanCannotDeleteTheDatabase()
    {
        // bin/<Configuration>/<TargetFramework> is gitignored and erased by `dotnet clean`.
        var project = NewDirectory("project");
        var binDirectory = Path.Combine(project, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(Path.Combine(project, "KanbanFlow.csproj"), "<Project />");

        Assert.Equal(project, DbPathResolver.StripDevelopmentBuildLayout(binDirectory));
        Assert.Equal(project, DbPathResolver.StripDevelopmentBuildLayout(binDirectory + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AppAnchor_LeavesAPublishedLayoutAlone()
    {
        // No project file above it ⇒ not a development build ⇒ the app directory IS the anchor,
        // even when it happens to sit under a folder named "bin".
        var published = Path.Combine(NewDirectory("published"), "bin", "Release", "net10.0");
        Directory.CreateDirectory(published);

        Assert.Equal(published, DbPathResolver.StripDevelopmentBuildLayout(published));
    }

    [Fact]
    public void AppAnchor_LeavesAFlatApplicationDirectoryAlone()
    {
        var flat = NewDirectory("flat");

        Assert.Equal(flat, DbPathResolver.StripDevelopmentBuildLayout(flat));
    }

    [Fact]
    public void AppAnchor_IsDerivedFromTheAssemblyLocation_NotTheLaunchDirectory()
    {
        // Asserted against the real running test host: the anchor is a stable, fully-qualified
        // directory derived from AppContext.BaseDirectory — which, unlike Environment.CurrentDirectory,
        // does not move with the process's working directory. (Launch-independence of the resolved
        // path itself is proven by the two-launch-directory cases above, which need no global state.)
        var anchor = DbPathResolver.AppAnchorDirectory();

        Assert.True(Path.IsPathFullyQualified(anchor));
        Assert.Equal(anchor, DbPathResolver.AppAnchorDirectory());
        Assert.Equal(DbPathResolver.StripDevelopmentBuildLayout(AppContext.BaseDirectory), anchor);
    }

    // ── AC5: the rule exists in exactly one place. Two copies is how it drifted (#354 Evidence 4). ──

    [Theory]
    [InlineData("src/KanbanFlow/Program.cs")]
    [InlineData("src/KanbanFlow/Components/Pages/Settings.razor")]
    public void TheResolutionRuleIsNotReImplemented(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

        Assert.DoesNotContain("Environment.CurrentDirectory", source, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{DbPathResolver.EnvironmentVariableName}\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repository root, found by walking up from this file's own compile-time location — so it
    /// does not depend on the working directory the test host happens to run under.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        for (var directory = new FileInfo(thisFile).Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.slnx").Any())
                return directory.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no *.slnx) above '{thisFile}'.");
    }
}
