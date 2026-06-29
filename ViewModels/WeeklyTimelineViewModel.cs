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
/// View model for the 주간 타임라인 (weekly timeline): a horizontally scrolling band of ISO-week columns
/// over the index-backed task date projection. Navigation is by month (이전 달 / 오늘 / 다음 달); each
/// column is one ISO week (Monday-start), and a task sits in a full-width lane positioned at its week.
/// </summary>
public partial class WeeklyTimelineViewModel : ObservableObject
{
    // The target per-column width (the old per-day column was 220) — wide enough to seat a compact
    // list-style card. ComputeWeekWidth divides the viewport by a whole number of columns chosen against
    // this target, so the columns always tile the viewport exactly (no half-clipped column at the right and
    // no dead strip): a wide window widens the columns to fill it, a narrow one fits fewer columns and the
    // band scrolls. The card spans WeekWidth minus an 8px gutter on each side.
    private const double MinWeekWidthValue = 312;

    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IReorderService _reorder;
    private readonly IRecurringTaskService _recurrence;
    private readonly INavDataChangeNotifier _navNotifier;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    // Serializes completion toggles so rapid ticks can't reorder their writes.
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private DateOnly _visibleMonth;
    private DateOnly _rangeStart;
    private DateOnly _rangeEnd;
    // The timeline content viewport width (fed from the page on resize) and the column width derived from it.
    private double _viewportWidth;
    private double _weekWidth = MinWeekWidthValue;

    /// <summary>The week-column header view models, left to right across the visible month.</summary>
    public ObservableCollection<TimelineWeekViewModel> Weeks { get; } = new();

    /// <summary>One lane per task, each holding a list-style card placed at its ISO-week column.</summary>
    public ObservableCollection<WeeklyTimelineRowViewModel> Rows { get; } = new();

    public TaskDetailViewModel Detail { get; }

    public double WeekWidth => _weekWidth;
    public double TrackWidth => Weeks.Count * _weekWidth;
    public double TodayLineOffset { get; private set; }
    public bool HasTodayInRange { get; private set; }

