using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;

namespace Cue.ViewModels;

/// <summary>
/// View model for the vertical month timeline (agenda): tasks with a concrete When date in the
/// visible month, grouped by day. Only days that actually have tasks appear, each as a section with
/// its date header and the day's task cards stacked beneath it. The cards reuse
/// <see cref="TaskRowViewModel"/> so they render consistently with the main list rows.
/// </summary>
public partial class TimelineViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IRecurringTaskService _recurrence;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    // Serializes completion toggles so rapid checks can't reorder their saves (mirrors the list).
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private DateOnly _visibleMonth;
    private DateOnly _rangeStart;
    private DateOnly _rangeEnd;
    private bool _rowsCompact;

    public ObservableCollection<TimelineDayViewModel> Days { get; } = new();
    public TaskDetailViewModel Detail { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = "타임라인";

    [ObservableProperty]
    public partial string RangeCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public TimelineViewModel(
        ITaskStore store,
        ITaskIndex index,
        IReorderService reorder,
        IRecurringTaskService recurrence,
        TimeProvider clock,
        TimeZoneInfo zone,
        INavDataChangeNotifier navNotifier)
    {
        _store = store;
        _index = index;
        _recurrence = recurrence;
        _clock = clock;
        _zone = zone;

        var today = Today();
        _visibleMonth = new DateOnly(today.Year, today.Month, 1);
        _rangeStart = _visibleMonth;
        _rangeEnd = _visibleMonth.AddMonths(1).AddDays(-1);
        UpdateRangeCaption();
        Detail = new TaskDetailViewModel(store, index, reorder, clock, zone, LoadAsync, navNotifier);
        // Clear the selection accent when the detail panel closes.
        Detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskDetailViewModel.IsOpen) && !Detail.IsOpen)
                ApplySelection(null);
        };
    }

    [RelayCommand]
    public Task LoadAsync()
    {
        _rangeStart = _visibleMonth;
        _rangeEnd = _visibleMonth.AddMonths(1).AddDays(-1);
        UpdateRangeCaption();
        return ReloadRowsAsync();
    }

    [RelayCommand]
    private async Task PreviousMonthAsync()
    {
        _visibleMonth = _visibleMonth.AddMonths(-1);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextMonthAsync()
    {
        _visibleMonth = _visibleMonth.AddMonths(1);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task GoTodayAsync()
    {
        var today = Today();
        _visibleMonth = new DateOnly(today.Year, today.Month, 1);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SelectTaskAsync(Guid id)
    {
        if (Detail.IsOpen && Detail.CurrentTaskId != id)
        {
            // Persist and drain the outgoing task's pending edits before tearing the panel down, so a
            // queued autosave can't resume against the next task's freshly loaded fields.
            await Detail.FlushAsync();
            Detail.Close();
            await Task.Delay(90);
        }

        await Detail.OpenAsync(id);
        ApplySelection(id);
    }

    /// <summary>
    /// Toggles completion for one card's task, persisting through the store (or the recurrence service
    /// for a repeating task). On failure the checkbox is restored so the UI never disagrees with disk.
    /// Mirrors <c>TaskListViewModel.ToggleCompleteAsync</c>.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleCompleteAsync(TaskRowViewModel row)
    {
        var completed = row.IsCompleted;
        await _toggleGate.WaitAsync();
        try
        {
            if (completed)
                await _recurrence.CompleteAsync(row.Id, _clock.GetUtcNow());
            else
                await _store.MutateAsync<TaskItem>(row.Id, task => { task.CompletedAt = null; return true; });
        }
        catch
        {
            row.SetCompletedSilently(!completed);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    /// <summary>Sets whether cards reflow their group/tag chips beneath the title (narrow layout). The
    /// page calls this as the content column resizes; the state is remembered so cards built during a
    /// reload inherit it.</summary>
    public void SetRowsCompact(bool compact)
    {
        _rowsCompact = compact;
        foreach (var day in Days)
            foreach (var row in day.Tasks)
                row.IsCompact = compact;
    }

    private async Task ReloadRowsAsync()
    {
        var items = await _index.GetTimelineRowsAsync(_rangeStart, _rangeEnd);
        var today = Today();

        // Items arrive ordered by day (then time, then sort order); collect that order into day buckets.
        var order = new List<DateOnly>();
        var buckets = new Dictionary<DateOnly, List<TaskListItem>>();
        foreach (var item in items)
        {
            var day = item.WhenDate!.Value;
            if (!buckets.TryGetValue(day, out var bucket))
            {
                bucket = new List<TaskListItem>();
                buckets.Add(day, bucket);
                order.Add(day);
            }
            bucket.Add(item);
        }

        // Reconcile the Days collection in place, reusing existing day + card view models by key so a
        // save-triggered reload doesn't recreate (and re-animate) untouched cards.
        for (var i = Days.Count - 1; i >= 0; i--)
            if (!buckets.ContainsKey(Days[i].Date))
                Days.RemoveAt(i);

        for (var i = 0; i < order.Count; i++)
        {
            var day = order[i];
            if (i >= Days.Count || Days[i].Date != day)
            {
                var existing = IndexOfDay(day);
                if (existing >= 0)
                    Days.Move(existing, i);
                else
                    Days.Insert(i, new TimelineDayViewModel(day, today));
            }
            SyncDayTasks(Days[i].Tasks, buckets[day]);
        }

        IsEmpty = Days.Count == 0;
        ApplySelection(Detail.IsOpen ? Detail.CurrentTaskId : null);
    }

    private void SyncDayTasks(ObservableCollection<TaskRowViewModel> target, List<TaskListItem> items)
    {
        var desired = new HashSet<Guid>(items.Count);
        foreach (var item in items) desired.Add(item.Id);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i].Id))
                target.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i >= target.Count || target[i].Id != item.Id)
            {
                var existing = IndexOfRow(target, item.Id);
                if (existing >= 0)
                {
                    target.Move(existing, i);
                    target[i].Update(item);
                }
                else
                {
                    target.Insert(i, new TaskRowViewModel(item, r => ToggleCompleteCommand.Execute(r)) { IsCompact = _rowsCompact });
                }
            }
            else
            {
                target[i].Update(item);
            }
        }
    }

    private int IndexOfDay(DateOnly date)
    {
        for (var i = 0; i < Days.Count; i++)
            if (Days[i].Date == date) return i;
        return -1;
    }

    private static int IndexOfRow(ObservableCollection<TaskRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id) return i;
        return -1;
    }

    private void ApplySelection(Guid? id)
    {
        foreach (var day in Days)
            foreach (var row in day.Tasks)
                row.IsSelected = row.Id == id;
    }

    private void UpdateRangeCaption()
        => RangeCaption = _visibleMonth.ToString("yyyy년 M월", CultureInfo.GetCultureInfo("ko-KR"));

    private DateOnly Today()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).DateTime);
}

/// <summary>One day section in the vertical timeline: its date header plus the day's task cards.</summary>
public sealed class TimelineDayViewModel
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public DateOnly Date { get; }
    public string DateHeader { get; }
    public string RelativeLabel { get; }
    public bool HasRelative => RelativeLabel.Length > 0;
    public bool IsToday { get; }
    public bool IsNotToday => !IsToday;
    // 내일 / 어제 read as a quiet secondary label; 오늘 gets the accent pill instead.
    public bool ShowSecondaryRelative => HasRelative && !IsToday;
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public TimelineDayViewModel(DateOnly date, DateOnly today)
    {
        Date = date;
        DateHeader = date.ToString("M월 d일 (ddd)", Korean);
        IsToday = date == today;
        var delta = date.DayNumber - today.DayNumber;
        RelativeLabel = delta switch
        {
            0 => "오늘",
            1 => "내일",
            -1 => "어제",
            _ => string.Empty,
        };
    }
}
