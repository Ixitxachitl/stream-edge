// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamEdge.Chat;

/// <summary>One whisper in a conversation, either way.</summary>
public sealed record WhisperLine(DateTimeOffset Time, bool FromMe, string Text);

/// <summary>The whispers with one person, oldest first.</summary>
public sealed class WhisperConversation
{
    public string Login { get; set; } = "";
    public string Name { get; set; } = "";
    public string? UserId { get; set; }

    /// <summary>Their whispers not yet seen in the inbox.</summary>
    public int Unread { get; set; }

    public List<WhisperLine> Lines { get; set; } = [];
}

/// <summary>The whisper inbox as saved in whispers.json. Twitch keeps no
/// whisper history the API can read, so this is the only record; it belongs
/// to one account (<see cref="Owner"/>) and is dropped for another.</summary>
public sealed class WhisperArchive
{
    public const string FileName = "whispers.json";

    /// <summary>Most conversations kept; the quietest go first.</summary>
    public const int MaxConversations = 50;

    /// <summary>Most lines kept per conversation.</summary>
    public const int MaxLines = 200;

    /// <summary>The Twitch user id these whispers were to and from.</summary>
    public string? Owner { get; set; }

    /// <summary>Most recent first.</summary>
    public List<WhisperConversation> Conversations { get; set; } = [];

    /// <summary>The archive for <paramref name="owner"/>; empty when there's
    /// none, it's another account's, or the file doesn't parse.</summary>
    public static WhisperArchive Load(string? owner)
    {
        var path = AppData.PathFor(FileName);
        if (owner is null) return new();
        // The copy before it, if a crash left this one unreadable.
        return TryRead(path, owner) ?? TryRead(AppData.BackupOf(path), owner) ?? new() { Owner = owner };
    }

    private static WhisperArchive? TryRead(string path, string owner)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), WhisperArchiveJson.Default.WhisperArchive) is { } archive
                && archive.Owner == owner)
                return archive;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
        return null;
    }

    public void Save()
    {
        if (Owner is null) return;
        foreach (var c in Conversations)
            if (c.Lines.Count > MaxLines) c.Lines.RemoveRange(0, c.Lines.Count - MaxLines);
        if (Conversations.Count > MaxConversations) Conversations.RemoveRange(MaxConversations, Conversations.Count - MaxConversations);
        AppData.WriteSafely(AppData.PathFor(FileName), JsonSerializer.Serialize(this, WhisperArchiveJson.Default.WhisperArchive));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WhisperArchive))]
internal sealed partial class WhisperArchiveJson : JsonSerializerContext;
