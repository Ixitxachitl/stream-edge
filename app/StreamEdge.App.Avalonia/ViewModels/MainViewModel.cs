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
    private EventSubClient? _eventSub;
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
        ChannelPanel = new ChannelPanelViewModel(_http, demo: options.Demo);
        if (!options.Demo && !Playback.Mpv.MpvPlayer.IsAvailable(out var why)) PlayerUnavailable = why;
        Account = new AccountViewModel(settings, _http);
        Services = new ChatServices(_http, settings, ui, () => Account.IsSignedIn ? Account.Login : null);
        // Emotes and badges load in the background; lines already drawn are
        // redrawn when they arrive (null room = globals: every tab).
        Services.Emotes.Changed += room => _ui.Post(() => RenderChats(room));
        Services.Badges.Changed += room => _ui.Post(() => RenderChats(room));
        // Own lines drawn before the user's emotes arrived get them now.
        Services.MyEmotesChanged += () => RenderChats(null);
        // Shared chat: a channel's name and icon arrived.
        Services.ChannelLoaded += OnChannelLoaded;
        if (options.Demo) Services.LookUpChannels = false;
        EmoteMenu = new EmoteMenuViewModel(Services, () => ActiveTab, Account);
        ModMenu = new ModMenuViewModel(() => ActiveTab, Moderation);
        Columns.CollectionChanged += (_, _) => OnSplitsChanged();
        Activate(AddColumn(0));
        Account.AccountChanged += () => _ = OnAccountChangedAsync();
        // Same account, new token: chat reconnects with it from now on.
        Account.TokenRenewed += token => _ui.Post(() =>
        {
            if (_chat is not null) _chat.Token = token;
        });
        Streams.OpenRequested += (login, mode) => OpenChannel(login, mode);
        Streams.ShowOfflineChanged += OnShowOfflineChanged;
        Search = new ChannelSearchViewModel(LocalChannels, SearchChannelsAsync, (login, mode) => OpenChannel(login, mode), ui);

        if (options.Demo) LoadDemo();
        else _ = StartAsync();

        _clock = new Timer(_ => _ui.Post(Tick), null, ClockTick, ClockTick);
        _playbackClock = new Timer(_ => _ui.Post(TickPlayback), null, PlaybackTick, PlaybackTick);
    }

    public StreamListViewModel Streams { get; }
    public ChannelSearchViewModel Search { get; }
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

    /// <summary>The front tab's quality menu, copied into one collection that
    /// lives as long as the app. The menu binds this rather than
    /// <c>Player.Qualities</c>: once a MenuFlyout has been opened, giving it
    /// a different collection leaves it drawing the old one, so after a
    /// switch of streams it showed the closed tab's (emptied) list and
    /// wouldn't open.</summary>
    public ObservableCollection<QualityOption> Qualities { get; } = [];

    private PlayerViewModel? _qualitiesFrom;

    partial void OnFrontTabChanged(ChannelTabViewModel? value)
    {
        if (_qualitiesFrom is { } old) old.Qualities.CollectionChanged -= OnFrontQualitiesChanged;
        _qualitiesFrom = Player;
        _qualitiesFrom.Qualities.CollectionChanged += OnFrontQualitiesChanged;
        OnFrontQualitiesChanged(null, null);
        // The panel under the video follows the channel on it.
        if (ShowChannelPanel) _ = ChannelPanel.LoadAsync(value);
        else ChannelPanel.Show(value);
    }

    private void OnFrontQualitiesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        Qualities.Clear();
        foreach (var option in Player.Qualities) Qualities.Add(option);
    }

    public bool HasChannel => FrontTab is not null;

    public bool HasTabs => Tabs.Count > 0;

    /// <summary>False in demo mode: no players are created at all.</summary>
    public bool PlaybackEnabled => !_options.Demo && PlayerUnavailable is null;

    public double ChatShare
    {
        get => _settings.ChatShare;
        set => SetProperty(_settings.ChatShare, Math.Clamp(value, 0, 1), _settings, (s, v) => s.ChatShare = v);
    }

    /// <summary>Each pane's place around the video: side, position along it,
    /// stacked or not; kept where it was left.</summary>
    public IReadOnlyDictionary<PaneId, PanePlace> PanePlaces => _settings.PanePlaces;

    public PanePlace ChatPlace => PanePlaces[PaneId.Chat];
    public PanePlace ListPlace => PanePlaces[PaneId.List];
    public PanePlace PanelPlace => PanePlaces[PaneId.Panel];

    /// <summary>Which side of the video chat docks to.</summary>
    public DockSide ChatDock => ChatPlace.Side;

    /// <summary>Which side of the video the live list docks to.</summary>
    public DockSide ListDock => ListPlace.Side;

    /// <summary>Which side of the video the About panels dock to.</summary>
    public DockSide PanelDock => PanelPlace.Side;

    /// <summary>Puts the panes where a drag dropped them (see
    /// <see cref="PaneArrangement"/>) and remembers it. A pane moved
    /// somewhere is shown, if it was hidden.</summary>
    public void SetPanePlaces(IReadOnlyDictionary<PaneId, PanePlace> places, PaneId? moved = null)
    {
        _settings.SetPanePlaces(places);
        foreach (var name in new[] { nameof(PanePlaces), nameof(ChatPlace), nameof(ListPlace), nameof(PanelPlace),
                     nameof(ChatDock), nameof(ListDock), nameof(PanelDock), nameof(MinWindowWidth) })
            OnPropertyChanged(name);
        if (moved == PaneId.List) ShowStreamList = true;
        if (moved == PaneId.Panel) ShowChannelPanel = true;
    }

    /// <summary>Moves chat to a side of the video, next to it.</summary>
    [RelayCommand]
    private void DockChat(DockSide side) => SetPanePlaces(PaneArrangement.MoveToSide(PanePlaces, PaneId.Chat, side), PaneId.Chat);

    /// <summary>Moves the live list to a side of the video, next to it, showing it.</summary>
    [RelayCommand]
    private void DockList(DockSide side) => SetPanePlaces(PaneArrangement.MoveToSide(PanePlaces, PaneId.List, side), PaneId.List);

    /// <summary>The live list is shown (where <see cref="ListDock"/> says);
    /// off, chat takes its room.</summary>
    public bool ShowStreamList
    {
        get => _settings.ShowStreamList;
        set
        {
            if (SetProperty(_settings.ShowStreamList, value, _settings, (s, v) => s.ShowStreamList = v))
                OnPropertyChanged(nameof(MinWindowWidth));
        }
    }

    /// <summary>Panes on both sides of the video need room for it between
    /// two minimum-width sides; otherwise less will do.</summary>
    public double MinWindowWidth
    {
        get
        {
            var docks = new List<DockSide> { ChatDock };
            if (ShowStreamList) docks.Add(ListDock);
            if (ShowChannelPanel) docks.Add(PanelDock);
            return docks.Contains(DockSide.Left) && docks.Contains(DockSide.Right) ? 960 : 640;
        }
    }

    /// <summary>The channel's About panels, docked where <see cref="PanelDock"/> says.</summary>
    public bool ShowChannelPanel
    {
        get => _settings.ShowChannelPanel;
        set
        {
            if (!SetProperty(_settings.ShowChannelPanel, value, _settings, (s, v) => s.ShowChannelPanel = v)) return;
            OnPropertyChanged(nameof(MinWindowWidth));
            if (value) _ = ChannelPanel.LoadAsync(FrontTab);
        }
    }

    /// <summary>Moves the About panels to a side of the video, next to it, showing them.</summary>
    [RelayCommand]
    private void DockChannelPanel(DockSide side) =>
        SetPanePlaces(PaneArrangement.MoveToSide(PanePlaces, PaneId.Panel, side), PaneId.Panel);

    /// <summary>Share of the height the row above the video takes; two-way,
    /// as dragging the line under it writes it back.</summary>
    public double TopRowShare
    {
        get => _settings.TopRowShare;
        set => SetProperty(_settings.TopRowShare, Math.Clamp(value, 0.1, 0.7), _settings, (s, v) => s.TopRowShare = v);
    }

    /// <summary>The same for the row below the video.</summary>
    public double BottomRowShare
    {
        get => _settings.BottomRowShare;
        set => SetProperty(_settings.BottomRowShare, Math.Clamp(value, 0.1, 0.7), _settings, (s, v) => s.BottomRowShare = v);
    }

    /// <summary>The front channel's About panels.</summary>
    public ChannelPanelViewModel ChannelPanel { get; }

    public bool ReopenLastChannel
    {
        get => _settings.ReopenLastChannel;
        set => SetProperty(_settings.ReopenLastChannel, value, _settings, (s, v) => s.ReopenLastChannel = v);
    }

    /// <summary>Background tabs as small live videos over the player; off,
    /// they don't play at all until brought to the front.</summary>
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
        // The demo channel shares its chat, as on twitch.tv.
        foreach (var login in SampleData.Streams(now).Select(s => s.Login))
            Services.Remember(SampleData.RoomId(login), SampleData.Info(login));
        var shared = SampleData.SharedChat.Select(SampleData.RoomId).ToList();
        foreach (var room in shared) _sharedSessions[room] = shared;
        sample = [.. sample.Select(WithSharedChat)];
        Streams.Set(sample, now);
        var tab = OpenChannel(SampleData.Channel, OpenMode.NewTab)!;
        tab.Chat.Clear();
        foreach (var line in SampleData.Chat(now)) tab.Chat.Add(line);
        tab.Chat.Roster.AddNames(SampleData.Lurkers);
        tab.Chat.Points = SampleData.Points();
        tab.Chat.RoomId = SampleData.RoomId(SampleData.Channel);
        tab.Chat.SetSharedSession(shared);
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
        _eventSub = CreateEventSub();
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
        client.MessageReceived += m => _ui.Post(() => OnChatMessage(m));
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
        if (_eventSub is not null) await _eventSub.DisposeAsync();
        _eventSub = CreateEventSub();
        _follows.Clear();
        foreach (var tab in Tabs) await _chat.JoinAsync(tab.Login);
        await LoadTwitchBadgesAsync();
        RenderChats(null);
        await LoadMyEmotesAsync();
        await RefreshStreamsAsync();
    }

    // ----- Tabs -----------------------------------------------------------

    /// <summary>Open channels at most: the main video and a full 4×4 grid
    /// of thumbnails. Each is a live stream and a chat connection.</summary>
    public const int MaxTabs = 17;

    /// <summary>Opens a channel. An already-open channel is just brought
    /// forward (or left where it is, for background). Null when nothing
    /// opened: no name, or <see cref="MaxTabs"/> already open (replacing the
    /// front tab in place still works then).</summary>
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
        var replacing = mode == OpenMode.Front && column.Selected is not null;
        if (!replacing && Tabs.Count >= MaxTabs)
        {
            ActiveTab?.Chat.Add(ChatMessage.System(ActiveTab.Login,
                $"{MaxTabs} channels are open, the most there's room for: close one to open {login}.", DateTimeOffset.Now));
            return null;
        }
        var index = column.Tabs.Count;
        if (mode == OpenMode.Front && column.Selected is { } replaced)
        {
            // Double-click swaps the channel in the active column's tab, in place.
            index = column.Tabs.IndexOf(replaced);
            CloseTab(replaced, pickNewFront: false);
        }

        var tab = AddTab(login, column, index, select: mode != OpenMode.Background || column.Selected is null);
        if (mode != OpenMode.Background || FrontTab is null) SetFront(tab);
        else ApplyTiers();
        SaveTabs();
        OnPropertyChanged(nameof(HasTabs));
        return tab;
    }

    /// <summary>A new tab for <paramref name="login"/> at <paramref name="index"/>
    /// in <paramref name="column"/>: joins its chat and starts loading its
    /// look, history and points. Video is the caller's call.</summary>
    private ChannelTabViewModel AddTab(string login, ChatColumnViewModel column, int index, bool select)
    {
        var tab = new ChannelTabViewModel(login, _settings, Services, SelectTab, t => CloseTab(t))
        {
            IsFront = false,
        };
        // Open tabs follow a new pick too, so the next one brought to the
        // front isn't still at the old quality.
        tab.QualityPicked = quality =>
        {
            foreach (var other in Tabs)
                if (other != tab && other.Session is { } session) session.PreferredQuality = quality;
        };
        tab.Chat.CanSend = _chat?.Token is not null;
        tab.Chat.Status = _chat is null ? "" : "Connected";
        tab.Chat.Add(ChatMessage.System(login, $"### Joining #{login} ###", DateTimeOffset.Now));
        if (PlayerUnavailable is { } why) tab.Player.Status = why;
        Tabs.Add(tab);
        column.Tabs.Insert(Math.Min(index, column.Tabs.Count), tab);
        if (select) column.Selected = tab;
        _ = _chat?.JoinAsync(login);
        ShowChannelInfo(tab);
        if (!_options.Demo)
        {
            _ = LoadChannelLookAsync(tab);
            _ = LoadHistoryAsync(tab);
            _ = LoadPointsAsync(tab);
        }
        UpdateWatched();
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
        // The column with the video keeps showing its chat (dragging doesn't
        // change the video, and its tab is the one marked); elsewhere the
        // dropped tab is shown.
        target.Selected = FrontTab is { } front && front != tab && target.Tabs.Contains(front) ? front : tab;
        Activate(target);
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

    private void SetFront(ChannelTabViewModel? tab)
    {
        FrontTab = tab;
        // The CC choice is one setting; each tab's button shows it when in front.
        if (tab is not null) tab.Player.Captions = _settings.Captions;
        foreach (var t in Tabs) t.IsFront = t == tab;
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
            _eventSub?.Unwatch(room);
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
        SaveTabs();
        UpdateWatched();
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

    /// <summary>Front: full quality. Others: a thumbnail with PIP on; with it
    /// off they stop, silent and downloading nothing, until brought forward.</summary>
    private void ApplyTiers()
    {
        foreach (var tab in Tabs)
        {
            if (tab.Session is { } session)
                session.Tier = tab.IsFront ? PlaybackTier.Full : ShowPip ? PlaybackTier.Thumbnail : PlaybackTier.Off;
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (ActiveTab is not { } tab) return;
        var text = tab.Chat.Draft.Trim();
        if (text.Length == 0 || _chat is null || !tab.Chat.CanSend) return;
        tab.Chat.Draft = "";

        // Slash commands (other than /me) go through Helix, not IRC.
        if (ChatCommand.Parse(text) is { } command)
        {
            await RunCommandAsync(tab, command);
            return;
        }

        var reply = tab.Chat.ReplyTo?.Message;
        tab.Chat.ReplyTo = null;
        await _chat.SendMessageAsync(tab.Login, text, reply?.Id);

        // Twitch doesn't echo our own PRIVMSG back, so draw it here, the way
        // Twitch would: from the USERSTATE it sent for this channel (badges,
        // colour, display name).
        var kind = ChatMessageKind.Message;
        if (text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
        {
            kind = ChatMessageKind.Action;
            text = text[4..];
        }
        var state = _userStates.GetValueOrDefault(tab.Login);
        tab.Chat.AddOwn(new ChatMessage(DateTimeOffset.Now, kind, tab.Login, Account.Login,
            state?.DisplayName ?? _globalUserState?.DisplayName ?? Account.Login,
            state?.Color ?? _globalUserState?.Color, state?.Badges ?? [], text)
        {
            UserId = _globalUserState?.UserId ?? Account.UserId,
            ReplyParentName = reply?.Sender,
            ReplyParentBody = reply?.Text,
        });
    }

    private readonly Dictionary<string, ChatUserState> _userStates = new(StringComparer.OrdinalIgnoreCase);
    private ChatUserState? _globalUserState;

    private void OnUserState(ChatUserState state)
    {
        if (state.Channel is { } channel)
        {
            _userStates[channel] = state;
            // Moderator (lead or not) or broadcaster here: mod icons on
            // lines, the mod menu.
            if (TabFor(channel) is { } tab)
            {
                tab.Chat.IsModerator = state.CanModerate;
                tab.Chat.IsBroadcaster = state.IsBroadcaster;
                if (state.MessageId is { } sent) tab.Chat.ClaimOwnId(sent);
            }
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
                UpdateWatched();
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
        {
            _ = Services.Badges.LoadChannelAsync(_http, _settings.EffectiveClientId, token, roomId, _stop.Token);
            _ = LoadCheermotesAsync(token, roomId);
        }
        // Follower emotes are only listed when asked for by channel.
        _ = LoadMyEmotesAsync(roomId);
        _eventSub?.Watch(roomId);
        _ = RefreshWatchedAsync();
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

    /// <summary>The channel's cheermotes (Twitch's and its own), so cheers
    /// draw as pictures; signed out they stay text.</summary>
    private async Task LoadCheermotesAsync(string token, string roomId)
    {
        var cheermotes = await Cheermotes.FetchAsync(_http, _settings.EffectiveClientId, token, roomId, _stop.Token);
        if (cheermotes.Count == 0) return;
        Services.SetCheermotes(roomId, cheermotes);
        RenderChats(roomId);
    }

    /// <summary>Redraws chats after emotes or badges loaded: one room's tabs,
    /// or all of them for globals (null).</summary>
    private void RenderChats(string? roomId)
    {
        // Shared chat: lines from another channel use its emotes and badges.
        foreach (var tab in Tabs.Where(t => roomId is null || t.RoomId == roomId || t.Chat.SharedRooms.Contains(roomId)
                                            || t.Chat.Lines.Any(l => l.Message.SourceRoomId == roomId)))
            tab.Chat.RenderAll();
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

    /// <summary>Deleted messages crossed out rather than replaced.</summary>
    public bool ShowDeletedMessages
    {
        get => _settings.ShowDeletedMessages;
        set
        {
            if (SetProperty(_settings.ShowDeletedMessages, value, _settings, (s, v) => s.ShowDeletedMessages = v)) RenderChats(null);
        }
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
        var tier = tab.IsFront ? PlaybackTier.Full : ShowPip ? PlaybackTier.Thumbnail : PlaybackTier.Off;
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

    /// <summary>A thumbnail was dragged: the cells the thumbnails on show now
    /// have, kept per channel so they come back there.</summary>
    public void MovePips(IReadOnlyList<(ChannelTabViewModel Tab, PipCell Cell)> moved)
    {
        foreach (var (tab, cell) in moved)
        {
            if (!Tabs.Contains(tab)) continue;
            tab.PipCell = cell;
            _settings.PipCells[tab.Login] = cell;
        }
    }

    /// <summary>The mouse over a tab's video: the main video shows a strip
    /// of stream info along its bottom; PIP thumbnails are too small for one.</summary>
    public void OnVideoHover(ChannelTabViewModel tab, bool over)
    {
        if (Tabs.Contains(tab)) tab.ShowInfoStrip(over && tab.IsFront);
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

    // ----- Shared chat ----------------------------------------------------

    private static readonly TimeSpan SharedChatRefresh = TimeSpan.FromMinutes(3);

    /// <summary>Shared chat sessions by broadcaster id: every channel in it,
    /// host first. Channels not in one aren't in here.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _sharedSessions = [];
    private DateTimeOffset _lastFollowedSharedCheck = DateTimeOffset.MinValue;

    /// <summary>Other channels' rooms whose emotes and badges are loaded for
    /// lines shared from them.</summary>
    private readonly HashSet<string> _sharedRoomsLoaded = [];

    private void OnChatMessage(ChatMessage m)
    {
        if (TabFor(m.Channel) is not { } tab) return;
        tab.Chat.Add(m);
        if (m.IsFromOtherRoom(tab.RoomId) && m.SourceRoomId is { } source) LoadSharedRoom(source);
    }

    /// <summary>A line shared from a channel that isn't open: its emotes and
    /// badges, so the line looks as it does there.</summary>
    private void LoadSharedRoom(string room)
    {
        if (_options.Demo || !_sharedRoomsLoaded.Add(room)) return;
        _ = Services.Emotes.LoadChannelAsync(room, _stop.Token);
        if (Account.IsSignedIn && Account.AccessToken is { } token)
            _ = Services.Badges.LoadChannelAsync(_http, _settings.EffectiveClientId, token, room, _stop.Token);
    }

    private void OnChannelLoaded(string room)
    {
        RenderChats(room);
        foreach (var tab in Tabs.Where(t => t.Chat.SharedRooms.Contains(room))) tab.Chat.RefreshSharedChannels();
        // The stream list names a session's channels.
        if (_sharedSessions.ContainsKey(room))
        {
            Streams.Set([.. Streams.Followed.Select(WithSharedChat)], DateTimeOffset.Now);
            UpdateWatched();
        }
    }

    /// <summary>The stream as twitch.tv lists it: with the channels it
    /// shares chat with, by name and icon.</summary>
    private LiveStream WithSharedChat(LiveStream s)
    {
        if (s.UserId is not { } id || !_sharedSessions.TryGetValue(id, out var session))
            return s.SharedWith is null ? s : s with { SharedWith = null };
        return s with
        {
            SharedWith = [.. session.Where(r => r != id)
                .Select(r => Services.ChannelById(r) ?? new ChannelInfo("", "…", null, null))],
        };
    }

    /// <summary>Asks Twitch which channels share chat: the open tabs' every
    /// refresh, the followed live ones every few minutes (one request each).</summary>
    private async Task RefreshSharedChatsAsync(HelixClient helix, IReadOnlyList<LiveStream> live)
    {
        var now = DateTimeOffset.Now;
        var ids = Tabs.Select(t => t.RoomId).OfType<string>().ToHashSet();
        var followed = now - _lastFollowedSharedCheck >= SharedChatRefresh;
        if (followed)
        {
            _lastFollowedSharedCheck = now;
            ids.UnionWith(live.Select(s => s.UserId).OfType<string>());
        }
        try
        {
            // A few at a time: one request per channel adds up.
            var answers = new Dictionary<string, IReadOnlyList<string>>();
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(ids.Select(async id =>
            {
                await gate.WaitAsync(_stop.Token);
                try
                {
                    var session = await helix.GetSharedChatAsync(id, _stop.Token);
                    lock (answers) answers[id] = session;
                }
                finally
                {
                    gate.Release();
                }
            }));
            foreach (var (id, session) in answers)
            {
                if (session.Count > 1) _sharedSessions[id] = session;
                else _sharedSessions.Remove(id);
            }
            // Followed channels not asked this time keep their last answer;
            // ones no longer live drop out.
            if (followed)
                foreach (var gone in _sharedSessions.Keys.Where(k => !ids.Contains(k)).ToList()) _sharedSessions.Remove(gone);
            var unknown = _sharedSessions.Values.SelectMany(v => v).Distinct()
                .Where(r => Services.ChannelById(r) is null).ToList();
            if (unknown.Count > 0)
                foreach (var (id, info) in await helix.GetUsersAsync(unknown, _stop.Token))
                    Services.Remember(id, info);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return;
        }
        foreach (var tab in Tabs)
            if (tab.RoomId is { } room)
                tab.Chat.SetSharedSession(_sharedSessions.TryGetValue(room, out var s) ? s : []);
    }

    // ----- Raids ----------------------------------------------------------

    public bool FollowRaids
    {
        get => _settings.FollowRaids;
        set => SetProperty(_settings.FollowRaids, value, _settings, (s, v) => s.FollowRaids = v);
    }

    /// <summary>An alert when a favourite (★) goes live.</summary>
    public bool NotifyGoLive
    {
        get => _settings.NotifyGoLive;
        set => SetProperty(_settings.NotifyGoLive, value, _settings, (s, v) => s.NotifyGoLive = v);
    }

    /// <summary>Raids out of open channels come over EventSub, which needs
    /// a signed-in user; signed out, a raiding channel just goes offline.</summary>
    private EventSubClient? CreateEventSub()
    {
        if (_options.Demo || !Account.IsSignedIn) return null;
        var client = new EventSubClient(_http, _settings.EffectiveClientId, () => Account.AccessToken);
        client.Raided += raid => _ui.Post(() => OnRaid(raid));
        client.Whispered += whisper => _ui.Post(() => OnWhisper(whisper));
        client.Start();
        if (Account.CanUseUserActions && Account.UserId is { } me) client.WatchWhispers(me);
        foreach (var room in Tabs.Select(t => t.RoomId).OfType<string>().Distinct()) client.Watch(room);
        return client;
    }

    /// <summary>An open channel raided another: said in its chat, and with
    /// <see cref="FollowRaids"/> the tab moves to the raided channel. Off,
    /// the tab stays and shows the offline screen when the stream ends.</summary>
    public void OnRaid(RaidEvent raid)
    {
        var source = Tabs.FirstOrDefault(t => t.RoomId == raid.FromId) ?? TabFor(raid.FromLogin);
        if (source is null) return;
        var name = raid.ToName.Length > 0 ? raid.ToName : raid.ToLogin;
        source.Chat.Add(ChatMessage.System(source.Login,
            $"Raiding {name} with {UiFormats.Viewers(raid.Viewers)} viewers.", DateTimeOffset.Now));
        if (!FollowRaids || string.Equals(raid.ToLogin, source.Login, StringComparison.OrdinalIgnoreCase)) return;

        var from = source.DisplayName ?? source.Login;
        if (TabFor(raid.ToLogin) is { } open)
        {
            // Already open: shown if the raider was on video. The raider's
            // tab stays, and goes offline when its stream ends.
            if (source.IsFront) SelectTab(open);
            open.Chat.Add(ChatMessage.System(open.Login, $"### Raid from {from} ###", DateTimeOffset.Now));
            return;
        }
        var target = ReplaceTab(source, raid.ToLogin.ToLowerInvariant());
        target?.Chat.Add(ChatMessage.System(target.Login, $"### Followed the raid from {from} ###", DateTimeOffset.Now));
    }

    /// <summary>The same place in the same column, now showing another
    /// channel; on video if the old one was.</summary>
    private ChannelTabViewModel? ReplaceTab(ChannelTabViewModel old, string login)
    {
        if (ColumnOf(old) is not { } column) return null;
        var index = column.Tabs.IndexOf(old);
        var wasSelected = column.Selected == old;
        var wasFront = FrontTab == old;
        CloseTab(old, pickNewFront: false);
        var tab = AddTab(login, column, index, select: wasSelected || column.Selected is null);
        if (wasFront || FrontTab is null) SetFront(tab);
        else ApplyTiers();
        SaveTabs();
        OnPropertyChanged(nameof(HasTabs));
        return tab;
    }

    // ----- Channel search and watched channels -----------------------------

    /// <summary>Channels the app already knows of whose name has the query
    /// in it: followed live ones, then open tabs.</summary>
    private IEnumerable<ChannelSuggestion> LocalChannels(string query)
    {
        foreach (var s in Streams.Followed.Concat(Streams.Watched))
            if (ChannelSearch.Matches(s.Login, s.DisplayName, query))
                yield return new ChannelSuggestion(s.Login, s.DisplayName, s.IsLive, s.Game, s.ProfileImage);
        foreach (var t in Tabs)
            if (ChannelSearch.Matches(t.Login, t.DisplayName ?? t.Login, query))
                yield return new ChannelSuggestion(t.Login, t.DisplayName ?? t.Login, ProfileImage: t.Player.ProfileImage);
    }

    /// <summary>Twitch's channel search when signed in; signed out, only a
    /// channel by that exact name can be found.</summary>
    private async Task<IReadOnlyList<ChannelSuggestion>> SearchChannelsAsync(string query, CancellationToken ct)
    {
        if (_options.Demo) return [];
        if (Account.IsSignedIn && Account.AccessToken is { } token)
            return await new HelixClient(_http, _settings.EffectiveClientId, token)
                .SearchChannelsAsync(query, ChannelSearchViewModel.MaxSuggestions, ct);
        if (!ChannelSearch.IsValidLogin(query)) return [];
        return await ChannelInfo.FetchAsync(_http, query, ct) is { } info
            ? [new ChannelSuggestion(info.Login, info.DisplayName, ProfileImage: info.ProfileImage)]
            : [];
    }

    /// <summary>Live state of open channels outside the followed list, by
    /// login, from the last refresh.</summary>
    private readonly Dictionary<string, LiveStream> _watchedLive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the user follows a channel, by room id; asked once.</summary>
    private readonly Dictionary<string, bool> _follows = [];

    /// <summary>Open tabs the user doesn't follow, for the top of the stream
    /// list, in tab order. Signed in, a tab is listed once Twitch has said
    /// it isn't followed; signed out, every open tab is.</summary>
    private void UpdateWatched()
    {
        var followedLive = Streams.Followed.Select(s => s.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var signedIn = Account.IsSignedIn && !_options.Demo;
        var watched = new List<LiveStream>();
        foreach (var t in Columns.SelectMany(c => c.Tabs))
        {
            if (followedLive.Contains(t.Login)) continue;
            if (signedIn && (t.RoomId is not { } room || _follows.GetValueOrDefault(room, true))) continue;
            watched.Add(_watchedLive.TryGetValue(t.Login, out var live)
                ? WithSharedChat(live with { ProfileImage = live.ProfileImage ?? t.Player.ProfileImage })
                // No stream info: live is whether it's playing (or was, when
                // a background tab stopped with PIP off).
                : new LiveStream(t.Login, t.DisplayName ?? t.Login, "", t.Player.Game, -1, default,
                    ProfileImage: t.Player.ProfileImage,
                    IsLive: t.Session?.LastKnownLive == true,
                    LastLive: LastLiveFor(t.Login)));
        }
        Streams.SetWatched(watched, DateTimeOffset.Now);
        // Offline ones: when they were last live, for the hover card.
        _ = LoadWatchedLastLiveAsync(watched.Where(w => !w.IsLive).Select(w => w.Login));
    }

    /// <summary>Asks Twitch which open channels the user follows and which
    /// unfollowed ones are live, then redraws the watched rows.</summary>
    private async Task RefreshWatchedAsync()
    {
        if (_options.Demo || !Account.IsSignedIn || Account.UserId is not { } me || Account.AccessToken is not { } token)
        {
            UpdateWatched();
            return;
        }
        var helix = new HelixClient(_http, _settings.EffectiveClientId, token);
        var followedLive = Streams.Followed.Select(s => s.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = Tabs.Where(t => !followedLive.Contains(t.Login)).ToList();
        try
        {
            foreach (var room in others.Select(t => t.RoomId).OfType<string>().Where(r => !_follows.ContainsKey(r)).ToList())
                _follows[room] = await helix.IsFollowingAsync(me, room, _stop.Token);
            var live = others.Count == 0 ? [] : await helix.GetStreamsAsync([.. others.Select(t => t.Login)], _stop.Token);
            live = await WithLabelsAsync(helix, await WithAvatarsAsync(helix, live));
            _watchedLive.Clear();
            foreach (var s in live) _watchedLive[s.Login] = s;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Rows keep what they showed; the next refresh tries again.
        }
        UpdateWatched();
    }

    private void ShowChannelInfo(ChannelTabViewModel tab)
    {
        var now = DateTimeOffset.Now;
        var stream = Streams.Items.Select(i => i.Stream)
            .FirstOrDefault(s => string.Equals(s.Login, tab.Login, StringComparison.OrdinalIgnoreCase));
        tab.Player.Channel = stream?.DisplayName ?? tab.DisplayName ?? tab.Login;
        tab.Player.Title = stream?.Title ?? "";
        tab.Player.Game = stream?.Game ?? "";
        tab.Player.Viewers = stream is { IsLive: true } ? stream.Viewers : -1;
        tab.Player.StartedAt = stream?.StartedAt ?? default;
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

    private int _streamRefreshes;

    private async Task RefreshStreamsAsync()
    {
        _lastStreamRefresh = DateTimeOffset.Now;
        // The ⟳ button and the timer can overlap; the later one's answer wins.
        var mine = ++_streamRefreshes;
        try
        {
            var token = Account.IsSignedIn ? await Account.FreshTokenAsync(_stop.Token) : null;
            if (Account.UserId is not { } me || token is null)
            {
                Streams.Set([], DateTimeOffset.Now);
                Streams.EmptyText = "Log in to see which channels you follow are live.";
                Streams.RefreshProblem = null;
                UpdateWatched();
                return;
            }
            IReadOnlyList<LiveStream> live;
            HelixClient helix;
            try
            {
                helix = new HelixClient(_http, _settings.EffectiveClientId, token);
                live = await helix.GetFollowedStreamsAsync(me, _stop.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Revoked or expired early: renew and try once more.
                if (await Account.FreshTokenAsync(_stop.Token, force: true) is not { } renewed) return;
                helix = new HelixClient(_http, _settings.EffectiveClientId, renewed);
                live = await helix.GetFollowedStreamsAsync(me, _stop.Token);
            }
            live = await WithLabelsAsync(helix, await WithAvatarsAsync(helix, live));
            if (mine != _streamRefreshes) return;
            // The list first; who shares chat is filled in after (one
            // request per live channel) and the list redrawn with it.
            Streams.Set([.. live.Select(WithSharedChat)], DateTimeOffset.Now);
            AlertGoLive(live);
            Streams.EmptyText = "No followed channels are live.";
            Streams.RefreshProblem = null;
            foreach (var tab in Tabs) ShowChannelInfo(tab);
            await RefreshWatchedAsync();
            await RefreshSharedChatsAsync(helix, live);
            if (mine == _streamRefreshes) Streams.Set([.. live.Select(WithSharedChat)], DateTimeOffset.Now);
            // Offline followed channels last: slow to change, fetched seldom.
            if (mine == _streamRefreshes) await RefreshOfflineAsync(helix, me, live);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !_stop.IsCancellationRequested)
        {
            // Said in the header too: with streams already listed, the empty
            // text never shows and a failed refresh looked like no change.
            var why = ex is OperationCanceledException ? "Twitch didn't answer in time" : ex.Message;
            Streams.EmptyText = "Could not load streams: " + why;
            Streams.RefreshProblem = "Couldn't refresh: " + why;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
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
        // Signed out, a watched row is live while its video plays.
        UpdateWatched();
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
        if (_eventSub is not null) await _eventSub.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _stop.Dispose();
    }
}
