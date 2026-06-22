using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>Which index-backed list this view shows.</summary>
public enum TaskListMode
{
    /// <summary>Home / Cue — project-less (unclassified) open tasks.</summary>
    Inbox,

    /// <summary>Today — open tasks due or scheduled today or earlier.</summary>
    Today,
}

/// <summary>
/// Drives one task list: the quick-add line at the top and the list below. The full data loop lives
/// here — quick-add text goes through the parser into a <see cref="TaskItem"/>, is saved through the
/// store (which writes the file and updates the index together), and the list is reloaded straight
/// from the index.
/// </summary>
public partial class TaskListViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IDateParser _parser;
    private readonly TimeProvider _clock;
    private readonly string _timeZoneId;

    private TaskListMode _mode = TaskListMode.Inbox;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string QuickAddText { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public TaskListViewModel(ITaskStore store, ITaskIndex index, IDateParser parser, TimeProvider clock, TimeZoneInfo zone)
    {
        _store = store;
        _index = index;
        _parser = parser;
        _clock = clock;
        _timeZoneId = zone.Id;

        Title = "Cue";
        QuickAddText = string.Empty;
    }

    /// <summary>Switches which index view this list reflects, and retitles accordingly.</summary>
    public void SetMode(TaskListMode mode)
    {
        _mode = mode;
        Title = mode == TaskListMode.Inbox ? "Cue" : "Today";
    }

    /// <summary>Quick-add: parse the line, create + save a task, then refresh from the index.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var text = QuickAddText?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var parsed = _parser.Parse(text, _clock.GetUtcNow(), _timeZoneId);
        var task = new TaskItem
        {
            Title = parsed.Title,
            When = parsed.When,
            Deadline = parsed.Deadline,
            Recurrence = parsed.Recurrence,
            // No project yet — a new task lands in the unclassified Inbox.
        };

        await _store.SaveAsync(task);
        QuickAddText = string.Empty;
        await LoadAsync();
    }

    /// <summary>Reloads the list from the index for the current mode.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var items = _mode == TaskListMode.Inbox
            ? await _index.GetInboxAsync()
            : await _index.GetTodayAsync();

        Tasks.Clear();
        foreach (var item in items)
            Tasks.Add(new TaskRowViewModel(item, ToggleCompleteAsync));
        IsEmpty = Tasks.Count == 0;
    }

    private async Task ToggleCompleteAsync(TaskRowViewModel row, bool completed)
    {
        var task = await _store.GetAsync<TaskItem>(row.Id);
        if (task is null)
            return;

        task.CompletedAt = completed ? _clock.GetUtcNow() : null;
        await _store.SaveAsync(task);
        await LoadAsync();
    }
}
