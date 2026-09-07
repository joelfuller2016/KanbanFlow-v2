using System.Runtime.CompilerServices;

namespace KanbanFlow.Tests;

/// <summary>
/// Reads files out of the checked-out repository for tests that guard SOURCE rather than behaviour.
/// </summary>
/// <remarks>
/// <para>
/// Most of what this project asserts, it asserts by calling something. Two things cannot be: the
/// stylesheet (this repo has no CSS engine in-process) and a <c>.razor</c> <c>@code</c> block (no
/// bUnit — see <see cref="Services.MirroredDescription"/>'s remarks). A guard on those has to read
/// the file, so the file has to be findable from the test host.
/// </para>
/// <para>
/// The root is found by walking up from this file's own COMPILE-TIME path rather than from the
/// working directory, which the test host is free to move.
/// </para>
/// </remarks>
internal static class RepositorySource
{
    /// <summary>The text of <paramref name="relativePath"/>, resolved against the repository root.</summary>
    internal static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The repository root — the directory holding the <c>*.slnx</c>.</summary>
    internal static string Root([CallerFilePath] string thisFile = "")
    {
        for (var directory = new FileInfo(thisFile).Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.slnx").Any())
                return directory.FullName;
        }

        // Deliberately loud rather than skipped. [CallerFilePath] bakes in the BUILD machine's source
        // path, so a caller needs the source tree beside the test assembly — true for `dotnet test`
        // from a checkout, which is this repo's only gate (no CI). Dynamic skipping would need xunit
        // v3 (Assert.Skip); on 2.9.3 the alternative is a silent no-op, and a guard that quietly
        // passes when it cannot run is exactly the failure mode these guards exist to catch.
        throw new InvalidOperationException(
            $"Could not locate the repository root (no *.slnx) above '{thisFile}'. Source guards read the "
            + "files they guard, so they must run from a checkout — run `dotnet test` from the "
            + "repository rather than against a copied-out test assembly.");
    }
}
