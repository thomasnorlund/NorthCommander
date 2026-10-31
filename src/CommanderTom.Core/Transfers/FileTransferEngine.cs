using System.Buffers;

namespace CommanderTom.Core.Transfers;

/// <summary>Streaming copy/delete primitives shared by the copy and move operations.</summary>
internal static class FileTransferEngine
{
    private const int BufferSize = 1024 * 1024;

    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static void EnsureValid(string source, string destination)
    {
        if (FileTransferPaths.Validate(source, destination) is { } error)
        {
            throw new IOException(error);
        }
    }

    /// <summary>
    /// Copies a file or directory tree to <paramref name="destination"/>. Existing directories are merged;
    /// for existing files the <see cref="FileOperationContext"/> decides (overwrite, skip, rename).
    /// </summary>
    public static async Task<CopyResult> CopyAsync(string source, string destination, FileOperationContext context)
    {
        EnsureValid(source, destination);
        var result = new CopyResult();

        if (File.Exists(source))
        {
            var length = new FileInfo(source).Length;
            context.SetTotalBytes(length);
            CreateParentDirectory(destination);
            await CopyFileResolvingConflictAsync(source, destination, length, context, result).ConfigureAwait(false);
            return result;
        }

        var sourceDirectory = new DirectoryInfo(source);
        var entries = new List<FileSystemInfo>();
        long totalBytes = 0;
        foreach (var info in sourceDirectory.EnumerateFileSystemInfos("*", RecursiveOptions))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            entries.Add(info);
            if (info is FileInfo file)
            {
                totalBytes += file.Length;
            }
        }

        context.SetTotalBytes(totalBytes);
        CreateParentDirectory(destination);
        CreateDirectory(destination);

        // Recursive enumeration yields each directory before its contents.
        foreach (var info in entries)
        {
            await context.CheckpointAsync().ConfigureAwait(false);

            var target = Path.Combine(destination, Path.GetRelativePath(sourceDirectory.FullName, info.FullName));
            if (info is FileInfo file)
            {
                await CopyFileResolvingConflictAsync(file.FullName, target, file.Length, context, result).ConfigureAwait(false);
            }
            else
            {
                CreateDirectory(target);
            }
        }

        return result;
    }

    /// <summary>
    /// Where a single file should go given that <paramref name="destination"/> may exist:
    /// null when the user skips it.
    /// </summary>
    public static async Task<FileTarget?> ResolveFileTargetAsync(string source, string destination, FileOperationContext context)
    {
        if (Directory.Exists(destination))
        {
            throw new IOException($"A folder with the same name already exists: {destination}");
        }

        if (!File.Exists(destination))
        {
            return new FileTarget(destination, Overwrite: false);
        }

        return await context.ResolveConflictAsync(source, destination).ConfigureAwait(false) switch
        {
            FileConflictResolution.Overwrite => new FileTarget(destination, Overwrite: true),
            FileConflictResolution.Rename => new FileTarget(FileTransferPaths.GetUniquePath(destination), Overwrite: false),
            _ => null,
        };
    }

    /// <summary>
    /// Streams <paramref name="source"/> to <paramref name="destination"/>. When overwriting, the data goes to a
    /// temporary file first so the existing file survives a failed or canceled copy.
    /// </summary>
    public static async Task CopyFileAsync(string source, string destination, bool overwrite, FileOperationContext context)
    {
        context.SetCurrentItem(source);
        var writePath = overwrite ? GetTemporaryPath(destination) : destination;
        var created = false;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(writePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0,
                             FileOptions.Asynchronous))
            {
                created = true;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), context.CancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), context.CancellationToken).ConfigureAwait(false);
                    context.ReportBytesTransferred(read);
                    await context.CheckpointAsync().ConfigureAwait(false);
                }
            }

            File.SetLastWriteTimeUtc(writePath, File.GetLastWriteTimeUtc(source));
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(writePath, File.GetAttributes(source));
            }

            if (overwrite)
            {
                ClearReadOnly(destination);
                File.Move(writePath, destination, overwrite: true);
            }
        }
        catch
        {
            if (created)
            {
                TryDeleteFile(writePath);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            ClearReadOnly(path);
            File.Delete(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", RecursiveOptions))
            {
                ClearReadOnly(file);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// After a partial move: deletes the files that were copied, then every directory under (and including)
    /// <paramref name="root"/> that ended up empty. Skipped files and their folders stay.
    /// </summary>
    public static void DeleteMoved(string root, IEnumerable<string> copiedFiles)
    {
        foreach (var file in copiedFiles)
        {
            ClearReadOnly(file);
            File.Delete(file);
        }

        var directories = Directory.EnumerateDirectories(root, "*", RecursiveOptions)
            .OrderByDescending(d => d.Length)
            .Append(root);
        foreach (var directory in directories)
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static async Task CopyFileResolvingConflictAsync(
        string source, string destination, long length, FileOperationContext context, CopyResult result)
    {
        context.SetCurrentItem(source);
        if (await ResolveFileTargetAsync(source, destination, context).ConfigureAwait(false) is not { } target)
        {
            result.SkippedCount++;
            context.ReportBytesTransferred(length);
            return;
        }

        await CopyFileAsync(source, target.Path, target.Overwrite, context).ConfigureAwait(false);
        result.CopiedFiles.Add(source);
    }

    private static void CreateDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException($"A file with the same name already exists: {path}");
        }

        Directory.CreateDirectory(path);
    }

    private static void CreateParentDirectory(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static string GetTemporaryPath(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination) ?? string.Empty,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.ctpart");

    private static void ClearReadOnly(string file)
    {
        var attributes = File.GetAttributes(file);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDeleteFile(string path)
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

internal readonly record struct FileTarget(string Path, bool Overwrite);

internal sealed class CopyResult
{
    /// <summary>Source files that were actually copied (a move deletes only these).</summary>
    public List<string> CopiedFiles { get; } = [];

    public int SkippedCount { get; set; }
}