    /// <summary>The page heading — the visible month, e.g. "7월 타임라인", so which month a step landed on
    /// is obvious from the title alone (there is no separate month caption beneath it).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "주간 타임라인";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public WeeklyTimelineViewModel(
        ITaskStore store,
        ITaskIndex index,
        IReorderService reorder,
        IRecurringTaskService recurrence,
        TimeProvider clock,
        TimeZoneInfo zone,
        INavDataChangeNotifier navNotifier,
        SaveFailureCoordinator coordinator)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
        _recurrence = recurrence;
        _navNotifier = navNotifier;
        _clock = clock;
        _zone = zone;

        var today = Today();
        _visibleMonth = new DateOnly(today.Year, today.Month, 1);
        RebuildWeeks();
        Detail = new TaskDetailViewModel(store, index, reorder, recurrence, clock, zone, LoadAsync, navNotifier, coordinator);
    }

    [RelayCommand]
    public Task LoadAsync()
    {
        RebuildWeeks();
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

    /// <summary>
    /// Creates a blank all-day task anchored to the start (Monday) of the given week column and opens it in
    /// the detail panel right away — the double-click-empty-space affordance. The task lands on the week the
    /// click fell in; the user fills in the title/exact day in the panel. No-op for an out-of-range index.
    /// </summary>
    [RelayCommand]
    private async Task CreateTaskInWeekAsync(int weekIndex)
    {
        if (weekIndex < 0 || weekIndex >= Weeks.Count)
            return;

        var weekStart = Weeks[weekIndex].WeekStart;
        var when = ScheduledWhen.AllDay(ZonedDateTime.FromLocal(weekStart.ToDateTime(TimeOnly.MinValue), _zone.Id));
        var task = new TaskItem
        {
            When = when,
            // New tasks append after the rows currently shown.
            SortOrder = _reorder.AppendRank(Rows.Select(row => row.Row.SortOrder)),
        };

        await _store.SaveAsync(task);
        _navNotifier.NotifyCountsChanged();
        await ReloadRowsAsync();
        // Route through the same flush/close/open path as a card tap so the panel opens cleanly on the new task.
        await SelectTaskAsync(task.Id);
    }

    /// <summary>
    /// Applies a card's completion change to the store, serialized through a gate. A simplified take on the
    /// list's toggle without the acknowledgement-bar/fold presentation: completing runs the cycle through
    /// the recurrence service (advancing a repeating series, stamping a one-off done) then reloads in place;
    /// un-ticking a done-for-now (ahead of schedule) recurring card rolls its latest cycle back rather than
    /// advancing further; un-completing a terminal card clears <see cref="TaskItem.CompletedAt"/>. The
    /// timeline keeps completed cards (rendered dimmed) so every path just reloads the lanes. On failure the
    /// checkbox is restored so the UI never disagrees with disk.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleCompleteAsync(TaskRowViewModel row)
    {
        var completed = row.IsCompleted;
        await _toggleGate.WaitAsync();
        try
        {
            if (completed)
            {
                // Repeating: records the current cycle and advances. One-off: stamps it done. Either way the
                // card stays in range and reloads (dimmed when terminally complete).
                await _recurrence.CompleteAsync(row.Id, _clock.GetUtcNow());
                _navNotifier.NotifyCountsChanged();
                await ReloadRowsAsync();
            }
            else if (row.IsAheadOfSchedule)
            {
                // Un-ticking a done-for-now recurring card undoes its latest completed cycle rather than
                // pushing the series further forward.
                var latest = await _index.GetOccurrencesAsync(row.Id, limit: 1);
                if (latest.Count > 0 && latest[0].Status == OccurrenceStatus.Completed)
                    await _recurrence.UndoCompletionAsync(row.Id, latest[0].Id, _clock.GetUtcNow());
                _navNotifier.NotifyCountsChanged();
                await ReloadRowsAsync();
            }
            else
            {
                // Un-completing a terminal card: clear the completion instant on the latest record.
                await _store.MutateAsync<TaskItem>(row.Id, task => { task.CompletedAt = null; return true; });
                _navNotifier.NotifyCountsChanged();
                await ReloadRowsAsync();
            }
        }
        catch
        {
            // Save failed — put the checkbox back so it reflects the real (unchanged) state.
            row.SetCompletedSilently(!completed);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    private async Task ReloadRowsAsync()
    {
        var items = await _index.GetTimelineRowsAsync(_rangeStart, _rangeEnd);

        Rows.Clear();
        foreach (var item in items)
        {
            // The card reuses the list-row projection; its checkbox toggle is routed to this VM's
            // completion command. Compact so group/tag chips reflow under the title to fit the card width;
            // the nested checklist is intentionally left empty so lane heights stay uniform.
            var card = new TaskRowViewModel(item, r => ToggleCompleteCommand.Execute(r), showWeekNumber: false)
            {
                IsCompact = true,
            };
            Rows.Add(new WeeklyTimelineRowViewModel(card, item.WhenDate, _rangeStart, _rangeEnd, Weeks.Count, _weekWidth));
        }

        IsEmpty = Rows.Count == 0;
    }

    /// <summary>Rebuilds the visible month's ISO-week columns: from the Monday of the week containing the 1st
    /// to the Sunday of the week containing the last day (≈5–6 columns). Also recomputes the today line.</summary>
    private void RebuildWeeks()
    {
        var monthStart = _visibleMonth;
        var monthEnd = _visibleMonth.AddMonths(1).AddDays(-1);
        _rangeStart = MondayOf(monthStart);
        _rangeEnd = MondayOf(monthEnd).AddDays(6);

        var now = LocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);

        var weekCount = (int)((_rangeEnd.DayNumber - _rangeStart.DayNumber) / 7) + 1;
        _weekWidth = ComputeWeekWidth(weekCount);

        Weeks.Clear();
        for (var week = _rangeStart; week <= _rangeEnd; week = week.AddDays(7))
            Weeks.Add(new TimelineWeekViewModel(week, today, _weekWidth));

        Title = $"{_visibleMonth.Month}월 타임라인";
        RecomputeTodayLine(now, today, weekCount);

        OnPropertyChanged(nameof(WeekWidth));
        OnPropertyChanged(nameof(TrackWidth));
        OnPropertyChanged(nameof(TodayLineOffset));
        OnPropertyChanged(nameof(HasTodayInRange));
    }

    /// <summary>
    /// Records the timeline content's viewport width (fed from the page on resize) and, when it changes the
    /// derived column width, restretches the columns to fill the new width without re-querying the index —
    /// the headers are rebuilt cheaply and each lane's pixel layout is recomputed in place (preserving the
    /// card view models and their state). Stretches to fill when there is room; stays at the minimum (and
    /// scrolls) when narrow.
    /// </summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0)
            return;
        _viewportWidth = width;
        if (Weeks.Count == 0)
            return;

        var newWeekWidth = ComputeWeekWidth(Weeks.Count);
        if (Math.Abs(newWeekWidth - _weekWidth) < 0.5)
            return;
        _weekWidth = newWeekWidth;

        var now = LocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var weekCount = Weeks.Count;

        // Rebuild the headers at the new column width (no IO), then relayout each lane in place.
        var starts = Weeks.Select(w => w.WeekStart).ToList();
        Weeks.Clear();
        foreach (var start in starts)
            Weeks.Add(new TimelineWeekViewModel(start, today, _weekWidth));
        foreach (var row in Rows)
            row.ApplyLayout(_weekWidth, weekCount);

        RecomputeTodayLine(now, today, weekCount);

        OnPropertyChanged(nameof(WeekWidth));
        OnPropertyChanged(nameof(TrackWidth));
        OnPropertyChanged(nameof(TodayLineOffset));
        OnPropertyChanged(nameof(HasTodayInRange));
    }

    /// <summary>
    /// The per-column width for the current viewport. The viewport is divided evenly by a <i>whole</i> number
    /// of columns so the visible columns tile it edge-to-edge — the rightmost visible column always ends flush
    /// at the viewport's right edge, never half-clipped (the cause of the "오른쪽이 잘린다" report). The visible
    /// count targets <see cref="MinWeekWidthValue"/> per column but is capped at the month's week count, so a
    /// wide window with few weeks simply widens the columns to fill the width (no dead strip on the right),
    /// while a narrow window fits one column to the viewport and scrolls for the rest.
    /// </summary>
    private double ComputeWeekWidth(int weekCount)
    {
        if (weekCount <= 0 || _viewportWidth <= 0)
            return MinWeekWidthValue;
        var visible = Math.Max(1, (int)Math.Round(_viewportWidth / MinWeekWidthValue));
        visible = Math.Min(visible, weekCount);
        return _viewportWidth / visible;
    }

    private void RecomputeTodayLine(DateTimeOffset now, DateOnly today, int weekCount)
    {
        HasTodayInRange = today >= _rangeStart && today <= _rangeEnd;
        if (HasTodayInRange)
        {
            var weekIndex = (MondayOf(today).DayNumber - _rangeStart.DayNumber) / 7;
            var daysSinceMonday = today.DayNumber - MondayOf(today).DayNumber;
            var timeFraction = now.TimeOfDay.TotalSeconds / TimeSpan.FromDays(1).TotalSeconds;
            TodayLineOffset = weekIndex * _weekWidth +
                              (daysSinceMonday + timeFraction) / 7 * _weekWidth;
        }
        else
        {
            TodayLineOffset = 0;
        }
    }

    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>The Monday of the ISO week containing <paramref name="date"/> (Monday-start weeks).</summary>
    internal static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private DateOnly Today() => DateOnly.FromDateTime(LocalNow().DateTime);

    private DateTimeOffset LocalNow() => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);
}

