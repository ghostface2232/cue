namespace Cue.Services;

/// <summary>
/// App-layer boundary for Windows toast delivery. No Toolkit or WinRT notification type crosses this
/// interface, keeping the maintenance-mode package replaceable and out of Domain, Storage, and ViewModels.
/// </summary>
public interface IToastPresenter
{
    void Schedule(
        DateTimeOffset deliveryTime,
        string tag,
        string title,
        string body,
        string completionArguments);

    void CancelScheduled(string tag);

    IReadOnlyList<string> GetScheduledTags();

    void Show(string title, string body, string? activationArguments = null);
}

/// <summary>Toolkit-independent source of toast activation payloads.</summary>
internal interface IToastActivationSource
{
    bool WasCurrentProcessToastActivated { get; }

    event EventHandler<ToastActivationRequest> Activated;
}

internal sealed class ToastActivationRequest(string arguments) : EventArgs
{
    public string Arguments { get; } = arguments;
}
