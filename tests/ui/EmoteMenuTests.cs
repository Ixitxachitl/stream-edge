// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class EmoteMenuTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static Emote E(string code) => new(code, new Uri($"https://img.example/{code}"), EmoteProvider.Twitch);

    private static MainViewModel Demo()
    {
        var vm = new MainViewModel(new AppSettings { ShowPronouns = false }, new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        vm.FrontTab!.Chat.RoomId = "100";
        vm.Services.SetMyEmotes(
        [
            new UserEmote(E("chanHype"), "100", "subscriptions"),
            new UserEmote(E("otherWave"), "200", "subscriptions"),
            new UserEmote(E("Kappa"), "0", "globals"),
        ],
        new Dictionary<string, EmoteOwner>
        {
            ["100"] = new("100", "PixelPeregrine", null),
            ["200"] = new("200", "OtherStreamer", null),
        }, replace: true);
        return vm;
    }

    private static List<string> Headers(EmoteMenuViewModel menu) =>
        [.. menu.Rows.OfType<EmoteMenuHeader>().Select(h => h.Title)];

    private static List<string> Codes(EmoteMenuViewModel menu) =>
        [.. menu.Rows.OfType<EmoteMenuEmotes>().SelectMany(r => r.Items).Select(i => i.Emote.Code)];

    [Fact]
    public void SectionsFollowTwitchsOrder() => Ui(() =>
    {
        var vm = Demo();
        vm.EmoteMenu.Open();
        Assert.Equal(["PixelPeregrine", "OtherStreamer", "Twitch"], Headers(vm.EmoteMenu));
        Assert.Equal(["chanHype", "otherWave", "Kappa"], Codes(vm.EmoteMenu));
    });

    [Fact]
    public void PickingInsertsTheCodeAndRemembersIt() => Ui(() =>
    {
        var vm = Demo();
        vm.Chat.Draft = "hi";
        vm.EmoteMenu.Open();
        var kappa = vm.EmoteMenu.Rows.OfType<EmoteMenuEmotes>().SelectMany(r => r.Items).Single(i => i.Emote.Code == "Kappa");

        kappa.Insert.Execute(kappa.Emote);

        Assert.Equal("hi Kappa ", vm.Chat.Draft);
        vm.EmoteMenu.Open();
        Assert.Equal("Frequently used", Headers(vm.EmoteMenu)[0]);
    });

    [Fact]
    public void SearchFlattensAndPrefersPrefixMatches() => Ui(() =>
    {
        var vm = Demo();
        vm.EmoteMenu.Open();
        vm.EmoteMenu.Search = "hype";
        Assert.Equal(["Results"], Headers(vm.EmoteMenu));
        Assert.Equal(["chanHype"], Codes(vm.EmoteMenu));
    });

    [Fact]
    public void OwnLinesShowOwnTwitchEmotesOthersDoNot() => Ui(() =>
    {
        var vm = Demo();
        var chat = vm.FrontTab!.Chat;
        chat.AddOwn(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", "me", "Me", "#1E90FF",
            ["subscriber/12"], "nice chanHype"));
        chat.Add(new ChatMessage(DateTimeOffset.Now, ChatMessageKind.Message, "pixelperegrine", "bob", "bob", null,
            [], "nice chanHype"));

        Assert.Contains(chat.Lines[^2].Tokens, t => t.Kind == ChatTokenKind.Emote && t.Emote!.Code == "chanHype");
        Assert.DoesNotContain(chat.Lines[^1].Tokens, t => t.Kind == ChatTokenKind.Emote);
        Assert.Equal(["subscriber"], chat.Lines[^2].Badges.Select(b => b.Set));
    });
}
