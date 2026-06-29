using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cue.Domain;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;

namespace Cue.ViewModels;

/// <summary>Which index-backed list this view shows.</summary>
public enum TaskListMode
{
    /// <summary>Home / 모든 할 일 — every active task regardless of group, with completed rows dimmed.</summary>
    AllTasks,

    /// <summary>Today — active tasks with a When date today or earlier, with completed rows dimmed.</summary>
    Today,

    /// <summary>Active tasks with a When date on a future day, with completed rows dimmed.</summary>
    Upcoming,

    /// <summary>Active tasks without a When date — the "언젠가" bucket, with completed rows dimmed.</summary>
    Anytime,

    /// <summary>Completed tasks.</summary>
    Logbook,

    /// <summary>Prioritized tasks (P1–P4), grouped by priority.</summary>
    Priority,

    /// <summary>Active tasks belonging to one task group, with completed rows dimmed.</summary>
    TaskGroup,

    /// <summary>Active tasks carrying one tag, with completed rows dimmed.</summary>
    Tag,

    /// <summary>Active tasks in no group at all — the 그룹 없음 collection point for unfiled captures.</summary>
    NoTaskGroup,

    /// <summary>Active tasks carrying no tag — the 태그 없음 collection point for unfiled captures.</summary>
    NoTag,
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
    private readonly IReorderService _reorder;
    private readonly IRecurringTaskService _recurrence;
    private readonly TimeProvider _clock;
    private readonly string _timeZoneId;
    private readonly TimeZoneInfo _timeZone;
    private readonly INavDataChangeNotifier _navNotifier;
    private readonly IListDisplayPreferences? _listPreferences;

    /// <summary>Whether a task completed today should linger dimmed in its place on the active list until
    /// the day rolls over (the "완료한 일 당일 표시" preference). Read live from the injected preferences so a
    /// fresh list (recreated when the user returns from Settings) picks up a toggled value; null prefs (tests)
    /// mean off — the open-only default.</summary>
    private bool KeepCompletedToday => _listPreferences?.KeepCompletedForToday ?? false;

    // Serializes reorder persists so a fast run of drops can't interleave their rank writes.
    private readonly SemaphoreSlim _reorderGate = new(1, 1);

    // Serializes completion toggles so concurrent/rapid checks can't reorder their saves.
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    private TaskListMode _mode = TaskListMode.AllTasks;
    private Guid? _filterId;

    // How many completed rows the "완료한 일" section pages in per batch — the first expand realizes one
    // batch, each "더 보기" adds another. Sized so a typical session never builds more completed
    // TaskRowViewModels than fit a screenful of scrolling, while a few hundred finished tasks stay cheap.
    private const int CompletedPageSize = 100;

    // The completed-section paging window for the current view: how many completed rows are currently
    // requested (0 while collapsed and never opened). A refresh re-fetches this same window so an opened
    // section keeps its rows; SetNavigation resets it to 0 so each view opens collapsed and unrealized.
    private int _completedWindow;

    // Fetches a [0, limit) page of the current view's completed rows, set per load to the mode's query
    // (Today / group / tag). Null on views with no completed section.
    private Func<int, Task<IReadOnlyList<TaskListItem>>>? _completedPager;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    /// <summary>Priority sections for the 중요도 (priority) view — one of the sectioned lists.</summary>
    public ObservableCollection<PrioritySectionViewModel> PrioritySections { get; } = new();

    /// <summary>Date sections for the 완료한 일 (Logbook) view — completed tasks grouped by day.</summary>
    public ObservableCollection<DateSectionViewModel> LogbookSections { get; } = new();

    /// <summary>The collapsible "완료한 일" section beneath the open list in the Today, group, and tag
    /// views. Empty (and hidden) in every other view; it stays a single reused instance so its
    /// collapsed/expanded state survives list refreshes.</summary>
    public CompletedSectionViewModel CompletedSection { get; } = new();

    /// <summary>Raised right after a task is completed from an active list, so the View can run the in-row
    /// acknowledgement timing. Carries the completed row and the recurrence outcome: a non-null local date
    /// is the <b>next occurrence</b> of a repeating task that rolled to its next cycle (the View refreshes
    /// the row in place to that date); <c>null</c> is a terminal completion (one-off or exhausted series,
    /// which folds away). The View then calls <see cref="FinalizeCompletionAsync"/> to reload.</summary>
    public event Action<TaskRowViewModel, DateOnly?>? CompletionAcknowledged;

    /// <summary>Raised after a quick-add creates a task that does not appear on the current list because it
    /// is pinned to a day other than today (e.g. "내일 …" typed on the 오늘 할 일 list). The View surfaces a
    /// snackbar pointing to 모든 할 일 so the just-created task isn't silently lost from view.</summary>
    public event Action? OffscreenTaskCreated;

