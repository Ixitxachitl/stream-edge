// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace StreamEdge.AvaloniaApp.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AboutButton.Click += (_, _) =>
        {
            // Opened from a flyout, whose top level is a popup, not a window.
            var owner = TopLevel.GetTopLevel(this) as Window
                        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is not null) _ = new AboutWindow().ShowDialog(owner);
            else new AboutWindow().Show();
        };
    }
}
