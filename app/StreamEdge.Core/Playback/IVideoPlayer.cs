// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Playback;

/// <summary>What <see cref="LivePlaybackSession"/> needs from a player.
/// Implemented over libmpv by <see cref="Mpv.MpvPlayer"/>; tests fake it.</summary>
public interface IVideoPlayer
{
    /// <summary>Starts playing a URL, replacing whatever was playing.</summary>
    void Load(Uri url);

    void Stop();

    /// <summary>Playback rate; audio stays pitch-corrected.</summary>
    void SetSpeed(double speed);

    VideoPlayerStats GetStats();

    /// <summary>Brief on-video message ("Jumping to live").</summary>
    void ShowMessage(string text);

    void SetVolume(int percent);

    void SetMute(bool mute);

    /// <summary>Text that stays on the video until cleared (null), e.g.
    /// "Ad break" while ads are being skipped and the picture is black.</summary>
    void SetStatusText(string? text);
}

/// <param name="BufferSeconds">Media buffered ahead of the playhead.</param>
/// <param name="Stalled">Paused waiting for data.</param>
/// <param name="Idle">Nothing loaded: never started, or the stream ended.</param>
public readonly record struct VideoPlayerStats(double BufferSeconds, bool Stalled, bool Idle);
