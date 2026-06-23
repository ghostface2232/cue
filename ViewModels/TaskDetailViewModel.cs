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
public sealed record ProjectEditorOption(Guid? Id, string Name);
public sealed record TimeOption(int Value, string Label);

public partial class LabelEditorOption : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }

    /// <summary>The label's hex color (e.g. "#3498DB"), or <c>null</c> for the default.</summary>
    public string? Color { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public LabelEditorOption(Guid id, string name, bool isSelected, string? color = null)
    {
        Id = id;
        Name = name;
        IsSelected = isSelected;
        Color = color;
    }
}

public partial class SubtaskRowViewModel : ObservableObject
{
    private readonly Action<SubtaskRowViewModel> _onToggled;
    private bool _suppressToggle;

    public Guid Id { get; }
    public string Title { get; }
    public double VisualOpacity => IsCompleted ? 0.48 : 1.0;

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    public SubtaskRowViewModel(TaskListItem item, Action<SubtaskRowViewModel> onToggled)
    {
        Id = item.Id;
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(제목 없음)" : item.Title;
        _onToggled = onToggled;
        _suppressToggle = true;
        IsCompleted = item.IsCompleted;
        _suppressToggle = false;
    }

    partial void OnIsCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(VisualOpacity));
        if (!_suppressToggle) _onToggled(this);
    }

    public void SetCompletedSilently(bool value)
    {
        _suppressToggle = true;
        IsCompleted = value;
        _suppressToggle = false;
    }
}

