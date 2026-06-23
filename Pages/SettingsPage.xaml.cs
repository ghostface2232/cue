using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Cue.Services;

namespace Cue.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly (string Name, string Value)[] AccentOptions =
    {
        ("시스템", "System"),
        ("파랑", "#2563EB"),
        ("민트", "#0F766E"),
        ("초록", "#15803D"),
        ("보라", "#7C3AED"),
        ("빨강", "#DC2626"),
        ("주황", "#EA580C"),
    };

    private readonly AppPreferences _preferences;
    private readonly ObservableCollection<CustomDateMeaningRow> _customDateRows = new();
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
        BuildAccentSwatches();
        ReloadCustomDateRows();
        AutoAfternoonSwitch.IsOn = _preferences.AutoAfternoonForBareOneToSix;
        _loading = false;
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
                AddComboItem(TimeZoneCombo, $"{zone.DisplayName} · {zone.Id}", zone.Id);
        }
        SelectByTag(TimeZoneCombo, _preferences.TimeZoneId);
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

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var (name, value) in AccentOptions)
        {
            var selected = string.Equals(_preferences.AccentColor, value, StringComparison.OrdinalIgnoreCase);
            FrameworkElement content = string.Equals(value, "System", StringComparison.Ordinal)
                ? new TextBlock { Text = name, Margin = new Thickness(8, 0, 8, 0) }
                : new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = BrushFromHex(value),
                };
            var button = new Button
            {
                Tag = value,
                Width = string.Equals(value, "System", StringComparison.Ordinal) ? 72 : 34,
                Height = 34,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(17),
                Content = content,
                BorderThickness = selected ? new Thickness(2) : new Thickness(1),
                BorderBrush = selected
                    ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                    : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            };
            ToolTipService.SetToolTip(button, name);
            button.Click += AccentSwatch_Click;
            AccentSwatches.Children.Add(button);
        }
    }

    private static Brush BrushFromHex(string hex)
    {
        var r = Convert.ToByte(hex[1..3], 16);
        var g = Convert.ToByte(hex[3..5], 16);
        var b = Convert.ToByte(hex[5..7], 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
    }

    private void ReloadCustomDateRows()
    {
        _customDateRows.Clear();
        foreach (var meaning in _preferences.CustomDateMeanings)
            _customDateRows.Add(new CustomDateMeaningRow(meaning.Name, meaning.DayOfMonth));
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
        AppPreferences.ApplyThemeAndAccent(App.CurrentWindow, _preferences);
    }

    private void AutoAfternoonSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _preferences.AutoAfternoonForBareOneToSix = AutoAfternoonSwitch.IsOn;
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value })
            return;
        _preferences.AccentColor = value;
        AppPreferences.ApplyThemeAndAccent(App.CurrentWindow, _preferences);
        BuildAccentSwatches();
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
