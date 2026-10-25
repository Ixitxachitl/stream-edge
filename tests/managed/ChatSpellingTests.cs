// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Chat;
using Xunit;

namespace StreamEdge.Tests;

public class ChatSpellingTests
{
    // Loading the dictionary takes a moment: once for every test.
    private static readonly ChatSpelling Spelling = ChatSpelling.Load();

    private static List<string> Wrong(string text, int? caret = null, Func<string, bool>? special = null) =>
        [.. Spelling.Misspelled(text, caret ?? -1, special).Select(w => text.Substring(w.Start, w.Length))];

    [Fact]
    public void UnderlinesMisspelledWordsOnly()
    {
        Assert.Equal(["teh", "recieve"], Wrong("did you get teh thing I sent? I didn't recieve it"));
        Assert.Empty(Wrong("Don't worry, it’s fine. Can't wait!"));
    }

    [Fact]
    public void LeavesWhatIsNotProseAlone()
    {
        // Mentions, links, a command, names with digits or underscores,
        // acronyms and chat's own words.
        Assert.Empty(Wrong("/timeout sumdood @glowngsun twitch.tv/xyzzq https://exmpl.cmo/abc gamer_xyz qwz42"));
        Assert.Empty(Wrong("OMEGALUL KEKW lol pog that was ez gg"));
        // Emotes and chatters' names, as the chat box knows them.
        string[] special = ["PogChamp", "catJAM", "Glowngsun"];
        Assert.Empty(Wrong("PogChamp catJAM thanks Glowngsun!", special: special.Contains));
    }

    [Fact]
    public void TheWordBeingTypedWaits()
    {
        Assert.Empty(Wrong("hello wrl", caret: 9));
        Assert.Equal(["wrl"], Wrong("hello wrl", caret: 3));
        Assert.Equal(["wrl"], Wrong("hello wrl ", caret: 10));
    }

    [Fact]
    public void SuggestsAndLearnsWords()
    {
        Assert.Contains("receive", Spelling.Suggest("recieve"));
        Assert.True(Spelling.Suggest("recieve").Count <= ChatSpelling.MaxSuggestions);

        Assert.Equal(["blorpus"], Wrong("blorpus"));
        Spelling.AddOwnWord("blorpus");
        Assert.Empty(Wrong("blorpus Blorpus"));
        Spelling.SetOwnWords([]);
        Assert.Equal(["blorpus"], Wrong("blorpus"));
    }
}
