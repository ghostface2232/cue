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

    /// <summary>Open tasks scheduled for a future day or carrying a future deadline.</summary>
    Upcoming,

    /// <summary>Open tasks without a scheduled When date.</summary>
    Anytime,

    /// <summary>Open tasks parked for Someday.</summary>
    Someday,

    /// <summary>Completed tasks.</summary>
    Logbook,
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

    // Serializes completion toggles so concurrent/rapid checks can't reorder their saves.
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private TaskListMode _mode = TaskListMode.Inbox;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>The evening-flagged subset of Today, displayed as a section on that page.</summary>
    public ObservableCollection<TaskRowViewModel> EveningTasks { get; } = new();

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string QuickAddText { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasEveningTasks { get; set; }

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
        Title = mode switch
        {
            TaskListMode.Inbox => "Cue",
            TaskListMode.Today => "Today",
            TaskListMode.Upcoming => "Upcoming",
            TaskListMode.Anytime => "Anytime",
            TaskListMode.Someday => "Someday",
            TaskListMode.Logbook => "Logbook",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
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
        IReadOnlyList<TaskListItem> items;
        IReadOnlyList<TaskListItem> eveningItems = Array.Empty<TaskListItem>();

        switch (_mode)
        {
            case TaskListMode.Inbox:
                items = await _index.GetInboxAsync();
                break;
            case TaskListMode.Today:
                items = await _index.GetTodayAsync();
                eveningItems = await _index.GetThisEveningAsync();
                break;
            case TaskListMode.Upcoming:
                items = await _index.GetUpcomingAsync();
                break;
            case TaskListMode.Anytime:
                items = await _index.GetAnytimeAsync();
                break;
            case TaskListMode.Someday:
                items = await _index.GetSomedayAsync();
                break;
            case TaskListMode.Logbook:
                items = await _index.GetLogbookAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        // This Evening is already a subset query of Today. Keep it in its own section without
        // rendering those same rows twice on the Today page.
        var eveningIds = eveningItems.Select(item => item.Id).ToHashSet();

        Tasks.Clear();
        foreach (var item in items.Where(item => !eveningIds.Contains(item.Id)))
            Tasks.Add(CreateRow(item));

        EveningTasks.Clear();
        foreach (var item in eveningItems)
            EveningTasks.Add(CreateRow(item));

        HasEveningTasks = EveningTasks.Count > 0;
        IsEmpty = Tasks.Count == 0 && !HasEveningTasks;
    }

    private TaskRowViewModel CreateRow(TaskListItem item)
        => new(item, row => ToggleCompleteCommand.Execute(row));

    /// <summary>
    /// Applies a row's completion change to the store, then refreshes. Serialized through a gate so
    /// rapid toggles can't reorder their writes (concurrent executions are allowed so none are
    /// dropped — they queue on the gate); on failure the row's checkbox is restored so the UI never
    /// disagrees with what's on disk.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleCompleteAsync(TaskRowViewModel row)
    {
        var completed = row.IsCompleted;
        await _toggleGate.WaitAsync();
        try
        {
            var task = await _store.GetAsync<TaskItem>(row.Id);
            if (task is not null)
            {
                task.CompletedAt = completed ? _clock.GetUtcNow() : null;
                await _store.SaveAsync(task);
            }
            await LoadAsync();
        }
        catch
        {
            // Save/reload failed — put the checkbox back so it reflects the real (unchanged) state.
            row.SetCompletedSilently(!completed);
        }
        finally
        {
            _toggleGate.Release();
        }
    }
}
