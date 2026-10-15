// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamEdge.Playback;

namespace StreamEdge.Chat;

/// <summary>Recent chat for a channel just opened, so it doesn't start empty.
///
/// Twitch's own: the <c>recentChatMessages</c> field twitch.tv loads on
/// joining, from its GQL API. It needs a signed-in twitch.tv session, which
/// is the <c>auth-token</c> cookie the user may already have saved for
/// ad-free playback (the app's own OAuth token isn't accepted there).
///
/// Fallback: recent-messages.robotty.de, a third-party service that logs
/// chat (used by Chatterino and Chatty), for users without an auth-token.</summary>
public static class ChatHistory
{
    private const string Query =
        "query ChatHistory($login: String!) { channel(name: $login) { id recentChatMessages {"
        + " id sentAt deletedAt"
        + " sender { id login displayName chatColor displayBadges { setID version } }"
        + " content { text fragments { text content { __typename ... on Emote { id token } } } }"
        + " } } }";

    public const string RecentMessagesUrl = "https://recent-messages.robotty.de/api/v2/recent-messages/";

    /// <summary>Twitch's history; null when it can't be had (no token, not
    /// accepted, or the API changed), so the caller can fall back.</summary>
    public static async Task<IReadOnlyList<ChatMessage>?> FetchTwitchAsync(HttpClient http, string login,
        string authToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["operationName"] = "ChatHistory",
            ["query"] = Query,
            ["variables"] = new JsonObject { ["login"] = login },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Client-ID", TwitchPlayback.WebClientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", authToken.Trim());
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return ParseTwitch(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), login);
    }

    /// <summary>The GQL reply as chat lines, oldest first, deleted ones left
    /// out. Null if it carries errors or no message list. Public for tests.</summary>
    public static IReadOnlyList<ChatMessage>? ParseTwitch(string json, string login)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["errors"] is JsonArray { Count: > 0 }) return null;
            var channel = root?["data"]?["channel"];
            if (channel?["recentChatMessages"] is not JsonArray list) return null;

            var messages = new List<ChatMessage>();
            foreach (var node in list)
            {
                if (node is null || node["deletedAt"]?.GetValue<string>() is { Length: > 0 }) continue;
                var sender = node["sender"];
                var senderLogin = sender?["login"]?.GetValue<string>();
                if (senderLogin is null) continue;

                // The text and emote positions come from the fragments: each
                // emote is its own fragment, so its offset is wherever the
                // text built so far ends (already UTF-16, unlike IRC tags).
                var text = new StringBuilder();
                var emotes = new List<TwitchEmoteRange>();
                foreach (var fragment in node["content"]?["fragments"]?.AsArray() ?? [])
                {
                    var piece = fragment?["text"]?.GetValue<string>() ?? "";
                    if (fragment?["content"] is JsonObject content
                        && content["__typename"]?.GetValue<string>() == "Emote"
                        && content["id"]?.GetValue<string>() is { } emoteId
                        && piece.Length > 0)
                        emotes.Add(new TwitchEmoteRange(emoteId, text.Length, piece.Length));
                    text.Append(piece);
                }
                if (text.Length == 0 && node["content"]?["text"]?.GetValue<string>() is { } plain) text.Append(plain);

                var badges = (sender?["displayBadges"]?.AsArray() ?? [])
                    .Select(b => $"{b?["setID"]?.GetValue<string>()}/{b?["version"]?.GetValue<string>()}")
                    .Where(b => !b.StartsWith('/'))
                    .ToList();
                var sentAt = DateTimeOffset.TryParse(node["sentAt"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var at) ? at.ToLocalTime() : DateTimeOffset.Now;

                messages.Add(new ChatMessage(sentAt, ChatMessageKind.Message, login, senderLogin,
                    sender?["displayName"]?.GetValue<string>(), Empty(sender?["chatColor"]?.GetValue<string>()),
                    badges, text.ToString())
                {
                    Id = node["id"]?.GetValue<string>(),
                    UserId = sender?["id"]?.GetValue<string>(),
                    TwitchEmotes = emotes,
                });
            }
            return [.. messages.OrderBy(m => m.Timestamp)];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The third-party fallback; empty on any failure.</summary>
    public static async Task<IReadOnlyList<ChatMessage>> FetchRecentMessagesAsync(HttpClient http, string login,
        int limit, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                $"{RecentMessagesUrl}{Uri.EscapeDataString(login)}?limit={limit}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            return ParseRecentMessages(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // History is a nicety: a service that's down (or a dropped
            // connection) just means the chat starts empty.
            return [];
        }
    }

    /// <summary>{"messages": ["@tags :nick!… PRIVMSG #chan :text", …]}: raw
    /// IRC lines, parsed the same as live ones. Deleted lines (marked
    /// <c>rm-deleted</c>) are left out. Public for tests.</summary>
    public static IReadOnlyList<ChatMessage> ParseRecentMessages(string json)
    {
        try
        {
            var lines = JsonNode.Parse(json)?["messages"]?.AsArray() ?? [];
            var messages = new List<ChatMessage>();
            foreach (var line in lines)
            {
                if (line?.GetValue<string>() is not { } raw) continue;
                var irc = IrcMessage.Parse(raw);
                if (irc.Tag("rm-deleted") == "1") continue;
                // Only what people said: history doesn't replay joins, parts
                // or notices from before the user arrived.
                if (irc.Command is not ("PRIVMSG" or "USERNOTICE")) continue;
                if (ChatMessage.FromIrc(irc, DateTimeOffset.Now) is { } message) messages.Add(message);
            }
            return messages;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
