// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace StreamEdge.Ui;

public static class UiFormats
{
    /// <summary>Compact uptime: minutes below an hour ("22m"), then hours to
    /// one decimal ("2.4h", "10.2h") — short enough for a list header on a
    /// 400-pixel-wide pane.</summary>
    public static string Uptime(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 60)
            return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        return Math.Round(span.TotalHours, 1, MidpointRounding.AwayFromZero)
            .ToString("0.0", CultureInfo.InvariantCulture) + "h";
    }

    /// <summary>Viewer count with thousands separators ("11,455").</summary>
    public static string Viewers(int viewers) =>
        viewers.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Window-title stats, "[viewers|uptime]".</summary>
    public static string TitleStats(int viewers, TimeSpan uptime) =>
        $"[{Viewers(viewers)}|{Uptime(uptime)}]";
}
