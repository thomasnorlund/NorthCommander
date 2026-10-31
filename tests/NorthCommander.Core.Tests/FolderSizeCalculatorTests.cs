using System.IO.Compression;
using NorthCommander.Core.Services;

namespace NorthCommander.Core.Tests;

public sealed class FolderSizeCalculatorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderSize_");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Sums_nested_and_hidden_files()
    {
        var folder = Tree();
        var hidden = Path.Combine(folder, ".hidden");
        File.WriteAllText(hidden, "1234");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var size = await FolderSizeCalculator.CalculateAsync(folder);

        Assert.Equal(new FolderSize(3 + 20 + 100 + 4, 4, 3), size);
    }

    [Fact]
    public async Task Does_not_follow_links()
    {
        var folder = Tree();
        Directory.CreateSymbolicLink(Path.Combine(folder, "loop"), folder);

        var size = await FolderSizeCalculator.CalculateAsync(folder);

        Assert.Equal(new FolderSize(123, 3, 4), size);
    }

    [Fact]
    public async Task Skips_unreadable_folders()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Tree();
        var locked = Path.Combine(folder, "a", "b");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var size = await FolderSizeCalculator.CalculateAsync(folder);
            Assert.InRange(size.Bytes, 23, 123);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderSizeCalculator.CalculateAsync(Tree(), cts.Token));
    }

    [Fact]
    public async Task Missing_folder_throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => FolderSizeCalculator.CalculateAsync(Path.Combine(_root.FullName, "missing")));
    }

    [Fact]
    public async Task Calculates_folders_inside_archives()
    {
        var zip = Path.Combine(_root.FullName, "a.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, length) in new[] { ("docs/a.txt", 3), ("docs/sub/deep/b.txt", 5), ("other.txt", 7) })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(new byte[length]);
            }
        }

        Assert.Equal(new FolderSize(8, 2, 2), await FolderSizeCalculator.CalculateAsync(Path.Combine(zip, "docs")));
        Assert.Equal(new FolderSize(15, 3, 3), await FolderSizeCalculator.CalculateAsync(zip));
    }

    /// <summary>folder/x.txt (3 B), folder/a/y.txt (20 B), folder/a/b/z.bin (100 B), folder/empty/.</summary>
    private string Tree()
    {
        var folder = _root.CreateSubdirectory("folder").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "a", "b"));
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllBytes(Path.Combine(folder, "x.txt"), new byte[3]);
        File.WriteAllBytes(Path.Combine(folder, "a", "y.txt"), new byte[20]);
        File.WriteAllBytes(Path.Combine(folder, "a", "b", "z.bin"), new byte[100]);
        return folder;
    }
}
