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
/// <para>
/// Completed rows are <b>lazy</b>: while collapsed the section shows only its title and
/// <see cref="TotalCount"/> (from a cheap COUNT query) and realizes no <see cref="TaskRowViewModel"/> at
/// all — a long-lived group with hundreds of finished tasks costs nothing until the user opens it. The
/// first expand pages in <see cref="LoadMoreRequested"/>'s first batch, and the "더 보기" affordance pulls
/// each further page, so the view never builds more rows than the user actually looks at.
/// </para>
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

    /// <summary>The full completed-task count for this view, from a COUNT query — the header number and the
    /// section's visibility read this, so the section shows its heading and total without realizing a
    /// single completed row. Rows are paged into <see cref="Tasks"/> only once the section is expanded.</summary>
    [ObservableProperty]
    public partial int TotalCount { get; set; }

    /// <summary>True while a page of completed rows is being fetched, so the header toggle and "더 보기"
    /// can't re-enter and the affordance can hide itself mid-load.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Realizes the next page of completed rows. Set by the owning list view model: the header
    /// toggle calls it on the first expand, and the "더 보기" row calls it for each further page. Null until
    /// wired (and on views that carry no completed section).</summary>
    public Func<Task>? LoadMoreRequested { get; set; }

    /// <summary>Completed-task count shown in the header — the full total, not just the realized rows.</summary>
    public int Count => TotalCount;

    /// <summary>Drives the section's own visibility — hidden entirely when there is nothing completed.</summary>
    public bool HasItems => TotalCount > 0;

    /// <summary>More completed rows exist than are currently realized in <see cref="Tasks"/>.</summary>
    public bool HasMore => Tasks.Count < TotalCount;

    /// <summary>Drives the "더 보기" affordance: shown only while expanded, with unrealized rows left, and
    /// not already loading.</summary>
    public bool CanLoadMore => IsExpanded && HasMore && !IsLoading;

    public CompletedSectionViewModel()
    {
        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(CanLoadMore));
        };
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(CanLoadMore));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(CanLoadMore));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanLoadMore));

    /// <summary>Header click: flips the section between expanded and collapsed. The first expand pages in
    /// the first batch of completed rows; a re-expand keeps whatever was already loaded.</summary>
    [RelayCommand]
    private async Task ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded && Tasks.Count == 0 && HasMore && LoadMoreRequested is { } load)
            await load();
    }

    /// <summary>"더 보기" click: pages in the next batch of completed rows.</summary>
    [RelayCommand]
    private async Task LoadMore()
    {
        if (!IsLoading && HasMore && LoadMoreRequested is { } load)
            await load();
    }
}

/// <summary>
/// One date bucket in the 완료한 일 (Logbook) view — the tasks completed on a single local day, under a
/// day heading. Plain and always shown (no collapse): the Logbook is a flat date-grouped history, newest
/// day first.
/// </summary>
/// <remarks>
/// The section's identity is its <see cref="Date"/> (a full <see cref="DateOnly"/>), <i>not</i> its
/// rendered heading. Older days render without the year ("6월 22일"), so two same-day dates in different
/// years share a heading; keying the reconcile on the date keeps them separate sections instead of
/// merging 2025-06-22 and 2026-06-22 into one. <see cref="DisplayTitle"/> is refreshed on reconcile so a
/// section's heading still rolls 오늘 → 어제 as the day turns over.
/// </remarks>
public sealed partial class DateSectionViewModel : ObservableObject
{
    /// <summary>The local completion day this section gathers — the section's reconcile identity.</summary>
    public DateOnly Date { get; }

    /// <summary>The rendered day heading: 오늘 / 어제 / "M월 d일" (this year) / "yyyy년 M월 d일" (an earlier
    /// year). Updatable so a reused section re-titles itself as the current day advances.</summary>
    [ObservableProperty]
    public partial string DisplayTitle { get; set; }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public DateSectionViewModel(DateOnly date, string displayTitle)
    {
        Date = date;
        DisplayTitle = displayTitle;
    }
}
