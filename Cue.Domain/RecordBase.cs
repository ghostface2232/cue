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
public abstract class RecordBase : IEquatable<RecordBase>
{
    /// <summary>Schema version written by the current build. Bump when the shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Stable identity. The persistence layer uses this as the file name ({Id}.json). Init-only:
    /// it is set once at creation (or on deserialization) and never reassigned, which keeps the
    /// Id-based <see cref="GetHashCode"/> stable while an instance lives in a hash set.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

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

    /// <summary>
    /// Records have <i>identity</i> equality: two instances are equal when they are the exact
    /// same record type with the same <see cref="Id"/>, regardless of their other field values.
    /// This keeps de-duplication and lookups (<c>Distinct</c>, <c>HashSet</c>, <c>Contains</c>,
    /// <c>==</c>) keyed on Id rather than on whole-object value comparison.
    /// <para>
    /// The type check uses exact <see cref="object.GetType"/> equality (not <c>is</c>), so a
    /// record never compares equal to one of a different concrete type that happens to share its
    /// Id. <see cref="GetHashCode"/> hashes only the immutable <see cref="Id"/>, so it stays
    /// stable for the lifetime of the instance.
    /// </para>
    /// <para>
    /// <b>Do not use equality to detect content changes.</b> Because equality is Id-only, two
    /// versions of the <i>same</i> record — e.g. a local copy and one arriving from sync that
    /// differ in <see cref="UpdatedAt"/> or any other field — are still "equal" here. Index
    /// refresh and sync reconciliation must compare <see cref="UpdatedAt"/> (or specific fields),
    /// never <c>old.Equals(new)</c>, which would always report "unchanged" and silently drop the
    /// update.
    /// </para>
    /// </summary>
    public bool Equals(RecordBase? other)
        => other is not null && other.GetType() == GetType() && other.Id == Id;

    /// <inheritdoc cref="Equals(RecordBase?)"/>
    public override bool Equals(object? obj) => Equals(obj as RecordBase);

    /// <inheritdoc cref="Equals(RecordBase?)"/>
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>Identity equality (see <see cref="Equals(RecordBase?)"/>), null-safe.</summary>
    public static bool operator ==(RecordBase? left, RecordBase? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Negation of <see cref="operator ==(RecordBase?, RecordBase?)"/>.</summary>
    public static bool operator !=(RecordBase? left, RecordBase? right) => !(left == right);
}
