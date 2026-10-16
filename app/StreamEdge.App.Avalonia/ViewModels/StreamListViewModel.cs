// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Streams;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>How a stream opens.</summary>
public enum OpenMode
{
    /// <summary>In the front tab, replacing its channel (double-click).</summary>
    Front,
    /// <summary>In a new tab brought to the front.</summary>
    NewTab,
    /// <summary>In a new tab that stays behind, playing audio or as PIP.</summary>
    Background,
}

public sealed partial class StreamItemViewModel : ObservableObject
{
    private readonly Action<StreamItemViewModel> _toggleFavorite;
    private readonly Action<string, OpenMode> _open;

    public StreamItemViewModel(LiveStream stream, DateTimeOffset now,
        Action<StreamItemViewModel> toggleFavorite, Action<string, OpenMode> open)
    {
        Stream = stream;
        _header = stream.Header(now);
        _uptime = UptimeText(stream, now);
        _toggleFavorite = toggleFavorite;
        _open = open;
    }

    public LiveStream Stream { get; }
    public string Title => Stream.Title;

    /// <summary>In a shared chat session: up to two of the other channels'
    /// icons, drawn over the corner of this one's, as twitch.tv's sidebar.</summary>
    public IReadOnlyList<SharedAvatar> SharedAvatars => Stream.SharedWith is { } others
        ? [.. others.Take(2).Select(o => new SharedAvatar(o.DisplayName, o.ProfileImage))]
        : [];

    public bool IsSharedChat => Stream.IsSharedChat;

    /// <summary>"Shared chat with A, B", in the row's hover card.</summary>
    public string SharedText => Stream.SharedWith is { Count: > 0 } others
        ? "Shared chat with " + string.Join(", ", others.Select(o => o.DisplayName))
        : "";
    public string Game => Stream.IsLive ? Stream.Game : "Offline";
    public bool IsLive => Stream.IsLive;

    /// <summary>Open in a tab but not followed: listed above the followed
    /// channels, which <see cref="SeparatorBelow"/> marks off.</summary>
    public bool IsWatched { get; init; }

    /// <summary>The last watched channel, with followed ones below it.</summary>
    public bool SeparatorBelow { get; init; }

    /// <summary>twitch.tv/name, where following and unfollowing happen.</summary>
    [RelayCommand]
    private void OpenOnTwitch() => UrlLauncher.TryOpen("https://www.twitch.tv/" + Uri.EscapeDataString(Stream.Login));
    public string DisplayName => Stream.DisplayName;
    public Uri? ProfileImage => Stream.ProfileImage;

    /// <summary>Shown in the avatar's place until it loads, or if there is none.</summary>
    public string Initial => DisplayName.Length > 0 ? char.ToUpperInvariant(DisplayName[0]).ToString() : "?";

    /// <summary>"11.4K", as twitch.tv's sidebar has it.</summary>
    public string ViewersText => IsLive && Stream.Viewers >= 0 ? UiFormats.CompactViewers(Stream.Viewers) : "";
    public bool IsFavorite => Stream.IsFavorite;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteMenuText => IsFavorite ? "Unstar" : "Star (keep at top)";

    /// <summary>"Name (viewers | uptime)": refreshed by the clock tick.</summary>
    [ObservableProperty]
    private string _header;

    /// <summary>"Live for 2.4h", for the row's tooltip; refreshed by the clock tick.</summary>
    [ObservableProperty]
    private string _uptime;

    [RelayCommand]
    private void ToggleFavorite() => _toggleFavorite(this);

    [RelayCommand]
    private void Open() => _open(Stream.Login, OpenMode.Front);

    [RelayCommand]
    private void OpenInNewTab() => _open(Stream.Login, OpenMode.NewTab);

    [RelayCommand]
    private void OpenInBackground() => _open(Stream.Login, OpenMode.Background);

    private static string UptimeText(LiveStream s, DateTimeOffset now) =>
        !s.IsLive ? "Offline" : s.Viewers < 0 ? "Live" : "Live for " + UiFormats.Uptime(s.Uptime(now));

    public void Tick(DateTimeOffset now)
    {
        Header = Stream.Header(now);
        Uptime = UptimeText(Stream, now);
    }
}

/// <summary>A small channel icon (its initial until the picture loads).</summary>
public sealed record SharedAvatar(string Name, Uri? Image)
{
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
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

    public string Header => $"Live Streams ({_source.Count}) [Sorting: {Sort}]";

    /// <summary>No rows at all.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>No followed channels live (or signed out): the empty text and
    /// login box show, under any watched channels.</summary>
    public bool NoFollowed => _source.Count == 0;

    /// <summary>The followed live channels, as last set.</summary>
    public IReadOnlyList<LiveStream> Followed => _source;

    private IReadOnlyList<LiveStream> _watched = [];

    /// <summary>Channels open in tabs that the user doesn't follow, listed
    /// first, in tab order.</summary>
    public IReadOnlyList<LiveStream> Watched => _watched;

    public void SetWatched(IReadOnlyList<LiveStream> watched, DateTimeOffset now)
    {
        if (watched.SequenceEqual(_watched)) return;
        _watched = watched;
        Rebuild(now);
    }

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
        void Open(string login, OpenMode mode) => OpenRequested?.Invoke(login, mode);
        for (var i = 0; i < _watched.Count; i++)
            Items.Add(new StreamItemViewModel(_watched[i] with { IsFavorite = _settings.IsFavorite(_watched[i].Login) },
                now, ToggleFavorite, Open)
            {
                IsWatched = true,
                SeparatorBelow = i == _watched.Count - 1,
            });
        foreach (var s in StreamSort.Apply(starred, Sort))
            Items.Add(new StreamItemViewModel(s, now, ToggleFavorite, Open));
        Selected = Items.FirstOrDefault(i => i.Stream.Login == keep);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoFollowed));
    }

    /// <summary>The user asked to open a stream: double-click (front) or
    /// the context menu. Selecting alone opens nothing.</summary>
    public event Action<string, OpenMode>? OpenRequested;
}
