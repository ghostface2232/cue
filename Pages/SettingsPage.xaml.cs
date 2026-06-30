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

    /// <summary>Body width below which the settings nav collapses to icon-only (compact) mode.
    /// Matched to the app-wide sidebar's hide point: the NavigationView drops to Minimal mode
    /// (pane fully hidden) below its CompactModeThresholdWidth (640), so the settings nav goes
    /// compact at the same width the sidebar disappears.</summary>
    private const double CompactThreshold = 640;
    /// <summary>Hysteresis: expand back only when width exceeds the threshold by this much.</summary>
    private const double CompactHysteresis = 40;

    private readonly AppPreferences _preferences;
    private readonly UpdateService _updates;
    private readonly ObservableCollection<CustomDateMeaningRow> _customDateRows = new();
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _loading;
    private bool _isNavCompact;

    // Updater UI state. The button is one of: idle (checks), update-ready (downloads + installs), or
    // busy (disabled). _pendingUpdate holds a found-but-not-yet-installed update; _updateBusy gates
    // re-entrancy; _defaultButtonStyle is the button's resting style, restored when reverting from accent.
    private UpdateCheckResult? _pendingUpdate;
    private bool _updateBusy;
    private Style? _defaultButtonStyle;

    public SettingsPage()
    {
        _preferences = App.Services.GetRequiredService<AppPreferences>();
        _updates = App.Services.GetRequiredService<UpdateService>();
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        // The selected section's glyph is tinted imperatively (the container template can't drive an icon
        // that lives in the item's content), so re-tint it when the app theme changes — including the
        // user picking a new 화면 모드 right here — so it follows Cue's theme without a navigation.
        ActualThemeChanged += (_, _) => UpdateNavGlyphs();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        CustomDatesList.ItemsSource = _customDateRows;
        PopulateFirstDays();
        PopulateTimeZones();
        PopulateThemes();
        PopulateFocusModes();
        ReloadCustomDateRows();
        AutoAfternoonSwitch.IsOn = _preferences.AutoAfternoonForBareOneToSix;
        KeepCompletedSwitch.IsOn = _preferences.KeepCompletedForToday;
        WeekNumberSwitch.IsOn = _preferences.ShowWeekNumber;
        WeekRollForwardSwitch.IsOn = _preferences.WeekNumberPastRollsToNextYear;
        WeekRollForwardSwitch.IsEnabled = _preferences.ShowWeekNumber;
        VersionText.Text = $"버전 {AppVersion()}";
        _defaultButtonStyle = UpdateButton.Style;
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

    /// <summary>
    /// The single update button. With no pending update it checks GitHub for the latest release; once a
    /// newer one is found (button now accent), the next press downloads + verifies the installer and hands
    /// off to it silently, then exits so the installer can replace the running files.
    /// </summary>
    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        if (_pendingUpdate is { UpdateAvailable: true } ready)
            await DownloadAndInstallAsync(ready);
        else
            await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        SetUpdateBusy(true);
        UpdateStatusText.Text = "업데이트를 확인하고 있어요…";
        try
        {
            var result = await _updates.CheckAsync();
            if (result.UpdateAvailable)
            {
                _pendingUpdate = result;
                UpdateButton.Content = $"v{result.Latest} 다운로드 및 설치";
                UpdateButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style ?? _defaultButtonStyle;
                UpdateStatusText.Text = $"새 버전 v{result.Latest}을(를) 설치할 수 있어요.";
            }
            else
            {
                _pendingUpdate = null;
                UpdateButton.Content = "업데이트 확인";
                UpdateButton.Style = _defaultButtonStyle;
                UpdateStatusText.Text = "최신 버전을 사용하고 있어요.";
            }
        }
        catch (UpdateException exception)
        {
            _pendingUpdate = null;
            UpdateButton.Content = "업데이트 확인";
            UpdateButton.Style = _defaultButtonStyle;
            UpdateStatusText.Text = exception.Message;
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async Task DownloadAndInstallAsync(UpdateCheckResult update)
    {
        SetUpdateBusy(true);
        // Throttle the caption to whole-percent updates so it doesn't churn on every chunk.
        var lastPercent = -1;
        var progress = new Progress<double>(fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent == lastPercent)
                return;
            lastPercent = percent;
            UpdateStatusText.Text = $"다운로드 중… {percent}%";
        });

        try
        {
            var installerPath = await _updates.DownloadAsync(update, progress);
            UpdateStatusText.Text = "설치를 시작할게요. 잠시 후 앱이 다시 열려요.";
            UpdateService.LaunchInstaller(installerPath);
            // Release the program files so the silent installer can replace them; the helper relaunches Cue.
            Application.Current.Exit();
        }
        catch (UpdateException exception)
        {
            UpdateStatusText.Text = exception.Message;
            SetUpdateBusy(false);
        }
    }

    /// <summary>Toggles the spinner + disables the button for the duration of an async update step.</summary>
    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        UpdateButton.IsEnabled = !busy;
        UpdateSpinner.IsActive = busy;
        UpdateSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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

    private void PopulateFocusModes()
    {
        if (FocusCombo.Items.Count == 0)
        {
            AddComboItem(FocusCombo, "숨김", CueFocusVisualMode.Hidden);
            AddComboItem(FocusCombo, "자동", CueFocusVisualMode.Auto);
        }
        SelectByTag(FocusCombo, _preferences.KeyboardFocusMode);
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
        // Resolve the glyph tones for the IN-APP theme (the window root's ElementTheme), not the OS theme
        // that Application.Current.Resources follows — otherwise the glyphs keep the OS theme's color while
        // Cue is switched to a different mode. Re-run on ActualThemeChanged (see constructor) so a runtime
        // theme switch repaints them.
        if (ThemeResources.Brush("CueGlyphPrimaryBrush") is not Brush primary ||
            ThemeResources.Brush("CueGlyphSecondaryBrush") is not Brush secondary)
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
        // Auto-mode focus colors follow light/dark, so re-resolve them after a theme switch.
        AppPreferences.ApplyFocusVisuals(App.CurrentWindow, _preferences);
    }

    private void FocusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FocusCombo.SelectedItem is not ComboBoxItem { Tag: CueFocusVisualMode mode })
            return;
        _preferences.KeyboardFocusMode = mode;
        AppPreferences.ApplyFocusVisuals(App.CurrentWindow, _preferences);
    }

    private void AutoAfternoonSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _preferences.AutoAfternoonForBareOneToSix = AutoAfternoonSwitch.IsOn;
    }

    private void KeepCompletedSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        // The active lists pick this up the next time they load — returning from Settings recreates the
        // list page (and its view model), which reads the preference fresh.
        _preferences.KeepCompletedForToday = KeepCompletedSwitch.IsOn;
    }

    private void WeekNumberSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // Keep the dependent roll-forward toggle's enabled state in sync even while loading (it has no
        // persistence side effect); only the preference write is gated on _loading.
        WeekRollForwardSwitch.IsEnabled = WeekNumberSwitch.IsOn;
        if (_loading)
            return;
        // Display (list rows) and input recognition both read this; a fresh list / next parse pick it up.
        _preferences.ShowWeekNumber = WeekNumberSwitch.IsOn;
    }

    private void WeekRollForwardSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _preferences.WeekNumberPastRollsToNextYear = WeekRollForwardSwitch.IsOn;
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

    /// <summary>
    /// Collapses the settings nav to icon-only when the body is too narrow for the full labels,
    /// and expands it back once there is enough room (with hysteresis to avoid flicker).
    /// </summary>
    private void BodyGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (!_isNavCompact && width < CompactThreshold)
            SetNavCompact(true);
        else if (_isNavCompact && width > CompactThreshold + CompactHysteresis)
            SetNavCompact(false);
    }

    private void SetNavCompact(bool compact)
    {
        _isNavCompact = compact;
        var labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;

        NavLabelTime.Visibility = labelVisibility;
        NavLabelParsing.Visibility = labelVisibility;
        NavLabelAppearance.Visibility = labelVisibility;
        NavLabelNotifications.Visibility = labelVisibility;
        NavLabelAbout.Visibility = labelVisibility;

        // In compact mode, adjust item padding and column spacing to fit nicely in 50px width.
        // Keep Margin consistent (4, 2) and Padding (12, 0) consistent in both states.
        var itemMargin = new Thickness(4, 2, 4, 2);
        var itemPadding = new Thickness(12, 0, 12, 0);
        var columnSpacing = compact ? 0 : 8; // Default column spacing is CueGap8 = 8

        foreach (var item in SectionNav.Items.OfType<ListViewItem>())
        {
            item.Margin = itemMargin;
            item.Padding = itemPadding;

            if (item.Content is Grid grid)
            {
                grid.ColumnSpacing = columnSpacing;
            }
        }

        double expandedWidth = 200;
        try
        {
            if (Application.Current.Resources["CueSettingsNavWidth"] is double w)
                expandedWidth = w;
        }
        catch { }

        // In compact mode the nav width is fixed to 50px; expanded mode uses the design token.
        SectionNav.Width = compact ? 50 : expandedWidth;
    }
}

public sealed record CustomDateMeaningRow(string Name, int DayOfMonth)
{
    public string DayCaption => $"매월 {DayOfMonth}일";
}
