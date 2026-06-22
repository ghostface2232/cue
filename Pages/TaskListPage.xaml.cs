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
        var mode = Enum.TryParse<TaskListMode>(e.Parameter as string, ignoreCase: true, out var parsed)
            ? parsed
            : TaskListMode.Inbox;
        ViewModel.SetMode(mode);
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
}
