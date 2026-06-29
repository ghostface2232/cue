using System.Text.Json;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Cue.Services;

public enum CueThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>How the keyboard focus rectangle is shown. Cue hides Windows' high-visibility focus visual by
/// default (it reads as visual noise in the app's restrained style); <see cref="Auto"/> opts back into the
/// standard Windows behavior. High-contrast mode always shows focus regardless of this choice.</summary>
public enum CueFocusVisualMode
{
    /// <summary>키보드 포커스 숨김 (기본) — the focus rectangle is suppressed app-wide.</summary>
    Hidden,

    /// <summary>자동 (Windows 기본 동작) — the standard Windows focus rectangle is shown.</summary>
    Auto,
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
public sealed class AppPreferences : Cue.ViewModels.IListDisplayPreferences
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

    /// <summary>Whether Windows' keyboard focus rectangle is shown. Defaults to <see cref="CueFocusVisualMode.Hidden"/>
    /// — Cue suppresses the focus visual app-wide (see <see cref="ApplyFocusVisuals"/> and App.xaml).</summary>
    public CueFocusVisualMode KeyboardFocusMode
    {
        get => EnumValue(nameof(KeyboardFocusMode), CueFocusVisualMode.Hidden);
        set => Set(nameof(KeyboardFocusMode), value.ToString());
    }

    public bool AutoAfternoonForBareOneToSix
    {
        get => BoolValue(nameof(AutoAfternoonForBareOneToSix), true);
        set => Set(nameof(AutoAfternoonForBareOneToSix), value);
    }

    /// <summary>When on, a task completed today stays in its place on the active list, dimmed, for the rest
    /// of the local day instead of dropping out into its 완료한 일 section immediately; at the next day
    /// rollover it leaves the list and lives only in the completed section / Logbook. Off (the default)
    /// keeps the original open-only behavior — a completion drops out of the live list at once.</summary>
    public bool KeepCompletedForToday
    {
        get => BoolValue(nameof(KeepCompletedForToday), false);
        set => Set(nameof(KeepCompletedForToday), value);
    }

    /// <summary>When on, a dated list row shows its ISO-8601 week number next to the date ("· W27") and the
    /// quick-add parser recognizes week expressions ("W27", "27주차", "27주에", "W27까지", with an optional
    /// weekday) as dates — a single switch for the whole "연중 주차" feature. Off by default.</summary>
    public bool ShowWeekNumber
    {
        get => BoolValue(nameof(ShowWeekNumber), false);
        set => Set(nameof(ShowWeekNumber), value);
    }

    /// <summary>The global ordering applied to standard task lists — set once from any list's header and
    /// reflected on every list. Defaults to <see cref="Cue.ViewModels.TaskSortMode.Date"/> (날짜순). A value
    /// persisted by an older build (e.g. the removed "Manual" mode) fails to parse and falls back here too.</summary>
    public Cue.ViewModels.TaskSortMode SortMode
    {
        get => EnumValue(nameof(SortMode), Cue.ViewModels.TaskSortMode.Date);
        set => Set(nameof(SortMode), value.ToString());
    }

    /// <summary>Controls how a parsed week number that already lies in the past resolves: when on, it rolls
    /// to that week of the <i>next</i> year; when off (the default) it stays in the current ISO year even if
    /// the date is past. Only meaningful while <see cref="ShowWeekNumber"/> is on.</summary>
    public bool WeekNumberPastRollsToNextYear
    {
        get => BoolValue(nameof(WeekNumberPastRollsToNextYear), false);
        set => Set(nameof(WeekNumberPastRollsToNextYear), value);
    }

    /// <summary>The width the user last dragged the task detail panel to, shared across every task list so
    /// the resize sticks as you move between lists. Null until the panel has ever been resized; the page
    /// clamps it to its current window-dependent range and falls back to its own default when unset.</summary>
    public double? DetailPanelWidth
    {
        get => DoubleValue(nameof(DetailPanelWidth));
        set
        {
            if (value is { } width && width > 0)
                Set(nameof(DetailPanelWidth), width);
        }
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

    /// <summary>
    /// Applies the keyboard focus-rectangle preference to the app-wide focus-stroke brushes App.xaml defines
    /// (see the comment there). Cue suppresses Windows' high-visibility focus rectangle by default;
    /// <see cref="CueFocusVisualMode.Auto"/> opts back into it. <b>High-contrast mode always shows focus</b>
    /// regardless of the setting — an accessibility requirement — by forcing the brushes to the system's
    /// high-contrast colors. Mutates the existing brush <i>instances'</i> Color (control templates reference
    /// them by StaticResource), so the change takes effect on the next focus draw with no restart. Safe to
    /// call before the window has content; the active theme then falls back to the app's requested theme.
    /// </summary>
    public static void ApplyFocusVisuals(Window? window, AppPreferences preferences)
    {
        if (Application.Current?.Resources is not { } resources)
            return;

        var (outer, inner) = ResolveFocusColors(window, preferences);
        SetBrushColor(resources, "FocusStrokeColorOuterBrush", outer);
        SetBrushColor(resources, "FocusStrokeColorInnerBrush", inner);
        SetBrushColor(resources, "SystemControlFocusVisualPrimaryBrush", outer);
        SetBrushColor(resources, "SystemControlFocusVisualSecondaryBrush", inner);
    }

    private static (Color Outer, Color Inner) ResolveFocusColors(Window? window, AppPreferences preferences)
    {
        // Accessibility wins over the preference: in high contrast, always show focus, using the system's
        // own high-contrast text (outer) and window (inner) colors so it reads against the active HC theme.
        if (IsHighContrast())
        {
            try
            {
                var ui = new UISettings();
                return (ui.GetColorValue(UIColorType.Foreground), ui.GetColorValue(UIColorType.Background));
            }
            catch
            {
                return (Microsoft.UI.Colors.Black, Microsoft.UI.Colors.White);
            }
        }

        if (preferences.KeyboardFocusMode == CueFocusVisualMode.Hidden)
            return (Microsoft.UI.Colors.Transparent, Microsoft.UI.Colors.Transparent);

        // Auto: WinUI's default high-visibility focus colors for the active light/dark theme.
        return IsDarkTheme(window)
            ? (Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xB3, 0x00, 0x00, 0x00))
            : (Color.FromArgb(0xE4, 0x00, 0x00, 0x00), Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    }

    private static bool IsDarkTheme(Window? window)
        => window?.Content is FrameworkElement root
            ? root.ActualTheme == ElementTheme.Dark
            : Application.Current?.RequestedTheme == ApplicationTheme.Dark;

    /// <summary>True when Windows high-contrast is active. Guarded — <see cref="AccessibilitySettings"/> can
    /// throw on some desktop configurations; we then treat it as off (focus follows the preference).</summary>
    public static bool IsHighContrast()
    {
        try { return new AccessibilitySettings().HighContrast; }
        catch { return false; }
    }

    private static void SetBrushColor(ResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            brush.Color = color;
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

    private static double? DoubleValue(string name)
    {
        if (Store is { } store && store.TryGetValue(Key(name), out var persisted) && persisted is double number)
            return number;
        return Memory.TryGetValue(Key(name), out var value) && value is double remembered ? remembered : null;
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
