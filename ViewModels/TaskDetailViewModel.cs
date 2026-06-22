using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;

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

public partial class LabelEditorOption : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public LabelEditorOption(Guid id, string name, bool isSelected)
    {
        Id = id;
        Name = name;
        IsSelected = isSelected;
    }
}

public partial class SubtaskRowViewModel : ObservableObject
{
    private readonly Action<SubtaskRowViewModel> _onToggled;
    private bool _suppressToggle;

    public Guid Id { get; }
    public string Title { get; }

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
    private DateTimeOffset? _loadedDeadlineDate;
    private TimeSpan? _loadedDeadlineTime;

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();
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
    public partial bool IsEvening { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DeadlineDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? DeadlineTime { get; set; }

    [ObservableProperty]
    public partial ProjectEditorOption? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string NewSubtaskTitle { get; set; } = string.Empty;

    public bool IsSpecificDate => SelectedWhenOption.Mode == WhenEditorMode.SpecificDate;
    public bool HasConcreteWhen => SelectedWhenOption.Mode is WhenEditorMode.Today or WhenEditorMode.ThisEvening or WhenEditorMode.SpecificDate;
    public bool HasDeadline => DeadlineDate is not null;

    public TaskDetailViewModel(
        ITaskStore store,
        ITaskIndex index,
        TimeProvider clock,
        TimeZoneInfo zone,
        Func<Task> refreshOwner,
        Func<Guid, Task> openTask)
    {
        _store = store;
        _index = index;
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
    }

    partial void OnDeadlineDateChanged(DateTimeOffset? value)
    {
        if (value is not null && DeadlineTime is null)
            DeadlineTime = TimeSpan.FromHours(12);
        OnPropertyChanged(nameof(HasDeadline));
    }

    public async Task OpenAsync(Guid taskId)
    {
        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is null || task.IsDeleted)
        {
            Close();
            return;
        }

        _taskId = task.Id;
        Title = task.Title;
        Notes = task.Notes ?? string.Empty;
        SelectedPriority = task.Priority;
        WhenDate = task.When.Date?.ToLocal();
        WhenTime = task.When.Date?.ToLocal().TimeOfDay;
        DeadlineDate = task.Deadline?.ToLocal();
        DeadlineTime = task.Deadline?.ToLocal().TimeOfDay;
        IsEvening = task.When.IsEvening;
        SelectedWhenOption = OptionFor(task.When);

        _originalWhen = task.When;
        _originalDeadline = task.Deadline;
        _loadedWhenMode = SelectedWhenOption.Mode;
        _loadedWhenDate = WhenDate;
        _loadedWhenTime = WhenTime;
        _loadedIsEvening = IsEvening;
        _loadedDeadlineDate = DeadlineDate;
        _loadedDeadlineTime = DeadlineTime;

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

        var child = new TaskItem
        {
            Title = NewSubtaskTitle.Trim(),
            ParentTaskId = parent.Id,
            ProjectId = parent.ProjectId,
            SectionId = parent.SectionId,
        };
        await _store.SaveAsync(child);
        NewSubtaskTitle = string.Empty;
        await _refreshOwner();
        await LoadSubtasksAsync(parentId);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSubtaskAsync(SubtaskRowViewModel row)
    {
        var completed = row.IsCompleted;
        try
        {
            var task = await _store.GetAsync<TaskItem>(row.Id);
            if (task is null || task.IsDeleted) return;
            task.CompletedAt = completed ? _clock.GetUtcNow() : null;
            await _store.SaveAsync(task);
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

    private ScheduledWhen BuildWhen()
    {
        if (SelectedWhenOption.Mode == _loadedWhenMode
            && SameDay(WhenDate, _loadedWhenDate)
            && WhenTime == _loadedWhenTime
            && IsEvening == _loadedIsEvening)
            return _originalWhen;

        return SelectedWhenOption.Mode switch
        {
            WhenEditorMode.Unscheduled => ScheduledWhen.Unscheduled,
            WhenEditorMode.Someday => ScheduledWhen.SomeDay,
            WhenEditorMode.Today => ScheduledWhen.On(PinDate(LocalNow(), WhenTime)),
            WhenEditorMode.ThisEvening => ScheduledWhen.On(PinDate(LocalNow(), WhenTime), evening: true),
            WhenEditorMode.SpecificDate when WhenDate is { } date => ScheduledWhen.On(PinDate(date, WhenTime), IsEvening),
            WhenEditorMode.SpecificDate => ScheduledWhen.Unscheduled,
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private ZonedDateTime? BuildDeadline()
    {
        if (SameDay(DeadlineDate, _loadedDeadlineDate) && DeadlineTime == _loadedDeadlineTime)
            return _originalDeadline;
        return DeadlineDate is { } deadline ? PinDate(deadline, DeadlineTime) : null;
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
            Labels.Add(new LabelEditorOption(label.Id, label.Name, selected.Contains(label.Id)));
    }

    private async Task LoadSubtasksAsync(Guid parentId)
    {
        Subtasks.Clear();
        foreach (var item in await _index.GetSubtasksAsync(parentId))
            Subtasks.Add(new SubtaskRowViewModel(item, row => ToggleSubtaskCommand.Execute(row)));
    }
}
