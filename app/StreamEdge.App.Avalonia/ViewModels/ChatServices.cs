// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using StreamEdge.Streams;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>What every tab's chat shares: third-party emotes, Twitch and FFZ
/// badges, pronouns, the user's own Twitch emotes, settings, and who "me" is
/// for mention highlighting.</summary>
public sealed class ChatServices
{
    private const int MaxRecent = 24;

    private readonly object _gate = new();
    private Dictionary<string, UserEmote> _mine = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, EmoteOwner> _owners = new Dictionary<string, EmoteOwner>();

    private readonly HttpClient _http;

    public ChatServices(HttpClient http, AppSettings settings, IUiDispatcher ui, Func<string?> selfLogin)
    {
        _http = http;
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

    // ----- Channels by room id (shared chat) ---------------------------------

    private readonly Dictionary<string, ChannelInfo?> _channels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _channelsLoading = new(StringComparer.Ordinal);

    /// <summary>A channel's name and avatar became known (on the UI thread),
    /// by room id: lines from it and lists naming it redraw.</summary>
    public event Action<string>? ChannelLoaded;

    /// <summary>Off in demo mode and tests: channels are only what's remembered.</summary>
    public bool LookUpChannels { get; set; } = true;

    /// <summary>The channel with this room id, for shared chat's icons and
    /// names: null until known, and then <see cref="ChannelLoaded"/> fires.
    /// Looked up once (GQL, so signed out too). Call on the UI thread.</summary>
    public ChannelInfo? ChannelById(string roomId)
    {
        if (_channels.TryGetValue(roomId, out var known)) return known;
        if (!LookUpChannels || !_channelsLoading.Add(roomId)) return null;
        _ = Task.Run(async () =>
        {
            ChannelInfo? info = null;
            try
            {
                info = await ChannelInfo.FetchByIdAsync(_http, roomId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Lines keep a plain placeholder.
            }
            Ui.Post(() =>
            {
                _channelsLoading.Remove(roomId);
                if (info is null) return;
                Remember(roomId, info);
            });
        });
        return null;
    }

    /// <summary>A channel known from elsewhere (Helix, demo data).</summary>
    public void Remember(string roomId, ChannelInfo info)
    {
        _channels[roomId] = info;
        ChannelLoaded?.Invoke(roomId);
    }

    /// <summary>Raised after the user's own emotes change (on the UI thread).</summary>
    public event Action? MyEmotesChanged;

    /// <summary>The user's Twitch emotes (subs, follower emotes, globals).</summary>
    public IReadOnlyList<UserEmote> MyEmotes
    {
        get { lock (_gate) return [.. _mine.Values]; }
    }

    public IReadOnlyDictionary<string, EmoteOwner> Owners
    {
        get { lock (_gate) return _owners; }
    }

    /// <summary>Adds to (or, with <paramref name="replace"/>, replaces) the
    /// user's emotes. Call on the UI thread.</summary>
    public void SetMyEmotes(IEnumerable<UserEmote> emotes, IReadOnlyDictionary<string, EmoteOwner>? owners, bool replace)
    {
        lock (_gate)
        {
            var next = replace ? new Dictionary<string, UserEmote>(StringComparer.Ordinal) : new(_mine, StringComparer.Ordinal);
            foreach (var e in emotes) next[e.Emote.Code] = e;
            _mine = next;
            if (owners is not null)
            {
                var merged = replace ? new Dictionary<string, EmoteOwner>() : new Dictionary<string, EmoteOwner>(_owners);
                foreach (var (id, owner) in owners) merged[id] = owner;
                _owners = merged;
            }
        }
        MyEmotesChanged?.Invoke();
    }

    public Emote? MyEmote(string code)
    {
        lock (_gate) return _mine.TryGetValue(code, out var e) ? e.Emote : null;
    }

    /// <summary>An emote the user can send by typing <paramref name="code"/>
    /// in this room: their own Twitch emotes, then the channel's and global
    /// third-party ones. What the chat box draws as an image.</summary>
    public Emote? SendableEmote(string code, string? roomId) => MyEmote(code) ?? Emotes.Lookup(roomId, code);

    /// <summary>Suggestions for a word being typed in the chat box (see
    /// <see cref="EmoteCompletion.Rank"/>).</summary>
    public IReadOnlyList<Emote> CompleteEmote(string typed, string? roomId, bool contains, int max = 10) =>
        EmoteCompletion.Rank(
            MyEmotes.Select(m => m.Emote).Concat(Emotes.SetsFor(roomId).SelectMany(s => s.Emotes)),
            typed, [.. Settings.RecentEmotes.Select(r => r.Code)], contains, max);

    /// <summary>Remembers an emote picked from the menu.</summary>
    public void NoteUsed(Emote emote)
    {
        var recent = Settings.RecentEmotes;
        recent.RemoveAll(r => r.Code == emote.Code && r.Provider == emote.Provider);
        recent.Insert(0, new RecentEmote(emote.Code, emote.ImageUrl, emote.Provider));
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
    }

    // ----- Spell check ------------------------------------------------------

    private ChatSpelling? _spelling;

    /// <summary>The chat box's spell checker, once its dictionary has loaded
    /// and while spell check is on; null otherwise.</summary>
    public ChatSpelling? Spelling => Settings.SpellCheck ? _spelling : null;

    /// <summary>Spelling to draw again: the dictionary loaded, a word was
    /// added, or spell check was turned on or off (on the UI thread).</summary>
    public event Action? SpellingChanged;

    /// <summary>Reads the dictionary in the background.</summary>
    public async Task LoadSpellingAsync()
    {
        var spelling = await Task.Run(ChatSpelling.Load).ConfigureAwait(false);
        Ui.Post(() =>
        {
            spelling.SetOwnWords(Settings.SpellingWords);
            _spelling = spelling;
            SpellingChanged?.Invoke();
        });
    }

    /// <summary>The words the user added, A to Z, as settings lists them.
    /// Change them on the UI thread, through the methods below.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> SpellingWords =>
        _spellingWords ??= [.. Settings.SpellingWords.Order(StringComparer.OrdinalIgnoreCase)];
    private System.Collections.ObjectModel.ObservableCollection<string>? _spellingWords;

    /// <summary>"Add to dictionary": the word is fine from now on, here and
    /// in later runs.</summary>
    public void AddSpellingWord(string word)
    {
        if (!Settings.SpellingWords.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            Settings.SpellingWords.Add(word);
            var words = SpellingWords;
            var at = 0;
            while (at < words.Count && StringComparer.OrdinalIgnoreCase.Compare(words[at], word) < 0) at++;
            words.Insert(at, word);
        }
        _spelling?.AddOwnWord(word);
        Settings.Save();
        SpellingChanged?.Invoke();
    }

    /// <summary>Takes a word back out of the user's dictionary.</summary>
    public void RemoveSpellingWord(string word)
    {
        Settings.SpellingWords.RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        SpellingWords.Remove(word);
        _spelling?.SetOwnWords(Settings.SpellingWords);
        Settings.Save();
        SpellingChanged?.Invoke();
    }

    internal void RaiseSpellingChanged() => SpellingChanged?.Invoke();

    /// <summary>One badge to draw: its image when known, else the set name
    /// for a placeholder chip (Twitch's badge API needs a signed-in user).
    /// <paramref name="Detail"/> goes under its name in the tooltip.</summary>
    public readonly record struct Badge(string Set, BadgeImage? Image, string? Detail = null);

    /// <summary>Twitch badges in IRC order (FFZ's mod/VIP art replacing
    /// Twitch's when the channel has it), then the user's FFZ badges.</summary>
    public IReadOnlyList<Badge> BadgesFor(ChatMessage message, string? roomId)
    {
        var list = new List<Badge>(message.ShownBadges.Count + 1);
        foreach (var badge in message.ShownBadges)
        {
            var set = badge.Split('/')[0];
            var ffz = roomId is null ? null : set switch
            {
                "moderator" => Emotes.FfzModeratorBadge(roomId),
                "vip" => Emotes.FfzVipBadge(roomId),
                _ => null,
            };
            // As twitch.tv: how long, over the subscriber badge.
            var detail = set is "subscriber" or "founder" && message.SubscribedMonths is { } months
                ? $"Subscribed for {months} month{(months == 1 ? "" : "s")}"
                : null;
            list.Add(ffz is not null
                ? new Badge(set, new BadgeImage(ffz, set == "vip" ? "VIP" : "Moderator", set == "moderator" ? "#00AD03" : null))
                : new Badge(set, Badges.Resolve(roomId, badge), detail));
        }
        foreach (var ffz in Emotes.FfzBadgesFor(message.UserId))
            list.Add(new Badge("ffz", ffz));
        return list;
    }

    /// <param name="own">The user's own line: Twitch never sends it back with
    /// emote tags, so their Twitch emotes are matched by code. Not for other
    /// people's lines, which may contain codes they can't use.</param>
    public IReadOnlyList<ChatToken> Tokenize(ChatMessage message, string? roomId, bool own = false) =>
        ChatTokenizer.Tokenize(message,
            own ? code => MyEmote(code) ?? Emotes.Lookup(roomId, code) : code => Emotes.Lookup(roomId, code),
            SelfLogin(), Settings.ShowGifs, CheermotesFor(message.SourceRoomId) ?? CheermotesFor(roomId));

    // ----- Cheermotes, by room id ------------------------------------------

    private readonly Dictionary<string, IReadOnlyList<Cheermote>> _cheermotes = new(StringComparer.Ordinal);

    public void SetCheermotes(string roomId, IReadOnlyList<Cheermote> cheermotes)
    {
        lock (_gate) _cheermotes[roomId] = cheermotes;
    }

    private IReadOnlyList<Cheermote>? CheermotesFor(string? roomId)
    {
        if (roomId is null) return null;
        lock (_gate) return _cheermotes.GetValueOrDefault(roomId);
    }
}
