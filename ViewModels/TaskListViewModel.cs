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
    All,

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

    /// <summary>Active tasks belonging to one project, with completed rows dimmed.</summary>
    Project,

    /// <summary>Active tasks carrying one label, with completed rows dimmed.</summary>
    Label,
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

    private TaskListMode _mode = TaskListMode.All;
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
    public partial bool IsProjectMode { get; set; }

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
            TaskListMode.All => "모든 할 일",
            TaskListMode.Today => "오늘 할 일",
            TaskListMode.Upcoming => "앞으로 할 일",
            TaskListMode.Anytime => "언젠가 할 일",
            TaskListMode.Logbook => "완료한 일",
            TaskListMode.Priority => "중요도",
            TaskListMode.Project => "그룹",
            TaskListMode.Label => "태그",
            _ => throw new ArgumentOutOfRangeException(nameof(navigation)),
        };
        TitleCaption = string.Empty;
        OnPropertyChanged(nameof(HasTitleCaption));
        IsProjectMode = _mode == TaskListMode.Project;
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
            ProjectId = _mode == TaskListMode.Project ? _filterId : null,
            // New tasks append to the end of the list the user is currently looking at.
            SortOrder = _reorder.AppendRank(VisibleRowRanks()),
        };
        if (_mode == TaskListMode.Label && _filterId is { } labelId)
            task.LabelIds.Add(labelId);

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
            case TaskListMode.All:
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
            case TaskListMode.Project:
                items = await _index.GetByProjectAsync(RequiredFilterId());
                break;
            case TaskListMode.Label:
                items = await _index.GetByLabelAsync(RequiredFilterId());
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        Tasks.Clear();
        AddHierarchicalRows(Tasks, items);

        Groups.Clear();
        if (_mode == TaskListMode.Priority)
        {
            // One group per priority that has rows, P1 → P4. Rows arrive already ordered by priority.
            (Priority Priority, string Name)[] buckets =
            [
                (Priority.P1, "매우 중요"), (Priority.P2, "중요"), (Priority.P3, "보통"), (Priority.P4, "사소"),
            ];
            var byPriority = Tasks.ToLookup(row => row.Priority);
            Tasks.Clear();
            foreach (var (priority, name) in buckets)
            {
                var rows = byPriority[priority].ToList();
                if (rows.Count == 0) continue;
                var group = new TaskGroupViewModel(name);
                foreach (var row in rows) group.Tasks.Add(row);
                Groups.Add(group);
            }
        }

        IsEmpty = IsGroupedList
            ? Groups.Count == 0
            : Tasks.Count == 0;

        // Re-apply the selection accent to the freshly rebuilt rows so it survives a reload.
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

    private void AddHierarchicalRows(
        ObservableCollection<TaskRowViewModel> destination,
        IEnumerable<TaskListItem> source)
    {
        var items = source.ToList();
        var rows = items.ToDictionary(item => item.Id, CreateRow);

        foreach (var item in items)
        {
            var row = rows[item.Id];
            if (item.ParentTaskId is { } parentId && rows.TryGetValue(parentId, out var parent))
                parent.AddSubtask(row);
            else
                destination.Add(row);
        }
    }

    // ---- Row context-menu actions (move group / tag / rename / delete) -------

    /// <summary>The live task record by id, so a row context menu can reflect its current group and
    /// tags. Reads the file source of truth.</summary>
    public Task<TaskItem?> GetTaskAsync(Guid id) => _store.GetAsync<TaskItem>(id);

    /// <summary>Active groups, for the row context menu's "move to group" submenu.</summary>
    public Task<IReadOnlyList<ProjectListItem>> GetProjectsAsync() => _index.GetProjectsAsync();

    /// <summary>Active tags, for the row context menu's tag submenu.</summary>
    public Task<IReadOnlyList<LabelListItem>> GetLabelsAsync() => _index.GetLabelsAsync();

    /// <summary>Moves a task into a group, or to the Cue home when <paramref name="projectId"/> is
    /// null, then refreshes. A no-op if the task is gone or already there.</summary>
    public async Task MoveTaskToProjectAsync(Guid taskId, Guid? projectId)
    {
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted || task.ProjectId == projectId) return;
        task.ProjectId = projectId;
        await _store.SaveAsync(task);
        await LoadAsync();
    }

    /// <summary>Adds the tag if absent, removes it if present, then refreshes.</summary>
    public async Task ToggleTaskLabelAsync(Guid taskId, Guid labelId)
    {
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted) return;
        if (!task.LabelIds.Remove(labelId)) task.LabelIds.Add(labelId);
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
