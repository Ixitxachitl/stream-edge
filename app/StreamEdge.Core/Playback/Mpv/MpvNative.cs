// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;

namespace StreamEdge.Playback.Mpv;

/// <summary>The handful of libmpv client API calls the player uses.
/// https://github.com/mpv-player/mpv/blob/master/include/mpv/client.h</summary>
internal static class MpvNative
{
    private const string Lib = "mpv";

    public const int FormatFlag = 3;
    public const int FormatDouble = 5;

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, Resolve);
    }

    /// <summary>libmpv's file name differs per platform and per packaging;
    /// try the ones distributions actually ship.</summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows() ? ["libmpv-2.dll", "mpv-2.dll", "mpv-1.dll"]
            : OperatingSystem.IsMacOS() ? ["libmpv.2.dylib", "libmpv.dylib",
                "/opt/homebrew/lib/libmpv.dylib", "/usr/local/lib/libmpv.dylib"]
            : ["libmpv.so.2", "libmpv.so.1", "libmpv.so"];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
        return IntPtr.Zero;
    }

    [DllImport(Lib)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib)]
    public static extern int mpv_command_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    [DllImport(Lib)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    [DllImport(Lib, EntryPoint = "mpv_get_property")]
    public static extern int mpv_get_property_flag(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int data);

    [DllImport(Lib)]
    public static extern IntPtr mpv_error_string(int error);

    public static string ErrorString(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
