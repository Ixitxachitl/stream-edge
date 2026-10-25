// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Chat;
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>A chat line as drawn: the message, its tokens (text, emotes,
/// links, mentions) and badges as resolved right now, plus state that can
/// change after it's shown: pronouns arriving, emote sets finishing loading,
/// a moderator deleting it. The view redraws on any property change.</summary>
public sealed partial class ChatEntry : ObservableObject
{
    public ChatEntry(ChatMessage message, string time)
    {
        Message = message;
        Time = time;
    }

    public ChatMessage Message { get; private set; }

    /// <summary>Our own line learns its id from Twitch's USERSTATE, after
    /// it's drawn; with it, it can be deleted or replied to.</summary>
    internal void SetId(string id)
    {
        Message = Message with { Id = id };
        OnPropertyChanged(nameof(Message));
    }
    public string Time { get; }

    /// <summary>The user's own line, drawn locally (Twitch doesn't echo it).</summary>
    public bool IsOwn { get; init; }

    /// <summary>Sent by the signed-in user: drawn here, or come back another
    /// way (history, or sent from twitch.tv or another app).</summary>
    public bool IsFromSelf => IsOwn || _sentBySelf;
    private bool _sentBySelf;

    [ObservableProperty]
    private IReadOnlyList<ChatToken> _tokens = [];

    [ObservableProperty]
    private IReadOnlyList<ChatServices.Badge> _badges = [];

    [ObservableProperty]
    private string? _pronoun;

    [ObservableProperty]
    private bool _isDeleted;

    /// <summary>The channel-points reward this message redeemed, once the
    /// channel's rewards are known.</summary>
    [ObservableProperty]
    private ChannelReward? _reward;

    /// <summary>The channel's points icon, drawn beside a reward's cost;
    /// null for the default.</summary>
    public Uri? PointsIcon { get; set; }

    /// <summary>The user moderates this channel: the line shows mod icons.</summary>
    [ObservableProperty]
    private bool _canModerate;

    /// <summary>The user is this channel's broadcaster, who (unlike a
    /// moderator) can act on moderators too.</summary>
    [ObservableProperty]
    private bool _asBroadcaster;

    /// <summary>Deleted, the text stays, crossed out, rather than giving
    /// way to "&lt;message deleted&gt;" (a setting).</summary>
    [ObservableProperty]
    private bool _showsDeletedText;

    /// <summary>Twitch would delete this line: it has an id, was sent here
    /// (a shared line is deleted in its own channel), is young enough, and
    /// its sender's role allows it.</summary>
    public bool CanDelete => Message.Id is not null && !IsFromOtherRoom
        && DateTimeOffset.Now - Message.Timestamp < ChatRoles.DeleteWindow && Allows(ModPower.Delete);

    /// <summary>The user's own line, drawn before Twitch has named its id:
    /// deletable once it does (<see cref="SetId"/>), so its delete icon's
    /// place is kept meanwhile and nothing shifts when it arrives.</summary>
    public bool WillBeDeletable => IsOwn && Message.Id is null && Allows(ModPower.Delete);

    /// <summary>Twitch would time out or ban this line's sender.</summary>
    public bool CanRemove => Allows(ModPower.Remove);

    private bool Allows(ModPower power) =>
        ChatRoles.Allows(power, AsBroadcaster, ChatRoles.RoleOf(Message.Login, Message.Channel, Message.Badges), IsFromSelf);

    /// <summary>Someone @-mentioned the signed-in user.</summary>
    public bool IsSelfMention => Tokens.Any(t => t.IsSelfMention);

    partial void OnTokensChanged(IReadOnlyList<ChatToken> value) => OnPropertyChanged(nameof(IsSelfMention));

    /// <summary>Shared chat: the channel the line was sent in, for the icon
    /// twitch.tv draws before it; null outside a session or until known.</summary>
    [ObservableProperty]
    private ChannelInfo? _source;

    /// <summary>Shared chat: sent in another channel of the session, so the
    /// mod tools here can't delete it (only time out or ban the sender).</summary>
    public bool IsFromOtherRoom { get; private set; }

    internal void Render(ChatServices services, string? roomId)
    {
        // A shared line's badges and channel emotes are those of the
        // channel it was sent in.
        var room = Message.SourceRoomId ?? roomId;
        IsFromOtherRoom = Message.IsFromOtherRoom(roomId);
        _sentBySelf = Message.Login is { } login && string.Equals(login, services.SelfLogin(), StringComparison.OrdinalIgnoreCase);
        Tokens = services.Tokenize(Message, room, IsOwn);
        Badges = services.BadgesFor(Message, room);
        Source = Message.SourceRoomId is { } source ? services.ChannelById(source) : null;
    }
}

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ChatServices? _services;

    /// <param name="services">Null only for the idle placeholder chat.</param>
    public ChatViewModel(AppSettings settings, ChatServices? services = null)
    {
        _settings = settings;
        _services = services;
    }

    public ObservableCollection<ChatEntry> Lines { get; } = [];

    [ObservableProperty]
    private string _channel = "";

    /// <summary>Twitch's numeric channel id (from ROOMSTATE), which channel
    /// emotes and badges are keyed by. Lines drawn before it arrived are
    /// redrawn when it does.</summary>
    [ObservableProperty]
    private string? _roomId;

    /// <summary>The user is a moderator (or the broadcaster) here, from
    /// USERSTATE: lines get mod icons and the mod menu shows.</summary>
    [ObservableProperty]
    private bool _isModerator;

    partial void OnIsModeratorChanged(bool value)
    {
        foreach (var entry in Lines) entry.CanModerate = value;
    }

    /// <summary>The user is the broadcaster here (from USERSTATE).</summary>
    [ObservableProperty]
    private bool _isBroadcaster;

    partial void OnIsBroadcasterChanged(bool value)
    {
        foreach (var entry in Lines) entry.AsBroadcaster = value;
    }

    /// <summary>This channel's points name, icon, rewards and the user's
    /// balance; null until loaded, or if the channel has points off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoints), nameof(BalanceText), nameof(PointsTip), nameof(Rewards), nameof(PointsNotice))]
    private ChannelPoints? _points;

    public bool HasPoints => Points is not null;

    /// <summary>"12.3K", or empty without an auth-token to read it with.</summary>
    public string BalanceText => Points?.Balance is { } b ? ChannelPoints.Short(b) : "";

    public string PointsTip => Points is not { } p ? ""
        : p.Balance is { } b ? $"{b:N0} {p.Name}"
        : $"{p.Name} · add your auth-token in Settings to see your balance";

    /// <summary>The points menu's tiles, cheapest first.</summary>
    public IReadOnlyList<RewardTile> Rewards => Points is not { } p ? []
        : [.. p.Rewards.Select(r => new RewardTile(r, p.Balance))];

    public string PointsNotice => Points switch
    {
        null => "",
        { Balance: null } => "Add your auth-token in Settings to see your balance. Redeeming happens on twitch.tv.",
        { Rewards.Count: 0 } => "This channel has no custom rewards right now.",
        _ => "Redeeming happens on twitch.tv: Twitch only allows it from its own apps.",
    };

    partial void OnPointsChanged(ChannelPoints? value)
    {
        foreach (var entry in Lines)
            if (entry.Message.CustomRewardId is not null)
            {
                entry.PointsIcon = value?.Icon;
                entry.Reward = value?.Reward(entry.Message.CustomRewardId);
            }
    }

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _draft = "";

    /// <summary>How many sent messages Up can bring back.</summary>
    public const int SentLimit = 100;

    /// <summary>Messages sent from this chat, oldest first.</summary>
    private readonly List<string> _sent = [];
    // Which of them the chat box shows while Up and Down step through; -1
    // is the message being written, kept aside in _unsent.
    private int _recalled = -1;
    private string _unsent = "";
    private bool _recalling;

    partial void OnDraftChanged(string value)
    {
        // Typed into or replaced: Up starts again from the newest.
        if (!_recalling) _recalled = -1;
    }

    /// <summary>A message just sent, for Up to bring back. The same one
    /// twice in a row is kept once.</summary>
    public void RememberSent(string text)
    {
        if (_sent.Count == 0 || _sent[^1] != text) _sent.Add(text);
        if (_sent.Count > SentLimit) _sent.RemoveAt(0);
        _recalled = -1;
    }

    /// <summary>Up (-1) puts the previous sent message in the chat box,
    /// Down (+1) the next; Down past the newest brings back what was being
    /// written. False when there's nowhere further to go.</summary>
    public bool Recall(int step)
    {
        var from = _recalled == -1 ? _sent.Count : _recalled;
        var to = from + step;
        if (to < 0 || to > _sent.Count || to == from) return false;
        if (_recalled == -1) _unsent = Draft;
        _recalled = to == _sent.Count ? -1 : to;
        _recalling = true;
        Draft = _recalled == -1 ? _unsent : _sent[to];
        _recalling = false;
        return true;
    }

    /// <summary>The line the next message answers, shown above the chat box
    /// until sent or cancelled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplying), nameof(ReplyText))]
    private ChatEntry? _replyTo;

    public bool IsReplying => ReplyTo is not null;

    public string ReplyText => ReplyTo?.Message is { } m ? $"Replying to @{m.Sender}: {m.Text}" : "";

    [RelayCommand]
    private void CancelReply() => ReplyTo = null;

    // ----- Sharing a resub ------------------------------------------------

    /// <summary>A resub the user can announce here (twitch.tv's "Share your
    /// resub"), from Twitch with the auth-token; null when there's none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResub), nameof(ResubTitle), nameof(HasStreak))]
    private ResubToken? _resub;

    /// <summary>The user closed the bar for this resub; a later one shows again.</summary>
    private string? _dismissedResub;

    public bool ShowsResub => Resub is { } r && r.Id != _dismissedResub;

    public string ResubTitle => Resub is not { } r ? ""
        : r.IsGift && r.Gifter is { Length: > 0 } gifter
            ? $"Share your {Months(r.CumulativeMonths)} sub (gifted by {gifter})"
            : $"Share your {Months(r.CumulativeMonths)} resub";

    private static string Months(int n) => n == 1 ? "1-month" : $"{n}-month";

    /// <summary>A streak worth mentioning: the switch for it shows.</summary>
    public bool HasStreak => Resub?.StreakMonths > 1;

    /// <summary>The message box and switches are open (Share… pressed).</summary>
    [ObservableProperty]
    private bool _resubExpanded;

    [ObservableProperty]
    private string _resubMessage = "";

    [ObservableProperty]
    private bool _resubIncludeStreak = true;

    /// <summary>Why the last share failed, or "Sharing…" while it goes.</summary>
    [ObservableProperty]
    private string? _resubStatus;

    partial void OnResubChanged(ResubToken? oldValue, ResubToken? newValue)
    {
        if (oldValue?.Id == newValue?.Id) return;
        ResubExpanded = false;
        ResubMessage = "";
        ResubStatus = null;
    }

    [RelayCommand]
    private void ExpandResub() => ResubExpanded = true;

    [RelayCommand]
    private void DismissResub()
    {
        _dismissedResub = Resub?.Id;
        ResubExpanded = false;
        OnPropertyChanged(nameof(ShowsResub));
    }

    /// <summary>Sending needs a signed-in account; anonymous chat is read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private bool _canSend;

    public string Placeholder => CanSend ? "Send a message" : "Log in to chat";

    partial void OnRoomIdChanged(string? value) => RenderAll();

    /// <summary>A line the user just sent, drawn from what Twitch said about
    /// them (badges, colour) since the message itself never comes back.</summary>
    public void AddOwn(ChatMessage message)
    {
        // Twitch doesn't echo it, so it lacks the shared chat tag the
        // others get: it was sent here.
        Add(IsShared && RoomId is { } room ? message with { SourceRoomId = room } : message, own: true);
        if (_pendingOwnId is { } id)
        {
            _pendingOwnId = null;
            ClaimOwnId(id);
        }
    }

    /// <summary>An id Twitch gave a message of ours before its line was drawn.</summary>
    private string? _pendingOwnId;

    /// <summary>Twitch's USERSTATE for a message we sent names its id: give
    /// it to our latest line still without one (or keep it for the line
    /// about to be drawn).</summary>
    public void ClaimOwnId(string id)
    {
        var line = Lines.LastOrDefault(l => l.IsOwn && l.Message.Id is null
                                            && l.Message.Kind is ChatMessageKind.Message or ChatMessageKind.Action);
        if (line is null) _pendingOwnId = id;
        else line.SetId(id);
    }

    public void Add(ChatMessage message) => Add(message, own: false);

    /// <summary>Who's in this chat, as far as it has shown; the viewer list's source.</summary>
    public ChatterRoster Roster => _roster ??= new ChatterRoster(Channel);
    private ChatterRoster? _roster;

    private void Add(ChatMessage message, bool own)
    {
        Roster.Observe(message);
        if (!own) ObserveShared(message);
        // Official Twitch chat doesn't show joins and parts; they're opt-in.
        if (message.Kind == ChatMessageKind.Membership && !_settings.ShowJoinPart) return;

        Lines.Add(NewEntry(message, own));
        while (Lines.Count > Math.Max(50, _settings.ChatScrollback))
            Lines.RemoveAt(0);
    }

    private ChatEntry NewEntry(ChatMessage message, bool own)
    {
        var entry = new ChatEntry(message, TimeOf(message.Timestamp))
        {
            IsOwn = own, CanModerate = IsModerator, AsBroadcaster = IsBroadcaster,
            ShowsDeletedText = _settings.ShowDeletedMessages,
            PointsIcon = Points?.Icon, Reward = Points?.Reward(message.CustomRewardId),
        };
        if (_services is not null)
        {
            entry.Render(_services, RoomId);
            LoadPronoun(entry);
        }
        return entry;
    }

    /// <summary>Puts recent history above what's arrived live so far (the
    /// "Joining" line stays on top), ending with the divider Chatty and
    /// Chatterino use. Lines that already arrived live aren't repeated.</summary>
    public void InsertHistory(IReadOnlyList<ChatMessage> history, string source)
    {
        var live = new HashSet<string>(Lines.Select(l => l.Message.Id).OfType<string>(), StringComparer.Ordinal);
        var at = Lines.Count > 0 && Lines[0].Message.Kind == ChatMessageKind.System && Lines[0].Message.Login is null ? 1 : 0;
        var inserted = 0;
        foreach (var message in history)
        {
            if (message.Id is { } id && live.Contains(id)) continue;
            if (message.WasDeleted && !_settings.ShowDeletedMessages) continue;
            var entry = NewEntry(message, own: false);
            entry.IsDeleted = message.WasDeleted;
            Lines.Insert(at + inserted++, entry);
        }
        if (inserted > 0)
        {
            var now = DateTimeOffset.Now;
            Lines.Insert(at + inserted, new ChatEntry(
                ChatMessage.System(Channel, $"### Finished with history logs ({source}). ###", now), TimeOf(now)));
        }
        while (Lines.Count > Math.Max(50, _settings.ChatScrollback))
            Lines.RemoveAt(0);
    }

    private string TimeOf(DateTimeOffset at) =>
        _settings.TimestampFormat.Length == 0 ? "" : at.ToString(_settings.TimestampFormat);

    private void LoadPronoun(ChatEntry entry)
    {
        if (_services is null || !_settings.ShowPronouns || entry.Message.Login is not { } login
            || entry.Message.Kind is not (ChatMessageKind.Message or ChatMessageKind.Action))
            return;
        if (_services.Pronouns.TryGetCached(login, out var cached))
        {
            entry.Pronoun = cached;
            return;
        }
        _ = Task.Run(async () =>
        {
            var pronoun = await _services.Pronouns.GetAsync(login).ConfigureAwait(false);
            if (pronoun is not null) _services.Ui.Post(() => entry.Pronoun = pronoun);
        });
    }

    // ----- Shared chat ------------------------------------------------------

    private readonly List<string> _sharedRooms = [];

    /// <summary>The channels sharing this chat, this one included, host
    /// first; empty outside a session.</summary>
    public IReadOnlyList<string> SharedRooms => _sharedRooms;

    /// <summary>For the strip over the chat: each channel's icon and name.</summary>
    public ObservableCollection<SharedChannel> SharedChannels { get; } = [];

    public bool IsShared => _sharedRooms.Count > 1;

    /// <summary>"Shared chat with A, B" (the others), for the strip.</summary>
    public string SharedSummary => SharedChannels.Count == 0 ? ""
        : "Shared chat with " + string.Join(", ", SharedChannels.Where(c => c.RoomId != RoomId).Select(c => c.Name));

    /// <summary>The session as Twitch reports it (Helix), or none: empty.</summary>
    public void SetSharedSession(IReadOnlyList<string> rooms)
    {
        if (rooms.Count > 0 && RoomId is { } me && !rooms.Contains(me)) rooms = [me, .. rooms];
        if (rooms.Count < 2) rooms = [];
        if (rooms.SequenceEqual(_sharedRooms)) return;
        _sharedRooms.Clear();
        _sharedRooms.AddRange(rooms);
        RefreshSharedChannels();
    }

    /// <summary>Without Helix (signed out) the session shows in the tags:
    /// every line carries its channel while there is one, and none after.</summary>
    private void ObserveShared(ChatMessage message)
    {
        if (message.Login is null || message.Kind is not (ChatMessageKind.Message or ChatMessageKind.Action or ChatMessageKind.System))
            return;
        if (message.SourceRoomId is not { } source)
        {
            if (_sharedRooms.Count > 0) SetSharedSession([]);
            return;
        }
        if (_sharedRooms.Contains(source) || (source == RoomId && _sharedRooms.Count > 0)) return;
        var rooms = new List<string>(_sharedRooms);
        if (rooms.Count == 0 && RoomId is { } me) rooms.Add(me);
        if (!rooms.Contains(source)) rooms.Add(source);
        if (rooms.Count < 2) return;
        _sharedRooms.Clear();
        _sharedRooms.AddRange(rooms);
        RefreshSharedChannels();
    }

    /// <summary>Names and icons for the strip, as far as known; called again
    /// when a channel's arrive.</summary>
    public void RefreshSharedChannels()
    {
        SharedChannels.Clear();
        if (_sharedRooms.Count > 1)
            foreach (var room in _sharedRooms)
            {
                var info = _services?.ChannelById(room);
                SharedChannels.Add(new SharedChannel(room, info?.DisplayName ?? (room == RoomId ? Channel : "…"), info?.ProfileImage));
            }
        OnPropertyChanged(nameof(IsShared));
        OnPropertyChanged(nameof(SharedSummary));
    }

    /// <summary>Re-tokenizes every line: emote sets or badges finished
    /// loading, or a provider or display setting was switched.</summary>
    public void RenderAll()
    {
        if (_services is null) return;
        foreach (var entry in Lines)
        {
            entry.ShowsDeletedText = _settings.ShowDeletedMessages;
            entry.Render(_services, RoomId);
            if (!_settings.ShowPronouns) entry.Pronoun = null;
            else if (entry.Pronoun is null) LoadPronoun(entry);
        }
    }

    /// <summary>CLEARMSG / CLEARCHAT: hide what a moderator removed and say
    /// what happened, as Twitch does.</summary>
    public void Apply(ChatModeration moderation)
    {
        switch (moderation.Kind)
        {
            case ChatModerationKind.DeleteMessage:
                // A deletion in another channel of a shared chat names the
                // message by its id there.
                foreach (var entry in Lines.Where(e => moderation.TargetMessageId is { } target
                             && (e.Message.Id == target || e.Message.SourceId == target)))
                    entry.IsDeleted = true;
                break;
            case ChatModerationKind.ClearUser:
            {
                // Shared chat: a ban elsewhere only takes away what the user
                // said there.
                var elsewhere = moderation.SourceRoomId is { } src && RoomId is not null && src != RoomId ? src : null;
                foreach (var entry in Lines.Where(e =>
                             (elsewhere is null || e.Message.SourceRoomId == elsewhere)
                             && ((moderation.TargetUserId is { } uid && e.Message.UserId == uid)
                                 || (moderation.TargetLogin is { } login && string.Equals(e.Message.Login, login, StringComparison.OrdinalIgnoreCase)))))
                    entry.IsDeleted = true;
                var who = moderation.TargetLogin ?? "A user";
                var where = elsewhere is null ? "" : " in " + (_services?.ChannelById(elsewhere)?.DisplayName ?? "another channel");
                Add(ChatMessage.System(Channel, moderation.Duration is { } d
                    ? $"{who} has been timed out{where} for {FormatDuration(d)}."
                    : $"{who} has been banned{where}.", moderation.Timestamp));
                break;
            }
            case ChatModerationKind.ClearChat:
                // Another channel's /clear doesn't clear this one.
                if (moderation.SourceRoomId is { } cleared && RoomId is not null && cleared != RoomId) break;
                Add(ChatMessage.System(Channel, "Chat was cleared by a moderator.", moderation.Timestamp));
                break;
        }
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds} seconds"
        : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} minutes"
        : d.TotalHours < 48 ? $"{(int)d.TotalHours} hours"
        : $"{(int)d.TotalDays} days";

    public void Clear() => Lines.Clear();
}

