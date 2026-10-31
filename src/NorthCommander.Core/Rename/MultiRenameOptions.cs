namespace NorthCommander.Core.Rename;

/// <summary>Settings of the multi-rename tool (Ctrl+M).</summary>
public sealed record MultiRenameOptions
{
    /// <summary>Mask for the name part, e.g. <c>Holiday_[C]_[N1-8]</c>.</summary>
    public string NameMask { get; init; } = "[N]";

    /// <summary>Mask for the extension (without the dot); an empty result drops the extension.</summary>
    public string ExtensionMask { get; init; } = "[E]";

    /// <summary>Text replaced in the complete new name (case-sensitive); empty disables search &amp; replace.</summary>
    public string Search { get; init; } = string.Empty;

    public string Replace { get; init; } = string.Empty;

    public long CounterStart { get; init; } = 1;

    public long CounterStep { get; init; } = 1;

    /// <summary>Minimum number of counter digits, padded with zeros (3 → 001).</summary>
    public int CounterDigits { get; init; } = 1;
}
