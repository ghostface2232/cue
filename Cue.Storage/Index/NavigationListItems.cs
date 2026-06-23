namespace Cue.Storage.Index;

/// <summary>An active project (pure group) projection served by the disposable SQLite index.</summary>
public sealed record ProjectListItem(
    Guid Id,
    string Name,
    string? Icon,
    string SortOrder);

/// <summary>An active cross-cutting label projection served by the disposable SQLite index.</summary>
public sealed record LabelListItem(
    Guid Id,
    string Name,
    string? Color,
    string SortOrder);
