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
        WireChannelSearch();
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

    /// <summary>Up/Down pick a suggestion, Enter joins it in a new tab
    /// (Shift+Enter: in the background), Esc clears the box; a click on a
    /// suggestion joins it too.</summary>
    private void WireChannelSearch()
    {
        // Tunnel: the text box would otherwise take Up/Down for the caret.
        ChannelSearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Vm?.Search is not { } search) return;
            switch (e.Key)
            {
                case Key.Down:
                    search.Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    search.Move(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    search.Accept(search.SelectedIndex,
                        e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? OpenMode.Background : OpenMode.NewTab);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    search.Cancel();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);
        ChannelSearchBox.LostFocus += (_, _) =>
        {
            // Not while clicking a suggestion, which takes focus for a moment.
            if (!ChannelSearchBox.IsKeyboardFocusWithin) Vm?.Search.Close();
        };
        ChannelSuggestionList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is Streams.ChannelSuggestion picked && Vm?.Search is { } search)
                search.Accept(search.Suggestions.IndexOf(picked));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
