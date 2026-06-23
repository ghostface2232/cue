using Cue.Domain;
using Cue.Storage;

namespace Cue.Tests;

public sealed class FileTaskStoreTests : IDisposable
{
    private const string Seoul = "Asia/Seoul";
    private readonly List<string> _roots = new();

    private FileTaskStore NewStore(TimeProvider? clock = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cue-store-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new FileTaskStore(new FileTaskStoreOptions { RootPath = root }, clock);
    }

    private static ZonedDateTime Zoned(int y, int mo, int d, int h, int mi)
        => ZonedDateTime.FromLocal(new DateTime(y, mo, d, h, mi, 0), Seoul);

    private static TaskItem FullTask() => new()
    {
        Title = "분기 보고서 마무리",
        Notes = "**굵게** 그리고 _기울임_ 마크다운",
        CompletedAt = new DateTimeOffset(2026, 6, 20, 1, 2, 3, TimeSpan.Zero),
        When = ScheduledWhen.On(Zoned(2026, 6, 25, 0, 0)),
        Priority = Priority.P1,
        TaskGroupId = Guid.NewGuid(),
        Checklist =
        {
            new ChecklistItem { Title = "초안 작성", IsChecked = true, Note = "**굵게** 메모" },
            new ChecklistItem { Title = "검토 요청" },
        },
        TagIds = { Guid.NewGuid(), Guid.NewGuid() },
        Recurrence = new RecurrenceRule("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", Zoned(2026, 6, 22, 9, 0)),
        SortOrder = "0|hzzzzz:",
    };

    [Fact]
    public async Task Task_RoundTrips_AllFields()
    {
        var store = NewStore();
        var task = FullTask();

        await store.SaveAsync(task);
        var loaded = await store.GetAsync<TaskItem>(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.Id);
        Assert.Equal(task.Title, loaded.Title);
        Assert.Equal(task.Notes, loaded.Notes);
        Assert.Equal(task.CompletedAt, loaded.CompletedAt);
        Assert.True(loaded.IsCompleted);
        Assert.Equal(task.When, loaded.When);
        Assert.Equal(task.Priority, loaded.Priority);
        Assert.Equal(task.TaskGroupId, loaded.TaskGroupId);
        Assert.Equal(2, loaded.Checklist.Count);
        Assert.Equal(task.Checklist[0].Id, loaded.Checklist[0].Id);
        Assert.Equal("초안 작성", loaded.Checklist[0].Title);
        Assert.True(loaded.Checklist[0].IsChecked);
        Assert.Equal("**굵게** 메모", loaded.Checklist[0].Note);
        Assert.Equal("검토 요청", loaded.Checklist[1].Title);
        Assert.False(loaded.Checklist[1].IsChecked);
        Assert.Null(loaded.Checklist[1].Note);
        Assert.Equal(task.TagIds, loaded.TagIds);
        Assert.NotNull(loaded.Recurrence);
        Assert.Equal(task.Recurrence!.Rule, loaded.Recurrence!.Rule);
        Assert.Equal(task.Recurrence.Anchor, loaded.Recurrence.Anchor);
        Assert.Equal(task.SortOrder, loaded.SortOrder);
        // Store stamped the audit timestamps.
        Assert.NotEqual(default, loaded.CreatedAt);
        Assert.NotEqual(default, loaded.UpdatedAt);
    }

    [Fact]
    public async Task ZonedDate_PreservesInstantAndZone()
    {
        var store = NewStore();
        var task = new TaskItem { When = ScheduledWhen.On(Zoned(2026, 7, 1, 18, 0)) };

        await store.SaveAsync(task);
        var loaded = await store.GetAsync<TaskItem>(task.Id);

        Assert.Equal(task.When.Date!.Value.Utc, loaded!.When.Date!.Value.Utc);
        Assert.Equal(Seoul, loaded.When.Date.Value.TimeZoneId);
        // The zone id survives, so display conversion still works after a round-trip.
        Assert.Equal(task.When.Date.Value.ToLocal(), loaded.When.Date.Value.ToLocal());
    }

