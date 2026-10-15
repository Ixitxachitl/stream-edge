// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Auth;

/// <summary>StreamEdge's own Twitch application.</summary>
public static class TwitchApp
{
    /// <summary>Registered as a <b>Public</b> client, so it has no secret and
    /// can ship in the binary: every request already carries it in the clear.
    /// Public is what lets device-code tokens refresh without a secret.</summary>
    public const string ClientId = "86lp6yl3alt7ig7ldk2btoboh3rl54";
}
