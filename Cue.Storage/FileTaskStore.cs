using System.Text.Json;
using Cue.Domain;
using Cue.Storage.Serialization;

namespace Cue.Storage;

/// <summary>
/// File-based <see cref="ITaskStore"/>: the source of truth is one JSON file per record, named
/// <c>{id}.json</c>, under a per-type subfolder of the configured root.
/// </summary>
public sealed class FileTaskStore : ITaskStore
{
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _json;

    public FileTaskStore(FileTaskStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("RootPath must be set.", nameof(options));

        _root = options.RootPath;
        _clock = timeProvider ?? TimeProvider.System;
        _json = StoreSerialization.CreateOptions();
    }

    /// <summary>The configured root folder. Exposed mainly for diagnostics and tests.</summary>
    public string RootPath => _root;

    public async Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        where T : RecordBase
    {
        var dir = DirectoryFor(typeof(T));
        if (!Directory.Exists(dir))
            return Array.Empty<T>();

        var results = new List<T>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            // Guard against the legacy wildcard also matching "*.json.tmp".
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<T>(bytes, _json);
            if (record is not null)
                results.Add(record);
        }

        return results;
    }

    public async Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default)
        where T : RecordBase
    {
        var path = PathFor(typeof(T), id);
        if (!File.Exists(path))
            return null;

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, _json);
    }

    public async Task SaveAsync<T>(T record, CancellationToken cancellationToken = default)
        where T : RecordBase
    {
        ArgumentNullException.ThrowIfNull(record);

        // Timestamp stamping is the store's job, not the domain's.
        var now = _clock.GetUtcNow();
        if (record.CreatedAt == default)
            record.CreatedAt = now;
        record.UpdatedAt = now;

        // Serialize by the runtime type so subclass-specific properties are included.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, record.GetType(), _json);

        var dir = DirectoryFor(record.GetType());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, record.Id + ".json");

        // Atomic write: write a temp file, then swap it into place so a crash mid-write can't
        // leave a half-written record.
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default)
        where T : RecordBase
    {
        var record = await GetAsync<T>(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return; // nothing to delete; idempotent

        // Soft delete: stamp the tombstone and re-save (which also bumps UpdatedAt). Never remove
        // the file — sync and index rebuild must still see the tombstone.
        record.DeletedAt = _clock.GetUtcNow();
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private string DirectoryFor(Type recordType) => Path.Combine(_root, FolderName(recordType));

    private string PathFor(Type recordType, Guid id)
        => Path.Combine(DirectoryFor(recordType), id + ".json");

    private static string FolderName(Type recordType) => recordType switch
    {
        _ when recordType == typeof(TaskItem) => "tasks",
        _ when recordType == typeof(Project) => "projects",
        _ when recordType == typeof(Section) => "sections",
        _ when recordType == typeof(Label) => "labels",
        _ => throw new NotSupportedException($"No storage partition is defined for '{recordType.Name}'."),
    };
}
