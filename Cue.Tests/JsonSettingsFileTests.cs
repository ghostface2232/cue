using Cue.Services;

namespace Cue.Tests;

/// <summary>
/// The unpackaged channel's preference persistence (H3): without MSIX identity ApplicationData is
/// unavailable, so <see cref="JsonSettingsFile"/> must give settings a real on-disk life — surviving
/// a relaunch, starting from defaults on corruption, and never letting an IO failure surface.
/// </summary>
public sealed class JsonSettingsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cue-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Values_SurviveANewInstance()
    {
        // The three primitive shapes the preference classes store, round-tripped through a fresh
        // instance — the "relaunch" that used to reset every setting on the unpackaged channel.
        var store = new JsonSettingsFile(SettingsPath);
        store.Set("settings.ThemeMode", "Dark");
        store.Set("settings.NotificationsEnabled", false);
        store.Set("settings.DetailPanelWidth", 512.0);

        var reloaded = new JsonSettingsFile(SettingsPath);
        Assert.True(reloaded.TryGetValue("settings.ThemeMode", out var theme));
        Assert.Equal("Dark", theme);
        Assert.True(reloaded.TryGetValue("settings.NotificationsEnabled", out var notifications));
        Assert.Equal(false, notifications);
        Assert.True(reloaded.TryGetValue("settings.DetailPanelWidth", out var width));
        Assert.Equal(512.0, width);
    }

    [Fact]
    public void MissingKey_ReportsNotFound()
    {
        var store = new JsonSettingsFile(SettingsPath);
        Assert.False(store.TryGetValue("nav.today", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void CorruptFile_StartsFromDefaults_AndTheNextSetRecovers()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, "{not-json");

        var store = new JsonSettingsFile(SettingsPath);
        Assert.False(store.TryGetValue("settings.ThemeMode", out _));

        store.Set("settings.ThemeMode", "Light");
        var reloaded = new JsonSettingsFile(SettingsPath);
        Assert.True(reloaded.TryGetValue("settings.ThemeMode", out var theme));
        Assert.Equal("Light", theme);
    }

    [Fact]
    public void UnwritableFile_KeepsTheValueForTheSession()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, "{}");
        // Hold the file open with no sharing so both the load and the write-through fail.
        using var lockHandle = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var store = new JsonSettingsFile(SettingsPath);
        store.Set("settings.ThemeMode", "Dark");   // must not throw
        Assert.True(store.TryGetValue("settings.ThemeMode", out var theme));
        Assert.Equal("Dark", theme);
    }
}
