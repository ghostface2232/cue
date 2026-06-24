namespace Cue.Domain;

/// <summary>
/// A single lightweight checklist entry embedded in a <see cref="TaskItem"/>.
/// </summary>
/// <remarks>
/// A checklist item is <i>not</i> a record: it has no own file, no tombstone, no schema version, and
/// is never saved or deleted through <see cref="ITaskStore"/> on its own. It lives inside its owning
/// task's JSON and is persisted as part of that one file, so the parent is the single unit of save and
/// sync. It carries only what a checklist needs — a title and a checked flag — with no memo, date, tag,
/// priority, or recurrence of its own, and it can never nest. Ordering is the position
/// in the parent's <see cref="TaskItem.Checklist"/> list (the whole file is rewritten atomically on
/// every change), so there is no fractional rank.
/// <para>
/// Do not add a read-only computed property here: the store's serializer drops setter-less properties,
/// so such a value would silently vanish from the file.
/// </para>
/// </remarks>
public sealed class ChecklistItem
{
    /// <summary>Stable id, used only to address an item within its parent (e.g. toggle/remove). Not a
    /// record identity — it is never a file name.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The item's text.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Whether the item is ticked. A plain flag — a checklist item has no completion history,
    /// so unlike <see cref="TaskItem.CompletedAt"/> it needs no timestamp.</summary>
    public bool IsChecked { get; set; }
}
