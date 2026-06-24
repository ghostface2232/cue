using Cue.Domain;

namespace Cue.Storage;

/// <summary>
/// The single write path for every record in the app. All data mutations go through this
/// interface, which is what makes a later sync layer additive.
/// </summary>
/// <remarks>
/// Methods are generic over the concrete record type (<see cref="TaskItem"/>, <see cref="TaskGroup"/>,
/// <see cref="Tag"/>): the type selects the storage partition. The foundation-phase implementation
/// is file-based (<see cref="FileTaskStore"/>).
/// </remarks>
public interface ITaskStore
{
    /// <summary>
    /// Returns every stored record of type <typeparamref name="T"/>, <b>including tombstones</b>
    /// (records with a non-null <see cref="RecordBase.DeletedAt"/>). Filtering deleted items out is
    /// the job of the query/index layer, not the store — sync and index rebuild need to see them.
    /// </summary>
    Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) where T : RecordBase;

    /// <summary>Returns the record with the given id, or <c>null</c> if no file exists for it.</summary>
    Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase;

    /// <summary>
    /// Persists <paramref name="record"/>. The store stamps <see cref="RecordBase.CreatedAt"/> once
    /// (if unset) and <see cref="RecordBase.UpdatedAt"/> on every save.
    /// </summary>
    Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase;

    /// <summary>
    /// Soft-deletes the record: stamps <see cref="RecordBase.DeletedAt"/> (and bumps
    /// <see cref="RecordBase.UpdatedAt"/>) and re-saves it. The file is never removed. A no-op if
    /// no record with that id exists.
    /// </summary>
    Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase;

    /// <summary>
    /// Atomically reads the record with <paramref name="id"/>, applies <paramref name="mutate"/> to it,
    /// and persists the result — the whole read-modify-write held under the same lock that serializes
    /// <see cref="SaveAsync"/>. This is the only safe way to update a record several callers touch
    /// concurrently: each one reads the latest persisted state and writes back only the fields it
    /// changed, so an interleaved save can never clobber another's update with a stale copy.
    /// <para>
    /// <paramref name="mutate"/> runs on the just-read record and returns <c>true</c> to commit the
    /// save or <c>false</c> to abandon it (no change to persist). Returns the persisted record, or
    /// <c>null</c> when none exists for <paramref name="id"/>, it is tombstoned, or the mutation
    /// declined to save.
    /// </para>
    /// <para>
    /// The default implementation is a plain (non-atomic) read-modify-write for simple stores; the
    /// indexed/file stores override it to hold their write lock across the whole sequence.
    /// </para>
    /// </summary>
    async Task<T?> MutateAsync<T>(Guid id, Func<T, bool> mutate, CancellationToken cancellationToken = default)
        where T : RecordBase
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var record = await GetAsync<T>(id, cancellationToken).ConfigureAwait(false);
        if (record is null || record.IsDeleted) return null;
        if (!mutate(record)) return null;
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one atomic unit under the store's write lock: every read and
    /// save it performs through the supplied <see cref="ITaskMutationScope"/> is held under the same
    /// lock that serializes <see cref="SaveAsync"/> and <see cref="MutateAsync"/>, so the whole
    /// sequence commits together and cannot interleave with another mutation. Use this for the rare
    /// multi-record operation that must be consistent across writes — e.g. recurrence completion, which
    /// writes a Logbook copy and then advances the original. A single read-modify-write should use the
    /// simpler <see cref="MutateAsync"/>.
    /// <para>
    /// The default implementation forwards the scope straight to this store (correct, but with no
    /// isolation) for simple stores; the indexed store overrides it to hold its mutation lock across
    /// the whole unit.
    /// </para>
    /// </summary>
    Task RunInTransactionAsync(Func<ITaskMutationScope, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(new ForwardingMutationScope(this), cancellationToken);
    }
}

/// <summary>
/// The read/save surface available inside <see cref="ITaskStore.RunInTransactionAsync"/>. A sequence
/// of calls on one scope is performed under the transaction's lock, so they commit as a single atomic
/// unit. Mirrors the read-by-id and save halves of <see cref="ITaskStore"/>.
/// </summary>
public interface ITaskMutationScope
{
    /// <inheritdoc cref="ITaskStore.GetAsync{T}"/>
    Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase;

    /// <inheritdoc cref="ITaskStore.SaveAsync{T}"/>
    Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase;
}

/// <summary>A scope that forwards straight to an <see cref="ITaskStore"/> — the default
/// (non-isolating) transaction surface for stores that don't override
/// <see cref="ITaskStore.RunInTransactionAsync"/>.</summary>
internal sealed class ForwardingMutationScope(ITaskStore store) : ITaskMutationScope
{
    public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
        => store.GetAsync<T>(id, cancellationToken);

    public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
        => store.SaveAsync(record, cancellationToken);
}
