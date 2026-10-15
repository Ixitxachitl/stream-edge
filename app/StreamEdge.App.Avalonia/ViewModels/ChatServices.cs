// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>What every tab's chat shares: third-party emotes, Twitch and FFZ
/// badges, pronouns, settings, and who "me" is for mention highlighting.</summary>
public sealed class ChatServices
{
    public ChatServices(HttpClient http, AppSettings settings, IUiDispatcher ui, Func<string?> selfLogin)
    {
        Settings = settings;
        Ui = ui;
        SelfLogin = selfLogin;
        Emotes = new EmoteRegistry(http);
        Emotes.Apply(settings);
        Badges = new TwitchBadges();
        Pronouns = new PronounService(http);
    }

    public AppSettings Settings { get; }
    public IUiDispatcher Ui { get; }
    public Func<string?> SelfLogin { get; }
    public EmoteRegistry Emotes { get; }
    public TwitchBadges Badges { get; }
    public PronounService Pronouns { get; }

    /// <summary>One badge to draw: its image when known, else the set name
    /// for a placeholder chip (Twitch's badge API needs a signed-in user).</summary>
    public readonly record struct Badge(string Set, BadgeImage? Image);

    /// <summary>Twitch badges in IRC order (FFZ's mod/VIP art replacing
    /// Twitch's when the channel has it), then the user's FFZ badges.</summary>
    public IReadOnlyList<Badge> BadgesFor(ChatMessage message, string? roomId)
    {
        var list = new List<Badge>(message.Badges.Count + 1);
        foreach (var badge in message.Badges)
        {
            var set = badge.Split('/')[0];
            var ffz = roomId is null ? null : set switch
            {
                "moderator" => Emotes.FfzModeratorBadge(roomId),
                "vip" => Emotes.FfzVipBadge(roomId),
                _ => null,
            };
            list.Add(ffz is not null
                ? new Badge(set, new BadgeImage(ffz, set == "vip" ? "VIP" : "Moderator", set == "moderator" ? "#00AD03" : null))
                : new Badge(set, Badges.Resolve(roomId, badge)));
        }
        foreach (var ffz in Emotes.FfzBadgesFor(message.UserId))
            list.Add(new Badge("ffz", ffz));
        return list;
    }

    public IReadOnlyList<ChatToken> Tokenize(ChatMessage message, string? roomId) =>
        ChatTokenizer.Tokenize(message, code => Emotes.Lookup(roomId, code), SelfLogin());
}
