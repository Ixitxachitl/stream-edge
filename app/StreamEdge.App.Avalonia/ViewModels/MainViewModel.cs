// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Auth;
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using StreamEdge.Demo;
using StreamEdge.Playback;
using StreamEdge.Streams;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan StreamRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PointsRefresh = TimeSpan.FromMinutes(5);
    // Catch-up decisions need fresh buffer readings; 4 Hz is plenty.
    private static readonly TimeSpan PlaybackTick = TimeSpan.FromMilliseconds(250);

    private readonly AppSettings _settings;
    private readonly StartupOptions _options;
    private readonly IUiDispatcher _ui;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer _clock;
    private readonly Timer _playbackClock;
    // Shown in the chat and player panes while no tab is open.
    private readonly ChatViewModel _idleChat;
    private readonly PlayerViewModel _idlePlayer = new();
    private TwitchChatClient? _chat;
    private DateTimeOffset _lastStreamRefresh = DateTimeOffset.MinValue;

    public MainViewModel(AppSettings settings, StartupOptions options, IUiDispatcher ui)
    {
        _settings = settings;
        // A demo session shows the user's layout but must not overwrite it.
        if (options.Demo) settings.ReadOnly = true;
        _options = options;
        _ui = ui;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamEdge/0.1");
        _idleChat = new ChatViewModel(settings);
        Streams = new StreamListViewModel(settings);
        if (!options.Demo && !Playback.Mpv.MpvPlayer.IsAvailable(out var why)) PlayerUnavailable = why;
        Account = new AccountViewModel(settings, _http);
        Services = new ChatServices(_http, settings, ui, () => Account.IsSignedIn ? Account.Login : null);
        // Emotes and badges load in the background; lines already drawn are
        // redrawn when they arrive (null room = globals: every tab).
        Services.Emotes.Changed += room => _ui.Post(() => RenderChats(room));
        Services.Badges.Changed += room => _ui.Post(() => RenderChats(room));
        // Own lines drawn before the user's emotes arrived get them now.
        Services.MyEmotesChanged += () => RenderChats(null);
        EmoteMenu = new EmoteMenuViewModel(Services, () => ActiveTab, Account);
        ModMenu = new ModMenuViewModel(() => ActiveTab, Moderation);
        Columns.CollectionChanged += (_, _) => OnSplitsChanged();
        Activate(AddColumn(0));
        Account.AccountChanged += () => _ = OnAccountChangedAsync();
        Streams.OpenRequested += (login, mode) => OpenChannel(login, mode);

        if (options.Demo) LoadDemo();
        else _ = StartAsync();

        _clock = new Timer(_ => _ui.Post(Tick), null, ClockTick, ClockTick);
        _playbackClock = new Timer(_ => _ui.Post(TickPlayback), null, PlaybackTick, PlaybackTick);
    }

    public StreamListViewModel Streams { get; }
    public ChatServices Services { get; }
    public EmoteMenuViewModel EmoteMenu { get; }
    public ModMenuViewModel ModMenu { get; }
    public AccountViewModel Account { get; }

    /// <summary>Every open channel, in the order opened. Each is in exactly
    /// one of <see cref="Columns"/>, which hold the order shown.</summary>
    public ObservableCollection<ChannelTabViewModel> Tabs { get; } = [];

    /// <summary>Chat splits, left to right and top to bottom; always at
    /// least one. A split with <see cref="ChatColumnViewModel.StacksBelow"/>
    /// sits under the one before it, so a run of those is one column.</summary>
    public ObservableCollection<ChatColumnViewModel> Columns { get; } = [];

    /// <summary>Most columns side by side.</summary>
    public const int MaxColumns = 4;

    /// <summary>Most chats stacked in one column.</summary>
    public const int MaxStack = 4;

    /// <summary>Most chats in all.</summary>
    public const int MaxSplits = 8;

    /// <summary>Chats were stacked or unstacked: the chat pane re-lays them out.</summary>
    public event Action? SplitLayoutChanged;

    /// <summary>The chats in <paramref name="split"/>'s column, top to bottom.</summary>
    public IReadOnlyList<ChatColumnViewModel> StackOf(ChatColumnViewModel split)
    {
        var at = Columns.IndexOf(split);
        if (at < 0) return [];
        var head = at;
        while (head > 0 && Columns[head].StacksBelow) head--;
        var end = head + 1;
        while (end < Columns.Count && Columns[end].StacksBelow) end++;
        return [.. Columns.Skip(head).Take(end - head)];
    }

    /// <summary>Columns side by side: chats that don't stack under another.</summary>
    public int VisibleColumnCount => Columns.Count(c => !c.StacksBelow);

    private void OnSplitsChanged()
    {
        // The first chat can't stack under anything.
        if (Columns.Count > 0) Columns[0].StacksBelow = false;
        // The PIP switch goes in the top right chat's tab strip.
        var lastHead = Columns.LastOrDefault(c => !c.StacksBelow);
        foreach (var c in Columns) c.IsLast = c == lastHead;
        SplitLayoutChanged?.Invoke();
    }

    /// <summary>Before <paramref name="split"/> leaves its place: if it heads a
    /// column, the chat under it heads the column now.</summary>
    private void Unstack(ChatColumnViewModel split)
    {
        var at = Columns.IndexOf(split);
        if (at >= 0 && !split.StacksBelow && at + 1 < Columns.Count && Columns[at + 1].StacksBelow)
            Columns[at + 1].StacksBelow = false;
    }

    /// <summary>The tab on video (full size); the rest are PIP or audio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Player), nameof(HasChannel))]
    private ChannelTabViewModel? _frontTab;

    /// <summary>The column last clicked or typed in: where new channels open
    /// and what the chat box, emote and mod menus act on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chat), nameof(ActiveTab))]
    private ChatColumnViewModel? _activeColumn;

    /// <summary>The active column's selected tab.</summary>
    public ChannelTabViewModel? ActiveTab => ActiveColumn?.Selected;

    /// <summary>The active column's chat (an empty one with no tabs).</summary>
    public ChatViewModel Chat => ActiveTab?.Chat ?? _idleChat;

    public PlayerViewModel Player => FrontTab?.Player ?? _idlePlayer;

    public bool HasChannel => FrontTab is not null;

    public bool HasTabs => Tabs.Count > 0;

    /// <summary>False in demo mode: no players are created at all.</summary>
    public bool PlaybackEnabled => !_options.Demo && PlayerUnavailable is null;

    public double ChatShare
    {
        get => _settings.ChatShare;
        set => SetProperty(_settings.ChatShare, Math.Clamp(value, 0, 1), _settings, (s, v) => s.ChatShare = v);
    }

    /// <summary>Swaps the side panes: chat left, list right.</summary>
    public bool ChatOnLeft
    {
        get => _settings.ChatOnLeft;
        set => SetProperty(_settings.ChatOnLeft, value, _settings, (s, v) => s.ChatOnLeft = v);
    }

    /// <summary>The 4:3 layout: video above the stream list, chat beside them.</summary>
    public bool VideoAboveList
    {
        get => _settings.VideoAboveList;
        set
        {
            if (SetProperty(_settings.VideoAboveList, value, _settings, (s, v) => s.VideoAboveList = v))
                OnPropertyChanged(nameof(MinWindowWidth));
        }
    }

    /// <summary>Three across needs room for 16:9 between two minimum-width
    /// sides; stacked fits an 800×600 screen.</summary>
    public double MinWindowWidth => VideoAboveList ? 640 : 960;

    public bool ReopenLastChannel
    {
        get => _settings.ReopenLastChannel;
        set => SetProperty(_settings.ReopenLastChannel, value, _settings, (s, v) => s.ReopenLastChannel = v);
    }

    /// <summary>Background tabs as small live videos over the player; off,
    /// they play audio only.</summary>
    public bool ShowPip
    {
        get => _settings.ShowPip;
        set
        {
            if (SetProperty(_settings.ShowPip, value, _settings, (s, v) => s.ShowPip = v)) ApplyTiers();
        }
    }

    [RelayCommand]
    private void TogglePip() => ShowPip = !ShowPip;

    public int? ScreenIndex => _options.ScreenIndex ?? _settings.ScreenIndex;

    /// <summary>A --screen on the command line beats the remembered placement.</summary>
    public bool HasExplicitScreen => _options.ScreenIndex is not null;

    /// <summary>The window's remembered placement; the window keeps it current.</summary>
    public WindowPlacement? SavedWindow
    {
        get => _settings.Window;
        set => _settings.Window = value;
    }
    public bool Fullscreen => _options.Fullscreen || _settings.Fullscreen;

    /// <summary>"#channel [viewers|uptime] - title", the Chatty-style window
    /// title (also what shows in the taskbar of the main monitor).</summary>
    [ObservableProperty]
    private string _windowTitle = "StreamEdge";

    private void LoadDemo()
    {
        var now = DateTimeOffset.Now;
        var sample = SampleData.Streams(now);
        foreach (var s in sample.Where(s => s.IsFavorite && !_settings.IsFavorite(s.Login)))
            _settings.ToggleFavorite(s.Login);
        Streams.Set(sample, now);
        var tab = OpenChannel(SampleData.Channel, OpenMode.NewTab)!;
        tab.Chat.Clear();
        foreach (var line in SampleData.Chat(now)) tab.Chat.Add(line);
        tab.Chat.Roster.AddNames(SampleData.Lurkers);
        tab.Chat.Points = SampleData.Points();
        tab.Chat.Status = "Demo data";
        tab.Player.Status = "Demo mode: no playback.";
    }

    private async Task StartAsync()
    {
        await Account.RestoreAsync(_stop.Token);
        _ = Services.Emotes.LoadGlobalsAsync(_stop.Token);
        _ = LoadTwitchBadgesAsync();
        _ = LoadMyEmotesAsync();
        _chat = CreateChatClient();
        if (_options.Channel is { Length: > 0 } wanted)
        {
            // --channel always wins, alone.
            OpenChannel(wanted, OpenMode.NewTab);
        }
        else if (_settings.ReopenLastChannel)
        {
            // Every column's tabs in their saved order, then the saved front
            // one forward (an already-open channel is only brought forward).
            var front = _settings.Channel;
            var columns = _settings.ChatColumns.Count > 0
                ? _settings.ChatColumns.ToList()
                : [new ChatColumnState { Tabs = [.. _settings.OpenTabs], Selected = front }];
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Tabs.Count == 0) continue;
                var column = Columns[^1].Tabs.Count == 0 ? Columns[^1] : AddColumn(Columns.Count);
                column.StacksBelow = columns[i].Below && Columns.Count > 1;
                Activate(column);
                foreach (var login in columns[i].Tabs.ToList()) OpenChannel(login, OpenMode.Background);
                if (TabFor(columns[i].Selected ?? "") is { } selected && column.Tabs.Contains(selected))
                    column.Selected = selected;
            }
            if (TabFor(front ?? "") is { } frontTab) SelectTab(frontTab);
            else if (front is { Length: > 0 }) OpenChannel(front, OpenMode.NewTab);
        }
        await RefreshStreamsAsync();
    }

    private TwitchChatClient CreateChatClient()
    {
        var client = new TwitchChatClient
        {
            Token = Account.IsSignedIn ? Account.AccessToken : null,
            Login = Account.IsSignedIn ? Account.Login : null,
        };
        client.MessageReceived += m => _ui.Post(() => TabFor(m.Channel)?.Chat.Add(m));
        client.ModerationReceived += m => _ui.Post(() => TabFor(m.Channel)?.Chat.Apply(m));
        client.NamesReceived += (channel, names) => _ui.Post(() => TabFor(channel)?.Chat.Roster.AddNames(names));
        client.RoomStateReceived += (channel, roomId) => _ui.Post(() => OnRoomState(channel, roomId));
        client.UserStateReceived += state => _ui.Post(() => OnUserState(state));
        client.StatusChanged += s => _ui.Post(() =>
        {
            foreach (var tab in Tabs) tab.Chat.Status = s;
            _idleChat.Status = s;
        });
        var canSend = client.Token is not null;
        foreach (var tab in Tabs) tab.Chat.CanSend = canSend;
        client.Start();
        return client;
    }

    private ChannelTabViewModel? TabFor(string login) =>
        Tabs.FirstOrDefault(t => string.Equals(t.Login, login, StringComparison.OrdinalIgnoreCase));

    private async Task OnAccountChangedAsync()
    {
        // Reconnect chat under the new identity, keeping every open channel.
        if (_chat is not null) await _chat.DisposeAsync();
        _chat = CreateChatClient();
        foreach (var tab in Tabs) await _chat.JoinAsync(tab.Login);
        await LoadTwitchBadgesAsync();
        RenderChats(null);
        await LoadMyEmotesAsync();
        await RefreshStreamsAsync();
    }

    // ----- Tabs -----------------------------------------------------------

    /// <summary>Opens a channel. An already-open channel is just brought
    /// forward (or left where it is, for background).</summary>
    public ChannelTabViewModel? OpenChannel(string login, OpenMode mode)
    {
        login = login.Trim().TrimStart('#').ToLowerInvariant();
        if (login.Length == 0) return null;

        if (TabFor(login) is { } existing)
        {
            if (mode != OpenMode.Background) SelectTab(existing);
            return existing;
        }

        var column = ActiveColumn ?? Columns[0];
        var index = column.Tabs.Count;
        if (mode == OpenMode.Front && column.Selected is { } replaced)
        {
            // Double-click swaps the channel in the active column's tab, in place.
            index = column.Tabs.IndexOf(replaced);
            CloseTab(replaced, pickNewFront: false);
        }

        var tab = new ChannelTabViewModel(login, _settings, Services, SelectTab, t => CloseTab(t))
        {
            IsFront = false,
        };
        tab.Chat.CanSend = _chat?.Token is not null;
        tab.Chat.Status = _chat is null ? "" : "Connected";
        tab.Chat.Add(ChatMessage.System(login, $"### Joining #{login} ###", DateTimeOffset.Now));
        if (PlayerUnavailable is { } why) tab.Player.Status = why;
        Tabs.Add(tab);
        column.Tabs.Insert(Math.Min(index, column.Tabs.Count), tab);
        if (mode != OpenMode.Background || column.Selected is null) column.Selected = tab;
        _ = _chat?.JoinAsync(login);
        ShowChannelInfo(tab);
        if (!_options.Demo)
        {
            _ = LoadChannelLookAsync(tab);
            _ = LoadHistoryAsync(tab);
            _ = LoadPointsAsync(tab);
        }

        if (mode != OpenMode.Background || FrontTab is null) SetFront(tab);
        else ApplyTiers();
        SaveTabs();
        OnPropertyChanged(nameof(HasTabs));
        return tab;
    }

    /// <summary>A tab clicked: shown in its column, which becomes active, and
    /// put on video.</summary>
    private void SelectTab(ChannelTabViewModel tab)
    {
        if (ColumnOf(tab) is not { } column) return;
        column.Selected = tab;
        Activate(column);
        SetFront(tab);
    }

    /// <summary>The chat box, emote and mod menus now act on this column.</summary>
    public void Activate(ChatColumnViewModel column)
    {
        if (!Columns.Contains(column)) return;
        ActiveColumn = column;
        foreach (var c in Columns) c.IsActive = c == column;
    }

    public ChatColumnViewModel? ColumnOf(ChannelTabViewModel tab) => Columns.FirstOrDefault(c => c.Tabs.Contains(tab));

    private ChatColumnViewModel AddColumn(int at)
    {
        var column = NewColumn();
        Columns.Insert(Math.Clamp(at, 0, Columns.Count), column);
        return column;
    }

    private ChatColumnViewModel NewColumn()
    {
        var column = new ChatColumnViewModel(this, _idleChat);
        // The active column's selection is what the chat box acts on.
        column.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatColumnViewModel.Selected) && column == ActiveColumn)
            {
                OnPropertyChanged(nameof(ActiveTab));
                OnPropertyChanged(nameof(Chat));
            }
            else if (e.PropertyName == nameof(ChatColumnViewModel.StacksBelow) && Columns.Contains(column))
            {
                OnSplitsChanged();
            }
        };
        return column;
    }

    /// <summary>Drag and drop within or between tab strips: the tab moves to
    /// <paramref name="index"/> in <paramref name="target"/> (counted before
    /// the move) and is shown there. What's on video doesn't change.</summary>
    public void MoveTab(ChannelTabViewModel tab, ChatColumnViewModel target, int index)
    {
        if (ColumnOf(tab) is not { } source || !Columns.Contains(target)) return;
        if (source == target)
        {
            var from = source.Tabs.IndexOf(tab);
            // Dropping after itself: the gap it leaves shifts the rest left.
            if (index > from) index--;
            index = Math.Clamp(index, 0, source.Tabs.Count - 1);
            if (index != from) source.Tabs.Move(from, index);
        }
        else
        {
            TakeOut(tab, source);
            target.Tabs.Insert(Math.Clamp(index, 0, target.Tabs.Count), tab);
            DropEmptyColumn(source);
        }
        target.Selected = tab;
        Activate(target);
        UpdateColumnFlags();
        SaveTabs();
    }

    /// <summary>Whether dropping on a column edge, making a new column at
    /// <paramref name="at"/>, would do anything: a tab alone in its column
    /// moves that column (not beside itself); any other needs a free slot.</summary>
    public bool CanSplit(ChannelTabViewModel tab, int at)
    {
        if (ColumnOf(tab) is not { } source) return false;
        var from = Columns.IndexOf(source);
        var lone = source.Tabs.Count == 1;
        // A lone tab in a column of its own just moves that column.
        if (lone && StackOf(source).Count == 1) return at != from && at != from + 1;
        return VisibleColumnCount < MaxColumns && (lone || Columns.Count < MaxSplits);
    }

    /// <summary>Drop on a column's edge: the tab becomes a new column there
    /// (<paramref name="at"/> counts columns as they are before the move).
    /// A tab alone in its column takes the column with it.</summary>
    public void MoveTabToNewColumn(ChannelTabViewModel tab, int at)
    {
        if (!CanSplit(tab, at) || ColumnOf(tab) is not { } source) return;
        if (source.Tabs.Count == 1)
        {
            var from = Columns.IndexOf(source);
            Unstack(source);
            source.StacksBelow = false;
            Columns.Move(from, at > from ? at - 1 : at);
            Activate(source);
        }
        else
        {
            var column = AddColumn(at);
            TakeOut(tab, source);
            column.Tabs.Add(tab);
            column.Selected = tab;
            Activate(column);
        }
        UpdateColumnFlags();
        SaveTabs();
    }

    /// <summary>Whether dropping on a chat's top or bottom edge, stacking a
    /// new chat above or below <paramref name="anchor"/>, would do anything.</summary>
    public bool CanStack(ChannelTabViewModel tab, ChatColumnViewModel anchor, bool below)
    {
        if (ColumnOf(tab) is not { } source || !Columns.Contains(anchor)) return false;
        var lone = source.Tabs.Count == 1;
        var stack = StackOf(anchor);
        if (lone)
        {
            if (source == anchor) return false;
            // Already right there.
            var (s, a) = (Columns.IndexOf(source), Columns.IndexOf(anchor));
            if (stack.Contains(source) && (below ? s == a + 1 : s == a - 1)) return false;
        }
        var adds = lone && stack.Contains(source) ? 0 : 1;
        return stack.Count + adds <= MaxStack && (lone || Columns.Count < MaxSplits);
    }

    /// <summary>Drop on a chat's top or bottom edge: the tab becomes a new
    /// chat stacked above or below <paramref name="anchor"/>, in its column.
    /// A tab alone in its chat takes the chat with it.</summary>
    public void MoveTabToStack(ChannelTabViewModel tab, ChatColumnViewModel anchor, bool below)
    {
        if (!CanStack(tab, anchor, below) || ColumnOf(tab) is not { } source) return;
        ChatColumnViewModel split;
        if (source.Tabs.Count == 1)
        {
            Unstack(source);
            Columns.Remove(source);
            split = source;
        }
        else
        {
            split = NewColumn();
            TakeOut(tab, source);
            split.Tabs.Add(tab);
            split.Selected = tab;
        }
        var at = Columns.IndexOf(anchor);
        if (below)
        {
            split.StacksBelow = true;
            Columns.Insert(at + 1, split);
        }
        else
        {
            // In first, then the anchor stacks under it: the other way round,
            // a first-place anchor would be unstacked again on the spot.
            split.StacksBelow = anchor.StacksBelow;
            Columns.Insert(at, split);
            anchor.StacksBelow = true;
        }
        Activate(split);
        UpdateColumnFlags();
        SaveTabs();
    }

    /// <summary>Removes a tab from its column, selecting a neighbour there.</summary>
    private static void TakeOut(ChannelTabViewModel tab, ChatColumnViewModel column)
    {
        var index = column.Tabs.IndexOf(tab);
        column.Tabs.RemoveAt(index);
        if (column.Selected == tab)
            column.Selected = column.Tabs.Count == 0 ? null : column.Tabs[Math.Min(index, column.Tabs.Count - 1)];
        tab.IsSelected = false;
    }

    /// <summary>An emptied column goes, unless it's the last one.</summary>
    private void DropEmptyColumn(ChatColumnViewModel column)
    {
        if (column.Tabs.Count > 0 || Columns.Count <= 1) return;
        var index = Columns.IndexOf(column);
        Unstack(column);
        Columns.Remove(column);
        if (ActiveColumn == column) Activate(Columns[Math.Min(index, Columns.Count - 1)]);
    }

    private void UpdateColumnFlags()
    {
        foreach (var c in Columns) c.HasFront = FrontTab is not null && c.Tabs.Contains(FrontTab);
    }

    private void SetFront(ChannelTabViewModel? tab)
    {
        FrontTab = tab;
        // The CC choice is one setting; each tab's button shows it when in front.
        if (tab is not null) tab.Player.Captions = _settings.Captions;
        foreach (var t in Tabs) t.IsFront = t == tab;
        UpdateColumnFlags();
        ApplyTiers();
        if (tab is not null) ShowChannelInfo(tab);
        else WindowTitle = "StreamEdge";
        _settings.Channel = tab?.Login;
    }

    private void CloseTab(ChannelTabViewModel tab, bool pickNewFront = true)
    {
        var column = ColumnOf(tab);
        if (!Tabs.Remove(tab)) return;
        if (column is not null) TakeOut(tab, column);
        _ = _chat?.PartAsync(tab.Login);
        if (tab.RoomId is { } room && Tabs.All(t => t.RoomId != room))
        {
            Services.Emotes.UnloadChannel(room);
            Services.Badges.UnloadChannel(room);
        }
        _ = tab.DisposeAsync().AsTask();
        // Replacing in place keeps the column for the tab that's coming.
        if (pickNewFront && column is not null) DropEmptyColumn(column);
        if (FrontTab == tab)
        {
            FrontTab = null;
            // Video goes to what that column now shows, else the active one's.
            if (pickNewFront)
                SetFront(column is { Selected: { } next } && Columns.Contains(column) ? next
                    : ActiveTab ?? Columns.Select(c => c.Selected).FirstOrDefault(t => t is not null));
        }
        UpdateColumnFlags();
        SaveTabs();
        OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>Closes the active column's tab (the ✕ in its chat header).</summary>
    [RelayCommand]
    private void LeaveChannel()
    {
        if (ActiveTab is { } tab) CloseTab(tab);
    }

    private void SaveTabs()
    {
        _settings.OpenTabs = [.. Columns.SelectMany(c => c.Tabs).Select(t => t.Login)];
        _settings.ChatColumns = [.. Columns.Where(c => c.Tabs.Count > 0).Select(c => new ChatColumnState
        {
            Tabs = [.. c.Tabs.Select(t => t.Login)],
            Selected = c.Selected?.Login,
            Below = c.StacksBelow,
        })];
        _settings.Channel = FrontTab?.Login;
    }

    /// <summary>Front: full quality. Others: a thumbnail with PIP on, audio
    /// only with it off. Background tabs keep playing either way.</summary>
    private void ApplyTiers()
    {
        foreach (var tab in Tabs)
        {
            if (tab.Session is { } session)
                session.Tier = tab.IsFront ? PlaybackTier.Full : ShowPip ? PlaybackTier.Thumbnail : PlaybackTier.AudioOnly;
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (ActiveTab is not { } tab) return;
        var text = tab.Chat.Draft.Trim();
        if (text.Length == 0 || _chat is null || !tab.Chat.CanSend) return;
        tab.Chat.Draft = "";
        await _chat.SendMessageAsync(tab.Login, text);

        // Twitch doesn't echo our own PRIVMSG back, so draw it here, the way
        // Twitch would: from the USERSTATE it sent for this channel (badges,
        // colour, display name). Other /commands aren't chat lines.
        var kind = ChatMessageKind.Message;
        if (text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
        {
            kind = ChatMessageKind.Action;
            text = text[4..];
        }
        else if (text.StartsWith('/'))
        {
            return;
        }
        var state = _userStates.GetValueOrDefault(tab.Login);
        tab.Chat.AddOwn(new ChatMessage(DateTimeOffset.Now, kind, tab.Login, Account.Login,
            state?.DisplayName ?? _globalUserState?.DisplayName ?? Account.Login,
            state?.Color ?? _globalUserState?.Color, state?.Badges ?? [], text)
        {
            UserId = _globalUserState?.UserId ?? Account.UserId,
        });
    }

    private readonly Dictionary<string, ChatUserState> _userStates = new(StringComparer.OrdinalIgnoreCase);
    private ChatUserState? _globalUserState;

    private void OnUserState(ChatUserState state)
    {
        if (state.Channel is { } channel)
        {
            _userStates[channel] = state;
            // Moderator or broadcaster here: mod icons on lines, the mod menu.
            if (TabFor(channel) is { } tab)
                tab.Chat.IsModerator = state.Badges.Any(b => b.StartsWith("moderator/", StringComparison.Ordinal)
                                                            || b.StartsWith("broadcaster/", StringComparison.Ordinal));
        }
        else
        {
            _globalUserState = state;
        }
    }

    /// <summary>The card for a name clicked in the front tab's chat; starts
    /// loading the profile (and, for mods, the follow date) in the background.</summary>
    public UserCardViewModel? OpenUserCard(string login, ChatEntry? clicked)
    {
        // The tab the name was clicked in: its line says which channel.
        var tab = (clicked is null ? null : TabFor(clicked.Message.Channel)) ?? ActiveTab;
        if (tab is null) return null;
        var card = new UserCardViewModel(tab, login, clicked, Moderation, l => OpenChannel(l, OpenMode.NewTab));
        if (!_options.Demo)
            _ = card.LoadAsync(_http, _settings.EffectiveClientId,
                Account.IsSignedIn && Account.CanModerate ? Account.AccessToken : null, _stop.Token);
        return card;
    }

    /// <summary>Opens "Users in chat" over a column, for the channel it shows.
    /// The complete list needs the user to moderate there and a login with
    /// the chatters scope; otherwise it's who chat has shown.</summary>
    public void ShowChatters(ChatColumnViewModel column)
    {
        if (column.Selected is not { } tab) return;
        Func<CancellationToken, Task<IReadOnlyList<Chatter>?>>? fetchAll = null;
        string note;
        if (_options.Demo)
            note = "Demo data.";
        else if (!tab.Chat.IsModerator)
            note = "Twitch only gives moderators the full list, so this is everyone who has chatted, plus lurkers in smaller channels.";
        else if (!Account.CanReadChatters || Account.AccessToken is null || Account.UserId is null || tab.RoomId is null)
            note = "Log in again (⚙) to see everyone: StreamEdge now asks for permission to read the viewer list.";
        else
        {
            note = "Only people chat has shown.";
            var (token, me, room) = (Account.AccessToken, Account.UserId, tab.RoomId);
            fetchAll = async ct =>
                await ChatterRoster.FetchAsync(_http, _settings.EffectiveClientId, token, room, me, ct) is { } all
                    ? tab.Chat.Roster.Merge(all)
                    : null;
        }
        column.Chatters = new ChatterListViewModel(tab.Chat, fetchAll, note, () => column.Chatters = null);
        _ = column.Chatters.RefreshAsync();
    }

    // ----- Moderation -----------------------------------------------------

    /// <summary>Acting as the signed-in user, or null with why not.</summary>
    private (TwitchModeration? Mod, string? Why) Moderation()
    {
        if (!Account.IsSignedIn || Account.AccessToken is not { } token || Account.UserId is not { } me)
            return (null, "Log in to moderate.");
        if (!Account.CanModerate)
            return (null, "Log in again (⚙) to moderate: StreamEdge now asks for moderator permissions.");
        return (new TwitchModeration(_http, _settings.EffectiveClientId, token, me), null);
    }

    /// <summary>The icons on a chat line: delete it, time its sender out for
    /// ten minutes, or ban them. Problems are said in that tab's chat.</summary>
    public async Task ModerateAsync(ChannelTabViewModel tab, ChatEntry entry, Views.ModAction action)
    {
        var (mod, why) = Moderation();
        var room = tab.RoomId;
        var m = entry.Message;
        if (mod is null || room is null || m.UserId is null && action != Views.ModAction.Delete)
        {
            tab.Chat.Add(ChatMessage.System(tab.Login, why ?? "Can't moderate this line yet.", DateTimeOffset.Now));
            return;
        }
        var result = action switch
        {
            Views.ModAction.Delete when m.Id is { } id => await mod.DeleteMessageAsync(room, id, _stop.Token),
            Views.ModAction.Timeout => await mod.BanAsync(room, m.UserId!, 600, null, _stop.Token),
            Views.ModAction.Ban => await mod.BanAsync(room, m.UserId!, null, null, _stop.Token),
            _ => new ModResult(false, "Nothing to do."),
        };
        if (!result.Ok) tab.Chat.Add(ChatMessage.System(tab.Login, "Twitch refused: " + result.Error, DateTimeOffset.Now));
    }

    /// <summary>The user's Twitch emotes, for the emote menu and their own
    /// lines. Needs the emotes permission, which logins from before it was
    /// added don't have.</summary>
    private async Task LoadMyEmotesAsync(string? roomId = null)
    {
        if (!Account.IsSignedIn || !Account.CanUseOwnEmotes || Account.UserId is not { } userId
            || Account.AccessToken is not { } token) return;
        try
        {
            var clientId = _settings.EffectiveClientId;
            var emotes = await TwitchUserEmotes.FetchAsync(_http, clientId, token, userId, roomId, _stop.Token);
            var owners = await TwitchUserEmotes.FetchOwnersAsync(_http, clientId, token,
                emotes.Select(e => e.OwnerId).Where(id => !Services.Owners.ContainsKey(id)), _stop.Token);
            _ui.Post(() => Services.SetMyEmotes(emotes, owners, replace: roomId is null));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine("user emotes: " + ex.Message);
        }
    }

    /// <summary>Recent chat for a tab just opened: Twitch's own history with
    /// the user's auth-token, else (if allowed) recent-messages.robotty.de.</summary>
    private async Task LoadHistoryAsync(ChannelTabViewModel tab)
    {
        if (!_settings.LoadChatHistory) return;
        try
        {
            IReadOnlyList<ChatMessage>? history = null;
            var source = "Twitch";
            if (SecretStore.Get(SecretStore.PlaybackAuthToken) is { } authToken)
                history = await ChatHistory.FetchTwitchAsync(_http, tab.Login, authToken, _stop.Token);
            if (history is null && _settings.UseRecentMessages)
            {
                history = await ChatHistory.FetchRecentMessagesAsync(_http, tab.Login, 200, _stop.Token);
                source = "recent-messages";
            }
            if (history is { Count: > 0 } lines)
                _ui.Post(() =>
                {
                    if (Tabs.Contains(tab)) tab.Chat.InsertHistory(lines, source);
                });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // History is a nicety; the chat just starts with live messages.
        }
    }

    public bool LoadChatHistory
    {
        get => _settings.LoadChatHistory;
        set => SetProperty(_settings.LoadChatHistory, value, _settings, (s, v) => s.LoadChatHistory = v);
    }

    public bool UseRecentMessages
    {
        get => _settings.UseRecentMessages;
        set => SetProperty(_settings.UseRecentMessages, value, _settings, (s, v) => s.UseRecentMessages = v);
    }

    /// <summary>Name, offline banner and avatar, for the player pane when
    /// the channel isn't live. Signed out works too (GQL).</summary>
    private async Task LoadChannelLookAsync(ChannelTabViewModel tab)
    {
        try
        {
            if (await ChannelInfo.FetchAsync(_http, tab.Login, _stop.Token) is not { } info) return;
            _ui.Post(() =>
            {
                tab.DisplayName = info.DisplayName;
                tab.Player.OfflineImage = info.OfflineImage;
                tab.Player.ProfileImage = info.ProfileImage;
                ShowChannelInfo(tab);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Cosmetic: without it the pane just says "Offline".
        }
    }

    /// <summary>The channel's points name, icon and rewards, and with the
    /// auth-token the user's balance. Signed out works for all but the balance.</summary>
    internal async Task LoadPointsAsync(ChannelTabViewModel tab)
    {
        tab.PointsLoadedAt = DateTimeOffset.Now;
        try
        {
            var points = await ChannelPoints.FetchAsync(_http, tab.Login,
                SecretStore.Get(SecretStore.PlaybackAuthToken), _stop.Token);
            if (points is null) return;
            _ui.Post(() =>
            {
                if (Tabs.Contains(tab)) tab.Chat.Points = points;
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Points are extra; chat works without them. Retried next refresh.
        }
    }

    /// <summary>twitch.tv's popout chat for the front channel, which has
    /// the redeem button this app can't offer.</summary>
    [RelayCommand]
    private void RedeemOnTwitch()
    {
        if (ActiveTab is { } front) UrlLauncher.TryOpen($"https://www.twitch.tv/popout/{Uri.EscapeDataString(front.Login)}/chat");
    }

    /// <summary>The points menu opening: a fresh balance if the last is
    /// more than a few seconds old.</summary>
    public void RefreshPoints()
    {
        if (!_options.Demo && ActiveTab is { } front && DateTimeOffset.Now - front.PointsLoadedAt >= TimeSpan.FromSeconds(10))
            _ = LoadPointsAsync(front);
    }

    // ----- Chat extras ----------------------------------------------------

    /// <summary>ROOMSTATE gives a channel's numeric id, which its emote sets
    /// and sub badges are keyed by. It repeats on mode changes; only a new
    /// id triggers loading.</summary>
    private void OnRoomState(string channel, string roomId)
    {
        if (TabFor(channel) is not { } tab || tab.Chat.RoomId == roomId) return;
        tab.Chat.RoomId = roomId;
        _ = Services.Emotes.LoadChannelAsync(roomId, _stop.Token);
        if (Account.IsSignedIn && Account.AccessToken is { } token)
            _ = Services.Badges.LoadChannelAsync(_http, _settings.EffectiveClientId, token, roomId, _stop.Token);
        // Follower emotes are only listed when asked for by channel.
        _ = LoadMyEmotesAsync(roomId);
    }

    /// <summary>Twitch's badge art needs a signed-in user's token; without
    /// one, badges show as coloured placeholder chips.</summary>
    private async Task LoadTwitchBadgesAsync()
    {
        if (!Account.IsSignedIn || Account.AccessToken is not { } token) return;
        await Services.Badges.LoadGlobalAsync(_http, _settings.EffectiveClientId, token, _stop.Token);
        foreach (var room in Tabs.Select(t => t.RoomId).OfType<string>().Distinct().ToList())
            await Services.Badges.LoadChannelAsync(_http, _settings.EffectiveClientId, token, room, _stop.Token);
    }

    /// <summary>Redraws chats after emotes or badges loaded: one room's tabs,
    /// or all of them for globals (null).</summary>
    private void RenderChats(string? roomId)
    {
        foreach (var tab in Tabs.Where(t => roomId is null || t.RoomId == roomId)) tab.Chat.RenderAll();
    }

    /// <summary>GIFs drawn in chat, or left as their "[title]" text.</summary>
    public bool ShowGifs
    {
        get => _settings.ShowGifs;
        set
        {
            if (SetProperty(_settings.ShowGifs, value, _settings, (s, v) => s.ShowGifs = v)) RenderChats(null);
        }
    }

    public bool ShowPronouns
    {
        get => _settings.ShowPronouns;
        set
        {
            if (SetProperty(_settings.ShowPronouns, value, _settings, (s, v) => s.ShowPronouns = v)) RenderChats(null);
        }
    }

    public bool UseBttv
    {
        get => _settings.UseBttv;
        set => SetProvider(value, _settings.UseBttv, (s, v) => s.UseBttv = v);
    }

    public bool UseFfz
    {
        get => _settings.UseFfz;
        set => SetProvider(value, _settings.UseFfz, (s, v) => s.UseFfz = v);
    }

    public bool UseSevenTv
    {
        get => _settings.UseSevenTv;
        set => SetProvider(value, _settings.UseSevenTv, (s, v) => s.UseSevenTv = v);
    }

    /// <summary>Switching a third-party provider takes effect at once: off
    /// stops matching its emotes, on fetches its sets for every open tab.</summary>
    private void SetProvider(bool value, bool current, Action<AppSettings, bool> set,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(current, value, _settings, set, name)) return;
        Services.Emotes.Apply(_settings);
        RenderChats(null);
        if (!value || _options.Demo) return;
        _ = Services.Emotes.LoadGlobalsAsync(_stop.Token);
        foreach (var room in Tabs.Select(t => t.RoomId).OfType<string>().Distinct())
            _ = Services.Emotes.LoadChannelAsync(room, _stop.Token);
    }

    /// <summary>JOIN/PART lines; official Twitch chat doesn't show them.
    /// Applies to lines from now on.</summary>
    public bool ShowJoinPart
    {
        get => _settings.ShowJoinPart;
        set => SetProperty(_settings.ShowJoinPart, value, _settings, (s, v) => s.ShowJoinPart = v);
    }

    public bool ShowTimestamps
    {
        get => _settings.TimestampFormat.Length > 0;
        set => SetProperty(ShowTimestamps, value, _settings, (s, v) => s.TimestampFormat = v ? "h:mm tt" : "");
    }

    // ----- Playback -------------------------------------------------------

    /// <summary>Why there's no video ("libmpv not found…"), or null.</summary>
    public string? PlayerUnavailable { get; }

    /// <summary>Called by the window when a tab's video surface has a native
    /// window with a player on it (or lost it: null).</summary>
    public void AttachPlayer(ChannelTabViewModel tab, IVideoPlayer? player)
    {
        if (!Tabs.Contains(tab)) return;
        var tier = tab.IsFront ? PlaybackTier.Full : ShowPip ? PlaybackTier.Thumbnail : PlaybackTier.AudioOnly;
        tab.Attach(player, _http, _settings, tier, SecretStore.Get(SecretStore.PlaybackAuthToken));
    }

    /// <summary>Just the video, filling the screen: chat, list and tabs
    /// hidden. Double-click the video (or Esc) to toggle.</summary>
    [ObservableProperty]
    private bool _videoOnly;

    /// <summary>Mouse and keys over a tab's video (from mpv, which owns the
    /// video window): double-click the front video for video-only
    /// fullscreen; click a PIP thumbnail to bring it forward; scroll for
    /// that tab's own volume; Esc leaves fullscreen.</summary>
    public void OnVideoInput(ChannelTabViewModel tab, VideoInput input)
    {
        if (!Tabs.Contains(tab)) return;
        switch (input)
        {
            case VideoInput.DoubleClick when tab.IsFront:
                VideoOnly = !VideoOnly;
                break;
            case VideoInput.Click or VideoInput.DoubleClick when !tab.IsFront:
                SelectTab(tab);
                break;
            case VideoInput.WheelUp:
                tab.ChangeVolume(+5, _settings);
                break;
            case VideoInput.WheelDown:
                tab.ChangeVolume(-5, _settings);
                break;
            case VideoInput.Escape:
                VideoOnly = false;
                break;
        }
    }

    /// <summary>A surface was created but mpv failed to start on it.</summary>
    public void PlayerFailed(ChannelTabViewModel tab, string message)
    {
        tab.Player.IsVideoVisible = false;
        tab.Player.Status = "Player failed to start: " + message;
    }

    private void TickPlayback()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var tab in Tabs) tab.Session?.Tick(now);
    }

    private void ShowChannelInfo(ChannelTabViewModel tab)
    {
        var now = DateTimeOffset.Now;
        var stream = Streams.Items.Select(i => i.Stream)
            .FirstOrDefault(s => string.Equals(s.Login, tab.Login, StringComparison.OrdinalIgnoreCase));
        tab.Player.Channel = stream?.DisplayName ?? tab.DisplayName ?? tab.Login;
        tab.Player.Title = stream?.Title ?? "";
        tab.Player.Game = stream?.Game ?? "";
        if (tab != FrontTab) return;
        WindowTitle = stream is null
            ? $"#{tab.Login} - StreamEdge"
            : $"#{tab.Login} {UiFormats.TitleStats(stream.Viewers, stream.Uptime(now))} - {stream.Title}";
    }

    /// <summary>The ⟳ button: reload the live list now, and retry any tab
    /// that's offline or stuck, rather than waiting for the timers.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        foreach (var tab in Tabs) tab.Session?.RetryNow();
        await RefreshStreamsAsync();
    }

    private async Task RefreshStreamsAsync()
    {
        _lastStreamRefresh = DateTimeOffset.Now;
        if (!Account.IsSignedIn || Account.UserId is null || Account.AccessToken is not { } token)
        {
            Streams.Set([], DateTimeOffset.Now);
            Streams.EmptyText = "Log in to see which channels you follow are live.";
            return;
        }
        try
        {
            var helix = new HelixClient(_http, _settings.EffectiveClientId, token);
            var live = await helix.GetFollowedStreamsAsync(Account.UserId, _stop.Token);
            live = await WithAvatarsAsync(helix, live);
            Streams.Set(live, DateTimeOffset.Now);
            Streams.EmptyText = "No followed channels are live.";
            foreach (var tab in Tabs) ShowChannelInfo(tab);
        }
        catch (HttpRequestException ex)
        {
            Streams.EmptyText = "Could not load streams: " + ex.Message;
        }
    }

    /// <summary>Channel avatars by user id. Kept for the session: they rarely
    /// change, and the list refreshes every few minutes.</summary>
    private readonly Dictionary<string, Uri> _avatars = [];

    /// <summary>The streams with their channels' avatars, looking up only
    /// the ones not seen before. A failed lookup leaves the list without
    /// avatars rather than without streams.</summary>
    private async Task<IReadOnlyList<LiveStream>> WithAvatarsAsync(HelixClient helix, IReadOnlyList<LiveStream> live)
    {
        var missing = live.Select(s => s.UserId).OfType<string>().Where(id => !_avatars.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                foreach (var (id, image) in await helix.GetProfileImagesAsync(missing, _stop.Token))
                    _avatars[id] = image;
            }
            catch (HttpRequestException) { }
        }
        return [.. live.Select(s => s.UserId is { } id && _avatars.TryGetValue(id, out var image) ? s with { ProfileImage = image } : s)];
    }

    private bool _overlayWarned;

    /// <summary>Says once, in every open chat, if an overlay known to crash
    /// the players has hooked into the app.</summary>
    private void WarnAboutOverlays()
    {
        if (_overlayWarned || _options.Demo) return;
        var advice = OverlayCheck.Loaded().ToList();
        if (advice.Count == 0) return;
        _overlayWarned = true;
        foreach (var tab in Tabs)
            foreach (var line in advice)
                tab.Chat.Add(ChatMessage.System(tab.Login, "⚠ " + line, DateTimeOffset.Now));
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        WarnAboutOverlays();
        Streams.Tick(now);
        foreach (var tab in Tabs) ShowChannelInfo(tab);
        if (!_options.Demo && now - _lastStreamRefresh >= StreamRefresh) _ = RefreshStreamsAsync();
        // The balance changes as the user redeems or earns elsewhere; only
        // the columns' shown tabs have theirs on screen.
        if (!_options.Demo)
            foreach (var shown in Columns.Select(c => c.Selected).OfType<ChannelTabViewModel>())
                if (now - shown.PointsLoadedAt >= PointsRefresh) _ = LoadPointsAsync(shown);
        // Not just at exit: a crash or power cut should cost at most this
        // tick's worth of layout, tabs and window placement.
        _settings.Save();
    }

    public void SaveSettings() => _settings.Save();

    public async ValueTask DisposeAsync()
    {
        await _clock.DisposeAsync().ConfigureAwait(false);
        await _playbackClock.DisposeAsync().ConfigureAwait(false);
        foreach (var tab in Tabs.ToList()) await tab.DisposeAsync().ConfigureAwait(false);
        _stop.Cancel();
        if (_chat is not null) await _chat.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _stop.Dispose();
    }
}
