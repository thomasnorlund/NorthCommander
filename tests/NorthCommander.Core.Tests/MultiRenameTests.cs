using NorthCommander.Core.Rename;

namespace NorthCommander.Core.Tests;

public sealed class MultiRenameTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderRename_");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("[N]", "Holiday")]
    [InlineData("[N2-5]", "olid")]
    [InlineData("[N3]", "l")]
    [InlineData("[N4-]", "iday")]
    [InlineData("[N2,3]", "oli")]
    [InlineData("[N-3-]", "day")]
    [InlineData("[N-3--2]", "da")]
    [InlineData("[N5-99]", "day")]
    [InlineData("[N9-12]", "")]
    [InlineData("x_[n]_[E]_[[C]]", "x_Holiday_jpg_[C]")]
    [InlineData("[E1-2]", "jp")]
    public void Expands_name_and_extension_tokens(string mask, string expected)
    {
        Assert.Equal(expected, RenameMask.Expand(mask, "Holiday", "jpg", 1, 1));
    }

    [Theory]
    [InlineData("[X]")]
    [InlineData("[N")]
    [InlineData("[N0-2]")]
    [InlineData("[Nabc]")]
    [InlineData("[C5]")]
    public void Rejects_invalid_masks(string mask)
    {
        Assert.Throws<FormatException>(() => RenameMask.Expand(mask, "a", "b", 1, 1));
    }

    [Fact]
    public void Counter_uses_start_step_and_digits()
    {
        var options = new MultiRenameOptions { NameMask = "Pic_[C]", CounterStart = 5, CounterStep = 10, CounterDigits = 3 };

        Assert.Equal(["Pic_005.jpg", "Pic_015.jpg", "Pic_025.jpg"], Enumerable.Range(0, 3).Select(i => MultiRename.BuildName("x.jpg", i, options)));
    }

    [Fact]
    public void Search_replace_and_extension_mask()
    {
        var options = new MultiRenameOptions { NameMask = "[N]", ExtensionMask = "txt", Search = " ", Replace = "_" };

        Assert.Equal("my_old_notes.txt", MultiRename.BuildName("my old notes.md", 0, options));
        Assert.Equal("README", MultiRename.BuildName("README.md", 0, options with { ExtensionMask = string.Empty, Search = string.Empty }));
    }

    [Fact]
    public void Preview_reports_invalid_duplicate_and_existing_names()
    {
        var a = Write("a.txt");
        var b = Write("b.txt");
        Write("taken.txt");

        Assert.All(MultiRename.Preview([a, b], new() { NameMask = "same" }), p => Assert.Equal("Duplicate new name", p.Error));
        Assert.Equal("Invalid character '/'", MultiRename.Preview([a], new() { NameMask = "x/y" })[0].Error);
        Assert.Equal("A file with this name already exists", MultiRename.Preview([a], new() { NameMask = "taken" })[0].Error);
        Assert.StartsWith("Unknown placeholder", MultiRename.Preview([a], new() { NameMask = "[Q]" })[0].Error);
        Assert.True(MultiRename.Preview([a], new())[0].IsUnchanged);
    }

    [Fact]
    public async Task Execute_renames_swaps_and_continues_after_errors()
    {
        var a = Write("a.txt", "A");
        var b = Write("b.txt", "B");
        var missing = Path.Combine(_root.FullName, "missing.txt");
        var c = Write("c.txt", "C");
        var plan = new List<RenamePreview>
        {
            new(a, "b.txt", null),
            new(b, "a.txt", null),
            new(missing, "gone.txt", null),
            new(c, "c-renamed.txt", null),
        };
        var reported = new List<RenameOutcome>();

        var outcomes = await MultiRename.ExecuteAsync(plan, new SyncProgress(reported));

        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));
        Assert.Equal("C", File.ReadAllText(Path.Combine(_root.FullName, "c-renamed.txt")));
        Assert.NotNull(outcomes[2].Error);
        Assert.All(outcomes.Where(o => o.Index != 2), o => Assert.Null(o.Error));
        Assert.Equal(4, reported.Count);
        Assert.DoesNotContain(_root.GetFiles(), f => f.Name.StartsWith(".ncrename-", StringComparison.Ordinal));
    }

    private string Write(string name, string content = "x")
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class SyncProgress(List<RenameOutcome> list) : IProgress<RenameOutcome>
    {
        public void Report(RenameOutcome value)
        {
            lock (list)
            {
                list.Add(value);
            }
        }
    }
}
