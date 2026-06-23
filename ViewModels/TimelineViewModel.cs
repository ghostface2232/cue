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

/// <summary>View model for the horizontal month timeline.</summary>
public partial class TimelineViewModel : ObservableObject
{
    private const double DayWidthValue = 220;
    private readonly ITaskIndex _index;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    private DateOnly _visibleMonth;
    private DateOnly _rangeStart;
    private DateOnly _rangeEnd;

    public ObservableCollection<TimelineDayViewModel> Days { get; } = new();
    public ObservableCollection<TimelineTaskRowViewModel> Rows { get; } = new();
    public TaskDetailViewModel Detail { get; }

    public double DayWidth => DayWidthValue;
    public double TrackWidth => Days.Count * DayWidthValue;
    public double TodayLineOffset { get; private set; }
    public bool HasTodayInRange { get; private set; }

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
        TimeZoneInfo zone)
    {
        _index = index;
        _clock = clock;
        _zone = zone;

        var today = Today();
        _visibleMonth = new DateOnly(today.Year, today.Month, 1);
        RebuildDays();
        Detail = new TaskDetailViewModel(store, index, reorder, recurrence, clock, zone, LoadAsync, SelectTaskAsync);
    }

    [RelayCommand]
    public Task LoadAsync()
    {
        RebuildDays();
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
    }

    private async Task ReloadRowsAsync()
    {
        var items = await _index.GetTimelineAsync(_rangeStart, _rangeEnd);

        Rows.Clear();
        foreach (var item in items)
            Rows.Add(new TimelineTaskRowViewModel(item, _rangeStart, _rangeEnd, DayWidthValue));

        IsEmpty = Rows.Count == 0;
    }

    private void RebuildDays()
    {
        _rangeStart = _visibleMonth;
        _rangeEnd = _visibleMonth.AddMonths(1).AddDays(-1);
        var now = LocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);

        Days.Clear();
        for (var day = _rangeStart; day <= _rangeEnd; day = day.AddDays(1))
            Days.Add(new TimelineDayViewModel(day, today, DayWidthValue));

        RangeCaption = _visibleMonth.ToString("yyyy년 M월", CultureInfo.GetCultureInfo("ko-KR"));
        HasTodayInRange = today >= _rangeStart && today <= _rangeEnd;
        TodayLineOffset = HasTodayInRange
            ? (today.DayNumber - _rangeStart.DayNumber) * DayWidthValue +
              (now.TimeOfDay.TotalSeconds / TimeSpan.FromDays(1).TotalSeconds * DayWidthValue)
            : 0;
        OnPropertyChanged(nameof(TrackWidth));
        OnPropertyChanged(nameof(TodayLineOffset));
        OnPropertyChanged(nameof(HasTodayInRange));
    }

    private DateOnly Today()
        => DateOnly.FromDateTime(LocalNow().DateTime);

    private DateTimeOffset LocalNow() => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);
}

public sealed class TimelineDayViewModel
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public DateOnly Date { get; }
    public string DayLabel { get; }
    public string WeekdayLabel { get; }
    public bool IsToday { get; }
    public bool IsNotToday => !IsToday;
    public bool IsWeekend { get; }
    public double Width { get; }

    public TimelineDayViewModel(DateOnly date, DateOnly today, double width)
    {
        Date = date;
        DayLabel = date.Day.ToString(Korean);
        WeekdayLabel = date.ToString("ddd", Korean);
        IsToday = date == today;
        IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        Width = width;
    }
}

public sealed class TimelineTaskRowViewModel
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public Guid Id { get; }
    public string Title { get; }
    public DateOnly Date { get; }
    public string DateCaption { get; }
    public bool IsCompleted { get; }
    public double VisualOpacity => IsCompleted ? 0.48 : 1.0;
    public Priority Priority { get; }
    public bool HasPriority => Priority != Priority.None;

    /// <summary>Left offset of the card, placed at the task's single When date (a point, not a bar).</summary>
    public double StartOffset { get; }

    /// <summary>Fixed card width — one day-column wide. The timeline shows a card at a date, not a span.</summary>
    public double CardWidth { get; }
    public double TrackWidth { get; }

    public TimelineTaskRowViewModel(TimelineTaskItem item, DateOnly rangeStart, DateOnly rangeEnd, double dayWidth)
    {
        Id = item.Id;
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(제목 없음)" : item.Title;
        Date = item.Date;
        IsCompleted = item.IsCompleted;
        Priority = item.Priority;

        var clamped = item.Date < rangeStart ? rangeStart : item.Date > rangeEnd ? rangeEnd : item.Date;
        StartOffset = (clamped.DayNumber - rangeStart.DayNumber) * dayWidth + 8;
        CardWidth = dayWidth - 16;
        TrackWidth = (rangeEnd.DayNumber - rangeStart.DayNumber + 1) * dayWidth;
        DateCaption = item.Date.ToString("M월 d일 (ddd)", Korean);
    }
}
