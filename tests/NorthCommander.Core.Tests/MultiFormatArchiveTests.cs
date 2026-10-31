using System.Formats.Tar;
using System.IO.Compression;
using NorthCommander.Core.Archives;
using NorthCommander.Core.Services;
using NorthCommander.Core.Transfers;

namespace NorthCommander.Core.Tests;

/// <summary>7z, RAR and TAR archives (read-only, through SharpCompress) behind the same virtual paths as ZIP.</summary>
public sealed class MultiFormatArchiveTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderArc_");
    private readonly BackgroundTransferQueue _queue = new([new ExtractFromArchiveOperation(), new AddToArchiveOperation()]);

    public void Dispose()
    {
        _queue.Dispose();
        _root.Delete(recursive: true);
    }

    public static TheoryData<string> Formats() => new() { "sample.7z", "sample.rar", "solid.rar", "sample.tar", "sample.tar.gz", "sample.tgz" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Lists_folders_and_files(string name)
    {
        var archive = Prepare(name);
        var service = new DirectoryService();

        var root = await service.GetDirectoryListingAsync(archive);
        Assert.True(root.IsArchive);
        Assert.True(root.Entries[0].IsParentLink);
        Assert.Equal(["docs", "empty", "readme"], root.Entries.Skip(1).Select(e => e.Name));
        Assert.Equal(5, root.TotalFileSize);

        var docs = await service.GetDirectoryListingAsync(Path.Combine(archive, "docs"));
        Assert.Equal(archive, docs.Entries[0].FullPath);
        Assert.Equal([Path.Combine(archive, "docs", "sub"), Path.Combine(archive, "docs", "a.txt")], docs.Entries.Skip(1).Select(e => e.FullPath));
        Assert.Empty((await service.GetDirectoryListingAsync(Path.Combine(archive, "empty"))).Entries.Skip(1));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Extracts_folder_and_single_file(string name)
    {
        var archive = Prepare(name);
        var target = _root.CreateSubdirectory("out").FullName;

        var task = _queue.Enqueue(FileTransferType.Extract, Path.Combine(archive, "docs"), Path.Combine(target, "docs"));
        await WaitAsync(task);

        Assert.Equal("AAA", File.ReadAllText(Path.Combine(target, "docs", "a.txt")));
        Assert.Equal("BB", File.ReadAllText(Path.Combine(target, "docs", "sub", "b.txt")));

        var temp = await ArchiveService.ExtractToTempAsync(Path.Combine(archive, "readme.txt"));
        Assert.Equal("hello", File.ReadAllText(temp));
        ArchiveService.DeleteTemporaryFile(temp);
    }

    [Fact]
    public async Task Encrypted_archive_is_a_clear_io_error()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => new DirectoryService().GetDirectoryListingAsync(Prepare("encrypted.7z")));
        Assert.Contains("password-protected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_zip_archives_are_read_only()
    {
        var archive = Prepare("sample.7z");
        var file = Path.Combine(_root.FullName, "new.txt");
        File.WriteAllText(file, "x");

        var task = _queue.Enqueue(FileTransferType.Pack, file, Path.Combine(archive, "new.txt"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(FileTransferStatus.Failed, task.Status);
        Assert.Contains("read-only", task.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognizes_archive_extensions()
    {
        Assert.True(ArchiveService.IsArchiveFile("a.7z"));
        Assert.True(ArchiveService.IsArchiveFile("a.TAR.GZ"));
        Assert.False(ArchiveService.IsArchiveFile("a.gz"));
        Assert.Equal("TAR.GZ", ArchiveService.GetArchiveTag("/x/a.tar.gz"));
        Assert.Equal("RAR", ArchiveService.GetArchiveTag("/x/a.rar"));
    }

    /// <summary>Copies a fixture (TestData) into the temp folder, or builds TAR variants from a sample folder.</summary>
    private string Prepare(string name)
    {
        var path = Path.Combine(_root.FullName, name);
        if (!name.Contains(".tar", StringComparison.Ordinal) && !name.EndsWith(".tgz", StringComparison.Ordinal))
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", name), path);
            return path;
        }

        var source = _root.CreateSubdirectory("tar-source");
        Directory.CreateDirectory(Path.Combine(source.FullName, "docs", "sub"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "empty"));
        File.WriteAllText(Path.Combine(source.FullName, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(source.FullName, "docs", "a.txt"), "AAA");
        File.WriteAllText(Path.Combine(source.FullName, "docs", "sub", "b.txt"), "BB");
        using (var output = File.Create(path))
        using (Stream stream = name.EndsWith(".tar", StringComparison.Ordinal) ? output : new GZipStream(output, CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(source.FullName, stream, includeBaseDirectory: false);
        }

        source.Delete(recursive: true);
        return path;
    }

    private static async Task WaitAsync(FileTransferTask task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "Transfer did not finish in time.");
            await Task.Delay(10);
        }

        Assert.True(task.ErrorMessage is null, task.ErrorMessage);
    }
}
