using Microsoft.Extensions.DependencyInjection;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;
using Cue.ViewModels;

namespace Cue.Services;

/// <summary>
/// Builds Cue's storage and service graph without touching a Window or App.xaml resources. Both the
/// normal UI launch and a headless toast completion use this exact bootstrap path.
/// </summary>
internal static class AppRuntimeBootstrapper
{
    public static async Task<AppRuntime> OpenAsync(IToastPresenter toastPresenter)
    {
        var preferences = new AppPreferences();
        var timeZone = preferences.ResolveTimeZone();
        var store = await IndexedTaskStore.OpenAsync(
            FileTaskStoreOptions.CreateDefault(), TimeProvider.System, timeZone);

        var runtime = new AppRuntime(
            store,
            preferences,
            timeZone,
            ConfigureServices(store, preferences, timeZone, toastPresenter));
        await runtime.Services.GetRequiredService<NotificationScheduler>().StartAsync();
        return runtime;
    }

    private static IServiceProvider ConfigureServices(
        IndexedTaskStore store,
        AppPreferences preferences,
        TimeZoneInfo timeZone,
        IToastPresenter toastPresenter)
    {
        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(timeZone);
        services.AddSingleton(preferences);
        services.AddSingleton<IListDisplayPreferences>(preferences);
        services.AddSingleton<IDateParser, PreferenceDateParser>();

        services.AddSingleton<ITaskStore>(store);
        services.AddSingleton<ITaskStoreChangeSource>(store);
        services.AddSingleton<ITaskIndex>(store);
        services.AddSingleton<IContainerDeletionStore>(store);
        services.AddSingleton<IReorderService, ReorderService>();
        services.AddSingleton<IRecurringTaskService, RecurringTaskService>();

        services.AddSingleton<IToastPresenter>(toastPresenter);
        services.AddSingleton<INotificationPreferences>(preferences);
        services.AddSingleton<NotificationScheduler>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<INavDataChangeNotifier, NavDataChangeNotifier>();
        services.AddSingleton<SaveFailureCoordinator>();

        services.AddTransient<TaskListViewModel>();
        services.AddTransient<WeeklyTimelineViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }
}

internal sealed record AppRuntime(
    IndexedTaskStore Store,
    AppPreferences Preferences,
    TimeZoneInfo TimeZone,
    IServiceProvider Services);
