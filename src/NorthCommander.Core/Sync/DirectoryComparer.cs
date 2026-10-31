using NorthCommander.Core.Archives;

namespace NorthCommander.Core.Sync;

public enum SyncDirection
{
    Equal,
    LeftToRight,
    RightToLeft,
    Conflict,
}

/// <summary>A file on one side of a comparison; <see cref="FullPath"/> may be a virtual path inside a ZIP.</summary>
public sealed record SyncFile(string FullPath, long Size, DateTime LastWriteTimeUtc);

public sealed record SyncItem(string RelativePath, SyncFile? Left, SyncFile? Right, SyncDirection Direction);

public sealed record SyncOptions
{
    public bool IncludeSubdirectories { get; init; } = true;

    /// <summary>Treat timestamps up to 2 seconds apart as equal (FAT32/ZIP store times with 2 s resolution).</summary>
    public bool UseTimeTolerance { get; init; } = true;
}

/// <summary>Compares two folders (physical or inside ZIP archives) by relative path, size and modification time.</summary>
public static class DirectoryComparer
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public static Task<IReadOnlyList<SyncItem>> CompareAsync(string leftRoot, string rightRoot, SyncOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => Compare(leftRoot, rightRoot, options, cancellationToken), cancellationToken);

    public static IReadOnlyList<SyncItem> Compare(string leftRoot, string rightRoot, SyncOptions options, CancellationToken cancellationToken = default)
    {
        var left = Scan(leftRoot, options, cancellationToken);
        var right = Scan(rightRoot, options, cancellationToken);
        return left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                left.TryGetValue(path, out var l);
                right.TryGetValue(path, out var r);
                return new SyncItem(path, l, r, Classify(l, r, options.UseTimeTolerance));
            })
            .ToList();
    }

    /// <summary>Newer (or only existing) side wins; same time but different size is a conflict.</summary>
    public static SyncDirection Classify(SyncFile? left, SyncFile? right, bool useTolerance)
    {
        if (left is null)
        {
            return right is null ? SyncDirection.Equal : SyncDirection.RightToLeft;
        }

        if (right is null)
        {
            return SyncDirection.LeftToRight;
        }

        var difference = left.LastWriteTimeUtc - right.LastWriteTimeUtc;
        var tolerance = useTolerance ? Tolerance : TimeSpan.Zero;
        if (difference.Duration() <= tolerance)
        {
            return left.Size == right.Size ? SyncDirection.Equal : SyncDirection.Conflict;
        }

        return difference > TimeSpan.Zero ? SyncDirection.LeftToRight : SyncDirection.RightToLeft;
    }

    /// <summary>Path of <paramref name="relativePath"/> below <paramref name="root"/> (physical or virtual ZIP path).</summary>
    public static string PathFor(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static Dictionary<string, SyncFile> Scan(string root, SyncOptions options, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase);
        if (ArchiveService.TryParse(root, out var archivePath, out var innerPath))
        {
            var prefix = innerPath.Length == 0 ? string.Empty : innerPath + "/";
            using var archive = ArchiveService.OpenRead(archivePath);
            foreach (var item in archive.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsDirectory || !item.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = item.Name[prefix.Length..];
                if (options.IncludeSubdirectories || !relative.Contains('/'))
                {
                    files[relative] = new SyncFile(ArchiveService.ToVirtualPath(archivePath, item.Name), item.Length, item.LastWriteTime.ToUniversalTime());
                }
            }

            return files;
        }

        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }

        var enumeration = new EnumerationOptions { RecurseSubdirectories = options.IncludeSubdirectories, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in directory.EnumerateFiles("*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files[Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/')] = new SyncFile(file.FullName, file.Length, file.LastWriteTimeUtc);
        }

        return files;
    }
}
