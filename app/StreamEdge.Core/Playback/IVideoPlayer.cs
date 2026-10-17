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

    /// <summary>Seconds buffered before playback starts, or resumes after a stall.</summary>
    void SetResumeBuffer(double seconds);

    /// <summary>Brief on-video message ("Jumping to live").</summary>
    void ShowMessage(string text);

    void SetVolume(int percent);

    void SetMute(bool mute);

    /// <summary>Closed captions on or off (CEA-608/708 in the video stream).</summary>
    void SetCaptions(bool on);

    /// <summary>Text that stays on the video until cleared (null), e.g.
    /// "Ad break" while ads are being skipped and the picture is black.</summary>
    void SetStatusText(string? text);

    /// <summary>The stream-info strip along the bottom of the video, shown
    /// while the mouse is over it; null hides it.</summary>
    void SetInfoStrip(InfoStrip? strip);
}

/// <param name="BufferSeconds">Media buffered ahead of the playhead.</param>
/// <param name="Stalled">Paused waiting for data.</param>
/// <param name="Idle">Nothing loaded: never started, or the stream ended.</param>
public readonly record struct VideoPlayerStats(double BufferSeconds, bool Stalled, bool Idle);
