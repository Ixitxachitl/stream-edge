// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge;

/// <summary>Where StreamEdge keeps its files: %APPDATA%\StreamEdge on Windows,
/// ~/.config/StreamEdge on Linux, ~/Library/Application Support/StreamEdge on
/// macOS. Tests point <see cref="Override"/> at a temp folder; the
/// <c>STREAMEDGE_DATA</c> environment variable does the same for a whole run
/// (handy for trying things without touching your real settings).</summary>
public static class AppData
{
    public static string? Override { get; set; }

    public static string Directory
    {
        get
        {
            var dir = Override
                ?? (Environment.GetEnvironmentVariable("STREAMEDGE_DATA") is { Length: > 0 } env ? env : null)
                ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.Create),
                "StreamEdge");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PathFor(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>The previous good copy of <paramref name="path"/>, kept by
    /// <see cref="WriteSafely(string, byte[])"/>.</summary>
    public static string BackupOf(string path) => path + ".bak";

    /// <summary>Replaces <paramref name="path"/> so that a crash or power cut
    /// leaves either the old file or the new one, never a half-written one.
    /// The new bytes reach the disk before the swap: without that, NTFS can
    /// keep the new length but not the data, leaving a file of NULs. The file
    /// it replaces is kept as <see cref="BackupOf"/>.</summary>
    public static void WriteSafely(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(temp, path, BackupOf(path), ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }

    public static void WriteSafely(string path, string text) =>
        WriteSafely(path, System.Text.Encoding.UTF8.GetBytes(text));
}
