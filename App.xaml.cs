using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.ViewModels;

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
        // Open the store (and rebuild the index from the files) before composing the rest, since the
        // store is created through an async factory and everything else depends on it.
        var store = await IndexedTaskStore.OpenAsync(
            FileTaskStoreOptions.CreateDefault(), TimeProvider.System, TimeZoneInfo.Local);

        Services = ConfigureServices(store);

        _window = new MainWindow();
        _window.Activate();
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

        // A fresh list view model per navigation.
        services.AddTransient<TaskListViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }
}
