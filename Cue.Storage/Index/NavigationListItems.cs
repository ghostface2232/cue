namespace Cue.Storage.Index;

/// <summary>An active task group (pure group) projection served by the disposable SQLite index.</summary>
public sealed record TaskGroupListItem(
    Guid Id,
    string Name,
    string? Icon,
    string SortOrder);

/// <summary>An active cross-cutting tag projection served by the disposable SQLite index.</summary>
public sealed record TagListItem(
    Guid Id,
    string Name,
    string? Color,
    string SortOrder);
