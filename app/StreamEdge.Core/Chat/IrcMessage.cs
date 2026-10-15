// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace StreamEdge.Chat;

/// <summary>One IRCv3 line as Twitch sends it:
/// <c>@tags :prefix COMMAND params :trailing</c>. Parsing never throws; a
/// malformed line comes back with an empty <see cref="Command"/>.</summary>
public sealed record IrcMessage(
    IReadOnlyDictionary<string, string> Tags,
    string? Prefix,
    string Command,
    IReadOnlyList<string> Params)
{
    private static readonly IReadOnlyDictionary<string, string> NoTags =
        new Dictionary<string, string>();

    private static readonly IrcMessage Empty = new(NoTags, null, "", []);

    /// <summary>Nick part of the prefix ("nick!user@host" → "nick").</summary>
    public string? Nick => Prefix switch
    {
        null => null,
        var p when p.IndexOf('!') is var bang and >= 0 => p[..bang],
        var p => p,
    };

    /// <summary>The last parameter, which for PRIVMSG/NOTICE is the text.</summary>
    public string? Trailing => Params.Count > 0 ? Params[^1] : null;

    /// <summary>A tag's value, or null when it is absent or empty.</summary>
    public string? Tag(string key) => Tags.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public static IrcMessage Parse(string line)
    {
        var span = line.AsSpan().TrimEnd("\r\n");
        IReadOnlyDictionary<string, string> tags = NoTags;
        string? prefix = null;

        if (span.StartsWith("@"))
        {
            var end = span.IndexOf(' ');
            if (end < 0) return Empty;
            tags = ParseTags(span[1..end]);
            span = span[(end + 1)..].TrimStart(' ');
        }

        if (span.StartsWith(":"))
        {
            var end = span.IndexOf(' ');
            if (end < 0) return Empty;
            prefix = span[1..end].ToString();
            span = span[(end + 1)..].TrimStart(' ');
        }

        string command;
        var cmdEnd = span.IndexOf(' ');
        if (cmdEnd < 0)
        {
            command = span.ToString();
            span = default;
        }
        else
        {
            command = span[..cmdEnd].ToString();
            span = span[(cmdEnd + 1)..];
        }

        var parameters = new List<string>();
        while (true)
        {
            span = span.TrimStart(' ');
            if (span.IsEmpty) break;
            if (span[0] == ':')
            {
                parameters.Add(span[1..].ToString());
                break;
            }
            var end = span.IndexOf(' ');
            if (end < 0)
            {
                parameters.Add(span.ToString());
                break;
            }
            parameters.Add(span[..end].ToString());
            span = span[(end + 1)..];
        }

        return new IrcMessage(tags, prefix, command, parameters);
    }

    private static Dictionary<string, string> ParseTags(ReadOnlySpan<char> raw)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in raw.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.AsSpan();
            var eq = pair.IndexOf('=');
            if (eq < 0) tags[pair.ToString()] = "";
            else tags[pair[..eq].ToString()] = Unescape(pair[(eq + 1)..]);
        }
        return tags;
    }

    /// <summary>IRCv3 tag-value escapes: <c>\:</c> → ';', <c>\s</c> → space,
    /// <c>\\</c>, <c>\r</c>, <c>\n</c>. A trailing lone backslash is dropped.</summary>
    internal static string Unescape(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0) return value.ToString();
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (++i >= value.Length) break;
            sb.Append(value[i] switch
            {
                ':' => ';',
                's' => ' ',
                'r' => '\r',
                'n' => '\n',
                var other => other,
            });
        }
        return sb.ToString();
    }
}
