// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>Slash commands, whispers, replies and go-live alerts.</summary>
public sealed partial class MainViewModel
{
    // ----- Slash commands ---------------------------------------------------

    private void Say(ChannelTabViewModel tab, string text) =>
        tab.Chat.Add(ChatMessage.System(tab.Login, text, DateTimeOffset.Now));

    /// <summary>The user's own Helix actions, or null with why not. Looking
    /// people up works with any token; the actions need
    /// <see cref="AccountViewModel.CanUseUserActions"/>.</summary>
    private (TwitchUserApi? Api, string? Why) UserApi(bool action = true)
    {
        if (!Account.IsSignedIn || Account.AccessToken is not { } token || Account.UserId is not { } me)
            return (null, "Log in to do that.");
        if (action && !Account.CanUseUserActions)
            return (null, "Log in again (⚙) for whispers, /color and /block: StreamEdge now asks for those permissions.");
        return (new TwitchUserApi(_http, _settings.EffectiveClientId, token, me), null);
    }

    private async Task<TwitchUser?> FindUserAsync(ChannelTabViewModel tab, string? login)
    {
        if (login is null)
        {
            Say(tab, "Who? Name a user.");
            return null;
        }
        var (api, why) = UserApi(action: false);
        if (api is null)
        {
            Say(tab, why!);
            return null;
        }
        var user = await api.GetUserAsync(login, _stop.Token);
        if (user is null) Say(tab, $"No Twitch user called {login}.");
        return user;
    }

    /// <summary>Carries out a slash command typed in <paramref name="tab"/>'s
    /// chat box. Twitch dropped them from IRC in 2023, so each is a Helix
    /// call; what happened (or why not) is said in that chat.</summary>
    public async Task RunCommandAsync(ChannelTabViewModel tab, ChatCommand command)
    {
        switch (command.Name)
        {
            case "help" or "commands":
                Say(tab, "Commands: " + string.Join(" · ", ChatCommand.Help.Select(h => $"{h.Usage} ({h.What})")));
                return;
            case "w" or "whisper":
                await WhisperAsync(tab, command.User(0), command.After(1));
                return;
            case "r" or "reply":
                await WhisperAsync(tab, _lastWhisperFrom, command.Text);
                return;
            case "color" or "colour":
            {
                if (command.Arg(0) is not { } color)
                {
                    Say(tab, "Usage: /color <name or #hex>, e.g. /color blue_violet");
                    return;
                }
                var (api, why) = UserApi();
                var result = api is null ? new ModResult(false, why) : await api.SetChatColorAsync(color, _stop.Token);
                Say(tab, result.Ok ? $"Your name colour is now {TwitchUserApi.NormalizeColor(color)}." : result.Error!);
                return;
            }
            case "block" or "unblock":
            {
                var (api, why) = UserApi();
                if (api is null)
                {
                    Say(tab, why!);
                    return;
                }
                if (await FindUserAsync(tab, command.User(0)) is not { } user) return;
                var block = command.Name == "block";
                var result = block ? await api.BlockAsync(user.Id, _stop.Token) : await api.UnblockAsync(user.Id, _stop.Token);
                Say(tab, result.Ok ? $"{(block ? "Blocked" : "Unblocked")} {user.DisplayName}." : result.Error!);
                return;
            }
        }

        if (!ModeratorCommands.Contains(command.Name))
        {
            Say(tab, $"Unknown command /{command.Name}. Type /help for the ones StreamEdge knows.");
            return;
        }
        var (mod, whyNot) = Moderation();
        if (mod is null || tab.RoomId is not { } room)
        {
            Say(tab, whyNot ?? "This chat hasn't finished joining yet.");
            return;
        }
        var outcome = await ModerateAsync(tab, mod, room, command);
        if (outcome is { } r) Say(tab, r.Ok ? Done(command) : "Twitch refused: " + r.Error);
    }

    private static readonly HashSet<string> ModeratorCommands =
    [
        "ban", "unban", "timeout", "untimeout", "warn", "clear",
        "slow", "slowoff", "followers", "followersoff", "subscribers", "subscribersoff",
        "emoteonly", "emoteonlyoff", "uniquechat", "uniquechatoff",
        "announce", "announceblue", "announcegreen", "announceorange", "announcepurple",
        "shield", "shieldoff", "shoutout", "so",
    ];

