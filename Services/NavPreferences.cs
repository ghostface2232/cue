namespace Cue.Services;

/// <summary>
/// Persists which of the fixed sidebar lists (Today / Upcoming / Anytime / Logbook) the
/// user has chosen to show. This is a pure UI preference, so it lives in app-local settings rather
/// than the files-are-truth task store. Persistence goes through <see cref="LocalSettingsStore"/>,
/// which covers both channels (ApplicationData when packaged, a JSON settings file when not).
/// "모든 할 일" (All) is always shown and is not toggleable.
/// </summary>
public static class NavPreferences
{
    /// <summary>Whether a fixed list is shown. Until the user sets a preference, the list falls back to
    /// <paramref name="defaultVisible"/> (some lists — 앞으로 할 일 / 언젠가 할 일 — start hidden).</summary>
    public static bool IsVisible(string key, bool defaultVisible = true)
        => LocalSettingsStore.TryGetValue($"nav.{key}", out var value) && value is bool persisted
            ? persisted
            : defaultVisible;

    public static void SetVisible(string key, bool visible)
        => LocalSettingsStore.Set($"nav.{key}", visible);
}
