// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class ResubShareTests
{
    // Shaped like the GQL replies (fields as the schema accepts them); not
    // captured responses.
    [Fact]
    public void AWaitingResubIsRead()
    {
        var token = ResubShare.ParseToken("""
            {"data":{"user":{"self":{"resubNotification":{"id":"dG9rZW4=","cumulativeTenureMonths":14,
             "streakTenureMonths":6,"isGiftSubscription":true,"gifter":{"displayName":"Amy"}}}}}}
            """)!;
        Assert.Equal(new ResubToken("dG9rZW4=", 14, 6, true, "Amy"), token);
    }

    [Theory]
    [InlineData("""{"data":{"user":{"self":{"resubNotification":null}}}}""")]
    [InlineData("""{"data":{"user":{"self":null}}}""")]
    [InlineData("not json")]
    public void NoResubWaitingIsNull(string json) => Assert.Null(ResubShare.ParseToken(json));

    [Fact]
    public void SharingSaysWhyTwitchRefused()
    {
        Assert.Null(ResubShare.ParseShare("""{"data":{"useChatNotificationToken":{"isSuccess":true}}}"""));
        Assert.Equal("unauthenticated", ResubShare.ParseShare(
            """{"errors":[{"message":"unauthenticated"}],"data":{"useChatNotificationToken":null}}"""));
        Assert.Equal("Twitch refused.", ResubShare.ParseShare("""{"data":{"useChatNotificationToken":{"isSuccess":false}}}"""));
    }
}
