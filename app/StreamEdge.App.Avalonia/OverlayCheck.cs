// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;

namespace StreamEdge.AvaloniaApp;

/// <summary>Spots game overlays that inject themselves into every process
/// drawing with the GPU. RivaTuner Statistics Server (MSI Afterburner's
/// overlay) hooks the Direct3D swap chain, and its hook crashes inside
/// dxgi.dll when several videos resize quickly, as switching tabs, columns
/// or PIP does. It injects some seconds after start, so this is polled.</summary>
internal static partial class OverlayCheck
{
    private const string RivaTunerHooks = "RTSSHooks64.dll";

    private const string RivaTunerAdvice =
        "RivaTuner Statistics Server (MSI Afterburner's overlay) is hooked into StreamEdge, and its hook " +
        "can crash StreamEdge when videos switch size. In RivaTuner, add StreamEdge.exe with \"+\" and set " +
        "Application detection level to None, then restart StreamEdge.";

    /// <summary>Advice for each known overlay hooking this process.</summary>
    public static IEnumerable<string> Loaded()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var module = GetModuleHandle(RivaTunerHooks);
        // RivaTuner loads its DLL into every process, even ones whose profile
        // turns hooking off; it then leaves those alone. Only warn when this
        // app hasn't been excluded.
        if (module != IntPtr.Zero && !RivaTunerExcluded(module)) yield return RivaTunerAdvice;
    }

    /// <summary>RivaTuner's profile for this exe (Profiles\StreamEdge.exe.cfg
    /// next to its hook DLL) says EnableHooking=0, i.e. detection level None.</summary>
    private static bool RivaTunerExcluded(IntPtr module)
    {
        try
        {
            var path = new StringBuilder(1024);
            if (GetModuleFileName(module, path, path.Capacity) == 0) return false;
            var exe = Path.GetFileName(Environment.ProcessPath);
            if (Path.GetDirectoryName(path.ToString()) is not { } dir || exe is null) return false;
            var profile = Path.Combine(dir, "Profiles", exe + ".cfg");
            return File.Exists(profile)
                && File.ReadLines(profile).Any(l => l.Trim().Equals("EnableHooking=0", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string name);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);
}
