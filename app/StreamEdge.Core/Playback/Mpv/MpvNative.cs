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
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Lib)]
    public static extern IntPtr mpv_error_string(int error);

    public const int FormatInt64 = 4;
    public const int FormatNodeMap = 8;

    /// <summary>mpv_node: an 8-byte union (string, int64, double, list
    /// pointer) followed by its format.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Node
    {
        public long Value;
        public int Format;
    }

    /// <summary>mpv_node_list, for NODE_MAP and NODE_ARRAY.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NodeList
    {
        public int Count;
        public IntPtr Values;
        public IntPtr Keys;
    }

    [DllImport(Lib)]
    public static extern int mpv_command_ret(IntPtr ctx, IntPtr[] args, out Node result);

    [DllImport(Lib)]
    public static extern void mpv_free_node_contents(ref Node node);

    /// <summary>Runs a command with arguments as given (no string parsing)
    /// and returns the integer fields of a map result, e.g. osd-overlay's
    /// bounds. Null if the command failed.</summary>
    public static Dictionary<string, long>? CommandForInts(IntPtr ctx, params string[] args)
    {
        var argv = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            if (mpv_command_ret(ctx, argv, out var node) < 0) return null;
            try
            {
                var result = new Dictionary<string, long>();
                if (node.Format != FormatNodeMap) return result;
                var list = Marshal.PtrToStructure<NodeList>((IntPtr)node.Value);
                var size = Marshal.SizeOf<Node>();
                for (var i = 0; i < list.Count; i++)
                {
                    var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size));
                    var value = Marshal.PtrToStructure<Node>(list.Values + i * size);
                    if (key is null) continue;
                    if (value.Format == FormatInt64) result[key] = value.Value;
                    else if (value.Format == FormatDouble) result[key] = (long)BitConverter.Int64BitsToDouble(value.Value);
                }
                return result;
            }
            finally { mpv_free_node_contents(ref node); }
        }
        finally
        {
            foreach (var arg in argv) Marshal.FreeCoTaskMem(arg);
        }
    }

    public static string ErrorString(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
