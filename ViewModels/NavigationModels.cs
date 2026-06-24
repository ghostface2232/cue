using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cue.ViewModels;

/// <summary>Typed navigation parameter used by the single reusable task-list page.</summary>
public sealed record TaskListNavigation(
    TaskListMode Mode,
    Guid? FilterId = null,
    string? Title = null);

/// <summary>Input for a record rename command.</summary>
public sealed record RenameRecordRequest(Guid Id, string Name);

/// <summary>A drag-reorder move within a list, by source and destination position.</summary>
public sealed record ReorderRequest(int OldIndex, int NewIndex);

/// <summary>A priority-section heading (P1–P4) and its indexed task rows — used by the 중요도 (priority)
/// view, the only sectioned list. Not related to the domain's <c>TaskGroup</c>. Each section is
/// independently collapsible; its header shows the task count and an expand/collapse chevron.</summary>
public sealed partial class PrioritySectionViewModel : ObservableObject
{
    public string Name { get; }
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>Whether this section's rows are shown. Sections start expanded; the state lives only in
    /// memory (like the sidebar's group/tag sections) and survives a list refresh because the reconcile
    /// reuses section instances by name.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>Task count shown in the header, kept in sync with <see cref="Tasks"/>.</summary>
    public int Count => Tasks.Count;

    public PrioritySectionViewModel(string name)
    {
        Name = name;
        Tasks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Count));
    }

    /// <summary>Header click: flips this section between expanded and collapsed.</summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>
/// The collapsible "완료한 일" section shown at the foot of the Today, group, and tag views — the open
/// list above stays open-only, while completed work for that view collects here. Its header reuses the
/// sidebar group/tag section look: a title, a task count, and an expand/collapse chevron. The section
/// starts <b>collapsed</b> and is hidden entirely when it has no rows.
/// </summary>
public sealed partial class CompletedSectionViewModel : ObservableObject
{
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>Header label, e.g. "오늘 완료한 일" (Today) or "완료한 일" (a group / tag view).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "완료한 일";

    /// <summary>Whether the completed rows are shown. Starts collapsed (the spec's default); the state
    /// lives only in memory and survives a list refresh because the reconcile reuses this instance.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Completed-task count shown in the header, kept in sync with <see cref="Tasks"/>.</summary>
    public int Count => Tasks.Count;

    /// <summary>Drives the section's own visibility — hidden entirely when there is nothing completed.</summary>
    public bool HasItems => Tasks.Count > 0;

    public CompletedSectionViewModel()
    {
        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasItems));
        };
    }

    /// <summary>Header click: flips the section between expanded and collapsed.</summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>
/// One date bucket in the 완료한 일 (Logbook) view — a day heading (오늘 / 어제 / a "M월 d일" date) and the
/// tasks completed on that day. Plain and always shown (no collapse): the Logbook is a flat date-grouped
/// history, newest day first.
/// </summary>
public sealed partial class DateSectionViewModel : ObservableObject
{
    public string Title { get; }
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public DateSectionViewModel(string title) => Title = title;
}
