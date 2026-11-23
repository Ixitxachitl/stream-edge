// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class StreamEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static LiveStream Live(string login, string title = "t") => new(login, login.ToUpperInvariant(), title, "g", 5, Now);

    // ----- The socket's messages, as Twitch sent them ---------------------------

    [Fact]
    public void WelcomeSaysHowOftenToExpectSomething()
    {
        var m = StreamEventsClient.Parse("""
            {"welcome":{"keepaliveSec":15,"recoveryUrl":"wss://hermes.twitch.tv/a/v1?clientId=x&t=y","sessionId":"737e176d"},
             "id":"1","type":"welcome","timestamp":"2026-11-23T12:46:25.1Z"}
            """)!;
        Assert.Equal(("welcome", 15), (m.Type, m.KeepaliveSeconds));
    }

    [Fact]
    public void ResponsesSayWhichSubscriptionAndWhetherItTook()
    {
        var ok = StreamEventsClient.Parse("""
            {"subscribeResponse":{"subscription":{"id":"b95c2b93"},"result":"ok"},"id":"fb5ee69b","parentId":"c70a6ff8",
             "type":"subscribeResponse","timestamp":"2026-11-23T12:46:25.307298055Z"}
            """)!;
        Assert.Equal(("subscribeResponse", "b95c2b93", true), (ok.Type, ok.SubscriptionId, ok.Ok));

        // Past the 150th on a connection.
        var full = StreamEventsClient.Parse("""
            {"subscribeResponse":{"subscription":{"id":"e2b8af16"},"result":"error","error":"too many subscriptions","errorCode":"SUB006"},
             "id":"5","parentId":"9","type":"subscribeResponse","timestamp":"2026-11-23T12:46:26.3Z"}
            """)!;
        Assert.Equal(("e2b8af16", false), (full.SubscriptionId, full.Ok));

        var gone = StreamEventsClient.Parse("""
            {"unsubscribeResponse":{"subscription":{"id":"b95c2b93"},"result":"ok"},"id":"a","parentId":"b","type":"unsubscribeResponse","timestamp":"t"}
            """)!;
        Assert.Equal(("unsubscribeResponse", true), (gone.Type, gone.Ok));
    }

    [Theory]
    [InlineData("""{\"server_time\":1795438012,\"type\":\"stream-down\"}""", "stream-down")]
    [InlineData("""{\"server_time\":1795438012,\"play_delay\":0,\"type\":\"stream-up\"}""", "stream-up")]
    [InlineData("""{\"type\":\"viewcount\",\"server_time\":1795437681.047754,\"viewers\":104,\"collaboration_status\":\"none\"}""", "viewcount")]
    public void AnEventIsJsonInsideAString(string inner, string kind)
    {
        var m = StreamEventsClient.Parse(
            """{"notification":{"subscription":{"id":"bafe8d9c"},"type":"pubsub","pubsub":"INNER"},"id":"1","type":"notification","timestamp":"t"}"""
                .Replace("INNER", inner))!;
        Assert.Equal(("notification", "bafe8d9c", kind), (m.Type, m.SubscriptionId, m.Event));
    }

    [Fact]
    public void AnythingElseIsLeftAlone()
    {
        Assert.Equal("keepalive", StreamEventsClient.Parse("""{"id":"1","type":"keepalive","timestamp":"t"}""")!.Type);
        Assert.Equal("reconnect", StreamEventsClient.Parse("""{"type":"reconnect","reconnect":{"url":"wss://elsewhere"}}""")!.Type);
        Assert.Null(StreamEventsClient.Parse("not json"));
        Assert.Null(StreamEventsClient.Parse("""{"no":"type"}"""));
        // An event whose inside isn't JSON isn't one.
        Assert.Null(StreamEventsClient.Parse("""{"type":"notification","notification":{"subscription":{"id":"s"},"pubsub":"nope"}}"""));
    }

    [Fact]
    public void RequestsCarryAnIdTheTimeAndTheChannelsTopic()
    {
        var subscribe = JsonNode.Parse(StreamEventsClient.Subscribe("sub-1", "153859298"))!;
        Assert.Equal("subscribe", subscribe["type"]!.GetValue<string>());
        Assert.True(Guid.TryParse(subscribe["id"]!.GetValue<string>(), out _));
        Assert.True(DateTimeOffset.TryParse(subscribe["timestamp"]!.GetValue<string>(), out _));
        Assert.Equal("sub-1", subscribe["subscribe"]!["id"]!.GetValue<string>());
        Assert.Equal("pubsub", subscribe["subscribe"]!["type"]!.GetValue<string>());
        Assert.Equal("video-playback-by-id.153859298", subscribe["subscribe"]!["pubsub"]!["topic"]!.GetValue<string>());

        var unsubscribe = JsonNode.Parse(StreamEventsClient.Unsubscribe("sub-1"))!;
        Assert.Equal("unsubscribe", unsubscribe["type"]!.GetValue<string>());
        Assert.Equal("sub-1", unsubscribe["unsubscribe"]!["id"]!.GetValue<string>());
    }

    // ----- Events against a poll that runs behind them --------------------------

    [Fact]
    public void AChannelThatWentLiveIsListedBeforeThePollHasIt()
    {
        var events = new RecentStreamEvents();
        events.WentLive(Live("fresh", "from the event"), Now);
        events.WentLive(Live("stranger"), Now);

        // Added after the polled ones; one that isn't this list's is left out.
        var listed = events.Apply([Live("old")], login => login != "stranger", Now.AddSeconds(30));
        Assert.Equal(["old", "fresh"], listed.Select(s => s.Login));

        // The poll catches up: its row from then on, whatever comes after.
        listed = events.Apply([Live("old"), Live("fresh", "from the poll")], _ => true, Now.AddSeconds(90));
        Assert.Equal("from the poll", listed.Single(s => s.Login == "fresh").Title);
        Assert.Equal(["old"], events.Apply([Live("old")], login => login != "stranger", Now.AddSeconds(150)).Select(s => s.Login));
    }

    [Fact]
    public void AChannelThatWentOfflineIsDroppedWhileThePollStillListsIt()
    {
        var events = new RecentStreamEvents();
        events.WentOffline("ended", Now);
        Assert.Equal(["old"], events.Apply([Live("old"), Live("ended")], _ => true, Now.AddSeconds(30)).Select(s => s.Login));

        // The poll agrees, and later it's live again with no event: believed.
        Assert.Equal(["old"], events.Apply([Live("old")], _ => true, Now.AddSeconds(90)).Select(s => s.Login));
        Assert.Equal(["old", "ended"], events.Apply([Live("old"), Live("ended")], _ => true, Now.AddSeconds(150)).Select(s => s.Login));
    }

    [Fact]
    public void AnEventThePollNeverBearsOutIsLetGo()
    {
        var events = new RecentStreamEvents();
        events.WentLive(Live("ghost"), Now);
        events.WentOffline("stuck", Now);
        var late = Now + RecentStreamEvents.Hold + TimeSpan.FromSeconds(1);
        Assert.Equal(["stuck"], events.Apply([Live("stuck")], _ => true, late).Select(s => s.Login));
    }

    [Fact]
    public void TheLatestEventForAChannelCounts()
    {
        var events = new RecentStreamEvents();
        events.WentOffline("flaky", Now);
        events.WentLive(Live("flaky", "back"), Now.AddSeconds(20));
        Assert.Equal("back", events.Live("flaky", null, Now.AddSeconds(25))?.Title);

        events.WentOffline("FLAKY", Now.AddSeconds(40));
        Assert.Null(events.Live("flaky", Live("flaky"), Now.AddSeconds(45)));
        // A row that isn't live is no row.
        Assert.Null(events.Live("other", Live("other") with { IsLive = false }, Now));
    }
}
