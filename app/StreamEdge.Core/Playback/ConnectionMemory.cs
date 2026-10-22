// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Playback;

/// <summary>The bitrate the connection last held up at, when that was below
/// the preferred quality, so the next stream starts there rather than at the
/// top and stalling its way down. Null when it last kept up with the
/// preference. Shared by every tab; saved with the settings.</summary>
public sealed class ConnectionMemory
{
    /// <summary>Bits per second, as in the variants' BANDWIDTH.</summary>
    public long? Bandwidth { get; set; }
}
