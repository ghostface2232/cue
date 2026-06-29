using System.Globalization;
using Cue.Domain;
using Cue.Storage.Index;

namespace Cue.ViewModels;

/// <summary>
/// Orders an open task-row projection for display per the global <see cref="TaskSortMode"/>. Shared by every
/// surface that honours the choice — the standard list, each 중요도 section, and each 타임라인 week column — so
/// one rule drives them all: a date / name / priority ordering, each falling back to the stored fractional
/// rank (the order tasks were added) as a stable tiebreaker.
/// </summary>
/// <remarks>
/// Used to order rows <i>within</i> a fixed grouping too: the 중요도 buckets and the timeline's week columns
/// keep their structure, and this only sets the order of the rows inside each.
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
            // Defensive fallback for any unmapped mode: the stored fractional rank (the order tasks were
            // added), the same ordinal/byte order SQLite's BINARY collation sorts by.
            _ => items
                .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
                .ToList(),
        };
}
