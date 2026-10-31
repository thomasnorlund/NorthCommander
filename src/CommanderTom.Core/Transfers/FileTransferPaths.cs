namespace CommanderTom.Core.Transfers;

public static class FileTransferPaths
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// First free "name (2).ext", "name (3).ext", ... next to <paramref name="path"/> (used by conflict "Rename").
    /// </summary>
    public static string GetUniquePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!Path.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Maps a user-entered target to the full destination path of <paramref name="sourcePath"/>.
    /// An existing directory (or any target when transferring several items, or one ending in a separator)
    /// receives the item under its own name; otherwise the target is the new name (copy-as / rename).
    /// </summary>
    public static string ResolveDestination(string sourcePath, string target, bool multipleSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var fullTarget = Path.GetFullPath(target.Trim());
        var targetIsDirectory = multipleSources || Path.EndsInDirectorySeparator(target.Trim()) || Directory.Exists(fullTarget);
        if (!targetIsDirectory)
        {
            return fullTarget;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath)));
        return Path.Combine(fullTarget, name);
    }

    /// <summary>Returns a user-facing error if <paramref name="sourcePath"/> cannot be transferred to <paramref name="destinationPath"/>.</summary>
    public static string? Validate(string sourcePath, string destinationPath)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));

        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
        {
            return $"Source not found: {source}";
        }

        if (string.Equals(source, destination, PathComparison))
        {
            return "Source and destination are the same.";
        }

        if (isDirectory && IsSubPathOf(destination, source))
        {
            return "Cannot transfer a folder into itself.";
        }

        return null;
    }

    internal static bool IsSameVolume(string first, string second) =>
        string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), PathComparison);

    private static bool IsSubPathOf(string path, string parent) =>
        path.Length > parent.Length &&
        path.StartsWith(parent, PathComparison) &&
        (path[parent.Length] == Path.DirectorySeparatorChar || path[parent.Length] == Path.AltDirectorySeparatorChar
            || Path.EndsInDirectorySeparator(parent));
}
