using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;
using Cue.ViewModels;
using Cue.Services;

namespace Cue;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceKey = "Cue.MainInstance";

    private Window? _window;
    private AppInstance? _mainInstance;
    private DispatcherQueue? _dispatcherQueue;
    private readonly Queue<AppActivationArguments> _pendingActivations = new();
    internal static Window? CurrentWindow { get; private set; }

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
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!mainInstance.IsCurrent)
            {
                // Transfer foreground permission while this process still owns it, then hand the untouched
                // activation payload to the primary instance. No store or window is created in this process.
                WindowForegroundHelper.AllowForProcess(mainInstance.ProcessId);
                await mainInstance.RedirectActivationToAsync(activationArgs);
                Exit();
                return;
            }

            _mainInstance = mainInstance;
            _mainInstance.Activated += OnAppInstanceActivated;
            RouteActivation(activationArgs);

            var preferences = new AppPreferences();
            var timeZone = preferences.ResolveTimeZone();
            var store = await IndexedTaskStore.OpenAsync(
                FileTaskStoreOptions.CreateDefault(), TimeProvider.System, timeZone);

            Services = ConfigureServices(store, preferences, timeZone);
            _window = new MainWindow();
            CurrentWindow = _window;
            AppPreferences.ApplyTheme(_window, preferences);
            // Apply the keyboard-focus preference once the window root exists (default hides the focus
            // rectangle; 자동 / high contrast show it). MainWindow re-applies on theme / high-contrast change.
            AppPreferences.ApplyFocusVisuals(_window, preferences);
            _window.Activate();
            DrainPendingActivations();
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        => RouteActivation(args);

    /// <summary>
    /// Single entry point for initial and redirected activation payloads. Argument interpretation is
    /// intentionally deferred; for now every activation only brings Cue to the foreground.
    /// </summary>
    private void RouteActivation(AppActivationArguments args)
    {
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null)
            return;

        if (dispatcher.HasThreadAccess)
        {
            HandleActivationOnUiThread(args);
            return;
        }

        dispatcher.TryEnqueue(() => HandleActivationOnUiThread(args));
    }

    private void HandleActivationOnUiThread(AppActivationArguments args)
    {
        if (_window is null)
        {
            // A redirected launch can arrive while the primary instance is still opening its store.
            // Preserve the complete payload for the same routing point once the window exists.
            _pendingActivations.Enqueue(args);
            return;
        }

        WindowForegroundHelper.BringToForeground(_window);
    }

    private void DrainPendingActivations()
    {
        while (_pendingActivations.TryDequeue(out var args))
            HandleActivationOnUiThread(args);
    }

    /// <summary>Registers the services and view models. The store is supplied as a ready instance.</summary>
    private static IServiceProvider ConfigureServices(IndexedTaskStore store, AppPreferences preferences, TimeZoneInfo timeZone)
    {
        var services = new ServiceCollection();

        // Clock + zone that resolve "now"/"today" and pin parsed dates — the same ones the store
        // was opened with, so the index and the parser agree on the day.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(timeZone);
        services.AddSingleton(preferences);

        // The slice of preferences the task list reads (the keep-completed-in-place toggle), exposed to the
        // ViewModels layer through its own interface so that layer stays free of the WinUI settings store.
        services.AddSingleton<IListDisplayPreferences>(preferences);

        // The quick-add parser.
        services.AddSingleton<IDateParser, PreferenceDateParser>();

        // One store instance, exposed through both faces it implements: the write side (ITaskStore)
        // and the query side (ITaskIndex).
        services.AddSingleton<ITaskStore>(store);
        services.AddSingleton<ITaskIndex>(store);
        services.AddSingleton<IContainerDeletionStore>(store);

        // The rank service owns LexoRank assignment and persists reorders through the store.
        services.AddSingleton<IReorderService, ReorderService>();

        // Recurrence lifecycle: records a completed/skipped cycle as a RecurrenceOccurrence owned by the
        // series and advances it to its next cycle, ends a series, and edits past cycles. The Ical.Net
        // engine lives behind this service, inside the storage layer.
        services.AddSingleton<IRecurringTaskService, RecurringTaskService>();
        services.AddSingleton<DialogService>();

        // The in-app updater (settings → 정보): checks the latest GitHub release, downloads the
        // installer, and hands off to the silent installer.
        services.AddSingleton<UpdateService>();

        // Cross-panel signal: the sidebar and a detail panel both edit groups/tags but are separate
        // view models, so a change in one reloads the other through this app-scoped notifier.
        services.AddSingleton<INavDataChangeNotifier, NavDataChangeNotifier>();

        // App-scoped registry of every detail view model that still owes work to disk, so a save failure on
        // one page survives navigating to another (and isn't overwritten by a failure there). The window reads
        // it on close and the retry button drives it. Each fresh TaskListViewModel injects this singleton.
        services.AddSingleton<SaveFailureCoordinator>();

        // A fresh list view model per navigation.
        services.AddTransient<TaskListViewModel>();
        services.AddTransient<WeeklyTimelineViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }

    private static readonly Microsoft.UI.Xaml.Media.FontFamily RecoveryFont =
        new("ms-appx:///Assets/Fonts/PretendardJP-Regular.otf#Pretendard JP");

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
                    // This recovery window stands outside the normal page tree, so each label pins Pretendard
                    // directly (family built inline so the failure path never depends on a resource lookup)
                    // rather than falling back to the system font.
                    new TextBlock { Text = "Cue를 시작할 수 없습니다.", FontFamily = RecoveryFont, FontSize = 24 },
                    new TextBlock
                    {
                        Text = exception.Message,
                        FontFamily = RecoveryFont,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 640,
                    },
                    new TextBlock { Text = "데이터 폴더 권한과 사용 가능한 디스크 공간을 확인한 뒤 다시 실행해 주세요.", FontFamily = RecoveryFont },
                },
            },
        };
        _window.Activate();
    }
}
