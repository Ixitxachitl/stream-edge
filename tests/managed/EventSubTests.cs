// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class EventSubTests
{
    [Fact]
    public void WelcomeGivesTheSessionAndKeepalive()
    {
        var m = EventSubClient.Parse("""
            {"metadata":{"message_id":"m1","message_type":"session_welcome","message_timestamp":"2026-10-15T19:00:00Z"},
             "payload":{"session":{"id":"AQoQexAWVYKSTIu4ec_2VAxyuhAB","status":"connected","keepalive_timeout_seconds":10,"reconnect_url":null}}}
            """);
        Assert.NotNull(m);
        Assert.Equal("session_welcome", m.Type);
        Assert.Equal("m1", m.Id);
        Assert.Equal("AQoQexAWVYKSTIu4ec_2VAxyuhAB", m.SessionId);
        Assert.Equal(10, m.KeepaliveSeconds);
    }

    [Fact]
    public void ReconnectGivesTheNewUrlButOnlyASecureOne()
    {
        var m = EventSubClient.Parse("""
            {"metadata":{"message_id":"m2","message_type":"session_reconnect"},
             "payload":{"session":{"id":"s","status":"reconnecting","reconnect_url":"wss://eventsub.wss.twitch.tv/ws?reconnect=1"}}}
            """);
        Assert.Equal(new Uri("wss://eventsub.wss.twitch.tv/ws?reconnect=1"), m?.ReconnectUrl);

        var plain = EventSubClient.Parse("""
            {"metadata":{"message_id":"m3","message_type":"session_reconnect"},
             "payload":{"session":{"id":"s","reconnect_url":"http://example.com/ws"}}}
            """);
        Assert.Null(plain?.ReconnectUrl);
    }

    [Fact]
    public void RaidNotificationSaysWhoRaidedWhom()
    {
        var m = EventSubClient.Parse("""
            {"metadata":{"message_id":"m4","message_type":"notification","subscription_type":"channel.raid","subscription_version":"1"},
             "payload":{"subscription":{"id":"sub1","type":"channel.raid"},
               "event":{"from_broadcaster_user_id":"111","from_broadcaster_user_login":"pixelperegrine","from_broadcaster_user_name":"PixelPeregrine",
                        "to_broadcaster_user_id":"222","to_broadcaster_user_login":"cometcrumb","to_broadcaster_user_name":"CometCrumb","viewers":42}}}
            """);
        Assert.Equal(new RaidEvent("111", "pixelperegrine", "222", "cometcrumb", "CometCrumb", 42), m?.Raid);
    }

    [Fact]
    public void OtherMessagesParseWithoutARaid()
    {
        Assert.Null(EventSubClient.Parse("""{"metadata":{"message_id":"k","message_type":"session_keepalive"},"payload":{}}""")?.Raid);
        Assert.Equal("sub1", EventSubClient.Parse("""
            {"metadata":{"message_id":"r","message_type":"revocation"},"payload":{"subscription":{"id":"sub1","status":"user_removed"}}}
            """)?.RevokedSubscription);
        Assert.Null(EventSubClient.Parse("not json"));
        Assert.Null(EventSubClient.Parse("""{"payload":{}}"""));
    }
}
