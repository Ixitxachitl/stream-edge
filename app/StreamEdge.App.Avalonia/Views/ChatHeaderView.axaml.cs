// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The chat pane's toolbar: one across all its chats.</summary>
public partial class ChatHeaderView : UserControl
{
    public ChatHeaderView()
    {
        InitializeComponent();
        ModMenuButton.Click += (_, _) => _ = (DataContext as MainViewModel)?.ModMenu.OpenAsync();
    }
}
