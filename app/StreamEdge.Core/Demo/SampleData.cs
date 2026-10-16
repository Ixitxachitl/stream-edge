// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Streams;

namespace StreamEdge.Demo;

/// <summary>Fixed content for <c>--demo</c>, the UI tests and the XAML
/// previewer, so layout work needs neither a network nor a Twitch account.</summary>
public static class SampleData
{
    public const string Channel = "pixelperegrine";

    public static IReadOnlyList<LiveStream> Streams(DateTimeOffset now)
    {
        LiveStream S(string name, string title, string game, int viewers, double hours, bool fav = false) =>
            new(name.ToLowerInvariant(), name, title, game, viewers, now - TimeSpan.FromHours(hours), fav);

        return
        [
            S("CometCrumb", "Look out Streamer, Deadlines! (Level Design)", "Super Mario World", 57, 2.4, fav: true),
            S("lanternmoth", "[18+] LATE NIGHT SCREAMING — NEW CROWD CONTROL MEMES AND SCARE ALERTS !kofi", "Fears to Fathom - Scratch Creek", 55, 1.3),
            S("MossyLedge", "PSG finale??? :3 | !pronouns !discord", "Super Mario World", 44, 3.5),
            S("kaizokettle", "Testing Kaizo - Recovery Arc !slime | Lets be Friends", "Super Mario World", 16, 22 / 60.0),
            S("PixelPeregrine", "Gotta finish some games to play new ones haha <3 :3 !slap", "Graveyard Keeper", 11, 59 / 60.0, fav: true),
            S("Pebble_Patrol42", "elephants i geuss", "Super Mario World", 9, 2.1),
            S("fogcabinet", "First Playthrough | No Spoilers | No Backseating", "Resident Evil 2", 8, 1.8),
            S("AmberAtlas", "Convergence: Underworld Mage Path", "ELDEN RING", 5, 10.2),
            S("BigTimeBramble", "Donnie \"Free Wheel Spins\" McDer---- you know the rest", "Grand Theft Auto V", 11455, 2.7),
            S("VelvetCartridge", "Hellraiser Revival, Ace Combat 8", "Clive Barker's Hellraiser: Revival", 5518, 1.8),
            S("IronOrchard", "Finale time? VALOR MORTIS has been pretty fun, let's do it", "Valor Mortis", 2447, 3.4),
            S("RelayMarathon", "Speedrunning marathon in support of charity", "Retro", 676, 3.6),
        ];
    }

    /// <summary>People in the demo chat who haven't said anything (IRC NAMES).</summary>
    public static IReadOnlyList<string> Lurkers { get; } =
        ["quietkoala", "pixel_pancake", "lurkmaster9000", "sleepyotter", "moss_and_mist", "bytebard"];

    public static IReadOnlyList<ChatMessage> Chat(DateTimeOffset now)
    {
        var t = now - TimeSpan.FromMinutes(10);
        ChatMessage Say(int min, string login, string display, string color, string text, params string[] badges) =>
            new(t + TimeSpan.FromMinutes(min), ChatMessageKind.Message, Channel, login, display, color, badges, text);
        ChatMessage Member(int min, string text) =>
            new(t + TimeSpan.FromMinutes(min), ChatMessageKind.Membership, Channel, null, null, null, [], text);

        return
        [
            ChatMessage.System(Channel, "### Finished with history logs. ###", t),
            Say(0, "pixelperegrine", "PixelPeregrine", "#B65CFF", "https://www.twitch.tv/videos/1234567890", "broadcaster/1", "subscriber/0"),
            Say(0, "chattynarwhal", "ChattyNarwhal", "#4E8FD6", "i have a question"),
            Say(1, "chattynarwhal", "ChattyNarwhal", "#4E8FD6", "why am i soft in the middle now why am i soft in the middle when the rest of my life is so hard?"),
            Member(1, "JOIN: quillfeather"),
            Member(2, "JOIN: Tidewalker"),
            Member(2, "PART: Sprocket2210"),
            Member(4, "PART: Tidewalker"),
            new(t + TimeSpan.FromMinutes(5), ChatMessageKind.Action, Channel, "crittercatchbot", "CritterCatchBot", "#FF6B3D",
                ["moderator/1"], "A wild Murkrow appears Catch it using !pokecatch (winners revealed in 90s)"),
            Say(5, "pixelperegrine", "PixelPeregrine", "#B65CFF", "!pokecatch Ultraball", "broadcaster/1"),
            Say(6, "crittercatchbot", "CritterCatchBot", "#FF6B3D", "Murkrow has been caught by: PixelPeregrine", "moderator/1"),
            Say(7, "chattynarwhal", "ChattyNarwhal", "#4E8FD6", "Elden Ring next please") with { CustomRewardId = "next-game" },
        ];
    }

    public static ChannelPoints Points() => new("Pip Points", null,
    [
        new("hydrate", "Hydrate", 100, "#1F69FF", null, null, false, true),
        new("next-game", "Pick the next game", 2500, "#392E5C", null, "Name a game", true, true),
        new("vip", "VIP for a week", 50000, "#E91916", null, null, false, true),
    ], 12345);
}
