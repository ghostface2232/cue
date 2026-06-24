using Cue.Domain;

namespace Cue.Storage.Ranking;

/// <inheritdoc cref="IReorderService"/>
public sealed class ReorderService : IReorderService
{
    /// <summary>
    /// When the moved record's freshly computed rank grows past this many characters — or the list
    /// is found to be unranked / inconsistently ranked — the whole list is rebalanced. Normal inserts
    /// produce keys of a handful of characters, so this is reached only after a pathological run of
    /// repeated same-gap insertions.
    /// </summary>
    private const int RebalanceThreshold = 64;

    private readonly ITaskStore _store;

    public ReorderService(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public string AppendRank(IEnumerable<string?> existingRanks)
    {
        ArgumentNullException.ThrowIfNull(existingRanks);
        // The list is rank-sorted, but be order-independent: append after the largest existing rank.
        string? max = null;
        foreach (var rank in existingRanks)
        {
            if (string.IsNullOrEmpty(rank)) continue;
            if (max is null || string.CompareOrdinal(rank, max) > 0) max = rank;
        }
        return FractionalRank.Between(max, null);
    }

    public string PrependRank(IEnumerable<string?> existingRanks)
    {
        ArgumentNullException.ThrowIfNull(existingRanks);
        // Order-independent: prepend before the smallest existing rank.
        string? min = null;
        foreach (var rank in existingRanks)
        {
            if (string.IsNullOrEmpty(rank)) continue;
            if (min is null || string.CompareOrdinal(rank, min) < 0) min = rank;
        }
        return FractionalRank.Between(null, min);
    }

    public async Task<ReorderResult> MoveAsync<T>(
        Guid movedId,
        IReadOnlyList<RankedItem> orderedItems,
        CancellationToken cancellationToken = default) where T : RecordBase, ISortable
    {
        ArgumentNullException.ThrowIfNull(orderedItems);

        var index = -1;
        for (var i = 0; i < orderedItems.Count; i++)
        {
            if (orderedItems[i].Id == movedId) { index = i; break; }
        }
        if (index < 0)
            throw new ArgumentException("The moved id is not present in the ordered list.", nameof(movedId));

        var before = index > 0 ? orderedItems[index - 1].Rank : null;
        var after = index < orderedItems.Count - 1 ? orderedItems[index + 1].Rank : null;

        // A neighbor (not a list boundary) with an empty rank means the list was never ranked or is
        // inconsistent; out-of-order neighbors mean the same. Either way, re-rank the whole list.
        var needsRebalance =
            (index > 0 && string.IsNullOrEmpty(before)) ||
            (index < orderedItems.Count - 1 && string.IsNullOrEmpty(after)) ||
            (before is not null && after is not null && string.CompareOrdinal(before, after) >= 0);

        if (!needsRebalance)
        {
            string candidate;
            try
            {
                candidate = FractionalRank.Between(before, after);
            }
            catch (ArgumentException)
            {
                // The ranks looked usable but the generator disagreed — fall back to a rebalance.
                return await RebalanceAsync<T>(orderedItems, cancellationToken).ConfigureAwait(false);
            }

            if (candidate.Length <= RebalanceThreshold)
            {
                var saved = await AssignAsync<T>(movedId, candidate, cancellationToken).ConfigureAwait(false);
                return saved
                    ? new ReorderResult(false, new Dictionary<Guid, string> { [movedId] = candidate })
                    : ReorderResult.None;
            }
            // The key got too long: this gap is exhausted — rebalance instead of saving a bloated rank.
        }

        return await RebalanceAsync<T>(orderedItems, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-ranks the whole list evenly and saves only the records whose rank actually changed.</summary>
    private async Task<ReorderResult> RebalanceAsync<T>(
        IReadOnlyList<RankedItem> orderedItems,
        CancellationToken cancellationToken) where T : RecordBase, ISortable
    {
        var fresh = FractionalRank.EvenlyBetween(null, null, orderedItems.Count);
        var changed = new Dictionary<Guid, string>();
        for (var i = 0; i < orderedItems.Count; i++)
        {
            var item = orderedItems[i];
            var rank = fresh[i];
            if (string.Equals(item.Rank, rank, StringComparison.Ordinal)) continue;
            if (await AssignAsync<T>(item.Id, rank, cancellationToken).ConfigureAwait(false))
                changed[item.Id] = rank;
        }
        return new ReorderResult(true, changed);
    }

    /// <summary>Loads a live record, stamps its rank, and saves it through the store. Returns false if it is gone.</summary>
    private async Task<bool> AssignAsync<T>(Guid id, string rank, CancellationToken cancellationToken)
        where T : RecordBase, ISortable
    {
        var record = await _store.GetAsync<T>(id, cancellationToken).ConfigureAwait(false);
        if (record is null || record.IsDeleted) return false;
        if (string.Equals(record.SortOrder, rank, StringComparison.Ordinal)) return true;
        record.SortOrder = rank;
        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
