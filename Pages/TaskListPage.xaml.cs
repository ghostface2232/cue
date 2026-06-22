using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Cue.ViewModels;
using Cue.Services;
using Windows.System;

namespace Cue.Pages;

/// <summary>
/// Hosts one index-backed task list: the quick-add line and the list below. The view model is
/// resolved from DI; the navigation parameter selects which index view it reflects.
/// </summary>
public sealed partial class TaskListPage : Page
{
    public TaskListViewModel ViewModel { get; }
    private readonly DialogService _dialogs;

    public TaskListPage()
    {
        ViewModel = App.Services.GetRequiredService<TaskListViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            base.OnNavigatedTo(e);
            var navigation = e.Parameter as TaskListNavigation;
            if (navigation is null)
            {
                var mode = Enum.TryParse<TaskListMode>(e.Parameter as string, ignoreCase: true, out var parsed)
                    ? parsed
                    : TaskListMode.Inbox;
                navigation = new TaskListNavigation(mode);
            }
            ViewModel.SetNavigation(navigation);
            await ViewModel.LoadCommand.ExecuteAsync(null);
        });
    }

    private async void QuickAdd_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.AddCommand.CanExecute(null))
            await RunSafelyAsync(() => ViewModel.AddCommand.ExecuteAsync(null));
    }

    private async void TaskRow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await RunSafelyAsync(() => ViewModel.SelectTaskCommand.ExecuteAsync(id));
    }

    private void CloseDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ViewModel.Detail.Close();

    private async void SaveDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.SaveCommand.ExecuteAsync(null));

    private async void AddSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.AddSubtaskCommand.ExecuteAsync(null));

    private async void OpenSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await RunSafelyAsync(() => ViewModel.Detail.OpenSubtaskCommand.ExecuteAsync(id));
    }

    private async void DeleteSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "서브태스크를 삭제할까요?",
            Content = "파일은 지우지 않고 삭제 시각이 기록됩니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        await RunSafelyAsync(async () =>
        {
            if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
                await ViewModel.Detail.DeleteSubtaskCommand.ExecuteAsync(id);
        });
    }

    private async void AddSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            var name = await PromptNameAsync("새 섹션", "섹션 이름");
            if (name is not null) await ViewModel.CreateSectionCommand.ExecuteAsync(name);
        });
    }

    private async void RenameSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not Button { Tag: Guid id }) return;
            var group = ViewModel.ProjectGroups.FirstOrDefault(item => item.Id == id);
            if (group is null) return;
            var name = await PromptNameAsync("섹션 이름 변경", "섹션 이름", group.Name);
            if (name is not null) await ViewModel.RenameSectionCommand.ExecuteAsync(new RenameRecordRequest(id, name));
        });
    }

    private async void DeleteSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "섹션을 삭제할까요?",
            Content = "섹션의 작업은 삭제하지 않고 Cue Inbox로 이동합니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        await RunSafelyAsync(async () =>
        {
            if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
                await ViewModel.DeleteSectionCommand.ExecuteAsync(id);
        });
    }

    private async Task<string?> PromptNameAsync(string title, string placeholder, string initial = "")
    {
        var input = new TextBox { Text = initial, PlaceholderText = placeholder, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
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

    private async Task RunSafelyAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
            await _dialogs.TryShowAsync(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "작업을 완료하지 못했습니다",
                Content = exception.Message,
                CloseButtonText = "확인",
            });
        }
    }
}
