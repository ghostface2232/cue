using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Cue.Services;

/// <summary>App-layer isolation for the Win32 calls needed by unpackaged window activation.</summary>
internal static class WindowForegroundHelper
{
    private const int SwRestore = 9;

    /// <summary>Lets the primary process take foreground focus after this process redirects to it.</summary>
    public static void AllowForProcess(uint processId)
        => _ = AllowSetForegroundWindow(processId);

    /// <summary>Restores a minimized WinUI window and asks Windows to foreground it.</summary>
    public static void BringToForeground(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (handle == IntPtr.Zero)
            return;

        if (IsIconic(handle))
            _ = ShowWindow(handle, SwRestore);

        window.Activate();
        _ = SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
