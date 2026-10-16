// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace StreamEdge.Playback;

/// <summary>What the strip along the bottom of a hovered video says.</summary>
/// <param name="Stats">"11,455 viewers · 2.4h", or null when unknown.</param>
public sealed record InfoStrip(string Channel, string Title, string Game, string? Stats);

/// <summary>Turns an <see cref="InfoStrip"/> into ASS events for mpv's
/// <c>osd-overlay</c>. The video is a native window that Avalonia can't draw
/// over, so the strip is drawn by mpv itself.</summary>
public static class InfoStripAss
{
    /// <summary>Virtual height the events are laid out at; mpv scales it to
    /// the video. Width is left to mpv (res_x 0 follows the window's
    /// aspect), so the box is simply drawn wider than any window.</summary>
    public const int ResY = 720;

    /// <summary>Where the text's top is when it fits on two lines, for when
    /// mpv can't say how tall it came out.</summary>
    public const int TwoLineTop = ResY - Bottom - 66;

    private const int Pad = 18;
    private const int Bottom = 12;

    // ASS colours are &HBBGGRR&, alpha &H00 opaque to &HFF clear.
    private const string Text = "&HF1EFEF&";    // SeText #EFEFF1
    private const string Dim = "&HB8ADAD&";     // SeTextDim #ADADB8
    private const string Accent = "&HFF70A9&";  // SeAccent #A970FF
    private const string Live = "&H0004EB&";    // Twitch live red #EB0400

    /// <summary>The strip: a dark band from just above <paramref name="textTop"/>
    /// (the top of <see cref="TextEvent"/> as mpv laid it out) to the
    /// bottom, then the text over it.</summary>
    public static string Build(InfoStrip strip, int textTop)
    {
        var top = Math.Clamp(textTop - Bottom, 0, ResY);
        var height = ResY - top;
        // Full width, about 70% opaque.
        return $@"{{\an7\pos(0,{top})\bord0\shad0\1c&H000000&\1a&H50&\p1}}m 0 0 l 20000 0 20000 {height} 0 {height}{{\p0}}"
            + "\n" + TextEvent(strip);
    }

    /// <summary>Two lines anchored bottom-left; a title too long for the
    /// video's width wraps, growing the text upward.</summary>
    public static string TextEvent(InfoStrip strip)
    {
        var sb = new StringBuilder();
        sb.Append($@"{{\an1\pos({Pad},{ResY - Bottom})\q1\bord0\shad0\fs30\b1\1c{Text}}}").Append(Escape(strip.Channel));
        if (strip.Title.Length > 0) sb.Append($@"{{\b0\1c{Dim}}}   ").Append(Escape(strip.Title));
        sb.Append($@"\N{{\fs24\b0}}");
        var parts = new List<string>();
        if (strip.Stats is { Length: > 0 } stats) parts.Add($@"{{\1c{Live}}}●{{\1c{Dim}}} " + Escape(stats));
        if (strip.Game.Length > 0) parts.Add($@"{{\1c{Accent}}}" + Escape(strip.Game));
        sb.Append(string.Join($@"{{\1c{Dim}}}   ·   ", parts));
        return sb.ToString();
    }

    /// <summary>Stream titles are user text: braces would start override
    /// blocks and a backslash an escape, so both are defused, the way mpv's
    /// own OSC does. Newlines become spaces.</summary>
    public static string Escape(string text) => text
        .Replace("\\", "\\\u2060")
        .Replace("{", "\\{")
        .Replace("}", "\\}")
        .Replace('\n', ' ')
        .Replace('\r', ' ');
}
