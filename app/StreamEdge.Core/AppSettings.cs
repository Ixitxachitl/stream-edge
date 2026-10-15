// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamEdge.Streams;

namespace StreamEdge;

/// <summary>User settings, persisted as settings.json in <see cref="AppData"/>.
/// Unknown or missing fields fall back to defaults so older files keep
/// loading as the schema grows.</summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";

    /// <summary>Client ID of the Twitch application (registered as a Public
    /// client) used for sign-in and Helix. Not a secret.</summary>
    public string? ClientId { get; set; }

    /// <summary>Signed-in account, for display and Helix calls. The tokens
    /// themselves live in <see cref="Auth.SecretStore"/>, never here.</summary>
    public string? AccountLogin { get; set; }
    public string? AccountUserId { get; set; }

    /// <summary>Channels pinned to the top of the stream list.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>Channel to join on start-up (login name, no '#').</summary>
    public string? Channel { get; set; }

    /// <summary>Monitor to open on, by index into the platform's screen list.
    /// Null means "wherever the OS puts it"; the point of the app is a small
    /// secondary display, so this is usually set.</summary>
    public int? ScreenIndex { get; set; }

    /// <summary>Borderless full-screen on <see cref="ScreenIndex"/>.</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Share of the space left beside the player that goes to chat
    /// (the rest goes to the stream list), 0..1.</summary>
    public double ChatShare { get; set; } = 0.5;

    public StreamSortOrder StreamSort { get; set; } = StreamSortOrder.Viewers;

    /// <summary>.NET format string for chat timestamps; empty hides them.</summary>
    public string TimestampFormat { get; set; } = "h:mm tt";

    public int ChatScrollback { get; set; } = 500;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        var path = AppData.PathFor(FileName);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses must not stop the app
            // starting; keep it aside so the user can recover their edits.
            File.Copy(path, path + ".bad", overwrite: true);
        }
        return new();
    }

    public void Save()
    {
        var path = AppData.PathFor(FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
