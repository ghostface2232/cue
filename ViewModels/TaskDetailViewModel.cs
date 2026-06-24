using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;

namespace Cue.ViewModels;

public enum WhenEditorMode
{
    Unscheduled,
    Today,
    SpecificDate,
}

public sealed record WhenEditorOption(WhenEditorMode Mode, string Name);
public sealed record TaskGroupEditorOption(Guid? Id, string Name);
public sealed record TimeOption(int Value, string Label);

/// <summary>
/// An immutable capture of everything an autosave writes, taken the moment an edit happens. Binding the
/// target task id together with all field values means a queued save always persists the values that were
/// on screen for <see cref="Id"/> — never whatever the panel shows by the time the save runs, even if the
/// user has since switched to a different task.
/// </summary>
public sealed record TaskEditSnapshot(
    Guid Id,
    string Title,
    string? Notes,
    Priority Priority,
    ScheduledWhen When,
    Guid? TaskGroupId,
    IReadOnlyList<Guid> TagIds);

public partial class TagEditorOption : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }

    /// <summary>The tag's hex color (e.g. "#3498DB"), or <c>null</c> for the default.</summary>
    public string? Color { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public TagEditorOption(Guid id, string name, bool isSelected, string? color = null)
    {
        Id = id;
        Name = name;
        IsSelected = isSelected;
        Color = color;
    }
}

/// <summary>
/// One editable checklist item in the detail panel: a checkbox, an editable title, and an editable
/// memo. Any change (tick, title edit, memo edit) invokes a single callback the detail view model owns,
/// which rebuilds the parent task's <see cref="ChecklistItem"/> list and saves it.
/// </summary>
public partial class ChecklistItemViewModel : ObservableObject
{
    private readonly Action<ChecklistItemViewModel> _onChanged;
    private bool _suppress;

    public Guid Id { get; }
    public double VisualOpacity => IsChecked ? 0.48 : 1.0;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    public ChecklistItemViewModel(ChecklistItem item, Action<ChecklistItemViewModel> onChanged)
    {
        Id = item.Id;
        _onChanged = onChanged;
        _suppress = true;
        IsChecked = item.IsChecked;
        Title = item.Title;
        _suppress = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(VisualOpacity));
        if (!_suppress) _onChanged(this);
    }

    partial void OnTitleChanged(string value)
    {
        if (!_suppress) _onChanged(this);
    }

    /// <summary>Sets the checkbox without firing the save callback — used to revert a failed toggle.</summary>
    public void SetCheckedSilently(bool value)
    {
        _suppress = true;
        IsChecked = value;
        _suppress = false;
    }
}

