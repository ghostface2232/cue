using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cue.Services;

/// <summary>
/// The app's local diagnostics log, under <c>%LOCALAPPDATA%\Cue\logs</c>. Every layer writes through
/// <see cref="Trace"/>, which needs no dependency on this type and stays compiled in Release, so the storage
/// and view-model layers stay free of app concerns. The log never leaves the machine, and call sites must not
/// write task titles or notes to it.
/// </summary>
internal static class AppLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cue",
        "logs",
        "cue.log");

    /// <summary>Starts logging. Called first thing in the process so a failure anywhere after it is recorded.</summary>
    public static void Initialize()
    {
        Trace.Listeners.Add(new RollingFileTraceListener(FilePath));
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Trace.WriteLine($"[Cue] Unhandled exception (terminating: {args.IsTerminating}): {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Trace.WriteLine($"[Cue] Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Trace.WriteLine($"[Cue] Started {version} on {Environment.OSVersion.VersionString}");
    }

    /// <summary>Opens File Explorer with the current log file selected.</summary>
    public static void RevealLogFile() => RevealFile(ResolveShellVisibleFile());

    /// <summary>Opens File Explorer with <paramref name="path"/> selected.</summary>
    public static void RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Trace.WriteLine($"[Cue] Unable to open File Explorer: {exception.Message}");
        }
    }

    /// <summary>
    /// The log file's real location. In the packaged build Windows redirects writes under AppData into the
    /// package's private store, so the path this process uses doesn't exist for File Explorer, which runs
    /// outside the package. Asking the open file for its final path gives the location both can see, in
    /// either channel, without a channel branch.
    /// </summary>
    private static string ResolveShellVisibleFile()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, buffer.Capacity, 0);
            if (length > 0 && length < buffer.Capacity)
                return buffer.ToString().Replace(@"\\?\", string.Empty, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to the path as this process sees it.
        }
        return FilePath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int pathLength, int flags);
}
