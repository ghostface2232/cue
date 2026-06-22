using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cue.Domain;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>
/// One row in a task list — a display projection of a <see cref="TaskListItem"/> (which comes
/// straight from the index) plus a completion toggle that writes back through the store.
/// </summary>
/// <remarks>
/// The toggle is wired to a callback the parent list owns, so flipping the checkbox sets/clears
/// <see cref="TaskItem.CompletedAt"/> and refreshes the list. Because the active views exclude
/// completed tasks, completing a row dims it in place for acknowledgement; the next navigation or
/// explicit refresh naturally removes it from open-task views.
/// </remarks>
public partial class TaskRowViewModel : ObservableObject
{
    private readonly Action<TaskRowViewModel> _onUserToggled;
    private bool _suppressToggle;

    public Guid Id { get; }
    public string Title { get; }
    public string Schedule { get; }
    public bool HasSchedule => Schedule.Length > 0;
    public Priority Priority { get; }
    public bool HasPriority => Priority != Priority.None;
    public string PriorityCaption => HasPriority ? Priority.ToString() : string.Empty;
    public bool HasMetadata => HasSchedule || HasPriority || HasSubtasks;
    public double VisualOpacity => IsCompleted ? 0.48 : 1.0;
    public ObservableCollection<TaskRowViewModel> Subtasks { get; } = new();
    public bool HasSubtasks => Subtasks.Count > 0;
    public string SubtaskCaption => HasSubtasks ? $"하위 작업 {Subtasks.Count}" : string.Empty;

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    public TaskRowViewModel(TaskListItem item, Action<TaskRowViewModel> onUserToggled)
    {
        _onUserToggled = onUserToggled;
        Id = item.Id;
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(제목 없음)" : item.Title;
        Schedule = BuildSchedule(item);
        Priority = item.Priority;

        _suppressToggle = true;       // initial state from the index, not a user action
        IsCompleted = item.IsCompleted;
        _suppressToggle = false;
    }

    partial void OnIsCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(VisualOpacity));
        if (_suppressToggle)
            return;
        // Hand off to the list, which serializes the save and reverts us if it fails.
        _onUserToggled(this);
    }

    /// <summary>Sets the checkbox state without triggering a save — used to revert a failed toggle.</summary>
    public void SetCompletedSilently(bool value)
    {
        _suppressToggle = true;
        IsCompleted = value;
        _suppressToggle = false;
    }

    public void AddSubtask(TaskRowViewModel subtask)
    {
        Subtasks.Add(subtask);
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(SubtaskCaption));
    }

    private static string BuildSchedule(TaskListItem item)
    {
        var parts = new List<string>();

        if (item.WhenKind == WhenKind.SomeDay)
            parts.Add("언젠가");
        else if (item.WhenDate is { } when)
            parts.Add((item.IsEvening ? "저녁 · 예정 " : "예정 ") + Day(when));

        if (item.DeadlineDate is { } deadline)
            parts.Add("마감 " + Day(deadline));

        return string.Join("   ·   ", parts);
    }

    private static string Day(DateOnly d)
        => d.ToString("M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR"));
}
