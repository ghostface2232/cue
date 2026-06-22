namespace Cue.Storage.Index;

/// <summary>An active project projection served by the disposable SQLite index.</summary>
public sealed record ProjectListItem(
    Guid Id,
    string Name,
    DateOnly? DeadlineDate,
    string SortOrder);

/// <summary>An active section projection served by the disposable SQLite index.</summary>
public sealed record SectionListItem(
    Guid Id,
    Guid ProjectId,
    string Name,
    DateOnly? DeadlineDate,
    string SortOrder);

/// <summary>An active cross-cutting label projection served by the disposable SQLite index.</summary>
public sealed record LabelListItem(
    Guid Id,
    string Name,
    string? Color,
    string SortOrder);
