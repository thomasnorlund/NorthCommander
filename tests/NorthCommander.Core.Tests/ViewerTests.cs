using System.Text;
using NorthCommander.Core.Viewer;

namespace NorthCommander.Core.Tests;

public sealed class ViewerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderViewer_");
    private readonly List<MappedFileSource> _sources = [];

    public void Dispose()
    {
        _sources.ForEach(s => s.Dispose());
        _root.Delete(recursive: true);
    }

    [Fact]
    public void Reads_lines_with_crlf_tabs_and_utf8()
    {
        var pager = Pager("one\r\nt\twö\n€nd");

        var lines = pager.ReadPage(0, 10, 0);

        Assert.Equal(["one", "t       wö", "€nd"], lines.Select(l => l.Text));
        Assert.Equal(5, lines[1].Start);
        Assert.Equal(pager.Length, lines[^1].End);
    }

    [Fact]
    public void Wraps_long_lines_and_swallows_newline_at_wrap_edge()
    {
        var pager = Pager("abcdefgh\nxy");

        var lines = pager.ReadPage(0, 10, 4);

        Assert.Equal(["abcd", "efgh", "xy"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Control_and_invalid_bytes_are_replaced()
    {
        var pager = Pager([0x41, 0x00, 0xFF, 0x42]);

        Assert.Equal("A·\uFFFDB", pager.ReadLine(0, 0).Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100)]
    public void Scrolling_backwards_visits_the_same_lines_as_forwards(int wrap)
    {
        var text = new StringBuilder();
        var random = new Random(42);
        for (var i = 0; i < 300; i++)
        {
            text.Append(new string((char)('a' + (i % 26)), random.Next(0, 30))).Append(i % 5 == 0 ? "\r\n" : "\n");
        }

        text.Append(new string('z', TextPager.MaxLineBytes + 500)).Append("\nend");
        var pager = Pager(text.ToString());

        var forward = new List<long>();
        for (long top = 0; top < pager.Length; top = pager.ReadLine(top, wrap).End)
        {
            forward.Add(top);
        }

        var backward = new List<long>();
        for (var top = pager.LastPageTop(1, wrap); ; top = pager.PreviousLine(top, wrap))
        {
            backward.Add(top);
            if (top == 0)
            {
                break;
            }
        }

        backward.Reverse();
        Assert.Equal(forward, backward);
        Assert.All(forward, top => Assert.Equal(top, pager.AlignToLine(top, wrap)));
    }

    [Fact]
    public void Align_and_last_page()
    {
        var pager = Pager("aaa\nbbb\nccc\n");

        Assert.Equal(4, pager.AlignToLine(6, 0));
        Assert.Equal(4, pager.LastPageTop(2, 0));
        Assert.Equal(0, pager.LastPageTop(10, 0));
    }

    [Fact]
    public void Empty_file_has_no_lines()
    {
        var pager = Pager(string.Empty);

        Assert.Empty(pager.ReadPage(0, 5, 80));
        Assert.Equal(0, pager.LastPageTop(5, 80));
    }

    [Fact]
    public void Reads_across_mapping_windows()
    {
        var data = new byte[(int)MappedFileSource.WindowSize + 100];
        new Random(1).NextBytes(data);
        var source = Source(data);

        var buffer = new byte[64];
        var offset = MappedFileSource.WindowSize - 10;
        Assert.Equal(64, source.Read(offset, buffer, 0, 64));
        Assert.Equal(data.AsSpan((int)offset, 64).ToArray(), buffer);
        Assert.Equal(100, source.Read(MappedFileSource.WindowSize, new byte[500], 0, 500));
    }

    [Fact]
    public void Hex_rows()
    {
        var source = Source(Encoding.ASCII.GetBytes("Hello, hex world!\n"));

        var rows = HexFormatter.FormatPage(source, 0, 10);

        Assert.Equal(2, rows.Count);
        Assert.Equal("00000000 | 48 65 6C 6C 6F 2C 20 68  65 78 20 77 6F 72 6C 64 | Hello, hex world", rows[0]);
        Assert.StartsWith("00000010 | 21 0A    ", rows[1]);
        Assert.EndsWith("| !.", rows[1]);
        Assert.Equal(9, HexFormatter.OffsetDigits(0x1_0000_0001));
    }

    private TextPager Pager(string text) => new(Source(Encoding.UTF8.GetBytes(text)));

    private TextPager Pager(byte[] data) => new(Source(data));

    private MappedFileSource Source(byte[] data)
    {
        var path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, data);
        var source = new MappedFileSource(path);
        _sources.Add(source);
        return source;
    }
}
