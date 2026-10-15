// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace StreamEdge.AvaloniaApp;

/// <summary>Opens a URL in the user's default browser.</summary>
/// <remarks>Plain process launch rather than Avalonia's ILauncher, so view
/// models can call it without a TopLevel.</remarks>
public static class UrlLauncher
{
    public static bool TryOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return false;
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", uri.AbsoluteUri)
                : new ProcessStartInfo("xdg-open", uri.AbsoluteUri);
            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
