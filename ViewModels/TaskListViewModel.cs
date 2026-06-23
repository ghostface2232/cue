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
    /// <summary>Home / Cue — project-less (unclassified) open tasks.</summary>
    Inbox,

    /// <summary>Today — open tasks due or scheduled today or earlier.</summary>
    Today,

    /// <summary>Open tasks scheduled for a future day or carrying a future deadline.</summary>
    Upcoming,

    /// <summary>Open tasks without a scheduled When date.</summary>
    Anytime,

    /// <summary>Open tasks parked for Someday.</summary>
    Someday,

    /// <summary>Completed tasks.</summary>
    Logbook,

    /// <summary>Open tasks belonging to one project.</summary>
    Project,

    /// <summary>Open tasks carrying one label.</summary>
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

    private TaskListMode _mode = TaskListMode.Inbox;
    private Guid? _filterId;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>The evening-flagged subset of Today, displayed as a section on that page.</summary>
    public ObservableCollection<TaskRowViewModel> EveningTasks { get; } = new();

    public ObservableCollection<TaskSectionGroupViewModel> ProjectGroups { get; } = new();

    public TaskDetailViewModel Detail { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string QuickAddText { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasEveningTasks { get; set; }

    [ObservableProperty]
    public partial bool IsStandardList { get; set; } = true;

    [ObservableProperty]
    public partial bool IsProjectMode { get; set; }

    [ObservableProperty]
    public partial string TitleCaption { get; set; } = string.Empty;

    public bool HasTitleCaption => TitleCaption.Length > 0;
    public bool CanQuickAdd => _mode != TaskListMode.Logbook;

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

        Title = "Cue";
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

    /// <summary>Every realized row across all sections, including subtask rows.</summary>
    private IEnumerable<TaskRowViewModel> AllRows()
    {
        foreach (var row in Tasks) { yield return row; foreach (var sub in row.Subtasks) yield return sub; }
        foreach (var row in EveningTasks) { yield return row; foreach (var sub in row.Subtasks) yield return sub; }
        foreach (var group in ProjectGroups)
            foreach (var row in group.Tasks) { yield return row; foreach (var sub in row.Subtasks) yield return sub; }
    }

    /// <summary>Switches which index view this list reflects, and retitles accordingly.</summary>
    public void SetNavigation(TaskListNavigation navigation)
    {
        _mode = navigation.Mode;
        _filterId = navigation.FilterId;
        Title = navigation.Title ?? navigation.Mode switch
        {
            TaskListMode.Inbox => "Cue",
            TaskListMode.Today => "Today",
            TaskListMode.Upcoming => "Upcoming",
            TaskListMode.Anytime => "Anytime",
            TaskListMode.Someday => "Someday",
            TaskListMode.Logbook => "Logbook",
            TaskListMode.Project => "Project",
            TaskListMode.Label => "Label",
            _ => throw new ArgumentOutOfRangeException(nameof(navigation)),
        };
        TitleCaption = navigation.DeadlineDate is { } deadline
            ? $"마감 {deadline.Month}월 {deadline.Day}일"
            : string.Empty;
        OnPropertyChanged(nameof(HasTitleCaption));
        IsProjectMode = _mode == TaskListMode.Project;
        IsStandardList = !IsProjectMode;
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
        var task = new TaskItem
        {
            Title = parsed.Title,
            When = QuickAddContext.Apply(parsed.When, _mode, _clock.GetUtcNow(), _timeZone),
            Deadline = parsed.Deadline,
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
        IReadOnlyList<TaskListItem> eveningItems = Array.Empty<TaskListItem>();

        switch (_mode)
        {
            case TaskListMode.Inbox:
                items = await _index.GetInboxAsync();
                break;
            case TaskListMode.Today:
                items = await _index.GetTodayAsync();
                eveningItems = await _index.GetThisEveningAsync();
                break;
            case TaskListMode.Upcoming:
                items = await _index.GetUpcomingAsync();
                break;
            case TaskListMode.Anytime:
                items = await _index.GetAnytimeAsync();
                break;
            case TaskListMode.Someday:
                items = await _index.GetSomedayAsync();
                break;
            case TaskListMode.Logbook:
                items = await _index.GetLogbookAsync();
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

        // This Evening is already a subset query of Today. Keep it in its own section without
        // rendering those same rows twice on the Today page.
        var eveningIds = eveningItems.Select(item => item.Id).ToHashSet();

        Tasks.Clear();
        AddHierarchicalRows(Tasks, items.Where(item => !eveningIds.Contains(item.Id)));

        EveningTasks.Clear();
        AddHierarchicalRows(EveningTasks, eveningItems);

        HasEveningTasks = EveningTasks.Count > 0;

        ProjectGroups.Clear();
        if (_mode == TaskListMode.Project)
        {
            var sections = await _index.GetSectionsByProjectAsync(RequiredFilterId());
            var groups = sections.ToDictionary(section => section.Id, section => new TaskSectionGroupViewModel(section));
            var unsectioned = new TaskSectionGroupViewModel(null);
            foreach (var row in Tasks)
            {
                var item = items.First(item => item.Id == row.Id);
                if (item.SectionId is { } sectionId && groups.TryGetValue(sectionId, out var group))
                    group.Tasks.Add(row);
                else
                    unsectioned.Tasks.Add(row);
            }
            Tasks.Clear();
            foreach (var section in sections) ProjectGroups.Add(groups[section.Id]);
            if (unsectioned.Tasks.Count > 0) ProjectGroups.Add(unsectioned);
        }

        IsEmpty = IsProjectMode
            ? ProjectGroups.Count == 0
            : Tasks.Count == 0 && !HasEveningTasks;

        // Re-apply the selection accent to the freshly rebuilt rows so it survives a reload.
        ApplySelection(Detail.IsOpen ? Detail.CurrentTaskId : null);
    }

    [RelayCommand]
    private async Task CreateSectionAsync(string name)
    {
        if (!IsProjectMode || string.IsNullOrWhiteSpace(name)) return;
        var projectId = RequiredFilterId();
        var existing = await _index.GetSectionsByProjectAsync(projectId);
        await _store.SaveAsync(new Section
        {
            ProjectId = projectId,
            Name = name.Trim(),
            SortOrder = _reorder.AppendRank(existing.Select(section => section.SortOrder)),
        });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RenameSectionAsync(RenameRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return;
        var section = await _store.GetAsync<Section>(request.Id);
        if (section is null || section.IsDeleted) return;
        section.Name = request.Name.Trim();
        await _store.SaveAsync(section);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteSectionAsync(Guid id)
    {
        await _store.DeleteAsync<Section>(id);
        await LoadAsync();
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
        foreach (var row in EveningTasks) yield return row.SortOrder;
        foreach (var group in ProjectGroups)
            foreach (var row in group.Tasks) yield return row.SortOrder;
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
                await _recurrence.CompleteAsync(row.Id, _clock.GetUtcNow());
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
