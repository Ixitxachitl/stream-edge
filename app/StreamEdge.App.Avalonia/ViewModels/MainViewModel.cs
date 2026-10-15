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

    private readonly AppSettings _settings;
    private readonly StartupOptions _options;
    private readonly IUiDispatcher _ui;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer _clock;
    private TwitchChatClient? _chat;
    private DateTimeOffset _lastStreamRefresh = DateTimeOffset.MinValue;

    public MainViewModel(AppSettings settings, StartupOptions options, IUiDispatcher ui)
    {
        _settings = settings;
        _options = options;
        _ui = ui;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamEdge/0.1");
        Chat = new ChatViewModel(settings);
        Streams = new StreamListViewModel(settings);
        Player = new PlayerViewModel();
        Account = new AccountViewModel(settings, _http);
        Account.AccountChanged += () => _ = OnAccountChangedAsync();
        Streams.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StreamListViewModel.Selected) && Streams.Selected is { } s)
                OpenChannelCommand.Execute(s.Stream.Login);
        };

        if (options.Demo) LoadDemo();
        else _ = StartAsync();

        _clock = new Timer(_ => _ui.Post(Tick), null, ClockTick, ClockTick);
    }

    public ChatViewModel Chat { get; }
    public StreamListViewModel Streams { get; }
    public PlayerViewModel Player { get; }
    public AccountViewModel Account { get; }

    public double ChatShare
    {
        get => _settings.ChatShare;
        set => SetProperty(_settings.ChatShare, Math.Clamp(value, 0.1, 0.9), _settings, (s, v) => s.ChatShare = v);
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
        Streams.Set(SampleData.Streams(now), now);
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
        if ((_options.Channel ?? _settings.Channel) is { Length: > 0 } channel)
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
        await ResolvePlaybackAsync(login);
    }

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

    private async Task ResolvePlaybackAsync(string login)
    {
        Player.Playlist = null;
        var authToken = SecretStore.Get(SecretStore.PlaybackAuthToken);
        Player.Status = "Resolving stream…";
        try
        {
            var token = await new TwitchPlayback(_http).GetAccessTokenAsync(login, authToken, _stop.Token);
            if (token is null)
            {
                Player.Status = "Offline or unavailable.";
                return;
            }
            Player.Playlist = TwitchPlayback.UsherUrl(login, token);
            Player.AdFree = authToken is not null;
            Player.Status = authToken is null
                ? "Stream resolved (anonymous; ads possible)."
                : "Stream resolved with your auth-token (ad-free if subbed/Turbo).";
        }
        catch (HttpRequestException ex)
        {
            Player.Status = "Could not resolve stream: " + ex.Message;
        }
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
        if (!Account.IsSignedIn || _settings.ClientId is null || Account.UserId is null
            || Account.AccessToken is not { } token)
        {
            Streams.Set([], DateTimeOffset.Now);
            Streams.EmptyText = _settings.ClientId is null
                ? "Set a Twitch Client ID and sign in (⚙) to see followed channels."
                : "Sign in (⚙) to see live followed channels.";
            return;
        }
        try
        {
            var helix = new HelixClient(_http, _settings.ClientId, token);
            var live = await helix.GetFollowedStreamsAsync(Account.UserId,
                new HashSet<string>(_settings.Favorites, StringComparer.OrdinalIgnoreCase), _stop.Token);
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
        await _clock.DisposeAsync();
        _stop.Cancel();
        if (_chat is not null) await _chat.DisposeAsync();
        _http.Dispose();
        _stop.Dispose();
    }
}
