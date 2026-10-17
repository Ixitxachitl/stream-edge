// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat.Emotes;

public enum EmoteProvider
{
    Twitch,
    Bttv,
    Ffz,
    SevenTv,
}

/// <summary>An emote image to draw in place of <see cref="Code"/>.</summary>
/// <param name="ImageUrl">The 2x image: sharp on HiDPI, small enough to
/// download for a whole chat's worth of emotes. May be animated (GIF/WebP).</param>
/// <param name="ZeroWidth">Drawn on top of the emote before it (7TV
/// zero-width, BTTV's legacy hats, FFZ overlay modifiers) rather than beside it.</param>
/// <param name="Width">Display (1x, logical pixel) width when the provider
/// says; null means "unknown, assume Twitch's 28 px height and keep aspect".</param>
/// <param name="Height">Display (1x, logical pixel) height, as for <paramref name="Width"/>.</param>
public sealed record Emote(
    string Code,
    Uri ImageUrl,
    EmoteProvider Provider,
    bool ZeroWidth = false,
    int? Width = null,
    int? Height = null)
{
    /// <summary>The largest image there is, for an emote drawn far above its
    /// usual size (Twitch's "gigantify an emote", which only takes Twitch
    /// emotes): Twitch's 3.0, its biggest. Others keep <see cref="ImageUrl"/>.</summary>
    public Uri LargestImageUrl =>
        Provider == EmoteProvider.Twitch && ImageUrl.AbsoluteUri.EndsWith("/2.0", StringComparison.Ordinal)
            ? new Uri(ImageUrl.AbsoluteUri[..^"2.0".Length] + "3.0")
            : ImageUrl;
}
