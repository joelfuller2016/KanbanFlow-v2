using Microsoft.Data.Sqlite;

namespace KanbanFlow.Data;

/// <summary>
/// The outcome of resolving which SQLite file the application will open.
/// </summary>
/// <param name="Path">The absolute path of the database that will be opened.</param>
/// <param name="Source">
/// How <see cref="Path"/> was chosen: <c>env</c> (the <c>KANBANFLOW_DB_PATH</c> override),
/// <c>probe</c> (an existing database was found), or <c>default</c> (nothing existed, so one is
/// created at the application's own directory).
/// </param>
/// <param name="Existed">Whether the file was already on disk at resolution time.</param>
/// <param name="ShadowedPaths">
/// Other candidate locations that also hold a <c>kanbanflow.db</c> but were NOT opened, in probe
/// order. Non-empty means two real databases exist and only one is in use — the exact situation
/// that reads to a user as "my data is gone" (#354). Reported, never enforced: no roadblocks.
/// </param>
public sealed record DbPathResolution(
    string Path,
    string Source,
    bool Existed,
    IReadOnlyList<string> ShadowedPaths);

/// <summary>
/// Single source of truth for <em>which</em> database file KanbanFlow opens (#354).
///
/// <para>Before this existed, <c>Program.cs</c> and <c>Settings.razor</c> each re-implemented
/// "<c>KANBANFLOW_DB_PATH</c> else <c>Environment.CurrentDirectory</c>". Resolving against the
/// launch directory means the same build opened a different file depending on where it was started
/// from, and <c>EnsureCreated()</c> silently produced a fresh, empty database rather than reporting
/// anything. Both call sites now render one resolution, so the boot log and the UI cannot drift.</para>
///
/// <para>This type decides only which file is opened. It never touches schema or content — that is
/// <c>EnsureCreated()</c> plus the <c>Ensure*</c> patchers, and remains untouched (#86).</para>
/// </summary>
public static class DbPathResolver
{
    /// <summary>The database file name, in every candidate directory.</summary>
    public const string FileName = "kanbanflow.db";

    /// <summary>The opt-in absolute/relative override honoured ahead of every other candidate.</summary>
    public const string EnvironmentVariableName = "KANBANFLOW_DB_PATH";

    /// <summary><see cref="DbPathResolution.Source"/> when <see cref="EnvironmentVariableName"/> was set.</summary>
    public const string SourceEnvironment = "env";

    /// <summary><see cref="DbPathResolution.Source"/> when an existing database was found.</summary>
    public const string SourceProbe = "probe";

    /// <summary><see cref="DbPathResolution.Source"/> when nothing existed and a new file is created.</summary>
    public const string SourceDefault = "default";

