// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Auth;
using StreamEdge.Chat;
using StreamEdge.Demo;
using StreamEdge.Playback;
using StreamEdge.Streams;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan StreamRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(30);
    // Catch-up decisions need fresh buffer readings; 4 Hz is plenty.
    private static readonly TimeSpan PlaybackTick = TimeSpan.FromMilliseconds(250);

    private readonly AppSettings _settings;
    private readonly StartupOptions _options;
    private readonly IUiDispatcher _ui;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer _clock;
    private readonly Timer _playbackClock;
    private IVideoPlayer? _video;
    private LivePlaybackSession? _playback;
    private string? _playingLogin;
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
        Chat = new ChatViewModel(settings);
        Streams = new StreamListViewModel(settings);
        Player = new PlayerViewModel
        {
            Muted = settings.Muted,
            SelectQualityHandler = OnSelectQuality,
            MuteHandler = OnMute,
        };
        if (!options.Demo && !Playback.Mpv.MpvPlayer.IsAvailable(out var why)) PlayerUnavailable = why;
        Account = new AccountViewModel(settings, _http);
        Account.AccountChanged += () => _ = OnAccountChangedAsync();
        Streams.ChannelChosen += login => OpenChannelCommand.Execute(login);
        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.Channel)) OnPropertyChanged(nameof(HasChannel));
        };

        if (options.Demo) LoadDemo();
        else _ = StartAsync();

        _clock = new Timer(_ => _ui.Post(Tick), null, ClockTick, ClockTick);
        _playbackClock = new Timer(_ => _ui.Post(() => _playback?.Tick(DateTimeOffset.UtcNow)), null, PlaybackTick, PlaybackTick);
    }

    public ChatViewModel Chat { get; }
    public StreamListViewModel Streams { get; }
    public PlayerViewModel Player { get; }
    public AccountViewModel Account { get; }

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

    public bool ReopenLastChannel
    {
        get => _settings.ReopenLastChannel;
        set => SetProperty(_settings.ReopenLastChannel, value, _settings, (s, v) => s.ReopenLastChannel = v);
    }

    public int? ScreenIndex => _options.ScreenIndex ?? _settings.ScreenIndex;
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
        Chat.Channel = SampleData.Channel;
        foreach (var line in SampleData.Chat(now)) Chat.Add(line);
        Chat.Status = "Demo data";
        ShowChannelInfo(SampleData.Channel);
        Player.Status = "Demo mode: no playback.";
    }

    private async Task StartAsync()
    {
        await Account.RestoreAsync(_stop.Token);
        _chat = CreateChatClient();
        // --channel always wins; otherwise the last channel, if the user wants it.
        var startup = _options.Channel ?? (_settings.ReopenLastChannel ? _settings.Channel : null);
        if (startup is { Length: > 0 } channel)
            await OpenChannelAsync(channel);
        await RefreshStreamsAsync();
    }

    private TwitchChatClient CreateChatClient()
    {
        var client = new TwitchChatClient
        {
            Token = Account.IsSignedIn ? Account.AccessToken : null,
            Login = Account.IsSignedIn ? Account.Login : null,
        };
        client.MessageReceived += m => _ui.Post(() =>
        {
            if (string.Equals(m.Channel, Chat.Channel, StringComparison.OrdinalIgnoreCase)) Chat.Add(m);
        });
        client.StatusChanged += s => _ui.Post(() => Chat.Status = s);
        Chat.CanSend = client.Token is not null;
        client.Start();
        return client;
    }

    private async Task OnAccountChangedAsync()
    {
        // Reconnect chat under the new identity, keeping the open channel.
        if (_chat is not null) await _chat.DisposeAsync();
        _chat = CreateChatClient();
        if (Chat.Channel.Length > 0) await _chat.JoinAsync(Chat.Channel);
        await RefreshStreamsAsync();
    }

    [RelayCommand]
    private async Task OpenChannelAsync(string login)
    {
        login = login.Trim().TrimStart('#').ToLowerInvariant();
        if (login.Length == 0 || login == Chat.Channel) return;
        if (_options.Demo)
        {
            ShowChannelInfo(login);
            return;
        }

        if (_chat is not null)
        {
            if (Chat.Channel.Length > 0) await _chat.PartAsync(Chat.Channel);
            Chat.Clear();
            Chat.Channel = login;
            Chat.Add(ChatMessage.System(login, $"### Joining #{login} ###", DateTimeOffset.Now));
            await _chat.JoinAsync(login);
        }
        _settings.Channel = login;
        ShowChannelInfo(login);
        StartPlayback(login);
    }

    /// <summary>Closes the open channel: leaves chat, stops the player, and
    /// forgets it as the channel to reopen.</summary>
    [RelayCommand]
    private async Task LeaveChannelAsync()
    {
        if (Chat.Channel.Length == 0) return;
        if (_chat is not null) await _chat.PartAsync(Chat.Channel);
        Chat.Clear();
        Chat.Channel = "";
        _settings.Channel = null;
        Player.Channel = "";
        Player.Title = "";
        Player.Game = "";
        StopPlayback();
        Player.Status = "Pick a stream from the list.";
        WindowTitle = "StreamEdge";
        Streams.ClearSelection();
    }

    public bool HasChannel => Chat.Channel.Length > 0;

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Chat.Draft.Trim();
        if (text.Length == 0 || _chat is null || !Chat.CanSend) return;
        Chat.Draft = "";
        await _chat.SendMessageAsync(Chat.Channel, text);
        // Twitch doesn't echo our own PRIVMSG back; show it locally.
        Chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, Chat.Channel,
            Account.Login, Account.Login, null, [], text));
    }

    // ----- Playback -------------------------------------------------------

    /// <summary>Why there's no video ("libmpv not found…"), or null.</summary>
    public string? PlayerUnavailable { get; }

    /// <summary>Called by the window when the video surface's native window
    /// appears (with a player on it) or goes away (null).</summary>
    public void AttachPlayer(IVideoPlayer? player)
    {
        if (_playback is { } old)
        {
            _playback = null;
            old.Stop();
            _ = old.DisposeAsync().AsTask();
        }
        _video = player;
        if (player is null) return;

        player.SetVolume(_settings.Volume);
        player.SetMute(_settings.Muted);
        var session = new LivePlaybackSession(_http, player) { PreferredQuality = _settings.PreferredQuality };
        session.StatusChanged += () => Player.Apply(session.Status, session.Variants, session.Current);
        // Reader notices ("Ad break…") arrive on the pool; mpv's OSD is thread-safe.
        session.Notice += player.ShowMessage;
        _playback = session;
        if (_playingLogin is { } login) _ = session.StartAsync(login, SecretStore.Get(SecretStore.PlaybackAuthToken));
    }

    /// <summary>The surface was created but mpv failed to start on it.</summary>
    public void PlayerFailed(string message)
    {
        Player.IsVideoVisible = false;
        Player.Status = "Player failed to start: " + message;
    }

    private void StartPlayback(string login)
    {
        _playingLogin = login;
        if (PlayerUnavailable is { } why)
        {
            Player.Status = why;
            return;
        }
        Player.Status = "Resolving stream…";
        if (_playback is { } session)
            _ = session.StartAsync(login, SecretStore.Get(SecretStore.PlaybackAuthToken));
        // First time: showing the surface creates its native window, the
        // window starts mpv on it, and AttachPlayer picks up _playingLogin.
        Player.IsVideoVisible = true;
    }

    private void StopPlayback()
    {
        _playingLogin = null;
        _playback?.Stop();
        Player.IsVideoVisible = false;
        Player.Qualities.Clear();
        Player.Quality = null;
        Player.Latency = "";
        Player.Speed = "";
    }

    private void OnSelectQuality(int index)
    {
        if (_playback is not { } session || index >= session.Variants.Count) return;
        session.SelectQuality(index);
        // Audio-only is a mode, not a preference to start every channel in.
        if (!session.Variants[index].IsAudioOnly)
            _settings.PreferredQuality = index == 0 ? "source" : session.Variants[index].Name;
    }

    private void OnMute(bool muted)
    {
        _settings.Muted = muted;
        _video?.SetMute(muted);
    }

    private void ShowChannelInfo(string login)
    {
        var now = DateTimeOffset.Now;
        var stream = Streams.Items.Select(i => i.Stream)
            .FirstOrDefault(s => string.Equals(s.Login, login, StringComparison.OrdinalIgnoreCase));
        Player.Channel = stream?.DisplayName ?? login;
        Player.Title = stream?.Title ?? "";
        Player.Game = stream?.Game ?? "";
        WindowTitle = stream is null
            ? $"#{login} - StreamEdge"
            : $"#{login} {UiFormats.TitleStats(stream.Viewers, stream.Uptime(now))} - {stream.Title}";
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
            Streams.Set(live, DateTimeOffset.Now);
            Streams.EmptyText = "No followed channels are live.";
            if (Chat.Channel.Length > 0) ShowChannelInfo(Chat.Channel);
        }
        catch (HttpRequestException ex)
        {
            Streams.EmptyText = "Could not load streams: " + ex.Message;
        }
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        Streams.Tick(now);
        if (Chat.Channel.Length > 0) ShowChannelInfo(Chat.Channel);
        if (!_options.Demo && now - _lastStreamRefresh >= StreamRefresh) _ = RefreshStreamsAsync();
    }

    public void SaveSettings() => _settings.Save();

    public async ValueTask DisposeAsync()
    {
        await _clock.DisposeAsync().ConfigureAwait(false);
        await _playbackClock.DisposeAsync().ConfigureAwait(false);
        if (_playback is not null) await _playback.DisposeAsync().ConfigureAwait(false);
        _stop.Cancel();
        if (_chat is not null) await _chat.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _stop.Dispose();
    }
}
