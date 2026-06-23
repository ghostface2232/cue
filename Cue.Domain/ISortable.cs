namespace Cue.Domain;

/// <summary>
/// Marks a record that carries a manual ordering rank. Implemented by every manually-ordered
/// entity (<see cref="TaskItem"/>, <see cref="TaskGroup"/>, <see cref="Tag"/>).
/// </summary>
/// <remarks>
/// This is purely a property contract — it adds no behavior and keeps the domain a pure data holder.
/// It exists so the rank service / store can read and assign <see cref="SortOrder"/> generically
/// without reflection or a per-type switch. The rank string itself is a LexoRank-style fractional
/// key; the domain only holds it and never generates it.
/// </remarks>
public interface ISortable
{
    /// <summary>The LexoRank-style fractional ordering rank. Assigned by the rank service / store.</summary>
    string SortOrder { get; set; }
}