    /// <summary>Null when it stopped early and has already said why.</summary>
    private async Task<ModResult?> ModerateAsync(ChannelTabViewModel tab, TwitchModeration mod, string room, ChatCommand command)
    {
        var ct = _stop.Token;
        switch (command.Name)
        {
            case "ban" or "unban" or "untimeout" or "timeout" or "warn":
            {
                if (await FindUserAsync(tab, command.User(0)) is not { } user) return null;
                switch (command.Name)
                {
                    case "ban":
                        return await mod.BanAsync(room, user.Id, null, command.After(1), ct);
                    case "unban" or "untimeout":
                        return await mod.UnbanAsync(room, user.Id, ct);
                    case "timeout":
                        // "/timeout x spam" is ten minutes for spam; "/timeout x 1h spam" an hour.
                        var seconds = ChatCommand.ParseDuration(command.Arg(1));
                        return await mod.BanAsync(room, user.Id, seconds ?? 600, command.After(seconds is null ? 1 : 2), ct);
                    default:
                        if (command.After(1) is not { Length: > 0 } reason)
                        {
                            Say(tab, "Usage: /warn <user> <reason>");
                            return null;
                        }
                        return await mod.WarnAsync(room, user.Id, reason, ct);
                }
            }
            case "clear":
                return await mod.ClearChatAsync(room, ct);
            case "shield" or "shieldoff":
                return await mod.SetShieldModeAsync(room, command.Name == "shield", ct);
            case "shoutout" or "so":
            {
                if (!Account.CanShoutout)
                {
                    Say(tab, "Log in again (⚙) to give shoutouts: StreamEdge now asks for that permission.");
                    return null;
                }
                if (await FindUserAsync(tab, command.User(0)) is not { } user) return null;
                return await mod.ShoutoutAsync(room, user.Id, ct);
            }
            case var a when a.StartsWith("announce", StringComparison.Ordinal):
                if (command.Text.Length == 0)
                {
                    Say(tab, "Usage: /announce <message>");
                    return null;
                }
                var color = a == "announce" ? "primary" : a["announce".Length..];
                return await mod.AnnounceAsync(room, command.Text, color, ct);
            default:
            {
                // A chat mode: change the one asked for, keep the rest.
                if (await mod.GetChatSettingsAsync(room, ct) is not { } s)
                {
                    Say(tab, "Couldn't read this channel's chat settings.");
                    return null;
                }
                var duration = ChatCommand.ParseDuration(command.Arg(0));
                s = command.Name switch
                {
                    "slow" => s with { SlowSeconds = duration ?? 30 },
                    "slowoff" => s with { SlowSeconds = null },
                    // Followers-only takes minutes; "/followers 10m" and "/followers 10" both mean ten.
                    "followers" => s with { FollowerMinutes = command.Arg(0) is { } f && f.All(char.IsAsciiDigit) ? int.Parse(f) : (duration ?? 0) / 60 },
                    "followersoff" => s with { FollowerMinutes = null },
                    "subscribers" or "subscribersoff" => s with { SubscriberOnly = command.Name == "subscribers" },
                    "emoteonly" or "emoteonlyoff" => s with { EmoteOnly = command.Name == "emoteonly" },
                    _ => s with { UniqueChat = command.Name == "uniquechat" },
                };
                return await mod.SetChatSettingsAsync(room, s, ct);
            }
        }
    }

    private static string Done(ChatCommand command) => command.Name switch
    {
        "ban" => $"Banned {command.User(0)}.",
        "unban" or "untimeout" => $"{command.User(0)} can chat again.",
        "timeout" => $"Timed out {command.User(0)}.",
        "warn" => $"Warned {command.User(0)}.",
        "clear" => "Chat cleared.",
        "shield" => "Shield mode on.",
        "shieldoff" => "Shield mode off.",
        "shoutout" or "so" => $"Shouted out {command.User(0)}.",
        var a when a.StartsWith("announce", StringComparison.Ordinal) => "Announced.",
        _ => "Chat settings updated.",
    };

