using CommunityToolkit.Mvvm.ComponentModel;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>
/// One checklist item as a nested row under its task in a list — a display projection of a
/// <see cref="TaskListChecklistItem"/> plus a checkbox that writes back through the owning task.
/// </summary>
/// <remarks>
/// Unlike the old sub-task rows these are <i>not</i> tasks: they cannot be opened, dragged, or carry a
/// date/priority/group of their own. Ticking the checkbox hands off to a callback the parent list owns,
/// which loads the parent <see cref="Cue.Domain.TaskItem"/>, flips this item, and saves it.
/// </remarks>
public partial class ChecklistRowViewModel : ObservableObject
{
    private readonly Action<ChecklistRowViewModel> _onToggled;
    private bool _suppressToggle;

    /// <summary>The checklist item's id, used to address it inside its parent's list.</summary>
    public Guid Id { get; }

    /// <summary>The owning task's id — the record that is actually saved when this item is toggled.</summary>
    public Guid ParentTaskId { get; }

    public string Title { get; }
    public double VisualOpacity => IsChecked ? 0.48 : 1.0;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The label shown for a checklist item, with the blank-title fallback. Exposed so the
    /// owning list's in-place reconcile can tell whether a reused row's title is still current.</summary>
    public static string DisplayTitle(string? title) => string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;

    public ChecklistRowViewModel(Guid parentTaskId, TaskListChecklistItem item, Action<ChecklistRowViewModel> onToggled)
    {
        ParentTaskId = parentTaskId;
        Id = item.Id;
        Title = DisplayTitle(item.Title);
        _onToggled = onToggled;
        _suppressToggle = true;
        IsChecked = item.IsChecked;
        _suppressToggle = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(VisualOpacity));
        if (!_suppressToggle) _onToggled(this);
    }

    /// <summary>Sets the checkbox without firing the save callback — used to revert a failed toggle and
    /// to patch state from a list reload.</summary>
    public void SetCheckedSilently(bool value)
    {
        _suppressToggle = true;
        IsChecked = value;
        _suppressToggle = false;
    }
}
