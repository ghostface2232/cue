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
    private readonly IListDisplayPreferences? _listPreferences;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    // Serializes completion toggles so rapid ticks can't reorder their writes.
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private DateOnly _visibleMonth;
    private DateOnly _rangeStart;
    private DateOnly _rangeEnd;
    // The track spans a whole calendar year of ISO weeks (so a month step slides ~4–5 columns over one
    // continuous band rather than swapping content); this is the year currently built into Weeks/Rows.
    private int _loadedYear;
    // The timeline content viewport width (fed from the page on resize) and the column width derived from it.
    private double _viewportWidth;
    private double _weekWidth = MinWeekWidthValue;

    /// <summary>The week-column header view models, left to right across the visible month.</summary>
    public ObservableCollection<TimelineWeekViewModel> Weeks { get; } = new();

    /// <summary>The packed rows ("bands"). Each band is one horizontal row of the grid holding the cards
    /// that share that row across <i>different</i> week columns — tasks in different weeks stack from band 0,
    /// so a sparse week leaves its later bands free for other weeks rather than pushing every task to its own
    /// row. Within a single week, its tasks fill successive bands (0, 1, 2 …).</summary>
    public ObservableCollection<TimelineBandViewModel> Bands { get; } = new();

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
        SaveFailureCoordinator coordinator,
        IListDisplayPreferences? listPreferences = null)
    {
        _store = store;
        _index = index;
        _reorder = reorder;
        _recurrence = recurrence;
        _navNotifier = navNotifier;
        _listPreferences = listPreferences;
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
    private Task PreviousMonthAsync() => StepMonthAsync(-1);

    [RelayCommand]
    private Task NextMonthAsync() => StepMonthAsync(1);

    /// <summary>
    /// Moves the focused month by <paramref name="delta"/> over the continuous year band. Within the loaded
    /// year nothing is rebuilt — only the focused month (and so <see cref="FocusedMonthOffset"/>) shifts, and
    /// the page animates the scroll to it, so the columns slide ~4–5 places in the step's direction. Crossing
    /// into another year rebuilds the band for that year; the page still animates to the new month.
    /// </summary>
    private async Task StepMonthAsync(int delta)
    {
        _visibleMonth = _visibleMonth.AddMonths(delta);
        UpdateTitle();
        if (_visibleMonth.Year != _loadedYear)
            await LoadAsync();
        else
            OnPropertyChanged(nameof(FocusedMonthOffset));
    }

    [RelayCommand]
    private async Task GoTodayAsync()
    {
        var today = Today();
        _visibleMonth = new DateOnly(today.Year, today.Month, 1);
        UpdateTitle();
        if (today.Year != _loadedYear)
            await LoadAsync();
        else
            OnPropertyChanged(nameof(FocusedMonthOffset));
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
            SortOrder = _reorder.AppendRank(Bands.SelectMany(band => band.Cards).Select(card => card.Row.SortOrder)),
        };

        await _store.SaveAsync(task);
        _navNotifier.NotifyCountsChanged();
        await ReloadRowsAsync();
        // Route through the same flush/close/open path as a card tap so the panel opens cleanly on the new task.
        await SelectTaskAsync(task.Id);
    }

    // --- Card context-menu / Delete-key operations (mirrors the list view; reloads the band grid in place) ---

    /// <summary>The live task record by id, so a card's context menu can reflect its current group and tags.</summary>
    public Task<TaskItem?> GetTaskAsync(Guid id) => _store.GetAsync<TaskItem>(id);

    /// <summary>Active groups, for the context menu's "그룹으로 이동" submenu.</summary>
    public Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync() => _index.GetTaskGroupsAsync();

    /// <summary>Active tags, for the context menu's 태그 submenu.</summary>
    public Task<IReadOnlyList<TagListItem>> GetTagsAsync() => _index.GetTagsAsync();

    /// <summary>Moves a task into a group (or to the Cue home when null), then reloads. A no-op if unchanged.</summary>
    public async Task MoveTaskToTaskGroupAsync(Guid taskId, Guid? taskGroupId)
    {
        var moved = await MutateFromCardAsync(taskId, task =>
        {
            if (task.TaskGroupId == taskGroupId) return false;
            task.TaskGroupId = taskGroupId;
            return true;
        });
        if (moved is not null)
        {
            await ReloadRowsAsync();
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Toggles <paramref name="tagId"/> on the task (adds when absent, removes when present),
    /// leaving its other tags in place, then reloads.</summary>
    public async Task ToggleTaskTagAsync(Guid taskId, Guid tagId)
    {
        var changed = await MutateFromCardAsync(taskId, task =>
        {
            if (!task.TagIds.Remove(tagId)) task.TagIds.Add(tagId);
            return true;
        });
        if (changed is not null)
        {
            await ReloadRowsAsync();
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Removes a single <paramref name="tagId"/> from the task, then reloads. A no-op if absent.</summary>
    public async Task RemoveTaskTagAsync(Guid taskId, Guid tagId)
    {
        var changed = await MutateFromCardAsync(taskId, task => task.TagIds.Remove(tagId));
        if (changed is not null)
        {
            await ReloadRowsAsync();
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Removes every tag from the task, then reloads. A no-op if it had none.</summary>
    public async Task ClearTaskTagsAsync(Guid taskId)
    {
        var changed = await MutateFromCardAsync(taskId, task =>
        {
            if (task.TagIds.Count == 0) return false;
            task.TagIds.Clear();
            return true;
        });
        if (changed is not null)
        {
            await ReloadRowsAsync();
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Renames a task, then reloads. A blank name is ignored.</summary>
    public async Task RenameTaskAsync(Guid taskId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;
        var renamed = await MutateFromCardAsync(taskId, task =>
        {
            if (task.Title == trimmed) return false;
            task.Title = trimmed;
            return true;
        });
        if (renamed is not null) await ReloadRowsAsync();
    }

    /// <summary>
    /// Applies a card context-menu edit without letting an already-open detail panel overwrite it later.
    /// The panel owns a whole metadata snapshot, so its pending text/selection edits must land first; after
    /// the card mutation wins, re-open the same task to replace the panel's snapshot with the new record.
    /// </summary>
    private async Task<TaskItem?> MutateFromCardAsync(Guid taskId, Func<TaskItem, bool> mutate)
    {
        var refreshDetail = Detail.IsOpen && Detail.CurrentTaskId == taskId;
        if (refreshDetail)
            await Detail.FlushAsync();

        var changed = await _store.MutateAsync(taskId, mutate);
        if (changed is not null && refreshDetail)
            await Detail.OpenAsync(taskId);
        return changed;
    }

    /// <summary>Ends a recurring series (반복 종료): completes it to the Logbook, closes the detail panel if
    /// this task was open, and reloads. A no-op for a non-recurring task; the recorded history is preserved.</summary>
    public async Task EndSeriesAsync(Guid id)
    {
        await _recurrence.EndSeriesAsync(id, _clock.GetUtcNow());
        if (Detail.IsOpen && Detail.CurrentTaskId == id) Detail.Close();
        await ReloadRowsAsync();
        _navNotifier.NotifyCountsChanged();
    }

    /// <summary>Soft-deletes a task, closes the detail panel if it was open, and reloads the band grid.</summary>
    [RelayCommand]
    public async Task DeleteTaskAsync(Guid id)
    {
        await _store.DeleteAsync<TaskItem>(id);
        if (Detail.IsOpen && Detail.CurrentTaskId == id) Detail.Close();
        await ReloadRowsAsync();
        _navNotifier.NotifyCountsChanged();
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

        // The columns (ISO weeks) are fixed, but the order of cards *within* a week follows the global sort
        // (날짜순 / 이름순 / 중요도순) — the same preference the lists use. Reordering the flat result sets the
        // band-packing encounter order below, so within each week column the cards stack in that order.
        items = TaskListOrdering.Apply(items, _listPreferences?.SortMode ?? TaskSortMode.Date);

        // Pack into bands: each task drops into the next free band of its own week column, so tasks in
        // different weeks share a band (stack from band 0) instead of every task taking its own row. Within a
        // week the cards take successive bands in the global-sort order applied just above.
        var bands = new List<List<WeeklyTimelineRowViewModel>>();
        var perColumnCount = new Dictionary<int, int>();
        foreach (var item in items)
        {
            // The card reuses the list-row projection; its checkbox toggle is routed to this VM's
            // completion command. Compact so group/tag chips reflow under the title to fit the card width;
            // the nested checklist is intentionally left empty so card heights stay tidy.
            var card = new TaskRowViewModel(item, r => ToggleCompleteCommand.Execute(r), showWeekNumber: false)
            {
                IsCompact = true,
            };
            var placement = new WeeklyTimelineRowViewModel(card, item.WhenDate, _rangeStart, _rangeEnd, _weekWidth);

            var column = placement.WeekIndex;
            var bandIndex = perColumnCount.TryGetValue(column, out var used) ? used : 0;
            perColumnCount[column] = bandIndex + 1;
            while (bands.Count <= bandIndex)
                bands.Add(new List<WeeklyTimelineRowViewModel>());
            bands[bandIndex].Add(placement);
        }

        Bands.Clear();
        foreach (var band in bands)
            Bands.Add(new TimelineBandViewModel(band, Weeks.Count, _weekWidth));

        IsEmpty = Bands.Count == 0;
    }

    /// <summary>Rebuilds the focused year's ISO-week columns — every week from the Monday of the week holding
    /// Jan 1 to the Sunday of the week holding Dec 31 (~52–53 columns) — so month nav slides over one
    /// continuous band. Also recomputes the today line.</summary>
    private void RebuildWeeks()
    {
        _loadedYear = _visibleMonth.Year;
        _rangeStart = MondayOf(new DateOnly(_loadedYear, 1, 1));
        _rangeEnd = MondayOf(new DateOnly(_loadedYear, 12, 31)).AddDays(6);

        var now = LocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);

        var weekCount = (int)((_rangeEnd.DayNumber - _rangeStart.DayNumber) / 7) + 1;
        _weekWidth = ComputeWeekWidth(weekCount);

        Weeks.Clear();
        for (var week = _rangeStart; week <= _rangeEnd; week = week.AddDays(7))
            Weeks.Add(new TimelineWeekViewModel(week, today, _weekWidth));

        UpdateTitle();
        RecomputeTodayLine(now, today, weekCount);

        OnPropertyChanged(nameof(WeekWidth));
        OnPropertyChanged(nameof(TrackWidth));
        OnPropertyChanged(nameof(TodayLineOffset));
        OnPropertyChanged(nameof(HasTodayInRange));
        OnPropertyChanged(nameof(FocusedMonthOffset));
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

        // Rebuild the headers at the new column width (no IO), then relayout each band's cards in place.
        var starts = Weeks.Select(w => w.WeekStart).ToList();
        Weeks.Clear();
        foreach (var start in starts)
            Weeks.Add(new TimelineWeekViewModel(start, today, _weekWidth));
        foreach (var band in Bands)
            band.ApplyLayout(_weekWidth, weekCount);

        RecomputeTodayLine(now, today, weekCount);

        OnPropertyChanged(nameof(WeekWidth));
        OnPropertyChanged(nameof(TrackWidth));
        OnPropertyChanged(nameof(TodayLineOffset));
        OnPropertyChanged(nameof(HasTodayInRange));
        OnPropertyChanged(nameof(FocusedMonthOffset));
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

    /// <summary>The x-offset of the focused month's first week column within the year band — the scroll
    /// target the page animates to on a month step. Column-aligned, so the band lands flush.</summary>
    public double FocusedMonthOffset
    {
        get
        {
            if (Weeks.Count == 0 || _weekWidth <= 0)
                return 0;
            var firstWeekMonday = MondayOf(new DateOnly(_visibleMonth.Year, _visibleMonth.Month, 1));
            var index = Math.Clamp((firstWeekMonday.DayNumber - _rangeStart.DayNumber) / 7, 0, Weeks.Count - 1);
            return index * _weekWidth;
        }
    }

    /// <summary>The x-offset of the week column holding the given task — so the page can pin that column to
    /// the left edge when the detail panel opens (the panel narrows the timeline and recomputes the column
    /// width, which would otherwise leave the scroll on a different week). Returns -1 if the task isn't shown
    /// or the band isn't laid out yet. Reads the current column width, so call it after any resize settles.</summary>
    public double ColumnOffsetForTask(Guid id)
    {
        if (_weekWidth <= 0)
            return -1;
        foreach (var band in Bands)
            foreach (var card in band.Cards)
                if (card.Row.Id == id)
                    return card.WeekIndex * _weekWidth;
        return -1;
    }

    /// <summary>
    /// Updates the focused month (and the title) from a settled scroll offset, so free scrolling across the
    /// year keeps the heading in step with what is on screen. Uses the leftmost visible week's ISO month
    /// (its Thursday), clamped to the loaded year. The page calls this on a non-intermediate ViewChanged.
    /// </summary>
    public void SyncFocusedMonthToOffset(double offset)
    {
        if (Weeks.Count == 0 || _weekWidth <= 0)
            return;
        var index = Math.Clamp((int)Math.Round(offset / _weekWidth), 0, Weeks.Count - 1);
        var thursday = Weeks[index].WeekStart.AddDays(3);
        var month = thursday.Year == _loadedYear ? new DateOnly(thursday.Year, thursday.Month, 1)
            : thursday.Year < _loadedYear ? new DateOnly(_loadedYear, 1, 1)
            : new DateOnly(_loadedYear, 12, 1);
        if (month == _visibleMonth)
            return;
        _visibleMonth = month;
        UpdateTitle();
        OnPropertyChanged(nameof(FocusedMonthOffset));
    }

    private void UpdateTitle() => Title = $"{_visibleMonth.Month}월 타임라인";

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

/// <summary>One packed band: the cards that share this grid row across different week columns, plus the band's
/// full track width. The pixel layout (the band's width and its cards' offsets) is recomputed in place when
/// the column width changes (a viewport resize), so the cards — and their state — are never recreated just to
/// restretch; <see cref="TrackWidth"/> is observable so the binding updates.</summary>
public sealed partial class TimelineBandViewModel : ObservableObject
{
    private double _weekWidth;
    private int _weekCount;

    /// <summary>The cards in this band, each in a different week column (so they never overlap).</summary>
    public IReadOnlyList<WeeklyTimelineRowViewModel> Cards { get; }

    /// <summary>The band's full width, spanning every week column.</summary>
    public double TrackWidth => _weekCount * _weekWidth;

    public TimelineBandViewModel(IReadOnlyList<WeeklyTimelineRowViewModel> cards, int weekCount, double weekWidth)
    {
        Cards = cards;
        _weekCount = weekCount;
        _weekWidth = weekWidth;
    }

    /// <summary>Recomputes the layout for a new column width (a viewport resize): restretches the band and
    /// each of its cards in place, without recreating anything.</summary>
    public void ApplyLayout(double weekWidth, int weekCount)
    {
        _weekWidth = weekWidth;
        _weekCount = weekCount;
        OnPropertyChanged(nameof(TrackWidth));
        foreach (var card in Cards)
            card.ApplyLayout(weekWidth);
    }
}

/// <summary>One card placed at its ISO-week column within a band. The pixel layout is recomputed in place when
/// the column width changes, so the card view model — and its state — is never recreated just to restretch;
/// the layout properties are <see cref="ObservableObject"/> so the bindings update.</summary>
public sealed partial class WeeklyTimelineRowViewModel : ObservableObject
{
    private double _weekWidth;

    public TaskRowViewModel Row { get; }

    /// <summary>The card's week column index within the band's range — also what packs cards into bands (a
    /// task drops into the next free band of its own column).</summary>
    public int WeekIndex { get; }

    /// <summary>Left offset of the card, placed at its ISO-week column (a point, not a span). The card is
    /// left-aligned with this as its left margin (built into a Thickness by the page). Kept a plain double
    /// here because the view-model layer doesn't reference WinUI's Thickness.</summary>
    public double StartOffset => WeekIndex * _weekWidth + 8;

    /// <summary>Card width — one week-column wide, minus the gutters.</summary>
    public double CardWidth => _weekWidth - 16;

    public WeeklyTimelineRowViewModel(TaskRowViewModel row, DateOnly? whenDate, DateOnly rangeStart, DateOnly rangeEnd, double weekWidth)
    {
        Row = row;
        // Clamp the task's date to the visible range, then locate its week column from the Monday of that
        // clamped date relative to the range start.
        var date = whenDate ?? rangeStart;
        var clamped = date < rangeStart ? rangeStart : date > rangeEnd ? rangeEnd : date;
        WeekIndex = (WeeklyTimelineViewModel.MondayOf(clamped).DayNumber - rangeStart.DayNumber) / 7;
        _weekWidth = weekWidth;
    }

    /// <summary>Recomputes the pixel layout for a new column width, raising the derived bindings without
    /// recreating the card.</summary>
    public void ApplyLayout(double weekWidth)
    {
        _weekWidth = weekWidth;
        OnPropertyChanged(nameof(StartOffset));
        OnPropertyChanged(nameof(CardWidth));
    }
}
