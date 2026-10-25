// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace StreamEdge.Chat;

/// <summary>Spell check for the chat box, as a browser underlines words:
/// Hunspell with the en_US dictionary, plus chat's own words and the user's.
/// Leaves alone what isn't prose: emotes and names (asked of the caller),
/// @mentions, links, a /command, anything with digits or underscores, and
/// short all-capital words (acronyms, shouted emote names).</summary>
public sealed partial class ChatSpelling
{
    private readonly WordList _dictionary;
    private readonly HashSet<string> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _known = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private ChatSpelling(WordList dictionary) => _dictionary = dictionary;

    /// <summary>Most suggestions offered for a word.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>Words chat uses that a dictionary doesn't know.</summary>
    public static readonly IReadOnlySet<string> ChatWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "lol", "lmao", "lmfao", "rofl", "gg", "ggs", "wp", "gl", "hf", "glhf", "ez", "pog", "poggers", "pogchamp",
        "kekw", "kek", "lul", "omegalul", "xd", "ty", "tysm", "thx", "np", "brb", "afk", "irl", "ngl", "tbh",
        "imo", "imho", "idk", "idc", "ikr", "iirc", "btw", "fyi", "omg", "omfg", "wtf", "smh", "fr", "pls", "plz",
        "ppl", "rn", "nvm", "ofc", "tho", "ya", "yea", "yeah", "yep", "yup", "nope", "nah", "ok", "okay", "lemme",
        "gonna", "wanna", "gotta", "kinda", "sorta", "dunno", "cuz", "coz", "bc", "bro", "bruh", "sus", "ahh",
        "hmm", "hmmm", "uh", "um", "uhh", "umm", "oof", "yikes", "yay", "woo", "woohoo", "hype", "hyped",
        "streamer", "streamers", "streaming", "chatter", "chatters", "lurk", "lurker", "lurkers", "lurking",
        "emote", "emotes", "sub", "subs", "subbed", "subbing", "resub", "resubs", "gifted", "mod", "mods",
        "modded", "vod", "vods", "vip", "vips", "raid", "raided", "raiding", "clip", "clipped", "followage",
        "sadge", "copium", "hopium", "monkas", "pepega", "widepeepohappy", "catjam", "gigachad", "based",
        "cringe", "noob", "noobs", "nerf", "nerfed", "buff", "buffed", "op", "meta", "speedrun", "speedrunner",
        "speedrunning", "pb", "pbs", "wr", "rng", "dps", "npc", "npcs", "fps", "dlc", "pvp", "pve", "mmo",
        "gamer", "gamers", "esports", "twitch", "youtube", "discord", "reddit", "tiktok", "insta",
    };

    /// <summary>A checker with no words of the user's. The bundled
    /// dictionary is read the first time (a moment's work: do it off the UI
    /// thread) and shared after.</summary>
    public static ChatSpelling Load() => new(Dictionary.Value);

    private static readonly Lazy<WordList> Dictionary = new(() =>
    {
        var assembly = typeof(ChatSpelling).Assembly;
        using var dic = assembly.GetManifestResourceStream("StreamEdge.Dictionaries.en_US.dic")!;
        using var aff = assembly.GetManifestResourceStream("StreamEdge.Dictionaries.en_US.aff")!;
        return WordList.CreateFromStreams(dic, aff);
    });

    /// <summary>The user's own words ("Add to dictionary"), replacing any before.</summary>
    public void SetOwnWords(IEnumerable<string> words)
    {
        lock (_gate)
        {
            _own.Clear();
            _own.UnionWith(words);
        }
    }

    public void AddOwnWord(string word)
    {
        lock (_gate) _own.Add(word);
    }

    /// <summary>A word as spelled: in the dictionary, chat's words or the user's.</summary>
    public bool IsKnown(string word)
    {
        lock (_gate)
        {
            if (_own.Contains(word) || ChatWords.Contains(word)) return true;
            if (_known.TryGetValue(word, out var known)) return known;
            if (_known.Count > 10_000) _known.Clear();
            // Curly apostrophes, as phones type them, spelled as the dictionary has them.
            return _known[word] = _dictionary.Check(word.Replace('’', '\''));
        }
    }

    /// <summary>Spellings for a misspelled word, best first.</summary>
    public IReadOnlyList<string> Suggest(string word)
    {
        lock (_gate) return [.. _dictionary.Suggest(word.Replace('’', '\'')).Take(MaxSuggestions)];
    }

    /// <summary>The misspelled words in a message, as (start, length).</summary>
    /// <param name="caret">Where the user is typing: the word ending there at
    /// the end of the text is still being written, so not judged yet.</param>
    /// <param name="isSpecial">Words that are fine whatever their spelling:
    /// emote codes, people's names. Asked of each whole space-separated
    /// token and of each word in it.</param>
    public IReadOnlyList<(int Start, int Length)> Misspelled(string text, int caret, Func<string, bool>? isSpecial = null)
    {
        var found = new List<(int, int)>();
        // A command's first argument is mostly someone's name (/w, /ban); in
        // /me and /announce it's the message.
        var command = text.StartsWith('/') ? text[1..].Split(' ', 2)[0] : null;
        var skipNext = command is not null && command != "me" && !command.StartsWith("announce", StringComparison.Ordinal);
        var index = 0;
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            var token = text[start..i];
            if (index++ == 1 && skipNext || Skips(token, first: start == 0, isSpecial)) continue;
            foreach (Match word in Word().Matches(token))
            {
                var at = start + word.Index;
                if (at + word.Length == text.Length && caret == text.Length) continue;
                if (IsAcronym(word.Value) || isSpecial?.Invoke(word.Value) == true || IsKnown(word.Value)) continue;
                found.Add((at, word.Length));
            }
        }
        return found;
    }

    /// <summary>A token that isn't prose, so none of it is checked.</summary>
    private static bool Skips(string token, bool first, Func<string, bool>? isSpecial)
    {
        if (token[0] is '@' or '#' or ':' || first && token[0] == '/') return true;
        if (token.Any(c => char.IsDigit(c) || c == '_')) return true;
        if (token.Contains("://", StringComparison.Ordinal) || Link().IsMatch(token)) return true;
        return isSpecial is not null && (isSpecial(token) || isSpecial(token.Trim(TrimmedPunctuation)));
    }

    private static readonly char[] TrimmedPunctuation = ['.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '…'];

    /// <summary>"LOL", "OMEGALUL", "NA": all capitals, up to eight letters.</summary>
    private static bool IsAcronym(string word) => word.Length <= 8 && word.All(c => !char.IsLetter(c) || char.IsUpper(c));

    /// <summary>Letters, with apostrophes inside ("don't", "rock’n’roll").</summary>
    [GeneratedRegex(@"\p{L}+(?:['’]\p{L}+)*")]
    private static partial Regex Word();

    /// <summary>A bare domain or address, such as "twitch.tv/name".</summary>
    [GeneratedRegex(@"^(www\.)?[\p{L}-]+(\.[\p{L}-]+)*\.[a-z]{2,}(/\S*)?[.,!?]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}