/// <summary>
/// Edits one full <see cref="TaskItem"/>. Detail reads use the file source of truth by id; option
/// lists use <see cref="ITaskIndex"/>. The embedded checklist is read straight off the loaded task and
/// edited in place. Every mutation returns through <see cref="ITaskStore"/> and then asks the owning
/// list to re-query its current index view. A task has a single date (When) — there is no separate
/// deadline.
/// </summary>
public partial class TaskDetailViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IReorderService _reorder;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Func<Task> _refreshOwner;
    private readonly INavDataChangeNotifier _navNotifier;

    // Serializes checklist writes (add/remove/edit/tick) so a fast run of edits can't interleave their
    // load-modify-save of the parent task.
    private readonly SemaphoreSlim _checklistGate = new(1, 1);
    private Guid? _taskId;
    private ScheduledWhen _originalWhen = ScheduledWhen.Unscheduled;
    private WhenEditorMode _loadedWhenMode;
    private DateTimeOffset? _loadedWhenDate;
    private TimeSpan? _loadedWhenTime;
    private bool _loadedIsWhenAllDay;
    private bool _isLoading;

    // Coalesces a single user action that touches several properties into one save. Distinct from
    // <see cref="_isLoading"/>, which suppresses saves entirely while OpenAsync fills the panel.
    private bool _suppressAutoSave;

    // Autosaves run through a single serial chain: each link awaits the previous one, so a fast run of
    // edits can never interleave or reorder its writes (the guarantee the list's completion toggles get
    // from a semaphore). Every link carries an immutable <see cref="TaskEditSnapshot"/> captured when the
    // edit happened, so a queued save persists the values that were on screen for that task — never
    // whatever the panel shows by the time the save runs, even after the user switches to another task.
    // The tail is the whole queue's completion, so DrainPendingSaveAsync waits for every pending save,
    // not just the latest. Links are appended on the UI thread, so the store write and owner refresh
    // resume on the captured UI context.
    private Task _saveChain = Task.CompletedTask;

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();
    public IReadOnlyList<TimeOption> Hours { get; } = Enumerable.Range(0, 24).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<TimeOption> Minutes { get; } = Enumerable.Range(0, 60).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<WhenEditorOption> WhenOptions { get; } =
    [
        new(WhenEditorMode.Unscheduled, "미지정"),
        new(WhenEditorMode.Today, "Today"),
        new(WhenEditorMode.SpecificDate, "날짜 지정"),
    ];

    public ObservableCollection<TaskGroupEditorOption> TaskGroups { get; } = new();
    public ObservableCollection<TagEditorOption> Tags { get; } = new();
    public ObservableCollection<ChecklistItemViewModel> Checklist { get; } = new();
    public Guid? CurrentTaskId => _taskId;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Priority SelectedPriority { get; set; }

    [ObservableProperty]
    public partial WhenEditorOption SelectedWhenOption { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? WhenDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? WhenTime { get; set; }

    [ObservableProperty]
    public partial TimeOption? SelectedWhenHour { get; set; }

    [ObservableProperty]
    public partial TimeOption? SelectedWhenMinute { get; set; }

    /// <summary>종일 — a date-only When that carries no time (pinned to end of day, 23:59).</summary>
    [ObservableProperty]
    public partial bool IsWhenAllDay { get; set; }

    [ObservableProperty]
    public partial TaskGroupEditorOption? SelectedTaskGroup { get; set; }

    [ObservableProperty]
    public partial string NewChecklistItemTitle { get; set; } = string.Empty;

    /// <summary>True while the inline "new tag" field is showing in the tag card (replaces the old modal).</summary>
    [ObservableProperty]
    public partial bool IsAddingTag { get; set; }

    [ObservableProperty]
    public partial string NewTagName { get; set; } = string.Empty;

    public bool IsSpecificDate => SelectedWhenOption.Mode == WhenEditorMode.SpecificDate;
    public bool HasConcreteWhen => SelectedWhenOption.Mode is WhenEditorMode.Today or WhenEditorMode.SpecificDate;

    /// <summary>Time picker shows only with a concrete date that is not all-day.</summary>
    public bool ShowWhenTime => HasConcreteWhen && !IsWhenAllDay;

    /// <summary>End-of-day time a 종일 (all-day) item is pinned to so it expires at 23:59 local.</summary>
    private static readonly TimeSpan AllDayTime = new(23, 59, 0);
    private TimeSpan? EffectiveWhenTime => IsWhenAllDay ? AllDayTime : WhenTime;
    public bool IsWhenEditorVisible => HasConcreteWhen;
    // The "+ 날짜 추가" button shows only when there is no concrete date.
    public bool CanAddWhen => !IsWhenEditorVisible;

    public TaskDetailViewModel(
        ITaskStore store,
        ITaskIndex index,
        IReorderService reorder,
        TimeProvider clock,
        TimeZoneInfo zone,
        Func<Task> refreshOwner,
        INavDataChangeNotifier navNotifier)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
        _clock = clock;
        _zone = zone;
        _refreshOwner = refreshOwner;
        _navNotifier = navNotifier;
        SelectedWhenOption = WhenOptions[0];
    }

    partial void OnSelectedWhenOptionChanged(WhenEditorOption value)
    {
        var resume = SuppressAutoSave();
        if (value.Mode == WhenEditorMode.SpecificDate && WhenDate is null)
            WhenDate = LocalNow();
        if (value.Mode is WhenEditorMode.Today or WhenEditorMode.SpecificDate && WhenTime is null)
            WhenTime = TimeSpan.FromHours(12);
        OnPropertyChanged(nameof(IsSpecificDate));
        OnPropertyChanged(nameof(HasConcreteWhen));
        OnPropertyChanged(nameof(ShowWhenTime));
        OnPropertyChanged(nameof(IsWhenEditorVisible));
        OnPropertyChanged(nameof(CanAddWhen));
        resume();
    }

    partial void OnIsWhenAllDayChanged(bool value)
    {
        var resume = SuppressAutoSave();
        if (!value && WhenTime == AllDayTime)
        {
            WhenTime = TimeSpan.FromHours(12); // leaving all-day → restore a sensible editable time
            SetWhenTimeEditors(WhenTime);
        }
        OnPropertyChanged(nameof(ShowWhenTime));
        resume();
    }

    partial void OnWhenDateChanged(DateTimeOffset? value)
    {
        if (_isLoading) return;
        var resume = SuppressAutoSave();
        if (value is not null)
        {
            SelectedWhenOption = FindOption(WhenEditorMode.SpecificDate);
            WhenTime ??= TimeSpan.FromHours(12);
            SetWhenTimeEditors(WhenTime);
        }
        else
        {
            SelectedWhenOption = FindOption(WhenEditorMode.Unscheduled);
        }
        resume();
    }

    partial void OnWhenTimeChanged(TimeSpan? value) => RequestAutoSave();
    partial void OnSelectedPriorityChanged(Priority value) => RequestAutoSave();
    partial void OnSelectedTaskGroupChanged(TaskGroupEditorOption? value) => RequestAutoSave();

    partial void OnSelectedWhenHourChanged(TimeOption? value) => SyncWhenTimeFromParts();
    partial void OnSelectedWhenMinuteChanged(TimeOption? value) => SyncWhenTimeFromParts();

    public async Task OpenAsync(Guid taskId)
    {
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted)
        {
            Close();
            return;
        }

        _isLoading = true;
        _taskId = task.Id;
        Title = task.Title;
        Notes = task.Notes ?? string.Empty;
        SelectedPriority = task.Priority;
        WhenDate = task.When.Date?.ToLocal();
        WhenTime = task.When.Date?.ToLocal().TimeOfDay;
        SetWhenTimeEditors(WhenTime);
        // A date-only (종일) item was pinned to 23:59 — recognize it on load.
        IsWhenAllDay = task.When.Date is not null && WhenTime == AllDayTime;
        SelectedWhenOption = OptionFor(task.When);

        _originalWhen = task.When;
        _loadedWhenMode = SelectedWhenOption.Mode;
        _loadedWhenDate = WhenDate;
        _loadedWhenTime = WhenTime;
        _loadedIsWhenAllDay = IsWhenAllDay;

        // Stay in the loading guard until the panel is fully populated — setting SelectedTaskGroup and the
        // tag rows below must not trip autosave (no save should fire just from opening a task).
        await LoadTaskGroupsAsync(task.TaskGroupId);
        await LoadTagsAsync(task.TagIds);
        LoadChecklist(task);
        IsOpen = true;
        _isLoading = false;
    }

    public void Close()
    {
        _taskId = null;
        IsOpen = false;
    }

    public void SetWhenTime(TimeSpan time)
    {
        var resume = SuppressAutoSave();
        WhenTime = time;
        SetWhenTimeEditors(time);
        resume();
    }

    public void EnableWhenEditor()
    {
        var resume = SuppressAutoSave();
        WhenDate = LocalNow();
        WhenTime ??= TimeSpan.FromHours(12);
        SetWhenTimeEditors(WhenTime);
        SelectedWhenOption = FindOption(WhenEditorMode.SpecificDate);
        IsWhenAllDay = true; // new dates start as 종일; uncheck to set a time
        resume();
    }

    public void ClearWhen()
    {
        var resume = SuppressAutoSave();
        IsWhenAllDay = false;
        WhenDate = null;
        WhenTime = null;
        SelectedWhenHour = null;
        SelectedWhenMinute = null;
        SelectedWhenOption = FindOption(WhenEditorMode.Unscheduled);
        resume();
    }

    /// <summary>
    /// Persists the panel's current edits straight to the file source of truth, flushing any text that
    /// hasn't been committed by a focus-out yet, then waits for the whole save queue to settle. The detail
    /// panel autosaves on every change, so the close button and task switch call this to catch a
    /// title/notes edit whose <c>LostFocus</c> hasn't fired and to guarantee nothing is left in flight.
    /// </summary>
    public Task FlushAsync()
    {
        if (CaptureSnapshot() is { } snapshot) Enqueue(snapshot);
        return DrainPendingSaveAsync();
    }

    /// <summary>
    /// Fire-and-forget autosave for single-selection changes (priority, When, project, label toggles).
    /// A no-op while OpenAsync is filling the panel (<see cref="_isLoading"/>), while a multi-property
    /// action is coalescing its writes (<see cref="_suppressAutoSave"/>), or while the panel is closed.
    /// Captures the panel's current values as an immutable snapshot now and appends it to the serial save
    /// chain, so overlapping edits can't reorder their writes and a late save can't pick up another task's
    /// fields.
    /// </summary>
    private void RequestAutoSave()
    {
        if (_isLoading || _suppressAutoSave || !IsOpen) return;
        if (CaptureSnapshot() is { } snapshot) Enqueue(snapshot);
    }

    /// <summary>Awaits every autosave queued so far. A test/diagnostic seam: production code fires
    /// autosaves and forgets them (the chain keeps them ordered); only callers that need to observe the
    /// persisted result deterministically — or a task switch that must not strand a pending write — await
    /// this. The chain's tail completes only after every link before it, so this drains the full queue.</summary>
    internal Task DrainPendingSaveAsync() => _saveChain;

    /// <summary>Captures the panel's current edits as an immutable snapshot, or <c>null</c> when no task is
    /// open. Reads every field (and resolves When via <see cref="BuildWhen"/>) on the calling thread so the
    /// values can't shift under a later save.</summary>
    private TaskEditSnapshot? CaptureSnapshot()
    {
        if (_taskId is not { } id) return null;
        return new TaskEditSnapshot(
            id,
            Title.Trim(),
            string.IsNullOrWhiteSpace(Notes) ? null : Notes,
            SelectedPriority,
            BuildWhen(),
            SelectedTaskGroup?.Id,
            Tags.Where(tag => tag.IsSelected && tag.Id != Guid.Empty).Select(tag => tag.Id).ToList());
    }

    /// <summary>Appends a save to the serial chain. Called on the UI thread, so the continuation captures
    /// the UI synchronization context and the store write + owner refresh resume on it. Awaiting the
    /// returned tail (<see cref="_saveChain"/>) waits for this save and every save queued before it.</summary>
    private void Enqueue(TaskEditSnapshot snapshot)
        => _saveChain = ChainSaveAsync(_saveChain, snapshot);

    private async Task ChainSaveAsync(Task previous, TaskEditSnapshot snapshot)
    {
        // SaveSnapshotAsync swallows its own failures, so the predecessor never faults; the guard is pure
        // belt-and-braces so one bad link can never stall the queue behind it.
        try { await previous; }
        catch { /* logged by the failing save itself */ }
        await SaveSnapshotAsync(snapshot);
    }

    /// <summary>
    /// Suppresses per-property autosave for the duration of one user action that touches several
    /// properties (e.g. picking a date sets the option, date, and time), so it persists once. The
    /// returned callback restores the prior state and fires the single coalesced save; nesting is safe.
    /// </summary>
    private Action SuppressAutoSave()
    {
        var previous = _suppressAutoSave;
        _suppressAutoSave = true;
        return () =>
        {
            _suppressAutoSave = previous;
            RequestAutoSave();
        };
    }

    /// <summary>
    /// The single autosave path, reused by every trigger and by the close flush. Re-reads the live record
    /// by the snapshot's id, applies the snapshot's captured fields, and saves through
    /// <see cref="ITaskStore"/> (which stamps UpdatedAt). Because the values come from the snapshot rather
    /// than the live panel, a save still targets the task it was queued for even after the user switches
    /// tasks. The dirty-check in <see cref="BuildWhen"/> (run when the snapshot was captured) means a save
    /// that never touched the date preserves the original When's exact instant and time zone. A failed
    /// save is logged, not swallowed silently, and left for the next change or the close flush to retry.
    /// </summary>
    private async Task SaveSnapshotAsync(TaskEditSnapshot snapshot)
    {
        try
        {
            var task = await _store.GetAsync<TaskItem>(snapshot.Id);
            if (task is null || task.IsDeleted) return;

            task.Title = snapshot.Title;
            task.Notes = snapshot.Notes;
            task.Priority = snapshot.Priority;
            task.When = snapshot.When;
            task.TaskGroupId = snapshot.TaskGroupId;
            task.TagIds = snapshot.TagIds.ToList();

            await _store.SaveAsync(task);
            await _refreshOwner();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cue] Detail autosave failed: {ex.Message}");
        }
    }

    /// <summary>Appends a checklist item to the open task (title from the inline box), persists the
    /// parent, and adds the matching row to the panel without a full reload (so focus is undisturbed).</summary>
    [RelayCommand]
    private async Task AddChecklistItemAsync()
    {
        if (_taskId is not { } id || string.IsNullOrWhiteSpace(NewChecklistItemTitle)) return;
        await _checklistGate.WaitAsync();
        try
        {
            var parent = await _store.GetAsync<TaskItem>(id);
            if (parent is null || parent.IsDeleted) return;

            var item = new ChecklistItem { Title = NewChecklistItemTitle.Trim() };
            parent.Checklist.Add(item);
            await _store.SaveAsync(parent);

            Checklist.Add(CreateChecklistRow(item));
            NewChecklistItemTitle = string.Empty;
            await _refreshOwner();
        }
        finally { _checklistGate.Release(); }
    }

    /// <summary>Reveals the inline "new tag" field in the tag card (the + 새 태그 affordance).</summary>
    public void BeginAddTag()
    {
        NewTagName = string.Empty;
        IsAddingTag = true;
    }

    /// <summary>Dismisses the inline "new tag" field without creating anything.</summary>
    public void CancelAddTag()
    {
        IsAddingTag = false;
        NewTagName = string.Empty;
    }

    /// <summary>
    /// Creates the tag typed inline, selects it on this task, persists the assignment, and tells the
    /// sidebar (and any other open panel) to reload through the nav-change notifier. A blank name just
    /// closes the field.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAddTagAsync()
    {
        var name = NewTagName.Trim();
        if (name.Length == 0) { CancelAddTag(); return; }

        var existing = await _index.GetTagsAsync();
        var tag = new Tag
        {
            Name = name,
            Color = TagColors.ForNewTag(existing.Count),
            SortOrder = _reorder.AppendRank(existing.Select(item => item.SortOrder)),
        };
        await _store.SaveAsync(tag);

        var selected = Tags.Where(item => item.IsSelected && item.Id != Guid.Empty).Select(item => item.Id).Append(tag.Id);
        await LoadTagsAsync(selected);
        IsAddingTag = false;
        NewTagName = string.Empty;
        // The new tag is selected on the spot — persist the task's tag assignment too.
        await FlushAsync();
        // The sidebar tag list and any other open detail panel reflect the new tag immediately.
        _navNotifier.NotifyChanged();
    }

    /// <summary>
    /// Reloads the group and tag option lists from the index while preserving the panel's current
    /// selection, so a group/tag created elsewhere (the sidebar, another panel) appears here at once
    /// without disturbing in-progress edits. A no-op when the panel is closed. Runs under the loading
    /// guard so refilling the options never trips autosave.
    /// </summary>
    public async Task ReloadNavOptionsAsync()
    {
        if (!IsOpen || _taskId is null) return;
        var wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            var selectedTags = Tags.Where(item => item.IsSelected && item.Id != Guid.Empty).Select(item => item.Id).ToList();
            await LoadTaskGroupsAsync(SelectedTaskGroup?.Id);
            await LoadTagsAsync(selectedTags);
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    /// <summary>Soft-deletes the task currently open in the panel (its embedded checklist goes with it),
    /// closes the panel, and asks the owning list to refresh.</summary>
    [RelayCommand]
    private async Task DeleteTaskAsync()
    {
        if (_taskId is not { } id) return;
        await _store.DeleteAsync<TaskItem>(id);
        Close();
        await _refreshOwner();
    }

    /// <summary>Removes one checklist item from the open task, persists the parent, and drops its row
    /// from the panel. Embedded items have no tombstone — they are removed outright.</summary>
    [RelayCommand]
    private async Task DeleteChecklistItemAsync(Guid id)
    {
        if (_taskId is not { } taskId) return;
        await _checklistGate.WaitAsync();
        try
        {
            var parent = await _store.GetAsync<TaskItem>(taskId);
            if (parent is null || parent.IsDeleted) return;

            parent.Checklist.RemoveAll(item => item.Id == id);
            await _store.SaveAsync(parent);

            if (Checklist.FirstOrDefault(item => item.Id == id) is { } row) Checklist.Remove(row);
            await _refreshOwner();
        }
        finally { _checklistGate.Release(); }
    }

    /// <summary>Persists the checklist after a tick / title / memo edit: rebuilds the parent task's
    /// checklist from the panel's current rows and saves. Does not reload the rows, so an in-progress
    /// edit keeps focus. Serialized through the checklist gate; failures are logged, not thrown.</summary>
    private async Task SaveChecklistAsync()
    {
        if (_taskId is not { } id) return;
        // Snapshot the rows synchronously (before any await), so a task switch that repopulates the
        // Checklist collection can't make this save write one task's items onto another.
        var snapshot = Checklist.Select(row => new ChecklistItem
        {
            Id = row.Id,
            Title = row.Title.Trim(),
            IsChecked = row.IsChecked,
        }).ToList();
        await _checklistGate.WaitAsync();
        try
        {
            var parent = await _store.GetAsync<TaskItem>(id);
            if (parent is null || parent.IsDeleted) return;

            parent.Checklist = snapshot;
            await _store.SaveAsync(parent);
            await _refreshOwner();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cue] Checklist save failed: {ex.Message}");
        }
        finally { _checklistGate.Release(); }
    }

    private ScheduledWhen BuildWhen()
    {
        if (SelectedWhenOption.Mode == _loadedWhenMode
            && SameDay(WhenDate, _loadedWhenDate)
            && WhenTime == _loadedWhenTime
            && IsWhenAllDay == _loadedIsWhenAllDay)
            return _originalWhen;

        return SelectedWhenOption.Mode switch
        {
            WhenEditorMode.Unscheduled => ScheduledWhen.Unscheduled,
            WhenEditorMode.Today => ScheduledWhen.On(PinDate(LocalNow(), EffectiveWhenTime)),
            WhenEditorMode.SpecificDate when WhenDate is { } date => ScheduledWhen.On(PinDate(date, EffectiveWhenTime)),
            WhenEditorMode.SpecificDate => ScheduledWhen.Unscheduled,
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static bool SameDay(DateTimeOffset? left, DateTimeOffset? right)
        => left is null || right is null
            ? left is null && right is null
            : left.Value.Date == right.Value.Date;

    private WhenEditorOption OptionFor(ScheduledWhen when)
    {
        if (when.Kind == WhenKind.Unscheduled) return FindOption(WhenEditorMode.Unscheduled);

        var chosenDay = when.Date is { } value ? DateOnly.FromDateTime(value.ToLocal().DateTime) : default;
        var today = DateOnly.FromDateTime(LocalNow().DateTime);
        if (chosenDay == today)
            return FindOption(WhenEditorMode.Today);
        return FindOption(WhenEditorMode.SpecificDate);
    }

    private WhenEditorOption FindOption(WhenEditorMode mode) => WhenOptions.First(option => option.Mode == mode);

    private ZonedDateTime PinDate(DateTimeOffset selected, TimeSpan? selectedTime)
    {
        var time = selectedTime ?? TimeSpan.FromHours(12);
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(selectedTime));

        return ZonedDateTime.FromLocal(
            new DateTime(selected.Year, selected.Month, selected.Day).Add(time),
            _zone.Id);
    }

    private DateTimeOffset LocalNow() => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);

    private void SetWhenTimeEditors(TimeSpan? time)
    {
        SelectedWhenHour = time is { } value ? Hours[value.Hours] : null;
        SelectedWhenMinute = time is { } selected ? Minutes[selected.Minutes] : null;
    }

    private void SyncWhenTimeFromParts()
    {
        if (_isLoading || SelectedWhenHour is null || SelectedWhenMinute is null) return;
        WhenTime = new TimeSpan(SelectedWhenHour.Value, SelectedWhenMinute.Value, 0);
    }

    private async Task LoadTaskGroupsAsync(Guid? selectedId)
    {
        TaskGroups.Clear();
        TaskGroups.Add(new TaskGroupEditorOption(null, "그룹 없음"));
        foreach (var taskGroup in await _index.GetTaskGroupsAsync())
            TaskGroups.Add(new TaskGroupEditorOption(taskGroup.Id, taskGroup.Name));

        if (selectedId is { } id && TaskGroups.All(option => option.Id != id))
        {
            var inactive = await _store.GetAsync<TaskGroup>(id);
            if (inactive is not null && !inactive.IsDeleted)
                TaskGroups.Add(new TaskGroupEditorOption(inactive.Id, inactive.Name));
        }
        SelectedTaskGroup = TaskGroups.FirstOrDefault(option => option.Id == selectedId) ?? TaskGroups[0];
    }

    private async Task LoadTagsAsync(IEnumerable<Guid> selectedIds)
    {
        var selected = selectedIds.Where(id => id != Guid.Empty).ToHashSet();
        Tags.Clear();
        // A synthetic "태그 없음" row (Guid.Empty) makes the no-tag state explicit and is checked by
        // default; it stays mutually exclusive with the real tags via ToggleTag/SyncNoTagOption.
        Tags.Add(new TagEditorOption(Guid.Empty, "태그 없음", selected.Count == 0));
        foreach (var tag in await _index.GetTagsAsync())
            Tags.Add(new TagEditorOption(tag.Id, tag.Name, selected.Contains(tag.Id), tag.Color));
    }

    /// <summary>Toggles a tag row. The "태그 없음" entry (Guid.Empty) behaves like a reset: choosing it
    /// clears every real tag; choosing a real tag clears it; and it re-checks on its own once no
    /// real tag remains selected.</summary>
    public void ToggleTag(Guid id)
    {
        var target = Tags.FirstOrDefault(tag => tag.Id == id);
        if (target is null) return;

        if (id == Guid.Empty)
        {
            foreach (var tag in Tags) tag.IsSelected = tag.Id == Guid.Empty;
        }
        else
        {
            target.IsSelected = !target.IsSelected;
            SyncNoTagOption();
        }
        RequestAutoSave();
    }

    private void SyncNoTagOption()
    {
        var none = Tags.FirstOrDefault(tag => tag.Id == Guid.Empty);
        if (none is not null)
            none.IsSelected = Tags.All(tag => tag.Id == Guid.Empty || !tag.IsSelected);
    }

    /// <summary>Fills the checklist rows straight from the loaded task (the file source of truth) — the
    /// embedded list needs no index query.</summary>
    private void LoadChecklist(TaskItem task)
    {
        Checklist.Clear();
        foreach (var item in task.Checklist)
            Checklist.Add(CreateChecklistRow(item));
    }

    // The change callback is fire-and-forget; the explicit discard documents that the save Task is
    // intentionally unobserved (SaveChecklistAsync swallows its own failures, so no unobserved exception).
    private ChecklistItemViewModel CreateChecklistRow(ChecklistItem item)
        => new(item, changed => { _ = SaveChecklistAsync(); });
}
