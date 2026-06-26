using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cue.Domain;

namespace Cue.ViewModels;

/// <summary>How one timeline pip reads — a recorded past cycle's outcome, the live current cycle, a
/// projected future cycle, or the terminal head once the series has ended.</summary>
public enum OccurrencePipKind
{
    /// <summary>완료 — a recorded cycle that was carried out.</summary>
    Completed,

    /// <summary>미수행 — a recorded cycle that was not carried out.</summary>
    Missed,

    /// <summary>현재 — the live current cycle (the series' own <see cref="TaskItem.When"/>); not yet
    /// performed, so it reads as a neutral hollow marker, not the accent of a done cycle. Not a record.</summary>
    Current,

    /// <summary>예정 — a future cycle computed from the rule, shown dimmed ahead of the current one; not a
    /// record and not interactive (it hasn't happened yet).</summary>
    Future,

    /// <summary>종료 — the series has ended (반복 종료 or exhausted); the live head is the terminal record.</summary>
    Ended,
}

/// <summary>
/// One pip in the detail-panel recurrence timeline: a single cycle rendered as a date, a status glyph,
/// and a status label. Past cycles map to a <see cref="RecurrenceOccurrence"/> (and carry its
/// <see cref="OccurrenceId"/> so the per-cycle flyout can load and edit it); the current, future, and
/// terminal pips are synthesized from the live series / rule and carry no id.
/// </summary>
/// <remarks>
/// The status is conveyed three ways that never rely on color alone — a distinct glyph, a visible
/// status label under the date, and the <see cref="AutomationName"/>/<see cref="Tooltip"/> — so the
/// timeline stays legible to color-blind and screen-reader users and under reduced-motion.
/// </remarks>
public partial class OccurrencePipViewModel : ObservableObject
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>The occurrence record this pip stands for, or <c>null</c> for a synthesized pip — the
    /// current cycle, a future cycle, or the terminal head — none of which are editable records.</summary>
    public Guid? OccurrenceId { get; }

    public DateOnly Date { get; }
    private readonly DateTimeOffset? _completedAtLocal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    public partial OccurrencePipKind Kind { get; set; }

    public OccurrencePipViewModel(Guid? occurrenceId, DateOnly date, OccurrencePipKind kind, DateTimeOffset? completedAtLocal)
    {
        OccurrenceId = occurrenceId;
        Date = date;
        Kind = kind;
        _completedAtLocal = completedAtLocal;
    }

    /// <summary>True for the most recent recorded cycle (the one immediately before the current cycle).
    /// A single click on the latest <see cref="OccurrencePipKind.Completed"/> pip is the quick undo —
    /// it rolls that completion back — while older completions open the editor flyout instead. Set by the
    /// detail view model when it builds the strip.</summary>
    public bool IsLatestRecord { get; set; }

    /// <summary>True when the pip is a recorded past cycle that takes a click (opens its flyout or, for the
    /// latest completion, undoes it). The current/future/terminal pips are display-only.</summary>
    public bool IsInteractive => OccurrenceId is not null;

    /// <summary>A future cycle is rendered dimmed (it hasn't happened and can't be acted on yet); every
    /// other pip is at full strength.</summary>
    public double VisualOpacity => Kind == OccurrencePipKind.Future ? 0.5 : 1.0;

    /// <summary>Short date for the pip's top line, e.g. "6/5".</summary>
    public string DateLabel => Date.ToString("M/d", CultureInfo.InvariantCulture);

    /// <summary>A status glyph chosen to stay distinct without color: ● 완료 (filled = done), ○ 현재
    /// (hollow = pending now), × 미수행, ◌ 예정, ◉ 종료.</summary>
    public string Glyph => Kind switch
    {
        OccurrencePipKind.Completed => "●", // ●
        OccurrencePipKind.Missed => "×",    // ×
        OccurrencePipKind.Current => "○",   // ○
        OccurrencePipKind.Future => "◌",    // ◌
        OccurrencePipKind.Ended => "◉",     // ◉
        _ => "×",
    };

    /// <summary>The status word shown under the date — never color alone.</summary>
    public string StatusLabel => Kind switch
    {
        OccurrencePipKind.Completed => "완료",
        OccurrencePipKind.Missed => "미수행",
        OccurrencePipKind.Current => "현재",
        OccurrencePipKind.Future => "예정",
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
