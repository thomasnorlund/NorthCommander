using NorthCommander.Core.Services;

namespace NorthCommander.Core.Tests;

public sealed class FileSystemOperationsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderFsOps_");

    public void Dispose()
    {
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task Delete_removes_files_and_nested_folders_including_read_only()
    {
        var file = Write("a.txt");
        Write("dir/sub/b.txt");
        var readOnly = Write("dir/c.txt");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        await FileSystemOperations.DeleteAsync([file, PathOf("dir")]);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(PathOf("dir")));
    }

    [Fact]
    public async Task Delete_removes_symlink_but_not_its_target()
    {
        var target = Write("target/keep.txt");
        var link = PathOf("link");
        Directory.CreateSymbolicLink(link, PathOf("target"));

        await FileSystemOperations.DeleteAsync([link]);

        Assert.False(Path.Exists(link));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Delete_ignores_missing_paths()
    {
        await FileSystemOperations.DeleteAsync([PathOf("missing.txt")]);
    }

    [Fact]
    public void Rename_file_and_folder()
    {
        var file = Write("old.txt");
        Write("folder/x.txt");

        Assert.Equal(PathOf("new.txt"), FileSystemOperations.Rename(file, " new.txt "));
        Assert.Equal(PathOf("renamed"), FileSystemOperations.Rename(PathOf("folder"), "renamed"));
        Assert.True(File.Exists(PathOf("new.txt")));
        Assert.True(File.Exists(PathOf("renamed/x.txt")));
    }

    [Fact]
    public void Rename_case_only()
    {
        Write("folder/x.txt");

        var result = FileSystemOperations.Rename(PathOf("folder"), "Folder");

        Assert.Equal(PathOf("Folder"), result);
        Assert.Equal(["Folder"], _root.GetDirectories().Select(d => d.Name));
    }

    [Fact]
    public void Rename_rejects_existing_and_invalid_names()
    {
        var file = Write("a.txt");
        Write("b.txt");

        Assert.Throws<IOException>(() => FileSystemOperations.Rename(file, "b.txt"));
        Assert.Throws<ArgumentException>(() => FileSystemOperations.Rename(file, "sub/c.txt"));
        Assert.Throws<ArgumentException>(() => FileSystemOperations.Rename(file, " "));
        Assert.True(File.Exists(file));
    }

    private string PathOf(string relative) => Path.Combine(_root.FullName, relative);

    private string Write(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        return path;
    }
}
