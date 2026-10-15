// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StreamEdge.Chat;

namespace StreamEdge.AvaloniaApp.Views;

public partial class ChatterListView : UserControl
{
    public ChatterListView()
    {
        InitializeComponent();
        // A name opens its user card, through the same event as a name
        // clicked in chat, so the window shows it the same way.
        ChatterItems.AddHandler(TappedEvent, (_, e) =>
        {
            if (e.Source is Control { DataContext: Chatter chatter } source)
            {
                RaiseEvent(new UserClickedEventArgs(ChatLine.UserClickedEvent, chatter.Login, null, source));
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ViewModels.ChatterListViewModel list)
            {
                list.CloseCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SearchBox.Focus();
    }
}
