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
/// <see cref="TaskItem.CompletedAt"/>. Completing from an active list plays a brief in-row
/// acknowledgement (undo for a one-off, a repeat note + refresh spin for a repeating task) before the
/// row folds away; the completed task then resurfaces in a dedicated 완료한 일 section / the Logbook
/// rather than lingering dimmed in the open list. See the acknowledgement members below.
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

    /// <summary>True when the task repeats (carries a recurrence rule). Drives the row's repeat glyph.
    /// The list only needs the flag — the rule itself lives in the file and is loaded by the detail
    /// panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
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
    // A completed row reads dimmed (in its 완료한 일 section / Logbook), but while the acknowledgement bar
    // is showing the row returns to full opacity so the undo message stays legible.
    public double VisualOpacity => IsCompleted && !IsAcknowledging ? 0.48 : 1.0;

    /// <summary>The task's embedded checklist items as nested rows under it (read + toggle only). The
    /// owning list populates and reconciles this collection.</summary>
    public ObservableCollection<ChecklistRowViewModel> ChecklistItems { get; } = new();
    public bool HasChecklist => ChecklistItems.Count > 0;

    /// <summary>The nested checklist shows only when the row has items and is not mid-acknowledgement
    /// (the acknowledgement bar replaces the whole row body).</summary>
    public bool ShowChecklist => HasChecklist && ShowNormalContent;

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    /// <summary>
    /// True while this row's task is the one open in the detail panel. Drives the row's selection
    /// accent. Set by the list view model, not by the user toggling anything.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // --- Completion acknowledgement (the brief in-row moment after the user ticks a task) ---
    // When a task is completed from an active list it is not whisked away at once: the row holds its
    // place for a beat, swapping its normal content for a small acknowledgement bar — an "undo" affordance
    // for a one-off, or a "completed this occurrence" note for a repeating task — before it folds away and
    // the list reloads (dropping it into the relevant 완료한 일 section / Logbook). These flags drive that
    // swap; the View owns the timing and the fold/spin motion.

    /// <summary>True while the post-completion acknowledgement bar is showing in place of the row's normal
    /// content. The View flips this on via <see cref="BeginCompletionAcknowledgement"/> and clears it
    /// through <see cref="EndCompletionAcknowledgement"/> just before the row leaves the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNormalContent))]
    [NotifyPropertyChangedFor(nameof(ShowChecklist))]
    [NotifyPropertyChangedFor(nameof(VisualOpacity))]
    public partial bool IsAcknowledging { get; set; }

    /// <summary>Inverse of <see cref="IsAcknowledging"/> — drives the normal row body's visibility so it
    /// yields to the acknowledgement bar.</summary>
    public bool ShowNormalContent => !IsAcknowledging;

    /// <summary>The acknowledgement message: "할 일을 완료했습니다" for a one-off, "이번 할 일을 완료했습니다"
    /// for a repeating task (only this occurrence was completed; the series rolls on).</summary>
    [ObservableProperty]
    public partial string AcknowledgeMessage { get; set; } = string.Empty;

    /// <summary>True when the acknowledgement is for a repeating task: the bar shows a one-turn refresh
    /// spin in the circle and offers no undo (the series advanced); a one-off shows an undo button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUndo))]
    public partial bool IsRecurringCompletion { get; set; }

    /// <summary>Whether the acknowledgement bar offers an "실행 취소" (undo) button — one-off completions
    /// only. A repeating completion advanced the series, so it is not casually reversible here.</summary>
    public bool ShowUndo => IsAcknowledging && !IsRecurringCompletion;

    /// <summary>Enters the post-completion acknowledgement state. The completion itself is already saved;
    /// this only flips the row into its acknowledgement presentation.</summary>
    public void BeginCompletionAcknowledgement(bool recurring)
    {
        IsRecurringCompletion = recurring;
        AcknowledgeMessage = recurring ? "이번 할 일을 완료했습니다" : "할 일을 완료했습니다";
        IsAcknowledging = true;
        OnPropertyChanged(nameof(ShowUndo));
    }

    /// <summary>Leaves the acknowledgement state (on undo, or just before the row is removed/reloaded).</summary>
    public void EndCompletionAcknowledgement()
    {
        IsAcknowledging = false;
        IsRecurringCompletion = false;
        AcknowledgeMessage = string.Empty;
        OnPropertyChanged(nameof(ShowUndo));
    }

    public TaskRowViewModel(TaskListItem item, Action<TaskRowViewModel> onUserToggled)
    {
        _onUserToggled = onUserToggled;
        // The in-place list reconcile mutates ChecklistItems directly, so notify HasChecklist from the
        // collection itself — that's what flips the nested-list divider live when a row's first
        // checklist item appears (or its last is removed).
        ChecklistItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChecklist));
            OnPropertyChanged(nameof(ShowChecklist));
        };
        Id = item.Id;
        Title = FormatTitle(item.Title);
        SortOrder = item.SortOrder;
        Schedule = BuildSchedule(item);
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
        OnPropertyChanged(nameof(ShowChecklist));
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

    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    private static string Day(DateOnly d) => d.ToString("M월 d일 (ddd)", Korean);

    private static string Time(TimeOnly t) => t.ToString("tt h:mm", Korean);
}
