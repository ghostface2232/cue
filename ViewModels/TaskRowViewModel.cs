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
/// acknowledgement. A terminal completion (one-off, or a recurring series that has ended) shows an undo
/// note then folds the row away, resurfacing in a dedicated 완료한 일 section / the Logbook.
/// <para>
/// A repeating completion instead performs the series' current cycle and advances it one step. If that
/// advance lands the series in the future the row stays <i>ticked + dimmed</i> in place — it is "done for
/// now" (<see cref="IsAheadOfSchedule"/>), held until the cycle comes due again; ticking it a second time
/// undoes the completion rather than advancing further forward. If the advance leaves another cycle still
/// due today/overdue, the row simply reloads unchecked so the backlog can be worked off. Either way the
/// series lives on, so a repeating completion never runs the terminal undo/fold acknowledgement.
/// </para>
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
    [NotifyPropertyChangedFor(nameof(ScheduleCaption))]
    public partial string Schedule { get; set; }

    public bool HasSchedule => Schedule.Length > 0;

    /// <summary>
    /// The text shown on the row's schedule line. For a recurring row performed up into the future
    /// (<see cref="IsAheadOfSchedule"/>) it reads as the just-done cycle's completion plus the next due
    /// date — "이번 할 일 완료됨 · 다음: …" — so the ticked, dimmed row never looks merely scheduled for that
    /// future date (the source of the "is it already done until then?" confusion). Otherwise it is the
    /// plain <see cref="Schedule"/> string. Re-evaluated whenever Schedule or IsAheadOfSchedule changes.
    /// </summary>
    public string ScheduleCaption => IsAheadOfSchedule && Schedule.Length > 0
        ? $"이번 할 일 완료됨 · 다음: {Schedule}"
        : Schedule;

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
    /// True when this is a recurring task whose current cycle has been performed up into the future: the
    /// series' latest cycle is a completed occurrence and its current cycle is still ahead of today. The row
    /// is "done for now" — it renders ticked (its <see cref="IsCompleted"/> checkbox is seeded on) and
    /// dimmed, and the list routes a tick on it to <i>undo</i> the last completion rather than advancing the
    /// series another cycle forward (which is how the runaway "complete the future forever" path is blocked).
    /// Projected by the index per <see cref="TaskListItem.IsAheadOfSchedule"/>, refreshed on every reload.
    /// </summary>
    /// <remarks>Not an <c>[ObservableProperty]</c> (it drives no two-way control), but it does feed the
    /// derived <see cref="ScheduleCaption"/>, so the setter raises that one notification — this is what makes
    /// an in-place <see cref="Update"/> swap the row's caption back to the plain date the moment a day
    /// rollover ends the "done for now" state, even when the next date string itself is unchanged.</remarks>
    public bool IsAheadOfSchedule
    {
        get => _isAheadOfSchedule;
        private set
        {
            if (SetProperty(ref _isAheadOfSchedule, value))
                OnPropertyChanged(nameof(ScheduleCaption));
        }
    }
    private bool _isAheadOfSchedule;

    /// <summary>
    /// True while this row's task is the one open in the detail panel. Drives the row's selection
    /// accent. Set by the list view model, not by the user toggling anything.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // --- Completion acknowledgement (the brief in-row moment after the user ticks a task) ---
    // When a one-off (or an ended/exhausted recurring series) is completed from an active list it is not
    // whisked away at once: the row holds its place for a beat, swapping its normal content for a small
    // acknowledgement bar with an "undo" affordance, then folds away and reloads into the relevant 완료한 일
    // section / Logbook. A repeating completion does NOT use this bar — it stays ticked + dimmed in place
    // (see the class remarks), so the bar is purely the terminal-completion presentation. These flags drive
    // the swap; the View owns the timing and the fold motion.

    /// <summary>True while the post-completion acknowledgement bar is showing in place of the row's normal
    /// content. The View flips this on via <see cref="BeginCompletionAcknowledgement"/> and clears it
    /// through <see cref="EndCompletionAcknowledgement"/> just before the row leaves the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNormalContent))]
    [NotifyPropertyChangedFor(nameof(ShowChecklist))]
    [NotifyPropertyChangedFor(nameof(VisualOpacity))]
    [NotifyPropertyChangedFor(nameof(ShowUndo))]
    public partial bool IsAcknowledging { get; set; }

    /// <summary>Inverse of <see cref="IsAcknowledging"/> — drives the normal row body's visibility so it
    /// yields to the acknowledgement bar.</summary>
    public bool ShowNormalContent => !IsAcknowledging;

    /// <summary>The acknowledgement message. Always the terminal-completion note now that a repeating
    /// completion stays in place rather than running the bar.</summary>
    [ObservableProperty]
    public partial string AcknowledgeMessage { get; set; } = string.Empty;

    /// <summary>Whether the acknowledgement bar offers an "실행 취소" (undo) button. The bar only ever shows
    /// for a terminal completion now, so it always carries the undo affordance while it is up.</summary>
    public bool ShowUndo => IsAcknowledging;

    /// <summary>Enters the post-completion acknowledgement state for a terminal completion (a one-off, or a
    /// recurring series that has ended/exhausted). The completion itself is already saved; this only flips
    /// the row into its acknowledgement presentation with the undo affordance.</summary>
    public void BeginCompletionAcknowledgement()
    {
        AcknowledgeMessage = "할 일을 완료했습니다";
        IsAcknowledging = true;
    }

    /// <summary>Leaves the acknowledgement state (on undo, or just before the row is removed/reloaded).</summary>
    public void EndCompletionAcknowledgement()
    {
        IsAcknowledging = false;
        AcknowledgeMessage = string.Empty;
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
        IsAheadOfSchedule = item.IsAheadOfSchedule;
        ApplyGroupAndTags(item);

        _suppressToggle = true;       // initial state from the index, not a user action
        // An "ahead of schedule" recurring task is done-for-now, so its box reads ticked even though the
        // series itself is not completed (its CompletedAt is null).
        IsCompleted = item.IsCompleted || item.IsAheadOfSchedule;
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
        IsAheadOfSchedule = item.IsAheadOfSchedule;
        SortOrder = item.SortOrder;
        ApplyGroupAndTags(item);
        // Seed the box ticked for a done-for-now (ahead-of-schedule) recurring row as well as a genuinely
        // completed one — see the constructor.
        SetCompletedSilently(item.IsCompleted || item.IsAheadOfSchedule);
    }

    private void ApplyGroupAndTags(TaskListItem item)
    {
        GroupName = item.TaskGroupName ?? string.Empty;
        GroupGlyph = string.IsNullOrEmpty(item.TaskGroupIcon) ? DefaultGroupGlyph : item.TaskGroupIcon!;
        // A task carries at most one tag; show only the first so a legacy multi-tag row still reads as
        // single-tag (it collapses to one on next edit).
        Tags = item.Tags is { Count: > 0 }
            ? new[] { new TaskRowTag(item.Tags[0].Name, item.Tags[0].Color) }
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
