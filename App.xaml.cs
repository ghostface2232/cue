using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Cue.Domain;
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

    private readonly IToastPresenter _toastPresenter;
    private readonly IToastActivationSource _toastActivations;
    private readonly bool _wasToastActivated;
    private readonly TaskCompletionSource _launchReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _windowGate = new(1, 1);

    private Window? _window;
    private AppInstance? _mainInstance;
    private AppInstance? _redirectTarget;
    private AppActivationArguments? _initialLifecycleArguments;
    private DispatcherQueue? _dispatcherQueue;
    private AppRuntime? _runtime;
    private bool _xamlResourcesInitialized;
    internal static Window? CurrentWindow { get; private set; }

    /// <summary>The app-wide service provider. Built once at launch, after the store is opened.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App() : this(new ToolkitToastPresenter())
    {
    }

    internal App(ToolkitToastPresenter toastServices)
        : this(toastServices, toastServices)
    {
    }

    internal App(IToastPresenter toastPresenter, IToastActivationSource toastActivations)
    {
        _toastPresenter = toastPresenter;
        _toastActivations = toastActivations;
        _wasToastActivated = toastActivations.WasCurrentProcessToastActivated;
        _toastActivations.Activated += OnToastActivated;

        // A complete action deliberately skips App.xaml resource initialization. Open/normal launches
        // initialize lazily on the UI thread before creating a window.
        if (!_wasToastActivated)
            EnsureXamlResourcesInitialized();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!mainInstance.IsCurrent && !_wasToastActivated)
            {
                WindowForegroundHelper.AllowForProcess(mainInstance.ProcessId);
                await mainInstance.RedirectActivationToAsync(activationArgs);
                Exit();
                return;
            }

            if (mainInstance.IsCurrent)
            {
                _mainInstance = mainInstance;
                _mainInstance.Activated += OnAppInstanceActivated;
            }
            else
            {
                // Toolkit normally delivers to the already-running COM server. Keep this fallback for a
                // startup race where Windows launched a second toast process before that server registered.
                _redirectTarget = mainInstance;
                _initialLifecycleArguments = activationArgs;
            }

            _launchReady.TrySetResult();
            if (!_wasToastActivated)
                RouteActivation(AppActivationRequest.FromLifecycle(activationArgs));
        }
        catch (Exception exception)
        {
            _launchReady.TrySetResult();
            if (_wasToastActivated)
                return;
            ShowStartupFailure(exception);
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        => RouteActivation(AppActivationRequest.FromLifecycle(args));

    private void OnToastActivated(object? sender, ToastActivationRequest args)
    {
        if (ToastActivationPayload.TryParse(args.Arguments, out var payload))
            RouteActivation(AppActivationRequest.FromToast(payload!));
        else
            RouteActivation(AppActivationRequest.FromToast(
                new ToastActivationPayload(ToastAction.Open, Guid.Empty, null)));
    }

    /// <summary>
    /// Single entry point for initial, redirected, and toast activation payloads. Open requests marshal
    /// to the UI; complete requests stay headless and use the shared recurrence-aware completion service.
    /// </summary>
    private void RouteActivation(AppActivationRequest request)
        => _ = RouteActivationAsync(request);

    private async Task RouteActivationAsync(AppActivationRequest request)
    {
        try
        {
            await _launchReady.Task.ConfigureAwait(false);

            if (request.Toast is { Action: ToastAction.Complete } complete)
            {
                // Desktop ignores activationType=background as a separate background task: the EXE is
                // activated anyway. Keep this branch headless by touching only the storage/service graph.
                await CompleteFromToastAsync(complete).ConfigureAwait(false);
                if (_wasToastActivated && _window is null)
                    await ExitApplicationAsync().ConfigureAwait(false);
                return;
            }

            if (_redirectTarget is not null && _initialLifecycleArguments is not null)
            {
                WindowForegroundHelper.AllowForProcess(_redirectTarget.ProcessId);
                await _redirectTarget.RedirectActivationToAsync(_initialLifecycleArguments);
                await ExitApplicationAsync().ConfigureAwait(false);
                return;
            }

            await RunOnUiThreadAsync(async () =>
            {
                await EnsureMainWindowAsync();
                WindowForegroundHelper.BringToForeground(_window!);

                if (request.Toast is { TaskId: var taskId })
                {
                    // TODO(notification navigation): select taskId once shell-level task navigation has a
                    // stable public entry point. The activation payload is preserved here until then.
                    _ = taskId;
                }
            });
        }
        catch (Exception exception)
        {
            if (_wasToastActivated && request.Toast is { Action: ToastAction.Complete })
            {
                await ExitApplicationAsync().ConfigureAwait(false);
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                ShowStartupFailure(exception);
                return Task.CompletedTask;
            });
        }
    }

    private async Task CompleteFromToastAsync(ToastActivationPayload payload)
    {
        var runtime = await EnsureRuntimeAsync().ConfigureAwait(false);
        var task = await runtime.Store.GetAsync<TaskItem>(payload.TaskId).ConfigureAwait(false);
        if (task is null || task.IsDeleted || task.IsCompleted)
            return;

        if (payload.OccurrenceId is { } expectedOccurrenceId)
        {
            if (task.Recurrence is null)
                return;

            var occurrenceUtc = task.When.Date?.Utc ?? task.Recurrence.Anchor.Utc;
            if (RecurrenceOccurrenceId.From(task.Id, occurrenceUtc) != expectedOccurrenceId)
                return; // stale action for a cycle that has already advanced
        }

        var completion = runtime.Services.GetRequiredService<IRecurringTaskService>();
        var clock = runtime.Services.GetRequiredService<TimeProvider>();
        await completion.CompleteAsync(payload.TaskId, clock.GetUtcNow()).ConfigureAwait(false);
        var notifier = runtime.Services.GetRequiredService<INavDataChangeNotifier>();
        if (_window is null)
        {
            notifier.NotifyCountsChanged();
        }
        else
        {
            await RunOnUiThreadAsync(() =>
            {
                notifier.NotifyCountsChanged();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    private async Task<AppRuntime> EnsureRuntimeAsync()
    {
        if (_runtime is not null)
            return _runtime;

        await _runtimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runtime is null)
            {
                _runtime = await AppRuntimeBootstrapper.OpenAsync(_toastPresenter).ConfigureAwait(false);
                Services = _runtime.Services;
            }
            return _runtime;
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task EnsureMainWindowAsync()
    {
        if (_window is not null)
            return;

        await _windowGate.WaitAsync();
        try
        {
            if (_window is not null)
                return;

            EnsureXamlResourcesInitialized();
            var runtime = await EnsureRuntimeAsync();
            _window = new MainWindow();
            CurrentWindow = _window;
            AppPreferences.ApplyTheme(_window, runtime.Preferences);
            AppPreferences.ApplyFocusVisuals(_window, runtime.Preferences);
            _window.Activate();
        }
        finally
        {
            _windowGate.Release();
        }
    }

    private void EnsureXamlResourcesInitialized()
    {
        if (_xamlResourcesInitialized)
            return;
        InitializeComponent();
        _xamlResourcesInitialized = true;
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null)
            return Task.FromException(new InvalidOperationException("The app dispatcher is not initialized."));
        if (dispatcher.HasThreadAccess)
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The app dispatcher is shutting down."));
        }
        return completion.Task;
    }

    private Task ExitApplicationAsync()
        => RunOnUiThreadAsync(() =>
        {
            Exit();
            return Task.CompletedTask;
        });

    private sealed record AppActivationRequest(
        AppActivationArguments? Lifecycle,
        ToastActivationPayload? Toast)
    {
        public static AppActivationRequest FromLifecycle(AppActivationArguments arguments)
            => new(arguments, null);

        public static AppActivationRequest FromToast(ToastActivationPayload payload)
            => new(null, payload);
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
        CurrentWindow = _window;
        _window.Activate();
    }
}
