using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Cue.Services;

namespace Cue.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly IReadOnlyDictionary<string, string> TimeZoneLabels = new Dictionary<string, string>
    {
        ["Korea Standard Time"] = "서울",
        ["Tokyo Standard Time"] = "도쿄",
        ["China Standard Time"] = "베이징, 홍콩",
        ["Taipei Standard Time"] = "타이베이",
        ["Singapore Standard Time"] = "싱가포르",
        ["Pacific Standard Time"] = "로스앤젤레스",
        ["Mountain Standard Time"] = "덴버",
        ["Central Standard Time"] = "시카고",
        ["Eastern Standard Time"] = "뉴욕",
        ["GMT Standard Time"] = "런던",
        ["W. Europe Standard Time"] = "베를린, 로마, 파리",
        ["Romance Standard Time"] = "파리, 마드리드",
        ["Central European Standard Time"] = "부다페스트, 프라하",
        ["Russian Standard Time"] = "모스크바",
        ["AUS Eastern Standard Time"] = "시드니, 멜버른",
    };

    private readonly AppPreferences _preferences;
    private readonly ObservableCollection<CustomDateMeaningRow> _customDateRows = new();
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _loading;

    public SettingsPage()
    {
        _preferences = App.Services.GetRequiredService<AppPreferences>();
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        CustomDatesList.ItemsSource = _customDateRows;
        PopulateFirstDays();
        PopulateTimeZones();
        PopulateThemes();
        ReloadCustomDateRows();
        AutoAfternoonSwitch.IsOn = _preferences.AutoAfternoonForBareOneToSix;
        VersionText.Text = $"버전 {AppVersion()}";
        ApplySelectedSection();
        _loading = false;
    }

    /// <summary>The app's display version, read from the assembly so it always matches what was shipped.
    /// Prefers the informational version (the csproj's &lt;Version&gt;, e.g. "0.1.0"), trimming any
    /// "+commit" SourceLink suffix; falls back to the three-part assembly version.</summary>
    private static string AppVersion()
    {
        var assembly = typeof(SettingsPage).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
            return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void PopulateFirstDays()
    {
        if (FirstDayCombo.Items.Count == 0)
        {
            AddComboItem(FirstDayCombo, "월요일", DayOfWeek.Monday);
            AddComboItem(FirstDayCombo, "일요일", DayOfWeek.Sunday);
            AddComboItem(FirstDayCombo, "토요일", DayOfWeek.Saturday);
        }
        SelectByTag(FirstDayCombo, _preferences.FirstDayOfWeek);
    }

    private void PopulateTimeZones()
    {
        if (TimeZoneCombo.Items.Count == 0)
        {
            foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
                AddComboItem(TimeZoneCombo, TimeZoneLabel(zone), zone.Id);
        }
        SelectByTag(TimeZoneCombo, _preferences.TimeZoneId);
    }

    private static string TimeZoneLabel(TimeZoneInfo zone)
    {
        if (TimeZoneLabels.TryGetValue(zone.Id, out var label))
            return label;

        var display = zone.DisplayName;
        var close = display.IndexOf(')', StringComparison.Ordinal);
        if (close >= 0 && close + 1 < display.Length)
            display = display[(close + 1)..].Trim();

        return display
            .Replace(" Time", string.Empty, StringComparison.Ordinal)
            .Replace(" Standard", string.Empty, StringComparison.Ordinal)
            .Replace(" Daylight", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private void PopulateThemes()
    {
        if (ThemeCombo.Items.Count == 0)
        {
            AddComboItem(ThemeCombo, "시스템", CueThemeMode.System);
            AddComboItem(ThemeCombo, "라이트", CueThemeMode.Light);
            AddComboItem(ThemeCombo, "다크", CueThemeMode.Dark);
        }
        SelectByTag(ThemeCombo, _preferences.ThemeMode);
    }

    private static void AddComboItem(ComboBox combo, string content, object tag)
        => combo.Items.Add(new ComboBoxItem { Content = content, Tag = tag });

    private static void SelectByTag(ComboBox combo, object tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag, tag) || string.Equals(item.Tag?.ToString(), tag.ToString(), StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private void ReloadCustomDateRows()
    {
        _customDateRows.Clear();
        foreach (var meaning in _preferences.CustomDateMeanings)
            _customDateRows.Add(new CustomDateMeaningRow(meaning.Name, meaning.DayOfMonth));

        var hasRows = _customDateRows.Count > 0;
        CustomDatesList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        CustomDatesEmptyHint.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => (App.CurrentWindow as MainWindow)?.NavigateBackFromSettings();

    private void SectionNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplySelectedSection();

    private void ApplySelectedSection()
    {
        if (TimeSection is null)
            return;

        var selected = (SectionNav.SelectedItem as ListViewItem)?.Tag?.ToString() ?? "Time";
        TimeSection.Visibility = selected == "Time" ? Visibility.Visible : Visibility.Collapsed;
        ParsingSection.Visibility = selected == "Parsing" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = selected == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsSection.Visibility = selected == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = selected == "About" ? Visibility.Visible : Visibility.Collapsed;

        UpdateNavGlyphs();

        var section = selected switch
        {
            "Parsing" => ParsingSection,
            "Appearance" => AppearanceSection,
            "Notifications" => NotificationsSection,
            "About" => AboutSection,
            _ => (FrameworkElement)TimeSection,
        };
        AnimateSectionIn(section);
    }

    /// <summary>
    /// Brighten the selected section's glyph to primary (matching its label, and the sidebar's
    /// selected-icon behavior); unselected glyphs stay secondary. The icon lives in each item's
    /// content, so the container template can't drive it — we set it here on selection change.
    /// </summary>
    private void UpdateNavGlyphs()
    {
        if (Application.Current.Resources["TextFillColorPrimaryBrush"] is not Brush primary ||
            Application.Current.Resources["TextFillColorSecondaryBrush"] is not Brush secondary)
            return;

        foreach (var item in SectionNav.Items.OfType<ListViewItem>())
        {
            if (item.Content is Grid grid &&
                grid.Children.OfType<FontIcon>().FirstOrDefault() is FontIcon glyph)
                glyph.Foreground = item.IsSelected ? primary : secondary;
        }
    }

    /// <summary>
    /// Settle the freshly-shown section in with a short fade + upward slide on Cue's signature pane
    /// curve (CubicBezier 0.1,0.9 0.2,1.0), so switching sections matches the app's motion language
    /// instead of snapping. Skipped when the system has animations disabled.
    /// </summary>
    private void AnimateSectionIn(FrameworkElement section)
    {
        if (!_animationsEnabled)
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(section, true);
        var visual = ElementCompositionPreview.GetElementVisual(section);
        var compositor = visual.Compositor;
        var spline = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, spline);
        fade.Duration = TimeSpan.FromMilliseconds(220);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0f, new Vector3(0f, 8f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, spline);
        slide.Duration = TimeSpan.FromMilliseconds(300);

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    private void FirstDayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FirstDayCombo.SelectedItem is not ComboBoxItem { Tag: DayOfWeek day })
            return;
        _preferences.FirstDayOfWeek = day;
    }

    private void TimeZoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TimeZoneCombo.SelectedItem is not ComboBoxItem { Tag: string id })
            return;
        _preferences.TimeZoneId = id;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: CueThemeMode mode })
            return;
        _preferences.ThemeMode = mode;
        AppPreferences.ApplyTheme(App.CurrentWindow, _preferences);
    }

    private void AutoAfternoonSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _preferences.AutoAfternoonForBareOneToSix = AutoAfternoonSwitch.IsOn;
    }

    private void AddCustomDate_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomDateNameBox.Text.Trim();
        if (double.IsNaN(CustomDateDayBox.Value))
            return;
        var day = (int)Math.Round(CustomDateDayBox.Value);
        if (name.Length == 0 || day is < 1 or > 31)
            return;

        var updated = _preferences.CustomDateMeanings
            .Where(meaning => !string.Equals(meaning.Name, name, StringComparison.Ordinal))
            .Append(new CustomDateMeaning { Name = name, DayOfMonth = day })
            .ToList();
        _preferences.CustomDateMeanings = updated;
        CustomDateNameBox.Text = string.Empty;
        CustomDateDayBox.Value = double.NaN;
        ReloadCustomDateRows();
    }

    private void RemoveCustomDate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;
        _preferences.CustomDateMeanings = _preferences.CustomDateMeanings
            .Where(meaning => !string.Equals(meaning.Name, name, StringComparison.Ordinal))
            .ToList();
        ReloadCustomDateRows();
    }
}

public sealed record CustomDateMeaningRow(string Name, int DayOfMonth)
{
    public string DayCaption => $"매월 {DayOfMonth}일";
}
