using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;

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

/// <summary>One choice in the 반복 (recurrence) picker: a display name and the RRULE it stands for.
/// A <c>null</c> <see cref="Rule"/> is the "반복 안 함" (no recurrence) entry. The view model keeps the
/// RRULE string only — turning it into a <see cref="RecurrenceRule"/> (with an anchor) happens on save,
/// and evaluating it stays in the storage layer (invariant 9).</summary>
public sealed record RecurrenceEditorOption(string? Rule, string Name);

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
    RecurrenceRule? Recurrence,
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
/// One editable checklist item in the detail panel: a checkbox and an editable title. Any change
/// (tick, title edit) invokes a single callback the detail view model owns, which rebuilds the
/// parent task's <see cref="ChecklistItem"/> list and saves it.
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
    private readonly IRecurringTaskService _recurrence;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Func<Task> _refreshOwner;
    private readonly INavDataChangeNotifier _navNotifier;

    // How many recorded past cycles the timeline realizes per page. The panel opens showing the most
    // recent page plus the live head pip, and "이전 기록" pages older cycles in on demand — a long history
    // is never eager-loaded.
    private const int TimelinePageSize = 12;
    private int _timelineWindow;

    private Guid? _taskId;
    private ScheduledWhen _originalWhen = ScheduledWhen.Unscheduled;
    private WhenEditorMode _loadedWhenMode;
    private DateTimeOffset? _loadedWhenDate;
    private TimeSpan? _loadedWhenTime;
    private bool _loadedIsWhenAllDay;
    // The recurrence the task loaded with, kept so an edit that never touched 반복 preserves the rule's
    // exact original anchor instead of re-anchoring it (which would shift the series) on every save.
    private RecurrenceRule? _originalRecurrence;
    private string? _loadedRecurrenceRule;
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

    // Checklist writes (add/remove/tick/title) run on their own serial chain, parallel to _saveChain.
    // Each link captures the rows synchronously when the edit happens and persists through the store's
    // atomic MutateAsync, so it replaces only the checklist and never clobbers a concurrent metadata
    // save. The tail is tracked here (not fire-and-forget) so DrainPendingSaveAsync — and therefore a
    // task switch or close — waits for pending checklist writes too, never stranding one.
    private Task _checklistChain = Task.CompletedTask;

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();
    public IReadOnlyList<TimeOption> Hours { get; } = Enumerable.Range(0, 24).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<TimeOption> Minutes { get; } = Enumerable.Range(0, 60).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<WhenEditorOption> WhenOptions { get; } =
    [
        new(WhenEditorMode.Unscheduled, "미지정"),
        new(WhenEditorMode.Today, "Today"),
        new(WhenEditorMode.SpecificDate, "날짜 지정"),
    ];

    // The built-in 반복 presets, in display order. Their RRULEs match what the quick-add parser emits for
    // the same phrases (see BuiltInRules), so a task created by typing "매주 ..." and one set here are
    // interchangeable. A loaded task whose rule isn't one of these (e.g. a parsed 격주/특정 요일 rule) gets
    // a synthetic "custom" entry prepended on open so it round-trips.
    private static readonly RecurrenceEditorOption[] RecurrencePresets =
    [
        new(null, "반복 안 함"),
        new("FREQ=DAILY", "매일"),
        new("FREQ=WEEKLY", "매주"),
        new("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "평일 (월–금)"),
        new("FREQ=MONTHLY", "매월"),
        new("FREQ=YEARLY", "매년"),
    ];

    public ObservableCollection<TaskGroupEditorOption> TaskGroups { get; } = new();
    public ObservableCollection<TagEditorOption> Tags { get; } = new();
    public ObservableCollection<ChecklistItemViewModel> Checklist { get; } = new();
    public ObservableCollection<RecurrenceEditorOption> RecurrenceOptions { get; } = new();
    public Guid? CurrentTaskId => _taskId;

    /// <summary>The recurrence timeline pips for a recurring task, oldest cycle first with the live head
    /// (current/next or terminal) pip last. Empty for a non-recurring task.</summary>
    public ObservableCollection<OccurrencePipViewModel> Timeline { get; } = new();

    /// <summary>True when the open task carries a recurrence rule — gates the timeline strip and the
    /// 반복 종료 action in the panel. Tracks the live 반복 selection so clearing it (to "반복 안 함") turns
    /// the task back into a plain one in the panel at once.</summary>
    [ObservableProperty]
    public partial bool IsRecurring { get; set; }

    /// <summary>True while there are older recorded cycles beyond the realized window — drives the
    /// timeline's "이전 기록" affordance so history pages in rather than loading all at once.</summary>
    [ObservableProperty]
    public partial bool HasOlderTimeline { get; set; }

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

    /// <summary>종일 — a date-only When that carries no meaningful time. Stored explicitly via
    /// <see cref="ScheduledWhen.AllDay"/>; while on, <see cref="WhenTime"/> is null and the time picker is hidden.</summary>
    [ObservableProperty]
    public partial bool IsWhenAllDay { get; set; }

    [ObservableProperty]
    public partial TaskGroupEditorOption? SelectedTaskGroup { get; set; }

    /// <summary>The chosen 반복 (recurrence) preset, or the "반복 안 함" entry for none.</summary>
    [ObservableProperty]
    public partial RecurrenceEditorOption? SelectedRecurrence { get; set; }

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

    public bool IsWhenEditorVisible => HasConcreteWhen;
    // The "+ 날짜 추가" button shows only when there is no concrete date.
    public bool CanAddWhen => !IsWhenEditorVisible;

    public TaskDetailViewModel(
        ITaskStore store,
        ITaskIndex index,
        IReorderService reorder,
        IRecurringTaskService recurrence,
        TimeProvider clock,
        TimeZoneInfo zone,
        Func<Task> refreshOwner,
        INavDataChangeNotifier navNotifier)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
        _recurrence = recurrence;
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
        // A 종일 date carries no time, so don't default one when switching to a dated mode.
        if (value.Mode is WhenEditorMode.Today or WhenEditorMode.SpecificDate && WhenTime is null && !IsWhenAllDay)
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
        if (value)
        {
            WhenTime = null; // 종일 carries no time
            SetWhenTimeEditors(null);
        }
        else if (WhenTime is null)
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
            // A timed date defaults its time to noon; an all-day date keeps no time.
            if (!IsWhenAllDay)
            {
                WhenTime ??= TimeSpan.FromHours(12);
                SetWhenTimeEditors(WhenTime);
            }
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
    partial void OnSelectedRecurrenceChanged(RecurrenceEditorOption? value)
    {
        // Clearing 반복 (선택 "반복 안 함") converts the task back to a plain one in the panel immediately:
        // the timeline strip and 반복 종료 action hide. Re-enabling it shows them again. The autosave
        // persists Recurrence = null (or the chosen rule) like any other metadata edit — it never
        // completes the task or touches the cycle history.
        if (!_isLoading)
            IsRecurring = value?.Rule is not null;
        RequestAutoSave();
    }

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
        // 종일 carries no time; a timed date loads its wall-clock time. The flag is read straight off the
        // domain, not inferred from a sentinel time.
        WhenTime = task.When.IsAllDay ? null : task.When.Date?.ToLocal().TimeOfDay;
        SetWhenTimeEditors(WhenTime);
        IsWhenAllDay = task.When.IsAllDay;
        SelectedWhenOption = OptionFor(task.When);

        _originalWhen = task.When;
        _loadedWhenMode = SelectedWhenOption.Mode;
        _loadedWhenDate = WhenDate;
        _loadedWhenTime = WhenTime;
        _loadedIsWhenAllDay = IsWhenAllDay;

        LoadRecurrence(task.Recurrence);
        _originalRecurrence = task.Recurrence;
        _loadedRecurrenceRule = task.Recurrence?.Rule;

        // Stay in the loading guard until the panel is fully populated — setting SelectedTaskGroup and the
        // tag rows below must not trip autosave (no save should fire just from opening a task).
        await LoadTaskGroupsAsync(task.TaskGroupId);
        await LoadTagsAsync(task.TagIds);
        LoadChecklist(task);

        // The recurrence timeline: a recurring task (open or already-ended) shows its cycle history; a
        // plain task shows none. The most recent page loads now; older cycles page in on demand.
        IsRecurring = task.Recurrence is not null;
        _timelineWindow = TimelinePageSize;
        await LoadTimelineAsync(task);

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
        IsWhenAllDay = true; // new dates start as 종일; uncheck to set a time
        WhenDate = LocalNow();
        SelectedWhenOption = FindOption(WhenEditorMode.SpecificDate);
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
        // 반복 is anchored to the date, so removing the date clears the recurrence too — the 반복 field
        // hides with the When editor (IsWhenEditorVisible) and must not leave a now-meaningless rule behind.
        if (RecurrenceOptions.Count > 0)
            SelectedRecurrence = RecurrenceOptions[0];
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

    /// <summary>Awaits every autosave queued so far across <i>both</i> save paths — metadata edits on
    /// <see cref="_saveChain"/> and checklist edits on <see cref="_checklistChain"/>. A test/diagnostic
    /// seam: production code fires autosaves and forgets them (the chains keep each path ordered); only
    /// callers that need to observe the persisted result deterministically — or a task switch/close that
    /// must not strand a pending write — await this. Each chain's tail completes only after every link
    /// before it, so this drains both queues in full. Faults are swallowed (each save logs its own).</summary>
    internal async Task DrainPendingSaveAsync()
    {
        var metadata = _saveChain;
        var checklist = _checklistChain;
        try { await metadata; } catch { /* logged by the failing save */ }
        try { await checklist; } catch { /* logged by the failing save */ }
    }

    /// <summary>Captures the panel's current edits as an immutable snapshot, or <c>null</c> when no task is
    /// open. Reads every field (and resolves When via <see cref="BuildWhen"/>) on the calling thread so the
    /// values can't shift under a later save.</summary>
    private TaskEditSnapshot? CaptureSnapshot()
    {
        if (_taskId is not { } id) return null;
        var when = BuildWhen();
        return new TaskEditSnapshot(
            id,
            Title.Trim(),
            string.IsNullOrWhiteSpace(Notes) ? null : Notes,
            SelectedPriority,
            when,
            BuildRecurrence(when),
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
            // Read-modify-write the metadata fields atomically: MutateAsync re-reads the live record
            // under the store's write lock and the closure replaces only the snapshot's fields. The
            // embedded checklist is left untouched, so a checklist save that committed in between is
            // preserved rather than overwritten with the copy this save happened to load.
            var saved = await _store.MutateAsync<TaskItem>(snapshot.Id, task =>
            {
                task.Title = snapshot.Title;
                task.Notes = snapshot.Notes;
                task.Priority = snapshot.Priority;
                task.When = snapshot.When;
                task.Recurrence = snapshot.Recurrence;
                task.TaskGroupId = snapshot.TaskGroupId;
                task.TagIds = snapshot.TagIds.ToList();
                return true;
            });
            if (saved is not null)
            {
                await _refreshOwner();
                // The save may have moved the task between groups or changed its tags, shifting the
                // sidebar counts. Counts-only signal (cheap to recompute) — the group/tag set is unchanged.
                _navNotifier.NotifyCountsChanged();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cue] Detail autosave failed: {ex.Message}");
        }
    }

    /// <summary>Appends a checklist item to the open task (title from the inline box), persists the
    /// parent, and adds the matching row to the panel without a full reload (so focus is undisturbed).
    /// Runs on the serial checklist chain so it can't interleave with a tick/title save, and persists
    /// through the store's atomic MutateAsync so it appends to the latest checklist on disk.</summary>
    [RelayCommand]
    private Task AddChecklistItemAsync()
    {
        if (_taskId is not { } id || string.IsNullOrWhiteSpace(NewChecklistItemTitle)) return Task.CompletedTask;
        var item = new ChecklistItem { Title = NewChecklistItemTitle.Trim() };
        return EnqueueChecklistOp(async () =>
        {
            var saved = await _store.MutateAsync<TaskItem>(id, task => { task.Checklist.Add(item); return true; });
            if (saved is null) return;
            Checklist.Add(CreateChecklistRow(item));
            NewChecklistItemTitle = string.Empty;
            await _refreshOwner();
        });
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
            // New tags land at the top of the sidebar tag list (matches sidebar creation).
            SortOrder = _reorder.PrependRank(existing.Select(item => item.SortOrder)),
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
        // A deleted open task drops out of its group/tag counts in the sidebar.
        _navNotifier.NotifyCountsChanged();
    }

    // Recurrence timeline + series lifecycle

    /// <summary>
    /// Rebuilds the recurrence timeline from <paramref name="task"/>: the most recent page of recorded
    /// cycles (oldest-first), then the live head pip — the current/next cycle for an open series, or a
    /// terminal "종료" pip once the series has ended. A plain task clears the strip. Older cycles beyond the
    /// realized window page in via <see cref="LoadOlderTimelineAsync"/>, so a long history is never loaded
    /// all at once.
    /// </summary>
    private async Task LoadTimelineAsync(TaskItem task)
    {
        Timeline.Clear();
        if (task.Recurrence is null)
        {
            HasOlderTimeline = false;
            return;
        }

        var total = await _index.GetOccurrenceCountAsync(task.Id);
        var window = Math.Min(_timelineWindow, total);
        HasOlderTimeline = window < total;

        // The index returns cycles most-recent-first; render oldest-first so the strip reads left→right
        // with the live head pip on the right.
        var records = window > 0
            ? await _index.GetOccurrencesAsync(task.Id, window)
            : (IReadOnlyList<OccurrenceListItem>)Array.Empty<OccurrenceListItem>();
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];
            var completedLocal = record.CompletedAt is { } at ? TimeZoneInfo.ConvertTime(at, _zone) : (DateTimeOffset?)null;
            Timeline.Add(new OccurrencePipViewModel(record.Id, record.OccurrenceDate, MapPipKind(record.Status), completedLocal));
        }

        Timeline.Add(BuildHeadPip(task));
    }

    /// <summary>The live head pip: the series' own current/next cycle (◉ 다음) while open, or a terminal
    /// 종료 pip once the series has been ended/exhausted. Synthesized from the series, not a record, so it
    /// carries no occurrence id and cannot be edited as a past cycle.</summary>
    private OccurrencePipViewModel BuildHeadPip(TaskItem task)
    {
        var headDate = task.When.Date is { } when
            ? DateOnly.FromDateTime(when.ToLocal().DateTime)
            : DateOnly.FromDateTime(task.Recurrence!.Anchor.ToLocal().DateTime);

        if (task.IsCompleted)
        {
            var completedLocal = task.CompletedAt is { } at ? TimeZoneInfo.ConvertTime(at, _zone) : (DateTimeOffset?)null;
            return new OccurrencePipViewModel(null, headDate, OccurrencePipKind.Ended, completedLocal);
        }
        return new OccurrencePipViewModel(null, headDate, OccurrencePipKind.Next, null);
    }

    private static OccurrencePipKind MapPipKind(OccurrenceStatus status) => status switch
    {
        OccurrenceStatus.Completed => OccurrencePipKind.Completed,
        OccurrenceStatus.Skipped => OccurrencePipKind.Skipped,
        OccurrenceStatus.Missed => OccurrencePipKind.Missed,
        _ => OccurrencePipKind.Missed,
    };

    /// <summary>Pages the next older batch of recorded cycles into the timeline.</summary>
    [RelayCommand]
    private async Task LoadOlderTimelineAsync()
    {
        if (_taskId is not { } id) return;
        _timelineWindow += TimelinePageSize;
        if (await _store.GetAsync<TaskItem>(id) is { } task)
            await LoadTimelineAsync(task);
    }

    /// <summary>Loads one cycle's full record (its completion time and frozen checklist snapshot) for the
    /// per-cycle flyout. Lazy — only the opened pip's snapshot is read, never the whole history's.</summary>
    public Task<RecurrenceOccurrence?> GetOccurrenceAsync(Guid occurrenceId)
        => _store.GetAsync<RecurrenceOccurrence>(occurrenceId);

    /// <summary>Re-classifies one past cycle from its flyout (완료/건너뜀/미수행) and rebuilds the timeline.
    /// Editing history never moves the series' next scheduled cycle, so only the strip refreshes — the
    /// open lists are untouched.</summary>
    public async Task UpdateOccurrenceStatusAsync(Guid occurrenceId, OccurrenceStatus status)
    {
        await _recurrence.UpdateOccurrenceStatusAsync(occurrenceId, status);
        if (_taskId is { } id && await _store.GetAsync<TaskItem>(id) is { } task)
            await LoadTimelineAsync(task);
    }

    /// <summary>이번 회차 건너뛰기 — records the current cycle as 건너뜀 and advances the series, leaving it
    /// open. Refreshes the owning list and the panel (the head pip rolls to the next cycle, a new 건너뜀 pip
    /// joins the history).</summary>
    [RelayCommand]
    private async Task SkipCurrentAsync()
    {
        if (_taskId is not { } id) return;
        await FlushAsync();
        await _recurrence.SkipAsync(id, _clock.GetUtcNow());
        await _refreshOwner();
        _navNotifier.NotifyCountsChanged();
        await OpenAsync(id); // reload the advanced cycle + the new history pip
    }

    /// <summary>반복 종료 — stops the recurrence so the task becomes a plain, still-open one at its current
    /// cycle. It is deliberately not a completion ("완료"는 현재 회차에만): the panel stays open and is
    /// reloaded as a non-recurring task (the timeline and 반복 종료 affordances drop). History is preserved.</summary>
    [RelayCommand]
    private async Task EndSeriesAsync()
    {
        if (_taskId is not { } id) return;
        await FlushAsync();
        await _recurrence.EndSeriesAsync(id);
        await _refreshOwner();
        await OpenAsync(id); // reload as a plain task — timeline/반복 종료 hide
    }

    /// <summary>Removes one checklist item from the open task, persists the parent, and drops its row
    /// from the panel. Embedded items have no tombstone — they are removed outright.</summary>
    [RelayCommand]
    private Task DeleteChecklistItemAsync(Guid id)
    {
        if (_taskId is not { } taskId) return Task.CompletedTask;
        return EnqueueChecklistOp(async () =>
        {
            var saved = await _store.MutateAsync<TaskItem>(taskId, task => { task.Checklist.RemoveAll(item => item.Id == id); return true; });
            if (saved is null) return;
            if (Checklist.FirstOrDefault(item => item.Id == id) is { } row) Checklist.Remove(row);
            await _refreshOwner();
        });
    }

    /// <summary>Persists the checklist after a tick / title edit: captures the panel's current rows and
    /// replaces the parent task's checklist with them through the store's atomic MutateAsync (which
    /// preserves metadata edited concurrently). Does not reload the rows, so an in-progress edit keeps
    /// focus. The row snapshot is captured synchronously by the caller before the chained save runs, so
    /// a task switch that repopulates the collection can't make this write one task's items onto
    /// another. Serialized on the checklist chain; failures are logged, not thrown.</summary>
    private void QueueChecklistSave()
    {
        if (_taskId is not { } id) return;
        // Snapshot the rows synchronously (on the UI thread, before any await) so the values are bound
        // to this edit and can't shift under a later save or a task switch.
        var snapshot = Checklist.Select(row => new ChecklistItem
        {
            Id = row.Id,
            Title = row.Title.Trim(),
            IsChecked = row.IsChecked,
        }).ToList();
        _ = EnqueueChecklistOp(async () =>
        {
            var saved = await _store.MutateAsync<TaskItem>(id, task => { task.Checklist = snapshot; return true; });
            if (saved is not null) await _refreshOwner();
        });
    }

    /// <summary>Appends one checklist operation to the serial checklist chain and returns its completion.
    /// Awaiting the previous link first keeps add/remove/tick/title writes strictly ordered; a prior
    /// link's failure is swallowed here so it can't stall the queue behind it (each op logs its own).
    /// Called on the UI thread, so the op's store write and owner refresh resume on the UI context.</summary>
    private Task EnqueueChecklistOp(Func<Task> op)
    {
        var next = ChainChecklistAsync(_checklistChain, op);
        _checklistChain = next;
        return next;
    }

    private static async Task ChainChecklistAsync(Task previous, Func<Task> op)
    {
        try { await previous; } catch { /* logged by the failing op */ }
        try { await op(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Cue] Checklist save failed: {ex.Message}"); }
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
            WhenEditorMode.Today => DatedWhen(LocalNow()),
            WhenEditorMode.SpecificDate when WhenDate is { } date => DatedWhen(date),
            WhenEditorMode.SpecificDate => ScheduledWhen.Unscheduled,
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    /// <summary>
    /// Resolves the panel's 반복 selection to a <see cref="RecurrenceRule"/> (or <c>null</c> for none),
    /// anchoring on the task's own date. The original rule is returned verbatim only when <i>both</i> the
    /// rule string and the When are unchanged from what loaded — that is the metadata-only edit (priority,
    /// notes, …) where re-anchoring would needlessly shift the series. The moment the user moves the task's
    /// date or time, the anchor must follow: the anchor drives the series' cadence and wall-clock (a weekly
    /// rule with no BYDAY repeats on the anchor's weekday at the anchor's time), so a stale anchor would
    /// make the next occurrence land on the old day/time and contradict the edit. A re-anchored or newly
    /// chosen rule anchors on the task's When date if it has one, else on today (so a dateless recurring
    /// task still has a valid evaluable anchor).
    /// </summary>
    private RecurrenceRule? BuildRecurrence(ScheduledWhen when)
    {
        var rule = SelectedRecurrence?.Rule;
        if (rule == _loadedRecurrenceRule && when == _originalWhen)
            return _originalRecurrence;
        if (rule is null)
            return null;

        var anchor = when.Date ?? StartOfTodayAnchor();
        return new RecurrenceRule(rule, anchor);
    }

    /// <summary>A zoned anchor at local start of day today — the fallback recurrence anchor for a task
    /// that has no When date of its own.</summary>
    private ZonedDateTime StartOfTodayAnchor()
    {
        var now = LocalNow();
        return ZonedDateTime.FromLocal(new DateTime(now.Year, now.Month, now.Day), _zone.Id);
    }

    /// <summary>Builds the OnDate When for a chosen day: an all-day (종일) date pinned to local start of
    /// day with the 종일 flag, or a timed date pinned to the chosen wall-clock time.</summary>
    private ScheduledWhen DatedWhen(DateTimeOffset selected)
        => IsWhenAllDay
            ? ScheduledWhen.AllDay(ZonedDateTime.FromLocal(
                new DateTime(selected.Year, selected.Month, selected.Day), _zone.Id))
            : ScheduledWhen.On(PinDate(selected, WhenTime));

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

    /// <summary>
    /// Rebuilds the 반복 option list and selects the entry matching the loaded task. The built-in presets
    /// always lead; a recurring task whose rule isn't one of them (e.g. a parsed 격주/특정 요일 rule) gets a
    /// synthetic entry — labelled with a readable Korean summary — prepended after "반복 안 함", selected,
    /// so its exact rule round-trips even though it has no preset.
    /// </summary>
    private void LoadRecurrence(RecurrenceRule? recurrence)
    {
        RecurrenceOptions.Clear();
        foreach (var preset in RecurrencePresets)
            RecurrenceOptions.Add(preset);

        var rule = recurrence?.Rule;
        var match = RecurrenceOptions.FirstOrDefault(option => option.Rule == rule);
        if (match is null && rule is not null)
        {
            // Insert the custom rule right after "반복 안 함" so it reads as an alternative to the presets.
            match = new RecurrenceEditorOption(rule, RecurrenceSummary(rule));
            RecurrenceOptions.Insert(1, match);
        }
        SelectedRecurrence = match ?? RecurrenceOptions[0];
    }

    /// <summary>A short Korean summary of an RRULE, for labelling a non-preset rule in the picker. Covers
    /// the shapes the quick-add parser emits; an unrecognized rule falls back to a generic "반복" label.</summary>
    private static string RecurrenceSummary(string rule)
    {
        var parts = rule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) fields[kv[0]] = kv[1];
        }

        fields.TryGetValue("FREQ", out var freq);
        fields.TryGetValue("INTERVAL", out var intervalText);
        var interval = int.TryParse(intervalText, out var n) ? n : 1;

        switch (freq?.ToUpperInvariant())
        {
            case "DAILY":
                return interval > 1 ? $"{interval}일마다" : "매일";
            case "WEEKLY":
            {
                var days = fields.TryGetValue("BYDAY", out var byDay) ? KoreanDays(byDay) : null;
                var every = interval > 1 ? "격주" : "매주";
                if (byDay == "MO,TU,WE,TH,FR") return "평일 (월–금)";
                return days is null ? every : $"{every} {days}";
            }
            case "MONTHLY":
                return fields.TryGetValue("BYMONTHDAY", out var dom) ? $"매월 {dom}일" : "매월";
            case "YEARLY":
                return "매년";
            case "MINUTELY":
                return $"{interval}분마다";
            case "HOURLY":
                return $"{interval}시간마다";
            default:
                return "반복";
        }
    }

    private static string KoreanDays(string byDay)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MO"] = "월", ["TU"] = "화", ["WE"] = "수", ["TH"] = "목",
            ["FR"] = "금", ["SA"] = "토", ["SU"] = "일",
        };
        var labels = byDay
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => names.TryGetValue(code, out var label) ? label : code);
        return string.Join("·", labels) + "요일";
    }

    /// <summary>Fills the checklist rows straight from the loaded task (the file source of truth) — the
    /// embedded list needs no index query.</summary>
    private void LoadChecklist(TaskItem task)
    {
        Checklist.Clear();
        foreach (var item in task.Checklist)
            Checklist.Add(CreateChecklistRow(item));
    }

    // A tick/title edit queues a checklist save on the serial chain (captured synchronously here, so it
    // records the row's edited values). The chain tail is tracked in _checklistChain, so the save is no
    // longer truly fire-and-forget — DrainPendingSaveAsync/FlushAsync wait for it on switch and close.
    private ChecklistItemViewModel CreateChecklistRow(ChecklistItem item)
        => new(item, changed => QueueChecklistSave());
}
