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
            var name = await PromptNameAsync(isProject ? "새 프로젝트" : "새 라벨", isProject ? "프로젝트 이름" : "라벨 이름");
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
        ProjectsGroup.MenuItems.Add(new NavigationViewItem { Content = "+ 새 프로젝트", Tag = "create-project" });
        foreach (var project in ViewModel.Projects)
            ProjectsGroup.MenuItems.Add(CreateProjectItem(project));

        LabelsGroup.MenuItems.Clear();
        LabelsGroup.MenuItems.Add(new NavigationViewItem { Content = "+ 새 라벨", Tag = "create-label" });
        foreach (var label in ViewModel.Labels)
            LabelsGroup.MenuItems.Add(CreateLabelItem(label));
    }

    private NavigationViewItem CreateProjectItem(ProjectListItem project)
    {
        var item = new NavigationViewItem
        {
            Content = project.Name,
            Tag = new TaskListNavigation(TaskListMode.Project, project.Id, project.Name, project.DeadlineDate),
            Icon = new FontIcon { Glyph = "\uE8B7" },
        };
        if (ViewModel.ProjectTaskCounts.TryGetValue(project.Id, out var count) && count > 0)
            item.InfoBadge = new InfoBadge { Value = count };
        item.ContextFlyout = CreateRecordMenu(project, isProject: true);
        return item;
    }

    private NavigationViewItem CreateLabelItem(LabelListItem label)
    {
        var item = new NavigationViewItem
        {
            Content = label.Name,
            Tag = new TaskListNavigation(TaskListMode.Label, label.Id, label.Name),
            Icon = new FontIcon { Glyph = "\uE8EC" },
        };
        if (ViewModel.LabelTaskCounts.TryGetValue(label.Id, out var count) && count > 0)
            item.InfoBadge = new InfoBadge { Value = count };
        item.ContextFlyout = CreateRecordMenu(label, isProject: false);
        return item;
    }

    private MenuFlyout CreateRecordMenu(object record, bool isProject)
    {
        var rename = new MenuFlyoutItem { Text = "이름 변경", Tag = record };
        var delete = new MenuFlyoutItem { Text = "삭제", Tag = record };
        rename.Click += isProject ? RenameProject_Click : RenameLabel_Click;
        delete.Click += isProject ? DeleteProject_Click : DeleteLabel_Click;
        var menu = new MenuFlyout();
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not MenuFlyoutItem { Tag: ProjectListItem project }) return;
            var name = await PromptNameAsync("프로젝트 이름 변경", "프로젝트 이름", project.Name);
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
            var name = await PromptNameAsync("라벨 이름 변경", "라벨 이름", label.Name);
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
            if (!await ConfirmDeleteAsync("프로젝트를 삭제할까요?", "프로젝트의 작업은 삭제하지 않고 Cue Inbox로 이동합니다.")) return;
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
            if (!await ConfirmDeleteAsync("라벨을 삭제할까요?", "작업은 유지되고 이 라벨 참조만 제거됩니다.")) return;
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
