// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Chat.Emotes;

/// <summary>Lenient readers for third-party JSON. These APIs are run by
/// volunteers and change shape now and then; one odd entry should cost that
/// entry, not the whole set, so every accessor returns null instead of
/// throwing on a missing field or a wrong type.</summary>
internal static class JsonRead
{
    public static JsonNode? Parse(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });

    public static string? Str(this JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v ? AsString(v) : null;

    public static int? Int(this JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v ? AsInt(v) : null;

    public static bool Bool(this JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>A child of an object; unlike the indexer, null (not a throw)
    /// when <paramref name="node"/> turns out to be an array or a value.</summary>
    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject o ? o[key] : null;

    public static JsonObject? Obj(this JsonNode? node, string key) =>
        node is JsonObject o ? o[key] as JsonObject : null;

    public static IEnumerable<JsonNode> Arr(this JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonArray a ? Items(a) : [];

    public static IEnumerable<JsonNode> Items(JsonArray? array) =>
        array is null ? [] : array.Where(n => n is not null).Select(n => n!);

    /// <summary>Ids arrive as strings from some APIs and numbers from others.</summary>
    public static string? AsString(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    public static int? AsInt(JsonValue v) =>
        v.TryGetValue<int>(out var i) ? i
        : v.TryGetValue<double>(out var d) ? (int)d
        : null;

    public static Uri? Url(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u : null;
    }
}

internal static class Http
{
    /// <summary>GETs <paramref name="url"/> as text; null on 404, which
    /// these APIs use for "this channel has no emotes/pronouns here".</summary>
    /// <remarks>Always asks for gzip/brotli and decodes it here, whatever
    /// handler the caller's HttpClient has: a big channel's 7TV set is
    /// ~2.4 MB of JSON that 7tv.io serves at a crawl uncompressed (minutes,
    /// often reset) but in ~2 s as ~300 KB gzip. A handler with
    /// AutomaticDecompression strips Content-Encoding, so it isn't decoded twice.</remarks>
    public static async Task<string?> GetOrNullAsync(HttpClient http, string url, CancellationToken ct,
        Action<HttpRequestMessage>? prepare = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        prepare?.Invoke(request);
        if (request.Headers.AcceptEncoding.Count == 0)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var encoding = response.Content.Headers.ContentEncoding.LastOrDefault();
        if (encoding is null || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var _ = raw.ConfigureAwait(false);
        Stream decoded = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(raw, CompressionMode.Decompress),
            "deflate" => new ZLibStream(raw, CompressionMode.Decompress),
            "br" => new BrotliStream(raw, CompressionMode.Decompress),
            var other => throw new HttpRequestException($"Unsupported Content-Encoding '{other}'"),
        };
        await using (decoded.ConfigureAwait(false))
        {
            // These APIs all send UTF-8 JSON.
            using var reader = new StreamReader(decoded, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }
}
