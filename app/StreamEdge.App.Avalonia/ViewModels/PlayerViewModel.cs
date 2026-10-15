// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>State for the centre pane. Until the video surface lands
/// (docs/PLAN.md, phase 3) this shows what would play and how it resolved.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _channel = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _game = "";

    [ObservableProperty]
    private string _status = "Pick a stream from the list.";

    /// <summary>The usher HLS playlist the player will open.</summary>
    [ObservableProperty]
    private Uri? _playlist;

    [ObservableProperty]
    private bool _adFree;
}
