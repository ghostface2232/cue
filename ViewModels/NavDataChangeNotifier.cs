namespace Cue.ViewModels;

/// <summary>
/// App-scoped signal that the navigation record set — groups and tags — changed (created, renamed,
/// recolored/re-iconed, or deleted). The sidebar and any open detail panel both read groups/tags from
/// the index but are separate, transient view models, so whichever one makes a change raises this and
/// the other reloads in lockstep. Deliberately payload-free: listeners re-query the index themselves.
/// </summary>
public interface INavDataChangeNotifier
{
    event EventHandler? Changed;
    void NotifyChanged();
}

public sealed class NavDataChangeNotifier : INavDataChangeNotifier
{
    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
