// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ModerationTests
{
    /// <summary>Records each Helix call and answers with a canned reply.</summary>
    private sealed class FakeHelix(HttpStatusCode status = HttpStatusCode.NoContent, string body = "") : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Body)> Calls = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static TwitchModeration Mod(FakeHelix helix) => new(new HttpClient(helix), "cid", "tok", "me");

    [Fact]
    public async Task TimeoutIsABanWithADuration()
    {
        var helix = new FakeHelix();
        var result = await Mod(helix).BanAsync("chan", "u1", 600, "spam", CancellationToken.None);

        Assert.True(result.Ok);
        var (method, url, body) = Assert.Single(helix.Calls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Contains("moderation/bans?broadcaster_id=chan&moderator_id=me", url);
        Assert.Contains("\"duration\":600", body);
        Assert.Contains("\"reason\":\"spam\"", body);
    }

    [Fact]
    public async Task APermanentBanHasNoDuration()
    {
        var helix = new FakeHelix();
        await Mod(helix).BanAsync("chan", "u1", null, null, CancellationToken.None);
        Assert.DoesNotContain("duration", helix.Calls[0].Body);
    }

    [Fact]
    public async Task DeleteTargetsTheMessage()
    {
        var helix = new FakeHelix();
        await Mod(helix).DeleteMessageAsync("chan", "msg-1", CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, helix.Calls[0].Method);
        Assert.EndsWith("moderation/chat?broadcaster_id=chan&moderator_id=me&message_id=msg-1", helix.Calls[0].Url);
    }

    [Fact]
    public async Task TwitchsReasonComesBackOnFailure()
    {
        var helix = new FakeHelix(HttpStatusCode.BadRequest,
            """{"error":"Bad Request","status":400,"message":"The user specified in the user_id field is already banned."}""");
        var result = await Mod(helix).BanAsync("chan", "u1", null, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("The user specified in the user_id field is already banned.", result.Error);
    }

    [Fact]
    public async Task ChatSettingsRoundTrip()
    {
        var helix = new FakeHelix();
        await Mod(helix).SetChatSettingsAsync("chan", new ChatSettings(30, null, true, false, false), CancellationToken.None);
        var body = helix.Calls[0].Body!;
        Assert.Contains("\"slow_mode\":true", body);
        Assert.Contains("\"slow_mode_wait_time\":30", body);
        Assert.Contains("\"follower_mode\":false", body);
        Assert.Contains("\"subscriber_mode\":true", body);

        var parsed = TwitchModeration.ParseChatSettings("""
            {"data":[{"broadcaster_id":"1","slow_mode":true,"slow_mode_wait_time":10,"follower_mode":true,
              "follower_mode_duration":60,"subscriber_mode":false,"emote_mode":true,"unique_chat_mode":false}]}
            """);
        Assert.Equal(new ChatSettings(10, 60, false, true, false), parsed);
    }

    [Fact]
    public void UserCardParsesTheProfile()
    {
        var card = UserCard.Parse("""
            {"data":{"user":{"id":"487510275","login":"lofigirl","displayName":"LofiGirl","description":"Study - Chill",
              "createdAt":"2020-01-23T12:13:35.227695Z","profileImageURL":"https://static-cdn.jtvnw.net/x-150x150.png",
              "roles":{"isPartner":true,"isAffiliate":false},"followers":{"totalCount":72731}}}}
            """);
        Assert.NotNull(card);
        Assert.Equal("LofiGirl", card.DisplayName);
        Assert.True(card.IsPartner);
        Assert.Equal(72731, card.Followers);
        Assert.Equal(2020, card.CreatedAt?.Year);
    }

    [Fact]
    public void FollowDateComesFromHelix() =>
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
            UserCard.ParseFollowedAt("""{"total":1,"data":[{"user_id":"1","followed_at":"2024-03-01T00:00:00Z"}]}"""));

    [Fact]
    public void NotFollowingIsNull() =>
        Assert.Null(UserCard.ParseFollowedAt("""{"total":0,"data":[]}"""));

    [Theory]
    [InlineData("2026-10-15", "today")]
    [InlineData("2026-10-10", "5 days")]
    [InlineData("2026-07-01", "3 months")]
    [InlineData("2025-10-01", "1 year")]
    [InlineData("2020-01-23", "6 years, 8 months")]
    public void AgesReadLikeTwitchs(string since, string expected) =>
        Assert.Equal(expected, UserCard.Age(DateTimeOffset.Parse(since + "T00:00:00Z"),
            new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));
}
