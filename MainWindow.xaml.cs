using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Cue.Pages;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.ViewModels;
using Cue.Services;
using Windows.System;
using System.Runtime.InteropServices;

namespace Cue;

public sealed partial class MainWindow : Window
{
    // First-run window size: deliberately compact (narrow and short) rather than the oversized
    // Windows App SDK default. Once the user resizes, their placement is restored on next launch.
    private const int DefaultWindowWidth = 900;
    private const int DefaultWindowHeight = 640;

    // Floor below which the window can't be shrunk. Sized for the single-pane (overlay) layout so Cue
    // stays usable as a narrow column. DIPs; scaled to physical pixels per the window's DPI.
    private const int MinWindowWidth = 480;
    private const int MinWindowHeight = 540;

    private TaskListNavigation? _currentNavigation;
    // The nav item shown just before the Settings page was opened, so its back button can return there.
    private NavigationViewItem? _backTargetItem;
    private readonly DialogService _dialogs;
    private readonly INavDataChangeNotifier _navNotifier;
    private readonly AppPreferences _preferences;
    // App-scoped registry of every detail view model still owing work to disk. Drives the global save-error
    // bar (so a failure on one page isn't overwritten by one on another) and is what the close flow and the
    // retry button consult, rather than a single last-failed view model.
    private readonly SaveFailureCoordinator _saveFailures;
    // Kept alive for the lifetime of the window so its HighContrastChanged event keeps firing (a collected
    // AccessibilitySettings stops raising). Null when the platform refused to create it.
    private Windows.UI.ViewManagement.AccessibilitySettings? _accessibility;
    private readonly HashSet<NavigationViewItem> _insetNavItems = new();
    // Rows nested under the Groups/Tags sections — they get a deeper left gutter than top-level rows.
    private readonly HashSet<NavigationViewItem> _nestedNavItems = new();
    // The framework's NavigationViewItemExpandChevronMargin (0,0,-14,0) that pulls the centered chevron
    // glyph flush to the row's right edge; we keep this as the base and layer CueNavChevronRight on top.
    private const double ChevronFlushRight = -14;

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
        _preferences = App.Services.GetRequiredService<AppPreferences>();
        _saveFailures = App.Services.GetRequiredService<SaveFailureCoordinator>();
        // The aggregate failure state drives the global error bar. Changed can fire on a background save/retry
        // continuation, so marshal to the UI thread before touching the InfoBar.
        _saveFailures.Changed += (_, _) => DispatcherQueue.TryEnqueue(SyncGlobalErrorBar);
        InitializeComponent();
        // A group/tag created/edited in a detail panel reloads the sidebar lists at once.
        _navNotifier.Changed += OnNavDataChanged;
        // A task add/complete/delete/move changes the open-task counts but not the group/tag set, so the
        // badges are re-stamped in place rather than rebuilding the lists.
        _navNotifier.CountsChanged += OnNavCountsChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // Resolve the icon next to the executable rather than via a relative path: a relative path is
        // resolved against the process working directory, which isn't guaranteed to be the install folder
        // when launched from a shortcut — leaving the taskbar with no icon. AppContext.BaseDirectory is
        // always the app's own folder.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        NormalizeSidebarItems();

        RestoreOrSetDefaultPlacement();
        ApplyMinimumWindowSize();
        Closed += OnWindowClosed;
        AppWindow.Closing += OnWindowClosing;

        // The system caption buttons (minimize/maximize/close) are drawn by AppWindow, not XAML, so
        // their glyph color must be set per theme — otherwise they stay dark in dark mode. Re-apply
        // whenever the app theme changes; the focus-visual colors (auto mode follows light/dark, and high
        // contrast forces them visible) are re-resolved on the same signal.
        if (Content is FrameworkElement root)
            root.ActualThemeChanged += (_, _) =>
            {
                ApplyCaptionButtonColors();
                AppPreferences.ApplyFocusVisuals(this, _preferences);
                RefreshTagNavIconColors();
            };
        ApplyCaptionButtonColors();

        // Re-resolve focus visuals when high contrast is toggled mid-session, so the "always show focus in
        // high contrast" rule holds without a restart. Guarded — AccessibilitySettings/its event can throw
        // on some desktop configs; focus is still correct on the next theme change or relaunch if so.
        try
        {
            _accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
            _accessibility.HighContrastChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(() => AppPreferences.ApplyFocusVisuals(this, _preferences));
        }
        catch { /* live high-contrast updates unavailable on this config */ }

        NavView.Loaded += NavView_Loaded;
        NavView.DisplayModeChanged += (_, _) => UpdateNavRowInsets();
        NavView.Expanding += NavView_Expanding;
        GroupsSection.RegisterPropertyChangedCallback(NavigationViewItem.IsExpandedProperty, OnSectionIsExpandedChanged);
        TagsSection.RegisterPropertyChangedCallback(NavigationViewItem.IsExpandedProperty, OnSectionIsExpandedChanged);
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Pins a minimum window size so the responsive layout never has to cope with an unusably
    /// tiny window. Sized for the single-pane layout; converted from DIPs to physical pixels per DPI.</summary>
    private void ApplyMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        presenter.PreferredMinimumWidth = (int)Math.Round(MinWindowWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(MinWindowHeight * scale);
    }

