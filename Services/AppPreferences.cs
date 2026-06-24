using System.Text.Json;
using Windows.Foundation.Collections;
using Windows.Storage;
using Microsoft.UI.Xaml;

namespace Cue.Services;

public enum CueThemeMode
{
    System,
    Light,
    Dark,
}

public sealed class CustomDateMeaning
{
    public string Name { get; set; } = string.Empty;
    public int DayOfMonth { get; set; }
}

/// <summary>Last window position and size in physical pixels, used to restore placement across launches.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// App-local preferences. Task/project/label data still lives exclusively in the file-backed store.
/// </summary>
public sealed class AppPreferences
{
    private static readonly Dictionary<string, object?> Memory = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AppPreferences()
    {
        EnsureDefaultCustomDateMeanings();
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => EnumValue(nameof(FirstDayOfWeek), DayOfWeek.Monday);
        set => Set(nameof(FirstDayOfWeek), value.ToString());
    }

    public string TimeZoneId
    {
        get => StringValue(nameof(TimeZoneId), TimeZoneInfo.Local.Id);
        set => Set(nameof(TimeZoneId), string.IsNullOrWhiteSpace(value) ? TimeZoneInfo.Local.Id : value);
    }

    public CueThemeMode ThemeMode
    {
        get => EnumValue(nameof(ThemeMode), CueThemeMode.System);
        set => Set(nameof(ThemeMode), value.ToString());
    }

    public bool AutoAfternoonForBareOneToSix
    {
        get => BoolValue(nameof(AutoAfternoonForBareOneToSix), true);
        set => Set(nameof(AutoAfternoonForBareOneToSix), value);
    }

    public IReadOnlyList<CustomDateMeaning> CustomDateMeanings
    {
        get
        {
            var json = StringValue(nameof(CustomDateMeanings), string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<CustomDateMeaning>();

            try
            {
                return JsonSerializer.Deserialize<List<CustomDateMeaning>>(json, JsonOptions)
                    ?.Where(IsValidMeaning)
                    .ToList() ?? new List<CustomDateMeaning>();
            }
            catch
            {
                return Array.Empty<CustomDateMeaning>();
            }
        }
        set
        {
            var normalized = value
                .Where(IsValidMeaning)
                .GroupBy(meaning => meaning.Name.Trim(), StringComparer.Ordinal)
                .Select(group => new CustomDateMeaning
                {
                    Name = group.Key,
                    DayOfMonth = group.Last().DayOfMonth,
                })
                .OrderBy(meaning => meaning.Name, StringComparer.Ordinal)
                .ToList();
            Set(nameof(CustomDateMeanings), JsonSerializer.Serialize(normalized, JsonOptions));
        }
    }

    /// <summary>The last saved window placement, or null when the app has never persisted one (first run).</summary>
    public WindowPlacement? WindowPlacement
    {
        get
        {
            var json = StringValue(nameof(WindowPlacement), string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var placement = JsonSerializer.Deserialize<WindowPlacement>(json, JsonOptions);
                return placement is { Width: > 0, Height: > 0 } ? placement : null;
            }
            catch
            {
                return null;
            }
        }
        set
        {
            if (value is { Width: > 0, Height: > 0 })
                Set(nameof(WindowPlacement), JsonSerializer.Serialize(value, JsonOptions));
        }
    }

    public TimeZoneInfo ResolveTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch { return TimeZoneInfo.Local; }
    }

    public ElementTheme ResolveTheme()
        => ThemeMode switch
        {
            CueThemeMode.Light => ElementTheme.Light,
            CueThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    /// <summary>Applies the user's light/dark/system theme choice to the window root. The accent color
    /// is left to the system — Cue consumes WinUI's theme-aware accent tokens directly (see DESIGN.md
    /// "Restrained accent"), so there is no app-level accent override.</summary>
    public static void ApplyTheme(Window? window, AppPreferences preferences)
    {
        if (window?.Content is FrameworkElement root)
            root.RequestedTheme = preferences.ResolveTheme();
    }

    private void EnsureDefaultCustomDateMeanings()
    {
        if (CustomDateMeanings.Count == 0)
            CustomDateMeanings = [new CustomDateMeaning { Name = "월급날", DayOfMonth = 25 }];
    }

    private static bool IsValidMeaning(CustomDateMeaning meaning)
        => !string.IsNullOrWhiteSpace(meaning.Name) && meaning.DayOfMonth is >= 1 and <= 31;

    private static IPropertySet? Store
    {
        get
        {
            try { return ApplicationData.Current.LocalSettings.Values; }
            catch { return null; }
        }
    }

    private static string Key(string name) => $"settings.{name}";

    private static string StringValue(string name, string fallback)
    {
        if (Store is { } store && store.TryGetValue(Key(name), out var persisted) && persisted is string text)
            return text;
        return Memory.TryGetValue(Key(name), out var value) && value is string remembered ? remembered : fallback;
    }

    private static bool BoolValue(string name, bool fallback)
    {
        if (Store is { } store && store.TryGetValue(Key(name), out var persisted) && persisted is bool flag)
            return flag;
        return Memory.TryGetValue(Key(name), out var value) && value is bool remembered ? remembered : fallback;
    }

    private static T EnumValue<T>(string name, T fallback)
        where T : struct
    {
        var text = StringValue(name, fallback.ToString() ?? string.Empty);
        return Enum.TryParse<T>(text, out var parsed) ? parsed : fallback;
    }

    private static void Set(string name, object value)
    {
        Memory[Key(name)] = value;
        if (Store is { } store)
            store[Key(name)] = value;
    }
}
