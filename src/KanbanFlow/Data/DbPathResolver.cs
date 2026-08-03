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
    public static DbPathResolution Resolve(
        string? configuredPath,
        string currentDirectory,
        string contentRootPath,
        string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        // 1. The explicit override wins outright, exactly as it did before this resolver existed:
        //    anchored to an absolute path and its parent directory created. Every harness that wants
        //    an isolated database (scripts/smoke-api.ps1, scripts/ui-e2e.mjs) depends on this branch,
        //    so its behaviour is deliberately unchanged.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(configuredPath);
            EnsureParentDirectory(configured);
            return new DbPathResolution(configured, SourceEnvironment, File.Exists(configured), []);
        }

        // 2. Otherwise look for a database that already exists, in a fixed order. The launch
        //    directory comes first so the historical behaviour is preserved whenever it happened to
        //    be right; the application's own directory comes last so a build started from anywhere
        //    still finds the database that ships beside it.
        var candidates = new List<string>();
        foreach (var directory in new[] { currentDirectory, contentRootPath, appDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.GetFullPath(Path.Combine(directory, FileName));
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
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
    /// <see cref="AppContext.BaseDirectory"/> is launch-independent but, under <c>dotnet run</c>, it
    /// is <c>bin/&lt;Configuration&gt;/&lt;TargetFramework&gt;/</c> — gitignored and erased by
    /// <c>dotnet clean</c>, which would take the database with it. That development layout is
    /// therefore stripped back to the project directory, where the database already lives. The strip
    /// requires both a directory literally named <c>bin</c> two levels up AND a project file in the
    /// directory above it, so a published application that happens to sit under a folder named
    /// <c>bin</c> is never mistaken for a development build.
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

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
