using System.Text.Json;
using Windows.Foundation.Collections;
using Windows.Storage;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

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

    public string AccentColor
    {
        get => StringValue(nameof(AccentColor), "System");
        set => Set(nameof(AccentColor), string.IsNullOrWhiteSpace(value) ? "System" : value);
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

    public static void ApplyThemeAndAccent(Window? window, AppPreferences preferences)
    {
        if (window?.Content is FrameworkElement root)
            root.RequestedTheme = preferences.ResolveTheme();

        ApplyAccent(preferences.AccentColor);
    }

    private static void ApplyAccent(string accent)
    {
        string[] keys =
        [
            "SystemAccentColor",
            "AccentFillColorDefault",
            "AccentFillColorSecondary",
            "AccentFillColorTertiary",
            "AccentTextFillColorPrimary",
        ];
        string[] brushKeys =
        [
            "AccentFillColorDefaultBrush",
            "AccentTextFillColorPrimaryBrush",
        ];

        if (string.Equals(accent, "System", StringComparison.OrdinalIgnoreCase) || !TryParseHex(accent, out var color))
        {
            foreach (var key in keys.Concat(brushKeys))
                Application.Current.Resources.Remove(key);
            return;
        }

        foreach (var key in keys)
            Application.Current.Resources[key] = color;
        foreach (var key in brushKeys)
            Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private void EnsureDefaultCustomDateMeanings()
    {
        if (CustomDateMeanings.Count == 0)
            CustomDateMeanings = [new CustomDateMeaning { Name = "월급날", DayOfMonth = 25 }];
    }

    private static bool IsValidMeaning(CustomDateMeaning meaning)
        => !string.IsNullOrWhiteSpace(meaning.Name) && meaning.DayOfMonth is >= 1 and <= 31;

    private static bool TryParseHex(string hex, out Windows.UI.Color color)
    {
        color = default;
        if (!hex.StartsWith('#') || hex.Length != 7)
            return false;
        try
        {
            var r = Convert.ToByte(hex[1..3], 16);
            var g = Convert.ToByte(hex[3..5], 16);
            var b = Convert.ToByte(hex[5..7], 16);
            color = ColorHelper.FromArgb(0xFF, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
