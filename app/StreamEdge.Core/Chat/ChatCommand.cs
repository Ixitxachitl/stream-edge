// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace StreamEdge.Chat;

/// <summary>A slash command typed in the chat box ("/timeout someone 10m
/// spam"). Twitch stopped running these over IRC in 2023 (only /me is left
/// there), so each one is carried out through the Helix API instead.</summary>
/// <param name="Name">Lower case, without the slash.</param>
/// <param name="Text">Everything after the name, trimmed.</param>
public sealed record ChatCommand(string Name, string Text)
{
    /// <summary>The command in <paramref name="text"/>, or null when it's an
    /// ordinary message (no slash, or "/me", which IRC still sends).</summary>
    public static ChatCommand? Parse(string text)
    {
        text = text.Trim();
        if (text.Length < 2 || text[0] != '/' || char.IsWhiteSpace(text[1])) return null;
        var space = text.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? text[1..] : text[1..space]).ToLowerInvariant();
        if (name == "me") return null;
        return new ChatCommand(name, space < 0 ? "" : text[space..].Trim());
    }

    /// <summary>The words of <see cref="Text"/>.</summary>
    public IReadOnlyList<string> Args => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The <paramref name="index"/>th word, or null.</summary>
    public string? Arg(int index) => index < Args.Count ? Args[index] : null;

    /// <summary>A user named as an argument, without the @ people type.</summary>
    public string? User(int index) => Arg(index)?.TrimStart('@').ToLowerInvariant() is { Length: > 0 } u ? u : null;

    /// <summary><see cref="Text"/> after its first <paramref name="words"/>
    /// words, as typed (spacing kept), trimmed.</summary>
    public string After(int words)
    {
        var rest = Text.AsSpan();
        for (var i = 0; i < words; i++)
        {
            rest = rest.TrimStart();
            var end = rest.IndexOfAny(' ', '\t');
            rest = end < 0 ? [] : rest[end..];
        }
        return rest.Trim().ToString();
    }

    /// <summary>A duration as typed for timeouts and chat modes: plain
    /// seconds ("90") or with a unit ("30s", "10m", "2h", "1d", "1w"). Null
    /// for anything else.</summary>
    public static int? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();
        var unit = char.IsLetter(text[^1]) ? text[^1] : 's';
        var digits = char.IsLetter(text[^1]) ? text[..^1] : text;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        long seconds = unit switch
        {
            's' => n,
            'm' => n * 60L,
            'h' => n * 3600L,
            'd' => n * 86_400L,
            'w' => n * 604_800L,
            _ => -1,
        };
        return seconds is >= 0 and <= int.MaxValue ? (int)seconds : null;
    }

    /// <summary>What /help lists: usage, then what it does.</summary>
    public static readonly IReadOnlyList<(string Usage, string What)> Help =
    [
        ("/me <text>", "Say it as an action"),
        ("/w <user> <message>", "Whisper (direct message)"),
        ("/r <message>", "Whisper back to whoever whispered last"),
        ("/color <name or #hex>", "Change your name colour"),
        ("/block <user>, /unblock <user>", "Block or unblock someone"),
        ("/ban <user> [reason], /unban <user>", "Moderators"),
        ("/timeout <user> [10m] [reason], /untimeout <user>", "Moderators; plain seconds or s/m/h/d/w"),
        ("/warn <user> <reason>", "Moderators"),
        ("/clear", "Moderators: clear the chat"),
        ("/slow [seconds], /slowoff", "Moderators"),
        ("/followers [duration], /followersoff", "Moderators"),
        ("/subscribers, /subscribersoff", "Moderators"),
        ("/emoteonly, /emoteonlyoff", "Moderators"),
        ("/uniquechat, /uniquechatoff", "Moderators"),
        ("/announce <message>", "Moderators; also /announceblue, green, orange, purple"),
        ("/shield, /shieldoff", "Moderators: shield mode"),
        ("/shoutout <user>, /so <user>", "Moderators: shout out another channel"),
    ];

    /// <summary>Each command on its own, for the list typing '/' opens.</summary>
    public static readonly IReadOnlyList<ChatCommandInfo> Known =
    [
        new("me", "<text>", "Say it as an action"),
        new("w", "<user> <message>", "Whisper (direct message)", Aliases: ["whisper"]),
        new("r", "<message>", "Whisper back to whoever whispered last", Aliases: ["reply"]),
        new("color", "<name or #hex>", "Change your name colour", Aliases: ["colour"]),
        new("block", "<user>", "Block someone"),
        new("unblock", "<user>", "Unblock someone"),
        new("help", "", "List the commands", Aliases: ["commands"]),
        new("shoutout", "<user>", "Shout out another channel", Moderator: true, Aliases: ["so"]),
        new("ban", "<user> [reason]", "Ban someone", Moderator: true),
        new("unban", "<user>", "Lift a ban", Moderator: true),
        new("timeout", "<user> [10m] [reason]", "Time someone out; seconds or s/m/h/d/w", Moderator: true),
        new("untimeout", "<user>", "End a timeout", Moderator: true),
        new("warn", "<user> <reason>", "Warn someone", Moderator: true),
        new("announce", "<message>", "Announce; also /announceblue, green, orange, purple", Moderator: true),
        new("clear", "", "Clear the chat", Moderator: true),
        new("slow", "[seconds]", "Slow mode", Moderator: true),
        new("slowoff", "", "Slow mode off", Moderator: true),
        new("followers", "[duration]", "Followers-only mode", Moderator: true),
        new("followersoff", "", "Followers-only mode off", Moderator: true),
        new("subscribers", "", "Subscribers-only mode", Moderator: true),
        new("subscribersoff", "", "Subscribers-only mode off", Moderator: true),
        new("emoteonly", "", "Emote-only mode", Moderator: true),
        new("emoteonlyoff", "", "Emote-only mode off", Moderator: true),
        new("uniquechat", "", "Unique chat mode", Moderator: true),
        new("uniquechatoff", "", "Unique chat mode off", Moderator: true),
        new("shield", "", "Shield mode", Moderator: true),
        new("shieldoff", "", "Shield mode off", Moderator: true),
    ];

    /// <summary>The commands whose name (or an alias) starts with
    /// <paramref name="typed"/>, in <see cref="Known"/>'s order; moderator
    /// ones only for a <paramref name="moderator"/>.</summary>
    public static IReadOnlyList<ChatCommandInfo> Complete(string typed, bool moderator) =>
    [
        .. Known.Where(c => (moderator || !c.Moderator)
            && c.Names.Any(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))),
    ];
}

/// <summary>A command the chat box knows, as its suggestion list shows it.</summary>
/// <param name="Usage">Its arguments, e.g. "&lt;user&gt; [reason]".</param>
public sealed record ChatCommandInfo(string Name, string Usage, string What, bool Moderator = false,
    IReadOnlyList<string>? Aliases = null)
{
    public string Slash => "/" + Name;

    public IEnumerable<string> Names => [Name, .. Aliases ?? []];
}
