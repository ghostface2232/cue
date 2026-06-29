namespace Cue.ViewModels;

/// <summary>
/// How a standard task list orders its open rows. The choice is a single global preference (set once
/// from any list's header, reflected on every list), persisted through <see cref="IListDisplayPreferences"/>.
/// </summary>
/// <remarks>
/// <see cref="Manual"/> is the app's native order — the fractional <c>SortOrder</c> rank the user arranges
/// by dragging — and is the only mode in which drag-to-reorder is active. The other three are computed
/// orderings layered over the index result at display time; they leave the stored ranks untouched, so
/// switching back to <see cref="Manual"/> restores the hand-arranged order exactly.
/// </remarks>
public enum TaskSortMode
{
    /// <summary>자유 배치 — the user's drag-arranged order (the stored fractional rank). Drag is enabled.</summary>
    Manual,

    /// <summary>날짜순 — by When date then time, ascending; undated rows last. On a single-day view (e.g.
    /// 오늘 할 일) this collapses to a time ordering.</summary>
    Date,

    /// <summary>이름순 — by title, ascending (Korean collation).</summary>
    Name,

    /// <summary>중요도순 — most urgent first (P1→P4), unflagged rows last.</summary>
    Priority,
}
