using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cue.Services;

/// <summary>The outcome of a single update check against the GitHub Releases API.</summary>
/// <param name="Current">The running app's version (from the assembly).</param>
/// <param name="Latest">The latest published release's version, or null if none could be read.</param>
/// <param name="UpdateAvailable">True only when <paramref name="Latest"/> is strictly newer than
/// <paramref name="Current"/> and a downloadable installer asset was found.</param>
/// <param name="DownloadUrl">The installer asset's download URL (when an update is available).</param>
/// <param name="Sha256Url">The matching ".sha256" checksum asset's URL, or null if the release does
/// not carry one (older releases predate it — verification is then skipped).</param>
/// <param name="Size">The installer asset's size in bytes (0 if unknown), used to drive progress.</param>
public sealed record UpdateCheckResult(
    Version Current,
    Version? Latest,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? Sha256Url,
    long Size);

/// <summary>A user-facing failure during an update check, download, or launch. Its message is written
/// to be shown verbatim in the settings UI (Korean), so call sites don't reinterpret the cause.</summary>
public sealed class UpdateException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Drives the in-app updater: queries the GitHub Releases API for the latest published release,
/// compares it to the running version, downloads the installer asset (verifying its SHA-256 when the
/// release publishes a checksum), and hands off to the silent installer.
///
/// The app is unpackaged + self-contained and installed per-user by an Inno Setup installer
/// (CueSetup-win-x64.exe). Because a running Cue holds locks on its own files, the installer can't
/// replace them while we're alive — so <see cref="LaunchInstaller"/> spawns a detached helper that runs
/// the installer silently and relaunches Cue once it finishes, and the caller then exits the app.
/// </summary>
public sealed class UpdateService
{
    // The repository whose published releases drive updates. Releases are created as drafts and
    // published manually; "releases/latest" returns only the latest published, non-draft,
    // non-prerelease release — exactly the set we want to offer.
    private const string Owner = "ghostface2232";
    private const string Repo = "cue";

