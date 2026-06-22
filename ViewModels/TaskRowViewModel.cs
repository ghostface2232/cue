using CommunityToolkit.Mvvm.ComponentModel;
using Cue.Domain;
using Cue.Storage.Index;
using Microsoft.UI.Xaml;

namespace Cue.ViewModels;

/// <summary>
/// One row in a task list — a display projection of a <see cref="TaskListItem"/> (which comes
/// straight from the index) plus a completion toggle that writes back through the store.
/// </summary>
/// <remarks>
/// The toggle is wired to a callback the parent list owns, so flipping the checkbox sets/clears
/// <see cref="TaskItem.CompletedAt"/> and refreshes the list. Because the active views exclude
/// completed tasks, completing a row makes it drop out on the next refresh — the immediate,
/// visible reflection of the change.
/// </remarks>
public partial class TaskRowViewModel : ObservableObject
{
    private readonly Func<TaskRowViewModel, bool, Task> _onToggle;
    private bool _suppressToggle;

    public Guid Id { get; }
    public string Title { get; }
    public string Schedule { get; }
    public Visibility ScheduleVisibility => Schedule.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    public TaskRowViewModel(TaskListItem item, Func<TaskRowViewModel, bool, Task> onToggle)
    {
        _onToggle = onToggle;
        Id = item.Id;
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(제목 없음)" : item.Title;
        Schedule = BuildSchedule(item);

        _suppressToggle = true;       // initial state from the index, not a user action
        IsCompleted = item.IsCompleted;
        _suppressToggle = false;
    }

    partial void OnIsCompletedChanged(bool value)
    {
        if (_suppressToggle)
            return;
        _ = _onToggle(this, value);
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

    private static string Day(DateOnly d) => $"{d.Month}월 {d.Day}일";
}