    /// <summary>Restores the last saved window placement, or centers a compact default on first run.</summary>
    private void RestoreOrSetDefaultPlacement()
    {
        var saved = _preferences.WindowPlacement;
        if (saved is not null)
        {
            // Clamp against the display the window was last on, so a removed monitor or a changed
            // resolution can't strand the window off-screen.
            var work = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(saved.X, saved.Y), DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.MoveAndResize(ClampToWorkArea(saved, work));
            if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
            return;
        }

        var primary = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(DefaultWindowWidth, primary.Width);
        var height = Math.Min(DefaultWindowHeight, primary.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            primary.X + ((primary.Width - width) / 2),
            primary.Y + ((primary.Height - height) / 2),
            width,
            height));
    }

    private static Windows.Graphics.RectInt32 ClampToWorkArea(WindowPlacement p, Windows.Graphics.RectInt32 work)
    {
        var width = Math.Clamp(p.Width, 480, work.Width);
        var height = Math.Clamp(p.Height, 360, work.Height);
        var x = Math.Clamp(p.X, work.X, work.X + work.Width - width);
        var y = Math.Clamp(p.Y, work.Y, work.Y + work.Height - height);
        return new Windows.Graphics.RectInt32(x, y, width, height);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // AppWindow reports the maximized bounds while maximized; persisting them alongside the flag
        // means the next launch reopens maximized and un-maximizes to a sensible size.
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _preferences.WindowPlacement = new WindowPlacement
        {
            X = pos.X,
            Y = pos.Y,
            Width = size.Width,
            Height = size.Height,
            Maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
        };
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
        DispatcherQueue.TryEnqueue(UpdateNavRowInsets);
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
        // These are bookkeeping toggles, not user intent — suppress the expand/collapse animation around each
        // so the section just appears open on first launch rather than animating (or triggering the exit path).
        _suppressSectionAnim = true;
        section.IsExpanded = false;
        _suppressSectionAnim = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            _suppressSectionAnim = true;
            section.IsExpanded = true;
            _suppressSectionAnim = false;
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

        // Remember the last non-Settings selection so the Settings page's back button can return to it.
        if (item.Tag is not string s || s != "settings")
            _backTargetItem = item;

        if (item.Tag is string settings && settings == "settings")
        {
            _currentNavigation = null;
            NavFrame.Navigate(typeof(SettingsPage));
            NavFrame.BackStack.Clear();
            return;
        }

        // The weekly timeline is its own page, not an index-backed list, so it bypasses the generic
        // TaskListPage navigation below (mirrors the settings branch).
        if (item.Tag is string weekly && weekly == "weekly")
        {
            _currentNavigation = null;
            NavFrame.Navigate(typeof(WeeklyTimelinePage));
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

    /// <summary>Reflects a task change (add, complete/reopen, delete, group/tag move) in the sidebar count
    /// badges. Only the numbers shift — the group/tag set is unchanged — so this recomputes the counts and
    /// updates each row's badge in place, leaving the realized items (and so selection, expansion, and
    /// scroll) untouched. Cheaper and less disruptive than the full list rebuild a structural change does.</summary>
    private async void OnNavCountsChanged(object? sender, EventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            await ViewModel.RefreshTaskCountsAsync();
            UpdateCountBadges();
        });
    }

    /// <summary>Re-stamps every group/tag row's open-task count badge from the freshly refreshed counts,
    /// matching each realized nav item to its count by the navigation tag it carries.</summary>
    private void UpdateCountBadges()
    {
        foreach (var item in GroupsSection.MenuItems) UpdateCountBadge(item);
        foreach (var item in TagsSection.MenuItems) UpdateCountBadge(item);
    }

    private void UpdateCountBadge(object obj)
    {
        if (obj is not NavigationViewItem item || item.Tag is not TaskListNavigation nav)
            return;
        var count = nav.Mode switch
        {
            TaskListMode.TaskGroup when nav.FilterId is { } id =>
                ViewModel.TaskGroupTaskCounts.TryGetValue(id, out var c) ? c : 0,
            TaskListMode.Tag when nav.FilterId is { } id =>
                ViewModel.TagTaskCounts.TryGetValue(id, out var c) ? c : 0,
            TaskListMode.NoTaskGroup => ViewModel.NoTaskGroupTaskCount,
            TaskListMode.NoTag => ViewModel.NoTagTaskCount,
            _ => 0,
        };
        // Reuse the existing badge when present (just retarget its value) so the count can change without
        // re-creating the control; drop it entirely once the row's open count falls to zero.
        if (count > 0)
        {
            if (item.InfoBadge is { } badge) badge.Value = count;
            else item.InfoBadge = CreateCountBadge(count);
        }
        else
        {
            item.InfoBadge = null;
        }
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
        CancelInlineRename();
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
            // Enter commits, which removes this editor from the tree and so fires LostFocus a second time
            // — after the _inlineCommitting guard has already reset. Bail unless this is still the live
            // editor, so a committed/cancelled item can't be created twice (which produced a duplicate
            // tag with the next palette color).
            if (!ReferenceEquals(_inlineCreateItem, item)) return;
            if (string.IsNullOrWhiteSpace(box.Text)) CancelInlineCreate();
            else await CommitInlineCreateAsync(box.Text);
        };

        // Insert at the top so the inline editor sits where the created group/tag will land (PrependRank),
        // instead of appearing at the bottom and jumping to the top on commit.
        section.MenuItems.Insert(0, item);
        NormalizeSidebarItem(item, nested: true);
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
        // A rebuild replaces the section items wholesale, so any open inline editor is gone.
        _inlineCreateItem = null;
        _inlineRenameItem = null;
        _inlineRenameOriginalContent = null;
        // The 그룹 없음 / 태그 없음 catch-all rows always sit at the very bottom of their section, below the
        // real (user-reorderable) groups/tags, so they read as the section's trailing "everything else" bucket.
        GroupsSection.MenuItems.Clear();
        foreach (var taskGroup in ViewModel.TaskGroups)
            GroupsSection.MenuItems.Add(CreateTaskGroupItem(taskGroup));
        GroupsSection.MenuItems.Add(CreateUnfiledItem(
            "그룹 없음", TaskListMode.NoTaskGroup, ViewModel.NoTaskGroupTaskCount));

        TagsSection.MenuItems.Clear();
        foreach (var tag in ViewModel.Tags)
            TagsSection.MenuItems.Add(CreateTagItem(tag));
        TagsSection.MenuItems.Add(CreateUnfiledItem(
            "태그 없음", TaskListMode.NoTag, ViewModel.NoTagTaskCount));
        NormalizeSidebarItems();
        UpdateNavRowInsets();
    }

    private void NormalizeSidebarItems()
    {
        foreach (var item in NavView.MenuItems)
            NormalizeSidebarObject(item);
        foreach (var item in NavView.FooterMenuItems)
            NormalizeSidebarObject(item);
    }

    private void NormalizeSidebarObject(object item, bool nested = false)
    {
        if (item is not NavigationViewItem navItem)
            return;

        NormalizeSidebarItem(navItem, nested);
        // Direct children of a section (Groups/Tags) are the nested group/tag rows.
        foreach (var child in navItem.MenuItems)
            NormalizeSidebarObject(child, nested: true);
    }

    private void NormalizeSidebarItem(NavigationViewItem item, bool nested)
    {
        item.HorizontalAlignment = HorizontalAlignment.Stretch;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (nested) _nestedNavItems.Add(item);
        else _nestedNavItems.Remove(item);
        ApplyNavRowInset(item);
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
            // Use a faded real group/tag glyph rather than a funnel: marks the catch-all bucket as
            // belonging to the section without the full visual weight of an actual group/tag.
            Icon = new FontIcon
            {
                Glyph = mode == TaskListMode.NoTaskGroup ? "" : "",
                Opacity = NavD("CueNavUnfiledIconOpacity", 0.4),
            },
        };
        NormalizeSidebarItem(item, nested: true);
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
        ("weekly", "", "주간 타임라인"),
    };

    private NavigationViewItem NavItemFor(string key) => key switch
    {
        "today" => TodayItem,
        "upcoming" => UpcomingItem,
        "anytime" => AnytimeItem,
        "logbook" => LogbookItem,
        "priority" => PriorityItem,
        "weekly" => WeeklyItem,
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
        NormalizeSidebarItem(item, nested: true);
        // Tapping the glyph opens the icon picker directly (no right-click depth); Handled stops the
        // tap from also navigating into the group.
        icon.Tapped += (sender, e) => { e.Handled = true; ShowTaskGroupIconPicker((FrameworkElement)sender, taskGroup.Id, taskGroup.Icon); };
        if (ViewModel.TaskGroupTaskCounts.TryGetValue(taskGroup.Id, out var count) && count > 0)
            item.InfoBadge = CreateCountBadge(count);
        item.ContextFlyout = CreateRecordMenu(taskGroup, isGroup: true, item);
        AddDeleteAccelerator(item, anchor => DeleteTaskGroupFlowAsync(taskGroup, anchor));
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
        NormalizeSidebarItem(item, nested: true);
        // Tapping the glyph opens the color picker directly (no right-click depth).
        icon.Tapped += (sender, e) => { e.Handled = true; ShowTagColorPicker((FrameworkElement)sender, tag.Id, tag.Color); };
        if (ViewModel.TagTaskCounts.TryGetValue(tag.Id, out var count) && count > 0)
            item.InfoBadge = CreateCountBadge(count);
        item.ContextFlyout = CreateRecordMenu(tag, isGroup: false, item);
        AddDeleteAccelerator(item, anchor => DeleteTagFlowAsync(tag, anchor));
        return item;
    }

    /// <summary>Re-resolves the sidebar tag icons' colors against the current theme. Their foreground is set
    /// once in <see cref="CreateTagItem"/> via <see cref="HexToBrushConverter"/>, which darkens a bright tag
    /// color for the Light theme; a runtime theme toggle rebuilds nothing here, so without this the glyphs
    /// keep the previous theme's color until the next data-driven nav rebuild. Re-runs the same converter
    /// per realized tag item, looking its color up from the live tag list by id.</summary>
    private void RefreshTagNavIconColors()
    {
        foreach (var menuItem in TagsSection.MenuItems)
        {
            if (menuItem is NavigationViewItem
                {
                    Icon: FontIcon icon,
                    Tag: TaskListNavigation { Mode: TaskListMode.Tag, FilterId: { } tagId },
                }
                && ViewModel.Tags.FirstOrDefault(t => t.Id == tagId) is { } tag
                && new HexToBrushConverter().Convert(
                    tag.Color ?? string.Empty, typeof(Microsoft.UI.Xaml.Media.Brush), null!, null!)
                    is Microsoft.UI.Xaml.Media.Brush brush)
            {
                icon.Foreground = brush;
            }
        }
    }

    /// <summary>Open-task count badge for a nav item, using the centered-digit style (see
    /// CueCountInfoBadgeStyle — fixes Pretendard's digit sitting high in the fixed-height badge).</summary>
    private static InfoBadge CreateCountBadge(int count)
    {
        var badge = new InfoBadge { Value = count };
        if (ThemeStyle("CueCountInfoBadgeStyle") is { } style)
            badge.Style = style;
        // Inset from the row's right edge so the count doesn't hug it.
        badge.Margin = new Thickness(0, 0, NavD("CueNavBadgeRight", 8), 0);
        return badge;
    }

    private static Style? ThemeStyle(string key)
    {
        try { return (Style)Application.Current.Resources[key]; }
        catch { return null; }
    }

    /// <summary>Positions a sidebar row's hover/selection pill, icon, and label independently. Resource
    /// overrides don't reach the NavigationView presenter's built-in right margin and nesting indent, so
    /// this sets the relevant template parts directly once the row is realized. Idempotent, so it's safe
    /// on container reuse.</summary>
    private void ApplyNavRowInset(NavigationViewItem item)
    {
        if (!_insetNavItems.Add(item))
            return;

        item.Loaded += NavRow_Loaded;
        if (item.IsLoaded)
            ApplyNavRowInsetForCurrentPane(item);
    }

    private void NavRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item) return;
        ApplyNavRowInsetForCurrentPane(item);
    }

    // Stock NavigationView gives no reveal motion to an expanding section's own rows — the only thing that
    // visibly animates is whatever sits *below* the section sliding down. That leaves the last section (태그),
    // which has nothing beneath it, looking instant. So we drive our own per-row fade+slide on both expand
    // and collapse, applied to both sections, so a section animates the same way regardless of its position.
    // Set true while we drive IsExpanded ourselves so those programmatic toggles don't re-enter the handlers.
    private bool _suppressSectionAnim;

    private void NavView_Expanding(NavigationView sender, NavigationViewItemExpandingEventArgs args)
    {
        if (_suppressSectionAnim) return;
        if (args.ExpandingItem is not NavigationViewItem section) return;
        if (section != GroupsSection && section != TagsSection) return;

        // Pin every row to its pre-reveal state now, before the framework paints the expanded section, so
        // there's no full-opacity flash between the section opening and the animation starting.
        foreach (var row in section.MenuItems.OfType<NavigationViewItem>())
        {
            ElementCompositionPreview.SetIsTranslationEnabled(row, true);
            var visual = ElementCompositionPreview.GetElementVisual(row);
            visual.Opacity = 0f;
            visual.Properties.InsertVector3("Translation", new Vector3(0f, -8f, 0f));
        }
        // The rows lay out as the section expands; animate them in on the next tick once they're realized.
        DispatcherQueue.TryEnqueue(() => AnimateSectionRows(section, reveal: true));
    }

    // NavigationView has no "collapsing" (pre-collapse) event — only Collapsed, which fires after the rows
    // are already gone. So we watch IsExpanded ourselves: when the user collapses a section, revert it open
    // so the rows stay on screen, play the exit animation, then collapse for real once it finishes.
    private void OnSectionIsExpandedChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_suppressSectionAnim) return;
        if (sender is not NavigationViewItem section) return;
        if (section.IsExpanded) return; // the expand path is driven by NavView_Expanding

        _suppressSectionAnim = true;    // holds through the revert's Expanding and the final collapse
        section.IsExpanded = true;
        AnimateSectionRows(section, reveal: false, onComplete: () =>
        {
            section.IsExpanded = false;
            _suppressSectionAnim = false;
        });
    }

    // Rooted reference for the in-flight collapse timer (see AnimateSectionRows) so it survives until Tick.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _collapseTimer;

    private const int RowAnimDurationMs = 200;
    private const int RowAnimStaggerMs = 22;
    // Fire the real collapse this much *before* the last row's fade finishes, so the host folding away
    // overlaps the tail of the fade instead of starting after a dead beat (which read as a stutter).
    private const int CollapseLeadMs = 70;

    // A staggered fade + 8px slide, run on the composition thread. Reveal cascades top-to-bottom from the
    // pinned pre-reveal state (opacity 0, nudged up); collapse folds bottom-to-top back to it. onComplete
    // (collapse only) fires on the UI thread slightly before the last row finishes — see CollapseLeadMs.
    private void AnimateSectionRows(NavigationViewItem section, bool reveal, Action? onComplete = null)
    {
        var rows = section.MenuItems.OfType<NavigationViewItem>().ToList();
        if (rows.Count == 0)
        {
            onComplete?.Invoke();
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(rows[0]).Compositor;
        for (var i = 0; i < rows.Count; i++)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(rows[i], true);
            var visual = ElementCompositionPreview.GetElementVisual(rows[i]);
            var order = reveal ? i : rows.Count - 1 - i;
            var delay = TimeSpan.FromMilliseconds(order * RowAnimStaggerMs);

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.InsertKeyFrame(1f, reveal ? Vector3.Zero : new Vector3(0f, -8f, 0f));
            slide.Duration = TimeSpan.FromMilliseconds(RowAnimDurationMs);
            slide.DelayTime = delay;

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(1f, reveal ? 1f : 0f);
            fade.Duration = TimeSpan.FromMilliseconds(RowAnimDurationMs);
            fade.DelayTime = delay;

            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
        }

        if (onComplete == null) return;
        // Total run = last row's stagger delay + its duration. Trip the collapse a touch before that.
        var totalMs = (rows.Count - 1) * RowAnimStaggerMs + RowAnimDurationMs;
        var leadMs = Math.Max(RowAnimDurationMs / 2, totalMs - CollapseLeadMs);
        // Held in a field, not a local: a DispatcherQueueTimer that nothing references is eligible for GC
        // before it ticks, which would leave IsExpanded stuck open (the section never actually collapses).
        _collapseTimer = DispatcherQueue.CreateTimer();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(leadMs);
        _collapseTimer.IsRepeating = false;
        _collapseTimer.Tick += (t, _) =>
        {
            t.Stop();
            _collapseTimer = null;
            onComplete();
        };
        _collapseTimer.Start();
    }

    private void UpdateNavRowInsets()
    {
        // The group/tag divider only earns its keep in the expanded pane; in the compact rail it would
        // sit between bare icons and read as noise, so collapse it there.
        if (GroupTagSeparator is { } separator)
            separator.Visibility = NavView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _insetNavItems)
        {
            if (item.IsLoaded)
                ApplyNavRowInsetForCurrentPane(item);
        }
    }

    private void ApplyNavRowInsetForCurrentPane(DependencyObject root)
    {
        var compact = !NavView.IsPaneOpen;
        // LayoutRoot = the pill (hover/selection background + the accent indicator);
        // ContentGrid = the icon + label block (carries the nesting indent we override);
        // ContentPresenter = the label.
        // Nested group/tag rows get a deeper left gutter than top-level rows so the hierarchy reads.
        var nested = root is NavigationViewItem nvi && _nestedNavItems.Contains(nvi);
        if (FindDescendantByName(root, "LayoutRoot") is { } pill)
            pill.Margin = new Thickness(
                NavD(nested ? "CueNavChildPillLeft" : "CueNavPillLeft", 4), 2, NavD("CueNavPillRight", 5), 2);
        // Keep the accent bar + content (icon, highlight) flush to the pill's content edge in BOTH pane
        // states. The collapsed (compact) geometry is the reference: its zero left inset is what the open
        // pane now matches, so the icon / highlight / accent bar sit the same distance from the window's
        // left edge and don't shift when the pane toggles. Only the trailing spacing (right gutter, label
        // gap) differs by pane state. PresenterContentRootGrid holds the accent bar + content and the
        // NavigationView would otherwise indent it on its own — pin it to 0 so it can't drift.
        // Pin the content root to a zero left inset in both pane states so the icon, highlight, and accent
        // bar sit at the rail's natural position and don't drift when the pane toggles. The title bar's
        // pane-toggle glyph is aligned to THIS natural position from its own side (its button margin), so
        // the nav rows are left untouched here.
        if (FindDescendantByName(root, "PresenterContentRootGrid") is { } inner)
            inner.Margin = new Thickness(0);
        if (FindDescendantByName(root, "ContentGrid") is { } content)
            content.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 4, 0);
        if (FindDescendantByName(root, "ContentPresenter") is { } label)
            label.Margin = compact ? new Thickness(0) : new Thickness(NavD("CueNavLabelLeft", 4), -1, 4, -1);
        // The expand-collapse chevron is a 40px-wide grid with a 12px glyph centered in it; the framework
        // hides the resulting right-hand slack with a -14 right margin (NavigationViewItemExpandChevronMargin)
        // so the glyph sits flush to the row's edge. Keep that -14 base and add our inset on top, so
        // CueNavChevronRight reads directly as the glyph's gap from the right edge. Only sections with
        // children have this part, so other rows no-op.
        if (FindDescendantByName(root, "ExpandCollapseChevron") is { } chevron)
            chevron.Margin = new Thickness(0, 0, ChevronFlushRight + NavD("CueNavChevronRight", 8), 0);
    }

    private static double NavD(string key, double fallback)
    {
        try { return (double)Application.Current.Resources[key]; }
        catch { return fallback; }
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return fe;
            if (FindDescendantByName(child, name) is { } match)
                return match;
        }
        return null;
    }

    private MenuFlyout CreateRecordMenu(object record, bool isGroup, NavigationViewItem owner)
    {
        var rename = new MenuFlyoutItem { Text = "이름 변경", Tag = record };
        var delete = new MenuFlyoutItem { Text = "삭제", Tag = record };
        // Rename edits the name inline in the row (like creating a group/tag), not in a modal dialog.
        rename.Click += (_, _) => BeginInlineRename(owner, isGroup, record);
        // Delete confirms in a popover anchored to the owning row — same flow the Delete key triggers.
        delete.Click += (_, _) =>
        {
            if (isGroup && record is TaskGroupListItem taskGroupRecord)
                _ = DeleteTaskGroupFlowAsync(taskGroupRecord, owner);
            else if (!isGroup && record is TagListItem tagRecord)
                _ = DeleteTagFlowAsync(tagRecord, owner);
        };
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
            // Hover/press only lightens the swatch instead of replacing it with a theme fill that
            // would hide the color completely. Keep the color visible through both states.
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

    // Inline rename: swaps the row's label for a focused TextBox (the same affordance creating a group/tag
    // uses), so renaming never opens a modal. Enter or blur-with-text commits, Escape or blur-empty cancels.
    private NavigationViewItem? _inlineRenameItem;
    private object? _inlineRenameOriginalContent;
    private bool _inlineRenameIsGroup;
    private Guid _inlineRenameId;
    private bool _inlineRenameCommitting;

    private void BeginInlineRename(NavigationViewItem item, bool isGroup, object record)
    {
        CancelInlineCreate();   // only one inline editor at a time
        CancelInlineRename();
        var (id, name) = record switch
        {
            TaskGroupListItem taskGroup => (taskGroup.Id, taskGroup.Name),
            TagListItem tag => (tag.Id, tag.Name),
            _ => (Guid.Empty, string.Empty),
        };
        if (id == Guid.Empty) return;

        _inlineRenameItem = item;
        _inlineRenameOriginalContent = item.Content;
        _inlineRenameIsGroup = isGroup;
        _inlineRenameId = id;

        var box = new TextBox
        {
            Text = name,
            Margin = new Thickness(0, 2, 4, 2),
        };
        box.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; await CommitInlineRenameAsync(box.Text); }
            else if (args.Key == VirtualKey.Escape) { args.Handled = true; CancelInlineRename(); }
        };
        box.LostFocus += async (_, _) =>
        {
            // Enter commits, which rebuilds the row and fires a second LostFocus — bail unless this is still
            // the live editor (mirrors the inline-create guard) so a rename can't fire twice.
            if (!ReferenceEquals(_inlineRenameItem, item)) return;
            if (string.IsNullOrWhiteSpace(box.Text)) CancelInlineRename();
            else await CommitInlineRenameAsync(box.Text);
        };

        item.Content = box;
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
    }

    private void CancelInlineRename()
    {
        if (_inlineRenameItem is null) return;
        _inlineRenameItem.Content = _inlineRenameOriginalContent;
        _inlineRenameItem = null;
        _inlineRenameOriginalContent = null;
    }

    private async Task CommitInlineRenameAsync(string text)
    {
        if (_inlineRenameCommitting) return;
        _inlineRenameCommitting = true;
        try
        {
            var item = _inlineRenameItem;
            if (item is null) return;
            var isGroup = _inlineRenameIsGroup;
            var id = _inlineRenameId;
            var name = text.Trim();
            // Clear the live-editor reference first so the LostFocus that Enter triggers bails out.
            _inlineRenameItem = null;
            if (name.Length == 0)
            {
                item.Content = _inlineRenameOriginalContent;
                _inlineRenameOriginalContent = null;
                return;
            }
            _inlineRenameOriginalContent = null;
            await RunSafelyAsync(async () =>
            {
                var mode = isGroup ? TaskListMode.TaskGroup : TaskListMode.Tag;
                var isCurrent = _currentNavigation?.Mode == mode && _currentNavigation.FilterId == id;
                await MutateNavAsync(() => isGroup
                    ? ViewModel.RenameTaskGroupCommand.ExecuteAsync(new RenameRecordRequest(id, name))
                    : ViewModel.RenameTagCommand.ExecuteAsync(new RenameRecordRequest(id, name)));
                RebuildLiveNavigation();
                if (isCurrent) Navigate(new TaskListNavigation(mode, id, name));
            });
        }
        finally { _inlineRenameCommitting = false; }
    }

    /// <summary>Wires the Delete key on a sidebar group/tag row to its delete flow, anchored to that
    /// row — the same flow the right-click 삭제 item runs. Fire-and-forget; the flow is guarded by
    /// RunSafelyAsync.</summary>
    private static void AddDeleteAccelerator(NavigationViewItem item, Func<FrameworkElement, Task> deleteFlow)
    {
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.Delete };
        accelerator.Invoked += (accel, args) =>
        {
            // A KeyboardAccelerator with no ScopeOwner is window-global: it would fire wherever focus sits
            // (e.g. in the detail panel with no text box focused), popping this group's delete confirm out
            // of nowhere. Delete only deletes the row that is itself focused — matching how a focused task
            // row handles Delete. A focused descendant (the inline rename box) leaves the item Unfocused, so
            // Delete there still edits text. When unfocused, leave args.Handled false so others can respond.
            if (item.FocusState == FocusState.Unfocused) return;
            args.Handled = true;
            _ = deleteFlow(item);
        };
        item.KeyboardAccelerators.Add(accelerator);
    }

    private Task DeleteTaskGroupFlowAsync(TaskGroupListItem taskGroup, FrameworkElement anchor)
        => RunSafelyAsync(async () =>
        {
            var mode = await AskTaskGroupDeletionAsync(taskGroup.Name, anchor);
            if (mode is null) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.TaskGroup && _currentNavigation.FilterId == taskGroup.Id;
            await MutateNavAsync(() => ViewModel.DeleteTaskGroupAsync(taskGroup.Id, mode.Value));
            RebuildLiveNavigation();
            if (isCurrent) NavigateHome();
        });

    /// <summary>Deleting a group asks what to do with its tasks in an anchored popover: keep them (move
    /// to the Cue home — the least-destructive default) or delete them along with the group. Null = the
    /// user cancelled.</summary>
    private static async Task<TaskGroupDeletionMode?> AskTaskGroupDeletionAsync(string name, FrameworkElement anchor)
    {
        var choice = await ConfirmPopover.ShowChoiceAsync(anchor, new ChoicePopoverOptions
        {
            Message = $"'{name}' 그룹을 삭제할까요?",
            Actions =
            {
                new ChoicePopoverAction("그룹만 제거"),
                new ChoicePopoverAction("할 일까지 삭제", Destructive: true),
            },
            // Focus/Enter defaults to the least-destructive 그룹만 제거.
            DefaultActionIndex = 0,
        });
        return choice switch
        {
            0 => TaskGroupDeletionMode.Reparent,
            1 => TaskGroupDeletionMode.DeleteTasks,
            _ => null,
        };
    }

    private Task DeleteTagFlowAsync(TagListItem tag, FrameworkElement anchor)
        => RunSafelyAsync(async () =>
        {
            if (!await ConfirmPopover.ShowAsync(anchor, new ConfirmPopoverOptions
            {
                Message = "이 태그를 삭제할까요?",
            })) return;
            var isCurrent = _currentNavigation?.Mode == TaskListMode.Tag && _currentNavigation.FilterId == tag.Id;
            await MutateNavAsync(() => ViewModel.DeleteTagCommand.ExecuteAsync(tag.Id));
            RebuildLiveNavigation();
            if (isCurrent) NavigateHome();
        });

    private void Navigate(TaskListNavigation navigation)
    {
        _currentNavigation = navigation;
        NavFrame.Navigate(typeof(TaskListPage), navigation);
        NavFrame.BackStack.Clear();
    }

    /// <summary>Returns from the Settings page to the view that was showing before it was opened (the
    /// Settings page has its own back-arrow button; navigation here is flat, so there is no Frame back
    /// stack to walk). Re-selecting the remembered nav item drives the normal selection flow.</summary>
    public void NavigateBackFromSettings()
    {
        var target = _backTargetItem ?? CueItem;
        if (ReferenceEquals(NavView.SelectedItem, target))
            return;
        NavView.SelectedItem = target;
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

    private bool _allowClose = false;
    private bool _closeFlushInProgress = false;
    // True once the close flow has surfaced the error bar with the 지금 종료 escape hatch, so a later
    // coordinator update (a background retry, a fresh failure) re-renders the on-close variant rather than
    // downgrading to the normal bar. Cleared when the bar is dismissed.
    private bool _offerForceExit;

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        args.Cancel = true;

        if (_closeFlushInProgress) return;
        _closeFlushInProgress = true;

        bool hasOpenDetail = false;
        Task flushTask = Task.CompletedTask;

        if (NavFrame.Content is TaskListPage taskListPage && taskListPage.DetailViewModel is { } detail && detail.IsOpen)
        {
            hasOpenDetail = true;
            taskListPage.CommitFocusedTextBox();
            flushTask = taskListPage.FlushDetailAsync();
        }

        if (!hasOpenDetail)
        {
            // No panel to flush. Exit only when nothing is still owed to disk anywhere; a failure recorded on
            // any page — even one since closed or navigated away from — keeps the app open with the error bar
            // (다시 시도 / 저장하지 않고 종료) so its unsaved work isn't silently dropped.
            if (_saveFailures.HasFailures)
            {
                _closeFlushInProgress = false;
                ShowGlobalErrorOnClose();
                return;
            }
            _allowClose = true;
            _closeFlushInProgress = false;
            Close();
            return;
        }

        var delayTimer = DispatcherQueue.CreateTimer();
        delayTimer.Interval = TimeSpan.FromMilliseconds(400);
        delayTimer.IsRepeating = false;
        delayTimer.Tick += (s, e) =>
        {
            GlobalSavingInfoBar.IsOpen = true;
        };
        delayTimer.Start();

        try
        {
            await flushTask;

            delayTimer.Stop();
            GlobalSavingInfoBar.IsOpen = false;

            // The open panel flushed cleanly — but only exit when nothing is owed to disk across all pages. A
            // failure on this panel, or carried over from one elsewhere, still blocks the exit. The view model
            // records every settled failure into the coordinator before this flush completes, so it is the
            // single source of truth here.
            if (_saveFailures.HasFailures)
            {
                _closeFlushInProgress = false;
                ShowGlobalErrorOnClose();
                return;
            }

            _allowClose = true;
            _closeFlushInProgress = false;
            Close();
        }
        catch (Exception)
        {
            delayTimer.Stop();
            GlobalSavingInfoBar.IsOpen = false;
            _closeFlushInProgress = false;

            // The flush faulted, so the view model just recorded the failure into the coordinator. Surface it
            // rather than exiting; only quit if (defensively) nothing is actually owed.
            if (_saveFailures.HasFailures)
            {
                ShowGlobalErrorOnClose();
            }
            else
            {
                _allowClose = true;
                Close();
            }
        }
    }

    /// <summary>Re-renders the global save-error bar from the coordinator's aggregate state: shown (with the
    /// 지금 종료 escape hatch while a close is pending) when any page owes work to disk, hidden once every
    /// failure has cleared. Driven by the coordinator's Changed event.</summary>
    private void SyncGlobalErrorBar()
    {
        if (_saveFailures.HasFailures)
        {
            if (_offerForceExit) ShowGlobalErrorOnClose();
            else ShowGlobalErrorNormal();
        }
        else
        {
            HideGlobalError();
        }
    }

    private string UnsavedFailureMessage()
    {
        var count = _saveFailures.UnsavedTaskCount;
        return count > 0 ? $"저장하지 못한 할 일이 {count}개 있습니다" : "저장하지 못한 할 일이 있습니다";
    }

    private void ShowGlobalErrorNormal()
    {
        GlobalErrorInfoBar.Message = UnsavedFailureMessage();
        ForceExitButton.Visibility = Visibility.Collapsed;
        GlobalErrorInfoBar.IsOpen = true;
    }

    private void ShowGlobalErrorOnClose()
    {
        _offerForceExit = true;
        GlobalErrorInfoBar.Message = UnsavedFailureMessage();
        ForceExitButton.Visibility = Visibility.Visible;
        GlobalErrorInfoBar.IsOpen = true;
    }

    private void HideGlobalError()
    {
        _offerForceExit = false;
        GlobalErrorInfoBar.IsOpen = false;
    }

    /// <summary>The user dismissed the error bar by its close button. The work is still owed (the coordinator
    /// keeps the failures and re-surfaces the bar on the next change), but any pending close attempt is
    /// abandoned, so the 지금 종료 escape hatch shouldn't carry over to a later, unrelated failure.</summary>
    private void GlobalErrorInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        _offerForceExit = false;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _offscreenSnackbarTimer;

    /// <summary>Shows the bottom snackbar telling the user a just-created task is pinned to another day and
    /// so isn't on the current screen, with a jump to 모든 할 일. Auto-dismisses after a few seconds; a
    /// repeat add restarts the timer so the latest message stays up for its full duration.</summary>
    public void ShowOffscreenTaskSnackbar()
    {
        OffscreenTaskInfoBar.IsOpen = true;

        _offscreenSnackbarTimer ??= DispatcherQueue.CreateTimer();
        _offscreenSnackbarTimer.Stop();
        _offscreenSnackbarTimer.Interval = TimeSpan.FromSeconds(6);
        _offscreenSnackbarTimer.IsRepeating = false;
        _offscreenSnackbarTimer.Tick -= OffscreenSnackbarTimer_Tick;
        _offscreenSnackbarTimer.Tick += OffscreenSnackbarTimer_Tick;
        _offscreenSnackbarTimer.Start();
    }

    private void OffscreenSnackbarTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        OffscreenTaskInfoBar.IsOpen = false;
    }

    private void OffscreenGoAllTasks_Click(object sender, RoutedEventArgs e)
    {
        _offscreenSnackbarTimer?.Stop();
        OffscreenTaskInfoBar.IsOpen = false;
        NavigateHome();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalErrorInfoBar.IsOpen = false;
        // Retry every page's outstanding saves together, not just the last one to fail. A retry mounted during
        // the close flow (the 지금 종료 button is showing) closes the app once everything lands.
        var closing = ForceExitButton.Visibility == Visibility.Visible;
        GlobalSavingInfoBar.IsOpen = true;
        try
        {
            await _saveFailures.RetryAllAsync();
            GlobalSavingInfoBar.IsOpen = false;
            if (closing)
            {
                _allowClose = true;
                Close();
            }
            // Outside the close flow, the coordinator's Changed event hides the bar once the queue is empty.
        }
        catch (Exception)
        {
            GlobalSavingInfoBar.IsOpen = false;
            // Some saves still failed — re-surface the bar with the latest count, keeping the close escape
            // hatch if a close was pending.
            if (closing) ShowGlobalErrorOnClose();
            else ShowGlobalErrorNormal();
        }
    }

    private void ForceExitButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalErrorInfoBar.IsOpen = false;
        _allowClose = true;
        Close();
    }
}
