using NorthCommander.Core.Settings;

namespace NorthCommander.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderSettings_");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Round_trips_and_falls_back_to_defaults()
    {
        var path = Path.Combine(_root.FullName, SettingsStore.FileName);
        Assert.Equal("[N]", SettingsStore.Load(path).MultiRename.NameMask);

        var settings = new AppSettings { ActivePanel = "Right" };
        settings.Left.Tabs.Add(new TabSettings { Path = "/data", IsLocked = true });
        settings.Window.Width = 1300;
        settings.MultiRename.NameMask = "Photo_[C]";
        Assert.True(SettingsStore.TrySave(settings, path));

        var loaded = SettingsStore.Load(path);
        Assert.Equal("Right", loaded.ActivePanel);
        Assert.True(loaded.Left.Tabs.Single().IsLocked);
        Assert.Equal(1300, loaded.Window.Width);
        Assert.Equal("Photo_[C]", loaded.MultiRename.NameMask);
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, "{ not json");
        Assert.Equal("Left", SettingsStore.Load(path).ActivePanel);
    }

    [Fact]
    public void Default_path_is_next_to_the_executable() =>
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "NorthCommander.config.json"), SettingsStore.DefaultPath);
}
