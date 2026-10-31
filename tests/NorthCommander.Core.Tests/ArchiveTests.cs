using System.IO.Compression;
using System.Text;
using NorthCommander.Core.Archives;
using NorthCommander.Core.Services;
using NorthCommander.Core.Transfers;

namespace NorthCommander.Core.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderZip_");
    private readonly AnswerResolver _resolver = new();
    private readonly BackgroundTransferQueue _queue;
    private readonly string _zip;

    public ArchiveTests()
    {
        _queue = new BackgroundTransferQueue(
            [new CopyFileOperation(), new MoveFileOperation(), new ExtractFromArchiveOperation(), new AddToArchiveOperation()],
            conflictResolver: _resolver);
        _zip = Path.Combine(_root.FullName, "data.zip");
        using var archive = ZipFile.Open(_zip, ZipArchiveMode.Create);
        Add(archive, "readme.txt", "hello");
        Add(archive, "docs/a.txt", "AAA");
        Add(archive, "docs/sub/b.txt", "BB");
        archive.CreateEntry("empty/");
        Add(archive, "docs/../../evil.txt", "x");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _root.Delete(recursive: true);
    }

    [Fact]
    public void Parses_virtual_paths()
    {
        Assert.True(ArchiveService.TryParse(_zip, out var archive, out var inner));
        Assert.Equal((_zip, ""), (archive, inner));
        Assert.True(ArchiveService.TryParse(Path.Combine(_zip, "docs", "sub"), out archive, out inner));
        Assert.Equal((_zip, "docs/sub"), (archive, inner));
        Assert.False(ArchiveService.TryParse(_root.FullName, out _, out _));
        Assert.False(ArchiveService.TryParse(Path.Combine(_root.FullName, "nothing.zip", "x"), out _, out _));
    }

    [Fact]
    public async Task Lists_root_and_subfolders_through_directory_service()
    {
        var service = new DirectoryService();

        var root = await service.GetDirectoryListingAsync(_zip);
        Assert.True(root.IsArchive);
        Assert.Equal(_root.FullName, root.Entries[0].FullPath);
        Assert.True(root.Entries[0].IsParentLink);
        Assert.Equal(["docs", "empty", "readme"], root.Entries.Skip(1).Select(e => e.Name));
        Assert.Equal(5, root.TotalFileSize);

        var docs = await service.GetDirectoryListingAsync(Path.Combine(_zip, "docs"));
        Assert.Equal(_zip, docs.Entries[0].FullPath);
        Assert.Equal([Path.Combine(_zip, "docs", "sub"), Path.Combine(_zip, "docs", "a.txt")], docs.Entries.Skip(1).Select(e => e.FullPath));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.GetDirectoryListingAsync(Path.Combine(_zip, "missing")));
    }

    [Fact]
    public async Task Invalid_archive_is_an_io_error()
    {
        var bad = Path.Combine(_root.FullName, "bad.zip");
        File.WriteAllText(bad, "not a zip");

        await Assert.ThrowsAsync<IOException>(() => new DirectoryService().GetDirectoryListingAsync(bad));
    }

    [Fact]
    public async Task Extracts_file_and_folder_with_conflict_skip()
    {
        var target = _root.CreateSubdirectory("out").FullName;
        File.WriteAllText(Path.Combine(target, "readme.txt"), "keep");
        _resolver.Answer = FileConflictResolution.Skip;

        var file = _queue.Enqueue(FileTransferType.Extract, Path.Combine(_zip, "readme.txt"), Path.Combine(target, "readme.txt"));
        var folder = _queue.Enqueue(FileTransferType.Extract, Path.Combine(_zip, "docs"), Path.Combine(target, "docs"));
        await WaitAsync(file);
        await WaitAsync(folder);

        Assert.Equal(FileTransferStatus.Completed, file.Status);
        Assert.Equal(FileTransferStatus.Completed, folder.Status);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal(5, _resolver.Conflicts.Single().SourceSize);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(target, "docs", "a.txt")));
        Assert.Equal("BB", File.ReadAllText(Path.Combine(target, "docs", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(_root.FullName, "evil.txt")));
    }

    [Fact]
    public async Task Packs_files_and_folders_and_overwrites_on_request()
    {
        var file = Path.Combine(_root.FullName, "readme.txt");
        File.WriteAllText(file, "new readme");
        var folder = _root.CreateSubdirectory("photos");
        File.WriteAllText(Path.Combine(folder.FullName, "p1.jpg"), "jpg");
        folder.CreateSubdirectory("empty");
        _resolver.Answer = FileConflictResolution.Overwrite;

        var t1 = _queue.Enqueue(FileTransferType.Pack, file, Path.Combine(_zip, "readme.txt"));
        var t2 = _queue.Enqueue(FileTransferType.Pack, folder.FullName, Path.Combine(_zip, "docs", "photos"));
        await WaitAsync(t1);
        await WaitAsync(t2);

        Assert.Equal(FileTransferStatus.Completed, t1.Status);
        Assert.Equal(FileTransferStatus.Completed, t2.Status);
        using var archive = ZipFile.OpenRead(_zip);
        Assert.Equal("new readme", Read(archive, "readme.txt"));
        Assert.Equal("AAA", Read(archive, "docs/a.txt"));
        Assert.Equal("jpg", Read(archive, "docs/photos/p1.jpg"));
        Assert.NotNull(archive.GetEntry("docs/photos/empty/"));
        Assert.Single(archive.Entries, e => e.FullName == "readme.txt");
        Assert.DoesNotContain(_root.GetFiles(), f => f.Name.EndsWith(".ncpart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pack_rename_keeps_both_entries()
    {
        var file = Path.Combine(_root.FullName, "a.txt");
        File.WriteAllText(file, "second");
        _resolver.Answer = FileConflictResolution.Rename;

        var task = _queue.Enqueue(FileTransferType.Pack, file, Path.Combine(_zip, "docs", "a.txt"));
        await WaitAsync(task);

        using var archive = ZipFile.OpenRead(_zip);
        Assert.Equal("AAA", Read(archive, "docs/a.txt"));
        Assert.Equal("second", Read(archive, "docs/a (2).txt"));
    }

    [Fact]
    public async Task Extracts_single_entry_to_temp()
    {
        var temp = await ArchiveService.ExtractToTempAsync(Path.Combine(_zip, "docs", "sub", "b.txt"));

        Assert.Equal("BB", File.ReadAllText(temp));
        ArchiveService.DeleteTemporaryFile(temp);
        Assert.False(File.Exists(temp));
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
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

    private sealed class AnswerResolver : IFileConflictResolver
    {
        public FileConflictResolution Answer { get; set; } = FileConflictResolution.Skip;

        public List<FileConflict> Conflicts { get; } = [];

        public Task<FileConflictResolution> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
        {
            Conflicts.Add(conflict);
            return Task.FromResult(Answer);
        }
    }
}
