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
    /// Creates a rule. Both a non-empty RRULE and a real zoned <paramref name="anchor"/> are
    /// required: without an anchor the rule cannot be evaluated and a default
    /// <see cref="ZonedDateTime"/> would have a null <c>TimeZoneId</c> that later blows up in
    /// <see cref="ZonedDateTime.ToLocal"/>.
    /// </summary>
    public RecurrenceRule(string rule, ZonedDateTime anchor)
    {
        if (string.IsNullOrWhiteSpace(rule))
            throw new ArgumentException("A recurrence rule must be a non-empty RRULE string.", nameof(rule));
        if (string.IsNullOrEmpty(anchor.TimeZoneId))
            throw new ArgumentException("A recurrence anchor must be a real zoned date (its TimeZoneId is unset).", nameof(anchor));

        Rule = rule;
        Anchor = anchor;
    }

    /// <summary>
    /// The recurrence rule in RFC 5545 RRULE form, e.g. <c>"FREQ=WEEKLY;INTERVAL=2;BYDAY=MO"</c>.
    /// An end can be expressed inside the rule itself (<c>UNTIL=…</c> or <c>COUNT=…</c>).
    /// </summary>
    public string Rule { get; }

    /// <summary>The anchor (DTSTART-equivalent) the rule is evaluated from.</summary>
    public ZonedDateTime Anchor { get; }
}
