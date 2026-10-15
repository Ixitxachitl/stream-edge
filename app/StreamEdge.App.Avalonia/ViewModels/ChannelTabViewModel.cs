// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Playback;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>One open channel: its chat, its player, and the stream session
/// feeding that player. The front tab fills the player pane and the chat
/// pane; the others keep playing behind it, as audio, or as PIP thumbnails.</summary>
public sealed partial class ChannelTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Action<ChannelTabViewModel> _select;
    private readonly Action<ChannelTabViewModel> _close;

    public ChannelTabViewModel(string login, AppSettings settings, ChatServices services,
        Action<ChannelTabViewModel> select, Action<ChannelTabViewModel> close)
    {
        Login = login;
        _select = select;
        _close = close;
        Chat = new ChatViewModel(settings, services) { Channel = login };
        _volume = settings.ChannelVolumes.TryGetValue(login, out var volume) ? volume : settings.Volume;
        Player = new PlayerViewModel { Channel = login };
        Player.MuteHandler = muted => Video?.SetMute(muted);
        Player.SelectQualityHandler = index => Session?.SelectQuality(index);
        Player.Captions = settings.Captions;
        Player.CaptionsHandler = on =>
        {
            settings.Captions = on;
            if (Session is { } s) s.Captions = on;
        };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Channel)) OnPropertyChanged(nameof(Title));
        };
    }

    public string Login { get; }

    /// <summary>"LofiGirl" for "lofigirl", once known.</summary>
    public string? DisplayName { get; set; }
    public ChatViewModel Chat { get; }
    public PlayerViewModel Player { get; }

    /// <summary>The name shown on the tab: display name once known.</summary>
    public string Title => Player.Channel.Length > 0 ? Player.Channel : Login;

    [ObservableProperty]
    private bool _isFront;

    /// <summary>When channel points were last asked for.</summary>
    internal DateTimeOffset PointsLoadedAt { get; set; }

    /// <summary>This tab's own volume, 0–100: each tab (PIP thumbnails
    /// included) is its own player. Remembered per channel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeTip))]
    private int _volume;

    public string VolumeTip => $"Volume {Volume}% (scroll over the video)";

    /// <summary>The scroll wheel over this tab's video.</summary>
    public void ChangeVolume(int delta, AppSettings settings)
    {
        Volume = Math.Clamp(Volume + delta, 0, 100);
        settings.ChannelVolumes[Login] = Volume;
        if (Video is { } video)
        {
            video.SetVolume(Volume);
            // Scrolling up from muted means "I want to hear this".
            if (Player.Muted && delta > 0) Player.ToggleMuteCommand.Execute(null);
            video.ShowMessage($"Volume {Volume}%");
        }
    }

    /// <summary>Set once the view has a player on this tab's video surface.</summary>
    internal IVideoPlayer? Video { get; private set; }

    internal LivePlaybackSession? Session { get; private set; }

    /// <summary>Twitch's numeric id for the channel, from ROOMSTATE; what
    /// third-party emote and badge APIs are keyed by.</summary>
    public string? RoomId => Chat.RoomId;

    [RelayCommand]
    private void Select() => _select(this);

    [RelayCommand]
    private void Close() => _close(this);

    /// <summary>Hands this tab a player (or takes it away, null) and starts
    /// a session on it.</summary>
    internal void Attach(IVideoPlayer? player, HttpClient http, AppSettings settings, PlaybackTier tier, string? authToken)
    {
        if (Session is { } old)
        {
            Session = null;
            old.Stop();
            _ = old.DisposeAsync().AsTask();
        }
        Video = player;
        if (player is null) return;

        player.SetVolume(Volume);
        player.SetMute(Player.Muted);
        var session = new LivePlaybackSession(http, player) { PreferredQuality = settings.PreferredQuality, Tier = tier };
        session.StatusChanged += () => Player.Apply(session.Status, session.Variants, session.Current);
        // Reader notices ("Ad break…") arrive on the pool; mpv's OSD is thread-safe.
        session.Notice += player.ShowMessage;
        session.Captions = settings.Captions;
        Session = session;
        Player.Status = "Resolving stream…";
        _ = session.StartAsync(Login, authToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Session is { } session)
        {
            Session = null;
            session.Stop();
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Mouse and keyboard input on a video.</summary>
public enum VideoInput
{
    Click,
    DoubleClick,
    WheelUp,
    WheelDown,
    Escape,
}
