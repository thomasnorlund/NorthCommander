using NorthCommander.Core.Services;

namespace NorthCommander.Core.Rename;

/// <summary>Planned new name of one item; <see cref="Error"/> is set when it cannot be renamed.</summary>
public sealed record RenamePreview(string SourcePath, string NewName, string? Error)
{
    public bool IsUnchanged => Error is null && string.Equals(Path.GetFileName(SourcePath), NewName, StringComparison.Ordinal);
}

/// <summary>Result of renaming one item: the new path, or the error that stopped it.</summary>
public sealed record RenameOutcome(int Index, string? NewPath, string? Error);

/// <summary>Multi-rename: builds new names from masks, validates them, and renames sequentially.</summary>
public static class MultiRename
{
    private static readonly char[] InvalidNameChars = [.. Path.GetInvalidFileNameChars().Union(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])];

    /// <summary>New file name for the item at <paramref name="index"/> (the counter advances per item).</summary>
    /// <exception cref="FormatException">A mask is invalid.</exception>
    public static string BuildName(string fileName, int index, MultiRenameOptions options)
    {
        var extension = Path.GetExtension(fileName);
        var name = fileName[..^extension.Length];
        extension = extension.TrimStart('.');
        var counter = options.CounterStart + (index * options.CounterStep);

        var newName = RenameMask.Expand(options.NameMask, name, extension, counter, options.CounterDigits);
        var newExtension = RenameMask.Expand(options.ExtensionMask, name, extension, counter, options.CounterDigits);
        var result = newExtension.Length > 0 ? $"{newName}.{newExtension}" : newName;
        return options.Search.Length > 0 ? result.Replace(options.Search, options.Replace, StringComparison.Ordinal) : result;
    }

    /// <summary>Builds and validates all new names: invalid characters, duplicates and existing targets are reported per item.</summary>
    public static IReadOnlyList<RenamePreview> Preview(IReadOnlyList<string> paths, MultiRenameOptions options)
    {
        var sources = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var previews = new RenamePreview[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            string newName;
            try
            {
                newName = BuildName(Path.GetFileName(paths[i]), i, options);
            }
            catch (FormatException ex)
            {
                previews[i] = new RenamePreview(paths[i], string.Empty, ex.Message);
                continue;
            }

            previews[i] = new RenamePreview(paths[i], newName, ValidateName(newName) ?? ValidateTarget(paths[i], newName, sources));
        }

        // Two items must not end up with the same name in the same folder (case-insensitive, as on Windows).
        var duplicates = previews
            .Where(p => p.Error is null)
            .GroupBy(p => Path.Combine(FolderOf(p.SourcePath), p.NewName), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();
        for (var i = 0; i < previews.Length; i++)
        {
            if (duplicates.Contains(previews[i]))
            {
                previews[i] = previews[i] with { Error = "Duplicate new name" };
            }
        }

        return previews;
    }

    /// <summary>
    /// Renames every valid, changed item in order on a background thread. Failures are reported per item and do not stop
    /// the rest. Swaps and chains (a→b, b→a) work: items whose target is another item's current name move to a temporary
    /// name first.
    /// </summary>
    public static Task<IReadOnlyList<RenameOutcome>> ExecuteAsync(
        IReadOnlyList<RenamePreview> plan,
        IProgress<RenameOutcome>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(plan, progress, cancellationToken), cancellationToken);

    private static IReadOnlyList<RenameOutcome> Execute(IReadOnlyList<RenamePreview> plan, IProgress<RenameOutcome>? progress, CancellationToken cancellationToken)
    {
        var outcomes = new RenameOutcome?[plan.Count];
        var current = plan.Select(p => p.SourcePath).ToArray();
        var sources = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);

        void Report(RenameOutcome outcome)
        {
            outcomes[outcome.Index] = outcome;
            progress?.Report(outcome);
        }

        for (var i = 0; i < plan.Count; i++)
        {
            var item = plan[i];
            if (item.Error is not null)
            {
                Report(new RenameOutcome(i, null, $"Skipped: {item.Error}"));
            }
            else if (item.IsUnchanged)
            {
                Report(new RenameOutcome(i, item.SourcePath, null));
            }
            else
            {
                var target = Path.Combine(FolderOf(item.SourcePath), item.NewName);
                if (!string.Equals(target, item.SourcePath, StringComparison.OrdinalIgnoreCase) && sources.Contains(target))
                {
                    try
                    {
                        var temporary = Path.Combine(FolderOf(item.SourcePath), $".ncrename-{Guid.NewGuid():N}");
                        Move(item.SourcePath, temporary);
                        current[i] = temporary;
                    }
                    catch (Exception ex) when (IsFileSystemError(ex))
                    {
                        Report(new RenameOutcome(i, null, ex.Message));
                    }
                }
            }
        }

        for (var i = 0; i < plan.Count; i++)
        {
            if (outcomes[i] is not null)
            {
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Restore(plan[i].SourcePath, current[i]);
                Report(new RenameOutcome(i, null, "Canceled"));
                continue;
            }

            try
            {
                Report(new RenameOutcome(i, FileSystemOperations.Rename(current[i], plan[i].NewName), null));
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                Restore(plan[i].SourcePath, current[i]);
                Report(new RenameOutcome(i, null, ex.Message));
            }
        }

        return outcomes.Select(o => o!).ToList();
    }

    private static void Restore(string original, string current)
    {
        if (string.Equals(original, current, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Move(current, original);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // The item stays under its temporary name; the error is already reported for it.
        }
    }

    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Empty name";
        }

        if (name is "." or "..")
        {
            return "Invalid name";
        }

        var invalid = name.IndexOfAny(InvalidNameChars);
        if (invalid >= 0)
        {
            return char.IsControl(name[invalid]) ? "Invalid control character" : $"Invalid character '{name[invalid]}'";
        }

        return name.EndsWith('.') || name.EndsWith(' ') ? "Name must not end with '.' or space" : null;
    }

    private static string? ValidateTarget(string source, string newName, HashSet<string> sources)
    {
        var target = Path.Combine(FolderOf(source), newName);
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase) || sources.Contains(target))
        {
            return null;
        }

        return Path.Exists(target) ? "A file with this name already exists" : null;
    }

    private static string FolderOf(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? string.Empty;

    private static bool IsFileSystemError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException;
}
