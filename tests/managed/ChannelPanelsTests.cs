// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

public class ChannelPanelsTests
{
    [Fact]
    public void DefaultPanelsAreKeptAndExtensionsAndEmptyOnesDropped()
    {
        var panels = ChannelPanels.Parse("""
            {"data":{"user":{"panels":[
              {"__typename":"DefaultPanel","title":" Rules ","description":"Be **nice**.\n\n\n\nNo spam.","linkURL":null,"imageURL":"https://panels.twitch.tv/rules.png"},
              {"__typename":"ExtensionPanel"},
              {"__typename":"DefaultPanel","title":null,"description":null,"linkURL":"https://shop.example/","imageURL":null},
              {"__typename":"DefaultPanel","title":"Shop","description":"[Merch here](https://shop.example/) ![logo](x.png)","linkURL":"https://shop.example/","imageURL":null}]}}}
            """);
        Assert.Equal(2, panels.Count);
        var rules = panels[0];
        Assert.Equal("Rules", rules.Title);
        Assert.Equal(new Uri("https://panels.twitch.tv/rules.png"), rules.Image);
        Assert.Equal("Be nice.\n\nNo spam.", rules.Description);
        Assert.Null(rules.Link);
        Assert.Equal([new PanelRun("Be "), new PanelRun("nice", Bold: true), new PanelRun(".\n\nNo spam.")], rules.Runs);
        Assert.Equal("Merch here", panels[1].Description);
        Assert.Equal(new Uri("https://shop.example/"), panels[1].Link);
    }

    [Theory]
    [InlineData("# Schedule\nMon *and* Wed", "Schedule\nMon and Wed")]
    [InlineData("see https://example.com/a_b_c here", "see https://example.com/a_b_c here")]
    [InlineData("__bold__ and ~~gone~~", "bold and gone")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData(null, "")]
    public void PanelMarkdownReadsAsPlainText(string? markdown, string text) =>
        Assert.Equal(text, ChannelPanels.PlainText(markdown));

    [Fact]
    public void PanelMarkdownKeepsBoldLinksAndLineBreaks()
    {
        var runs = ChannelPanels.Format(
            "## Schedule\nMon **7 pm**, see [Discord](https://discord.example/x) or https://clips.example/y\n\n\n\nbye ![pic](a.png)");
        Assert.Equal(
        [
            new PanelRun("Schedule", Bold: true),
            new PanelRun("\nMon "),
            new PanelRun("7 pm", Bold: true),
            new PanelRun(", see "),
            new PanelRun("Discord", new Uri("https://discord.example/x")),
            new PanelRun(" or "),
            new PanelRun("https://clips.example/y", new Uri("https://clips.example/y")),
            new PanelRun("\n\nbye"),
        ], runs);
        Assert.Empty(ChannelPanels.Format(null));
        // A link to anything but the web stays text.
        Assert.Equal([new PanelRun("evil")], ChannelPanels.Format("[evil](javascript:void)"));
    }
}
