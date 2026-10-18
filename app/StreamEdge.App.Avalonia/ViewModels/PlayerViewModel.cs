// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Playback;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>One entry in the quality menu. Carries its command because the
/// menu is a popup, outside the window's tree, so it can't bind upward.</summary>
public sealed record QualityOption(string Name, int Index, bool IsCurrent, System.Windows.Input.ICommand Select);

/// <summary>State for the centre pane and the player controls in the chat
/// header.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    /// <summary>Set by MainViewModel; null in demo mode or before mpv exists.</summary>
    internal Action<int>? SelectQualityHandler { get; set; }
    internal Action<bool>? MuteHandler { get; set; }
    internal Action<bool>? CaptionsHandler { get; set; }

    [ObservableProperty]
    private string _channel = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _game = "";

    [ObservableProperty]
    private string _status = "Pick a stream from the list.";

    /// <summary>Viewer count from the live list, -1 when unknown.</summary>
    internal int Viewers { get; set; } = -1;

    internal DateTimeOffset StartedAt { get; set; }

    /// <summary>What the strip over a hovered video shows, as of now.</summary>
    internal InfoStrip InfoStrip(DateTimeOffset now) => new(Channel, Title, Game,
        Viewers < 0 ? null : $"{UiFormats.Viewers(Viewers)} viewers   ·   {UiFormats.Uptime(now - StartedAt)}");

    /// <summary>Show the native video surface. Off while there's nothing to
    /// play, so the placeholder text underneath is visible.</summary>
    [ObservableProperty]
    private bool _isVideoVisible;

    /// <summary>The channel isn't live: show its offline banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBanner))]
    private bool _isOffline;

    /// <summary>The channel's offline banner, or null if it has none (the
    /// avatar is shown instead).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOfflineImage), nameof(ShowsBanner))]
    private Uri? _offlineImage;

    [ObservableProperty]
    private Uri? _profileImage;

    public bool HasOfflineImage => OfflineImage is not null;

    /// <summary>Offline with a banner to show (the text moves aside for it).</summary>
    public bool ShowsBanner => IsOffline && HasOfflineImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityLabel))]
    private string? _quality;

    /// <summary>"1.9s" behind the broadcaster, "" when unknown.</summary>
    [ObservableProperty]
    private string _latency = "";

    /// <summary>"1.15×" while catching up, "" at normal speed.</summary>
    [ObservableProperty]
    private string _speed = "";

    [ObservableProperty]
    private bool _adFree;

    [ObservableProperty]
    private bool _muted;

    public string QualityLabel => (Quality ?? "Quality") + " ▾";

    /// <summary>Closed captions (the CC button). Shown only when the stream
    /// carries them; many don't.</summary>
    [ObservableProperty]
    private bool _captions;

    partial void OnCaptionsChanged(bool value) => CaptionsHandler?.Invoke(value);

    public ObservableCollection<QualityOption> Qualities { get; } = [];

    [RelayCommand]
    private void SelectQuality(QualityOption option) => SelectQualityHandler?.Invoke(option.Index);

    [RelayCommand]
    private void ToggleMute()
    {
        Muted = !Muted;
        MuteHandler?.Invoke(Muted);
    }

    /// <summary>Copies a session snapshot into bindable properties.</summary>
    public void Apply(PlaybackStatus status, IReadOnlyList<HlsVariant> variants, HlsVariant? current)
    {
        Quality = status.Quality;
        AdFree = status.AdFree;
        Latency = status.LatencySeconds is { } l ? l.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "";
        Speed = status.Speed > 1.001 ? status.Speed.ToString("0.00", CultureInfo.InvariantCulture) + "×" : "";
        Status = status.State switch
        {
            PlaybackState.Offline => "Offline.",
            PlaybackState.Error or PlaybackState.Resolving => status.Message,
            _ => Status,
        };
        // Only the settled states change visibility: a restart passes through
        // Stopped, and hiding the video for that moment would make it blink.
        IsOffline = status.State == PlaybackState.Offline;
        if (status.State == PlaybackState.Offline) IsVideoVisible = false;
        else if (status.State == PlaybackState.Playing) IsVideoVisible = true;

        if (Qualities.Count != variants.Count || Qualities.Any(q => q.IsCurrent != (q.Index < variants.Count && variants[q.Index] == current)))
        {
            Qualities.Clear();
            for (var i = 0; i < variants.Count; i++)
                // Marked as twitch.tv does: a 720p60 broadcast also gets a
                // 720p60 transcode, and the two would otherwise look the same.
                Qualities.Add(new QualityOption(variants[i].IsSource ? variants[i].Name + " (source)" : variants[i].Name,
                    i, variants[i] == current, SelectQualityCommand));
        }
    }
}
