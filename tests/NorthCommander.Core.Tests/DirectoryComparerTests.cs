using System.IO.Compression;
using NorthCommander.Core.Sync;

namespace NorthCommander.Core.Tests;

public sealed class DirectoryComparerTests : IDisposable
{
    private static readonly DateTime Time = new(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderSync_");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Classifies_by_presence_time_and_size()
    {
        Write("L/same.txt", "abc", Time);
        Write("R/same.txt", "abc", Time.AddSeconds(1));
        Write("L/newer-left.txt", "1", Time.AddHours(1));
        Write("R/newer-left.txt", "1", Time);
        Write("R/only-right.txt", "x", Time);
        Write("L/sub/only-left.txt", "x", Time);
        Write("L/conflict.txt", "aaaa", Time);
        Write("R/conflict.txt", "bb", Time);

        var items = DirectoryComparer.Compare(PathOf("L"), PathOf("R"), new SyncOptions()).ToDictionary(i => i.RelativePath, i => i.Direction);

        Assert.Equal(SyncDirection.Equal, items["same.txt"]);
        Assert.Equal(SyncDirection.LeftToRight, items["newer-left.txt"]);
        Assert.Equal(SyncDirection.RightToLeft, items["only-right.txt"]);
        Assert.Equal(SyncDirection.LeftToRight, items["sub/only-left.txt"]);
        Assert.Equal(SyncDirection.Conflict, items["conflict.txt"]);

        var strict = DirectoryComparer.Compare(PathOf("L"), PathOf("R"), new SyncOptions { UseTimeTolerance = false, IncludeSubdirectories = false });
        Assert.Equal(SyncDirection.RightToLeft, strict.Single(i => i.RelativePath == "same.txt").Direction);
        Assert.DoesNotContain(strict, i => i.RelativePath.Contains('/'));
    }

    [Fact]
    public void Compares_zip_folder_with_physical_folder()
    {
        Write("R/a.txt", "AAA", Time);
        Write("R/extra.txt", "E", Time);
        var zip = PathOf("left.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("data/a.txt");
            entry.LastWriteTime = Time;
            using (var stream = entry.Open())
            {
                stream.Write("AAA"u8);
            }

            archive.CreateEntry("data/b.txt").LastWriteTime = Time;
        }

        var items = DirectoryComparer.Compare(Path.Combine(zip, "data"), PathOf("R"), new SyncOptions());

        Assert.Equal(["a.txt", "b.txt", "extra.txt"], items.Select(i => i.RelativePath));
        Assert.Equal(SyncDirection.Equal, items[0].Direction);
        Assert.Equal(Path.Combine(zip, "data", "b.txt"), items[1].Left!.FullPath);
        Assert.Equal(SyncDirection.LeftToRight, items[1].Direction);
        Assert.Equal(SyncDirection.RightToLeft, items[2].Direction);
    }

    private string PathOf(string relative) => Path.Combine(_root.FullName, relative);

    private void Write(string relative, string content, DateTime time)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time);
    }
}
