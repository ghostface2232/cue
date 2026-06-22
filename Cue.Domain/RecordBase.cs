namespace Cue.Domain;

/// <summary>
/// Common base for every persisted record in Cue.
/// </summary>
/// <remarks>
/// These are pure domain types: they hold data only and have <b>zero</b> knowledge of
/// how or where they are stored (no file, DB, or serialization concerns live here).
/// <para>
/// Deletion is always a <i>soft delete</i>: a record is never physically removed. Instead
/// <see cref="DeletedAt"/> is set to the time of deletion (a tombstone), and the record is
/// re-saved. A <c>null</c> <see cref="DeletedAt"/> means the record is alive. This is what
/// makes folder-based sync safe later — a deletion is just another last-write-wins update.
/// </para>
/// </remarks>
public abstract class RecordBase
{
    /// <summary>Schema version written by the current build. Bump when the shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Stable identity. The persistence layer uses this as the file name ({Id}.json).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>When the record was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the record was last modified (UTC). The store stamps this on every save.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Soft-delete tombstone (UTC). <c>null</c> means the record is alive; a non-null value
    /// means it was deleted at that time. Records are never hard-deleted.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Schema version this record was written with, for forward migration.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>True when the record has been soft-deleted (tombstoned).</summary>
    public bool IsDeleted => DeletedAt is not null;
}
