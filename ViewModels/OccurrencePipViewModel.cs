using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cue.Domain;

namespace Cue.ViewModels;

/// <summary>How one timeline pip reads — a recorded past cycle's outcome, or the live head of the
/// series (its current/next cycle, or a terminal one once the series has ended).</summary>
public enum OccurrencePipKind
{
    /// <summary>완료 — a recorded cycle that was carried out.</summary>
    Completed,

    /// <summary>건너뜀 — a recorded cycle that was deliberately skipped.</summary>
    Skipped,

    /// <summary>미수행 — a recorded cycle that was neither done nor skipped.</summary>
    Missed,

    /// <summary>다음 — the live current/next cycle (the series' own <see cref="TaskItem.When"/>); not a record.</summary>
    Next,

    /// <summary>종료 — the series has ended (반복 종료 or exhausted); the live head is the terminal record.</summary>
    Ended,
}

/// <summary>
/// One pip in the detail-panel recurrence timeline: a single cycle rendered as a date, a status glyph,
/// and a status label. Past cycles map to a <see cref="RecurrenceOccurrence"/> (and carry its
/// <see cref="OccurrenceId"/> so the per-cycle flyout can load and edit it); the head pip is synthesized
/// from the live series and carries no id.
/// </summary>
/// <remarks>
/// The status is conveyed three ways that never rely on color alone — a distinct glyph, a visible
/// status label under the date, and the <see cref="AutomationName"/>/<see cref="Tooltip"/> — so the
/// timeline stays legible to color-blind and screen-reader users and under reduced-motion.
/// </remarks>
public partial class OccurrencePipViewModel : ObservableObject
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>The occurrence record this pip stands for, or <c>null</c> for the live head pip (which is
    /// the series itself, not a record, so it cannot be edited as a past cycle).</summary>
    public Guid? OccurrenceId { get; }

    public DateOnly Date { get; }
    private readonly DateTimeOffset? _completedAtLocal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(IsHead))]
    public partial OccurrencePipKind Kind { get; set; }

    public OccurrencePipViewModel(Guid? occurrenceId, DateOnly date, OccurrencePipKind kind, DateTimeOffset? completedAtLocal)
    {
        OccurrenceId = occurrenceId;
        Date = date;
        Kind = kind;
        _completedAtLocal = completedAtLocal;
    }

    /// <summary>True for the live current/next/terminal head pip (no backing record) — it is not an
    /// editable past cycle and reads with a heavier marker.</summary>
    public bool IsHead => Kind is OccurrencePipKind.Next or OccurrencePipKind.Ended;

    /// <summary>The complement of <see cref="IsHead"/>. Lets the recorded-cycle marker (a plain colored
    /// glyph) and the head marker (an accent disc) swap by visibility, the way the old timeline's day
    /// header toggled its plain day number against the accent "today" disc.</summary>
    public bool IsNotHead => !IsHead;

    /// <summary>True when the pip is a recorded past cycle whose status can be edited from its flyout.</summary>
    public bool IsEditable => OccurrenceId is not null;

    /// <summary>Short date for the pip's top line, e.g. "6/5".</summary>
    public string DateLabel => Date.ToString("M/d", CultureInfo.InvariantCulture);

    /// <summary>A status glyph chosen to stay distinct without color: ● 완료, ○ 미수행, × 건너뜀, ◉ 다음/종료.</summary>
    public string Glyph => Kind switch
    {
        OccurrencePipKind.Completed => "●", // ●
        OccurrencePipKind.Missed => "○",    // ○
        OccurrencePipKind.Skipped => "×",   // ×
        OccurrencePipKind.Next => "◉",      // ◉
        OccurrencePipKind.Ended => "◉",     // ◉
        _ => "○",
    };

    /// <summary>The status word shown under the date — never color alone.</summary>
    public string StatusLabel => Kind switch
    {
        OccurrencePipKind.Completed => "완료",
        OccurrencePipKind.Missed => "미수행",
        OccurrencePipKind.Skipped => "건너뜀",
        OccurrencePipKind.Next => "다음",
        OccurrencePipKind.Ended => "종료",
        _ => string.Empty,
    };

    /// <summary>The accessibility name read by a screen reader: the full date and the status.</summary>
    public string AutomationName
    {
        get
        {
            var date = Date.ToString("M월 d일", Korean);
            return Kind == OccurrencePipKind.Completed && _completedAtLocal is { } at
                ? $"{date}, 완료, {at.ToString("tt h시 m분", Korean)}"
                : $"{date}, {StatusLabel}";
        }
    }

    /// <summary>The hover tooltip: the date, the status, and the completion time for a completed cycle.</summary>
    public string Tooltip
    {
        get
        {
            var date = Date.ToString("M월 d일", Korean);
            return Kind == OccurrencePipKind.Completed && _completedAtLocal is { } at
                ? $"{date} · 완료 · {at.ToString("tt h:mm", Korean)}"
                : $"{date} · {StatusLabel}";
        }
    }
}
