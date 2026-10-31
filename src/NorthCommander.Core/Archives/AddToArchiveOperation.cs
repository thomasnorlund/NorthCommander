using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using NorthCommander.Core.Transfers;

namespace NorthCommander.Core.Archives;

/// <summary>
/// Adds a file or folder to a ZIP archive (<see cref="FileTransferType.Pack"/>). The destination is a virtual path such
/// as <c>/data/backup.zip/docs/report.pdf</c>.
/// </summary>
/// <remarks>
/// The archive is rebuilt into a temporary file (existing entries are re-streamed, new ones appended) and only replaces
/// the original after success, so memory use stays constant and a failed or canceled task leaves the archive untouched.
/// </remarks>
public sealed class AddToArchiveOperation : IFileOperation
{
    private const int BufferSize = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ArchiveLocks = new(StringComparer.OrdinalIgnoreCase);

    public FileTransferType Type => FileTransferType.Pack;

    public async Task ExecuteAsync(FileTransferTask task, FileOperationContext context)
    {
        var destinationFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(task.DestinationPath)) ?? string.Empty;
        if (!ArchiveService.TryParse(destinationFolder, out var archivePath, out var targetFolder))
        {
            throw new IOException($"Target is not inside an archive: {task.DestinationPath}");
        }

        var entryRoot = (targetFolder.Length == 0 ? string.Empty : targetFolder + "/") + Path.GetFileName(task.DestinationPath);
        var additions = CollectAdditions(Path.GetFullPath(task.SourcePath), entryRoot);

        var gate = ArchiveLocks.GetOrAdd(archivePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var temporary = FileTransferPaths.GetUniquePath(archivePath + ".ncpart");
        try
        {
            using (var source = ArchiveService.OpenZip(archivePath))
            {
                var existing = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in source.Entries)
                {
                    existing.TryAdd(entry.FullName.Replace('\\', '/'), entry);
                }

                // Ask about conflicts before the (possibly long) rebuild starts.
                var replaced = new HashSet<string>(StringComparer.Ordinal);
                var plan = new List<(string? File, string EntryName)>();
                foreach (var (file, entryName) in additions)
                {
                    if (file is null)
                    {
                        if (!existing.ContainsKey(entryName))
                        {
                            plan.Add((null, entryName));
                        }

                        continue;
                    }

                    var name = entryName;
                    if (existing.TryGetValue(entryName, out var current))
                    {
                        var stamp = new FileStamp(current.Length, current.LastWriteTime.LocalDateTime);
                        switch (await context.ResolveConflictAsync(file, ArchiveService.ToVirtualPath(archivePath, entryName), null, stamp).ConfigureAwait(false))
                        {
                            case FileConflictResolution.Overwrite:
                                replaced.Add(entryName);
                                break;
                            case FileConflictResolution.Rename:
                                name = UniqueEntryName(entryName, existing, plan);
                                break;
                            default:
                                continue;
                        }
                    }

                    plan.Add((file, name));
                }

                var kept = source.Entries.Where(e => !replaced.Contains(e.FullName.Replace('\\', '/'))).ToList();
                context.SetTotalBytes(kept.Sum(e => e.Length) + plan.Sum(p => p.File is null ? 0 : new FileInfo(p.File).Length));

                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                using var target = new ZipArchive(output, ZipArchiveMode.Create);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    foreach (var entry in kept)
                    {
                        var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        copy.LastWriteTime = entry.LastWriteTime;
                        copy.ExternalAttributes = entry.ExternalAttributes;
                        if (!ArchiveService.IsDirectoryEntry(entry))
                        {
                            await using var from = entry.Open();
                            await using var to = copy.Open();
                            await CopyAsync(from, to, buffer, context).ConfigureAwait(false);
                        }
                    }

                    foreach (var (file, entryName) in plan)
                    {
                        var entry = target.CreateEntry(entryName, CompressionLevel.Optimal);
                        if (file is null)
                        {
                            continue;
                        }

                        context.SetCurrentItem(file);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        await using var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var to = entry.Open();
                        await CopyAsync(from, to, buffer, context).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            File.Move(temporary, archivePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Files (with entry names) and folder entries (File = null, name ends with '/') to add.</summary>
    private static List<(string? File, string EntryName)> CollectAdditions(string source, string entryRoot)
    {
        if (File.Exists(source))
        {
            return [(source, entryRoot)];
        }

        if (!Directory.Exists(source))
        {
            throw new FileNotFoundException($"Source not found: {source}");
        }

        var result = new List<(string?, string)> { (null, entryRoot + "/") };
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            result.Add((null, $"{entryRoot}/{Path.GetRelativePath(source, directory).Replace('\\', '/')}/"));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            result.Add((file, $"{entryRoot}/{Path.GetRelativePath(source, file).Replace('\\', '/')}"));
        }

        return result;
    }

    private static async Task CopyAsync(Stream from, Stream to, byte[] buffer, FileOperationContext context)
    {
        int read;
        while ((read = await from.ReadAsync(buffer.AsMemory(0, BufferSize), context.CancellationToken).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), context.CancellationToken).ConfigureAwait(false);
            context.ReportBytesTransferred(read);
            await context.CheckpointAsync().ConfigureAwait(false);
        }
    }

    private static string UniqueEntryName(string entryName, Dictionary<string, ZipArchiveEntry> existing, List<(string? File, string EntryName)> plan)
    {
        var slash = entryName.LastIndexOf('/');
        var folder = slash >= 0 ? entryName[..(slash + 1)] : string.Empty;
        var fileName = entryName[(slash + 1)..];
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{folder}{stem} ({i}){extension}";
            if (!existing.ContainsKey(candidate) && !plan.Exists(p => p.EntryName == candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
