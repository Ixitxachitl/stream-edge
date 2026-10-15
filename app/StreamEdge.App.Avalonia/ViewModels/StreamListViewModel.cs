// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

public sealed partial class StreamItemViewModel : ObservableObject
{
    private readonly Action<StreamItemViewModel> _toggleFavorite;

    public StreamItemViewModel(LiveStream stream, DateTimeOffset now, Action<StreamItemViewModel> toggleFavorite)
    {
        Stream = stream;
        _header = stream.Header(now);
        _toggleFavorite = toggleFavorite;
    }

    public LiveStream Stream { get; }
    public string Title => Stream.Title;
    public string Game => "(" + Stream.Game + ")";
    public bool IsFavorite => Stream.IsFavorite;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteMenuText => IsFavorite ? "Unstar" : "Star (keep at top)";

    /// <summary>"Name (viewers | uptime)": refreshed by the clock tick.</summary>
    [ObservableProperty]
    private string _header;

    [RelayCommand]
    private void ToggleFavorite() => _toggleFavorite(this);

    public void Tick(DateTimeOffset now) => Header = Stream.Header(now);
}

public sealed partial class StreamListViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private IReadOnlyList<LiveStream> _source = [];

    public StreamListViewModel(AppSettings settings)
    {
        _settings = settings;
        _sort = settings.StreamSort;
    }

    public ObservableCollection<StreamItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private StreamSortOrder _sort;

    [ObservableProperty]
    private StreamItemViewModel? _selected;

    /// <summary>Shown in place of the list when it is empty.</summary>
    [ObservableProperty]
    private string _emptyText = "Log in to see which channels you follow are live.";

    public string Header => $"Live Streams ({Items.Count}) [Sorting: {Sort}]";
    public bool IsEmpty => Items.Count == 0;

    partial void OnSortChanged(StreamSortOrder value)
    {
        _settings.StreamSort = value;
        Rebuild(DateTimeOffset.Now);
    }

    [RelayCommand]
    private void CycleSort() =>
        Sort = (StreamSortOrder)(((int)Sort + 1) % Enum.GetValues<StreamSortOrder>().Length);

    public void Set(IReadOnlyList<LiveStream> streams, DateTimeOffset now)
    {
        _source = streams;
        Rebuild(now);
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var item in Items) item.Tick(now);
    }

    private void ToggleFavorite(StreamItemViewModel item)
    {
        _settings.ToggleFavorite(item.Stream.Login);
        _settings.Save();
        Rebuild(DateTimeOffset.Now);
    }

    private void Rebuild(DateTimeOffset now)
    {
        var keep = Selected?.Stream.Login;
        // Stars are the user's, not the source's: re-apply them on every
        // rebuild so a refresh or a toggle can't lose one.
        var starred = _source.Select(s => s with { IsFavorite = _settings.IsFavorite(s.Login) });
        Items.Clear();
        foreach (var s in StreamSort.Apply(starred, Sort))
            Items.Add(new StreamItemViewModel(s, now, ToggleFavorite));
        // Silently: re-selecting the channel already open must not reopen it.
        _suppressSelection = true;
        try { Selected = Items.FirstOrDefault(i => i.Stream.Login == keep); }
        finally { _suppressSelection = false; }
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool _suppressSelection;

    public void ClearSelection()
    {
        _suppressSelection = true;
        try { Selected = null; }
        finally { _suppressSelection = false; }
    }

    /// <summary>The user picked a channel (not a rebuild restoring the selection).</summary>
    public event Action<string>? ChannelChosen;

    partial void OnSelectedChanged(StreamItemViewModel? value)
    {
        if (!_suppressSelection && value is not null) ChannelChosen?.Invoke(value.Stream.Login);
    }
}
