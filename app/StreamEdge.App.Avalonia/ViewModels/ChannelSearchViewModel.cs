// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StreamEdge.Streams;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>The search box over the stream list: suggests channels as the
/// user types, as twitch.tv's does, and joins the one picked (or the name
/// exactly as typed). Channels already known (followed, open) show at once;
/// Twitch's search fills in after a short pause in typing.</summary>
public sealed partial class ChannelSearchViewModel : ObservableObject
{
    public const int MaxSuggestions = 8;

    private readonly Func<string, IEnumerable<ChannelSuggestion>> _local;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ChannelSuggestion>>> _remote;
    private readonly Action<string, OpenMode> _open;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _pending;

    public ChannelSearchViewModel(Func<string, IEnumerable<ChannelSuggestion>> local,
        Func<string, CancellationToken, Task<IReadOnlyList<ChannelSuggestion>>> remote,
        Action<string, OpenMode> open, IUiDispatcher ui)
    {
        _local = local;
        _remote = remote;
        _open = open;
        _ui = ui;
    }

    /// <summary>The pause in typing before asking Twitch.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(250);

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private bool _isOpen;

    public ObservableCollection<ChannelSuggestion> Suggestions { get; } = [];

    partial void OnQueryChanged(string value)
    {
        _pending?.Cancel();
        _pending = null;
        var q = ChannelSearch.Normalize(value);
        if (q.Length == 0)
        {
            Close();
            return;
        }
        Show(ChannelSearch.Rank(_local(q), q, MaxSuggestions), q);
        var cts = _pending = new CancellationTokenSource();
        _ = FetchAsync(q, cts.Token);
    }

    private async Task FetchAsync(string q, CancellationToken ct)
    {
        try
        {
            if (Debounce > TimeSpan.Zero) await Task.Delay(Debounce, ct).ConfigureAwait(false);
            var remote = await _remote(q, ct).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (!ct.IsCancellationRequested) Show(ChannelSearch.Rank(_local(q).Concat(remote), q, MaxSuggestions), q);
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Superseded by more typing, or Twitch is unreachable: what's
            // known locally stays up.
        }
    }

    private void Show(IReadOnlyList<ChannelSuggestion> found, string q)
    {
        var keep = SelectedIndex >= 0 && SelectedIndex < Suggestions.Count ? Suggestions[SelectedIndex].Login : null;
        Suggestions.Clear();
        foreach (var s in found) Suggestions.Add(s);
        // A name nobody has suggested yet can still be joined as typed.
        if (ChannelSearch.IsValidLogin(q) && !found.Any(s => string.Equals(s.Login, q, StringComparison.OrdinalIgnoreCase)))
            Suggestions.Add(new ChannelSuggestion(q.ToLowerInvariant(), q) { IsTyped = true });
        var at = keep is null ? -1 : Suggestions.ToList().FindIndex(s => s.Login == keep);
        SelectedIndex = at >= 0 ? at : Suggestions.Count > 0 ? 0 : -1;
        IsOpen = Suggestions.Count > 0;
    }

    /// <summary>Up and down through the suggestions, wrapping.</summary>
    public void Move(int step)
    {
        if (Suggestions.Count == 0) return;
        SelectedIndex = (SelectedIndex + step + Suggestions.Count) % Suggestions.Count;
    }

    /// <summary>Joins a suggestion (Enter or a click), or the name as typed
    /// when none is picked, and empties the box.</summary>
    public void Accept(int index, OpenMode mode = OpenMode.NewTab)
    {
        string? login = index >= 0 && index < Suggestions.Count ? Suggestions[index].Login : null;
        if (login is null && ChannelSearch.Normalize(Query) is var typed && ChannelSearch.IsValidLogin(typed))
            login = typed;
        if (login is null) return;
        _open(login, mode);
        Query = "";
    }

    /// <summary>Hides the suggestions; the text stays.</summary>
    public void Close()
    {
        IsOpen = false;
        Suggestions.Clear();
        SelectedIndex = -1;
    }

    /// <summary>Esc: forget the search.</summary>
    public void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
        Query = "";
        Close();
    }
}
