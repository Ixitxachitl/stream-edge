// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Playback;

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

    [ObservableProperty]
    private string _channel = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _game = "";

    [ObservableProperty]
    private string _status = "Pick a stream from the list.";

    /// <summary>Show the native video surface. Off while there's nothing to
    /// play, so the placeholder text underneath is visible.</summary>
    [ObservableProperty]
    private bool _isVideoVisible;

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
    [NotifyPropertyChangedFor(nameof(MuteGlyph))]
    private bool _muted;

    public string QualityLabel => (Quality ?? "Quality") + " ▾";
    public string MuteGlyph => Muted ? "🔇" : "🔊";

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
        // Stopped, and hiding the surface for that blink could tear down the
        // native window mpv is drawing into.
        if (status.State == PlaybackState.Offline) IsVideoVisible = false;
        else if (status.State == PlaybackState.Playing) IsVideoVisible = true;

        if (Qualities.Count != variants.Count || Qualities.Any(q => q.IsCurrent != (q.Index < variants.Count && variants[q.Index] == current)))
        {
            Qualities.Clear();
            for (var i = 0; i < variants.Count; i++)
                Qualities.Add(new QualityOption(variants[i].Name, i, variants[i] == current, SelectQualityCommand));
        }
    }
}
