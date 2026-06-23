using Cue.Domain;

namespace Cue.Storage.Ranking;

/// <summary>One entry in an ordered list: a record id paired with its current rank.</summary>
public readonly record struct RankedItem(Guid Id, string? Rank);

/// <summary>The outcome of a reorder: which records' ranks changed, and whether a rebalance ran.</summary>
public sealed record ReorderResult(bool Rebalanced, IReadOnlyDictionary<Guid, string> ChangedRanks)
{
    /// <summary>An empty result for a move that produced no change (e.g. dropped in place).</summary>
    public static readonly ReorderResult None =
        new(false, new Dictionary<Guid, string>());
}

/// <summary>
/// Assigns LexoRank-style ordering ranks and persists reorders through <see cref="ITaskStore"/>.
/// </summary>
/// <remarks>
/// This is the "rank service" the architecture allows to own rank assignment (the domain never
/// does). It is layout-agnostic: callers describe a list purely as an ordered set of
/// <see cref="RankedItem"/>s, and the service computes the single new rank for the moved record and
/// saves <i>only that record</i> — neighbors are never touched. Every write goes through
/// <see cref="ITaskStore.SaveAsync"/>, so the store stamps <see cref="RecordBase.UpdatedAt"/> and
/// reflects the change into the index (invariants 4 and 8).
/// <para>
/// The one exception is the rebalance safety net: if repeated inserts into the same gap push a
/// list's ranks past a length limit (or a list was never ranked / has duplicate ranks), the whole
/// list is re-ranked evenly in one pass. This is rare and confined here — normal moves never
/// trigger it.
/// </para>
/// </remarks>
public interface IReorderService
{
    /// <summary>
    /// Computes a rank that places a new record at the <b>end</b> of a list whose current items carry
    /// <paramref name="existingRanks"/> (in any order — the maximum is used). The caller assigns the
    /// returned rank to the new record before saving it.
    /// </summary>
    string AppendRank(IEnumerable<string?> existingRanks);

    /// <summary>
    /// Persists a reorder. <paramref name="orderedItems"/> is the full list in its new display order
    /// (including the moved record at its target position), each with its current rank. The moved
    /// record is re-ranked to fit strictly between its new neighbors and saved; nothing else is
    /// written unless a rebalance is required.
    /// </summary>
    Task<ReorderResult> MoveAsync<T>(
        Guid movedId,
        IReadOnlyList<RankedItem> orderedItems,
        CancellationToken cancellationToken = default) where T : RecordBase, ISortable;
}
