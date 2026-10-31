using System.Text.Json;
using System.Text.Json.Serialization;

namespace NorthCommander.Core.Settings;

/// <summary>Everything NorthCommander remembers between runs; stored next to the executable (portable).</summary>
public sealed class AppSettings
{
    public WindowSettings Window { get; set; } = new();

    public PanelSettings Left { get; set; } = new();

    public PanelSettings Right { get; set; } = new();

    /// <summary>"Left" or "Right".</summary>
    public string ActivePanel { get; set; } = "Left";

    public RenameSettings MultiRename { get; set; } = new();

    public SyncSettings Sync { get; set; } = new();
}

public sealed class WindowSettings
{
    public double? Width { get; set; }

    public double? Height { get; set; }

    public int? X { get; set; }

    public int? Y { get; set; }

    public bool IsMaximized { get; set; }
}

public sealed class PanelSettings
{
    public List<TabSettings> Tabs { get; set; } = [];

    public int SelectedIndex { get; set; }
}

public sealed class TabSettings
{
    public string Path { get; set; } = string.Empty;

    public bool IsLocked { get; set; }
}

public sealed class RenameSettings
{
    public string NameMask { get; set; } = "[N]";

    public string ExtensionMask { get; set; } = "[E]";

    public string SearchText { get; set; } = string.Empty;

    public string ReplaceText { get; set; } = string.Empty;

    public int CounterStart { get; set; } = 1;

    public int CounterStep { get; set; } = 1;

    public int CounterDigits { get; set; } = 1;
}

public sealed class SyncSettings
{
    public bool IncludeSubdirectories { get; set; } = true;

    public bool UseTimeTolerance { get; set; } = true;

    public bool HideIdentical { get; set; }
}

/// <summary>Reads/writes <see cref="AppSettings"/> as JSON. Never touches AppData or the registry.</summary>
public static class SettingsStore
{
    public const string FileName = "NorthCommander.config.json";

    /// <summary><c>NorthCommander.config.json</c> in the folder of NorthCommander.exe.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Returns defaults when the file is missing or unreadable.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically (temp file + rename); returns false if the folder is read-only.</summary>
    public static bool TrySave(AppSettings settings, string path)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
