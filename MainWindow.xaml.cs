using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cue.Pages;
using Cue.Storage.Index;
using Cue.ViewModels;
using Cue.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Cue;

public sealed partial class MainWindow : Window
{
    private TaskListNavigation? _currentNavigation;
    private readonly DialogService _dialogs;

    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // The system caption buttons (minimize/maximize/close) are drawn by AppWindow, not XAML, so
        // their glyph color must be set per theme — otherwise they stay dark in dark mode. Re-apply
        // whenever the app theme changes.
        if (Content is FrameworkElement root)
            root.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
        ApplyCaptionButtonColors();

        NavView.Loaded += NavView_Loaded;
    }

    /// <summary>Tints the system caption buttons to match the current theme (transparent backgrounds,
    /// theme-appropriate glyph color, subtle hover/press fills).</summary>
    private void ApplyCaptionButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        var isDark = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
        var glyph = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var inactiveGlyph = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
            : Windows.UI.Color.FromArgb(0xFF, 0x86, 0x86, 0x86);

        titleBar.ButtonForegroundColor = glyph;
        titleBar.ButtonHoverForegroundColor = glyph;
        titleBar.ButtonPressedForegroundColor = glyph;
        titleBar.ButtonInactiveForegroundColor = inactiveGlyph;

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x28, 0x00, 0x00, 0x00);
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private async void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            RebuildLiveNavigation();
            ApplyNavVisibility();
        });
    }

    private async void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        await RunSafelyAsync(() => HandleSelectionChangedAsync(args));
    }

    private async Task HandleSelectionChangedAsync(NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        if (item.Tag is string action && action is "create-project" or "create-label")
        {
            var isProject = action == "create-project";
            var name = await PromptNameAsync(isProject ? "새 그룹" : "새 태그", isProject ? "그룹 이름" : "태그 이름");
            if (name is not null)
            {
                if (isProject) await ViewModel.CreateProjectCommand.ExecuteAsync(name);
                else await ViewModel.CreateLabelCommand.ExecuteAsync(name);
            }
            // Recreate the action row even after cancel so it does not remain selected/dead.
            RebuildLiveNavigation();
            NavView.SelectedItem = null;
            return;
        }

        if (item.Tag is not (string or TaskListNavigation)) return;
        _currentNavigation = item.Tag as TaskListNavigation;
        NavFrame.Navigate(typeof(TaskListPage), item.Tag);
        NavFrame.BackStack.Clear(); // flat navigation — no back history between lists
    }

    private void RebuildLiveNavigation()
    {
        ProjectsGroup.MenuItems.Clear();
        ProjectsGroup.MenuItems.Add(new NavigationViewItem { Content = "+ 새 그룹", Tag = "create-project" });
        foreach (var project in ViewModel.Projects)
            ProjectsGroup.MenuItems.Add(CreateProjectItem(project));

        LabelsGroup.MenuItems.Clear();
        LabelsGroup.MenuItems.Add(new NavigationViewItem { Content = "+ 새 태그", Tag = "create-label" });
        foreach (var label in ViewModel.Labels)
            LabelsGroup.MenuItems.Add(CreateLabelItem(label));
    }

    // The fixed lists the user can show/hide from the sidebar context menu. "Cue" (Inbox) is omitted —
    // it is the home list and is always present.
    private static readonly (string Key, string Glyph, string Name)[] ToggleableNav =
    {
        ("today", "", "오늘 할 일"),
        ("upcoming", "", "앞으로 할 일"),
        ("anytime", "", "언제든 할 일"),
        ("someday", "", "나중에 할 일"),
        ("logbook", "", "완료한 일"),
        ("priority", "", "중요도"),
    };

    private NavigationViewItem NavItemFor(string key) => key switch
    {
        "today" => TodayItem,
        "upcoming" => UpcomingItem,
        "anytime" => AnytimeItem,
        "someday" => SomedayItem,
        "logbook" => LogbookItem,
        "priority" => PriorityItem,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private void ApplyNavVisibility()
    {
        foreach (var (key, _, _) in ToggleableNav)
            NavItemFor(key).Visibility = NavPreferences.IsVisible(key) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Right-click anywhere in the sidebar pane opens a checkable list of the fixed views to
    /// show/hide. Gated to the pane so right-clicks on the content frame are left alone.</summary>
    private void NavView_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(NavView);
        if (position.X > NavView.OpenPaneLength) return;
        e.Handled = true;
        ShowNavToggleFlyout(position);
    }

    private void ShowNavToggleFlyout(Windows.Foundation.Point position)
    {
        var panel = new StackPanel { Spacing = 1, MinWidth = 220 };
        foreach (var (key, glyph, name) in ToggleableNav)
            panel.Children.Add(BuildNavToggleRow(key, glyph, name));
        new Flyout
        {
            Content = panel,
            FlyoutPresenterStyle = null,
        }.ShowAt(NavView, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
    }

    /// <summary>One checkable row mirroring the detail label list: glyph + name on the left, an accent
    /// check on the right when shown. Clicking toggles in place and keeps the flyout open.</summary>
    private Button BuildNavToggleRow(string key, string glyph, string name)
    {
        var check = new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = NavPreferences.IsVisible(key) ? Visibility.Visible : Visibility.Collapsed,
        };
        if (AccentBrush() is { } accent) check.Foreground = accent;

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(check, 2);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(check);

        var button = new Button
        {
            Content = grid,
            MinWidth = 220,
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        button.Click += (_, _) =>
        {
            var visible = !NavPreferences.IsVisible(key);
            NavPreferences.SetVisible(key, visible);
            check.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            var item = NavItemFor(key);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible && ReferenceEquals(NavView.SelectedItem, item)) NavigateHome();
        };
        return button;
    }

    private static Microsoft.UI.Xaml.Media.Brush? AccentBrush() => ThemeBrush("AccentTextFillColorPrimaryBrush");

    private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key)
    {
        try { return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key]; }
        catch { return null; }
    }

    private NavigationViewItem CreateProjectItem(ProjectListItem project)
    {
        var icon = new FontIcon { Glyph = string.IsNullOrEmpty(project.Icon) ? "\uE8B7" : project.Icon };
        var item = new NavigationViewItem
        {
            Content = project.Name,
            Tag = new TaskListNavigation(TaskListMode.Project, project.Id, project.Name, project.DeadlineDate),
            Icon = icon,
        };
        // Tapping the glyph opens the icon picker directly (no right-click depth); Handled stops the
        // tap from also navigating into the project.
        icon.Tapped += (sender, e) => { e.Handled = true; ShowProjectIconPicker((FrameworkElement)sender, project.Id, project.Icon); };
        if (ViewModel.ProjectTaskCounts.TryGetValue(project.Id, out var count) && count > 0)
            item.InfoBadge = new InfoBadge { Value = count };
        item.ContextFlyout = CreateRecordMenu(project, isProject: true, item);
        return item;
    }

    private NavigationViewItem CreateLabelItem(LabelListItem label)
    {
        var icon = new FontIcon { Glyph = "\uE8EC" };
        if (new HexToBrushConverter().Convert(label.Color ?? string.Empty, typeof(Microsoft.UI.Xaml.Media.Brush), null!, null!) is Microsoft.UI.Xaml.Media.Brush brush)
            icon.Foreground = brush;
        var item = new NavigationViewItem
        {
            Content = label.Name,
            Tag = new TaskListNavigation(TaskListMode.Label, label.Id, label.Name),
            Icon = icon,
        };
        // Tapping the glyph opens the color picker directly (no right-click depth).
        icon.Tapped += (sender, e) => { e.Handled = true; ShowLabelColorPicker((FrameworkElement)sender, label.Id, label.Color); };
        if (ViewModel.LabelTaskCounts.TryGetValue(label.Id, out var count) && count > 0)
            item.InfoBadge = new InfoBadge { Value = count };
        item.ContextFlyout = CreateRecordMenu(label, isProject: false, item);
        return item;
    }

    private MenuFlyout CreateRecordMenu(object record, bool isProject, NavigationViewItem owner)
    {
        var rename = new MenuFlyoutItem { Text = "이름 변경", Tag = record };
        var delete = new MenuFlyoutItem { Text = "삭제", Tag = record };
        rename.Click += isProject ? RenameProject_Click : RenameLabel_Click;
        delete.Click += isProject ? DeleteProject_Click : DeleteLabel_Click;
        var menu = new MenuFlyout();
        menu.Items.Add(rename);
        if (isProject && record is ProjectListItem project)
        {
            var pick = new MenuFlyoutItem { Text = "아이콘 변경" };
            pick.Click += (_, _) => ShowProjectIconPicker(owner, project.Id, project.Icon);
            menu.Items.Add(pick);
        }
        if (!isProject && record is LabelListItem label)
        {
            var pick = new MenuFlyoutItem { Text = "색 변경" };
            pick.Click += (_, _) => ShowLabelColorPicker(owner, label.Id, label.Color);
            menu.Items.Add(pick);
        }
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>A compact 4-column swatch grid in a flyout — icon/color only, no labels.</summary>
    private static Flyout BuildSwatchGridFlyout(int count, int columns, Func<int, FrameworkElement> makeCell)
    {
        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6, Padding = new Thickness(4) };
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = makeCell(i);
            Grid.SetColumn(cell, i % columns);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }
        return new Flyout { Content = grid };
    }

    private void ShowProjectIconPicker(FrameworkElement anchor, Guid projectId, string? currentGlyph)
    {
        var accent = AccentBrush();
        Flyout? flyout = null;
        flyout = BuildSwatchGridFlyout(ProjectIcons.Length, 4, i =>
        {
            var glyph = ProjectIcons[i].Glyph;
            var button = new Button
            {
                Width = 40,
                Height = 36,
                Padding = new Thickness(0),
                Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            };
            // The current icon is emphasized with an accent ring.
            if (string.Equals(glyph, currentGlyph, StringComparison.Ordinal) && accent is not null)
            {
                button.BorderThickness = new Thickness(2);
                button.BorderBrush = accent;
            }
            button.Click += (_, _) => { flyout!.Hide(); PickProjectIcon(projectId, glyph); };
            return button;
        });
        flyout.ShowAt(anchor);
    }

    private void ShowLabelColorPicker(FrameworkElement anchor, Guid labelId, string? currentColor)
    {
        var ring = ThemeBrush("TextFillColorPrimaryBrush");
        Flyout? flyout = null;
        flyout = BuildSwatchGridFlyout(LabelColors.Palette.Count, 4, i =>
        {
            var hex = LabelColors.Palette[i];
            var baseColor = ((Microsoft.UI.Xaml.Media.SolidColorBrush)new HexToBrushConverter()
                .Convert(hex, typeof(Microsoft.UI.Xaml.Media.Brush), null!, null!)).Color;
            var button = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(baseColor),
            };
            // Hover/press only lightens the swatch instead of replacing it with a theme fill (which
            // used to hide the color completely). Keep the color visible through both states.
            button.Resources["ButtonBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Lighten(baseColor, 0.18));
            button.Resources["ButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Lighten(baseColor, 0.30));
            // The current color is emphasized with a high-contrast ring that survives hover/press.
            if (string.Equals(hex, currentColor, StringComparison.OrdinalIgnoreCase) && ring is not null)
            {
                button.BorderThickness = new Thickness(2);
                button.BorderBrush = ring;
                button.Resources["ButtonBorderBrushPointerOver"] = ring;
                button.Resources["ButtonBorderBrushPressed"] = ring;
            }
            button.Click += (_, _) => { flyout!.Hide(); PickLabelColor(labelId, hex); };
            return button;
        });
        flyout.ShowAt(anchor);
    }

    private static Windows.UI.Color Lighten(Windows.UI.Color color, double amount)
    {
        byte Mix(byte channel) => (byte)(channel + (255 - channel) * amount);
        return Windows.UI.Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private async void PickProjectIcon(Guid projectId, string glyph)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.SetProjectIconAsync(projectId, glyph);
            RebuildLiveNavigation();
        });

    private async void PickLabelColor(Guid labelId, string hex)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.SetLabelColorAsync(labelId, hex);
            RebuildLiveNavigation();
        });

    // Common to-do project glyphs (Segoe Fluent). Deliberately avoids the fixed sidebar glyphs
    // (E80F/E8BF/E823/E8FD/E8F1/E73E/E8B7/E8EC) so projects stay visually distinct from them.
    private static readonly (string Glyph, string Name)[] ProjectIcons =
    {
        ("", "장보기"), ("", "업무"), ("", "독서"), ("", "수리"),
        ("", "별"), ("", "가족"), ("", "사람"), ("", "건강"),
        ("", "여행"), ("", "장소"), ("", "설정"), ("", "메일"),
        ("", "고정"), ("", "미디어"), ("", "웹"), ("", "잠금"),
    };

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: ProjectListItem project }) return;
            var name = await PromptNameAsync("그룹 이름 변경", "그룹 이름", project.Name);
            if (name is null) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Project && _currentNavigation.FilterId == project.Id;
            await ViewModel.RenameProjectCommand.ExecuteAsync(new RenameRecordRequest(project.Id, name));
            RebuildLiveNavigation();
            if (isCurrent) Navigate(new TaskListNavigation(TaskListMode.Project, project.Id, name, project.DeadlineDate));
        });
    }

    private async void RenameLabel_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: LabelListItem label }) return;
            var name = await PromptNameAsync("태그 이름 변경", "태그 이름", label.Name);
            if (name is null) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Label && _currentNavigation.FilterId == label.Id;
            await ViewModel.RenameLabelCommand.ExecuteAsync(new RenameRecordRequest(label.Id, name));
            RebuildLiveNavigation();
            if (isCurrent) Navigate(new TaskListNavigation(TaskListMode.Label, label.Id, name));
        });
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: ProjectListItem project }) return;
            if (!await ConfirmDeleteAsync("그룹을 삭제할까요?", "그룹 안의 할 일은 지우지 않고 Cue로 옮깁니다.")) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Project && _currentNavigation.FilterId == project.Id;
            await ViewModel.DeleteProjectCommand.ExecuteAsync(project.Id);
            RebuildLiveNavigation();
            if (isCurrent) NavigateHome();
        });
    }

    private async void DeleteLabel_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: LabelListItem label }) return;
            if (!await ConfirmDeleteAsync("태그를 삭제할까요?", "할 일은 그대로 두고 이 태그만 떼어냅니다.")) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Label && _currentNavigation.FilterId == label.Id;
            await ViewModel.DeleteLabelCommand.ExecuteAsync(label.Id);
            RebuildLiveNavigation();
            if (isCurrent) NavigateHome();
        });
    }

    private void Navigate(TaskListNavigation navigation)
    {
        _currentNavigation = navigation;
        NavFrame.Navigate(typeof(TaskListPage), navigation);
        NavFrame.BackStack.Clear();
    }

    private void NavigateHome()
    {
        _currentNavigation = null;
        if (!ReferenceEquals(NavView.SelectedItem, CueItem))
            NavView.SelectedItem = CueItem;
        else
        {
            NavFrame.Navigate(typeof(TaskListPage), "inbox");
            NavFrame.BackStack.Clear();
        }
    }

    private async Task<string?> PromptNameAsync(string title, string placeholder, string initial = "")
    {
        var input = new TextBox { Text = initial, PlaceholderText = placeholder, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "저장",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await _dialogs.ShowAsync(dialog);
        var name = input.Text.Trim();
        return result == ContentDialogResult.Primary && name.Length > 0 ? name : null;
    }

    private async Task<bool> ConfirmDeleteAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        return await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task RunSafelyAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = "작업을 완료하지 못했습니다",
                Content = exception.Message,
                CloseButtonText = "확인",
            };
            await _dialogs.TryShowAsync(dialog);
        }
    }
}