/// <summary>
/// Edits one full <see cref="TaskItem"/>. Detail reads use the file source of truth by id; option
/// and subtask lists use <see cref="ITaskIndex"/>. Every mutation returns through
/// <see cref="ITaskStore"/> and then asks the owning list to re-query its current index view.
/// A task has a single date (When) — there is no separate deadline.
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
    private readonly Func<Guid, Task> _openTask;
    private Guid? _taskId;
    private ScheduledWhen _originalWhen = ScheduledWhen.Unscheduled;
    private WhenEditorMode _loadedWhenMode;
    private DateTimeOffset? _loadedWhenDate;
    private TimeSpan? _loadedWhenTime;
    private bool _loadedIsWhenAllDay;
    private bool _isLoading;

    // Serializes autosaves so a fast run of edits can't interleave or reorder their writes — the same
    // pattern the list uses for completion toggles. Distinct from <see cref="_isLoading"/> (which
    // suppresses saves while OpenAsync fills the panel) and <see cref="_suppressAutoSave"/> (which
    // coalesces a single user action that touches several properties into one save).
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _suppressAutoSave;

    // Tracks the most recently requested autosave so it can be awaited deterministically (tests).
    private Task _pendingAutoSave = Task.CompletedTask;

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();
    public IReadOnlyList<TimeOption> Hours { get; } = Enumerable.Range(0, 24).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<TimeOption> Minutes { get; } = Enumerable.Range(0, 60).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<WhenEditorOption> WhenOptions { get; } =
    [
        new(WhenEditorMode.Unscheduled, "미지정"),
        new(WhenEditorMode.Today, "Today"),
        new(WhenEditorMode.SpecificDate, "날짜 지정"),
    ];

    public ObservableCollection<ProjectEditorOption> Projects { get; } = new();
    public ObservableCollection<LabelEditorOption> Labels { get; } = new();
    public ObservableCollection<SubtaskRowViewModel> Subtasks { get; } = new();
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
    public partial ProjectEditorOption? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string NewSubtaskTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Guid? ParentTaskId { get; set; }

    public bool IsSpecificDate => SelectedWhenOption.Mode == WhenEditorMode.SpecificDate;
    public bool HasConcreteWhen => SelectedWhenOption.Mode is WhenEditorMode.Today or WhenEditorMode.SpecificDate;

    /// <summary>Time picker shows only with a concrete date that is not all-day.</summary>
    public bool ShowWhenTime => HasConcreteWhen && !IsWhenAllDay;

    /// <summary>End-of-day time a 종일 (all-day) item is pinned to so it expires at 23:59 local.</summary>
    private static readonly TimeSpan AllDayTime = new(23, 59, 0);
    private TimeSpan? EffectiveWhenTime => IsWhenAllDay ? AllDayTime : WhenTime;
    public bool HasParentTask => ParentTaskId is not null;
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
        Func<Guid, Task> openTask)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
        _recurrence = recurrence;
        _clock = clock;
        _zone = zone;
        _refreshOwner = refreshOwner;
        _openTask = openTask;
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
    partial void OnSelectedProjectChanged(ProjectEditorOption? value) => RequestAutoSave();

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
        ParentTaskId = task.ParentTaskId;
        OnPropertyChanged(nameof(HasParentTask));
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

        // Stay in the loading guard until the panel is fully populated — setting SelectedProject and the
        // label rows below must not trip autosave (no save should fire just from opening a task).
        await LoadProjectsAsync(task.ProjectId);
        await LoadLabelsAsync(task.LabelIds);
        await LoadSubtasksAsync(task.Id);
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
    /// hasn't been committed by a focus-out yet. The detail panel autosaves on every change, so the
    /// close button calls this only to catch a title/notes edit whose <c>LostFocus</c> hasn't fired.
    /// </summary>
    public Task FlushAsync() => AutoSaveAsync();

    /// <summary>
    /// Fire-and-forget autosave for single-selection changes (priority, When, project, label toggles).
    /// A no-op while OpenAsync is filling the panel (<see cref="_isLoading"/>) or while a multi-property
    /// action is coalescing its writes (<see cref="_suppressAutoSave"/>); the actual save is serialized
    /// through <see cref="_saveGate"/> so overlapping edits can't reorder their writes.
    /// </summary>
    private void RequestAutoSave()
    {
        if (_isLoading || _suppressAutoSave) return;
        _pendingAutoSave = AutoSaveAsync();
    }

    /// <summary>Awaits the most recently requested autosave. A test/diagnostic seam: production code
    /// fires autosaves and forgets them (the gate keeps them ordered); only callers that need to
    /// observe the persisted result deterministically await this.</summary>
    internal Task DrainPendingSaveAsync() => _pendingAutoSave;

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
    /// The single autosave path, reused by every trigger and by the close flush. Reads the live record,
    /// applies the panel's fields, and saves through <see cref="ITaskStore"/> (which stamps UpdatedAt).
    /// The dirty-check in <see cref="BuildWhen"/> means a save that never touched the date preserves the
    /// original When's exact instant and time zone. A failed save is logged, not swallowed silently, and
    /// left for the next change or the close flush to retry.
    /// </summary>
    private async Task AutoSaveAsync()
    {
        if (_isLoading || !IsOpen || _taskId is not { } id) return;

        await _saveGate.WaitAsync();
        try
        {
            var task = await _store.GetAsync<TaskItem>(id);
            if (task is null || task.IsDeleted) return;

            task.Title = Title.Trim();
            task.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes;
            task.Priority = SelectedPriority;
            task.When = BuildWhen();
            task.ProjectId = SelectedProject?.Id;
            task.LabelIds = Labels.Where(label => label.IsSelected && label.Id != Guid.Empty).Select(label => label.Id).ToList();

            await _store.SaveAsync(task);
            await _refreshOwner();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Cue] Detail autosave failed: {ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    [RelayCommand]
    private async Task AddSubtaskAsync()
    {
        if (_taskId is not { } parentId || string.IsNullOrWhiteSpace(NewSubtaskTitle)) return;
        var parent = await _store.GetAsync<TaskItem>(parentId);
        if (parent is null || parent.IsDeleted) return;

        var siblings = await _index.GetSubtasksAsync(parent.Id);
        var child = new TaskItem
        {
            Title = NewSubtaskTitle.Trim(),
            ParentTaskId = parent.Id,
            ProjectId = parent.ProjectId,
            SortOrder = _reorder.AppendRank(siblings.Select(item => item.SortOrder)),
        };
        await _store.SaveAsync(child);
        NewSubtaskTitle = string.Empty;
        await _refreshOwner();
        await LoadSubtasksAsync(parentId);
    }

    [RelayCommand]
    private async Task AddLabelAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = await _index.GetLabelsAsync();
        var label = new Label
        {
            Name = name.Trim(),
            Color = LabelColors.ForNewLabel(existing.Count),
            SortOrder = _reorder.AppendRank(existing.Select(item => item.SortOrder)),
        };
        await _store.SaveAsync(label);
        var selected = Labels.Where(item => item.IsSelected && item.Id != Guid.Empty).Select(item => item.Id).Append(label.Id);
        await LoadLabelsAsync(selected);
        // A newly created tag is selected on the spot — persist the task's tag assignment too.
        await AutoSaveAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSubtaskAsync(SubtaskRowViewModel row)
    {
        var completed = row.IsCompleted;
        try
        {
            if (completed)
            {
                // Recurring subtasks advance and leave a Logbook copy just like top-level tasks.
                await _recurrence.CompleteAsync(row.Id, _clock.GetUtcNow());
            }
            else
            {
                var task = await _store.GetAsync<TaskItem>(row.Id);
                if (task is null || task.IsDeleted) return;
                task.CompletedAt = null;
                await _store.SaveAsync(task);
            }
            await _refreshOwner();
            if (_taskId is { } parentId) await LoadSubtasksAsync(parentId);
        }
        catch
        {
            row.SetCompletedSilently(!completed);
        }
    }

    [RelayCommand]
    private async Task DeleteSubtaskAsync(Guid id)
    {
        await _store.DeleteAsync<TaskItem>(id);
        await _refreshOwner();
        if (_taskId is { } parentId) await LoadSubtasksAsync(parentId);
    }

    [RelayCommand]
    private Task OpenSubtaskAsync(Guid id) => _openTask(id);

    [RelayCommand]
    private Task OpenParentAsync()
        => ParentTaskId is { } id ? _openTask(id) : Task.CompletedTask;

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

    private async Task LoadProjectsAsync(Guid? selectedId)
    {
        Projects.Clear();
        Projects.Add(new ProjectEditorOption(null, "Cue"));
        foreach (var project in await _index.GetProjectsAsync())
            Projects.Add(new ProjectEditorOption(project.Id, project.Name));

        if (selectedId is { } id && Projects.All(option => option.Id != id))
        {
            var inactive = await _store.GetAsync<Project>(id);
            if (inactive is not null && !inactive.IsDeleted)
                Projects.Add(new ProjectEditorOption(inactive.Id, inactive.Name));
        }
        SelectedProject = Projects.FirstOrDefault(option => option.Id == selectedId) ?? Projects[0];
    }

    private async Task LoadLabelsAsync(IEnumerable<Guid> selectedIds)
    {
        var selected = selectedIds.Where(id => id != Guid.Empty).ToHashSet();
        Labels.Clear();
        // A synthetic "태그 없음" row (Guid.Empty) makes the no-tag state explicit and is checked by
        // default; it stays mutually exclusive with the real tags via ToggleLabel/SyncNoLabelOption.
        Labels.Add(new LabelEditorOption(Guid.Empty, "태그 없음", selected.Count == 0));
        foreach (var label in await _index.GetLabelsAsync())
            Labels.Add(new LabelEditorOption(label.Id, label.Name, selected.Contains(label.Id), label.Color));
    }

    /// <summary>Toggles a label row. The "라벨 없음" entry (Guid.Empty) behaves like a reset: choosing it
    /// clears every real label; choosing a real label clears it; and it re-checks on its own once no
    /// real label remains selected.</summary>
    public void ToggleLabel(Guid id)
    {
        var target = Labels.FirstOrDefault(label => label.Id == id);
        if (target is null) return;

        if (id == Guid.Empty)
        {
            foreach (var label in Labels) label.IsSelected = label.Id == Guid.Empty;
        }
        else
        {
            target.IsSelected = !target.IsSelected;
            SyncNoLabelOption();
        }
        RequestAutoSave();
    }

    private void SyncNoLabelOption()
    {
        var none = Labels.FirstOrDefault(label => label.Id == Guid.Empty);
        if (none is not null)
            none.IsSelected = Labels.All(label => label.Id == Guid.Empty || !label.IsSelected);
    }

    private async Task LoadSubtasksAsync(Guid parentId)
    {
        Subtasks.Clear();
        foreach (var item in await _index.GetSubtasksAsync(parentId))
            Subtasks.Add(new SubtaskRowViewModel(item, row => ToggleSubtaskCommand.Execute(row)));
    }
}
