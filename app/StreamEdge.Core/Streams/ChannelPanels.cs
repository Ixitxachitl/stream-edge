// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StreamEdge.Playback;

namespace StreamEdge.Streams;

/// <summary>One of a channel's About panels (the cards under the stream on
/// twitch.tv): a title, an image, text, and where clicking goes. Any of
/// them may be missing.</summary>
public sealed record ChannelPanel(string? Title, Uri? Image, string Description, Uri? Link)
{
    /// <summary>The description as formatted text, for drawing it as
    /// twitch.tv does: bold, links, line breaks. <see cref="Description"/> is
    /// the same as plain text.</summary>
    public IReadOnlyList<PanelRun> Runs { get; init; } = [];
}

/// <summary>A run of a panel's text: plain, bold, or a link. Line breaks
/// are "\n" inside the text.</summary>
public sealed record PanelRun(string Text, Uri? Link = null, bool Bold = false);

/// <summary>A channel's About panels. Helix doesn't have them, so this is
/// Twitch's GQL API with the web client ID, like <see cref="ChannelInfo"/>:
/// read-only, public, no login, fetched once per channel.</summary>
public static partial class ChannelPanels
{
    private const string Query =
        "query ChannelPanels($login: String!) { user(login: $login) { panels { __typename " +
        "... on DefaultPanel { title description linkURL imageURL } } } }";

    /// <summary>Empty when the channel has none or they couldn't be had.
    /// Extension panels (web pages) are left out: there's nothing to show
    /// them in.</summary>
    public static async Task<IReadOnlyList<ChannelPanel>> FetchAsync(HttpClient http, string login, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["query"] = Query,
            ["variables"] = new JsonObject { ["login"] = login.ToLowerInvariant() },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Client-ID", TwitchPlayback.WebClientId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    public static IReadOnlyList<ChannelPanel> Parse(string json)
    {
        var panels = new List<ChannelPanel>();
        try
        {
            foreach (var p in JsonNode.Parse(json)?["data"]?["user"]?["panels"]?.AsArray() ?? [])
            {
                if (p?["__typename"]?.GetValue<string>() != "DefaultPanel") continue;
                var title = p["title"]?.GetValue<string>() is { Length: > 0 } t ? t.Trim() : null;
                var markdown = p["description"]?.GetValue<string>();
                var description = PlainText(markdown);
                var image = ToUri(p["imageURL"]?.GetValue<string>());
                if (title is null && description.Length == 0 && image is null) continue;
                panels.Add(new ChannelPanel(title, image, description, ToUri(p["linkURL"]?.GetValue<string>()))
                {
                    Runs = Format(markdown),
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return panels;
    }

    /// <summary>Panel text is Markdown; shown plain: links keep their words
    /// (and bare URLs stay), emphasis marks and headings go, line breaks stay.</summary>
    public static string PlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var text = MarkdownImage().Replace(markdown, "");
        text = MarkdownLink().Replace(text, "$1");
        text = Emphasis().Replace(text, "");
        text = Heading().Replace(text, "");
        text = BlankLines().Replace(text.Replace("\r\n", "\n"), "\n\n");
        return text.Trim();
    }

    /// <summary>Panel Markdown as runs: [text](url) and bare URLs become
    /// links, **bold** and headings bold, images and other emphasis marks
    /// go, line breaks stay (blank lines at most one).</summary>
    public static IReadOnlyList<PanelRun> Format(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var text = MarkdownImage().Replace(markdown.Replace("\r\n", "\n"), "");
        text = BlankLines().Replace(text, "\n\n").Trim();
        var runs = new List<PanelRun>();
        void Add(string s, Uri? link = null, bool bold = false)
        {
            if (s.Length == 0) return;
            // Plain runs next to each other merge, so the text wraps as one.
            if (link is null && runs.Count > 0 && runs[^1] is { Link: null } last && last.Bold == bold)
                runs[^1] = last with { Text = last.Text + s };
            else runs.Add(new PanelRun(s, link, bold));
        }
        var lines = text.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0) Add("\n");
            var line = lines[l];
            var heading = Heading().Match(line);
            var bold = heading.Success;
            if (bold) line = line[heading.Length..];
            var at = 0;
            foreach (Match m in Inline().Matches(line))
            {
                Add(Emphasis().Replace(line[at..m.Index], ""), bold: bold);
                if (m.Groups["lt"].Success)
                {
                    var target = ToUri(m.Groups["lu"].Value);
                    Add(Emphasis().Replace(m.Groups["lt"].Value, ""), target, bold);
                }
                else if (m.Groups["bt"].Success) Add(m.Groups["bt"].Value, bold: true);
                else if (ToUri(m.Groups["url"].Value) is { } url) Add(m.Groups["url"].Value, url, bold);
                else Add(m.Value, bold: bold);
                at = m.Index + m.Length;
            }
            Add(Emphasis().Replace(line[at..], ""), bold: bold);
        }
        return runs;
    }

    private static Uri? ToUri(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(\*\*|__|~~|(?<!\w)[*_](?=\S)|(?<=\S)[*_](?!\w))")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)[^)]*\)|\*\*(?<bt>.+?)\*\*|__(?<bt>.+?)__|(?<url>https?://[^\s)\]]+)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
