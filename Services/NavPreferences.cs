using Windows.Foundation.Collections;
using Windows.Storage;

namespace Cue.Services;

/// <summary>
/// Persists which of the fixed sidebar lists (Today / Upcoming / Anytime / Someday / Logbook) the
/// user has chosen to show. This is a pure UI preference, so it lives in app-local settings rather
/// than the files-are-truth task store. "Cue" (Inbox) is always shown and is not toggleable.
/// Falls back to an in-memory store when there is no package identity (unpackaged runs).
/// </summary>
public static class NavPreferences
{
    private static readonly Dictionary<string, bool> Memory = new();

    private static IPropertySet? Store
    {
        get
        {
            try { return ApplicationData.Current.LocalSettings.Values; }
            catch { return null; }
        }
    }

    /// <summary>Whether a fixed list is shown. Lists default to visible.</summary>
    public static bool IsVisible(string key)
    {
        if (Store is { } store && store.TryGetValue($"nav.{key}", out var value) && value is bool persisted)
            return persisted;
        return Memory.TryGetValue(key, out var remembered) ? remembered : true;
    }

    public static void SetVisible(string key, bool visible)
    {
        Memory[key] = visible;
        if (Store is { } store) store[$"nav.{key}"] = visible;
    }
}
