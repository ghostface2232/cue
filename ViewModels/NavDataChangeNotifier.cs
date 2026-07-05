namespace Cue.ViewModels;

/// <summary>
/// App-scoped signal that navigation data changed, in two flavors:
///
/// <para><see cref="Changed"/> — the record <i>set</i> changed: a group/tag was created, renamed,
/// recolored/re-iconed, or deleted. The sidebar and any open detail panel both read groups/tags from
/// the index but are separate, transient view models, so whichever one makes a change raises this and
/// the other reloads its lists in lockstep (which also re-stamps the count badges).</para>
///
/// <para><see cref="CountsChanged"/> — only the per-group/tag open-task <i>counts</i> changed, because a
/// task was added, completed/reopened, deleted, or moved between groups/tags. The record set is
/// unchanged, so the sidebar refreshes the count badges in place rather than rebuilding its item lists —
/// keeping selection, expansion, and scroll intact. Raising <see cref="Changed"/> already refreshes the
/// counts as part of its rebuild, so a structural change need not also raise this.</para>
///
/// <para><see cref="TasksChangedExternally"/> — a task record changed outside any open page's own UI
/// action (today: the toast notification's 완료 button completing a task while the window is open).
/// A page's own actions reload its rows themselves, so this fires only for out-of-page mutations;
/// the pages showing task rows reload on it so the affected row doesn't linger stale — an unchecked
/// row for an already-completed task otherwise invites a second completion. Raised on the UI
/// thread.</para>
///
/// Both signals are deliberately payload-free: listeners re-query the index themselves.
/// </summary>
public interface INavDataChangeNotifier
{
    event EventHandler? Changed;
    void NotifyChanged();

    event EventHandler? CountsChanged;
    void NotifyCountsChanged();

    event EventHandler? TasksChangedExternally;
    void NotifyTasksChangedExternally();
}

public sealed class NavDataChangeNotifier : INavDataChangeNotifier
{
    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CountsChanged;
    public void NotifyCountsChanged() => CountsChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? TasksChangedExternally;
    public void NotifyTasksChangedExternally() => TasksChangedExternally?.Invoke(this, EventArgs.Empty);
}