/// <summary>One ISO-week column header: its week number ("W27") and date range ("6/30–7/6"), with the
/// current week highlighted (accent), mirroring how the old day header highlighted today.</summary>
public sealed class TimelineWeekViewModel
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public DateOnly WeekStart { get; }
    public string WeekNumberLabel { get; }
    public string RangeLabel { get; }
    public bool IsThisWeek { get; }
    public double Width { get; }

    public TimelineWeekViewModel(DateOnly weekStart, DateOnly today, double width)
    {
        WeekStart = weekStart;
        var weekEnd = weekStart.AddDays(6);
        // ISO-8601 week number, zero-padded — consistent with the list's "연중 주차" convention.
        WeekNumberLabel = $"W{ISOWeek.GetWeekOfYear(weekStart.ToDateTime(TimeOnly.MinValue)):00}";
        RangeLabel = $"{weekStart.ToString("M/d", Korean)}–{weekEnd.ToString("M/d", Korean)}";
        IsThisWeek = today >= weekStart && today <= weekEnd;
        Width = width;
    }

    public bool IsNotThisWeek => !IsThisWeek;
}

/// <summary>A timeline lane: the list-style card (a <see cref="TaskRowViewModel"/>) plus the layout numbers
/// that place it at its ISO-week column and size its track. The pixel layout is recomputed in place when
/// the column width changes (a viewport resize), so the card view model — and its state — is never recreated
/// just to restretch; the layout properties are <see cref="ObservableObject"/> so the bindings update.</summary>
public sealed class WeeklyTimelineRowViewModel : ObservableObject
{
    private readonly int _weekIndex;
    private double _weekWidth;
    private int _weekCount;

    public TaskRowViewModel Row { get; }

    /// <summary>Left offset of the card, placed at its ISO-week column (a point, not a span). The lane is an
    /// auto-height Grid and the card is left-aligned with this as its left margin (built into a Thickness by
    /// the page) — so the lane row grows with the card's content rather than overflowing a fixed-height
    /// Canvas. Kept a plain double here because the view-model layer doesn't reference WinUI's Thickness.</summary>
    public double StartOffset => _weekIndex * _weekWidth + 8;

    /// <summary>Card width — one week-column wide, minus the gutters.</summary>
    public double CardWidth => _weekWidth - 16;

    /// <summary>The lane's full width, spanning every week column.</summary>
    public double TrackWidth => _weekCount * _weekWidth;

    public WeeklyTimelineRowViewModel(TaskRowViewModel row, DateOnly? whenDate, DateOnly rangeStart, DateOnly rangeEnd, int weekCount, double weekWidth)
    {
        Row = row;
        // Clamp the task's date to the visible range, then locate its week column from the Monday of that
        // clamped date relative to the range start.
        var date = whenDate ?? rangeStart;
        var clamped = date < rangeStart ? rangeStart : date > rangeEnd ? rangeEnd : date;
        _weekIndex = (WeeklyTimelineViewModel.MondayOf(clamped).DayNumber - rangeStart.DayNumber) / 7;
        _weekWidth = weekWidth;
        _weekCount = weekCount;
    }

    /// <summary>Recomputes the pixel layout for a new column width (on a viewport resize), raising the
    /// derived bindings without recreating the lane or its card.</summary>
    public void ApplyLayout(double weekWidth, int weekCount)
    {
        _weekWidth = weekWidth;
        _weekCount = weekCount;
        OnPropertyChanged(nameof(StartOffset));
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(TrackWidth));
    }
}
