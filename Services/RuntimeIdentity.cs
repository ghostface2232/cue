using System.Runtime.InteropServices;
using System.Text;

namespace Cue.Services;

/// <summary>
/// The single runtime check for how Cue was deployed: does the current process run with MSIX package
/// identity (the Microsoft Store channel) or without it (the unpackaged GitHub channel)?
/// <para>
/// Per the AGENTS.md distribution invariants, every channel branch reads this one helper at runtime —
/// there is no <c>#if</c> conditional-compilation split, so both code paths always compile. The result
/// is cached on first read because a process's deployment identity cannot change over its lifetime.
/// This mirrors the same probe the WCT compat library performs internally
/// (<c>DesktopBridgeHelpers.HasIdentity</c>), so our channel branch and the toast library's own
/// packaged/unpackaged switch always agree.
/// </para>
/// </summary>
internal static class RuntimeIdentity
{
    // GetCurrentPackageFullName returns this (APPMODEL_ERROR_NO_PACKAGE) when the process has no
    // package identity; anything else (a length + ERROR_INSUFFICIENT_BUFFER) means it does.
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<bool> LazyIsPackaged = new(DetectPackageIdentity);

    /// <summary>True when the process runs with MSIX package identity (the Microsoft Store channel).</summary>
    public static bool IsPackaged => LazyIsPackaged.Value;

    private static bool DetectPackageIdentity()
    {
        // First-call pattern: a zero length + null buffer asks only "is there a package?". An unpackaged
        // process answers APPMODEL_ERROR_NO_PACKAGE; a packaged one answers ERROR_INSUFFICIENT_BUFFER
        // (with the required length), which is all we need to distinguish the two.
        int length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
