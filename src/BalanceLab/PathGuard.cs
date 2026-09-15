namespace PetThem.BalanceLab;

/// <summary>Keeps generated files inside directories this tool is allowed to write to.</summary>
/// <remarks>An MCP client can pass any string, so output paths are resolved and bounded here.</remarks>
public static class PathGuard
{
    /// <summary>Resolves a requested output directory, rejecting anything outside <paramref name="allowedRoot"/>.</summary>
    public static string ResolveOutputDirectory(string? requested, string allowedRoot)
    {
        string root = Path.GetFullPath(allowedRoot);
        string full = string.IsNullOrWhiteSpace(requested)
            ? root
            : Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        if (!IsInside(full, root))
            throw new ArgumentException($"Output must stay inside '{root}'. Rejected: '{full}'.");
        return full;
    }

    /// <summary>Resolves a file that must already exist.</summary>
    public static string ResolveInputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"File not found: {full}");
        return full;
    }

    private static bool IsInside(string candidate, string root)
    {
        static string Normalize(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Normalize(candidate).StartsWith(Normalize(root), StringComparison.OrdinalIgnoreCase);
    }
}
