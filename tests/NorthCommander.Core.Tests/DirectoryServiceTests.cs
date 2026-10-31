using NorthCommander.Core.Models;
using NorthCommander.Core.Services;

namespace NorthCommander.Core.Tests;

public sealed class DirectoryServiceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderTests_");
    private readonly DirectoryService _service = new();

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Lists_parent_link_then_directories_then_files_sorted_by_name()
    {
        _root.CreateSubdirectory("zeta");
        _root.CreateSubdirectory("Alpha");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "b.txt"), "12345");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "A.md"), "");

        var listing = await _service.GetDirectoryListingAsync(_root.FullName);

        Assert.Equal(
            ["..", "Alpha", "zeta", "A", "b"],
            listing.Entries.Select(e => e.Name));
        Assert.Equal(FileSystemEntryKind.ParentLink, listing.Entries[0].Kind);
        Assert.Equal(2, listing.DirectoryCount);
        Assert.Equal(2, listing.FileCount);
        Assert.Equal(5, listing.TotalFileSize);
    }

    [Fact]
    public async Task Populates_file_metadata()
    {
        var path = Path.Combine(_root.FullName, "report.final.pdf");
        await File.WriteAllBytesAsync(path, new byte[1234]);
        var modified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Local);
        File.SetLastWriteTime(path, modified);

        var listing = await _service.GetDirectoryListingAsync(_root.FullName);
        var file = Assert.Single(listing.Entries, e => e.Kind == FileSystemEntryKind.File);

        Assert.Equal("report.final", file.Name);
        Assert.Equal("pdf", file.Extension);
        Assert.Equal(1234, file.Size);
        Assert.Equal(modified, file.LastWriteTime);
        Assert.Equal(path, file.FullPath);
    }

    [Fact]
    public async Task Directories_have_no_size()
    {
        _root.CreateSubdirectory("sub");

        var listing = await _service.GetDirectoryListingAsync(_root.FullName);
        var dir = Assert.Single(listing.Entries, e => e.Kind == FileSystemEntryKind.Directory);

        Assert.Null(dir.Size);
        Assert.True(dir.IsDirectory);
    }

    [Fact]
    public async Task Root_has_no_parent_link()
    {
        var root = Path.GetPathRoot(_root.FullName)!;

        var listing = await _service.GetDirectoryListingAsync(root);

        Assert.DoesNotContain(listing.Entries, e => e.IsParentLink);
    }

    [Fact]
    public async Task Missing_directory_throws()
    {
        var missing = Path.Combine(_root.FullName, "does-not-exist");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _service.GetDirectoryListingAsync(missing));
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.GetDirectoryListingAsync(_root.FullName, cts.Token));
    }

    [Theory]
    [InlineData("readme.txt", "readme", "txt")]
    [InlineData("archive.tar.gz", "archive.tar", "gz")]
    [InlineData(".gitignore", ".gitignore", "")]
    [InlineData("Makefile", "Makefile", "")]
    [InlineData("trailing.", "trailing.", "")]
    public void Splits_file_name_and_extension(string fileName, string expectedName, string expectedExtension)
    {
        var (name, extension) = DirectoryService.SplitFileName(fileName);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedExtension, extension);
    }
}
