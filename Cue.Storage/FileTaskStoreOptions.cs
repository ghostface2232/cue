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