    // Asset names produced by the release workflow (release.yml / build-installer.ps1).
    private const string InstallerAssetName = "CueSetup-win-x64.exe";
    private const string ChecksumAssetName = InstallerAssetName + ".sha256";

    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    // One shared client. Uses the system proxy by default and carries the headers GitHub's API requires
    // (a User-Agent is mandatory; the version pin keeps the response shape stable).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var product = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Cue-Updater/{product}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>The running app's version, read from the assembly so it always matches what shipped.
    /// Prefers the informational version (csproj <c>&lt;Version&gt;</c>, e.g. "0.1.3"), trimming any
    /// "+commit" SourceLink suffix; falls back to the assembly version.</summary>
    public static Version CurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational) &&
            Version.TryParse(informational.Split('+', '-')[0], out var parsed))
            return parsed;
        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>Asks GitHub for the latest published release and decides whether it's newer than the
    /// running build. Network/transport problems surface as <see cref="UpdateException"/> with a
    /// Korean, user-facing message; "no published release yet" resolves to "no update available".</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion();

        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(LatestReleaseUri, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException("업데이트 서버에 연결하지 못했어요. 네트워크 연결을 확인해 주세요.", exception);
        }

        // No published, non-draft release yet → nothing to offer (not an error).
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new UpdateCheckResult(current, null, false, null, null, 0);

        // GitHub signals an exhausted unauthenticated rate limit with 403 (or 429).
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            throw new UpdateException("업데이트 확인 요청이 잠시 제한됐어요. 잠시 후 다시 시도해 주세요.");

        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"업데이트를 확인하지 못했어요. (오류 {(int)response.StatusCode})");

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new UpdateException("업데이트 정보를 읽지 못했어요. 잠시 후 다시 시도해 주세요.", exception);
        }

        return Parse(body, current);
    }

    private static UpdateCheckResult Parse(string body, Version current)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            // A release with an unparseable tag is treated as "nothing newer to offer" rather than an error.
            return new UpdateCheckResult(current, null, false, null, null, 0);

        string? installerUrl = null;
        string? checksumUrl = null;
        long installerSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                    continue;

                if (string.Equals(name, InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    installerUrl = url;
                    if (asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size))
                        installerSize = size;
                }
                else if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        // Only offer the update when it's strictly newer AND we actually have an installer to fetch.
        var available = latest > current && installerUrl is not null;
        return new UpdateCheckResult(current, latest, available, installerUrl, checksumUrl, installerSize);
    }

    /// <summary>Downloads the installer to a temp file, reporting fractional progress (0–1), then —
    /// when the release published a checksum — verifies the file's SHA-256 against it, deleting the
    /// download and throwing on mismatch. Returns the local installer path on success.</summary>
    public async Task<string> DownloadAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update.DownloadUrl is null)
            throw new UpdateException("내려받을 설치 파일을 찾지 못했어요.");

        // A per-run temp folder keeps the installer out of the way and avoids clobbering a prior download.
        var folder = Path.Combine(Path.GetTempPath(), "Cue-Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var installerPath = Path.Combine(folder, InstallerAssetName);

        try
        {
            await DownloadToFileAsync(update.DownloadUrl, installerPath, update.Size, progress, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            TryDelete(installerPath);
            throw new UpdateException("업데이트를 내려받지 못했어요. 네트워크 연결을 확인해 주세요.", exception);
        }

        if (update.Sha256Url is not null)
            await VerifyChecksumAsync(installerPath, update.Sha256Url, cancellationToken);

        return installerPath;
    }

    private static async Task DownloadToFileAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? (expectedSize > 0 ? expectedSize : -1);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            if (total > 0)
                progress?.Report(Math.Clamp((double)read / total, 0, 1));
        }

        if (total <= 0)
            progress?.Report(1);
    }

    private static async Task VerifyChecksumAsync(
        string installerPath,
        string checksumUrl,
        CancellationToken cancellationToken)
    {
        string published;
        try
        {
            // The checksum asset is tiny; read it whole. Accepts both a bare hex digest and the common
            // "<hex> *filename" (Get-FileHash / sha256sum) shape — we take the first 64-hex token.
            var text = await Http.GetStringAsync(checksumUrl, cancellationToken);
            published = ExtractHex(text);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            TryDelete(installerPath);
            throw new UpdateException("설치 파일을 검증하지 못했어요. 잠시 후 다시 시도해 주세요.", exception);
        }

        if (published.Length != 64)
        {
            TryDelete(installerPath);
            throw new UpdateException("설치 파일 검증 정보를 읽지 못했어요. 잠시 후 다시 시도해 주세요.");
        }

        string actual;
        await using (var stream = File.OpenRead(installerPath))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            actual = Convert.ToHexString(hash);
        }

        if (!string.Equals(actual, published, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(installerPath);
            throw new UpdateException("내려받은 설치 파일이 손상됐어요. 다시 시도해 주세요.");
        }
    }

    /// <summary>The first 64-hex-character run in the text (the SHA-256 digest), or "".</summary>
    private static string ExtractHex(string text)
    {
        var run = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (Uri.IsHexDigit(text[i]))
            {
                if (run == 0) start = i;
                if (++run == 64) return text.Substring(start, 64);
            }
            else
            {
                run = 0;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Spawns a detached helper that runs the installer silently and relaunches Cue when it finishes,
    /// then returns. The caller must exit the app immediately afterward so the installer can replace the
    /// (otherwise locked) program files.
    ///
    /// The helper is a single <c>cmd /c</c> line: run the installer with Inno Setup's very-silent flags
    /// (no UI, no prompts, force-close the running app, no machine restart), then <c>start</c> the freshly
    /// installed exe. The install path is stable across versions (the installer keeps a constant AppId),
    /// so the current exe path still resolves to the upgraded binary.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        var appExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(appExe))
            throw new UpdateException("앱 경로를 확인하지 못해 설치를 시작할 수 없어요.");

        // cmd /c strips the single pair of outer quotes, so the whole command is wrapped once and each
        // path is quoted inside it. `&` chains the relaunch after the installer exits.
        var command =
            $"\"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS " +
            $"& start \"\" \"{appExe}\"\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            throw new UpdateException("설치 프로그램을 실행하지 못했어요.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort — a leftover temp file is harmless.
        }
    }
}
