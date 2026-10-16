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

    /// <summary>Channels open as tabs, in tab order; <see cref="Channel"/> is
    /// the one in front.</summary>
    public List<string> OpenTabs { get; set; } = [];

    /// <summary>Chat columns, left to right: each one's tabs and the one it
    /// shows. Empty (older files) means one column of <see cref="OpenTabs"/>.</summary>
    public List<ChatColumnState> ChatColumns { get; set; } = [];

    /// <summary>Show background tabs as small live videos over the player
    /// (off: background tabs are audio only).</summary>
    public bool ShowPip { get; set; } = true;

    /// <summary>Reopen <see cref="Channel"/> at start-up; off starts with no
    /// channel open.</summary>
    public bool ReopenLastChannel { get; set; } = true;

    /// <summary>When an open channel raids another, the tab moves to the
    /// raided channel, as on twitch.tv. Off, it stays and shows the offline
    /// screen once the stream ends. Needs a signed-in account.</summary>
    public bool FollowRaids { get; set; } = true;

    /// <summary>Quality to start at ("source", "720p60", "480p"…). Catch-up
    /// may drop below it while the connection struggles.</summary>
    public string PreferredQuality { get; set; } = "source";

    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }

    /// <summary>Volume per channel (0–100), set with the scroll wheel over
    /// its video; channels not in here use <see cref="Volume"/>.</summary>
    public Dictionary<string, int> ChannelVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Closed captions on the front tab, when the stream has them.</summary>
    public bool Captions { get; set; }

    /// <summary>Emotes picked from the emote menu, most recent first, for its
    /// "Frequently used" row.</summary>
    public List<RecentEmote> RecentEmotes { get; set; } = [];

    /// <summary>Default layout is list | player | chat; this puts chat on the
    /// left instead.</summary>
    public bool ChatOnLeft { get; set; }

    /// <summary>For 4:3 and other squarish screens: the player above the
    /// stream list in one column, chat beside it. Off is the three-across
    /// layout for bar displays.</summary>
    public bool VideoAboveList { get; set; }

    /// <summary>Monitor to open on, by index into the platform's screen list.
    /// Null means "wherever the OS puts it"; the point of the app is a small
    /// secondary display, so this is usually set.</summary>
    public int? ScreenIndex { get; set; }

    /// <summary>Borderless full-screen on <see cref="ScreenIndex"/>.</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Where the window was last, restored at start-up unless a
    /// <c>--screen</c> is given or it would land off every screen.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Share of the space left beside the player that goes to chat
    /// (the rest goes to the stream list), 0..1.</summary>
    public double ChatShare { get; set; } = 0.5;

    public StreamSortOrder StreamSort { get; set; } = StreamSortOrder.Viewers;

    /// <summary>.NET format string for chat timestamps; empty hides them.</summary>
    public string TimestampFormat { get; set; } = "h:mm tt";

    public int ChatScrollback { get; set; } = 500;

    // Chat extras. Each third-party service is its own switch because each
    // is a separate request to someone else's server, and some users only
    // want the one their community uses. Missing from older files → the
    // defaults below.

    /// <summary>Pronoun tags from pronouns.alejo.io beside names.</summary>
    public bool ShowPronouns { get; set; } = true;

    /// <summary>BetterTTV emotes.</summary>
    public bool UseBttv { get; set; } = true;

    /// <summary>FrankerFaceZ emotes and badges (incl. custom mod badges).</summary>
    public bool UseFfz { get; set; } = true;

    /// <summary>7TV emotes.</summary>
    public bool UseSevenTv { get; set; } = true;

    /// <summary>GIFs (Twitch's GIPHY GIFs) drawn in chat; off, they show as
    /// their "[title]" text. They're full-size downloads, so this is the
    /// switch for a slow connection or a busy chat.</summary>
    public bool ShowGifs { get; set; } = true;

    /// <summary>JOIN/PART lines. Off by default: official chat doesn't show
    /// them and in busy channels they drown the conversation.</summary>
    public bool ShowJoinPart { get; set; }

    /// <summary>Show recent chat when a channel opens: Twitch's own (needs
    /// the auth-token), else the third-party service below.</summary>
    public bool LoadChatHistory { get; set; } = true;

    /// <summary>recent-messages.robotty.de, for history without an auth-token.</summary>
    public bool UseRecentMessages { get; set; } = true;

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

/// <summary>An emote the user picked from the menu, stored with enough to
/// draw and send it again.</summary>
public sealed record RecentEmote(string Code, Uri ImageUrl, StreamEdge.Chat.Emotes.EmoteProvider Provider);

/// <summary>A chat as saved: its tabs in order, the selected one, and
/// whether it stacks under the one before.</summary>
public sealed class ChatColumnState
{
    public List<string> Tabs { get; set; } = [];
    public string? Selected { get; set; }

    /// <summary>Stacked under the chat before it rather than beside it.</summary>
    public bool Below { get; set; }
}
