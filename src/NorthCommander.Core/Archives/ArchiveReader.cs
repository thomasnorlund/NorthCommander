using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NorthCommander.Core.Archives;

/// <summary>A file or folder in an archive. <see cref="Name"/> uses '/' and has no leading "./" or trailing '/'.</summary>
internal sealed record ArchiveItem(string Name, bool IsDirectory, long Length, DateTime LastWriteTime, bool IsEncrypted, string Key);

/// <summary>
/// Read access to any supported archive: ZIP through System.IO.Compression, 7z/RAR/TAR through SharpCompress random access,
/// and compressed TARs (and solid RARs) as a forward-only stream that is read once per <see cref="Read"/>.
/// </summary>
internal sealed class ArchiveReader : IDisposable
{
    private readonly string _path;
    private readonly ZipArchive? _zip;
    private readonly IArchive? _archive;
    private readonly bool _sequential;
    private readonly Dictionary<string, object> _handles = new(StringComparer.Ordinal);

    private ArchiveReader(string path, ZipArchive? zip, IArchive? archive, bool sequential, List<ArchiveItem> items)
    {
        _path = path;
        _zip = zip;
        _archive = archive;
        _sequential = sequential;
        Items = items;
    }

    public IReadOnlyList<ArchiveItem> Items { get; }

    public static ArchiveReader Open(string path)
    {
        try
        {
            return OpenCore(path);
        }
        catch (SharpCompress.Common.CryptographicException ex)
        {
            throw new IOException($"{Path.GetFileName(path)} is password-protected. Encrypted archives are not supported yet.", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or SharpCompressException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new IOException($"{Path.GetFileName(path)} is not a valid or supported archive: {ex.Message}", ex);
        }
    }

    private static ArchiveReader OpenCore(string path)
    {
        var archiveTime = File.GetLastWriteTime(path);
        var items = new List<ArchiveItem>();
        if (ArchiveService.IsZipFile(path))
        {
            var zip = ZipFile.OpenRead(path);
            var reader = new ArchiveReader(path, zip, null, false, items);
            foreach (var entry in zip.Entries)
            {
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                reader.AddItem(entry.FullName, isDirectory, entry.Length, entry.LastWriteTime.LocalDateTime, false, entry);
            }

            return reader;
        }

        if (ArchiveService.IsCompressedTar(path))
        {
            var result = new ArchiveReader(path, null, null, true, items);
            using var stream = ReaderFactory.OpenReader(path, null);
            while (stream.MoveToNextEntry())
            {
                var entry = stream.Entry;
                result.AddItem(entry.Key ?? string.Empty, entry.IsDirectory, entry.Size, ToLocal(entry.LastModifiedTime, archiveTime), entry.IsEncrypted, null);
            }

            return result;
        }

        var archive = ArchiveFactory.OpenArchive(path, null);
        try
        {
            // Solid RARs can only be decompressed front to back.
            var sequential = archive.Type == ArchiveType.Rar && archive.IsSolid;
            var result = new ArchiveReader(path, null, archive, sequential, items);
            foreach (var entry in archive.Entries)
            {
                result.AddItem(entry.Key ?? string.Empty, entry.IsDirectory, entry.Size, ToLocal(entry.LastModifiedTime, archiveTime), entry.IsEncrypted, entry);
            }

            return result;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Yields the files accepted by <paramref name="filter"/> together with their content, in archive order.
    /// Each stream must be used (or skipped) before moving to the next item, and is disposed by the enumerator.
    /// </summary>
    public IEnumerable<(ArchiveItem Item, Stream Data)> Read(Func<ArchiveItem, bool> filter)
    {
        var wanted = Items.Where(i => !i.IsDirectory && filter(i)).ToDictionary(i => i.Key, StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            yield break;
        }

        if (!_sequential)
        {
            foreach (var item in Items)
            {
                if (!wanted.ContainsKey(item.Key))
                {
                    continue;
                }

                EnsureNotEncrypted(item);
                using var data = _handles[item.Key] switch
                {
                    ZipArchiveEntry zip => zip.Open(),
                    IArchiveEntry entry => entry.OpenEntryStream(),
                    _ => throw new InvalidOperationException(),
                };
                yield return (item, data);
            }

            yield break;
        }

        using var reader = _archive is not null ? _archive.ExtractAllEntries() : ReaderFactory.OpenReader(_path, null);
        while (wanted.Count > 0 && reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory || !wanted.Remove(reader.Entry.Key ?? string.Empty, out var item))
            {
                continue;
            }

            EnsureNotEncrypted(item);
            using var data = reader.OpenEntryStream();
            yield return (item, data);
        }
    }

    public void Dispose()
    {
        _zip?.Dispose();
        _archive?.Dispose();
    }

    private void AddItem(string key, bool isDirectory, long length, DateTime lastWriteTime, bool isEncrypted, object? handle)
    {
        var name = key.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        name = name.TrimEnd('/');
        if (name.Length == 0 || ArchiveService.NormalizeEntryName(name) is null || _handles.ContainsKey(key))
        {
            return;
        }

        _handles[key] = handle ?? key;
        ((List<ArchiveItem>)Items).Add(new ArchiveItem(name, isDirectory, isDirectory ? 0 : length, lastWriteTime, isEncrypted, key));
    }

    private void EnsureNotEncrypted(ArchiveItem item)
    {
        if (item.IsEncrypted)
        {
            throw new IOException($"'{item.Name}' in {Path.GetFileName(_path)} is password-protected. Encrypted archives are not supported yet.");
        }
    }

    private static DateTime ToLocal(DateTime? time, DateTime fallback) => time switch
    {
        null => fallback,
        { Kind: DateTimeKind.Utc } utc => utc.ToLocalTime(),
        { } local => local,
    };
}
