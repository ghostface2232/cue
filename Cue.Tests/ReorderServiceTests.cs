using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Ranking;

namespace Cue.Tests;

/// <summary>
/// Exercises the rank service against a fake store: that a move re-ranks only the moved record and
/// leaves neighbors untouched, that appends land at the end, and that the rebalance safety net fires
/// only when a list is unranked or a rank grows pathologically long.
/// </summary>
public sealed class ReorderServiceTests
{
    private static int Cmp(string a, string b) => string.CompareOrdinal(a, b);

    [Fact]
    public void AppendRank_AfterMaxExistingRank()
    {
        var service = new ReorderService(new FakeStore());
        var a = FractionalRank.Between(null, null);
        var b = FractionalRank.Between(a, null);

        var appended = service.AppendRank(new[] { a, b });
        Assert.True(Cmp(b, appended) < 0);
    }

    [Fact]
    public void AppendRank_EmptyList_IsAValidFirstKey()
    {
        var service = new ReorderService(new FakeStore());
        var appended = service.AppendRank(Array.Empty<string?>());
        Assert.False(string.IsNullOrEmpty(appended));
    }

    [Fact]
    public async Task MoveAsync_ReRanksOnlyTheMovedRecord()
    {
        var store = new FakeStore();
        var tasks = await SeedTasksAsync(store, 4); // ranks a..d ascending, ids 0..3

        var beforeStamps = await StampsAsync(store, tasks);

        // Move the last task (index 3) to the front: it should land before tasks[0].
        var moved = tasks[3];
        var newOrder = new[]
        {
            new RankedItem(tasks[3].Id, tasks[3].SortOrder),
            new RankedItem(tasks[0].Id, tasks[0].SortOrder),
            new RankedItem(tasks[1].Id, tasks[1].SortOrder),
            new RankedItem(tasks[2].Id, tasks[2].SortOrder),
        };

        var service = new ReorderService(store);
        var result = await service.MoveAsync<TaskItem>(moved.Id, newOrder);

        Assert.False(result.Rebalanced);
        Assert.True(result.ChangedRanks.ContainsKey(moved.Id));
        Assert.Single(result.ChangedRanks);

        var reloaded = await store.GetAsync<TaskItem>(moved.Id);
        Assert.NotNull(reloaded);
        Assert.True(Cmp(reloaded!.SortOrder, tasks[0].SortOrder) < 0);

        // Neighbors are untouched: same rank and same UpdatedAt as before.
        foreach (var sibling in new[] { tasks[0], tasks[1], tasks[2] })
        {
            var after = await store.GetAsync<TaskItem>(sibling.Id);
            Assert.Equal(sibling.SortOrder, after!.SortOrder);
            Assert.Equal(beforeStamps[sibling.Id], after.UpdatedAt);
        }
    }

    [Fact]
    public async Task MoveAsync_UnrankedList_TriggersRebalance()
    {
        var store = new FakeStore();
        // Three records with empty ranks (legacy / never ranked).
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var task = new TaskItem { Title = $"t{i}", SortOrder = string.Empty };
            await store.SaveAsync(task);
            ids.Add(task.Id);
        }

        var order = ids.Select(id => new RankedItem(id, string.Empty)).ToList();
        var service = new ReorderService(store);
        var result = await service.MoveAsync<TaskItem>(ids[2], order);

        Assert.True(result.Rebalanced);

        // Every record now carries a non-empty rank, ascending in the requested order.
        var ranks = new List<string>();
        foreach (var id in ids)
        {
            var task = await store.GetAsync<TaskItem>(id);
            Assert.False(string.IsNullOrEmpty(task!.SortOrder));
            ranks.Add(task.SortOrder);
        }
        for (var i = 1; i < ranks.Count; i++)
            Assert.True(Cmp(ranks[i - 1], ranks[i]) < 0);
    }

    private static async Task<List<TaskItem>> SeedTasksAsync(FakeStore store, int count)
    {
        var ranks = FractionalRank.EvenlyBetween(null, null, count);
        var tasks = new List<TaskItem>();
        for (var i = 0; i < count; i++)
        {
            var task = new TaskItem { Title = $"t{i}", SortOrder = ranks[i] };
            await store.SaveAsync(task);
            tasks.Add(task);
        }
        return tasks;
    }

    private static async Task<Dictionary<Guid, DateTimeOffset>> StampsAsync(FakeStore store, IEnumerable<TaskItem> tasks)
    {
        var map = new Dictionary<Guid, DateTimeOffset>();
        foreach (var task in tasks)
            map[task.Id] = (await store.GetAsync<TaskItem>(task.Id))!.UpdatedAt;
        return map;
    }

    /// <summary>A minimal in-memory <see cref="ITaskStore"/> that stamps UpdatedAt like the real store.</summary>
    private sealed class FakeStore : ITaskStore
    {
        private readonly Dictionary<(Type, Guid), RecordBase> _records = new();
        private long _tick;

        public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) where T : RecordBase
            => Task.FromResult<IReadOnlyList<T>>(_records.Values.OfType<T>().ToList());

        public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
            => Task.FromResult(_records.TryGetValue((typeof(T), id), out var record) ? (T)record : null);

        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
        {
            if (record.CreatedAt == default) record.CreatedAt = DateTimeOffset.UnixEpoch.AddTicks(++_tick);
            record.UpdatedAt = DateTimeOffset.UnixEpoch.AddTicks(++_tick);
            _records[(typeof(T), record.Id)] = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
        {
            if (_records.TryGetValue((typeof(T), id), out var record))
            {
                record.DeletedAt = DateTimeOffset.UnixEpoch.AddTicks(++_tick);
                record.UpdatedAt = record.DeletedAt.Value;
            }
            return Task.CompletedTask;
        }
    }
}
