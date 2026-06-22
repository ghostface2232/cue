using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Cue.ViewModels;
using Windows.System;

namespace Cue.Pages;

/// <summary>
/// Hosts one index-backed task list: the quick-add line and the list below. The view model is
/// resolved from DI; the navigation parameter selects which index view it reflects.
/// </summary>
public sealed partial class TaskListPage : Page
{
    public TaskListViewModel ViewModel { get; }

    public TaskListPage()
    {
        ViewModel = App.Services.GetRequiredService<TaskListViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
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
    }

    private void QuickAdd_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.AddCommand.CanExecute(null))
            ViewModel.AddCommand.Execute(null);
    }

    private async void AddSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var name = await PromptNameAsync("새 섹션", "섹션 이름");
        if (name is not null)
            await ViewModel.CreateSectionCommand.ExecuteAsync(name);
    }

    private async void RenameSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var group = ViewModel.ProjectGroups.FirstOrDefault(item => item.Id == id);
        if (group is null) return;
        var name = await PromptNameAsync("섹션 이름 변경", "섹션 이름", group.Name);
        if (name is not null)
            await ViewModel.RenameSectionCommand.ExecuteAsync(new RenameRecordRequest(id, name));
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteSectionCommand.ExecuteAsync(id);
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
        var result = await dialog.ShowAsync();
        var name = input.Text.Trim();
        return result == ContentDialogResult.Primary && name.Length > 0 ? name : null;
    }
}
