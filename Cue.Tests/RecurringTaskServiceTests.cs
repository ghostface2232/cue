using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Recurrence;

namespace Cue.Tests;

/// <summary>
/// Exercises the occurrence-record recurrence model: performing a repeating task records its current
/// cycle as a <see cref="RecurrenceOccurrence"/> owned by the series and advances the original one cycle
/// (open, rank/zone/all-day preserved) — it does <i>not</i> complete the task. Skipping records a skipped
/// cycle; ending the series is the only path that completes a recurring task; editing a past cycle never
/// shifts the schedule. Non-recurring and exhausted/unusable-rule tasks fall back to ordinary in-place
/// completion with no occurrence record.
/// </summary>
public sealed class RecurringTaskServiceTests : IAsyncLifetime
{
    private readonly List<string> _roots = new();

    // Fixed "now": Monday 2026-06-22 12:00 UTC, matching the index tests' reference day.
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        return Task.CompletedTask;
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cue-recurring-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private Task<IndexedTaskStore> OpenAsync(string root)
        => IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = root },
            new FixedClock(Now),
            TimeZoneInfo.Utc);

    private static ZonedDateTime OnDayZoned(DateOnly day, string tz = "UTC")
        => ZonedDateTime.FromLocal(new DateTime(day.Year, day.Month, day.Day, 9, 0, 0), tz);

    private static ScheduledWhen OnDay(DateOnly day, string tz = "UTC")
        => ScheduledWhen.On(OnDayZoned(day, tz));

    private static ScheduledWhen AllDayOn(DateOnly day, string tz = "UTC")
        => ScheduledWhen.AllDay(OnDayZoned(day, tz));

    private static RecurrenceRule Daily(DateOnly anchorDay, string tz = "UTC")
        => new("FREQ=DAILY", OnDayZoned(anchorDay, tz));

    private static DateOnly WhenDay(TaskItem task)
        => DateOnly.FromDateTime(task.When.Date!.Value.Utc.UtcDateTime);

    [Fact]
    public async Task CompletingRecurring_RecordsOneOccurrence_AndAdvancesOriginalOneCycle()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "매일 운동",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            SortOrder = "hhhh",
        };
        await store.SaveAsync(task);

        var next = await service.CompleteAsync(task.Id, Now);

        // The service reports the advanced cycle's local date (today → tomorrow) so the UI can show a
        // "다음: …" cue and refresh the row in place rather than fold it away.
        Assert.Equal(Today.AddDays(1), next);

        // The original is still open and advanced exactly one cycle — it is NOT completed (a recurring task
        // is only completed by ending the series). Rank and the recurrence rule are untouched.
        var original = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(original);
        Assert.False(original!.IsCompleted);
        Assert.NotNull(original.Recurrence);
        Assert.Equal("hhhh", original.SortOrder);
        Assert.Equal(Today.AddDays(1), WhenDay(original));

        // No second task is minted — the cycle is a RecurrenceOccurrence owned by the series, not a copy.
        Assert.Single(await store.GetAllAsync<TaskItem>());
        var occurrences = await store.GetAllAsync<RecurrenceOccurrence>();
        var occurrence = Assert.Single(occurrences);
        Assert.Equal(task.Id, occurrence.SeriesId);
        Assert.Equal(OccurrenceStatus.Completed, occurrence.Status);
        Assert.Equal(Now, occurrence.CompletedAt);
        Assert.Equal(Today, DateOnly.FromDateTime(occurrence.When.Date!.Value.Utc.UtcDateTime)); // the completed cycle's own date

        // The recurring cycle does NOT appear in the Logbook (the series is still open); the timeline index
        // surfaces it instead.
        Assert.Empty(await store.GetLogbookAsync());
        Assert.Equal(1, await store.GetOccurrenceCountAsync(task.Id));
        var indexed = Assert.Single(await store.GetOccurrencesAsync(task.Id));
        Assert.Equal(occurrence.Id, indexed.Id);
        Assert.Equal(Today, indexed.OccurrenceDate);
        Assert.Equal(OccurrenceStatus.Completed, indexed.Status);
    }

    [Fact]
    public async Task CompletingRecurring_FreezesChecklistSnapshot_AndResetsOriginalForNextCycle()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "매일 점검",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            Checklist =
            {
                new ChecklistItem { Title = "물 챙기기", IsChecked = true },
                new ChecklistItem { Title = "스트레칭", IsChecked = false },
            },
        };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        // The advanced original keeps its checklist items but resets them all to unchecked for next cycle.
        var original = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(2, original!.Checklist.Count);
        Assert.All(original.Checklist, item => Assert.False(item.IsChecked));

        // The occurrence freezes the checklist exactly as it was ticked at completion.
        var occurrence = Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>());
        Assert.Equal(2, occurrence.ChecklistSnapshot.Count);
        Assert.True(occurrence.ChecklistSnapshot[0].IsChecked);
        Assert.False(occurrence.ChecklistSnapshot[1].IsChecked);
    }

    [Fact]
    public async Task CompletingRecurring_IsIdempotent_WhenAdvanceCrashedAfterOccurrence()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);

        var task = new TaskItem
        {
            Title = "매일 운동",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            SortOrder = "hhhh",
        };
        await store.SaveAsync(task);

        // First completion crashes right after the occurrence is written (save #1) but before the advance
        // (save #2) — the exact window the two-save path is non-atomic in.
        var crashing = new CrashOnNthSaveStore(store, crashOnSave: 2);
        var crashedService = new RecurringTaskService(crashing);
        await Assert.ThrowsAsync<CrashOnNthSaveStore.SimulatedCrashException>(
            () => crashedService.CompleteAsync(task.Id, Now));

        // After the crash the orphaned occurrence is on disk while the original is still due today.
        Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>());
        var stillOpen = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(stillOpen!.IsCompleted);
        Assert.Equal(Today, WhenDay(stillOpen));

        // Retrying against a healthy store must overwrite the orphaned occurrence (its id is derived from
        // the series id + this cycle), not mint a second one, and advance once.
        var service = new RecurringTaskService(store);
        await service.CompleteAsync(task.Id, Now);

        Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>()); // still exactly one — no duplicate cycle
        Assert.Equal(1, await store.GetOccurrenceCountAsync(task.Id));
        var advanced = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(advanced!.IsCompleted);
        Assert.Equal(Today.AddDays(1), WhenDay(advanced));
    }

    [Fact]
    public async Task AdvancedOriginal_LeavesToday_AndSurfacesInUpcoming()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "daily", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);

        await service.CompleteAsync(task.Id, Now);

        // After advancing to tomorrow it drops out of Today and shows up in Upcoming — same row, next cycle.
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.Contains(await store.GetUpcomingAsync(), t => t.Id == task.Id);
    }

    [Fact]
    public async Task Advance_PreservesTimeZoneAndWallClock()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "서울 일정",
            When = OnDay(Today, tz: "Asia/Seoul"),
            Recurrence = Daily(Today, tz: "Asia/Seoul"),
        };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        var advanced = (await store.GetAsync<TaskItem>(task.Id))!.When.Date!.Value;
        Assert.Equal("Asia/Seoul", advanced.TimeZoneId);
        Assert.Equal(9, advanced.ToLocal().Hour); // 09:00 local preserved
    }

    [Fact]
    public async Task Advance_PreservesAllDayState()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        // A 종일 (all-day) recurring task: the advance must keep the next cycle all-day too, and the frozen
        // occurrence is likewise all-day.
        var task = new TaskItem { Title = "매일 종일", When = AllDayOn(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        var original = await store.GetAsync<TaskItem>(task.Id);
        Assert.True(original!.When.IsAllDay); // next cycle stays 종일
        Assert.Equal(Today.AddDays(1), WhenDay(original));

        var occurrence = Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>());
        Assert.True(occurrence.When.IsAllDay);
        Assert.True((await store.GetOccurrencesAsync(task.Id))[0].IsAllDay);
    }

    [Fact]
    public async Task Advance_KeepsTimedTaskTimed()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 09시", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        Assert.False((await store.GetAsync<TaskItem>(task.Id))!.When.IsAllDay);
    }

    [Fact]
    public async Task CompletingNonRecurring_StampsInPlace_WithNoOccurrence()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "한 번만", When = OnDay(Today) };
        await store.SaveAsync(task);

        var next = await service.CompleteAsync(task.Id, Now);
        Assert.Null(next); // a terminal completion reports no next occurrence

        Assert.Single(await store.GetAllAsync<TaskItem>());
        Assert.Empty(await store.GetAllAsync<RecurrenceOccurrence>());
        var logbook = await store.GetLogbookAsync();
        Assert.Single(logbook);
        Assert.Equal(task.Id, logbook[0].Id);
    }

    [Fact]
    public async Task CompletingExhaustedRule_CompletesInPlace_WithNoOccurrence()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        // A COUNT=1 series has exactly one cycle (today). Completing it exhausts the rule: it completes in
        // place (no further cycle to advance to) and the single cycle is the terminal Logbook record — not a
        // separate occurrence.
        var task = new TaskItem
        {
            Title = "한 회차짜리",
            When = OnDay(Today),
            Recurrence = new RecurrenceRule("FREQ=DAILY;COUNT=1", OnDayZoned(Today)),
        };
        await store.SaveAsync(task);

        var next = await service.CompleteAsync(task.Id, Now);
        Assert.Null(next);
        Assert.Empty(await store.GetAllAsync<RecurrenceOccurrence>());
        var completed = await store.GetAsync<TaskItem>(task.Id);
        Assert.True(completed!.IsCompleted);
        Assert.Equal(Today, WhenDay(completed)); // When unchanged
        Assert.Contains(await store.GetLogbookAsync(), t => t.Id == task.Id);
    }

    [Fact]
    public async Task CompletingUnparseableRule_FallsBackToInPlaceCompletion()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        // A malformed RRULE must not throw and must not advance — it completes like a one-off with no cycle.
        var task = new TaskItem
        {
            Title = "깨진 규칙",
            When = OnDay(Today),
            Recurrence = new RecurrenceRule("totally not a rule", OnDayZoned(Today)),
        };
        await store.SaveAsync(task);

        var next = await service.CompleteAsync(task.Id, Now);
        Assert.Null(next);

        Assert.Single(await store.GetAllAsync<TaskItem>());
        Assert.Empty(await store.GetAllAsync<RecurrenceOccurrence>());
        var only = (await store.GetAllAsync<TaskItem>())[0];
        Assert.True(only.IsCompleted);
        Assert.Equal(Today, WhenDay(only)); // When unchanged
    }

    [Fact]
    public async Task Skipping_RecordsSkippedOccurrence_AndAdvances_WithNoSnapshot()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "매일 운동",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            Checklist = { new ChecklistItem { Title = "스트레칭", IsChecked = true } },
        };
        await store.SaveAsync(task);

        var next = await service.SkipAsync(task.Id, Now);
        Assert.Equal(Today.AddDays(1), next);

        var original = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(original!.IsCompleted);
        Assert.Equal(Today.AddDays(1), WhenDay(original));

        var occurrence = Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>());
        Assert.Equal(OccurrenceStatus.Skipped, occurrence.Status);
        Assert.Null(occurrence.CompletedAt);
        Assert.Empty(occurrence.ChecklistSnapshot); // a skipped cycle keeps no snapshot
    }

    [Fact]
    public async Task SkippingNonRecurring_IsNoOp()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "한 번만", When = OnDay(Today) };
        await store.SaveAsync(task);

        var next = await service.SkipAsync(task.Id, Now);
        Assert.Null(next);
        Assert.False((await store.GetAsync<TaskItem>(task.Id))!.IsCompleted); // not touched
        Assert.Empty(await store.GetAllAsync<RecurrenceOccurrence>());
    }

    [Fact]
    public async Task EndSeries_CompletesOriginal_KeepsRecurrenceAndHistory()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now); // one recorded cycle, advanced to tomorrow
        await service.EndSeriesAsync(task.Id, Now);

        // Ending the series is the only path that completes a recurring task: it lands in the Logbook with
        // its recurrence rule preserved as historical context, and its recorded cycle history is untouched.
        var ended = await store.GetAsync<TaskItem>(task.Id);
        Assert.True(ended!.IsCompleted);
        Assert.NotNull(ended.Recurrence);
        Assert.Contains(await store.GetLogbookAsync(), t => t.Id == task.Id);
        Assert.Equal(1, await store.GetOccurrenceCountAsync(task.Id));
    }

    [Fact]
    public async Task EndSeries_OnNonRecurring_IsNoOp()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "한 번만", When = OnDay(Today) };
        await store.SaveAsync(task);

        await service.EndSeriesAsync(task.Id, Now);
        Assert.False((await store.GetAsync<TaskItem>(task.Id))!.IsCompleted); // untouched
    }

    [Fact]
    public async Task UpdateOccurrenceStatus_ChangesOnlyTheRecord_NotTheSchedule()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);
        await service.CompleteAsync(task.Id, Now);

        var occurrence = Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>());
        var scheduleBefore = WhenDay((await store.GetAsync<TaskItem>(task.Id))!);

        await service.UpdateOccurrenceStatusAsync(occurrence.Id, OccurrenceStatus.Skipped);

        var edited = await store.GetAsync<RecurrenceOccurrence>(occurrence.Id);
        Assert.Equal(OccurrenceStatus.Skipped, edited!.Status);
        Assert.Null(edited.CompletedAt); // cleared away from Completed

        // Editing history must not move the series' next scheduled cycle.
        Assert.Equal(scheduleBefore, WhenDay((await store.GetAsync<TaskItem>(task.Id))!));
    }

    [Fact]
    public async Task Occurrences_AreReturnedNewestFirst_AndPage()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        // Three completions record cycles for Today, +1, +2 (each advances the series one day).
        await service.CompleteAsync(task.Id, Now);
        await service.CompleteAsync(task.Id, Now);
        await service.CompleteAsync(task.Id, Now);

        Assert.Equal(3, await store.GetOccurrenceCountAsync(task.Id));

        // Newest cycle first.
        var all = await store.GetOccurrencesAsync(task.Id);
        Assert.Equal(new[] { Today.AddDays(2), Today.AddDays(1), Today }, all.Select(o => o.OccurrenceDate));

        // The window pages: the first page is the two newest, the offset page the oldest.
        var page = await store.GetOccurrencesAsync(task.Id, limit: 2);
        Assert.Equal(new[] { Today.AddDays(2), Today.AddDays(1) }, page.Select(o => o.OccurrenceDate));
        var older = await store.GetOccurrencesAsync(task.Id, limit: 2, offset: 2);
        Assert.Equal(new[] { Today }, older.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public async Task Occurrences_SurviveIndexRebuild()
    {
        var root = NewRoot();
        Guid taskId;
        await using (var store = await OpenAsync(root))
        {
            var service = new RecurringTaskService(store);
            var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
            await store.SaveAsync(task);
            taskId = task.Id;
            await service.CompleteAsync(task.Id, Now);
        }

        // Reopening rebuilds the index from the files; the recorded cycle must be re-indexed from its file.
        await using var reopened = await OpenAsync(root);
        Assert.Equal(1, await reopened.GetOccurrenceCountAsync(taskId));
        Assert.Single(await reopened.GetOccurrencesAsync(taskId));
    }

    [Fact]
    public async Task GetUpcoming_ProjectsNextCyclesAfterTheCurrentOne_OnTheRuleGrid()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        var upcoming = await service.GetUpcomingOccurrencesAsync(task.Id, 3);

        // Strictly after the current cycle (today), three days on the daily grid — never today itself.
        Assert.Equal(new[] { Today.AddDays(1), Today.AddDays(2), Today.AddDays(3) }, upcoming);
    }

    [Fact]
    public async Task GetUpcoming_StopsWhenTheRuleIsExhausted()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        // Only two more cycles exist after the current one (COUNT=3 total from the anchor).
        var task = new TaskItem
        {
            Title = "세 번만",
            When = OnDay(Today),
            Recurrence = new RecurrenceRule("FREQ=DAILY;COUNT=3", OnDayZoned(Today)),
        };
        await store.SaveAsync(task);

        var upcoming = await service.GetUpcomingOccurrencesAsync(task.Id, 10);

        // Asked for 10 but the series only has two cycles left — projection stops, never invents dates.
        Assert.Equal(new[] { Today.AddDays(1), Today.AddDays(2) }, upcoming);
    }

    [Fact]
    public async Task UndoCompletion_RollsTheLatestCompletionBackToTheCurrentCycle()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "매일 운동",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            Checklist = { new ChecklistItem { Title = "스트레칭" } },
        };
        await store.SaveAsync(task);

        // Tick the checklist, complete the cycle (advances to tomorrow, freezes the ticked checklist).
        var current = await store.GetAsync<TaskItem>(task.Id);
        current!.Checklist[0].IsChecked = true;
        await store.SaveAsync(current);
        await service.CompleteAsync(task.Id, Now);
        var occurrence = Assert.Single(await store.GetAllAsync<RecurrenceOccurrence>());

        var undone = await service.UndoCompletionAsync(task.Id, occurrence.Id, Now);

        Assert.True(undone);
        var rolledBack = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(rolledBack!.IsCompleted);
        Assert.Equal(Today, WhenDay(rolledBack)); // current cycle is the rolled-back day again
        Assert.True(rolledBack.Checklist[0].IsChecked); // the frozen checklist state is restored
        // The record is gone (tombstoned) — the cycle is the live current one again, not history.
        Assert.Empty(await store.GetOccurrencesAsync(task.Id));
    }

    [Fact]
    public async Task UndoCompletion_DeclinesForANonLatestCompletion()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        // Two completions: the first cycle is no longer the immediate predecessor of the current one.
        await service.CompleteAsync(task.Id, Now);                 // records Today, advances to +1
        await service.CompleteAsync(task.Id, Now.AddDays(1));      // records +1, advances to +2
        var records = await store.GetOccurrencesAsync(task.Id);    // most-recent-first
        var older = records[^1];                                   // the Today record (not the latest)

        var undone = await service.UndoCompletionAsync(task.Id, older.Id, Now);

        // The guard declines: the series is not rolled back and no record is removed.
        Assert.False(undone);
        Assert.Equal(Today.AddDays(2), WhenDay((await store.GetAsync<TaskItem>(task.Id))!));
        Assert.Equal(2, await store.GetOccurrenceCountAsync(task.Id));
    }

    [Fact]
    public async Task CompletingTheSameCycleTwice_OverwritesRatherThanDuplicating()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "매일 운동", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        // Complete today's cycle, then roll it back so today is the current cycle again, then complete it
        // once more. The occurrence id is derived from (series, cycle instant), so the second completion
        // reproduces the same id and overwrites — a date never gets two completed records.
        await service.CompleteAsync(task.Id, Now);
        var first = Assert.Single(await store.GetOccurrencesAsync(task.Id));
        await service.UndoCompletionAsync(task.Id, first.Id, Now);
        await service.CompleteAsync(task.Id, Now);

        var occurrence = Assert.Single(await store.GetOccurrencesAsync(task.Id));
        Assert.Equal(Today, occurrence.OccurrenceDate);
        Assert.Equal(OccurrenceStatus.Completed, occurrence.Status);
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// An <see cref="ITaskStore"/> decorator that forwards every call to the real store but throws on the
    /// Nth <see cref="SaveAsync"/>, simulating a crash partway through a multi-save operation. Writes
    /// before the throw really land on disk, so the post-crash state is faithful.
    /// </summary>
    private sealed class CrashOnNthSaveStore : ITaskStore
    {
        public sealed class SimulatedCrashException : Exception;

        private readonly ITaskStore _inner;
        private readonly int _crashOnSave;
        private int _saves;

        public CrashOnNthSaveStore(ITaskStore inner, int crashOnSave)
        {
            _inner = inner;
            _crashOnSave = crashOnSave;
        }

        public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
            where T : RecordBase => _inner.GetAllAsync<T>(cancellationToken);

        public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : RecordBase => _inner.GetAsync<T>(id, cancellationToken);

        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default)
            where T : RecordBase
        {
            if (++_saves == _crashOnSave)
                throw new SimulatedCrashException();
            return _inner.SaveAsync(record, cancellationToken);
        }

        public Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : RecordBase => _inner.DeleteAsync<T>(id, cancellationToken);
    }
}
