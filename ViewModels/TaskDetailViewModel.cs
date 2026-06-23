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
    ThisEvening,
    SpecificDate,
    Someday,
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
    private ZonedDateTime? _originalDeadline;
    private WhenEditorMode _loadedWhenMode;
    private DateTimeOffset? _loadedWhenDate;
    private TimeSpan? _loadedWhenTime;
    private bool _loadedIsEvening;
    private bool _loadedIsWhenAllDay;
    private DateTimeOffset? _loadedDeadlineDate;
    private TimeSpan? _loadedDeadlineTime;
    private bool _loadedIsDeadlineAllDay;
    private bool _isLoading;

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();
    public IReadOnlyList<TimeOption> Hours { get; } = Enumerable.Range(0, 24).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<TimeOption> Minutes { get; } = Enumerable.Range(0, 60).Select(value => new TimeOption(value, value.ToString("00"))).ToArray();
    public IReadOnlyList<WhenEditorOption> WhenOptions { get; } =
    [
        new(WhenEditorMode.Unscheduled, "미지정"),
        new(WhenEditorMode.Today, "Today"),
        new(WhenEditorMode.ThisEvening, "This Evening"),
        new(WhenEditorMode.SpecificDate, "날짜 지정"),
        new(WhenEditorMode.Someday, "Someday"),
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

    [ObservableProperty]
    public partial bool IsEvening { get; set; }

    [ObservableProperty]
    public partial bool IsSomeday { get; set; }

    /// <summary>종일 — a date-only When that carries no time (pinned to end of day, 23:59).</summary>
    [ObservableProperty]
    public partial bool IsWhenAllDay { get; set; }

    /// <summary>종일 — a date-only Deadline that carries no time (pinned to end of day, 23:59).</summary>
    [ObservableProperty]
    public partial bool IsDeadlineAllDay { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DeadlineDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? DeadlineTime { get; set; }

    [ObservableProperty]
    public partial TimeOption? SelectedDeadlineHour { get; set; }

    [ObservableProperty]
    public partial TimeOption? SelectedDeadlineMinute { get; set; }

    [ObservableProperty]
    public partial ProjectEditorOption? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string NewSubtaskTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Guid? ParentTaskId { get; set; }

    public bool IsSpecificDate => SelectedWhenOption.Mode == WhenEditorMode.SpecificDate;
    public bool HasConcreteWhen => SelectedWhenOption.Mode is WhenEditorMode.Today or WhenEditorMode.ThisEvening or WhenEditorMode.SpecificDate;
    public bool HasDeadline => DeadlineDate is not null;

    /// <summary>Time pickers show only with a concrete date that is neither all-day nor evening
    /// (evening implies an unspecified evening time, so a numeric clock would be misleading).</summary>
    public bool ShowWhenTime => HasConcreteWhen && !IsWhenAllDay && !IsEvening;
    public bool ShowDeadlineTime => HasDeadline && !IsDeadlineAllDay;

    /// <summary>End-of-day time a 종일 (all-day) item is pinned to so it expires at 23:59 local.</summary>
    private static readonly TimeSpan AllDayTime = new(23, 59, 0);
    private TimeSpan? EffectiveWhenTime => IsWhenAllDay ? AllDayTime : WhenTime;
    private TimeSpan? EffectiveDeadlineTime => IsDeadlineAllDay ? AllDayTime : DeadlineTime;
    public bool HasParentTask => ParentTaskId is not null;
    public bool IsWhenEditorVisible => SelectedWhenOption.Mode != WhenEditorMode.Unscheduled;
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
        if (value.Mode == WhenEditorMode.SpecificDate && WhenDate is null)
            WhenDate = LocalNow();
        if (value.Mode is WhenEditorMode.Today or WhenEditorMode.ThisEvening or WhenEditorMode.SpecificDate && WhenTime is null)
            WhenTime = TimeSpan.FromHours(12);
        if (value.Mode == WhenEditorMode.ThisEvening) IsEvening = true;
        else if (value.Mode != WhenEditorMode.SpecificDate) IsEvening = false;
        OnPropertyChanged(nameof(IsSpecificDate));
        OnPropertyChanged(nameof(HasConcreteWhen));
        OnPropertyChanged(nameof(ShowWhenTime));
        OnPropertyChanged(nameof(IsWhenEditorVisible));
        OnPropertyChanged(nameof(CanAddWhen));
    }

    partial void OnIsWhenAllDayChanged(bool value)
    {
        if (value)
        {
            IsEvening = false; // 종일 and 저녁 are mutually exclusive
        }
        else if (WhenTime == AllDayTime)
        {
            WhenTime = TimeSpan.FromHours(12); // leaving all-day → restore a sensible editable time
            SetWhenTimeEditors(WhenTime);
        }
        OnPropertyChanged(nameof(ShowWhenTime));
    }

    partial void OnIsDeadlineAllDayChanged(bool value)
    {
        if (!value && DeadlineTime == AllDayTime)
        {
            DeadlineTime = TimeSpan.FromHours(12);
            SetDeadlineTimeEditors(DeadlineTime);
        }
        OnPropertyChanged(nameof(ShowDeadlineTime));
    }

    partial void OnIsEveningChanged(bool value)
    {
        if (value) IsWhenAllDay = false; // 저녁 implies an evening time, not all-day
        OnPropertyChanged(nameof(ShowWhenTime));
    }

    partial void OnWhenDateChanged(DateTimeOffset? value)
    {
        if (_isLoading) return;
        if (value is not null)
        {
            IsSomeday = false;
            SelectedWhenOption = FindOption(WhenEditorMode.SpecificDate);
            WhenTime ??= TimeSpan.FromHours(12);
            SetWhenTimeEditors(WhenTime);
        }
        else if (!IsSomeday)
        {
            SelectedWhenOption = FindOption(WhenEditorMode.Unscheduled);
        }
    }

    partial void OnIsSomedayChanged(bool value)
    {
        if (_isLoading) return;
        if (value)
        {
            WhenDate = null;
            IsEvening = false;
            SelectedWhenOption = FindOption(WhenEditorMode.Someday);
        }
        else if (SelectedWhenOption.Mode == WhenEditorMode.Someday)
        {
            SelectedWhenOption = FindOption(WhenEditorMode.Unscheduled);
        }
    }

    partial void OnDeadlineDateChanged(DateTimeOffset? value)
    {
        if (value is not null && DeadlineTime is null)
            DeadlineTime = TimeSpan.FromHours(12);
        if (value is not null)
            SetDeadlineTimeEditors(DeadlineTime);
        if (value is null)
            IsDeadlineAllDay = false;
        OnPropertyChanged(nameof(HasDeadline));
        OnPropertyChanged(nameof(ShowDeadlineTime));
    }

    partial void OnSelectedWhenHourChanged(TimeOption? value) => SyncWhenTimeFromParts();
    partial void OnSelectedWhenMinuteChanged(TimeOption? value) => SyncWhenTimeFromParts();
    partial void OnSelectedDeadlineHourChanged(TimeOption? value) => SyncDeadlineTimeFromParts();
    partial void OnSelectedDeadlineMinuteChanged(TimeOption? value) => SyncDeadlineTimeFromParts();

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
        DeadlineDate = task.Deadline?.ToLocal();
        DeadlineTime = task.Deadline?.ToLocal().TimeOfDay;
        SetWhenTimeEditors(WhenTime);
        SetDeadlineTimeEditors(DeadlineTime);
        IsEvening = task.When.IsEvening;
        IsSomeday = task.When.Kind == WhenKind.SomeDay;
        // A date-only (종일) item was pinned to 23:59 with no evening flag — recognize it on load.
        IsWhenAllDay = task.When.Date is not null && !task.When.IsEvening && WhenTime == AllDayTime;
        IsDeadlineAllDay = task.Deadline is not null && DeadlineTime == AllDayTime;
        SelectedWhenOption = OptionFor(task.When);

        _originalWhen = task.When;
        _originalDeadline = task.Deadline;
        _loadedWhenMode = SelectedWhenOption.Mode;
        _loadedWhenDate = WhenDate;
        _loadedWhenTime = WhenTime;
        _loadedIsEvening = IsEvening;
        _loadedIsWhenAllDay = IsWhenAllDay;
        _loadedDeadlineDate = DeadlineDate;
        _loadedDeadlineTime = DeadlineTime;
        _loadedIsDeadlineAllDay = IsDeadlineAllDay;
        _isLoading = false;

        await LoadProjectsAsync(task.ProjectId);
        await LoadLabelsAsync(task.LabelIds);
        await LoadSubtasksAsync(task.Id);
        IsOpen = true;
    }

    public void Close()
    {
        _taskId = null;
        IsOpen = false;
    }

    public void SetWhenTime(TimeSpan time)
    {
        WhenTime = time;
        SetWhenTimeEditors(time);
    }

    public void SetDeadlineTime(TimeSpan time)
    {
        DeadlineTime = time;
        SetDeadlineTimeEditors(time);
    }

    public void EnableWhenEditor()
    {
        WhenDate = LocalNow();
        WhenTime ??= TimeSpan.FromHours(12);
        SetWhenTimeEditors(WhenTime);
        SelectedWhenOption = FindOption(WhenEditorMode.SpecificDate);
    }

    public void ClearWhen()
    {
        IsSomeday = false;
        IsEvening = false;
        IsWhenAllDay = false;
        WhenDate = null;
        WhenTime = null;
        SelectedWhenHour = null;
        SelectedWhenMinute = null;
        SelectedWhenOption = FindOption(WhenEditorMode.Unscheduled);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_taskId is not { } id) return;
        var task = await _store.GetAsync<TaskItem>(id);
        if (task is null || task.IsDeleted) return;

        task.Title = Title.Trim();
        task.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes;
        task.Priority = SelectedPriority;
        task.When = BuildWhen();
        task.Deadline = BuildDeadline();

        var projectId = SelectedProject?.Id;
        if (task.ProjectId != projectId) task.SectionId = null;
        task.ProjectId = projectId;
        task.LabelIds = Labels.Where(label => label.IsSelected).Select(label => label.Id).ToList();

        await _store.SaveAsync(task);
        await _refreshOwner();
        await OpenAsync(id);
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
            SectionId = parent.SectionId,
            SortOrder = _reorder.AppendRank(siblings.Select(item => item.SortOrder)),
        };
        await _store.SaveAsync(child);
        NewSubtaskTitle = string.Empty;
        await _refreshOwner();
        await LoadSubtasksAsync(parentId);
    }

    /// <summary>The label color palette offered in the per-label color picker.</summary>
    public IReadOnlyList<string> LabelColorPalette { get; } = LabelColors.Palette;

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
        var selected = Labels.Where(item => item.IsSelected).Select(item => item.Id).Append(label.Id);
        await LoadLabelsAsync(selected);
    }

    /// <summary>Recolors a label and refreshes the chips. The selection set is preserved.</summary>
    public async Task SetLabelColorAsync(Guid labelId, string color)
    {
        var label = await _store.GetAsync<Label>(labelId);
        if (label is null || label.IsDeleted) return;
        label.Color = color;
        await _store.SaveAsync(label);
        var selected = Labels.Where(item => item.IsSelected).Select(item => item.Id).ToList();
        await LoadLabelsAsync(selected);
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
            && IsEvening == _loadedIsEvening
            && IsWhenAllDay == _loadedIsWhenAllDay)
            return _originalWhen;

        return SelectedWhenOption.Mode switch
        {
            WhenEditorMode.Unscheduled => ScheduledWhen.Unscheduled,
            WhenEditorMode.Someday => ScheduledWhen.SomeDay,
            WhenEditorMode.Today => ScheduledWhen.On(PinDate(LocalNow(), EffectiveWhenTime)),
            WhenEditorMode.ThisEvening => ScheduledWhen.On(PinDate(LocalNow(), EffectiveWhenTime), evening: true),
            WhenEditorMode.SpecificDate when WhenDate is { } date => ScheduledWhen.On(PinDate(date, EffectiveWhenTime), IsEvening),
            WhenEditorMode.SpecificDate => ScheduledWhen.Unscheduled,
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private ZonedDateTime? BuildDeadline()
    {
        if (SameDay(DeadlineDate, _loadedDeadlineDate)
            && DeadlineTime == _loadedDeadlineTime
            && IsDeadlineAllDay == _loadedIsDeadlineAllDay)
            return _originalDeadline;
        return DeadlineDate is { } deadline ? PinDate(deadline, EffectiveDeadlineTime) : null;
    }

    private static bool SameDay(DateTimeOffset? left, DateTimeOffset? right)
        => left is null || right is null
            ? left is null && right is null
            : left.Value.Date == right.Value.Date;

    private WhenEditorOption OptionFor(ScheduledWhen when)
    {
        if (when.Kind == WhenKind.Unscheduled) return FindOption(WhenEditorMode.Unscheduled);
        if (when.Kind == WhenKind.SomeDay) return FindOption(WhenEditorMode.Someday);

        var chosenDay = when.Date is { } value ? DateOnly.FromDateTime(value.ToLocal().DateTime) : default;
        var today = DateOnly.FromDateTime(LocalNow().DateTime);
        if (chosenDay == today)
            return FindOption(when.IsEvening ? WhenEditorMode.ThisEvening : WhenEditorMode.Today);
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

    private void SetDeadlineTimeEditors(TimeSpan? time)
    {
        SelectedDeadlineHour = time is { } value ? Hours[value.Hours] : null;
        SelectedDeadlineMinute = time is { } selected ? Minutes[selected.Minutes] : null;
    }

    private void SyncWhenTimeFromParts()
    {
        if (_isLoading || SelectedWhenHour is null || SelectedWhenMinute is null) return;
        WhenTime = new TimeSpan(SelectedWhenHour.Value, SelectedWhenMinute.Value, 0);
    }

    private void SyncDeadlineTimeFromParts()
    {
        if (_isLoading || SelectedDeadlineHour is null || SelectedDeadlineMinute is null) return;
        DeadlineTime = new TimeSpan(SelectedDeadlineHour.Value, SelectedDeadlineMinute.Value, 0);
    }

    private async Task LoadProjectsAsync(Guid? selectedId)
    {
        Projects.Clear();
        Projects.Add(new ProjectEditorOption(null, "Cue Inbox"));
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
        var selected = selectedIds.ToHashSet();
        Labels.Clear();
        foreach (var label in await _index.GetLabelsAsync())
            Labels.Add(new LabelEditorOption(label.Id, label.Name, selected.Contains(label.Id), label.Color));
    }

    private async Task LoadSubtasksAsync(Guid parentId)
    {
        Subtasks.Clear();
        foreach (var item in await _index.GetSubtasksAsync(parentId))
            Subtasks.Add(new SubtaskRowViewModel(item, row => ToggleSubtaskCommand.Execute(row)));
    }
}