    // ----- Whispers ---------------------------------------------------------

    /// <summary>Who whispered last, for /r.</summary>
    private string? _lastWhisperFrom;

    private async Task WhisperAsync(ChannelTabViewModel tab, string? login, string message)
    {
        if (login is null)
        {
            Say(tab, _lastWhisperFrom is null && message.Length > 0
                ? "Nobody has whispered you yet: use /w <user> <message>."
                : "Usage: /w <user> <message>");
            return;
        }
        if (message.Length == 0)
        {
            Say(tab, $"Say something: /w {login} <message>");
            return;
        }
        // The whisper itself goes in the inbox, not this chat.
        Say(tab, await SendWhisperAsync(login, null, message) ?? $"Whispered {login}.");
    }

    /// <summary>Sends a whisper and keeps it in the inbox. Returns why it
    /// wasn't sent, or null.</summary>
    /// <param name="userId">Theirs, when known: saves looking it up.</param>
    private async Task<string?> SendWhisperAsync(string login, string? userId, string message)
    {
        var (api, why) = UserApi();
        if (api is null) return why;
        var user = userId is null ? await api.GetUserAsync(login, _stop.Token) : new TwitchUser(userId, login, "");
        if (user is null) return $"No Twitch user called {login}.";
        var result = await api.WhisperAsync(user.Id, message, _stop.Token);
        if (!result.Ok) return "Whisper not sent: " + result.Error;
        // No display name when it wasn't looked up: the conversation keeps its own.
        Whispers.Sent(user.Login, user.DisplayName, user.Id, message, DateTimeOffset.Now);
        return null;
    }

    /// <summary>A whisper arrived: into the ✉ inbox, never a chat, with an
    /// alert that opens it (unless it's open there already); /r answers it.</summary>
    public void OnWhisper(WhisperEvent whisper)
    {
        _lastWhisperFrom = whisper.FromLogin;
        var name = whisper.FromName.Length > 0 ? whisper.FromName : whisper.FromLogin;
        if (Whispers.Received(whisper.FromLogin, name, whisper.FromId, whisper.Text, DateTimeOffset.Now)) return;
        var from = whisper.FromLogin;
        Notify?.Invoke(new AppNotification($"Whisper from {name}", whisper.Text, () => Whispers.Open(from)));
    }

    // ----- Replies ----------------------------------------------------------

    /// <summary>Answer this line: the next message from its chat goes as a
    /// reply to it.</summary>
    public void StartReply(ChatEntry entry)
    {
        var m = entry.Message;
        if (m.Id is null || m.Login is null || TabFor(m.Channel) is not { } owner) return;
        owner.Chat.ReplyTo = entry;
        // Sent from the chat being typed in: make it this one.
        if (ColumnOf(owner) is { } column) ActiveColumn = column;
    }

    // ----- Go-live alerts ---------------------------------------------------

    /// <summary>A short alert inside the window, with what clicking it does.</summary>
    public event Action<AppNotification>? Notify;

    /// <summary>Favourites live at the last refresh; null before the first,
    /// so starting up doesn't announce everyone already live.</summary>
    private HashSet<string>? _favouritesLive;

    /// <summary>Favourites that went live since the last refresh get an alert.</summary>
    public void AlertGoLive(IReadOnlyList<LiveStream> live)
    {
        var now = live.Where(s => _settings.IsFavorite(s.Login)).ToList();
        var known = _favouritesLive;
        _favouritesLive = [.. now.Select(s => s.Login.ToLowerInvariant())];
        if (known is null || !_settings.NotifyGoLive) return;
        foreach (var s in now.Where(s => !known.Contains(s.Login.ToLowerInvariant())))
        {
            var login = s.Login;
            Notify?.Invoke(new AppNotification($"{s.DisplayName} is live",
                s.Game.Length > 0 ? $"{s.Title}\n{s.Game}" : s.Title,
                () => OpenChannel(login, OpenMode.NewTab)));
        }
    }
}

/// <param name="Open">Clicking the alert; null for nothing.</param>
public sealed record AppNotification(string Title, string Message, Action? Open);
