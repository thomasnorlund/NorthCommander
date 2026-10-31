using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NorthCommander.Core.Rename;

/// <summary>
/// Expands Total Commander rename placeholders:
/// <list type="bullet">
/// <item><c>[N]</c> name, <c>[E]</c> extension, <c>[C]</c> counter;</item>
/// <item><c>[N2-5]</c> characters 2 to 5 (1-based, inclusive), <c>[N3]</c> one character, <c>[N2-]</c> from 2 to the end,
/// <c>[N2,4]</c> 4 characters from 2; negative positions count from the end (<c>[N-3-]</c> = last three);
/// the same ranges work for <c>[E…]</c>;</item>
/// <item><c>[[</c> and <c>]]</c> insert literal brackets.</item>
/// </list>
/// </summary>
public static partial class RenameMask
{
    /// <exception cref="FormatException">The mask contains an unknown or malformed placeholder.</exception>
    public static string Expand(string mask, string name, string extension, long counter, int counterDigits)
    {
        var result = new StringBuilder(mask.Length + name.Length);
        for (var i = 0; i < mask.Length; i++)
        {
            var c = mask[i];
            if (c == '[' && i + 1 < mask.Length && mask[i + 1] == '[')
            {
                result.Append('[');
                i++;
            }
            else if (c == ']' && i + 1 < mask.Length && mask[i + 1] == ']')
            {
                result.Append(']');
                i++;
            }
            else if (c == '[')
            {
                var close = mask.IndexOf(']', i + 1);
                if (close < 0)
                {
                    throw new FormatException($"Missing ']' after '{mask[i..]}'.");
                }

                result.Append(ExpandToken(mask[(i + 1)..close], name, extension, counter, counterDigits));
                i = close;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static string ExpandToken(string token, string name, string extension, long counter, int counterDigits)
    {
        if (token.Length > 0)
        {
            var range = token[1..];
            switch (char.ToUpperInvariant(token[0]))
            {
                case 'N':
                    return Slice(name, range, token);
                case 'E':
                    return Slice(extension, range, token);
                case 'C' when range.Length == 0:
                    var digits = Math.Clamp(counterDigits, 1, 18);
                    return counter < 0
                        ? "-" + (-counter).ToString("D" + digits, CultureInfo.InvariantCulture)
                        : counter.ToString("D" + digits, CultureInfo.InvariantCulture);
            }
        }

        throw new FormatException($"Unknown placeholder [{token}].");
    }

    private static string Slice(string source, string range, string token)
    {
        if (range.Length == 0)
        {
            return source;
        }

        var match = RangePattern().Match(range);
        if (!match.Success)
        {
            throw new FormatException($"Invalid range in [{token}]; use e.g. [{token[0]}2-5], [{token[0]}3], [{token[0]}2-] or [{token[0]}2,4].");
        }

        var start = ToIndex(match.Groups["x"].Value, source.Length, token);
        int end;
        if (match.Groups["len"].Success)
        {
            end = start + int.Parse(match.Groups["len"].Value, CultureInfo.InvariantCulture) - 1;
        }
        else if (match.Groups["dash"].Success)
        {
            end = match.Groups["y"].Success ? ToIndex(match.Groups["y"].Value, source.Length, token) : source.Length - 1;
        }
        else
        {
            end = start;
        }

        start = Math.Max(start, 0);
        end = Math.Min(end, source.Length - 1);
        return start <= end ? source[start..(end + 1)] : string.Empty;
    }

    private static int ToIndex(string value, int length, string token)
    {
        var position = int.Parse(value, CultureInfo.InvariantCulture);
        return position switch
        {
            > 0 => position - 1,
            < 0 => length + position,
            _ => throw new FormatException($"Positions start at 1 in [{token}]."),
        };
    }

    [GeneratedRegex(@"^(?<x>-?\d{1,6})(?:(?<dash>-)(?<y>-?\d{1,6})?|,(?<len>\d{1,6}))?$")]
    private static partial Regex RangePattern();
}
