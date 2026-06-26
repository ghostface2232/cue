namespace Cue.ViewModels;

/// <summary>
/// App-scoped registry of detail view models that still owe work to disk. Each list-page navigation gets its
/// own <see cref="TaskListViewModel"/> and so its own <see cref="TaskDetailViewModel"/>;
/// a save failure recorded on one of them used to be parked in a single field on the window, where a second
/// failure on a different page silently overwrote the first — so the close prompt and the retry only ever saw
/// the last page's view model. This coordinator instead tracks <i>every</i> view model with outstanding
/// failures at once, aggregates the unsaved-task count across all of them, retries them all together, and lets
/// a successful whole-slice write of a task supersede a stale failure for that same task held by another page's
/// view model (the same task can be open in two lists — e.g. a group view and 오늘 할 일).
/// </summary>
/// <remarks>
/// <para>Each registered view model is the owner of its own failure machinery (retry/back-off, snapshot
/// supersession). This type holds only references to the view models that currently have failures and folds
/// their state together; it never duplicates the per-view-model bookkeeping.</para>
/// <para>Threading: the registry is guarded by <see cref="_gate"/>. A view model pushes its state in from
/// outside its own <c>lock(this)</c>, and every call this type makes back into a view model is done after
/// releasing <see cref="_gate"/> — so neither lock is ever held across a call into the other component, and the
/// two can't deadlock. <see cref="Changed"/> may fire on a background thread (a save/retry continuation), so
/// UI consumers must marshal to the dispatcher.</para>
/// </remarks>
public sealed class SaveFailureCoordinator
{
    private readonly object _gate = new();
    // Exactly the view models that currently have at least one outstanding failure. A view model adds itself
    // when it gains a failure and removes itself when its last one clears (see Update), so membership and
    // "has failures" are the same fact — and a member kept alive here stays retryable until it resolves.
    private readonly HashSet<TaskDetailViewModel> _failing = new();

    /// <summary>Raised whenever the aggregate failure state may have changed (a view model gained or cleared
    /// failures). May fire on a background thread — marshal to the UI dispatcher before touching UI.</summary>
    public event EventHandler? Changed;

    /// <summary>True when any registered view model still owes work to disk.</summary>
    public bool HasFailures
    {
        get { lock (_gate) return _failing.Count > 0; }
    }

    /// <summary>How many distinct tasks have unsaved work across every page's view model — the number shown
    /// on the close prompt. A task failing in two view models at once counts once.</summary>
    public int UnsavedTaskCount
    {
        get
        {
            TaskDetailViewModel[] snapshot;
            lock (_gate) snapshot = _failing.ToArray();
            var ids = new HashSet<Guid>();
            foreach (var vm in snapshot)
                foreach (var id in vm.UnsavedTaskIds)
                    ids.Add(id);
            return ids.Count;
        }
    }

    /// <summary>Folds a view model's current failure state into the registry: it joins when it has unsaved
    /// failures and leaves when it has none. Idempotent; called by the view model whenever its failure set
    /// changes (a settled save, a retry, or a cross-page supersession).</summary>
    public void Update(TaskDetailViewModel vm)
    {
        bool changed;
        lock (_gate)
            changed = vm.HasUnsavedFailures ? _failing.Add(vm) : _failing.Remove(vm);
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Announces that <paramref name="source"/> persisted the whole of <paramref name="taskId"/> to
    /// disk, so any stale whole-slice failure for that same task still held by a <i>different</i> page's view
    /// model is dropped — replaying its older snapshot would clobber the newer write. The source's own
    /// bookkeeping already cleared its slice, so it is skipped here.</summary>
    public void OnTaskPersisted(TaskDetailViewModel source, Guid taskId)
    {
        TaskDetailViewModel[] others;
        lock (_gate)
            others = _failing.Where(vm => !ReferenceEquals(vm, source)).ToArray();
        foreach (var vm in others)
            vm.DropWholeSliceFailures(taskId); // each calls back into Update once its set settles
    }

    /// <summary>Retries the outstanding saves on every registered view model, in a stable snapshot taken up
    /// front. View models that clear remove themselves via <see cref="Update"/> as they go; whatever fails
    /// again re-registers. Surfaces a single failure as-is and several as an <see cref="AggregateException"/>.</summary>
    public async Task RetryAllAsync()
    {
        TaskDetailViewModel[] snapshot;
        lock (_gate) snapshot = _failing.ToArray();

        var exceptions = new List<Exception>();
        foreach (var vm in snapshot)
        {
            try { await vm.RetrySaveAsync(); }
            catch (Exception ex) { exceptions.Add(ex); }
        }

        if (exceptions.Count == 1) throw exceptions[0];
        if (exceptions.Count > 1) throw new AggregateException("저장에 실패했습니다.", exceptions);
    }
}
