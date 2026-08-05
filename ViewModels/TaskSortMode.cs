namespace Cue.ViewModels;

/// <summary>
/// How a standard task list orders its open rows. The choice is a single global preference (set once
/// from any list's header, reflected on every list), persisted through <see cref="IListDisplayPreferences"/>.
/// </summary>
/// <remarks>
/// Every mode is a computed ordering layered over the index result at display time, each falling back to
/// the stored fractional <c>SortOrder</c> rank (the order tasks were added) as a stable tiebreaker.
/// </remarks>
public enum TaskSortMode
{
    /// <summary>날짜순 — by When date then time, ascending; undated rows last. On a single-day view (e.g.
    /// 오늘 할 일) this collapses to a time ordering. The default.</summary>
    Date,

    /// <summary>이름순 — by title, ascending (Korean collation).</summary>
    Name,

    /// <summary>중요도순 — most urgent first (P1→P4), unflagged rows last.</summary>
    Priority,
}