/// <summary>A channel in a shared chat session, for the strip over the chat.</summary>
public sealed record SharedChannel(string RoomId, string Name, Uri? ProfileImage)
{
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
}

/// <summary>One reward in the points menu, drawn like twitch.tv's tiles.</summary>
public sealed record RewardTile(ChannelReward Reward, int? Balance)
{
    public string Title => Reward.Title;
    public Uri? Image => Reward.Image;
    public string Cost => Reward.Cost.ToString("N0");
    public string Background => Reward.Color is { Length: > 0 } c && Avalonia.Media.Color.TryParse(c, out _) ? c : "#9147FF";

    /// <summary>The title's colour: black or white, whichever stands out
    /// more on the reward's own colour (white vanished on a white tile).</summary>
    public string Foreground => ReadsDark(Avalonia.Media.Color.Parse(Background)) ? "#0E0E10" : "#FFFFFF";

    /// <summary>Black text contrasts more than white (WCAG luminance).</summary>
    internal static bool ReadsDark(Avalonia.Media.Color c)
    {
        static double Linear(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        var l = 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        // Contrast with black (l + 0.05) / 0.05 against white's 1.05 / (l + 0.05).
        return (l + 0.05) * (l + 0.05) > 0.05 * 1.05;
    }

    /// <summary>Paused, sold out, or more than the user has: drawn dimmed.</summary>
    public double Opacity => Reward.IsAvailable && (Balance is not { } b || b >= Reward.Cost) ? 1 : 0.45;

    public string Tip => string.Join(Environment.NewLine, new[]
    {
        $"{Reward.Title} · {Reward.Cost:N0}",
        Reward.Prompt,
        Reward.IsAvailable ? null : "Not available right now",
        Balance is { } b && b < Reward.Cost ? $"{Reward.Cost - b:N0} more needed" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}
