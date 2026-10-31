using System.Numerics;
using System.Text;

namespace NorthCommander.Core.Viewer;

/// <summary>Classic hex dump rows: <c>Offset | 16 hex bytes | ASCII</c>.</summary>
public static class HexFormatter
{
    public const int BytesPerRow = 16;

    public static long RowCount(long length) => (length + BytesPerRow - 1) / BytesPerRow;

    /// <summary>Hex digits needed for the largest offset of a file (at least 8).</summary>
    public static int OffsetDigits(long length)
    {
        var bits = 64 - BitOperations.LeadingZeroCount((ulong)Math.Max(length - 1, 1));
        return Math.Max(8, (bits + 3) / 4);
    }

    public static string FormatRow(long offset, ReadOnlySpan<byte> bytes, int offsetDigits)
    {
        var line = new StringBuilder(offsetDigits + 80);
        line.Append(offset.ToString("X" + offsetDigits, System.Globalization.CultureInfo.InvariantCulture)).Append(" | ");
        for (var i = 0; i < BytesPerRow; i++)
        {
            if (i == BytesPerRow / 2)
            {
                line.Append(' ');
            }

            line.Append(i < bytes.Length ? $"{bytes[i]:X2} " : "   ");
        }

        line.Append("| ");
        foreach (var b in bytes)
        {
            line.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return line.ToString();
    }

    /// <summary>Formats <paramref name="rows"/> rows starting at row <paramref name="firstRow"/>.</summary>
    public static IReadOnlyList<string> FormatPage(MappedFileSource source, long firstRow, int rows)
    {
        var digits = OffsetDigits(source.Length);
        var buffer = new byte[BytesPerRow * Math.Max(rows, 0)];
        var offset = firstRow * BytesPerRow;
        var read = source.Read(offset, buffer, 0, buffer.Length);
        var lines = new List<string>(rows);
        for (var start = 0; start < read; start += BytesPerRow)
        {
            lines.Add(FormatRow(offset + start, buffer.AsSpan(start, Math.Min(BytesPerRow, read - start)), digits));
        }

        return lines;
    }
}
