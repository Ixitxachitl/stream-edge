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
}
