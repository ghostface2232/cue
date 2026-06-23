using System.Collections.ObjectModel;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>Typed navigation parameter used by the single reusable task-list page.</summary>
public sealed record TaskListNavigation(
    TaskListMode Mode,
    Guid? FilterId = null,
    string? Title = null,
    DateOnly? DeadlineDate = null);

/// <summary>Input for a record rename command.</summary>
public sealed record RenameRecordRequest(Guid Id, string Name);

/// <summary>A drag-reorder move within a list, by source and destination position.</summary>
public sealed record ReorderRequest(int OldIndex, int NewIndex);

/// <summary>A section heading and its indexed task rows on a project page.</summary>
public sealed class TaskSectionGroupViewModel
{
    public Guid? Id { get; }
    public string Name { get; }
    public string DeadlineCaption { get; }
    public bool CanEdit => Id is not null;
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public TaskSectionGroupViewModel(SectionListItem? section)
    {
        Id = section?.Id;
        Name = section?.Name ?? "기타";
        DeadlineCaption = section?.DeadlineDate is { } deadline
            ? $"마감일 {deadline.Month}월 {deadline.Day}일"
            : string.Empty;
    }
}
