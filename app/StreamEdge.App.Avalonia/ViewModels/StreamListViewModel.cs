// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

public sealed partial class StreamItemViewModel : ObservableObject
{
    public StreamItemViewModel(LiveStream stream, DateTimeOffset now)
    {
        Stream = stream;
        _header = stream.Header(now);
    }

    public LiveStream Stream { get; }
    public string Title => Stream.Title;
    public string Game => "(" + Stream.Game + ")";
    public bool IsFavorite => Stream.IsFavorite;

    /// <summary>"Name (viewers | uptime)": refreshed by the clock tick.</summary>
    [ObservableProperty]
    private string _header;

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
    private string _emptyText = "Sign in to see live followed channels.";

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

    private void Rebuild(DateTimeOffset now)
    {
        var keep = Selected?.Stream.Login;
        Items.Clear();
        foreach (var s in StreamSort.Apply(_source, Sort))
            Items.Add(new StreamItemViewModel(s, now));
        Selected = Items.FirstOrDefault(i => i.Stream.Login == keep);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
