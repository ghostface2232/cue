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
