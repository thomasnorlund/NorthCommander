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
    /// existing files are never overwritten (the transfer fails instead).
    /// </summary>
    public static async Task CopyAsync(string source, string destination, FileOperationContext context)
    {
        EnsureValid(source, destination);

        if (File.Exists(source))
        {
            context.SetTotalBytes(new FileInfo(source).Length);
            CreateParentDirectory(destination);
            await CopyFileAsync(source, destination, context).ConfigureAwait(false);
            return;
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
        Directory.CreateDirectory(destination);

        // Recursive enumeration yields each directory before its contents.
        foreach (var info in entries)
        {
            await context.CheckpointAsync().ConfigureAwait(false);

            var target = Path.Combine(destination, Path.GetRelativePath(sourceDirectory.FullName, info.FullName));
            if (info is FileInfo)
            {
                await CopyFileAsync(info.FullName, target, context).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(target);
            }
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

    private static async Task CopyFileAsync(string source, string destination, FileOperationContext context)
    {
        context.SetCurrentItem(source);
        var created = false;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0,
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

            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(destination, File.GetAttributes(source));
            }
        }
        catch
        {
            if (created)
            {
                TryDeleteFile(destination);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CreateParentDirectory(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }
    }

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
