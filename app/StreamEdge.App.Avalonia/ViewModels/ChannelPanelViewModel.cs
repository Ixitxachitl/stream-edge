// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>One About panel as drawn: its title, image and text, as on
/// twitch.tv; clicking the image opens its link.</summary>
public sealed partial class ChannelPanelCard(ChannelPanel panel)
{
    public string? Title => panel.Title;
    public bool HasTitle => panel.Title is not null;
    public Uri? Image => panel.Image;
    public bool HasImage => panel.Image is not null;
    public string Description => panel.Description;
    public bool HasDescription => panel.Description.Length > 0;

    /// <summary>The description formatted: bold, links, line breaks.</summary>
    public IReadOnlyList<PanelRun> Runs => panel.Runs;
    public bool HasLink => panel.Link is not null;
    public string? LinkTip => panel.Link?.AbsoluteUri;

    [RelayCommand]
    private void Open()
    {
        if (panel.Link is { } link) UrlLauncher.TryOpen(link.AbsoluteUri);
    }
}

/// <summary>The panel under the video: the front channel's name and title,
/// and its About panels (fetched once per channel).</summary>
public sealed partial class ChannelPanelViewModel(HttpClient http, bool demo) : ObservableObject
{
    private readonly Dictionary<string, IReadOnlyList<ChannelPanel>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The channel on video, whose name, title and game head the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannel))]
    private ChannelTabViewModel? _tab;

    public bool HasChannel => Tab is not null;

    public ObservableCollection<ChannelPanelCard> Cards { get; } = [];

    /// <summary>"Loading…", "No About panels", or empty with cards to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    public bool HasStatus => Status.Length > 0;

    /// <summary>Follow <paramref name="tab"/> without fetching (the panel is hidden).</summary>
    public void Show(ChannelTabViewModel? tab)
    {
        Tab = tab;
        Cards.Clear();
        Status = "";
    }

    public async Task LoadAsync(ChannelTabViewModel? tab)
    {
        Show(tab);
        if (tab is null)
        {
            Status = "Pick a stream to see its About panels.";
            return;
        }
        var login = tab.Login;
        if (!_cache.TryGetValue(login, out var panels))
        {
            if (demo)
            {
                panels = Demo.SampleData.Panels();
            }
            else
            {
                Status = "Loading…";
                panels = await ChannelPanels.FetchAsync(http, login, CancellationToken.None);
            }
            _cache[login] = panels;
            // Switched channel while this was loading: that load fills it.
            if (Tab != tab) return;
        }
        Cards.Clear();
        foreach (var panel in panels) Cards.Add(new ChannelPanelCard(panel));
        Status = panels.Count == 0 ? "This channel has no About panels." : "";
    }
}
