// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The chat pane's toolbar: one across all its chats.</summary>
public partial class ChatHeaderView : UserControl
{
    private WhisperInboxViewModel? _whispers;

    public ChatHeaderView()
    {
        InitializeComponent();
        ModMenuButton.Click += (_, _) => _ = (DataContext as MainViewModel)?.ModMenu.OpenAsync();
        var inbox = InboxButton.Flyout!;
        inbox.Opened += (_, _) =>
        {
            if (_whispers is not null) _whispers.IsOpen = true;
            Dispatcher.UIThread.Post(Inbox.FocusInput);
        };
        inbox.Closed += (_, _) =>
        {
            if (_whispers is not null) _whispers.IsOpen = false;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_whispers is not null) _whispers.OpenRequested -= OpenInbox;
        _whispers = (DataContext as MainViewModel)?.Whispers;
        if (_whispers is not null) _whispers.OpenRequested += OpenInbox;
    }

    /// <summary>A whisper's alert was clicked: show the inbox under ✉.</summary>
    private void OpenInbox()
    {
        if (InboxButton.IsEffectivelyVisible) InboxButton.Flyout!.ShowAt(InboxButton);
    }
}
