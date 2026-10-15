// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StreamEdge.Chat;

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

    public ChatMessage Message { get; }
    public string Time { get; }

    /// <summary>The user's own line, drawn locally (Twitch doesn't echo it).</summary>
    public bool IsOwn { get; init; }

    [ObservableProperty]
    private IReadOnlyList<ChatToken> _tokens = [];

    [ObservableProperty]
    private IReadOnlyList<ChatServices.Badge> _badges = [];

    [ObservableProperty]
    private string? _pronoun;

    [ObservableProperty]
    private bool _isDeleted;

    /// <summary>The user moderates this channel: the line shows mod icons.</summary>
    [ObservableProperty]
    private bool _canModerate;

    /// <summary>Someone @-mentioned the signed-in user.</summary>
    public bool IsSelfMention => Tokens.Any(t => t.IsSelfMention);

    partial void OnTokensChanged(IReadOnlyList<ChatToken> value) => OnPropertyChanged(nameof(IsSelfMention));

    internal void Render(ChatServices services, string? roomId)
    {
        Tokens = services.Tokenize(Message, roomId, IsOwn);
        Badges = services.BadgesFor(Message, roomId);
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

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _draft = "";

    /// <summary>Sending needs a signed-in account; anonymous chat is read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private bool _canSend;

    public string Placeholder => CanSend ? "Send a message" : "Log in to chat";

    public string Header => Channel.Length == 0 ? "Chat" : "#" + Channel;

    partial void OnChannelChanged(string value) => OnPropertyChanged(nameof(Header));

    partial void OnRoomIdChanged(string? value) => RenderAll();

    /// <summary>A line the user just sent, drawn from what Twitch said about
    /// them (badges, colour) since the message itself never comes back.</summary>
    public void AddOwn(ChatMessage message) => Add(message, own: true);

    public void Add(ChatMessage message) => Add(message, own: false);

    private void Add(ChatMessage message, bool own)
    {
        // Official Twitch chat doesn't show joins and parts; they're opt-in.
        if (message.Kind == ChatMessageKind.Membership && !_settings.ShowJoinPart) return;

        var entry = new ChatEntry(message, TimeOf(message.Timestamp)) { IsOwn = own, CanModerate = IsModerator };
        if (_services is not null)
        {
            entry.Render(_services, RoomId);
            LoadPronoun(entry);
        }
        Lines.Add(entry);
        while (Lines.Count > Math.Max(50, _settings.ChatScrollback))
            Lines.RemoveAt(0);
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
            var entry = new ChatEntry(message, TimeOf(message.Timestamp)) { CanModerate = IsModerator };
            if (_services is not null)
            {
                entry.Render(_services, RoomId);
                LoadPronoun(entry);
            }
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

    /// <summary>Re-tokenizes every line: emote sets or badges finished
    /// loading, or a provider was switched on or off.</summary>
    public void RenderAll()
    {
        if (_services is null) return;
        foreach (var entry in Lines)
        {
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
                foreach (var entry in Lines.Where(e => e.Message.Id is { } id && id == moderation.TargetMessageId))
                    entry.IsDeleted = true;
                break;
            case ChatModerationKind.ClearUser:
                foreach (var entry in Lines.Where(e =>
                             (moderation.TargetUserId is { } uid && e.Message.UserId == uid)
                             || (moderation.TargetLogin is { } login && string.Equals(e.Message.Login, login, StringComparison.OrdinalIgnoreCase))))
                    entry.IsDeleted = true;
                var who = moderation.TargetLogin ?? "A user";
                Add(ChatMessage.System(Channel, moderation.Duration is { } d
                    ? $"{who} has been timed out for {FormatDuration(d)}."
                    : $"{who} has been banned.", moderation.Timestamp));
                break;
            case ChatModerationKind.ClearChat:
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
