using Windows.Foundation.Collections;
using Windows.Storage;

namespace Cue.Services;

/// <summary>
/// The one place app-local preferences persist, bridging the two distribution channels. With MSIX
/// package identity (the Store channel) it is <c>ApplicationData.LocalSettings</c>, as always. Without
/// identity (the unpackaged GitHub/Inno channel) <c>ApplicationData</c> throws — previously every
/// preference then fell back to a process-lifetime dictionary, so the theme, time zone, notification
/// master switch, custom date meanings, window placement, panel width, and sidebar visibility all
/// silently reset on each launch. That channel now persists to a JSON file under
/// <c>%LOCALAPPDATA%\Cue</c> instead. Task data is unaffected — it lives exclusively in the
/// files-are-truth store under Documents.
/// </summary>
internal static class LocalSettingsStore
{
    // The deployment channel cannot change over a process lifetime, so the backend is resolved once
    // (per the RuntimeIdentity invariant: one runtime check, no #if split). Packaged-but-ApplicationData-
    // unavailable should not happen; if it ever does, the file store still gives working persistence.
    private static readonly Lazy<IPropertySet?> PackagedStore = new(() =>
    {
        if (!RuntimeIdentity.IsPackaged)
            return null;
        try { return ApplicationData.Current.LocalSettings.Values; }
        catch { return null; }
    });

    private static readonly Lazy<JsonSettingsFile> FileStore = new(() => new JsonSettingsFile(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cue",
            "settings.json")));

    public static bool TryGetValue(string key, out object? value)
    {
        if (PackagedStore.Value is { } packaged)
            return packaged.TryGetValue(key, out value);
        return FileStore.Value.TryGetValue(key, out value);
    }

    public static void Set(string key, object value)
    {
        if (PackagedStore.Value is { } packaged)
            packaged[key] = value;
        else
            FileStore.Value.Set(key, value);
    }
}
