using System.Text.Json;

namespace Cue.Storage;

internal enum ContainerDeletionKind { Project, Label }

internal sealed class ContainerDeletionOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public int SchemaVersion { get; init; } = 1;
    public required ContainerDeletionKind Kind { get; init; }
    public required Guid TargetId { get; init; }
    public bool IsCompleted { get; set; }
}

/// <summary>Durable, atomic intent log for idempotent multi-record deletion sagas.</summary>
internal sealed class ContainerDeletionJournal
{
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ContainerDeletionJournal(string rootPath, TimeProvider? clock = null)
    {
        _directory = Path.Combine(rootPath, "meta", "operations");
        _clock = clock ?? TimeProvider.System;
    }

    public async Task WriteAsync(ContainerDeletionOperation operation, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var now = _clock.GetUtcNow();
        if (operation.CreatedAt == default) operation.CreatedAt = now;
        operation.UpdatedAt = now;
        var path = Path.Combine(_directory, operation.Id + ".json");
        var temp = Path.Combine(_directory, $"{operation.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(operation, _json), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<ContainerDeletionOperation>> GetPendingAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory)) return Array.Empty<ContainerDeletionOperation>();
        var result = new List<ContainerDeletionOperation>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var operation = JsonSerializer.Deserialize<ContainerDeletionOperation>(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), _json);
                if (operation is { IsCompleted: false }) result.Add(operation);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Container deletion journal is unreadable: '{path}'.", exception);
            }
        }
        return result;
    }
}
