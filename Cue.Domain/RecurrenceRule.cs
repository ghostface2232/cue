namespace Cue.Domain;

/// <summary>
/// A recurrence specification: an RFC 5545 RRULE string plus the anchor date the rule is
/// measured from.
/// </summary>
/// <remarks>
/// The model only stores the rule and its anchor. Computing the next occurrence — and the
/// "advance this record's When to the next cycle on completion" behavior — is deliberately left
/// to the storage/logic layer; the domain stays a pure data holder.
/// <para>
/// <see cref="Anchor"/> is a user-specified scheduled date, so it follows the project-wide rule
/// of storing UTC plus the original time zone.
/// </para>
/// </remarks>
public sealed class RecurrenceRule
{
    /// <summary>
    /// The recurrence rule in RFC 5545 RRULE form, e.g. <c>"FREQ=WEEKLY;INTERVAL=2;BYDAY=MO"</c>.
    /// An end can be expressed inside the rule itself (<c>UNTIL=…</c> or <c>COUNT=…</c>).
    /// </summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>The anchor (DTSTART-equivalent) the rule is evaluated from.</summary>
    public ZonedDateTime Anchor { get; set; }
}
