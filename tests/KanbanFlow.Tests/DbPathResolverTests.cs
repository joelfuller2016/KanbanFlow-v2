using System.Runtime.CompilerServices;
using KanbanFlow.Data;
using Microsoft.Data.Sqlite;

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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not a test failure. UnauthorizedAccessException is
                // listed explicitly because Directory.Delete throws it for a read-only or locked
                // entry and it does NOT derive from IOException — uncaught, it would fail the whole
                // class for a cleanup problem rather than an assertion.
            }
        }
    }

    // ── KANBANFLOW_DB_PATH: the escape hatch every harness depends on. Behaviour is unchanged. ──

    [Fact]
    public void EnvironmentVariable_Wins_OverEveryOtherCandidate()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var shadowedByLaunch = WriteDatabase(launch);   // both would otherwise be probed and hit
        var shadowedByApp = WriteDatabase(app);
        var requested = Path.Combine(NewDirectory("explicit"), "chosen.db");

        var resolution = DbPathResolver.Resolve(requested, launch, launch, app);

        Assert.Equal(DbPathResolver.SourceEnvironment, resolution.Source);
        Assert.Equal(requested, resolution.Path);

        // The claim in this case's NAME — the override beats both candidates — is the two lines
        // above. This third line used to read Assert.Empty, which recorded the #498-review defect
        // rather than a requirement: the env branch hardcoded the list empty, so a condition the
        // health endpoint reports could never occur in the deployed configuration (every production
        // start pins the variable). Naming both abandoned boards is strictly stronger than
        // asserting the list is empty — it is the same two files, now checked by path.
        Assert.Equal([shadowedByLaunch, shadowedByApp], resolution.ShadowedPaths);
    }

    [Fact]
    public void EnvironmentVariable_NamesTheBoardsItDidNotOpen_AtTheApplicationsOwnAnchors()
    {
        // #498 review, finding 4. `shadowed-by-another-database` was dead code in production:
        // start-kanbanflow.ps1 always sets the variable and register-kanbanflow-task.ps1 pins
        // -DbPath, so every real start took the env branch — which returned an empty list by
        // construction. Two real boards existing while one is open is the #354 presentation itself.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var abandoned = WriteDatabase(app);
        var requested = Path.Combine(NewDirectory("explicit"), DbPathResolver.FileName);

        var resolution = DbPathResolver.Resolve(requested, launch, launch, app);

        Assert.Equal([abandoned], resolution.ShadowedPaths);
        // Reported, never enforced: the override still wins outright. No roadblocks (#354).
        Assert.Equal(requested, resolution.Path);
    }

    [Fact]
    public void EnvironmentVariable_DoesNotReportTheBoardItOpened_AsShadowingItself()
    {
        // The override pointing AT a candidate directory is the common harness shape:
        // scripts/smoke-api.ps1 launches with the working directory and KANBANFLOW_DB_PATH set to
        // the same fresh folder. Listing that file as its own shadow would make every smoke run
        // report `degraded` for a condition that does not exist.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var opened = WriteDatabase(launch);

        var resolution = DbPathResolver.Resolve(opened, launch, launch, app);

        Assert.Equal(opened, resolution.Path);
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
    public void EnvironmentVariable_RelativePath_AnchorsToTheLaunchDirectory_NotTheProcessWorkingDirectory()
    {
        // A relative override must resolve against the launch directory that was PASSED IN. The
        // single-argument Path.GetFullPath silently uses Environment.CurrentDirectory instead, which
        // is the ambient state this whole type exists to stop depending on.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");

        var resolution = DbPathResolver.Resolve(Path.Combine("nested", "board.db"), launch, launch, app);

        Assert.Equal(Path.Combine(launch, "nested", "board.db"), resolution.Path);
        // The temp launch directory is never the test host's working directory, so this pins that the
        // process-wide value was not consulted.
        Assert.NotEqual(Path.GetFullPath(Path.Combine("nested", "board.db")), resolution.Path);
    }

    [Fact]
    public void ToConnectionString_KeepsAPathContainingConnectionStringDelimiters_Intact()
    {
        // A connection string is a semicolon-delimited list of key=value pairs. Interpolating a path
        // that contains ';' or '=' truncates the data source or injects another keyword — opening the
        // wrong database or failing at startup. The default path comes from wherever the user
        // installed the app, so its characters are not under our control.
        var awkward = Path.Combine(NewDirectory("odd"), "we;ird=name.db");

        var connectionString = DbPathResolver.ToConnectionString(awkward);

        Assert.Equal(awkward, new SqliteConnectionStringBuilder(connectionString).DataSource);
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
    public void Adoption_IsDeliberate_AFreshLaunchDirectoryOpensTheDatabaseBesideTheApp()
    {
        // PINS AN ACCEPTED TRADE-OFF (#354 review, D4) — not merely a happy path.
        //
        // Started from a brand-new directory with no override, the app ADOPTS the database beside it,
        // and the user's subsequent edits are written INTO that board. Before #354 they would have got
        // a fresh empty board scoped to the launch directory. This is therefore a write into a board
        // the user did not explicitly pick, and it is chosen deliberately: the alternative is the #354
        // bug itself, which presented as total data loss. See the rationale beside the candidate list
        // in DbPathResolver.
        //
        // If this test ever fails, the trade-off has been reversed — which is a product decision, not
        // a bug to be patched back into green.
        var app = NewDirectory("app");
        var existingBoard = WriteDatabase(app);
        var freshLaunchDirectory = NewDirectory("fresh");

        var resolution = DbPathResolver.Resolve(null, freshLaunchDirectory, freshLaunchDirectory, app);

        Assert.Equal(DbPathResolver.SourceProbe, resolution.Source);
        Assert.Equal(existingBoard, resolution.Path);
        Assert.True(resolution.Existed);
        // ...and specifically NOT a new board of its own in the directory it was launched from.
        Assert.False(File.Exists(Path.Combine(freshLaunchDirectory, DbPathResolver.FileName)));
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

        // Assert the SOURCE, not just the path. Without this the case is blind to the very thing it
        // is named for: destroy the probe entirely and the default branch also lands on
        // app/kanbanflow.db, so both resolutions still agree and the test stays green. Proven — it
        // passed against a subject whose candidate list had been cut to the launch directory alone.
        Assert.Equal(DbPathResolver.SourceProbe, fromA.Source);
        Assert.Equal(DbPathResolver.SourceProbe, fromB.Source);
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

    // ── Candidate de-duplication vs. filesystem case sensitivity. ──
    //
    // The comparer is passed IN rather than read from the host, so both branches are exercised
    // wherever these run. A test that let the running OS pick would assert only whatever the test
    // machine happens to be, and would silently cover nothing on the other platform.

    /// <summary>
    /// Two spellings of one directory path that differ ONLY by case, with a database readable at each.
    /// On a case-insensitive filesystem that is one directory and one file addressed two ways; on a
    /// case-sensitive one it is two directories and two files. Either way both paths exist, so the
    /// comparer — not the host's filesystem — is what decides whether they collapse.
    /// </summary>
    private (string Lower, string Upper) NewCaseVariantDirectories()
    {
        var lower = NewDirectory("case");
        var upper = lower.ToUpperInvariant();

        Directory.CreateDirectory(upper);   // no-op on Windows/macOS; a second directory on Linux
        _tempDirectories.Add(upper);
        WriteDatabase(lower);
        WriteDatabase(upper);

        return (lower, upper);
    }

    [Fact]
    public void Probe_CaseInsensitiveComparer_CollapsesCandidatesDifferingOnlyByCase()
    {
        // Windows / default macOS: one directory spelled two ways is ONE file. It must collapse, or a
        // single database is reported as shadowing itself.
        var (lower, upper) = NewCaseVariantDirectories();

        var resolution = DbPathResolver.Resolve(
            null, lower, upper, lower, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(Path.Combine(lower, DbPathResolver.FileName), resolution.Path);
        Assert.Empty(resolution.ShadowedPaths);
    }

    [Fact]
    public void Probe_CaseSensitiveComparer_KeepsCandidatesDifferingOnlyByCase()
    {
        // Linux: two paths differing only by case are two DIFFERENT files, and collapsing them would
        // drop a real database from the probe — the failure this type exists to prevent. Both must
        // survive de-duplication, so the second is reported as shadowed rather than vanishing.
        var (lower, upper) = NewCaseVariantDirectories();

        var resolution = DbPathResolver.Resolve(
            null, lower, upper, lower, StringComparer.Ordinal);

        Assert.Equal(Path.Combine(lower, DbPathResolver.FileName), resolution.Path);
        Assert.Equal(new[] { Path.Combine(upper, DbPathResolver.FileName) }, resolution.ShadowedPaths);
    }

    [Fact]
    public void DefaultPathComparer_MatchesThePlatformsFilesystemSemantics()
    {
        // The policy itself: case-insensitive where the platform default filesystem is
        // (Windows NTFS, macOS APFS/HFS+), case-sensitive on Linux.
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        Assert.Same(expected, DbPathResolver.DefaultPathComparer);
    }

    [Fact]
    public void Resolve_DefaultsToThePlatformComparer_WhenNoneIsPassed()
    {
        // Ties the injectable parameter back to the production call path: omitting the comparer must
        // behave identically to passing the platform default, or the tests above would be exercising
        // a code path the app never takes.
        var app = NewDirectory("app");
        WriteDatabase(app);
        var launch = NewDirectory("launch");

        var withDefault = DbPathResolver.Resolve(null, launch, launch, app);
        var withExplicitComparer = DbPathResolver.Resolve(
            null, launch, launch, app, DbPathResolver.DefaultPathComparer);

        // Field-by-field, not record equality: ShadowedPaths is an IReadOnlyList, which the compiler's
        // generated Equals compares by REFERENCE, so two identical resolutions would never be equal.
        Assert.Equal(withExplicitComparer.Path, withDefault.Path);
        Assert.Equal(withExplicitComparer.Source, withDefault.Source);
        Assert.Equal(withExplicitComparer.Existed, withDefault.Existed);
        Assert.Equal(withExplicitComparer.ShadowedPaths, withDefault.ShadowedPaths);
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
    public void AppAnchor_StripsTheDevelopmentBuildLayout_SoTheDatabaseIsNotInsideBin()
    {
        // bin/<Configuration>/<TargetFramework> is gitignored and routinely wiped wholesale by
        // `git clean -xdf` and by "delete bin/obj and rebuild". NOT by `dotnet clean` — that was
        // measured and leaves stray files alone; see AppAnchorDirectory's remarks. What is under
        // test here is the strip itself, not any particular delete command.
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

    // ── The boot-time capture: WHEN the resolution happens is the whole signal (#498 review, 5). ──

    /// <summary>
    /// <see cref="DbPathResolution.Existed"/> and <see cref="DbPathResolution.Snapshot"/> are worth
    /// exactly as much as the instant they were taken. Both describe the database <em>as the
    /// application found it</em>, which is only true while the resolution is computed once, eagerly,
    /// before <c>EnsureCreated()</c> and <c>SeedData</c> run.
    ///
    /// <para>The one-line "cleanup" a future contributor writes —
    /// <c>AddSingleton(_ =&gt; DbPathResolver.Resolve(...))</c> — makes the singleton lazy, so it is
    /// first materialised by the health REQUEST, long after the app created and seeded the file. The
    /// endpoint then answers <c>existedAtStartup: true</c> while the app's own boot log says
    /// <c>existing=false</c>: the detector reports the opposite of what happened, and every
    /// behavioural test still passes, because each one injects its own resolution.</para>
    ///
    /// <para>A GUARD, not a reproduction — like <see cref="TheResolutionRuleIsNotReImplemented"/>
    /// above, it reads the source rather than running it. <c>scripts/smoke-api.ps1</c> is the
    /// behavioural bind and does catch this (exit 1); this case exists because <c>dotnet test</c> is
    /// the loop contributors actually run, and it stayed green through the sabotage. It is anchored
    /// on the two structural properties rather than on any identifier, so renaming the local does
    /// not defeat it.</para>
    /// </summary>
    [Fact]
    public void TheResolutionIsCapturedEagerlyAtBoot_BeforeTheDatabaseIsCreated()
    {
        // Comments only, stripped: Program.cs explains this very hazard in prose ABOVE the code
        // that avoids it, so raw text positions would report the explanation as the call site.
        var code = File.ReadAllLines(Path.Combine(RepositoryRoot(), "src/KanbanFlow/Program.cs"))
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        // 1. The registration must hand over an ALREADY-COMPUTED resolution. A factory lambda
        //    defers the stat until first use, which for this singleton is the first health request.
        var registration = Assert.Single(
            code,
            line => line.Contains("AddSingleton", StringComparison.Ordinal)
                    && line.Contains("Resol", StringComparison.Ordinal));
        Assert.DoesNotContain("=>", registration);

        // 2. And it must be computed before the schema/seed work that changes the very facts it
        //    captures — a resolution moved below EnsureCreated() reports the state the app itself
        //    produced, which is the same defect by a different route.
        var resolvedAt = code.FindIndex(line =>
            line.Contains($"{nameof(DbPathResolver)}.{nameof(DbPathResolver.Resolve)}(", StringComparison.Ordinal));
        var ensureCreatedAt = code.FindIndex(line => line.Contains("EnsureCreated(", StringComparison.Ordinal));
        Assert.True(resolvedAt >= 0, "Program.cs no longer resolves the database path through DbPathResolver");
        Assert.True(ensureCreatedAt >= 0, "Program.cs no longer calls EnsureCreated(); re-anchor this guard");
        Assert.True(
            resolvedAt < ensureCreatedAt,
            "the database path must be resolved BEFORE EnsureCreated(), or existedAtStartup and the "
            + "file facts describe the board this process just made rather than the one it found");
    }

    // ── The file's own facts, captured with Existed from one stat (#498 review, 2). ──

    [Fact]
    public void Resolution_CapturesTheFilesOwnFacts_WhenTheBoardAlreadyExists()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var existing = WriteDatabase(NewDirectory("explicit"));
        var onDisk = new FileInfo(existing);

        foreach (var resolution in new[]
                 {
                     DbPathResolver.Resolve(existing, launch, launch, app),   // env branch
                     DbPathResolver.Resolve(null, Path.GetDirectoryName(existing)!, launch, app), // probe branch
                 })
        {
            var snapshot = Assert.IsType<DbFileSnapshot>(resolution.Snapshot);
            Assert.Equal(onDisk.Length, snapshot.SizeBytes);
            Assert.Equal(onDisk.CreationTimeUtc, snapshot.CreatedAtUtc);
            Assert.Equal(onDisk.LastWriteTimeUtc, snapshot.LastWriteUtc);
        }
    }

    [Fact]
    public void Resolution_CarriesTheCopySignature_WhenTheBoardWasRestoredFromASeed()
    {
        // The literal Copy-Item signature, reproduced: the destination is created NOW and inherits
        // the source's older last-write time, so the board reports having been written before it
        // existed. Impossible for a board that has been used since it was made — and the only
        // in-app trace left when a launcher SUCCESSFULLY restores the seed onto the wrong path.
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");
        var restored = WriteDatabase(NewDirectory("wrong-root"));
        var seedWrittenAt = DateTime.UtcNow.AddHours(-3);
        File.SetLastWriteTimeUtc(restored, seedWrittenAt);

        var resolution = DbPathResolver.Resolve(restored, launch, launch, app);

        var snapshot = Assert.IsType<DbFileSnapshot>(resolution.Snapshot);
        Assert.Equal(seedWrittenAt, snapshot.LastWriteUtc);
        Assert.True(
            snapshot.LastWriteUtc < snapshot.CreatedAtUtc,
            $"last write {snapshot.LastWriteUtc:o} should precede creation {snapshot.CreatedAtUtc:o}");
    }

    [Fact]
    public void Resolution_HasNoFileFacts_WhenThereIsNoFileToStat()
    {
        var launch = NewDirectory("launch");
        var app = NewDirectory("app");

        // Both branches that resolve to a file which is not there: the explicit override, and the
        // fall-through default. This binds one direction only — no file, therefore no facts. The
        // converse does NOT hold: Existed can be true with a null snapshot when the file is there
        // but cannot be stat'd (#513), so this case is not evidence of a biconditional.
        var configured = DbPathResolver.Resolve(Path.Combine(app, "absent.db"), launch, launch, app);
        Assert.False(configured.Existed);
        Assert.Null(configured.Snapshot);

        var fallback = DbPathResolver.Resolve(null, launch, launch, app);
        Assert.False(fallback.Existed);
        Assert.Null(fallback.Snapshot);
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

        // Deliberately loud rather than skipped. [CallerFilePath] bakes in the BUILD machine's source
        // path, so this case needs the source tree beside the test assembly — true for `dotnet test`
        // from a checkout, which is this repo's only gate (no CI). Dynamic skipping would need xunit
        // v3 (Assert.Skip); on 2.9.3 the alternative is a silent no-op, and a guard that quietly
        // passes when it cannot run is exactly the failure mode this guard exists to catch.
        throw new InvalidOperationException(
            $"Could not locate the repository root (no *.slnx) above '{thisFile}'. This case reads the "
            + "source files it guards, so it must run from a checkout — run `dotnet test` from the "
            + "repository rather than against a copied-out test assembly.");
    }
}
