// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ChatterRosterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static ChatMessage Say(string login, string display, params string[] badges) =>
        new(Now, ChatMessageKind.Message, "chan", login, display, null, badges, "hi");

    private static ChatMessage Membership(string verb, string login) =>
        new(Now, ChatMessageKind.Membership, "chan", login, null, null, [], $"{verb}: {login}");

    [Fact]
    public void SectionsComeFromBadgesAndTheChannelName()
    {
        var roster = new ChatterRoster("chan");
        roster.Observe(Say("zed", "Zed"));
        roster.Observe(Say("mia", "Mia", "moderator/1", "subscriber/12"));
        roster.Observe(Say("chan", "Chan"));
        roster.Observe(Say("vee", "Vee", "vip/1"));
        roster.Observe(Say("amy", "amy"));

        Assert.Equal(
            [("chan", ChatterRole.Broadcaster), ("mia", ChatterRole.Moderator), ("vee", ChatterRole.Vip),
             ("amy", ChatterRole.User), ("zed", ChatterRole.User)],
            roster.Snapshot().Select(c => (c.Login, c.Role)));
    }

    [Fact]
    public void JoinsAndNamesAddLurkersAndPartsRemoveThem()
    {
        var roster = new ChatterRoster("chan");
        roster.AddNames(["a", "b"]);
        roster.Observe(Membership("JOIN", "c"));
        roster.Observe(Membership("PART", "a"));
        Assert.Equal(["b", "c"], roster.Snapshot().Select(c => c.Login));
    }

    [Fact]
    public void AJoinAfterAMessageKeepsTheRoleAndName()
    {
        var roster = new ChatterRoster("chan");
        roster.Observe(Say("mia", "Mia", "moderator/1"));
        var version = roster.Version;
        roster.Observe(Membership("JOIN", "mia"));
        var mia = roster.Snapshot().Single();
        Assert.Equal(("Mia", ChatterRole.Moderator), (mia.DisplayName, mia.Role));
        Assert.Equal(version, roster.Version);
    }

    [Fact]
    public void LinesWithoutASenderAreIgnored()
    {
        var roster = new ChatterRoster("chan");
        roster.Observe(ChatMessage.System("chan", "### Joining #chan ###", Now));
        Assert.Equal(0, roster.Count);
    }

    [Fact]
    public void HelixListKeepsTheSectionsChatShowed()
    {
        var roster = new ChatterRoster("chan");
        roster.Observe(Say("mia", "Mia", "moderator/1"));
        var all = roster.Merge([("lurker", "Lurker"), ("mia", "Mia"), ("chan", "Chan")]);
        Assert.Equal(
            [("chan", ChatterRole.Broadcaster), ("mia", ChatterRole.Moderator), ("lurker", ChatterRole.User)],
            all.Select(c => (c.Login, c.Role)));
    }

    [Fact]
    public async Task FetchFollowsHelixPages()
    {
        var calls = 0;
        var http = new HttpClient(new FakeHttp(request =>
        {
            calls++;
            var query = request.RequestUri!.Query;
            Assert.Contains("broadcaster_id=10", query);
            Assert.Contains("moderator_id=20", query);
            return query.Contains("after=next")
                ? FakeHttp.Json("""{"data":[{"user_id":"3","user_login":"c","user_name":"C"}],"pagination":{},"total":3}""")
                : FakeHttp.Json("""{"data":[{"user_id":"1","user_login":"a","user_name":"A"},{"user_id":"2","user_login":"b","user_name":""}],"pagination":{"cursor":"next"},"total":3}""");
        }));
        var all = await ChatterRoster.FetchAsync(http, "client", "token", "10", "20", CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal([("a", "A"), ("b", "b"), ("c", "C")], all!);
    }

    [Fact]
    public async Task FetchIsNullWhenTwitchRefuses()
    {
        var http = new HttpClient(new FakeHttp(_ => FakeHttp.Status(System.Net.HttpStatusCode.Forbidden)));
        Assert.Null(await ChatterRoster.FetchAsync(http, "client", "token", "10", "20", CancellationToken.None));
    }
}
