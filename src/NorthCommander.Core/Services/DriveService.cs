namespace NorthCommander.Core.Services;

public enum DriveKind
{
    Fixed,
    System,
    Removable,
    Network,
    CdRom,
    Documents,
}

/// <summary>One entry of the drive dropdown: a drive (or mount point) or the user's Documents folder.</summary>
public sealed record DriveEntry(string RootPath, string Name, string Label, DriveKind Kind, bool IsReady, long FreeBytes, long TotalBytes);

/// <summary>Lists drives the way the Legendary File Commander applications show them in their drive dropdown.</summary>
public static class DriveService
{
    private static readonly string[] LinuxMountPrefixes = ["/media/", "/mnt/", "/run/media/", "/Volumes/"];

    /// <summary>All drives (Windows) or real mount points (Linux/macOS), plus Documents. Never throws for a single bad drive.</summary>
    public static IReadOnlyList<DriveEntry> GetDrives(bool includeDocuments = true)
    {
        var result = new List<DriveEntry>();
        var systemRoot = OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory) : "/";
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (Describe(drive, systemRoot) is { } entry)
                {
                    result.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (includeDocuments)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents))
            {
                documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (!string.IsNullOrEmpty(documents))
            {
                result.Add(new DriveEntry(documents, "*", "My Documents", DriveKind.Documents, true, 0, 0));
            }
        }

        return result;
    }

    /// <summary>The drive whose root is the longest prefix of <paramref name="path"/> (Documents entries are skipped).</summary>
    public static DriveEntry? FindDrive(IEnumerable<DriveEntry> drives, string path, StringComparison? comparison = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var compare = comparison ?? (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return drives
            .Where(d => d.Kind != DriveKind.Documents && IsUnder(path, d.RootPath, compare))
            .MaxBy(d => d.RootPath.Length);
    }

    private static bool IsUnder(string path, string root, StringComparison comparison)
    {
        if (!path.StartsWith(root, comparison))
        {
            return string.Equals(path + '\\', root, comparison) || string.Equals(path + '/', root, comparison);
        }

        return path.Length == root.Length || root[^1] is '/' or '\\' || path[root.Length] is '/' or '\\';
    }

    private static DriveEntry? Describe(DriveInfo drive, string? systemRoot)
    {
        var root = drive.RootDirectory.FullName;
        var isSystem = string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase);
        var kind = drive.DriveType switch
        {
            DriveType.Removable => DriveKind.Removable,
            DriveType.Network => DriveKind.Network,
            DriveType.CDRom => DriveKind.CdRom,
            _ when isSystem => DriveKind.System,
            _ => DriveKind.Fixed,
        };

        if (!OperatingSystem.IsWindows() && !isSystem
            && (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom)
                || !LinuxMountPrefixes.Any(p => root.StartsWith(p, StringComparison.Ordinal))))
        {
            return null;
        }

        var ready = drive.IsReady;
        var label = ready && OperatingSystem.IsWindows() ? drive.VolumeLabel : string.Empty;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = OperatingSystem.IsWindows()
                ? kind switch
                {
                    DriveKind.Removable => "USB Drive",
                    DriveKind.Network => "Network Drive",
                    DriveKind.CdRom => "DVD Drive",
                    _ => "Local Disk",
                }
                : isSystem ? "System" : Path.GetFileName(root.TrimEnd('/'));
        }

        var name = OperatingSystem.IsWindows() ? root.TrimEnd('\\', ':').ToLowerInvariant() : root;
        return new DriveEntry(root, name, label, kind, ready, ready ? drive.AvailableFreeSpace : 0, ready ? drive.TotalSize : 0);
    }
}