    /// <summary>
    /// How two candidate paths are compared when de-duplicating the probe list, for this host.
    /// </summary>
    /// <remarks>
    /// <para>Matched to each platform's default filesystem semantics. Windows (NTFS) and macOS
    /// (APFS/HFS+) are case-insensitive: the launch directory and the content root must collapse to one
    /// candidate there, or a single database is reported as shadowing itself. Linux is case-sensitive,
    /// where collapsing would merge two genuinely distinct files and silently drop one from the probe —
    /// the failure this whole type exists to prevent.</para>
    ///
    /// <para>Filesystems can always be formatted against their platform default (case-sensitive APFS,
    /// per-directory case sensitivity on NTFS for WSL), and a mounted volume can differ from the boot
    /// volume. Detecting that for real means probing the filesystem at runtime — writing a file and
    /// re-statting it under another case — which is far too much I/O and failure surface for bootstrap
    /// path resolution, before logging even exists. The platform default is the right policy; the
    /// residual mis-match is a redundant shadow warning, which is visible and harmless.</para>
    ///
    /// <para>This is only the DEFAULT. <see cref="Resolve(string?, string, string, string, StringComparer?)"/>
    /// takes the comparer as a parameter so the behaviour of BOTH branches is tested on any host —
    /// a test that reads this property would assert only whatever the test machine happens to be.</para>
    /// </remarks>
    internal static StringComparer DefaultPathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Resolve against the live process: the environment variable, the launch directory, the host's
    /// content root, and the application's own directory.
    /// </summary>
    /// <param name="contentRootPath">The host's content root (<c>IHostEnvironment.ContentRootPath</c>).</param>
    public static DbPathResolution Resolve(string contentRootPath) =>
        Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            Environment.CurrentDirectory,
            contentRootPath,
            AppAnchorDirectory());

    /// <summary>
    /// The resolution rule itself, with every input passed in so it can be exercised without
    /// mutating process-wide state.
    /// </summary>
    /// <param name="configuredPath">The raw <see cref="EnvironmentVariableName"/> value, or null/blank if unset.</param>
    /// <param name="currentDirectory">The launch directory (<c>Environment.CurrentDirectory</c>).</param>
    /// <param name="contentRootPath">The host content root. In practice equal to <paramref name="currentDirectory"/>.</param>
    /// <param name="appDirectory">The application's own directory — see <see cref="AppAnchorDirectory"/>.</param>
    /// <param name="pathComparer">
    /// How two candidate paths are compared when de-duplicating. Defaults to
    /// <see cref="DefaultPathComparer"/>, the running platform's filesystem semantics. Passed in by
    /// tests so both the case-sensitive and case-insensitive branches are exercised on any host.
    /// </param>
    public static DbPathResolution Resolve(
        string? configuredPath,
        string currentDirectory,
        string contentRootPath,
        string appDirectory,
        StringComparer? pathComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        pathComparer ??= DefaultPathComparer;

        // 1. The explicit override wins outright, exactly as it did before this resolver existed:
        //    anchored to an absolute path and its parent directory created. Every harness that wants
        //    an isolated database (scripts/smoke-api.ps1, scripts/ui-e2e.mjs) depends on this branch,
        //    so its behaviour is deliberately unchanged.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // Anchor a RELATIVE override against the launch directory that was passed in. The
            // single-argument Path.GetFullPath would reach for Environment.CurrentDirectory instead —
            // ambient process state, which is the very thing this type exists to stop depending on,
            // and which would make the currentDirectory parameter a lie for this branch. Identical in
            // production, where the two are the same value; absolute overrides ignore the base.
            var basePath = string.IsNullOrWhiteSpace(currentDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(currentDirectory);
            var configured = Path.GetFullPath(configuredPath, basePath);
            EnsureParentDirectory(configured);
            return new DbPathResolution(configured, SourceEnvironment, File.Exists(configured), []);
        }

        // 2. Otherwise look for a database that already exists, in a fixed order. The launch
        //    directory comes first so the historical behaviour is preserved whenever it happened to
        //    be right; the application's own directory comes last so a build started from anywhere
        //    still finds the database that ships beside it.
        //
        //    ACCEPTED TRADE-OFF (#354 review, D4). Starting from a brand-new directory with no
        //    override, the app now ADOPTS the database beside it and the user's edits go into that
        //    board — where before they got a fresh empty one scoped to that directory. That is a
        //    write into a board the user did not explicitly pick, so it is not purely safer; it is
        //    chosen deliberately. Rationale: (a) the alternative is the #354 bug itself, which
        //    presented as total data loss; (b) for the overwhelmingly common case — one board, run
        //    from wherever — adopting it is exactly what the user wants; (c) the per-directory-board
        //    workflow is undocumented and still works, since the launch directory is candidate #1
        //    once its own file exists; (d) nothing is destroyed or migrated, and the adoption is
        //    stated out loud at boot and on the Settings page rather than inferred. KANBANFLOW_DB_PATH
        //    remains the explicit way to pin one. Revisit if this actually surprises anyone.
        var candidates = new List<string>();
        foreach (var directory in new[] { currentDirectory, contentRootPath, appDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.GetFullPath(Path.Combine(directory, FileName));
            if (!candidates.Contains(candidate, pathComparer))
                candidates.Add(candidate);
        }

        var existing = candidates.Where(File.Exists).ToList();
        if (existing.Count > 0)
            return new DbPathResolution(existing[0], SourceProbe, true, existing.Skip(1).ToList());

        // 3. Nothing exists anywhere: create at the application's own directory, so a fresh install
        //    lands in one deterministic place no matter which directory it was started from.
        var fallback = Path.GetFullPath(Path.Combine(appDirectory, FileName));
        EnsureParentDirectory(fallback);
        return new DbPathResolution(fallback, SourceDefault, false, []);
    }

    /// <summary>
    /// The application's own directory — the one anchor that does not move with the launch directory.
    /// </summary>
    /// <remarks>
    /// <para><see cref="AppContext.BaseDirectory"/> is launch-independent but, under <c>dotnet run</c>,
    /// it is <c>bin/&lt;Configuration&gt;/&lt;TargetFramework&gt;/</c>. That directory is gitignored and
    /// routinely wiped wholesale — <c>git clean -xdf</c>, "delete bin/obj and rebuild" troubleshooting,
    /// IDE clean commands — so a database there is destroyed by an operation the user believes is safe.
    /// The development layout is therefore stripped back to the project directory, where the database
    /// already lives. (<c>dotnet clean</c> specifically is <em>not</em> the hazard: measured, it leaves
    /// stray files alone, because MSBuild's Clean deletes only tracked outputs recorded in
    /// <c>*.FileListAbsolute.txt</c>. The earlier claim that it erases the database was wrong.)</para>
    ///
    /// <para>The strip requires both a directory literally named <c>bin</c> two levels up AND a project
    /// file in the directory above it, so a published application that happens to sit under a folder
    /// named <c>bin</c> is never mistaken for a development build.</para>
    ///
    /// <para><b>Known gap (#383):</b> a RID-qualified build emits
    /// <c>bin/&lt;Configuration&gt;/&lt;TargetFramework&gt;/&lt;RID&gt;/</c>, so the three-level walk
    /// binds <c>bin</c> to the configuration segment, the name check fails, and the database IS created
    /// inside <c>bin/</c> — the outcome this method exists to prevent. Same class for
    /// <c>&lt;BaseOutputPath&gt;</c>, <c>&lt;OutputPath&gt;</c> and <c>--artifacts-path</c>. #383
    /// replaces the fixed walk with a bounded search for the nearest ancestor holding a project file;
    /// it is split out so the algorithm change does not invalidate this change's verification.</para>
    /// </remarks>
    public static string AppAnchorDirectory() => StripDevelopmentBuildLayout(AppContext.BaseDirectory);

    /// <summary>Strip a <c>bin/&lt;Configuration&gt;/&lt;TargetFramework&gt;</c> suffix; see <see cref="AppAnchorDirectory"/>.</summary>
    internal static string StripDevelopmentBuildLayout(string baseDirectory)
    {
        var targetFramework = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        var configuration = targetFramework.Parent;
        var bin = configuration?.Parent;
        var project = bin?.Parent;

        if (project is not null &&
            string.Equals(bin!.Name, "bin", StringComparison.OrdinalIgnoreCase) &&
            project.EnumerateFiles("*.csproj").Any())
        {
            return project.FullName;
        }

        return targetFramework.FullName;
    }

    /// <summary>
    /// The SQLite connection string for a resolved database path.
    /// </summary>
    /// <remarks>
    /// Built rather than interpolated. A connection string is a semicolon-delimited list of key/value
    /// pairs, so a path containing <c>;</c> or <c>=</c> would silently truncate the data source or
    /// introduce another keyword — opening the wrong database, or failing at startup. This resolver
    /// derives the default path from the application's own location, which is whatever directory the
    /// user installed into, so the path is not under our control. The builder quotes it correctly.
    /// </remarks>
    public static string ToConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString;

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
