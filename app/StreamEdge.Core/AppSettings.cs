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

    /// <summary>Overrides the built-in <see cref="Auth.TwitchApp.ClientId"/>,
    /// for forks that register their own Twitch application. Hand-edit only.</summary>
    public string? ClientId { get; set; }

    [JsonIgnore]
    public string EffectiveClientId => string.IsNullOrWhiteSpace(ClientId) ? Auth.TwitchApp.ClientId : ClientId.Trim();

    /// <summary>Signed-in account, for display and Helix calls. The tokens
    /// themselves live in <see cref="Auth.SecretStore"/>, never here.</summary>
    public string? AccountLogin { get; set; }
    public string? AccountUserId { get; set; }

    /// <summary>Channels pinned to the top of the stream list.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>The last channel opened (login name, no '#').</summary>
    public string? Channel { get; set; }

    /// <summary>Reopen <see cref="Channel"/> at start-up; off starts with no
    /// channel open.</summary>
    public bool ReopenLastChannel { get; set; } = true;

    /// <summary>Quality to start at ("source", "720p60", "480p"…). Catch-up
    /// may drop below it while the connection struggles.</summary>
    public string PreferredQuality { get; set; } = "source";

    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }

    /// <summary>Default layout is list | player | chat; this puts chat on the
    /// left instead.</summary>
    public bool ChatOnLeft { get; set; }

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

    /// <summary>Set for <c>--demo</c>: settings are read so the layout looks
    /// like the user's, but nothing a demo session does is written back.</summary>
    [JsonIgnore]
    public bool ReadOnly { get; set; }

    public bool IsFavorite(string login) => Favorites.Contains(login, StringComparer.OrdinalIgnoreCase);

    /// <summary>Stars or un-stars a channel; returns the new state.</summary>
    public bool ToggleFavorite(string login)
    {
        if (Favorites.RemoveAll(f => string.Equals(f, login, StringComparison.OrdinalIgnoreCase)) > 0)
            return false;
        Favorites.Add(login.ToLowerInvariant());
        return true;
    }

    public void Save()
    {
        if (ReadOnly) return;
        var path = AppData.PathFor(FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
