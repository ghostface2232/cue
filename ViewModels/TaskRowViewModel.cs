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
/// <see cref="TaskItem.CompletedAt"/> and refreshes the list. Active views keep completed rows
/// visible and dimmed, so finishing an item remains acknowledged and reversible after reload.
/// </remarks>
public partial class TaskRowViewModel : ObservableObject
{
    private readonly Action<TaskRowViewModel> _onUserToggled;
    private bool _suppressToggle;

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// The row's current LexoRank ordering key, mirrored from the index. Kept up to date as the
    /// reorder service re-ranks the moved row (and, on a rare rebalance, its neighbors) so a chain of
    /// drags computes each "between" against fresh neighbor ranks.
    /// </summary>
    public string SortOrder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSchedule))]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    public partial string Schedule { get; set; }

    public bool HasSchedule => Schedule.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPriority))]
    [NotifyPropertyChangedFor(nameof(PriorityCaption))]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    public partial Priority Priority { get; set; }

    public bool HasPriority => Priority != Priority.None;
    public string PriorityCaption => HasPriority ? Priority.ToString() : string.Empty;
    // Subtasks are rendered as their own indented sub-list, so their presence is already obvious —
    // they intentionally do not add a "하위 작업 N" caption to the parent row's metadata line.
    public bool HasMetadata => HasSchedule || HasPriority;
    public double VisualOpacity => IsCompleted ? 0.48 : 1.0;
    public ObservableCollection<TaskRowViewModel> Subtasks { get; } = new();
    public bool HasSubtasks => Subtasks.Count > 0;

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    /// <summary>
    /// True while this row's task is the one open in the detail panel. Drives the row's selection
    /// accent. Set by the list view model, not by the user toggling anything.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public TaskRowViewModel(TaskListItem item, Action<TaskRowViewModel> onUserToggled)
    {
        _onUserToggled = onUserToggled;
        // The in-place list reconcile (SyncRows) mutates Subtasks directly rather than through AddSubtask,
        // so notify HasSubtasks from the collection itself — that's what flips the nested-list divider
        // live when a row's first subtask appears (or its last is removed).
        Subtasks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSubtasks));
        Id = item.Id;
        Title = FormatTitle(item.Title);
        SortOrder = item.SortOrder;
        Schedule = BuildSchedule(item);
        Priority = item.Priority;

        _suppressToggle = true;       // initial state from the index, not a user action
        IsCompleted = item.IsCompleted;
        _suppressToggle = false;
    }

    /// <summary>
    /// Patches this row's display fields in place from a fresh index projection, preserving the row
    /// instance — and with it the realized container, scroll position, selection accent, and any drag in
    /// progress — so a list refresh that only changed values never recreates the row. Only ever called for
    /// a row whose <see cref="Id"/> already matches <paramref name="item"/>. The generated setters skip the
    /// change notification when a value is unchanged, so a refresh that touched nothing here is silent.
    /// </summary>
    public void Update(TaskListItem item)
    {
        Title = FormatTitle(item.Title);
        Schedule = BuildSchedule(item);
        Priority = item.Priority;
        SortOrder = item.SortOrder;
        SetCompletedSilently(item.IsCompleted);
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
    }

    private static string FormatTitle(string title)
        => string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;

    // All-day (종일) tasks are pinned to 23:59; that is a marker, not a real time, so the row shows the
    // date alone. Any other time is a deliberate schedule and is shown next to the date.
    private static readonly TimeOnly AllDayTime = new(23, 59);

    private static string BuildSchedule(TaskListItem item)
    {
        if (item.WhenDate is not { } when)
            return string.Empty;
        return item.WhenTime is { } time && time != AllDayTime
            ? $"{Day(when)} {Time(time)}"
            : Day(when);
    }

    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    private static string Day(DateOnly d) => d.ToString("M월 d일 (ddd)", Korean);

    private static string Time(TimeOnly t) => t.ToString("tt h:mm", Korean);
}
