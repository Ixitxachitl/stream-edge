// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

public partial class StreamListView : UserControl
{
    public StreamListView()
    {
        InitializeComponent();
        StreamList.DoubleTapped += OnStreamDoubleTapped;
        // Scrolling puts away a row's tooltip: one left open sits under the
        // pointer and takes the wheel, stopping the list mid-scroll.
        StreamList.AddHandler(PointerWheelChangedEvent, (_, _) => CloseTooltips(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void CloseTooltips()
    {
        foreach (var row in StreamList.GetRealizedContainers())
            if (row is ListBoxItem { Content: not null } item && item.Presenter?.Child is Control content)
                ToolTip.SetIsOpen(content, false);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Double-click opens; a single click only selects, so browsing
    /// the list never interrupts what's playing.</summary>
    private void OnStreamDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (StreamList.SelectedItem is StreamItemViewModel item) item.OpenCommand.Execute(null);
    }
}