    public TaskDetailViewModel Detail { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string QuickAddText { get; set; }

    /// <summary>Accent-colored prefix of the quick-add placeholder — the group/tag name in a filtered
    /// view, empty otherwise. Rendered as a separate <c>Run</c> so only the name carries the accent.</summary>
    [ObservableProperty]
    public partial string QuickAddPlaceholderName { get; set; } = string.Empty;

    /// <summary>Normal-colored remainder of the quick-add placeholder (e.g. "할 일 입력하기"). Carries a
    /// leading space when <see cref="QuickAddPlaceholderName"/> precedes it.</summary>
    [ObservableProperty]
    public partial string QuickAddPlaceholderSuffix { get; set; } = "할 일 입력하기";

    /// <summary>Drives the custom placeholder overlay's visibility — shown only while the box is empty.</summary>
    public bool QuickAddIsEmpty => string.IsNullOrEmpty(QuickAddText);

    partial void OnQuickAddTextChanged(string value) => OnPropertyChanged(nameof(QuickAddIsEmpty));

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsStandardList { get; set; } = true;

    [ObservableProperty]
    public partial bool IsTaskGroupMode { get; set; }

    /// <summary>True when the list is rendered as priority sections (the 중요도 view's P1–P4 buckets)
    /// rather than one flat list.</summary>
    [ObservableProperty]
    public partial bool IsPrioritySectioned { get; set; }

    /// <summary>True when the list is rendered as the 완료한 일 (Logbook) view's date sections.</summary>
    [ObservableProperty]
    public partial bool IsLogbookSectioned { get; set; }

    [ObservableProperty]
    public partial string TitleCaption { get; set; } = string.Empty;

    public bool HasTitleCaption => TitleCaption.Length > 0;
    public bool CanQuickAdd => _mode is not (TaskListMode.Logbook or TaskListMode.Priority);

    public TaskListViewModel(ITaskStore store, ITaskIndex index, IDateParser parser, IReorderService reorder, IRecurringTaskService recurrence, TimeProvider clock, TimeZoneInfo zone, INavDataChangeNotifier navNotifier, SaveFailureCoordinator? coordinator = null, IListDisplayPreferences? listPreferences = null)
    {
        _store = store;
        _index = index;
        _parser = parser;
        _reorder = reorder;
        _recurrence = recurrence;
        _clock = clock;
        _timeZoneId = zone.Id;
        _timeZone = zone;
        _navNotifier = navNotifier;
        _listPreferences = listPreferences;

        Title = "모든 할 일";
        QuickAddText = string.Empty;
        // The completed section pages its rows in on demand; this is the callback its header toggle and
        // "더 보기" affordance invoke to realize the next batch.
        CompletedSection.LoadMoreRequested = LoadMoreCompletedAsync;
        Detail = new TaskDetailViewModel(store, index, reorder, recurrence, clock, zone, LoadAsync, navNotifier, coordinator);
        // Clear the row selection accent when the detail panel closes.
        Detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskDetailViewModel.IsOpen) && !Detail.IsOpen)
                ApplySelection(null);
        };
    }

    /// <summary>Marks the row whose task is <paramref name="id"/> selected and clears the rest.</summary>
    private void ApplySelection(Guid? id)
    {
        foreach (var row in AllRows())
            row.IsSelected = row.Id == id;
    }

    /// <summary>Every realized task row across the flat list and all priority sections. Checklist rows
    /// are not included — they are not selectable.</summary>
    private IEnumerable<TaskRowViewModel> AllRows()
    {
        foreach (var row in Tasks) yield return row;
        foreach (var row in CompletedSection.Tasks) yield return row;
        foreach (var section in PrioritySections)
            foreach (var row in section.Tasks) yield return row;
        foreach (var section in LogbookSections)
            foreach (var row in section.Tasks) yield return row;
    }

    /// <summary>Switches which index view this list reflects, and retitles accordingly.</summary>
    public void SetNavigation(TaskListNavigation navigation)
    {
        _mode = navigation.Mode;
        _filterId = navigation.FilterId;
        Title = navigation.Title ?? navigation.Mode switch
        {
            TaskListMode.AllTasks => "모든 할 일",
            TaskListMode.Today => "오늘 할 일",
            TaskListMode.Upcoming => "앞으로 할 일",
            TaskListMode.Anytime => "언젠가 할 일",
            TaskListMode.Logbook => "완료한 일",
            TaskListMode.Priority => "중요도",
            TaskListMode.TaskGroup => "그룹",
            TaskListMode.Tag => "태그",
            TaskListMode.NoTaskGroup => "그룹 없음",
            TaskListMode.NoTag => "태그 없음",
            _ => throw new ArgumentOutOfRangeException(nameof(navigation)),
        };
        // The quick-add placeholder echoes the active view: a plain "할 일 입력하기" everywhere, and an
        // accent-colored prefix (its own Run) ahead of the suffix in a dated/filtered view — "오늘" on the
        // Today list, the group/tag name in a filtered view.
        (QuickAddPlaceholderName, QuickAddPlaceholderSuffix) = _mode switch
        {
            TaskListMode.Today => ("오늘", " 할 일 입력하기"),
            TaskListMode.TaskGroup or TaskListMode.Tag => (Title, " 할 일 입력하기"),
            _ => (string.Empty, "할 일 입력하기"),
        };

        TitleCaption = string.Empty;
        OnPropertyChanged(nameof(HasTitleCaption));
        IsTaskGroupMode = _mode == TaskListMode.TaskGroup;
        IsPrioritySectioned = _mode is TaskListMode.Priority;
        IsLogbookSectioned = _mode is TaskListMode.Logbook;
        IsStandardList = !IsPrioritySectioned && !IsLogbookSectioned;

        // The completed section's collapsed/expanded state and title are reset when the view changes, so
        // it always opens fresh and collapsed; LoadAsync repopulates and re-titles it for the new mode.
        // The paging window resets too, so the new view opens with no realized completed rows — only its
        // count — and the first expand pages the first batch back in.
        CompletedSection.IsExpanded = false;
        _completedWindow = 0;
        CompletedSection.Title = _mode == TaskListMode.Today ? "오늘 완료한 일" : "완료한 일";
        OnPropertyChanged(nameof(CanQuickAdd));
    }

    /// <summary>Quick-add: parse the line, create + save a task, then refresh from the index.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (!CanQuickAdd) return;
        var text = QuickAddText?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var parsed = _parser.Parse(text, _clock.GetUtcNow(), _timeZoneId);

        // The parser's When is used as-is when it recognized any placement, including explicit
        // Unscheduled markers ("언젠가") and recurrence anchors. Only a genuinely dateless line gets
        // list placement; Today pins it to today and other lists leave it Unscheduled.
        var when = QuickAddContext.Apply(parsed.When, parsed.WhenAssigned, _mode, _clock.GetUtcNow(), _timeZone);

        // A task given only a date with no explicit time is registered as an all-day (종일) event —
        // marked explicitly via ScheduledWhen.AllDay, which the detail panel reads back as 종일. This
        // applies to a date-only recurrence too ("매주 금요일"): the rule's anchor keeps its concrete time
        // (00:00) so the recurrence engine can evaluate it, but the task's own When carries no meaningful
        // time, so the list shows the day alone and completion advances stay all-day (IsAllDay preserved).
        if (when.Kind == WhenKind.OnDate && !parsed.WhenHasTime)
            when = AllDay(when);

        // 반복 needs a date to repeat from. A line can parse a recurrence yet have no When — e.g. an
        // explicit "언젠가 매주 금요일", where the Unscheduled marker wins and the anchor is never promoted
        // to a When. An unanchorable repeat is meaningless, so drop the rule rather than store it.
        var recurrence = when.Kind == WhenKind.OnDate ? parsed.Recurrence : null;

        var task = new TaskItem
        {
            Title = parsed.Title,
            When = when,
            Recurrence = recurrence,
            TaskGroupId = _mode == TaskListMode.TaskGroup ? _filterId : null,
            // New tasks append to the end of the list the user is currently looking at.
            SortOrder = _reorder.AppendRank(VisibleRowRanks()),
        };
        if (_mode == TaskListMode.Tag && _filterId is { } tagId)
            task.TagIds.Add(tagId);

        await _store.SaveAsync(task);
        QuickAddText = string.Empty;
        await LoadAsync();
        // A new open task bumps its group/tag (or the 없음 bucket) count in the sidebar.
        _navNotifier.NotifyCountsChanged();
        NotifyIfCreatedOffscreen(task.Id, when);
    }

    /// <summary>After a quick-add reloads the list, warn (via <see cref="OffscreenTaskCreated"/>) when the
    /// new task is pinned to a day other than today yet isn't shown on the current screen — the user typed
    /// an explicit date ("내일", "3월 5일") on a list that doesn't show it, so the task they just created
    /// would otherwise vanish without a trace. A dateless or today-dated task, or one that did land in the
    /// current list, raises nothing.</summary>
    private void NotifyIfCreatedOffscreen(Guid id, ScheduledWhen when)
    {
        if (when.Kind != WhenKind.OnDate || when.Date is not { } date)
            return;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _timeZone).DateTime);
        var whenDay = DateOnly.FromDateTime(date.ToLocal().DateTime);
        if (whenDay == today)
            return;
        if (AllRows().Any(row => row.Id == id))
            return; // the new task is visible on this screen — no need to point elsewhere
        OffscreenTaskCreated?.Invoke();
    }

    /// <summary>
    /// The wall-clock delay from now until just after the next local day boundary (00:00 in the list's own
    /// time zone, plus a one-second cushion so a timer firing on the tick lands inside the new day). The
    /// page arms a one-shot refresh at this delay so a day rollover while the app stays open re-evaluates
    /// every date-relative state: 오늘/overdue membership, the Logbook's 오늘/어제 headings, and — the reason
    /// this exists — an "ahead of schedule" recurring row whose next cycle has now come due, which then
    /// reloads unticked. Computed against the same clock + zone the index uses for "today", so the refresh
    /// fires exactly when the index's <c>IsAheadOfSchedule</c> comparison flips.
    /// </summary>
    public TimeSpan DelayUntilNextDay()
    {
        var nowLocal = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _timeZone);
        var nextMidnight = nowLocal.Date.AddDays(1); // local 00:00 tomorrow
        return (nextMidnight - nowLocal.DateTime) + TimeSpan.FromSeconds(1);
    }

    /// <summary>Reloads the list from the index for the current mode. Completed work is excluded from the
    /// open list and surfaced separately: a collapsible "완료한 일" section (Today / group / tag), the
    /// priority buckets (open only), or the Logbook's date sections.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        // Each branch reconciles the live collections in place rather than clearing and rebuilding them,
        // so a refresh (every save routes through here) reuses unchanged row instances. That keeps scroll
        // position, focus, selection, a drag in progress, and the entrance animation intact. The branches
        // not in use are emptied so a switch between views leaves no stale rows behind.
        // When the "완료한 일 당일 표시" preference is on, active lists also carry today's completed work
        // (dimmed, in place); the paired "완료한 일" sections then drop today's rows so nothing is doubled.
        var keep = KeepCompletedToday;

        switch (_mode)
        {
            case TaskListMode.Priority:
                SyncRows(Tasks, []);
                ClearCompletedSection();
                SyncLogbookSections([]);
                SyncPrioritySections(await _index.GetByPriorityAsync(keep));
                IsEmpty = PrioritySections.Count == 0;
                break;

            case TaskListMode.Logbook:
                SyncRows(Tasks, []);
                ClearCompletedSection();
                SyncPrioritySections([]);
                SyncLogbookSections(await _index.GetLogbookAsync());
                IsEmpty = LogbookSections.Count == 0;
                break;

            case TaskListMode.Today:
                // With keep on, every task completed today is already shown in place, so the
                // "오늘 완료한 일" section would only duplicate it — skip it entirely (count 0, empty pager).
                Func<int, Task<IReadOnlyList<TaskListItem>>>? todayCompletedPager =
                    keep ? null : limit => _index.GetTodayCompletedAsync(limit);
                await LoadFlatWithCompletedAsync(
                    await _index.GetTodayAsync(keep),
                    keep ? 0 : await _index.GetTodayCompletedCountAsync(),
                    todayCompletedPager);
                break;

            case TaskListMode.TaskGroup:
                var groupId = RequiredFilterId();
                await LoadFlatWithCompletedAsync(
                    await _index.GetByTaskGroupAsync(groupId, keep),
                    await _index.GetCompletedCountByTaskGroupAsync(groupId, excludeCompletedToday: keep),
                    limit => _index.GetCompletedByTaskGroupAsync(groupId, limit, excludeCompletedToday: keep));
                break;

            case TaskListMode.Tag:
                var tagId = RequiredFilterId();
                await LoadFlatWithCompletedAsync(
                    await _index.GetByTagAsync(tagId, keep),
                    await _index.GetCompletedCountByTagAsync(tagId, excludeCompletedToday: keep),
                    limit => _index.GetCompletedByTagAsync(tagId, limit, excludeCompletedToday: keep));
                break;

            default:
                var items = _mode switch
                {
                    TaskListMode.AllTasks => await _index.GetAllActiveAsync(keep),
                    TaskListMode.Upcoming => await _index.GetUpcomingAsync(keep),
                    TaskListMode.Anytime => await _index.GetAnytimeAsync(keep),
                    TaskListMode.NoTaskGroup => await _index.GetWithoutTaskGroupAsync(keep),
                    TaskListMode.NoTag => await _index.GetWithoutTagAsync(keep),
                    _ => throw new ArgumentOutOfRangeException(),
                };
                SyncPrioritySections([]);
                SyncLogbookSections([]);
                ClearCompletedSection();   // these views exclude completed work outright
                SyncRows(Tasks, items);
                IsEmpty = Tasks.Count == 0;
                break;
        }

        // Re-apply the selection accent: reused rows keep theirs, but a freshly inserted row needs it set.
        ApplySelection(Detail.IsOpen ? Detail.CurrentTaskId : null);
    }

    /// <summary>Loads a flat open list plus its collapsible "완료한 일" section (Today / group / tag). The
    /// completed section is lazy: <paramref name="completedTotal"/> (a COUNT) drives its header and
    /// visibility, while only the rows inside the current paging window are realized through
    /// <paramref name="completedPage"/> — none while collapsed, the opened batch(es) once expanded. A
    /// refresh re-fetches that same window, so an opened section keeps its rows.</summary>
    private async Task LoadFlatWithCompletedAsync(
        IReadOnlyList<TaskListItem> open,
        int completedTotal,
        Func<int, Task<IReadOnlyList<TaskListItem>>>? completedPage)
    {
        SyncPrioritySections([]);
        SyncLogbookSections([]);
        SyncRows(Tasks, open);

        _completedPager = completedPage;
        CompletedSection.TotalCount = completedTotal;

        // Realize only the rows within the current window (0 while collapsed and never opened), capped at
        // the live total so a just-shrunk list can't ask for more than exists. A null pager pairs with a
        // zero total (the Today view when completed work is kept in place), so the window is 0 and unused.
        var window = Math.Min(_completedWindow, completedTotal);
        SyncCompletedSection(window > 0 && completedPage is not null ? await completedPage(window) : []);

        IsEmpty = Tasks.Count == 0 && CompletedSection.TotalCount == 0;
    }

    /// <summary>Pages in the next batch of completed rows for the current view — the callback behind the
    /// section's header toggle (first expand) and its "더 보기" affordance. Grows the window by one page,
    /// re-fetches it from the top (the reconcile reuses the rows already realized), and re-applies the
    /// selection accent to any freshly inserted row.</summary>
    private async Task LoadMoreCompletedAsync()
    {
        if (_completedPager is null || CompletedSection.IsLoading) return;
        CompletedSection.IsLoading = true;
        try
        {
            _completedWindow = Math.Min(_completedWindow + CompletedPageSize, CompletedSection.TotalCount);
            SyncCompletedSection(await _completedPager(_completedWindow));
            ApplySelection(Detail.IsOpen ? Detail.CurrentTaskId : null);
        }
        finally
        {
            CompletedSection.IsLoading = false;
        }
    }

    /// <summary>Clears the completed section for a view that carries none: empties its rows, zeroes its
    /// count (so it hides), and drops the pager so a stale toggle can't realize rows from a prior view.</summary>
    private void ClearCompletedSection()
    {
        _completedPager = null;
        CompletedSection.TotalCount = 0;
        SyncCompletedSection([]);
    }

    /// <summary>
    /// Commits a drag-reorder inside one list. The row has already been moved optimistically in
    /// <paramref name="list"/> (the reorder surface drives the visuals); here we persist the moved
    /// row's new rank through the rank service, which writes <i>only</i> that record — except for the
    /// rare rebalance. Each row's <see cref="TaskRowViewModel.SortOrder"/> is refreshed to the
    /// returned ranks so a following drag computes against current keys. A failed save reloads the
    /// list from the index, snapping it back to the persisted truth.
    /// </summary>
    public async Task PersistReorderAsync(ObservableCollection<TaskRowViewModel> list, Guid movedId)
    {
        await _reorderGate.WaitAsync();
        try
        {
            var ordered = list.Select(row => new RankedItem(row.Id, row.SortOrder)).ToList();
            var result = await _reorder.MoveAsync<TaskItem>(movedId, ordered);
            foreach (var row in list)
                if (result.ChangedRanks.TryGetValue(row.Id, out var rank))
                    row.SortOrder = rank;
        }
        catch
        {
            await LoadAsync();
        }
        finally
        {
            _reorderGate.Release();
        }
    }

    /// <summary>Every task row's current rank across the visible lists — the basis for an append rank.</summary>
    private IEnumerable<string?> VisibleRowRanks()
    {
        foreach (var row in Tasks) yield return row.SortOrder;
        foreach (var section in PrioritySections)
            foreach (var row in section.Tasks) yield return row.SortOrder;
    }

    /// <summary>Toggles one nested checklist item from the list: loads its owning task, flips the item
    /// by id, and saves the parent through the store. Serialized through the same gate as completion so
    /// rapid checks can't reorder their writes; on failure the checkbox is restored.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleChecklistItemAsync(ChecklistRowViewModel row)
    {
        var isChecked = row.IsChecked;
        await _toggleGate.WaitAsync();
        try
        {
            // Atomic read-modify-write through the store: flip just this item on the latest persisted
            // task, so a concurrent detail-panel metadata save (a different save path) can't clobber the
            // checklist, nor this toggle the metadata. MutateAsync returns null when the task or item is
            // gone, in which case the checkbox is put back.
            var saved = await _store.MutateAsync<TaskItem>(row.ParentTaskId, task =>
            {
                var item = task.Checklist.FirstOrDefault(i => i.Id == row.Id);
                if (item is null) return false;
                item.IsChecked = isChecked;
                return true;
            });
            if (saved is null)
            {
                row.SetCheckedSilently(!isChecked);
                return;
            }
            await LoadAsync();
        }
        catch
        {
            row.SetCheckedSilently(!isChecked);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    /// <summary>Marks a dated When as all-day (종일): keeps its calendar day (pinned to local start of
    /// day) but flags it as carrying no meaningful time, so the UI shows the date alone.</summary>
    private ScheduledWhen AllDay(ScheduledWhen when)
    {
        if (when.Date is not { } zoned) return when;
        var localDate = zoned.ToLocal().Date;
        return ScheduledWhen.AllDay(ZonedDateTime.FromLocal(localDate, _timeZoneId));
    }

    private Guid RequiredFilterId()
        => _filterId ?? throw new InvalidOperationException($"{_mode} navigation requires an id.");

    private TaskRowViewModel CreateRow(TaskListItem item)
    {
        var row = new TaskRowViewModel(item, r => ToggleCompleteCommand.Execute(r)) { IsCompact = _rowsCompact };
        SyncChecklistRows(row, item);
        return row;
    }

    // Tracks the list's current narrow/wide layout so rows created during a reload (a save, a task
    // switch) inherit it without waiting for the next resize.
    private bool _rowsCompact;

    /// <summary>Sets whether rows reflow their right-edge group/tag chips beneath the title. The page
    /// calls this as the list column resizes; the state is remembered so freshly created rows match.</summary>
    public void SetRowsCompact(bool compact)
    {
        _rowsCompact = compact;
        foreach (var row in AllRows())
            row.IsCompact = compact;
    }

    /// <summary>Re-runs the tag chip color bindings on every realized row. Tag label/icon colors are
    /// darkened for the Light theme by a converter that samples the theme once at convert time, so a
    /// runtime theme toggle would otherwise leave the visible rows showing the previous theme's colors
    /// (off-screen rows self-correct when virtualization re-realizes them). The page calls this on
    /// <c>ActualThemeChanged</c>; see <see cref="TaskRowViewModel.RefreshTagColors"/>.</summary>
    public void RefreshTagColorsForTheme()
    {
        foreach (var row in AllRows())
            row.RefreshTagColors();
    }

    // Fixed priority sections for the 중요도 view, in display order. A section is shown only when it has
    // rows. Unprioritized tasks (Priority.None) are intentionally omitted: this view is a lens on ranked
    // work only, and they stay visible in every other list. GetByPriorityAsync still returns them, but
    // they match no section here and are dropped.
    private static readonly (Priority Priority, string Name)[] PriorityBuckets =
    [
        (Priority.P1, "매우 중요"), (Priority.P2, "중요"), (Priority.P3, "보통"), (Priority.P4, "사소"),
    ];

    /// <summary>
    /// Reconciles <paramref name="target"/> in place to match <paramref name="items"/> by id, reusing the
    /// existing row instances: an unchanged row is patched (silently when nothing actually changed), a
    /// moved row is repositioned, a row that's gone is removed, and only a genuinely new row is created.
    /// Each row's nested checklist is re-synced too. This is what lets a save-triggered refresh avoid
    /// recreating the whole list, preserving scroll, focus, selection, and the entrance animation for
    /// unchanged rows. The list is flat — there is no task nesting.
    /// </summary>
    private void SyncRows(ObservableCollection<TaskRowViewModel> target, IReadOnlyList<TaskListItem> items)
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
                }
                else
                {
                    target.Insert(i, CreateRow(item));
                    continue;
                }
            }
            target[i].Update(item);
            SyncChecklistRows(target[i], item);
        }
    }

    /// <summary>Reconciles a row's nested checklist rows in place from its projection, by id — reusing
    /// the existing row instances exactly like <see cref="SyncRows"/>. A clear-and-rebuild here would
    /// tear down and re-realize every checkbox on every refresh (and every save routes through one):
    /// that replayed the entrance animation on each item (a flicker on any reload, e.g. a task switch)
    /// and — worse — destroyed the very checkbox the user just toggled mid-interaction, so its checked /
    /// dim state never stuck and it couldn't be unchecked. Reusing instances avoids all of that; only a
    /// genuinely new or retitled item is created.</summary>
    private void SyncChecklistRows(TaskRowViewModel row, TaskListItem item)
    {
        var target = row.ChecklistItems;
        var items = item.Checklist ?? (IReadOnlyList<TaskListChecklistItem>)Array.Empty<TaskListChecklistItem>();

        var desired = new HashSet<Guid>(items.Count);
        foreach (var checklistItem in items) desired.Add(checklistItem.Id);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i].Id))
                target.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            var checklistItem = items[i];
            var existing = IndexOfChecklistRow(target, checklistItem.Id);
            if (existing >= 0)
            {
                // The row's title is immutable, so reuse it only while the title is unchanged and just
                // patch the checked state (silently — patching must not fire the toggle/save callback).
                // A retitled item falls through to a fresh row.
                if (target[existing].Title == ChecklistRowViewModel.DisplayTitle(checklistItem.Title))
                {
                    if (existing != i) target.Move(existing, i);
                    target[i].SetCheckedSilently(checklistItem.IsChecked);
                    continue;
                }
                target.RemoveAt(existing);
            }
            target.Insert(i, new ChecklistRowViewModel(item.Id, checklistItem, r => ToggleChecklistItemCommand.Execute(r)));
        }
    }

    private static int IndexOfChecklistRow(ObservableCollection<ChecklistRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id) return i;
        return -1;
    }

    private static int IndexOfRow(ObservableCollection<TaskRowViewModel> rows, Guid id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id) return i;
        return -1;
    }

    /// <summary>Reconciles the priority sections (the only sectioned view) in place: keeps a section per
    /// priority that has rows, in P1→P4 order, reusing existing section instances and syncing each
    /// section's rows. Pass an empty list to clear the sections for an unsectioned view.</summary>
    private void SyncPrioritySections(IReadOnlyList<TaskListItem> items)
    {
        var desired = new List<(string Name, List<TaskListItem> Items)>();
        foreach (var (priority, name) in PriorityBuckets)
        {
            var bucket = items.Where(item => item.Priority == priority).ToList();
            if (bucket.Count > 0) desired.Add((name, bucket));
        }

        var desiredNames = new HashSet<string>(desired.Select(section => section.Name));
        for (var i = PrioritySections.Count - 1; i >= 0; i--)
            if (!desiredNames.Contains(PrioritySections[i].Name))
                PrioritySections.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            var (name, bucket) = desired[i];
            if (i >= PrioritySections.Count || PrioritySections[i].Name != name)
            {
                var existing = IndexOfPrioritySection(name);
                if (existing >= 0) PrioritySections.Move(existing, i);
                else PrioritySections.Insert(i, new PrioritySectionViewModel(name));
            }
            SyncRows(PrioritySections[i].Tasks, bucket);
        }
    }

    /// <summary>Reconciles the single collapsed "완료한 일" section's rows in place from the completed
    /// projection. Pass an empty list to clear it (so it hides) in views that don't carry one.</summary>
    private void SyncCompletedSection(IReadOnlyList<TaskListItem> items)
        => SyncRows(CompletedSection.Tasks, items);

    /// <summary>
    /// Reconciles the Logbook's date sections in place: one section per local completion <i>day</i>, newest
    /// day first, headed 오늘 / 어제 / "M월 d일" (this year) / "yyyy년 M월 d일" (an earlier year). Sections are
    /// keyed and reused by their full <see cref="DateOnly"/> date — never by the rendered heading, which
    /// drops the year on older days and would otherwise merge same-day dates a year apart (2025-06-22 and
    /// 2026-06-22 both read "6월 22일"). A reused section's heading is refreshed so it rolls 오늘 → 어제 as the
    /// day turns over. Pass an empty list to clear the sections for an unsectioned view.
    /// </summary>
    private void SyncLogbookSections(IReadOnlyList<TaskListItem> items)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _timeZone).DateTime);

        // Group by the local completion day (a DateOnly), preserving the index's newest-first order.
        // completed_at is set on every Logbook row, but guard a null defensively — it falls in the
        // DateOnly.MinValue bucket (heading "") and keeps its position.
        var desired = new List<(DateOnly Day, List<TaskListItem> Items)>();
        var byDay = new Dictionary<DateOnly, List<TaskListItem>>();
        foreach (var item in items)
        {
            var day = item.CompletedAt is { } at
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _timeZone).DateTime)
                : DateOnly.MinValue;
            if (!byDay.TryGetValue(day, out var bucket))
            {
                bucket = new List<TaskListItem>();
                byDay[day] = bucket;
                desired.Add((day, bucket));
            }
            bucket.Add(item);
        }

        var desiredDays = new HashSet<DateOnly>(desired.Select(section => section.Day));
        for (var i = LogbookSections.Count - 1; i >= 0; i--)
            if (!desiredDays.Contains(LogbookSections[i].Date))
                LogbookSections.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            var (day, bucket) = desired[i];
            if (i >= LogbookSections.Count || LogbookSections[i].Date != day)
            {
                var existing = IndexOfLogbookSection(day);
                if (existing >= 0) LogbookSections.Move(existing, i);
                else LogbookSections.Insert(i, new DateSectionViewModel(day, DayHeading(day, today)));
            }
            // Refresh the heading on the (possibly reused) section so a day rollover re-titles 오늘 → 어제.
            LogbookSections[i].DisplayTitle = DayHeading(day, today);
            SyncRows(LogbookSections[i].Tasks, bucket);
        }
    }

    private int IndexOfLogbookSection(DateOnly day)
    {
        for (var i = 0; i < LogbookSections.Count; i++)
            if (LogbookSections[i].Date == day) return i;
        return -1;
    }

    /// <summary>A Logbook day heading: 오늘 for the current day, 어제 for the day before, "M월 d일" for another
    /// day in the current year, and "yyyy년 M월 d일" for an earlier year — so same-day dates in different
    /// years read distinctly. The defensive DateOnly.MinValue bucket (a row with no completion instant)
    /// renders blank.</summary>
    private static string DayHeading(DateOnly day, DateOnly today)
    {
        if (day == DateOnly.MinValue) return "";
        if (day == today) return "오늘";
        if (day == today.AddDays(-1)) return "어제";
        var ko = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
        return day.Year == today.Year
            ? day.ToString("M월 d일", ko)
            : day.ToString("yyyy년 M월 d일", ko);
    }

    private int IndexOfPrioritySection(string name)
    {
        for (var i = 0; i < PrioritySections.Count; i++)
            if (PrioritySections[i].Name == name) return i;
        return -1;
    }

    // Row context-menu actions (move group / tag / rename / delete)

    /// <summary>The live task record by id, so a row context menu can reflect its current group and
    /// tags. Reads the file source of truth.</summary>
    public Task<TaskItem?> GetTaskAsync(Guid id) => _store.GetAsync<TaskItem>(id);

    /// <summary>Active groups, for the row context menu's "move to group" submenu.</summary>
    public Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync() => _index.GetTaskGroupsAsync();

    /// <summary>Active tags, for the row context menu's tag submenu.</summary>
    public Task<IReadOnlyList<TagListItem>> GetTagsAsync() => _index.GetTagsAsync();

    /// <summary>Moves a task into a group, or to the Cue home when <paramref name="taskGroupId"/> is
    /// null, then refreshes. A no-op if the task is gone or already there.</summary>
    public async Task MoveTaskToTaskGroupAsync(Guid taskId, Guid? taskGroupId)
    {
        // Atomic read-modify-write so moving the task touches only TaskGroupId on the latest record,
        // never overwriting a field a concurrent save path changed. Returning false leaves it a no-op
        // (and skips the reload) when the task is already in the target group.
        var moved = await _store.MutateAsync<TaskItem>(taskId, task =>
        {
            if (task.TaskGroupId == taskGroupId) return false;
            task.TaskGroupId = taskGroupId;
            return true;
        });
        if (moved is not null)
        {
            await LoadAsync();
            // The task left one group's count and joined another's (or the 그룹 없음 bucket).
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Toggles <paramref name="tagId"/> on the task: adds it when absent, removes it when
    /// present. The task's other tags are left in place (a task may carry any number of tags). Then
    /// refreshes.</summary>
    public async Task ToggleTaskTagAsync(Guid taskId, Guid tagId)
    {
        // Atomic read-modify-write so the tag change touches only TagIds on the latest record. Tags are
        // a multi-select set: flipping this one must not disturb the others, so we add or remove just it.
        var changed = await _store.MutateAsync<TaskItem>(taskId, task =>
        {
            if (!task.TagIds.Remove(tagId)) task.TagIds.Add(tagId);
            return true;
        });
        if (changed is not null)
        {
            await LoadAsync();
            // The tag gained or lost this task, shifting its sidebar count (and the 태그 없음 bucket).
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Removes <paramref name="tagId"/> from the task, leaving its other tags in place, then
    /// refreshes. A no-op (no reload) if the task didn't carry that tag. Used by the context menu's
    /// 태그 지우기 picker, where the user selects a single tag to drop from a multi-tag task.</summary>
    public async Task RemoveTaskTagAsync(Guid taskId, Guid tagId)
    {
        // Unlike a toggle, this only ever removes: if a concurrent edit already dropped the tag we leave
        // it gone rather than re-adding it.
        var changed = await _store.MutateAsync<TaskItem>(taskId, task => task.TagIds.Remove(tagId));
        if (changed is not null)
        {
            await LoadAsync();
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Removes every tag from the task, then refreshes. A no-op (no reload) if it had none.</summary>
    public async Task ClearTaskTagsAsync(Guid taskId)
    {
        var changed = await _store.MutateAsync<TaskItem>(taskId, task =>
        {
            if (task.TagIds.Count == 0) return false;
            task.TagIds.Clear();
            return true;
        });
        if (changed is not null)
        {
            await LoadAsync();
            _navNotifier.NotifyCountsChanged();
        }
    }

    /// <summary>Renames a task, then refreshes. A blank name is ignored.</summary>
    public async Task RenameTaskAsync(Guid taskId, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;
        // Atomic read-modify-write so the rename touches only Title on the latest record.
        var renamed = await _store.MutateAsync<TaskItem>(taskId, task => { task.Title = trimmed; return true; });
        if (renamed is not null) await LoadAsync();
    }

    /// <summary>Ends a recurring series straight from a row's context menu (반복 종료): completes the
    /// series so it leaves the active lists and lands in the Logbook, closes the detail panel if this task
    /// was open, and refreshes. A no-op for a non-recurring task. The recorded cycle history is preserved.</summary>
    public async Task EndSeriesAsync(Guid id)
    {
        await _recurrence.EndSeriesAsync(id, _clock.GetUtcNow());
        if (Detail.IsOpen && Detail.CurrentTaskId == id) Detail.Close();
        await LoadAsync();
        _navNotifier.NotifyCountsChanged();
    }

    /// <summary>Soft-deletes a task (its embedded checklist goes with it), closes the detail panel if
    /// this task was open, and refreshes the list.</summary>
    [RelayCommand]
    public async Task DeleteTaskAsync(Guid id)
    {
        await _store.DeleteAsync<TaskItem>(id);
        if (Detail.IsOpen && Detail.CurrentTaskId == id) Detail.Close();
        await LoadAsync();
        // A deleted open task drops out of its group/tag counts in the sidebar.
        _navNotifier.NotifyCountsChanged();
    }

    [RelayCommand]
    private async Task SelectTaskAsync(Guid id)
    {
        if (Detail.IsOpen && Detail.CurrentTaskId != id)
        {
            // Switching tasks swaps the panel's content in place — the panel never hides, so it doesn't
            // play its slide-out only to slide back in (and the list column never reflows wider then
            // narrow again). Persist and drain the outgoing task's pending edits in background so we don't
            // block the task switch transition.
            var flushTask = Detail.FlushAsync();
            ObserveFlushTask(flushTask);
        }

        await Detail.OpenAsync(id);
        ApplySelection(id);
    }

    /// <summary>
    /// Applies a row's completion change to the store. Serialized through a gate so rapid toggles can't
    /// reorder their writes (concurrent executions are allowed so none are dropped — they queue on the
    /// gate). On <b>completing</b> a row from an active list the row is <i>not</i> reloaded away at once:
    /// it enters the acknowledgement state (an in-row undo note) and the View runs the fold timing before
    /// calling <see cref="FinalizeCompletionAsync"/>, which reloads it into the relevant 완료한 일 section.
    /// <b>Un-completing</b> a row reloads immediately: a row that is <see cref="TaskRowViewModel.IsAheadOfSchedule"/>
    /// (a recurring series performed up into the future) rolls its latest cycle back instead of clearing
    /// <see cref="TaskItem.CompletedAt"/> — that, plus the index rendering an ahead row already ticked, is
    /// what stops a recurring task being completed forever into the future. On failure the checkbox is
    /// restored so the UI never disagrees with what's on disk.
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
                // Completion runs through the recurrence service: a repeating task records its current
                // cycle as a history occurrence and advances to its next cycle (returning that next
                // occurrence) without completing the series itself, while a one-off is simply stamped done
                // (returning null). The task's embedded checklist resets on a recurring advance; the cycle's
                // ticked state was frozen on the occurrence record.
                var nextOccurrence = await _recurrence.CompleteAsync(row.Id, _clock.GetUtcNow());
                _navNotifier.NotifyCountsChanged();

                if (KeepCompletedToday && nextOccurrence is null)
                {
                    // "완료한 일 당일 표시" on, terminal completion: the finished one-off stays dimmed right
                    // where it sat for the rest of the day, so there is nothing to whisk away and no undo bar
                    // to offer (re-tick the dimmed row to reopen it). Just reconcile in place.
                    await FinalizeCompletionAsync(row);
                }
                else
                {
                    // Hand off to the View for the acknowledgement moment. A terminal completion (nextOccurrence
                    // == null) lets the check + dim register, swaps in the undo note, then folds the row away and
                    // calls FinalizeCompletionAsync — dropping it into its 완료한 일 section. A repeating
                    // completion (next occurrence non-null) keeps the row in place and just reconciles it: it
                    // stays ticked + dimmed once the advance lands in the future (it is now ahead of schedule),
                    // or returns unchecked if another cycle is still due. No reload here — that would whisk a
                    // terminal completion away before its acknowledgement.
                    CompletionAcknowledged?.Invoke(row, nextOccurrence);
                }
            }
            else if (row.IsAheadOfSchedule)
            {
                // Un-ticking a done-for-now recurring row rolls its latest completed cycle back rather than
                // pushing the series even further forward — the second tick is an undo, never a new advance.
                await UndoLatestRecurringCompletionAsync(row);
            }
            else
            {
                // Un-completing a terminal row (from a 완료한 일 section, one-off or ended series): atomic
                // read-modify-write so clearing completion touches only CompletedAt on the latest record,
                // then reload so it returns to the open list.
                await _store.MutateAsync<TaskItem>(row.Id, task => { task.CompletedAt = null; return true; });
                _navNotifier.NotifyCountsChanged();
                await LoadAsync();
            }
        }
        catch
        {
            // Save failed — put the checkbox back and drop any acknowledgement so it reflects the real
            // (unchanged) state.
            row.EndCompletionAcknowledgement();
            row.SetCompletedSilently(!completed);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    /// <summary>
    /// Rolls back the most recent completed cycle of a recurring series straight from the list — the action
    /// behind un-ticking an "ahead of schedule" row. Finds the series' latest live occurrence and, when it is
    /// a completed cycle, hands it to <see cref="IRecurringTaskService.UndoCompletionAsync"/>, whose own guard
    /// re-checks that it really is the immediate predecessor of the current cycle before committing. Reloads
    /// either way so the row reflects the on-disk state (rolled back to a due cycle, or unchanged if declined).
    /// </summary>
    private async Task UndoLatestRecurringCompletionAsync(TaskRowViewModel row)
    {
        var latest = await _index.GetOccurrencesAsync(row.Id, limit: 1);
        if (latest.Count > 0 && latest[0].Status == OccurrenceStatus.Completed)
            await _recurrence.UndoCompletionAsync(row.Id, latest[0].Id, _clock.GetUtcNow());
        _navNotifier.NotifyCountsChanged();
        // The roll-back moved the series back a cycle, so an open detail panel on it must reload its strip too.
        await SyncOpenDetailForRowAsync(row.Id);
        await LoadAsync();
    }

    /// <summary>Ends a row's completion acknowledgement and reloads the list. For a terminal completion the
    /// just-completed task then leaves the open list and reappears in its 완료한 일 section / Logbook; for a
    /// repeating completion the same-id row is reconciled in place to its next cycle — staying ticked +
    /// dimmed when the series is now ahead of schedule, returning unchecked when another cycle is still due,
    /// or dropped if it has rolled out of the current view's range (e.g. Today). Called by the View once the
    /// fold (terminal) or the brief check-pop settle (repeating) has played.</summary>
    public async Task FinalizeCompletionAsync(TaskRowViewModel row)
    {
        await SyncOpenDetailForRowAsync(row.Id);
        row.EndCompletionAcknowledgement();
        await LoadAsync();
    }

    /// <summary>Keeps the detail panel in sync after a list-driven recurrence change to <paramref name="taskId"/>
    /// (a completion advanced it, or an undo rolled it back). When the panel shows that task and the series is
    /// still open, reopen it so its 반복 기록 strip, current cycle, and reset checklist reflect the new state;
    /// when the task has left the active list (a one-off done, or a series ended/exhausted) close the panel
    /// rather than let it linger on a gone task. A no-op when the panel is closed or showing something else.</summary>
    private async Task SyncOpenDetailForRowAsync(Guid taskId)
    {
        if (!Detail.IsOpen || Detail.CurrentTaskId != taskId)
            return;

        var task = await _store.GetAsync<TaskItem>(taskId);
        if (task is { IsDeleted: false, IsCompleted: false, Recurrence: not null })
        {
            // OpenAsync flushes any in-flight detail edit first, so this can't race the panel's own
            // (serialized) save chains.
            await Detail.OpenAsync(taskId);
        }
        else
        {
            // Flush first so any in-flight edit is persisted before the panel tears down.
            await Detail.FlushAsync();
            Detail.Close();
        }
    }

    /// <summary>Reverses a one-off completion straight from its acknowledgement bar ("실행 취소"): clears
    /// <see cref="TaskItem.CompletedAt"/>, drops the acknowledgement, and reloads so the row returns to
    /// the open list. Undo is offered for one-off completions only — a repeating completion advanced the
    /// series and is not reversed here.</summary>
    public async Task UndoCompletionAsync(TaskRowViewModel row)
    {
        await _toggleGate.WaitAsync();
        try
        {
            await _store.MutateAsync<TaskItem>(row.Id, task => { task.CompletedAt = null; return true; });
            _navNotifier.NotifyCountsChanged();
        }
        finally
        {
            _toggleGate.Release();
        }
        row.SetCompletedSilently(false);
        row.EndCompletionAcknowledgement();
        await LoadAsync();
    }

    private static void ObserveFlushTask(Task task)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } aggEx)
            {
                var _ = aggEx.Flatten();
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
