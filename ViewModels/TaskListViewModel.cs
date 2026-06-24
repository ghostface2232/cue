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

    /// <summary>Accent-colored prefix of the quick-add placeholder — the group/tag name in a filtered
    /// view, empty otherwise. Rendered as a separate <c>Run</c> so only the name carries the accent.</summary>
    [ObservableProperty]
    public partial string QuickAddPlaceholderName { get; set; } = string.Empty;

    /// <summary>Normal-colored remainder of the quick-add placeholder (e.g. "할 일 입력하기"). Carries a
    /// leading space when <see cref="QuickAddPlaceholderName"/> precedes it.</summary>
    [ObservableProperty]
    public partial string QuickAddPlaceholderSuffix { get; set; } = "할 일 입력하기";

    /// <summary>Drives the custom placeholder overlay's visibility — shown only while the box is empty.</summary>
    public bool QuickAddIsEmpty => string.IsNullOrEmpty(QuickAddText);

    partial void OnQuickAddTextChanged(string value) => OnPropertyChanged(nameof(QuickAddIsEmpty));

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

    public TaskListViewModel(ITaskStore store, ITaskIndex index, IDateParser parser, IReorderService reorder, IRecurringTaskService recurrence, TimeProvider clock, TimeZoneInfo zone, INavDataChangeNotifier navNotifier)
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
        Detail = new TaskDetailViewModel(store, index, reorder, clock, zone, LoadAsync, navNotifier);
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

    /// <summary>Every realized task row across the flat list and all groups. Checklist rows are not
    /// included — they are not selectable.</summary>
    private IEnumerable<TaskRowViewModel> AllRows()
    {
        foreach (var row in Tasks) yield return row;
        foreach (var group in Groups)
            foreach (var row in group.Tasks) yield return row;
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
        // The quick-add placeholder echoes the active view: a plain "할 일 입력하기" everywhere, the
        // dated "오늘 할 일 입력하기" in Today, and the group/tag name (accent-colored via its own Run)
        // ahead of the suffix in a filtered view.
        (QuickAddPlaceholderName, QuickAddPlaceholderSuffix) = _mode switch
        {
            TaskListMode.Today => (string.Empty, "오늘 할 일 입력하기"),
            TaskListMode.TaskGroup or TaskListMode.Tag => (Title, " 할 일 입력하기"),
            _ => (string.Empty, "할 일 입력하기"),
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

        // A task given only a date with no explicit time is registered as an all-day (종일) event —
        // marked explicitly via ScheduledWhen.AllDay, which the detail panel reads back as 종일.
        // Recurrence anchors keep their own time and are left untouched.
        if (when.Kind == WhenKind.OnDate && !parsed.WhenHasTime && parsed.Recurrence is null)
            when = AllDay(when);

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
        if (_mode == TaskListMode.Priority)
        {
            SyncRows(Tasks, []);   // the 중요도 view is grouped; its rows live in the buckets, not the flat list
            SyncGroups(items);
        }
        else
        {
            SyncGroups([]);        // every other view is flat; keep the bucket list empty
            SyncRows(Tasks, items);
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

    /// <summary>Toggles one nested checklist item from the list: loads its owning task, flips the item
    /// by id, and saves the parent through the store. Serialized through the same gate as completion so
    /// rapid checks can't reorder their writes; on failure the checkbox is restored.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleChecklistItemAsync(ChecklistRowViewModel row)
    {
        var isChecked = row.IsChecked;
        await _toggleGate.WaitAsync();
        try
        {
            // Atomic read-modify-write through the store: flip just this item on the latest persisted
            // task, so a concurrent detail-panel metadata save (a different save path) can't clobber the
            // checklist, nor this toggle the metadata. MutateAsync returns null when the task or item is
            // gone, in which case the checkbox is put back.
            var saved = await _store.MutateAsync<TaskItem>(row.ParentTaskId, task =>
            {
                var item = task.Checklist.FirstOrDefault(i => i.Id == row.Id);
                if (item is null) return false;
                item.IsChecked = isChecked;
                return true;
            });
            if (saved is null)
            {
                row.SetCheckedSilently(!isChecked);
                return;
            }
            await LoadAsync();
        }
        catch
        {
            row.SetCheckedSilently(!isChecked);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    /// <summary>Marks a dated When as all-day (종일): keeps its calendar day (pinned to local start of
    /// day) but flags it as carrying no meaningful time, so the UI shows the date alone.</summary>
    private ScheduledWhen AllDay(ScheduledWhen when)
    {
        if (when.Date is not { } zoned) return when;
        var localDate = zoned.ToLocal().Date;
        return ScheduledWhen.AllDay(ZonedDateTime.FromLocal(localDate, _timeZoneId));
    }

    private Guid RequiredFilterId()
        => _filterId ?? throw new InvalidOperationException($"{_mode} navigation requires an id.");

    private TaskRowViewModel CreateRow(TaskListItem item)
    {
        var row = new TaskRowViewModel(item, r => ToggleCompleteCommand.Execute(r)) { IsCompact = _rowsCompact };
        SyncChecklistRows(row, item);
        return row;
    }

    // Tracks the list's current narrow/wide layout so rows created during a reload (a save, a task
    // switch) inherit it without waiting for the next resize.
    private bool _rowsCompact;

    /// <summary>Sets whether rows reflow their right-edge group/tag chips beneath the title. The page
    /// calls this as the list column resizes; the state is remembered so freshly created rows match.</summary>
    public void SetRowsCompact(bool compact)
    {
        _rowsCompact = compact;
        foreach (var row in AllRows())
            row.IsCompact = compact;
    }

    // Fixed priority buckets for the 중요도 view, in display order. A bucket is shown only when it has rows.
    private static readonly (Priority Priority, string Name)[] PriorityBuckets =
    [
        (Priority.P1, "매우 중요"), (Priority.P2, "중요"), (Priority.P3, "보통"), (Priority.P4, "사소"),
    ];

    /// <summary>
    /// Reconciles <paramref name="target"/> in place to match <paramref name="items"/> by id, reusing the
    /// existing row instances: an unchanged row is patched (silently when nothing actually changed), a
    /// moved row is repositioned, a row that's gone is removed, and only a genuinely new row is created.
    /// Each row's nested checklist is re-synced too. This is what lets a save-triggered refresh avoid
    /// recreating the whole list, preserving scroll, focus, selection, and the entrance animation for
    /// unchanged rows. The list is flat — there is no task nesting.
    /// </summary>
    private void SyncRows(ObservableCollection<TaskRowViewModel> target, IReadOnlyList<TaskListItem> items)
    {
        var desired = new HashSet<Guid>(items.Count);
        foreach (var item in items) desired.Add(item.Id);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i].Id))
                target.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i >= target.Count || target[i].Id != item.Id)
            {
                var existing = IndexOfRow(target, item.Id);
                if (existing >= 0)
                {
                    target.Move(existing, i);
                }
                else
                {
                    target.Insert(i, CreateRow(item));
                    continue;
                }
            }
            target[i].Update(item);
            SyncChecklistRows(target[i], item);
        }
    }

    /// <summary>Reconciles a row's nested checklist rows in place from its projection, by id — reusing
    /// the existing row instances exactly like <see cref="SyncRows"/>. A clear-and-rebuild here would
    /// tear down and re-realize every checkbox on every refresh (and every save routes through one):
    /// that replayed the entrance animation on each item (a flicker on any reload, e.g. a task switch)
    /// and — worse — destroyed the very checkbox the user just toggled mid-interaction, so its checked /
    /// dim state never stuck and it couldn't be unchecked. Reusing instances avoids all of that; only a
    /// genuinely new or retitled item is created.</summary>
    private void SyncChecklistRows(TaskRowViewModel row, TaskListItem item)
    {
        var target = row.ChecklistItems;
        var items = item.Checklist ?? (IReadOnlyList<TaskListChecklistItem>)Array.Empty<TaskListChecklistItem>();

        var desired = new HashSet<Guid>(items.Count);
        foreach (var checklistItem in items) desired.Add(checklistItem.Id);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i].Id))
                target.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            var checklistItem = items[i];
            var existing = IndexOfChecklistRow(target, checklistItem.Id);
            if (existing >= 0)
            {
                // The row's title is immutable, so reuse it only while the title is unchanged and just
                // patch the checked state (silently — patching must not fire the toggle/save callback).
                // A retitled item falls through to a fresh row.
                if (target[existing].Title == ChecklistRowViewModel.DisplayTitle(checklistItem.Title))
                {
                    if (existing != i) target.Move(existing, i);
                    target[i].SetCheckedSilently(checklistItem.IsChecked);
                    continue;
                }
                target.RemoveAt(existing);
            }
            target.Insert(i, new ChecklistRowViewModel(item.Id, checklistItem, r => ToggleChecklistItemCommand.Execute(r)));
        }
    }

    private static int IndexOfChecklistRow(ObservableCollection<ChecklistRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id) return i;
        return -1;
    }

    private static int IndexOfRow(ObservableCollection<TaskRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id) return i;
        return -1;
    }

    /// <summary>Reconciles the priority buckets (the only grouped view) in place: keeps a group per
    /// priority that has rows, in P1→P4 order, reusing existing group instances and syncing each group's
    /// rows. Pass an empty list to clear the buckets for an ungrouped view.</summary>
    private void SyncGroups(IReadOnlyList<TaskListItem> items)
    {
        var desired = new List<(string Name, List<TaskListItem> Items)>();
        foreach (var (priority, name) in PriorityBuckets)
        {
            var bucket = items.Where(item => item.Priority == priority).ToList();
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

    // Row context-menu actions (move group / tag / rename / delete)

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
        // Atomic read-modify-write so moving the task touches only TaskGroupId on the latest record,
        // never overwriting a field a concurrent save path changed. Returning false leaves it a no-op
        // (and skips the reload) when the task is already in the target group.
        var moved = await _store.MutateAsync<TaskItem>(taskId, task =>
        {
            if (task.TaskGroupId == taskGroupId) return false;
            task.TaskGroupId = taskGroupId;
            return true;
        });
        if (moved is not null) await LoadAsync();
    }

    /// <summary>Adds the tag if absent, removes it if present, then refreshes.</summary>
    public async Task ToggleTaskTagAsync(Guid taskId, Guid tagId)
    {
        // Atomic read-modify-write so the tag toggle touches only TagIds on the latest record.
        var changed = await _store.MutateAsync<TaskItem>(taskId, task =>
        {
            if (!task.TagIds.Remove(tagId)) task.TagIds.Add(tagId);
            return true;
        });
        if (changed is not null) await LoadAsync();
    }

    /// <summary>Renames a task, then refreshes. A blank name is ignored.</summary>
    public async Task RenameTaskAsync(Guid taskId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;
        // Atomic read-modify-write so the rename touches only Title on the latest record.
        var renamed = await _store.MutateAsync<TaskItem>(taskId, task => { task.Title = trimmed; return true; });
        if (renamed is not null) await LoadAsync();
    }

    /// <summary>Soft-deletes a task (its embedded checklist goes with it), closes the detail panel if
    /// this task was open, and refreshes the list.</summary>
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
                // Logbook copy and advances to its next cycle, a one-off is simply stamped done. The
                // task's embedded checklist items are independent and left as they are.
                await _recurrence.CompleteAsync(row.Id, _clock.GetUtcNow());
            }
            else
            {
                // Atomic read-modify-write so clearing completion touches only CompletedAt on the
                // latest record, never overwriting a field a concurrent save path just changed.
                await _store.MutateAsync<TaskItem>(row.Id, task => { task.CompletedAt = null; return true; });
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
