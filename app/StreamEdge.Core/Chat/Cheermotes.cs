// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamEdge.Chat;

/// <summary>One step of a cheermote: from <see cref="MinBits"/> up, this
/// picture and colour.</summary>
public sealed record CheerTier(int MinBits, string Color, Uri Image);

/// <summary>A cheermote ("Cheer", "Kappa", a channel's own): the word
/// people type before the amount, and its tiers, smallest first.</summary>
public sealed record Cheermote(string Prefix, IReadOnlyList<CheerTier> Tiers);

/// <summary>A cheer in a message ("Cheer100"): its picture and colour for
/// the amount.</summary>
public sealed record Cheer(string Prefix, int Bits, string Color, Uri Image);

public static class Cheermotes
{
    /// <summary>Twitch's cheermotes plus the channel's own, from Helix "Get
    /// Cheermotes" (any user token). Empty if they can't be had.</summary>
    public static async Task<IReadOnlyList<Cheermote>> FetchAsync(HttpClient http, string clientId, string token,
        string broadcasterId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.twitch.tv/helix/bits/cheermotes?broadcaster_id=" + Uri.EscapeDataString(broadcasterId));
            request.Headers.Add("Client-Id", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Helix's answer: each prefix with tiers, each tier with dark-
    /// theme images (animated, at 2x, as twitch.tv draws them in chat).</summary>
    public static IReadOnlyList<Cheermote> Parse(string json)
    {
        try
        {
            var result = new List<Cheermote>();
            foreach (var c in JsonNode.Parse(json)?["data"]?.AsArray() ?? [])
            {
                if (c?["prefix"]?.GetValue<string>() is not { Length: > 0 } prefix) continue;
                var tiers = new List<CheerTier>();
                foreach (var t in c["tiers"]?.AsArray() ?? [])
                {
                    var images = t?["images"]?["dark"];
                    var url = images?["animated"]?["2"]?.GetValue<string>() ?? images?["static"]?["2"]?.GetValue<string>();
                    if (t?["min_bits"]?.GetValue<int>() is not { } min || !Uri.TryCreate(url, UriKind.Absolute, out var image)) continue;
                    tiers.Add(new CheerTier(min, t["color"]?.GetValue<string>() ?? "#9147FF", image));
                }
                if (tiers.Count > 0) result.Add(new Cheermote(prefix, [.. tiers.OrderBy(t => t.MinBits)]));
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    /// <summary>The cheer a word is ("cheer100", "Kappa5000"), or null:
    /// a known prefix, any case, then a whole number of bits, which picks the
    /// biggest tier it reaches.</summary>
    public static Cheer? Match(IReadOnlyList<Cheermote> cheermotes, string word)
    {
        var digits = word.Length;
        while (digits > 0 && char.IsAsciiDigit(word[digits - 1])) digits--;
        if (digits == 0 || digits == word.Length
            || !int.TryParse(word.AsSpan(digits), NumberStyles.None, CultureInfo.InvariantCulture, out var bits) || bits <= 0)
            return null;
        var prefix = word[..digits];
        var cheermote = cheermotes.FirstOrDefault(c => string.Equals(c.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        if (cheermote is null) return null;
        var tier = cheermote.Tiers.LastOrDefault(t => t.MinBits <= bits) ?? cheermote.Tiers[0];
        return new Cheer(cheermote.Prefix, bits, tier.Color, tier.Image);
    }
}
