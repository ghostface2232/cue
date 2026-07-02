namespace Cue.Storage;

/// <summary>
/// Signals that a successful store mutation has finished. Consumers re-read the file-backed source of
/// truth; the event deliberately carries no record snapshot that could become stale.
/// </summary>
public interface ITaskStoreChangeSource
{
    event EventHandler? Changed;
}
