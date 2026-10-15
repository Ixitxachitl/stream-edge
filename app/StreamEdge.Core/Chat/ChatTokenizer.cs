// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.Chat;

public enum ChatTokenKind
{
    /// <summary>Plain text, spaces included; draw as-is.</summary>
    Text,
    /// <summary>An image; <see cref="ChatToken.Emote"/> is set.</summary>
    Emote,
    /// <summary>A clickable http(s) URL; <see cref="ChatToken.Url"/> is set.</summary>
    Link,
    /// <summary>"@name"; <see cref="ChatToken.MentionLogin"/> is set.</summary>
    Mention,
}

/// <summary>One run of a chat line. <see cref="Text"/> is always the
/// original characters, so copying a message or falling back when an image
/// fails to load needs nothing else.</summary>
public sealed record ChatToken(ChatTokenKind Kind, string Text)
{
    public Emote? Emote { get; init; }

    /// <summary>Zero-width emotes stacked on top of <see cref="Emote"/>, in
    /// the order they were typed (draw the first one lowest).</summary>
    public IReadOnlyList<Emote> Overlays { get; init; } = [];

    public Uri? Url { get; init; }

    /// <summary>The mentioned login without '@' or trailing punctuation.</summary>
    public string? MentionLogin { get; init; }

    /// <summary>The mention names the signed-in user; official chat
    /// highlights the whole line for that.</summary>
    public bool IsSelfMention { get; init; }
}

/// <summary>Splits a <see cref="ChatMessage"/> into the runs the chat view
/// draws: text, emotes (Twitch and third-party), links and mentions.</summary>
public static class ChatTokenizer
{
    /// <summary>Tokenizes <paramref name="message"/>. Twitch's own emote
    /// ranges win; the remaining space-separated words are looked up with
    /// <paramref name="thirdPartyLookup"/> (normally
    /// <c>code =&gt; registry.Lookup(roomId, code)</c>).</summary>
    /// <param name="selfLogin">Signed-in login, to flag mentions of it; null when anonymous.</param>
    public static IReadOnlyList<ChatToken> Tokenize(
        ChatMessage message, Func<string, Emote?>? thirdPartyLookup, string? selfLogin)
    {
        var text = message.Text;
        var builder = new Builder();
        var pos = 0;
        foreach (var range in message.TwitchEmotes)
        {
            if (range.Start < pos || range.Start + range.Length > text.Length) continue;
            Words(builder, text, pos, range.Start, thirdPartyLookup, selfLogin);
            var code = text.Substring(range.Start, range.Length);
            builder.AddEmote(new Emote(code, range.ImageUrl, EmoteProvider.Twitch));
            pos = range.Start + range.Length;
        }
        Words(builder, text, pos, text.Length, thirdPartyLookup, selfLogin);
        return builder.Finish();
    }

    private static void Words(Builder b, string text, int from, int to,
        Func<string, Emote?>? lookup, string? selfLogin)
    {
        var i = from;
        while (i < to)
        {
            if (text[i] == ' ')
            {
                b.AddText(" ");
                i++;
                continue;
            }
            var end = text.IndexOf(' ', i, to - i);
            if (end < 0) end = to;
            Word(b, text[i..end], lookup, selfLogin);
            i = end;
        }
    }

    private static void Word(Builder b, string word, Func<string, Emote?>? lookup, string? selfLogin)
    {
        if (lookup?.Invoke(word) is { } emote)
        {
            b.AddEmote(emote);
            return;
        }

        if (word.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || word.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // "see https://x.y/z." — the full stop belongs to the sentence.
            var core = word.TrimEnd('.', ',', '!', '?', ')', ';', ':', '"', '\'');
            if (Uri.TryCreate(core, UriKind.Absolute, out var url) && url.Host.Length > 0)
            {
                b.Add(new ChatToken(ChatTokenKind.Link, core) { Url = url });
                b.AddText(word[core.Length..]);
                return;
            }
        }

        if (word.Length > 1 && word[0] == '@')
        {
            var len = 1;
            while (len < word.Length && (char.IsAsciiLetterOrDigit(word[len]) || word[len] == '_')) len++;
            if (len > 1)
            {
                var login = word[1..len];
                b.Add(new ChatToken(ChatTokenKind.Mention, word[..len])
                {
                    MentionLogin = login,
                    IsSelfMention = selfLogin is not null
                                    && string.Equals(login, selfLogin, StringComparison.OrdinalIgnoreCase),
                });
                b.AddText(word[len..]);
                return;
            }
        }

        b.AddText(word);
    }

    /// <summary>Accumulates tokens, merging adjacent text and folding
    /// zero-width emotes onto the emote before them.</summary>
    private sealed class Builder
    {
        private readonly List<ChatToken> _tokens = [];
        private readonly StringBuilder _text = new();

        public void AddText(string s) => _text.Append(s);

        public void Add(ChatToken token)
        {
            Flush();
            _tokens.Add(token);
        }

        public void AddEmote(Emote emote)
        {
            // Only whitespace may sit between a base emote and its overlay;
            // that space is swallowed so the overlay lands exactly on top.
            if (emote.ZeroWidth
                && _tokens.Count > 0 && _tokens[^1] is { Kind: ChatTokenKind.Emote } previous
                && IsBlank(_text))
            {
                _text.Clear();
                _tokens[^1] = previous with { Overlays = [.. previous.Overlays, emote] };
                return;
            }
            Add(new ChatToken(ChatTokenKind.Emote, emote.Code) { Emote = emote });
        }

        public IReadOnlyList<ChatToken> Finish()
        {
            Flush();
            return _tokens;
        }

        private void Flush()
        {
            if (_text.Length == 0) return;
            _tokens.Add(new ChatToken(ChatTokenKind.Text, _text.ToString()));
            _text.Clear();
        }

        private static bool IsBlank(StringBuilder sb)
        {
            for (var i = 0; i < sb.Length; i++)
                if (!char.IsWhiteSpace(sb[i])) return false;
            return true;
        }
    }
}
