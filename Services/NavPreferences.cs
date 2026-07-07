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

    /// <summary>Whether a collapsible sidebar section (그룹 / 태그) is expanded. This is the durable source
    /// of truth for the section's open/closed state: the NavigationView collapses and re-expands these
    /// sections on its own as the pane opens/closes or the width-driven display mode changes, so the chosen
    /// state is remembered here and reasserted after each transition rather than left to the framework.</summary>
    public static bool IsSectionExpanded(string key, bool defaultExpanded = true)
        => LocalSettingsStore.TryGetValue($"nav.section.{key}", out var value) && value is bool persisted
            ? persisted
            : defaultExpanded;

    public static void SetSectionExpanded(string key, bool expanded)
        => LocalSettingsStore.Set($"nav.section.{key}", expanded);
}
