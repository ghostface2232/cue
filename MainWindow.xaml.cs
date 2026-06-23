using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cue.Pages;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.ViewModels;
using Cue.Services;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Cue;

public sealed partial class MainWindow : Window
{
    private TaskListNavigation? _currentNavigation;
    private readonly DialogService _dialogs;
    private readonly INavDataChangeNotifier _navNotifier;

    // While the sidebar makes its own group/tag edit it refreshes the pane directly, so it ignores the
    // notification it triggers (the detail panels still react to it). A detail-panel edit leaves this
    // false, so the sidebar reloads in response.
    private bool _suppressNavReload;

    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        _navNotifier = App.Services.GetRequiredService<INavDataChangeNotifier>();
        InitializeComponent();
        // A group/tag created/edited in a detail panel reloads the sidebar lists at once.
        _navNotifier.Changed += OnNavDataChanged;

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
            // WinUI realizes an expanded NavigationViewItem's children only once. Because the sections
            // start expanded but are populated here (after that first realization), the freshly added
            // group/tag rows don't appear until the user toggles the section. Force one re-expand now so
            // they show on first launch. Later rebuilds hit an already-realized section and are fine.
            RealizeExpandedSection(GroupsSection);
            RealizeExpandedSection(TagsSection);
        });
    }

    /// <summary>Collapses then re-expands a section (on the next dispatcher tick) so its children, added
    /// after the section's first realization, actually render. No-op when the section is collapsed.</summary>
    private void RealizeExpandedSection(NavigationViewItem section)
    {
        if (!section.IsExpanded) return;
        section.IsExpanded = false;
        DispatcherQueue.TryEnqueue(() => section.IsExpanded = true);
    }

    private async void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        await RunSafelyAsync(() => HandleSelectionChangedAsync(args));
    }

    private async Task HandleSelectionChangedAsync(NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        if (item.Tag is string fixedView && fixedView == "timeline")
        {
            _currentNavigation = null;
            NavFrame.Navigate(typeof(TimelinePage));
            NavFrame.BackStack.Clear();
            return;
        }

        if (item.Tag is string settings && settings == "settings")
        {
            _currentNavigation = null;
            NavFrame.Navigate(typeof(SettingsPage));
            NavFrame.BackStack.Clear();
            return;
        }

        if (item.Tag is not (string or TaskListNavigation)) return;
        _currentNavigation = item.Tag as TaskListNavigation;
        NavFrame.Navigate(typeof(TaskListPage), item.Tag);
        NavFrame.BackStack.Clear(); // flat navigation — no back history between lists
    }

    /// <summary>Reloads the sidebar lists when a group/tag changes from outside the pane (a detail
    /// panel). The pane's own edits set <see cref="_suppressNavReload"/> since they refresh directly.</summary>
    private async void OnNavDataChanged(object? sender, EventArgs e)
    {
        if (_suppressNavReload) return;
        await RunSafelyAsync(async () =>
        {
            await ViewModel.LoadAsync();
            RebuildLiveNavigation();
        });
    }

    /// <summary>Runs one of the pane's own group/tag mutations with the self-notification suppressed, so
    /// the sidebar refreshes once (via the explicit rebuild that follows) rather than twice. The detail
    /// panels still receive the notification and reload.</summary>
    private async Task MutateNavAsync(Func<Task> mutate)
    {
        _suppressNavReload = true;
        try { await mutate(); }
        finally { _suppressNavReload = false; }
    }

    // The + buttons on the 그룹 / 태그 section headers open an inline name field at the foot of the
    // section (no modal); Enter or blur-with-text creates, Escape or blur-empty cancels.
    private void AddGroup_Click(object sender, RoutedEventArgs e) => BeginInlineCreate(isGroup: true);
    private void AddTag_Click(object sender, RoutedEventArgs e) => BeginInlineCreate(isGroup: false);

    private NavigationViewItem? _inlineCreateItem;
    private bool _inlineCreateIsGroup;
    private bool _inlineCommitting;

    private void BeginInlineCreate(bool isGroup)
    {
        CancelInlineCreate(); // only one inline editor at a time
        var section = isGroup ? GroupsSection : TagsSection;

        var box = new TextBox
        {
            PlaceholderText = isGroup ? "새 그룹 이름" : "새 태그 이름",
            Margin = new Thickness(0, 2, 4, 2),
        };
        var item = new NavigationViewItem
        {
            SelectsOnInvoked = false,
            Content = box,
            Icon = new FontIcon { Glyph = isGroup ? "" : "" },
        };
        _inlineCreateItem = item;
        _inlineCreateIsGroup = isGroup;

        box.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; await CommitInlineCreateAsync(box.Text); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; CancelInlineCreate(); }
        };
        box.LostFocus += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) CancelInlineCreate();
            else await CommitInlineCreateAsync(box.Text);
        };

        section.MenuItems.Add(item);
        section.IsExpanded = true;
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
    }

    private void CancelInlineCreate()
    {
        if (_inlineCreateItem is null) return;
        var section = _inlineCreateIsGroup ? GroupsSection : TagsSection;
        section.MenuItems.Remove(_inlineCreateItem);
        _inlineCreateItem = null;
    }

    // Guarded so the LostFocus that fires when Enter moves focus can't double-create the record.
    private async Task CommitInlineCreateAsync(string text)
    {
        if (_inlineCommitting) return;
        _inlineCommitting = true;
        try
        {
            var isGroup = _inlineCreateIsGroup;
            var name = text.Trim();
            CancelInlineCreate();
            if (name.Length == 0) return;
            await RunSafelyAsync(async () =>
            {
                await MutateNavAsync(() => isGroup
                    ? ViewModel.CreateTaskGroupCommand.ExecuteAsync(name)
                    : ViewModel.CreateTagCommand.ExecuteAsync(name));
                RebuildLiveNavigation();
            });
        }
        finally { _inlineCommitting = false; }
    }

    private void RebuildLiveNavigation()
    {
        // A rebuild replaces the section items wholesale, so any open inline-create editor is gone.
        _inlineCreateItem = null;
        GroupsSection.MenuItems.Clear();
        GroupsSection.MenuItems.Add(CreateUnfiledItem(
            "그룹 없음", TaskListMode.NoTaskGroup, ViewModel.NoTaskGroupTaskCount));
        foreach (var taskGroup in ViewModel.TaskGroups)
            GroupsSection.MenuItems.Add(CreateTaskGroupItem(taskGroup));

        TagsSection.MenuItems.Clear();
        TagsSection.MenuItems.Add(CreateUnfiledItem(
            "태그 없음", TaskListMode.NoTag, ViewModel.NoTagTaskCount));
        foreach (var tag in ViewModel.Tags)
            TagsSection.MenuItems.Add(CreateTagItem(tag));
    }

    /// <summary>The 그룹 없음 / 태그 없음 collection points: a fixed entry per section that re-gathers
    /// unfiled captures, so quick-add → sort-later survives the home list spanning every group. A funnel
    /// glyph marks them as filtered views rather than real groups/tags.</summary>
    private NavigationViewItem CreateUnfiledItem(string title, TaskListMode mode, int openCount)
    {
        var item = new NavigationViewItem
        {
            Content = title,
            Tag = new TaskListNavigation(mode, null, title),
            Icon = new FontIcon { Glyph = "" },
        };
        ApplyNavRowInset(item);
        if (openCount > 0)
            item.InfoBadge = CreateCountBadge(openCount);
        return item;
    }

    // The fixed lists the user can show/hide from the sidebar context menu. "모든 할 일" (All) is
    // omitted — it is the home list and is always present.
    private static readonly (string Key, string Glyph, string Name)[] ToggleableNav =
    {
        ("today", "", "오늘 할 일"),
        ("upcoming", "", "앞으로 할 일"),
        ("anytime", "", "언젠가 할 일"),
        ("logbook", "", "완료한 일"),
        ("priority", "", "중요도"),
        ("timeline", "\uE9D2", "타임라인"),
    };

    private NavigationViewItem NavItemFor(string key) => key switch
    {
        "today" => TodayItem,
        "upcoming" => UpcomingItem,
        "timeline" => TimelineItem,
        "anytime" => AnytimeItem,
        "logbook" => LogbookItem,
        "priority" => PriorityItem,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    // Lists that start hidden until the user opts to show them from the sidebar context menu.
    private static readonly HashSet<string> DefaultHiddenNav = new() { "upcoming", "anytime" };

    private static bool NavIsVisible(string key)
        => NavPreferences.IsVisible(key, !DefaultHiddenNav.Contains(key));

    private void ApplyNavVisibility()
    {
        foreach (var (key, _, _) in ToggleableNav)
            NavItemFor(key).Visibility = NavIsVisible(key) ? Visibility.Visible : Visibility.Collapsed;
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
            Visibility = NavIsVisible(key) ? Visibility.Visible : Visibility.Collapsed,
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
            var visible = !NavIsVisible(key);
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

    private NavigationViewItem CreateTaskGroupItem(TaskGroupListItem taskGroup)
    {
        var icon = new FontIcon { Glyph = string.IsNullOrEmpty(taskGroup.Icon) ? "\uE8B7" : taskGroup.Icon };
        var item = new NavigationViewItem
        {
            Content = taskGroup.Name,
            Tag = new TaskListNavigation(TaskListMode.TaskGroup, taskGroup.Id, taskGroup.Name),
            Icon = icon,
        };
        // Tapping the glyph opens the icon picker directly (no right-click depth); Handled stops the
        // tap from also navigating into the group.
        icon.Tapped += (sender, e) => { e.Handled = true; ShowTaskGroupIconPicker((FrameworkElement)sender, taskGroup.Id, taskGroup.Icon); };
        ApplyNavRowInset(item);
        if (ViewModel.TaskGroupTaskCounts.TryGetValue(taskGroup.Id, out var count) && count > 0)
            item.InfoBadge = CreateCountBadge(count);
        item.ContextFlyout = CreateRecordMenu(taskGroup, isGroup: true, item);
        return item;
    }

    private NavigationViewItem CreateTagItem(TagListItem tag)
    {
        var icon = new FontIcon { Glyph = "\uE8EC" };
        if (new HexToBrushConverter().Convert(tag.Color ?? string.Empty, typeof(Microsoft.UI.Xaml.Media.Brush), null!, null!) is Microsoft.UI.Xaml.Media.Brush brush)
            icon.Foreground = brush;
        var item = new NavigationViewItem
        {
            Content = tag.Name,
            Tag = new TaskListNavigation(TaskListMode.Tag, tag.Id, tag.Name),
            Icon = icon,
        };
        // Tapping the glyph opens the color picker directly (no right-click depth).
        icon.Tapped += (sender, e) => { e.Handled = true; ShowTagColorPicker((FrameworkElement)sender, tag.Id, tag.Color); };
        ApplyNavRowInset(item);
        if (ViewModel.TagTaskCounts.TryGetValue(tag.Id, out var count) && count > 0)
            item.InfoBadge = CreateCountBadge(count);
        item.ContextFlyout = CreateRecordMenu(tag, isGroup: false, item);
        return item;
    }

    /// <summary>Open-task count badge for a nav item, using the centered-digit style (see
    /// CueCountInfoBadgeStyle — fixes Pretendard's digit sitting high in the fixed-height badge).</summary>
    private static InfoBadge CreateCountBadge(int count)
    {
        var badge = new InfoBadge { Value = count };
        if (ThemeStyle("CueCountInfoBadgeStyle") is { } style)
            badge.Style = style;
        return badge;
    }

    private static Style? ThemeStyle(string key)
    {
        try { return (Style)Application.Current.Resources[key]; }
        catch { return null; }
    }

    /// <summary>Insets a group/tag row's hover/selection pill (and its icon+text, kept inside the pill)
    /// to the right — like the main list's subtasks — by overriding the pill's margin
    /// (NavigationViewItemButtonMargin, the LayoutRoot Margin) on this item only via its Resources.</summary>
    private static void ApplyNavRowInset(NavigationViewItem item)
    {
        try { item.Resources["NavigationViewItemButtonMargin"] = (Thickness)Application.Current.Resources["CueNavRowPillMargin"]; }
        catch { /* fall back to the stock pill margin */ }
    }

    private MenuFlyout CreateRecordMenu(object record, bool isGroup, NavigationViewItem owner)
    {
        var rename = new MenuFlyoutItem { Text = "이름 변경", Tag = record };
        var delete = new MenuFlyoutItem { Text = "삭제", Tag = record };
        rename.Click += isGroup ? RenameTaskGroup_Click : RenameTag_Click;
        delete.Click += isGroup ? DeleteTaskGroup_Click : DeleteTag_Click;
        var menu = new MenuFlyout();
        menu.Items.Add(rename);
        if (isGroup && record is TaskGroupListItem taskGroup)
        {
            var pick = new MenuFlyoutItem { Text = "아이콘 변경" };
            pick.Click += (_, _) => ShowTaskGroupIconPicker(owner, taskGroup.Id, taskGroup.Icon);
            menu.Items.Add(pick);
        }
        if (!isGroup && record is TagListItem tag)
        {
            var pick = new MenuFlyoutItem { Text = "색 변경" };
            pick.Click += (_, _) => ShowTagColorPicker(owner, tag.Id, tag.Color);
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

    private void ShowTaskGroupIconPicker(FrameworkElement anchor, Guid taskGroupId, string? currentGlyph)
    {
        var accent = AccentBrush();
        Flyout? flyout = null;
        flyout = BuildSwatchGridFlyout(TaskGroupIcons.Length, 4, i =>
        {
            var glyph = TaskGroupIcons[i].Glyph;
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
            button.Click += (_, _) => { flyout!.Hide(); PickTaskGroupIcon(taskGroupId, glyph); };
            return button;
        });
        flyout.ShowAt(anchor);
    }

    private void ShowTagColorPicker(FrameworkElement anchor, Guid tagId, string? currentColor)
    {
        var ring = ThemeBrush("TextFillColorPrimaryBrush");
        Flyout? flyout = null;
        flyout = BuildSwatchGridFlyout(TagColors.Palette.Count, 4, i =>
        {
            var hex = TagColors.Palette[i];
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
            button.Click += (_, _) => { flyout!.Hide(); PickTagColor(tagId, hex); };
            return button;
        });
        flyout.ShowAt(anchor);
    }

    private static Windows.UI.Color Lighten(Windows.UI.Color color, double amount)
    {
        byte Mix(byte channel) => (byte)(channel + (255 - channel) * amount);
        return Windows.UI.Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private async void PickTaskGroupIcon(Guid taskGroupId, string glyph)
        => await RunSafelyAsync(async () =>
        {
            await MutateNavAsync(() => ViewModel.SetTaskGroupIconAsync(taskGroupId, glyph));
            RebuildLiveNavigation();
        });

    private async void PickTagColor(Guid tagId, string hex)
        => await RunSafelyAsync(async () =>
        {
            await MutateNavAsync(() => ViewModel.SetTagColorAsync(tagId, hex));
            RebuildLiveNavigation();
        });

    // Common to-do group glyphs (Segoe Fluent). Deliberately avoids the fixed sidebar glyphs
    // (E80F/E8BF/E823/E8FD/E8F1/E73E/E8B7/E8EC) so groups stay visually distinct from them.
    private static readonly (string Glyph, string Name)[] TaskGroupIcons =
    {
        ("", "장보기"), ("", "업무"), ("", "독서"), ("", "수리"),
        ("", "별"), ("", "가족"), ("", "사람"), ("", "건강"),
        ("", "여행"), ("", "장소"), ("", "설정"), ("", "메일"),
        ("", "고정"), ("", "미디어"), ("", "웹"), ("", "잠금"),
    };

    private async void RenameTaskGroup_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: TaskGroupListItem taskGroup }) return;
            var name = await PromptNameAsync("그룹 이름 변경", "그룹 이름", taskGroup.Name);
            if (name is null) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.TaskGroup && _currentNavigation.FilterId == taskGroup.Id;
            await MutateNavAsync(() => ViewModel.RenameTaskGroupCommand.ExecuteAsync(new RenameRecordRequest(taskGroup.Id, name)));
            RebuildLiveNavigation();
            if (isCurrent) Navigate(new TaskListNavigation(TaskListMode.TaskGroup, taskGroup.Id, name));
        });
    }

    private async void RenameTag_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: TagListItem tag }) return;
            var name = await PromptNameAsync("태그 이름 변경", "태그 이름", tag.Name);
            if (name is null) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Tag && _currentNavigation.FilterId == tag.Id;
            await MutateNavAsync(() => ViewModel.RenameTagCommand.ExecuteAsync(new RenameRecordRequest(tag.Id, name)));
            RebuildLiveNavigation();
            if (isCurrent) Navigate(new TaskListNavigation(TaskListMode.Tag, tag.Id, name));
        });
    }

    private async void DeleteTaskGroup_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: TaskGroupListItem taskGroup }) return;
            var mode = await AskTaskGroupDeletionAsync(taskGroup.Name);
            if (mode is null) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.TaskGroup && _currentNavigation.FilterId == taskGroup.Id;
            await MutateNavAsync(() => ViewModel.DeleteTaskGroupAsync(taskGroup.Id, mode.Value));
            RebuildLiveNavigation();
            if (isCurrent) NavigateHome();
        });
    }

    /// <summary>Deleting a group asks what to do with its tasks: move them to the Cue home (the
    /// least-destructive default) or delete them along with the group. Null = the user cancelled.</summary>
    private async Task<TaskGroupDeletionMode?> AskTaskGroupDeletionAsync(string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = $"'{name}' 그룹을 삭제할까요?",
            Content = "그룹 안의 할 일을 어떻게 할지 선택하세요. '그룹만 제거'하면 할 일은 그대로 남아 '모든 할 일'에서 볼 수 있습니다.",
            PrimaryButtonText = "그룹만 제거",
            SecondaryButtonText = "할 일까지 삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await _dialogs.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => TaskGroupDeletionMode.Reparent,
            ContentDialogResult.Secondary => TaskGroupDeletionMode.DeleteTasks,
            _ => null,
        };
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: TagListItem tag }) return;
            if (!await ConfirmDeleteAsync("태그를 삭제할까요?", "할 일은 그대로 두고 이 태그만 떼어냅니다.")) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Tag && _currentNavigation.FilterId == tag.Id;
            await MutateNavAsync(() => ViewModel.DeleteTagCommand.ExecuteAsync(tag.Id));
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
            NavFrame.Navigate(typeof(TaskListPage), "alltasks");
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
