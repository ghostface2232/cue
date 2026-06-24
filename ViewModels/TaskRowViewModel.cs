using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cue.Domain;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>A tag as shown at the right edge of a task row: a colored dot + name. View-only.</summary>
public sealed record TaskRowTag(string Name, string? Color);

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

    /// <summary>The time portion of the schedule only (e.g. "오후 6:00"), empty for an all-day task or
    /// one with no time. The timeline groups rows by day, so a card shows just the time — the day
    /// itself is the section header — while the main list shows the full <see cref="Schedule"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeCaption))]
    [NotifyPropertyChangedFor(nameof(HasTimeOrRecurring))]
    public partial string TimeCaption { get; set; } = string.Empty;

    public bool HasTimeCaption => TimeCaption.Length > 0;

    /// <summary>Drives the timeline card's meta line (time + repeat glyph): shown when the task has a
    /// time or repeats, so an all-day recurring task still surfaces its repeat mark.</summary>
    public bool HasTimeOrRecurring => HasTimeCaption || IsRecurring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPriority))]
    [NotifyPropertyChangedFor(nameof(PriorityCaption))]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    public partial Priority Priority { get; set; }

    public bool HasPriority => Priority != Priority.None;
    public string PriorityCaption => HasPriority ? Priority.ToString() : string.Empty;

    /// <summary>True when the task repeats (carries a recurrence rule). Drives the row's repeat glyph.
    /// The list only needs the flag — the rule itself lives in the file and is loaded by the detail
    /// panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    [NotifyPropertyChangedFor(nameof(HasTimeOrRecurring))]
    public partial bool IsRecurring { get; set; }

    /// <summary>The row's group name, shown as a chip at the right edge; empty when the task is unfiled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroup))]
    [NotifyPropertyChangedFor(nameof(ShowRightMeta))]
    [NotifyPropertyChangedFor(nameof(ShowInlineMeta))]
    public partial string GroupName { get; set; } = string.Empty;

    /// <summary>The group's sidebar glyph for the row chip (default folder glyph when the group set none).</summary>
    [ObservableProperty]
    public partial string GroupGlyph { get; set; } = DefaultGroupGlyph;

    public bool HasGroup => GroupName.Length > 0;

    /// <summary>The row's tags (color + name), shown as chips at the right edge; empty when untagged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRightMeta))]
    [NotifyPropertyChangedFor(nameof(ShowInlineMeta))]
    public partial IReadOnlyList<TaskRowTag> Tags { get; set; } = Array.Empty<TaskRowTag>();

    /// <summary>
    /// True when the list is narrow enough that the right-edge group/tag chips should reflow to a line
    /// beneath the title instead of sitting at the row's right edge. Set by the page as the list resizes;
    /// new rows inherit the list's current state (see <c>TaskListViewModel.SetRowsCompact</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRightMeta))]
    [NotifyPropertyChangedFor(nameof(ShowInlineMeta))]
    public partial bool IsCompact { get; set; }

    private bool HasGroupOrTags => HasGroup || Tags.Count > 0;

    /// <summary>Group/tag chips sit at the right edge in the roomy layout…</summary>
    public bool ShowRightMeta => !IsCompact && HasGroupOrTags;

    /// <summary>…and reflow under the title once the list is compact.</summary>
    public bool ShowInlineMeta => IsCompact && HasGroupOrTags;

    // Matches the sidebar's default group glyph (Segoe Fluent folder) when a group carries no icon.
    private const string DefaultGroupGlyph = "";
    // Checklist items are rendered as their own indented sub-list, so their presence is already obvious —
    // they intentionally do not add a "체크리스트 N" caption to the parent row's metadata line.
    public bool HasMetadata => HasSchedule || HasPriority || IsRecurring;
    public double VisualOpacity => IsCompleted ? 0.48 : 1.0;

    /// <summary>The task's embedded checklist items as nested rows under it (read + toggle only). The
    /// owning list populates and reconciles this collection.</summary>
    public ObservableCollection<ChecklistRowViewModel> ChecklistItems { get; } = new();
    public bool HasChecklist => ChecklistItems.Count > 0;

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
        // The in-place list reconcile mutates ChecklistItems directly, so notify HasChecklist from the
        // collection itself — that's what flips the nested-list divider live when a row's first
        // checklist item appears (or its last is removed).
        ChecklistItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChecklist));
        Id = item.Id;
        Title = FormatTitle(item.Title);
        SortOrder = item.SortOrder;
        Schedule = BuildSchedule(item);
        TimeCaption = BuildTimeCaption(item);
        Priority = item.Priority;
        IsRecurring = item.IsRecurring;
        ApplyGroupAndTags(item);

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
        TimeCaption = BuildTimeCaption(item);
        Priority = item.Priority;
        IsRecurring = item.IsRecurring;
        SortOrder = item.SortOrder;
        ApplyGroupAndTags(item);
        SetCompletedSilently(item.IsCompleted);
    }

    private void ApplyGroupAndTags(TaskListItem item)
    {
        GroupName = item.TaskGroupName ?? string.Empty;
        GroupGlyph = string.IsNullOrEmpty(item.TaskGroupIcon) ? DefaultGroupGlyph : item.TaskGroupIcon!;
        Tags = item.Tags is { Count: > 0 }
            ? item.Tags.Select(tag => new TaskRowTag(tag.Name, tag.Color)).ToArray()
            : Array.Empty<TaskRowTag>();
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

    public void AddChecklistItem(ChecklistRowViewModel item)
    {
        ChecklistItems.Add(item);
        OnPropertyChanged(nameof(HasChecklist));
    }

    private static string FormatTitle(string title)
        => string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;

    // An all-day (종일) task carries no time in the index (its time column is NULL), so the row shows the
    // date alone. A present time is a deliberate schedule and is shown next to the date.
    private static string BuildSchedule(TaskListItem item)
    {
        if (item.WhenDate is not { } when)
            return string.Empty;
        return item.WhenTime is { } time
            ? $"{Day(when)} {Time(time)}"
            : Day(when);
    }

    // The time alone (empty for all-day / no time) — used by the timeline card where the day is already
    // the section header.
    private static string BuildTimeCaption(TaskListItem item)
        => item.WhenDate is not null && item.WhenTime is { } time ? Time(time) : string.Empty;

    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    private static string Day(DateOnly d) => d.ToString("M월 d일 (ddd)", Korean);

    private static string Time(TimeOnly t) => t.ToString("tt h:mm", Korean);
}
