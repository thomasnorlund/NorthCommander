using System.Buffers;
using System.Text;

namespace NorthCommander.Core.Viewer;

/// <summary>One screen line of the text viewer: bytes [Start, End) of the file rendered as <see cref="Text"/>.</summary>
public readonly record struct VisualLine(long Start, long End, string Text);

/// <summary>
/// Splits a <see cref="MappedFileSource"/> into UTF-8 screen lines on demand, without indexing the file.
/// The scroll position is a byte offset; lines are found by scanning forwards/backwards from it.
/// </summary>
/// <remarks>
/// Lines longer than <see cref="MaxLineBytes"/> are force-broken at absolute multiples of it, so scanning
/// forwards and backwards always agrees on line starts, even in binary files without line breaks.
/// </remarks>
public sealed class TextPager(MappedFileSource source)
{
    public const int MaxLineBytes = 8 * 1024;
    public const int MaxDisplayChars = 4096;
    public const int TabSize = 8;

    public long Length => source.Length;

    /// <summary>Reads the screen line starting at <paramref name="start"/>; <paramref name="wrapColumns"/> 0 disables wrapping.</summary>
    public VisualLine ReadLine(long start, int wrapColumns)
    {
        if (start >= Length)
        {
            return new VisualLine(start, start, string.Empty);
        }

        var limit = Math.Min(Length, (start / MaxLineBytes + 1) * MaxLineBytes);
        var toRead = (int)(limit - start);
        if (wrapColumns > 0)
        {
            // A wrapped line holds at most wrapColumns runes of at most 4 bytes, plus a trailing CR LF.
            toRead = Math.Min(toRead, (wrapColumns * 4) + 2);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            var bytes = buffer.AsSpan(0, source.Read(start, buffer, 0, toRead));
            var text = new StringBuilder(Math.Min(bytes.Length, 256));
            Span<char> chars = stackalloc char[2];
            var column = 0;
            var position = 0;
            while (position < bytes.Length)
            {
                var b = bytes[position];
                if (b == '\n')
                {
                    return new VisualLine(start, start + position + 1, text.ToString());
                }

                if (b == '\r')
                {
                    position++;
                    continue;
                }

                if (wrapColumns > 0 && column >= wrapColumns)
                {
                    break;
                }

                if (b == '\t')
                {
                    var spaces = TabSize - (column % TabSize);
                    if (wrapColumns > 0)
                    {
                        spaces = Math.Min(spaces, wrapColumns - column);
                    }

                    if (column < MaxDisplayChars)
                    {
                        text.Append(' ', spaces);
                    }

                    column += spaces;
                    position++;
                    continue;
                }

                if (Rune.DecodeFromUtf8(bytes[position..], out var rune, out var consumed) != OperationStatus.Done)
                {
                    rune = Rune.ReplacementChar;
                }

                if (rune.Value < 0x20 || rune.Value == 0x7F)
                {
                    rune = new Rune('·');
                }

                if (column < MaxDisplayChars)
                {
                    text.Append(chars[..rune.EncodeToUtf16(chars)]);
                }

                column++;
                position += Math.Max(consumed, 1);
            }

            return new VisualLine(start, start + position, text.ToString());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Up to <paramref name="rows"/> consecutive screen lines starting at <paramref name="top"/>.</summary>
    public IReadOnlyList<VisualLine> ReadPage(long top, int rows, int wrapColumns)
    {
        var lines = new List<VisualLine>(rows);
        var position = top;
        while (lines.Count < rows && position < Length)
        {
            var line = ReadLine(position, wrapColumns);
            lines.Add(line);
            position = line.End;
        }

        return lines;
    }

    /// <summary>Start of the screen line that contains <paramref name="offset"/>.</summary>
    public long AlignToLine(long offset, int wrapColumns)
    {
        offset = Math.Clamp(offset, 0, Length);
        var start = LogicalLineStart(offset);
        while (wrapColumns > 0)
        {
            var next = ReadLine(start, wrapColumns).End;
            if (next > offset || next == start)
            {
                break;
            }

            start = next;
        }

        return start;
    }

    /// <summary>Start of the screen line before the one starting at <paramref name="top"/>.</summary>
    public long PreviousLine(long top, int wrapColumns)
    {
        if (top <= 0)
        {
            return 0;
        }

        var start = LogicalLineStart(top - 1);
        while (wrapColumns > 0)
        {
            var next = ReadLine(start, wrapColumns).End;
            if (next >= top || next == start)
            {
                break;
            }

            start = next;
        }

        return start;
    }

    /// <summary>Top offset that shows the last <paramref name="rows"/> screen lines of the file.</summary>
    public long LastPageTop(int rows, int wrapColumns)
    {
        var top = Length;
        for (var i = 0; i < rows && top > 0; i++)
        {
            top = PreviousLine(top, wrapColumns);
        }

        return top;
    }

    /// <summary>Start of the (unwrapped, force-broken) line containing the byte at <paramref name="position"/>.</summary>
    private long LogicalLineStart(long position)
    {
        var boundary = position / MaxLineBytes * MaxLineBytes;
        var count = (int)(position - boundary);
        if (count == 0)
        {
            return position;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = source.Read(boundary, buffer, 0, count);
            var newline = buffer.AsSpan(0, read).LastIndexOf((byte)'\n');
            return newline >= 0 ? boundary + newline + 1 : boundary;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
