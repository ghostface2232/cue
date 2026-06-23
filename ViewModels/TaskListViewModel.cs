using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;

namespace Cue.ViewModels;

/// <summary>Which index-backed list this view shows.</summary>
public enum TaskListMode
{
    /// <summary>Home / 모든 할 일 — every active task regardless of group, with completed rows dimmed.</summary>
    AllTasks,

    /// <summary>Today — active tasks with a When date today or earlier, with completed rows dimmed.</summary>
    Today,

    /// <summary>Active tasks with a When date on a future day, with completed rows dimmed.</summary>
    Upcoming,

    /// <summary>Active tasks without a When date — the "언젠가" bucket, with completed rows dimmed.</summary>
    Anytime,

    /// <summary>Completed tasks.</summary>
    Logbook,

    /// <summary>Prioritized tasks (P1–P4), grouped by priority.</summary>
    Priority,

    /// <summary>Active tasks belonging to one task group, with completed rows dimmed.</summary>
    TaskGroup,

    /// <summary>Active tasks carrying one tag, with completed rows dimmed.</summary>
    Tag,

    /// <summary>Active tasks in no group at all — the 그룹 없음 collection point for unfiled captures.</summary>
    NoTaskGroup,

    /// <summary>Active tasks carrying no tag — the 태그 없음 collection point for unfiled captures.</summary>
    NoTag,
}

