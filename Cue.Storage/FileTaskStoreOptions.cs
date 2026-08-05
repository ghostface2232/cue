using System.Security.Cryptography;
using System.Text;

namespace Cue.Storage;

/// <summary>
/// Configuration for <see cref="FileTaskStore"/>. The root folder is injected, never hardcoded,
/// so that a later phase can point it at a cloud-synced folder by changing only this value.
/// </summary>
public sealed class FileTaskStoreOptions
{
    /// <summary>
    /// The root folder under which the per-type subfolders (<c>tasks/</c>, <c>groups/</c>,
    /// <c>tags/</c>, <c>meta/</c>) live.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Filesystem path for the SQLite query index. The index is a per-device, <i>disposable</i> cache,
    /// so it is configured separately from <see cref="RootPath"/>: the index must stay on local storage
    /// and never be synced — syncing a monolithic database file is exactly what the invariants forbid
    /// and would spawn per-device conflict copies. When <c>null</c>, the index is co-located at
    /// <c>{RootPath}/index.db</c>, which is only correct for a root that is guaranteed local (a test's
    /// temp folder). <see cref="CreateDefault"/> never leaves this null: the shipped default root is
    /// Documents, which Windows commonly redirects into OneDrive, so the app pins the index under
    /// <c>%LOCALAPPDATA%</c> instead — see <see cref="DefaultIndexPath"/>.
    /// </summary>
    public string? IndexPath { get; init; }

    /// <summary>
    /// The v1 default: a "Cue" folder under the user's Documents folder
    /// (<see cref="Environment.SpecialFolder.MyDocuments"/>) for the record files, with the index
    /// pinned to local storage (<see cref="DefaultIndexPath"/>). The root deliberately lives
    /// <i>outside</i> <c>%USERPROFILE%\AppData</c>: MSIX virtualizes writes under AppData (redirected,
    /// and wiped on uninstall), so keeping the root out of AppData gives the unpackaged and Store
    /// builds identical data semantics (data survives an uninstall) and lets a later cloud-folder root
    /// slot in. <see cref="Environment.SpecialFolder.MyDocuments"/> resolves the real Documents path
    /// even when it is redirected into OneDrive. Both folders are created on demand at store open and
    /// on first save.
    /// </summary>
    public static FileTaskStoreOptions CreateDefault(string appFolderName = "Cue")
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            appFolderName);
        return new FileTaskStoreOptions
        {
            RootPath = root,
            IndexPath = DefaultIndexPath(root, appFolderName),
        };
    }

    /// <summary>
    /// The local, never-synced home for a data root's index: <c>%LOCALAPPDATA%\{appFolderName}\index-{hash}.db</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default data root is Documents, and Windows redirects Documents into OneDrive on a large share
    /// of machines — so co-locating the index there would sync a single database file that is rewritten on
    /// every save. That is precisely what the invariants forbid: across two PCs it produces conflict
    /// copies, lock contention, and constant uploads, and it is the first thing to break when the data root
    /// itself becomes a synced folder. AppData is the right home for the index for the same reason it is
    /// the wrong home for the data root: it is per-device, never synced, and losing it on uninstall costs
    /// nothing because the index is rebuilt from the files at startup. The settings file already lives on
    /// this exact path, so both distribution channels are already proven against it.
    /// </para>
    /// <para>
    /// The file name carries a stable hash of the root so several data roots on one machine each get their
    /// own database instead of colliding on one. The hash is over the case-insensitively normalized path
    /// and is computed from SHA-256 (not <c>string.GetHashCode</c>, which .NET randomizes per process),
    /// so the same root always resolves to the same index file across launches.
    /// </para>
    /// </remarks>
    public static string DefaultIndexPath(string rootPath, string appFolderName = "Cue")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appFolderName,
            $"index-{StableRootHash(rootPath)}.db");
    }

    /// <summary>A short, launch-stable fingerprint of a data root path (16 hex chars of its SHA-256).</summary>
    private static string StableRootHash(string rootPath)
    {
        // Windows paths are case-insensitive and a trailing separator is not part of the identity, so
        // normalize both away — otherwise "…\Cue" and "…\cue\" would each get their own index.
        var normalized = rootPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
