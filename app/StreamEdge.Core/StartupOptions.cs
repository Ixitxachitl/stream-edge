// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge;

/// <summary>Command-line switches. Each overrides the matching setting for
/// this run only; nothing here is written back to settings.json.</summary>
/// <example><c>StreamEdge --screen 1 --fullscreen --channel nintendope503</c></example>
public sealed record StartupOptions
{
    /// <summary>Sample chat and streams, no network.</summary>
    public bool Demo { get; init; }
    public string? Channel { get; init; }
    public int? ScreenIndex { get; init; }
    public bool Fullscreen { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            options = args[i].ToLowerInvariant() switch
            {
                "--demo" => options with { Demo = true },
                "--fullscreen" => options with { Fullscreen = true },
                "--channel" when Next() is { } c => options with { Channel = c.TrimStart('#') },
                "--screen" when int.TryParse(Next(), out var n) => options with { ScreenIndex = n },
                _ => options,
            };
        }
        return options;
    }
}
