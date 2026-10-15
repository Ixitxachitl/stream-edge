// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Chat.Emotes;

/// <summary>Emote suggestions for the chat box: which word is being typed,
/// and which emotes fit it, best first.</summary>
public static class EmoteCompletion
{
    /// <summary>The word the caret is in or just after: from the previous
    /// space up to the caret. Empty (length 0) when the caret follows a space.</summary>
    public static (int Start, int Length) WordAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        return (start, caret - start);
    }

    /// <summary>Emotes for <paramref name="typed"/>, best first: ones the
    /// user picked recently, then an exact-case prefix, then any-case
    /// prefix, then (with <paramref name="contains"/>, as after ':') ones
    /// that merely contain it; shorter codes before longer within each.</summary>
    public static IReadOnlyList<Emote> Rank(IEnumerable<Emote> candidates, string typed,
        IReadOnlyList<string> recent, bool contains, int max)
    {
        if (typed.Length == 0) return [];
        return candidates
            .Where(e => contains
                ? e.Code.Contains(typed, StringComparison.OrdinalIgnoreCase)
                : e.Code.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Where(e => !e.ZeroWidth || e.Code.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(e => e.Code, StringComparer.Ordinal)
            .OrderBy(e => recent.IndexOf(e.Code) is var r && r >= 0 ? r : int.MaxValue)
            .ThenBy(e => e.Code.StartsWith(typed, StringComparison.Ordinal) ? 0
                : e.Code.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(e => e.Code.Length)
            .ThenBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static int IndexOf(this IReadOnlyList<string> list, string code)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], code, StringComparison.Ordinal)) return i;
        return -1;
    }
}
