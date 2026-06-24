using System.Collections.ObjectModel;

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

/// <summary>A priority-section heading (P1–P4 or 없음) and its indexed task rows — used by the 중요도
/// (priority) view, the only sectioned list. Not related to the domain's <c>TaskGroup</c>.</summary>
public sealed class PrioritySectionViewModel
{
    public string Name { get; }
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public PrioritySectionViewModel(string name) => Name = name;
}
