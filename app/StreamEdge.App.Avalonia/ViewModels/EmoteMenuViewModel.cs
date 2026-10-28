// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>A row of the emote menu: a section header, or a run of emotes.</summary>
public abstract record EmoteMenuRow;

public sealed record EmoteMenuHeader(string Title, Uri? Icon) : EmoteMenuRow;

public sealed record EmoteMenuEmotes(IReadOnlyList<EmoteMenuItem> Items) : EmoteMenuRow;

/// <summary>One emote button. Carries its command: the menu is a popup,
/// outside the window's tree, so it can't bind upward.</summary>
public sealed record EmoteMenuItem(Emote Emote, ICommand Insert)
{
    public string Tip => Emote.Code + Emote.Provider switch
    {
        EmoteProvider.Bttv => "  ·  BTTV",
        EmoteProvider.Ffz => "  ·  FFZ",
        EmoteProvider.SevenTv => "  ·  7TV",
        _ => "",
    };
}

/// <summary>The emote picker under the chat box, laid out like twitch.tv's:
/// frequently used, this channel, the user's other channels, Twitch
/// globals, then the third-party sets. Rows of a fixed width, so the list
/// virtualizes and only emotes scrolled into view load their images.</summary>
public sealed partial class EmoteMenuViewModel : ObservableObject
{
    public const int PerRow = 8;
    private const int MaxResults = 320;

    private readonly ChatServices _services;
    private readonly Func<ChannelTabViewModel?> _front;
    private readonly AccountViewModel _account;

    public EmoteMenuViewModel(ChatServices services, Func<ChannelTabViewModel?> front, AccountViewModel account)
    {
        _services = services;
        _front = front;
        _account = account;
    }

    public ObservableCollection<EmoteMenuRow> Rows { get; } = [];

    [ObservableProperty]
    private string _search = "";

    /// <summary>Why the user's Twitch emotes are missing, if they are.</summary>
    [ObservableProperty]
    private string? _notice;

    partial void OnSearchChanged(string value) => Rebuild();

    /// <summary>Called as the menu opens: emote sets may have loaded since.</summary>
    public void Open()
    {
        Search = "";
        Notice = !_account.IsSignedIn ? "Log in to use your Twitch emotes."
            : !_account.CanUseOwnEmotes ? "Log in again (⚙) to load your Twitch emotes: StreamEdge now asks for permission to read them."
            : null;
        Rebuild();
    }

    [RelayCommand]
    private void Insert(Emote emote)
    {
        if (_front()?.Chat is not { } chat) return;
        var draft = chat.Draft;
        chat.Draft = (draft.Length == 0 || draft.EndsWith(' ') ? draft : draft + " ") + emote.Code + " ";
        _services.NoteUsed(emote);
    }

    private void Rebuild()
    {
        Rows.Clear();
        var tab = _front();
        var roomId = tab?.RoomId;
        var mine = _services.MyEmotes;
        var owners = _services.Owners;
        var thirdParty = _services.Emotes.SetsFor(roomId);

        if (Search.Trim() is { Length: > 0 } query)
        {
            var hits = mine.Select(m => m.Emote)
                .Concat(thirdParty.SelectMany(s => s.Emotes))
                .Where(e => e.Code.Contains(query, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(e => e.Code)
                .OrderBy(e => !e.Code.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(e => e.Code.Length)
                .Take(MaxResults);
            Section("Results", null, hits);
            return;
        }

        // A Twitch emote only while the user still has it (a sub can lapse);
        // until their emotes load, all of them.
        Section("Frequently used", null, _services.Settings.RecentEmotes
            .Where(r => r.Provider != EmoteProvider.Twitch || mine.Count == 0 || _services.MyEmote(r.Code) is not null)
            .Select(r => new Emote(r.Code, r.ImageUrl, r.Provider)));

        var channelName = (roomId is null ? null : owners.GetValueOrDefault(roomId)?.DisplayName) ?? tab?.DisplayName ?? tab?.Login;
        if (roomId is not null)
            Section(channelName ?? "This channel", owners.GetValueOrDefault(roomId)?.ProfileImage,
                mine.Where(m => m.OwnerId == roomId).Select(m => m.Emote));
        foreach (var set in thirdParty.Where(s => s.IsChannel))
            Section($"{channelName} · {Name(set.Provider)}", null, set.Emotes);

        foreach (var group in mine.Where(m => m.OwnerId != roomId && owners.ContainsKey(m.OwnerId))
                     .GroupBy(m => m.OwnerId)
                     .OrderBy(g => owners[g.Key].DisplayName, StringComparer.OrdinalIgnoreCase))
            Section(owners[group.Key].DisplayName, owners[group.Key].ProfileImage, group.Select(m => m.Emote));

        Section("Twitch", null, mine.Where(m => m.OwnerId != roomId && !owners.ContainsKey(m.OwnerId))
            .Select(m => m.Emote));
        foreach (var set in thirdParty.Where(s => !s.IsChannel))
            Section($"{Name(set.Provider)} global", null, set.Emotes);
    }

    private void Section(string title, Uri? icon, IEnumerable<Emote> emotes)
    {
        var items = emotes.Select(e => new EmoteMenuItem(e, InsertCommand)).ToList();
        if (items.Count == 0) return;
        Rows.Add(new EmoteMenuHeader(title, icon));
        foreach (var row in items.Chunk(PerRow)) Rows.Add(new EmoteMenuEmotes(row));
    }

    private static string Name(EmoteProvider provider) => provider switch
    {
        EmoteProvider.Bttv => "BTTV",
        EmoteProvider.Ffz => "FFZ",
        EmoteProvider.SevenTv => "7TV",
        _ => "Twitch",
    };
}
