using KanbanFlow.Models.Workflows;

namespace KanbanFlow.Services.Workflows;

public static class RepositoryPaths
{
    public static string Resolve(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path)) throw new WorkflowException("invalid", "Choose an absolute repository path under Projects.");
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureWithin(resolved, root);
        if (!Directory.Exists(resolved) || (!Directory.Exists(Path.Combine(resolved, ".git")) && !File.Exists(Path.Combine(resolved, ".git"))))
            throw new WorkflowException("invalid", "The selected folder must be an existing Git repository or worktree.");
        return resolved;
    }

    public static void EnsureWithin(string path, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new WorkflowException("invalid", "The selected path must stay inside the configured workspace root.");
        for (var current = new DirectoryInfo(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorkflowException("invalid", "Junctions and symbolic links are not supported by the repository registry.");
            if (string.Equals(current.FullName, fullRoot, comparison)) break;
        }
    }

    public static void ValidateReferences(string references)
    {
        foreach (var line in references.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line.Replace('\\', '/');
            if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Contains("..") || path.Contains('\0'))
                throw new WorkflowException("invalid", "File references must be repository-relative paths, one per line.");
        }
    }
}
