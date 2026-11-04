using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Cue.Services;

/// <summary>
/// Writes <see cref="Trace"/> output to a size-capped local file, keeping a couple of older files beside it.
/// A toast's headless process can log at the same time as the main window's, so each write takes a
/// cross-process lock and opens the file afresh. Logging must never be the thing that breaks the app, so every
/// failure here is swallowed.
/// </summary>
internal sealed class RollingFileTraceListener : TraceListener
{
    private const int KeptOlderFiles = 2;
    // A log line is never worth holding up the UI thread for long.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly long _maxBytes;
    // Across processes, not just threads: FileMode.Append writes at the offset it found on opening, so two
    // writers without this overwrite each other's lines, and rotation would race too.
    private readonly Mutex _gate;

    public RollingFileTraceListener(string path, long maxBytes = 1_000_000)
    {
        _path = path;
        _maxBytes = maxBytes;
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        _gate = new Mutex(initiallyOwned: false, $"Cue.Log.{pathHash[..16]}");
    }

    public override void Write(string? message) => Append(message);

    public override void WriteLine(string? message) => Append(message);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _gate.Dispose();
        base.Dispose(disposing);
    }

    private void Append(string? message)
    {
        var acquired = false;
        try
        {
            try { acquired = _gate.WaitOne(MaxWait); }
            catch (AbandonedMutexException) { acquired = true; } // its owner exited mid-write; ours now
            if (!acquired)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            RotateIfFull();
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, Utf8WithoutBom);
            writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Environment.ProcessId}] {message}");
        }
        catch
        {
            // Nowhere left to report this; the app carries on without a log line.
        }
        finally
        {
            if (acquired)
                _gate.ReleaseMutex();
        }
    }

    private void RotateIfFull()
    {
        var current = new FileInfo(_path);
        if (!current.Exists || current.Length < _maxBytes)
            return;

        for (var i = KeptOlderFiles; i >= 1; i--)
        {
            var older = OlderPath(i);
            if (!File.Exists(older))
                continue;
            if (i == KeptOlderFiles)
                File.Delete(older);
            else
                File.Move(older, OlderPath(i + 1), overwrite: true);
        }
        File.Move(_path, OlderPath(1), overwrite: true);
    }

    private string OlderPath(int generation)
        => Path.Combine(
            Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.{generation}{Path.GetExtension(_path)}");
}
