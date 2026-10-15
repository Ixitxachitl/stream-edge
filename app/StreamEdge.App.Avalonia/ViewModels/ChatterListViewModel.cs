// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Chat;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>A section heading in the viewer list: "MODERATORS (3)".</summary>
public sealed record ChatterSection(string Title);

/// <summary>"Users in chat" for one channel, drawn over its chat column like
/// twitch.tv's: searchable, in sections (broadcaster, moderators, VIPs,
/// staff, users). Starts from what chat has shown, then, where the user
/// moderates, swaps in Twitch's complete list.</summary>
public sealed partial class ChatterListViewModel : ObservableObject
{
    private readonly ChatViewModel _chat;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Chatter>?>>? _fetchAll;
    private readonly Action _close;
    private IReadOnlyList<Chatter> _all = [];
    private bool _complete;
    private CancellationTokenSource? _loading;

    /// <param name="fetchAll">The complete list from Twitch, or null when
    /// the user can't have it here; returns null if Twitch refuses.</param>
    /// <param name="partialNote">What to say about a list built from chat alone.</param>
    public ChatterListViewModel(ChatViewModel chat, Func<CancellationToken, Task<IReadOnlyList<Chatter>?>>? fetchAll,
        string partialNote, Action close)
    {
        _chat = chat;
        _fetchAll = fetchAll;
        _close = close;
        PartialNote = partialNote;
    }

    public string Channel => _chat.Channel;

    /// <summary><see cref="ChatterSection"/>s and <see cref="Chatter"/>s, in order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty]
    private string _search = "";

    partial void OnSearchChanged(string value) => Rebuild();

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private bool _isLoading;

    public string PartialNote { get; }

    [ObservableProperty]
    private string _countText = "";

    private static string SectionTitle(ChatterRole role) => role switch
    {
        ChatterRole.Broadcaster => "BROADCASTER",
        ChatterRole.Moderator => "MODERATORS",
        ChatterRole.Vip => "VIPS",
        ChatterRole.Staff => "STAFF",
        _ => "USERS",
    };

    /// <summary>Shows what chat has seen right away, then fetches the
    /// complete list if the user may have it.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        _loading?.Cancel();
        if (!_complete) Show(_chat.Roster.Snapshot(), complete: false);
        if (_fetchAll is null) return;

        var loading = _loading = new CancellationTokenSource();
        IsLoading = true;
        Note = "Loading everyone in chat…";
        try
        {
            var all = await _fetchAll(loading.Token);
            if (loading.IsCancellationRequested) return;
            if (all is not null) Show(all, complete: true);
            else Show(_chat.Roster.Snapshot(), complete: false, "Twitch wouldn't give the full list. " + PartialNote);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!loading.IsCancellationRequested)
                Show(_chat.Roster.Snapshot(), complete: false, "Couldn't load the full list. " + PartialNote);
        }
        finally
        {
            if (_loading == loading) IsLoading = false;
        }
    }

    private void Show(IReadOnlyList<Chatter> chatters, bool complete, string? note = null)
    {
        _all = chatters;
        _complete = complete;
        Note = note ?? (complete ? "" : PartialNote);
        Rebuild();
    }

    private void Rebuild()
    {
        var query = Search.Trim();
        var shown = query.Length == 0 ? _all
            : _all.Where(c => c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || c.Login.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Items.Clear();
        foreach (var section in shown.GroupBy(c => c.Role))
        {
            Items.Add(new ChatterSection(SectionTitle(section.Key)));
            foreach (var chatter in section) Items.Add(chatter);
        }
        CountText = query.Length == 0 ? $"{_all.Count:N0} in chat" : $"{shown.Count:N0} of {_all.Count:N0}";
    }

    [RelayCommand]
    private void Close()
    {
        _loading?.Cancel();
        _close();
    }
}
