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
    /// <summary>How long ago, coarsely: "45m", "5h", "3d", "2mo".</summary>
    public static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 60 ? ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m"
            : span.TotalHours < 48 ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
            : span.TotalDays < 60 ? ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + "d"
            : ((int)(span.TotalDays / 30)).ToString(CultureInfo.InvariantCulture) + "mo";
    }

    public static string Viewers(int viewers) =>
        viewers.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Viewer count as twitch.tv's sidebar shows it: "676",
    /// "11.4K", "1.2M". Rounded down, so it never claims more than there are.</summary>
    public static string CompactViewers(int viewers) => viewers switch
    {
        < 1_000 => viewers.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (Math.Floor(viewers / 100.0) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => (Math.Floor(viewers / 100_000.0) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "M",
    };

    /// <summary>Window-title stats, "[viewers|uptime]".</summary>
    public static string TitleStats(int viewers, TimeSpan uptime) =>
        $"[{Viewers(viewers)}|{Uptime(uptime)}]";
}
