// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat;

/// <summary>Name colours for users who never picked one: the palette Twitch's
/// web chat falls back to, chosen stably from the login.</summary>
public static class UserColors
{
    private static readonly string[] Defaults =
    [
        "#FF0000", "#0000FF", "#008000", "#B22222", "#FF7F50",
        "#9ACD32", "#FF4500", "#2E8B57", "#DAA520", "#D2691E",
        "#5F9EA0", "#1E90FF", "#FF69B4", "#8A2BE2", "#00FF7F",
    ];

    public static string For(string? login, string? chosen)
    {
        if (!string.IsNullOrEmpty(chosen)) return chosen;
        if (string.IsNullOrEmpty(login)) return Defaults[0];
        // FNV-1a, not string.GetHashCode: that is randomised per process, and
        // a user's colour should not change between runs.
        uint hash = 2166136261;
        foreach (var c in login) hash = (hash ^ c) * 16777619;
        return Defaults[hash % (uint)Defaults.Length];
    }
}