/// <summary>
/// Drives one task list: the quick-add line at the top and the list below. The full data loop lives
/// here — quick-add text goes through the parser into a <see cref="TaskItem"/>, is saved through the
/// store (which writes the file and updates the index together), and the list is reloaded straight
/// from the index.
/// </summary>
public partial class TaskListViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IDateParser _parser;
    private readonly IReorderService _reorder;
    private readonly IRecurringTaskService _recurrence;
    private readonly TimeProvider _clock;
    private readonly string _timeZoneId;
    private readonly TimeZoneInfo _timeZone;

    // Serializes reorder persists so a fast run of drops can't interleave their rank writes.
    private readonly SemaphoreSlim _reorderGate = new(1, 1);

    // Serializes completion toggles so concurrent/rapid checks can't reorder their saves.
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private TaskListMode _mode = TaskListMode.AllTasks;
    private Guid? _filterId;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>Grouped rows for the 중요도 (priority) view — the only grouped list.</summary>
    public ObservableCollection<TaskGroupViewModel> Groups { get; } = new();

    public TaskDetailViewModel Detail { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string QuickAddText { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsStandardList { get; set; } = true;

    [ObservableProperty]
    public partial bool IsTaskGroupMode { get; set; }

    /// <summary>True when the list is rendered as grouped buckets (the 중요도 view's P1–P4 buckets)
    /// rather than one flat list.</summary>
    [ObservableProperty]
    public partial bool IsGroupedList { get; set; }

    [ObservableProperty]
    public partial string TitleCaption { get; set; } = string.Empty;

    public bool HasTitleCaption => TitleCaption.Length > 0;
    public bool CanQuickAdd => _mode is not (TaskListMode.Logbook or TaskListMode.Priority);

    public TaskListViewModel(ITaskStore store, ITaskIndex index, IDateParser parser, IReorderService reorder, IRecurringTaskService recurrence, TimeProvider clock, TimeZoneInfo zone)
    {
        _store = store;
        _index = index;
        _parser = parser;
        _reorder = reorder;
        _recurrence = recurrence;
        _clock = clock;
        _timeZoneId = zone.Id;
        _timeZone = zone;

        Title = "모든 할 일";
        QuickAddText = string.Empty;
        Detail = new TaskDetailViewModel(store, index, reorder, recurrence, clock, zone, LoadAsync, SelectTaskAsync);
        // Clear the row selection accent when the detail panel closes.
        Detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskDetailViewModel.IsOpen) && !Detail.IsOpen)
                ApplySelection(null);
        };
    }

    /// <summary>Marks the row whose task is <paramref name="id"/> selected and clears the rest.</summary>
    private void ApplySelection(Guid? id)
    {
        foreach (var row in AllRows())
            row.IsSelected = row.Id == id;
    }

    /// <summary>Every realized row across the flat list and all groups, including subtask rows.</summary>
    private IEnumerable<TaskRowViewModel> AllRows()
    {
        foreach (var row in Tasks) { yield return row; foreach (var sub in row.Subtasks) yield return sub; }
        foreach (var group in Groups)
            foreach (var row in group.Tasks) { yield return row; foreach (var sub in row.Subtasks) yield return sub; }
    }

    /// <summary>Switches which index view this list reflects, and retitles accordingly.</summary>
    public void SetNavigation(TaskListNavigation navigation)
    {
        _mode = navigation.Mode;
        _filterId = navigation.FilterId;
        Title = navigation.Title ?? navigation.Mode switch
        {
            TaskListMode.AllTasks => "모든 할 일",
            TaskListMode.Today => "오늘 할 일",
            TaskListMode.Upcoming => "앞으로 할 일",
            TaskListMode.Anytime => "언젠가 할 일",
            TaskListMode.Logbook => "완료한 일",
            TaskListMode.Priority => "중요도",
            TaskListMode.TaskGroup => "그룹",
            TaskListMode.Tag => "태그",
            TaskListMode.NoTaskGroup => "그룹 없음",
            TaskListMode.NoTag => "태그 없음",
            _ => throw new ArgumentOutOfRangeException(nameof(navigation)),
        };
        TitleCaption = string.Empty;
        OnPropertyChanged(nameof(HasTitleCaption));
        IsTaskGroupMode = _mode == TaskListMode.TaskGroup;
        IsGroupedList = _mode is TaskListMode.Priority;
        IsStandardList = !IsGroupedList;
        OnPropertyChanged(nameof(CanQuickAdd));
    }

    /// <summary>Quick-add: parse the line, create + save a task, then refresh from the index.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (!CanQuickAdd) return;
        var text = QuickAddText?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var parsed = _parser.Parse(text, _clock.GetUtcNow(), _timeZoneId);

        // The parser's When is used as-is when it recognized any placement, including explicit
        // Unscheduled markers ("언젠가") and recurrence anchors. Only a genuinely dateless line gets
        // list placement; Today pins it to today and other lists leave it Unscheduled.
        var when = QuickAddContext.Apply(parsed.When, parsed.WhenAssigned, _mode, _clock.GetUtcNow(), _timeZone);

        var task = new TaskItem
        {
            Title = parsed.Title,
            When = when,
            Recurrence = parsed.Recurrence,
            TaskGroupId = _mode == TaskListMode.TaskGroup ? _filterId : null,
            // New tasks append to the end of the list the user is currently looking at.
            SortOrder = _reorder.AppendRank(VisibleRowRanks()),
        };
        if (_mode == TaskListMode.Tag && _filterId is { } tagId)
            task.TagIds.Add(tagId);

        await _store.SaveAsync(task);
        QuickAddText = string.Empty;
        await LoadAsync();
    }

    /// <summary>Reloads the list from the index for the current mode.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IReadOnlyList<TaskListItem> items;

        switch (_mode)
        {
            case TaskListMode.AllTasks:
                items = await _index.GetAllActiveAsync();
                break;
            case TaskListMode.Today:
                items = await _index.GetTodayAsync();
                break;
            case TaskListMode.Upcoming:
                items = await _index.GetUpcomingAsync();
                break;
            case TaskListMode.Anytime:
                items = await _index.GetAnytimeAsync();
                break;
            case TaskListMode.Logbook:
                items = await _index.GetLogbookAsync();
                break;
            case TaskListMode.Priority:
                items = await _index.GetByPriorityAsync();
                break;
            case TaskListMode.TaskGroup:
                items = await _index.GetByTaskGroupAsync(RequiredFilterId());
                break;
            case TaskListMode.Tag:
                items = await _index.GetByTagAsync(RequiredFilterId());
                break;
            case TaskListMode.NoTaskGroup:
                items = await _index.GetWithoutTaskGroupAsync();
                break;
            case TaskListMode.NoTag:
                items = await _index.GetWithoutTagAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        // Reconcile the live collections in place instead of clearing and rebuilding them, so a refresh
        // (every save routes through here) reuses unchanged row instances. That keeps scroll position,
        // focus, selection, a drag in progress, and the item-entrance animation intact, and avoids
        // recreating hundreds of rows when a single detail-panel edit changed one value.
        var roots = BuildForest(items);
        if (_mode == TaskListMode.Priority)
        {
            SyncRows(Tasks, []);   // the 중요도 view is grouped; its rows live in the buckets, not the flat list
            SyncGroups(roots);
        }
        else
        {
            SyncGroups([]);        // every other view is flat; keep the bucket list empty
            SyncRows(Tasks, roots);
        }

        IsEmpty = IsGroupedList
            ? Groups.Count == 0
            : Tasks.Count == 0;

        // Re-apply the selection accent: reused rows keep theirs, but a freshly inserted row needs it set.
        ApplySelection(Detail.IsOpen ? Detail.CurrentTaskId : null);
    }

    /// <summary>
    /// Commits a drag-reorder inside one list. The row has already been moved optimistically in
    /// <paramref name="list"/> (the reorder surface drives the visuals); here we persist the moved
    /// row's new rank through the rank service, which writes <i>only</i> that record — except for the
    /// rare rebalance. Each row's <see cref="TaskRowViewModel.SortOrder"/> is refreshed to the
    /// returned ranks so a following drag computes against current keys. A failed save reloads the
    /// list from the index, snapping it back to the persisted truth.
    /// </summary>
    public async Task PersistReorderAsync(ObservableCollection<TaskRowViewModel> list, Guid movedId)
    {
        await _reorderGate.WaitAsync();
        try
        {
            var ordered = list.Select(row => new RankedItem(row.Id, row.SortOrder)).ToList();
            var result = await _reorder.MoveAsync<TaskItem>(movedId, ordered);
            foreach (var row in list)
                if (result.ChangedRanks.TryGetValue(row.Id, out var rank))
                    row.SortOrder = rank;
        }
        catch
        {
            await LoadAsync();
        }
        finally
        {
            _reorderGate.Release();
        }
    }

    /// <summary>Every task row's current rank across the visible lists — the basis for an append rank.</summary>
    private IEnumerable<string?> VisibleRowRanks()
    {
        foreach (var row in Tasks) yield return row.SortOrder;
        foreach (var group in Groups)
            foreach (var row in group.Tasks) yield return row.SortOrder;
    }

    /// <summary>Marks every open descendant (the full checklist subtree) of a completed task done.</summary>
    private async Task CompleteDescendantsAsync(Guid parentId, DateTimeOffset at)
    {
        foreach (var child in await _index.GetSubtasksAsync(parentId))
        {
            if (child.IsCompleted) continue;
            var task = await _store.GetAsync<TaskItem>(child.Id);
            if (task is null || task.IsDeleted) continue;
            task.CompletedAt = at;
            await _store.SaveAsync(task);
            await CompleteDescendantsAsync(child.Id, at);
        }
    }

    /// <summary>Every row beneath this one, depth-first — its subtasks and their subtasks.</summary>
    private static IEnumerable<TaskRowViewModel> Descendants(TaskRowViewModel row)
    {
        foreach (var sub in row.Subtasks)
        {
            yield return sub;
            foreach (var deeper in Descendants(sub)) yield return deeper;
        }
    }

    private Guid RequiredFilterId()
        => _filterId ?? throw new InvalidOperationException($"{_mode} navigation requires an id.");

    private TaskRowViewModel CreateRow(TaskListItem item)
        => new(item, row => ToggleCompleteCommand.Execute(row));

    // Fixed priority buckets for the 중요도 view, in display order. A bucket is shown only when it has rows.
    private static readonly (Priority Priority, string Name)[] PriorityBuckets =
    [
        (Priority.P1, "매우 중요"), (Priority.P2, "중요"), (Priority.P3, "보통"), (Priority.P4, "사소"),
    ];

    /// <summary>Groups a flat, source-ordered index projection into a forest: roots (items whose parent
    /// isn't in the result set) in order, each carrying its child rows in order — mirroring how the list
    /// nests subtasks under their parent.</summary>
    private static List<RowNode> BuildForest(IReadOnlyList<TaskListItem> items)
    {
        var byId = items.ToDictionary(item => item.Id, item => new RowNode(item));
        var roots = new List<RowNode>();
        foreach (var item in items)
        {
            var node = byId[item.Id];
            if (item.ParentTaskId is { } parentId && byId.TryGetValue(parentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        return roots;
    }

    /// <summary>
    /// Reconciles <paramref name="target"/> in place to match <paramref name="nodes"/> by id, reusing the
    /// existing row instances: an unchanged row is patched (silently when nothing actually changed), a
    /// moved row is repositioned, a row that's gone is removed, and only a genuinely new row is created.
    /// Recurses into each row's subtasks. This is what lets a save-triggered refresh avoid recreating the
    /// whole list, preserving scroll, focus, selection, and the entrance animation for unchanged rows.
    /// </summary>
    private void SyncRows(ObservableCollection<TaskRowViewModel> target, IReadOnlyList<RowNode> nodes)
    {
        var desired = new HashSet<Guid>(nodes.Count);
        foreach (var node in nodes) desired.Add(node.Item.Id);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i].Id))
                target.RemoveAt(i);

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (i >= target.Count || target[i].Id != node.Item.Id)
            {
                var existing = IndexOfRow(target, node.Item.Id);
                if (existing >= 0)
                {
                    target.Move(existing, i);
                }
                else
                {
                    target.Insert(i, BuildRow(node)); // BuildRow assembles the whole subtree
                    continue;
                }
            }
            target[i].Update(node.Item);
            SyncRows(target[i].Subtasks, node.Children);
        }
    }

    private TaskRowViewModel BuildRow(RowNode node)
    {
        var row = CreateRow(node.Item);
        foreach (var child in node.Children)
            row.AddSubtask(BuildRow(child));
        return row;
    }

    private static int IndexOfRow(ObservableCollection<TaskRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id) return i;
        return -1;
    }

    /// <summary>Reconciles the priority buckets (the only grouped view) in place: keeps a group per
    /// priority that has rows, in P1→P4 order, reusing existing group instances and syncing each group's
    /// rows. Pass an empty forest to clear the buckets for an ungrouped view.</summary>
    private void SyncGroups(IReadOnlyList<RowNode> roots)
    {
        var desired = new List<(string Name, List<RowNode> Roots)>();
        foreach (var (priority, name) in PriorityBuckets)
        {
            var bucket = roots.Where(node => node.Item.Priority == priority).ToList();
            if (bucket.Count > 0) desired.Add((name, bucket));
        }

        var desiredNames = new HashSet<string>(desired.Select(group => group.Name));
        for (var i = Groups.Count - 1; i >= 0; i--)
            if (!desiredNames.Contains(Groups[i].Name))
                Groups.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            var (name, bucket) = desired[i];
            if (i >= Groups.Count || Groups[i].Name != name)
            {
                var existing = IndexOfGroup(name);
                if (existing >= 0) Groups.Move(existing, i);
                else Groups.Insert(i, new TaskGroupViewModel(name));
            }
            SyncRows(Groups[i].Tasks, bucket);
        }
    }

    private int IndexOfGroup(string name)
    {
        for (var i = 0; i < Groups.Count; i++)
            if (Groups[i].Name == name) return i;
        return -1;
    }

    /// <summary>A node in the desired row forest built from an index projection: one item plus its ordered
    /// child nodes.</summary>
    private sealed class RowNode(TaskListItem item)
    {
        public TaskListItem Item { get; } = item;
        public List<RowNode> Children { get; } = new();
    }

    // ---- Row context-menu actions (move group / tag / rename / delete) -------

    /// <summary>The live task record by id, so a row context menu can reflect its current group and
    /// tags. Reads the file source of truth.</summary>
    public Task<TaskItem?> GetTaskAsync(Guid id) => _store.GetAsync<TaskItem>(id);

    /// <summary>Active groups, for the row context menu's "move to group" submenu.</summary>
    public Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync() => _index.GetTaskGroupsAsync();

    /// <summary>Active tags, for the row context menu's tag submenu.</summary>
    public Task<IReadOnlyList<TagListItem>> GetTagsAsync() => _index.GetTagsAsync();

    /// <summary>Moves a task into a group, or to the Cue home when <paramref name="taskGroupId"/> is
    /// null, then refreshes. A no-op if the task is gone or already there.</summary>
    public async Task MoveTaskToTaskGroupAsync(Guid taskId, Guid? taskGroupId)
    {
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted || task.TaskGroupId == taskGroupId) return;
        task.TaskGroupId = taskGroupId;
        await _store.SaveAsync(task);
        await LoadAsync();
    }

    /// <summary>Adds the tag if absent, removes it if present, then refreshes.</summary>
    public async Task ToggleTaskTagAsync(Guid taskId, Guid tagId)
    {
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted) return;
        if (!task.TagIds.Remove(tagId)) task.TagIds.Add(tagId);
        await _store.SaveAsync(task);
        await LoadAsync();
    }

    /// <summary>Renames a task, then refreshes. A blank name is ignored.</summary>
    public async Task RenameTaskAsync(Guid taskId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted) return;
        task.Title = trimmed;
        await _store.SaveAsync(task);
        await LoadAsync();
    }

    /// <summary>Soft-deletes a task (cascading to its subtask subtree, handled by the store), closes
    /// the detail panel if this task was open, and refreshes the list.</summary>
    [RelayCommand]
    public async Task DeleteTaskAsync(Guid id)
    {
        await _store.DeleteAsync<TaskItem>(id);
        if (Detail.IsOpen && Detail.CurrentTaskId == id) Detail.Close();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SelectTaskAsync(Guid id)
    {
        if (Detail.IsOpen && Detail.CurrentTaskId != id)
        {
            // Persist and drain the outgoing task's pending edits before tearing the panel down, so a
            // queued autosave can't resume against the next task's freshly loaded fields.
            await Detail.FlushAsync();
            Detail.Close();
            await Task.Delay(90);
        }

        await Detail.OpenAsync(id);
        ApplySelection(id);
    }

    /// <summary>
    /// Applies a row's completion change to the store, then refreshes. Serialized through a gate so
    /// rapid toggles can't reorder their writes (concurrent executions are allowed so none are
    /// dropped — they queue on the gate). The row remains dimmed in place until the next list load;
    /// on failure its checkbox is restored so the UI never disagrees with what's on disk.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleCompleteAsync(TaskRowViewModel row)
    {
        var completed = row.IsCompleted;
        await _toggleGate.WaitAsync();
        try
        {
            if (completed)
            {
                // Completion runs through the recurrence service: a repeating task leaves a completed
                // Logbook copy and advances to its next cycle, a one-off is simply stamped done.
                var now = _clock.GetUtcNow();
                await _recurrence.CompleteAsync(row.Id, now);
                // If the task was truly completed (not a recurring task that advanced to its next
                // cycle), pull its whole checklist down with it — a parent is never "done" while its
                // subtasks linger open.
                var parent = await _store.GetAsync<TaskItem>(row.Id);
                if (parent is { CompletedAt: not null })
                {
                    await CompleteDescendantsAsync(row.Id, now);
                    foreach (var descendant in Descendants(row)) descendant.SetCompletedSilently(true);
                }
            }
            else
            {
                var task = await _store.GetAsync<TaskItem>(row.Id);
                if (task is not null)
                {
                    task.CompletedAt = null;
                    await _store.SaveAsync(task);
                }
            }
            // Keep the row in place for this session so completion has a visible, reversible
            // acknowledgement. Index-backed navigation/reload naturally removes it later.
        }
        catch
        {
            // Save/reload failed — put the checkbox back so it reflects the real (unchanged) state.
            row.SetCompletedSilently(!completed);
        }
        finally
        {
            _toggleGate.Release();
        }
    }
}
