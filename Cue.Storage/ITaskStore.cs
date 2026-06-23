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
}
