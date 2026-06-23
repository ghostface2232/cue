using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.ViewModels;
using Cue.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Cue;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>The app-wide service provider. Built once at launch, after the store is opened.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            var store = await IndexedTaskStore.OpenAsync(
                FileTaskStoreOptions.CreateDefault(), TimeProvider.System, TimeZoneInfo.Local);

            Services = ConfigureServices(store);
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
        }
    }

    /// <summary>Registers the services and view models. The store is supplied as a ready instance.</summary>
    private static IServiceProvider ConfigureServices(IndexedTaskStore store)
    {
        var services = new ServiceCollection();

        // Clock + zone that resolve "now"/"today" and pin parsed dates — the same ones the store
        // was opened with, so the index and the parser agree on the day.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TimeZoneInfo.Local);

        // The quick-add parser.
        services.AddSingleton<IDateParser, KoreanDateParser>();

        // One store instance, exposed through both faces it implements: the write side (ITaskStore)
        // and the query side (ITaskIndex).
        services.AddSingleton<ITaskStore>(store);
        services.AddSingleton<ITaskIndex>(store);

        // The rank service owns LexoRank assignment and persists reorders through the store.
        services.AddSingleton<IReorderService, ReorderService>();
        services.AddSingleton<DialogService>();

        // A fresh list view model per navigation.
        services.AddTransient<TaskListViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }

    private void ShowStartupFailure(Exception exception)
    {
        _window = new Window
        {
            Title = "Cue 시작 오류",
            Content = new StackPanel
            {
                Padding = new Thickness(32),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Cue를 시작할 수 없습니다.", FontSize = 24 },
                    new TextBlock
                    {
                        Text = exception.Message,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 640,
                    },
                    new TextBlock { Text = "데이터 폴더 권한과 사용 가능한 디스크 공간을 확인한 뒤 다시 실행해 주세요." },
                },
            },
        };
        _window.Activate();
    }
}
