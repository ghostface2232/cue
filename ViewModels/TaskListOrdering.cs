using System.Globalization;
using Cue.Domain;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>
/// Orders an open task-row projection for display per the global <see cref="TaskSortMode"/>. Shared by every
/// surface that honours the choice — the standard list, each 중요도 section, and each 타임라인 week column — so
/// one rule drives them all: 자유 배치 keeps the index's stored-rank order, while the computed modes layer a
/// date / name / priority ordering over it, each falling back to the stored rank as a stable tiebreaker.
/// </summary>
/// <remarks>
/// Used to order rows <i>within</i> a fixed grouping too: the 중요도 buckets and the timeline's week columns
/// keep their structure, and this only sets the order of the rows inside each. A computed sort never touches
/// the persisted ranks, so 자유 배치 always restores the hand-arranged order exactly.
/// </remarks>
public static class TaskListOrdering
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), ignoreCase: true);

    public static IReadOnlyList<TaskListItem> Apply(IReadOnlyList<TaskListItem> items, TaskSortMode mode)
        => mode switch
        {
            TaskSortMode.Date => items
                .OrderBy(i => i.WhenDate is null)
                .ThenBy(i => i.WhenDate ?? DateOnly.MaxValue)
                .ThenBy(i => i.WhenTime ?? TimeOnly.MinValue)
                .ThenBy(i => i.SortOrder, StringComparer.Ordinal)
                .ToList(),
            TaskSortMode.Name => items
                .OrderBy(i => i.Title, NameComparer)
                .ThenBy(i => i.SortOrder, StringComparer.Ordinal)
                .ToList(),
            TaskSortMode.Priority => items
                .OrderBy(i => i.Priority == Priority.None)
                .ThenBy(i => i.Priority)
                .ThenBy(i => i.SortOrder, StringComparer.Ordinal)
                .ToList(),
            // 자유 배치: the stored fractional rank, the same ordinal/byte order SQLite's BINARY collation
            // sorts by. The flat list already arrives in this order, but the timeline arrives date-then-rank,
            // so sort explicitly here too — that is what makes a manual move reorder a timeline week column.
            _ => items
                .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
                .ToList(),
        };
}
