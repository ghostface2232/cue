namespace Cue.Storage;

/// <summary>
/// Configuration for <see cref="FileTaskStore"/>. The root folder is injected, never hardcoded,
/// so that a later phase can point it at a cloud-synced folder by changing only this value.
/// </summary>
public sealed class FileTaskStoreOptions
{
    /// <summary>
    /// The root folder under which the per-type subfolders (<c>tasks/</c>, <c>projects/</c>,
    /// <c>sections/</c>, <c>labels/</c>, <c>meta/</c>) live.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Filesystem path for the SQLite query index. The index is a per-device, <i>disposable</i> cache,
    /// so it is configured separately from <see cref="RootPath"/>: when the data root later points at
    /// a cloud-synced folder, the index must stay on local storage and never be synced — syncing a
    /// monolithic database file is exactly what the invariants forbid and would spawn per-device
    /// conflict copies. When <c>null</c>, the index is co-located at <c>{RootPath}/index.db</c>, which
    /// is correct while the root is itself local (the v1 default). Point this at a local path whenever
    /// <see cref="RootPath"/> moves to the cloud; if several local data roots share one machine, give
    /// each its own index path so they do not collide on one database.
    /// </summary>
    public string? IndexPath { get; init; }

    /// <summary>
    /// The v1 default: a "Cue" folder under the local application data path
    /// (<see cref="Environment.SpecialFolder.LocalApplicationData"/>).
    /// </summary>
    public static FileTaskStoreOptions CreateDefault(string appFolderName = "Cue")
        => new()
        {
            RootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                appFolderName),
        };
}