    [Theory]
    [InlineData("unscheduled")]
    [InlineData("ondate")]
    public async Task ScheduledWhen_EachVariant_RoundTrips(string variant)
    {
        var store = NewStore();
        var when = variant switch
        {
            "unscheduled" => ScheduledWhen.Unscheduled,
            "ondate" => ScheduledWhen.On(Zoned(2026, 6, 25, 0, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var task = new TaskItem { When = when };

        await store.SaveAsync(task);
        var loaded = await store.GetAsync<TaskItem>(task.Id);

        Assert.Equal(when, loaded!.When);
        Assert.Equal(when.Kind, loaded.When.Kind);
        Assert.Equal(when.Date, loaded.When.Date);
    }

    [Fact]
    public async Task TaskGroup_RoundTrips_AllFields()
    {
        var store = NewStore();
        var taskGroup = new TaskGroup
        {
            Name = "런치 준비",
            Notes = "보드 뷰로 본다",
            Color = "#4F8CC9",
            View = TaskGroupView.Board,
            SortOrder = "0|i00000:",
        };

        await store.SaveAsync(taskGroup);
        var loaded = await store.GetAsync<TaskGroup>(taskGroup.Id);

        Assert.NotNull(loaded);
        Assert.Equal(taskGroup.Name, loaded!.Name);
        Assert.Equal(taskGroup.Notes, loaded.Notes);
        Assert.Equal(taskGroup.Color, loaded.Color);
        Assert.Equal(TaskGroupView.Board, loaded.View);
        Assert.Equal(taskGroup.SortOrder, loaded.SortOrder);
    }

    [Fact]
    public async Task Tag_RoundTrips()
    {
        var store = NewStore();
        var tag = new Tag { Name = "긴급", Color = "#D33", SortOrder = "0|b:" };

        await store.SaveAsync(tag);

        var loadedTag = await store.GetAsync<Tag>(tag.Id);

        Assert.Equal(tag.Name, loadedTag!.Name);
        Assert.Equal(tag.Color, loadedTag.Color);
        Assert.Equal(tag.SortOrder, loadedTag.SortOrder);
    }

    [Fact]
    public async Task Priority_IsStoredAsString_NotNumber()
    {
        var store = NewStore();
        var task = new TaskItem { Priority = Priority.P1 };

        await store.SaveAsync(task);
        var json = await File.ReadAllTextAsync(Path.Combine(store.RootPath, "tasks", task.Id + ".json"));

        Assert.Contains("\"priority\": \"P1\"", json);
        Assert.DoesNotContain("\"priority\": 1", json);
    }

    [Fact]
    public async Task ComputedProperties_AreNotWrittenToFile()
    {
        var store = NewStore();
        var task = new TaskItem { CompletedAt = DateTimeOffset.UtcNow, When = ScheduledWhen.On(Zoned(2026, 6, 25, 0, 0)) };
        var taskGroup = new TaskGroup { Color = "#4F8CC9" };

        await store.SaveAsync(task);
        await store.SaveAsync(taskGroup);

        var taskJson = await File.ReadAllTextAsync(Path.Combine(store.RootPath, "tasks", task.Id + ".json"));
        var taskGroupJson = await File.ReadAllTextAsync(Path.Combine(store.RootPath, "groups", taskGroup.Id + ".json"));

        // Derived get-only properties must not leak into the file.
        Assert.DoesNotContain("isCompleted", taskJson);
        Assert.DoesNotContain("isDeleted", taskJson);
        Assert.DoesNotContain("hasDate", taskJson);
        // Real fields with setters stay.
        Assert.Contains("color", taskGroupJson);
    }

    [Fact]
    public async Task Save_SetsCreatedAtOnce_AndAlwaysBumpsUpdatedAt()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        var task = new TaskItem { Title = "first" };

        await store.SaveAsync(task);
        var createdAt = task.CreatedAt;
        var firstUpdate = task.UpdatedAt;
        Assert.Equal(clock.Now, createdAt);
        Assert.Equal(clock.Now, firstUpdate);

        clock.Now = clock.Now.AddMinutes(5);
        task.Title = "edited";
        await store.SaveAsync(task);

        Assert.Equal(createdAt, task.CreatedAt);          // set once, unchanged
        Assert.Equal(clock.Now, task.UpdatedAt);          // always bumped
        Assert.True(task.UpdatedAt > firstUpdate);
    }

    [Fact]
    public async Task SoftDelete_KeepsTombstoneVisibleInGetAll()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        var task = new TaskItem { Title = "삭제 대상" };
        await store.SaveAsync(task);

        clock.Now = clock.Now.AddMinutes(1);
        await store.DeleteAsync<TaskItem>(task.Id);

        var path = Path.Combine(store.RootPath, "tasks", task.Id + ".json");
        Assert.True(File.Exists(path), "soft delete must not remove the file");

        var all = await store.GetAllAsync<TaskItem>();
        var tombstone = Assert.Single(all);
        Assert.Equal(task.Id, tombstone.Id);
        Assert.NotNull(tombstone.DeletedAt);
        Assert.True(tombstone.IsDeleted);
        Assert.Equal(clock.Now, tombstone.DeletedAt);
        Assert.Equal(clock.Now, tombstone.UpdatedAt);

        // Still individually fetchable (the store never hides tombstones).
        Assert.NotNull(await store.GetAsync<TaskItem>(task.Id));
    }

    [Fact]
    public async Task GetAll_OnEmptyStore_ReturnsEmpty()
    {
        var store = NewStore();
        Assert.Empty(await store.GetAllAsync<TaskItem>());
    }

    [Fact]
    public async Task Delete_MissingRecord_IsNoOp()
    {
        var store = NewStore();
        await store.DeleteAsync<TaskItem>(Guid.NewGuid()); // must not throw
        Assert.Empty(await store.GetAllAsync<TaskItem>());
    }

    [Fact]
    public async Task GetAll_SkipsUnreadableFile_AndReturnsTheRest()
    {
        var store = NewStore();
        var good = new TaskItem { Title = "정상 레코드" };
        await store.SaveAsync(good);

        // Drop a corrupt file into the partition — e.g. a half-written or partially-synced file.
        var corrupt = Path.Combine(store.RootPath, "tasks", Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(corrupt, "{ this is not valid json");

        // One bad file must not throw the whole listing out; the valid record still comes back.
        var all = await store.GetAllAsync<TaskItem>();
        var only = Assert.Single(all);
        Assert.Equal(good.Id, only.Id);
    }

    [Fact]
    public async Task CompletedAt_IsNormalizedToUtcInstant()
    {
        var store = NewStore();
        // Caller supplies a non-UTC offset (+09:00). The same instant must be kept, but the offset
        // flattened to zero so the system timestamp is a true UTC instant (invariant 7).
        var withOffset = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.FromHours(9));
        var task = new TaskItem { CompletedAt = withOffset };

        // Normalized on assignment, before persistence is even involved.
        Assert.Equal(TimeSpan.Zero, task.CompletedAt!.Value.Offset);

        await store.SaveAsync(task);
        var loaded = await store.GetAsync<TaskItem>(task.Id);

        Assert.Equal(TimeSpan.Zero, loaded!.CompletedAt!.Value.Offset);
        Assert.Equal(withOffset.UtcDateTime, loaded.CompletedAt.Value.UtcDateTime); // instant preserved
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup of temp test folders
            }
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public MutableTimeProvider(DateTimeOffset start) => Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
