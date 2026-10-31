using NorthCommander.Core.Services;

namespace NorthCommander.Core.Tests;

public sealed class CommandRunnerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderCli_");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Runs_in_working_directory_and_captures_output_and_exit_code()
    {
        var output = Path.Combine(_root.FullName, "out.txt");

        Assert.Equal(0, await CommandRunner.RunAsync("mkdir test && echo hello", _root.FullName, output));
        Assert.True(Directory.Exists(Path.Combine(_root.FullName, "test")));
        Assert.Equal("hello", File.ReadAllText(output).Trim());

        Assert.Equal(3, await CommandRunner.RunAsync("exit 3", _root.FullName, output));
    }

    [Fact]
    public void Recognizes_cd()
    {
        var current = _root.FullName;
        Assert.True(CommandRunner.TryParseChangeDirectory("cd sub", current, out var target));
        Assert.Equal(Path.Combine(current, "sub"), target);
        Assert.True(CommandRunner.TryParseChangeDirectory("cd..", current, out target));
        Assert.Equal(Path.GetDirectoryName(current), target);
        Assert.True(CommandRunner.TryParseChangeDirectory("cd", current, out target));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target);
        Assert.False(CommandRunner.TryParseChangeDirectory("cdrecord x", current, out _));
        Assert.False(CommandRunner.TryParseChangeDirectory("echo cd", current, out _));
    }

    [Fact]
    public void Creates_directories()
    {
        var created = FileSystemOperations.CreateDirectory(_root.FullName, " new/nested ");

        Assert.Equal(Path.Combine(_root.FullName, "new", "nested"), created);
        Assert.True(Directory.Exists(created));
        Assert.Throws<IOException>(() => FileSystemOperations.CreateDirectory(_root.FullName, "new"));
        Assert.Throws<ArgumentException>(() => FileSystemOperations.CreateDirectory(_root.FullName, "../escape"));
        Assert.Throws<ArgumentException>(() => FileSystemOperations.CreateDirectory(_root.FullName, "  "));
    }
}
