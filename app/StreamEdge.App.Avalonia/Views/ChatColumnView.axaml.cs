// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>One chat column: tab strip, header, chat lines and chat box.
/// Clicking or typing anywhere in it makes it the active column, the one
/// the chat box, emote menu and mod menu act on.</summary>
public partial class ChatColumnView : UserControl
{
    /// <summary>How close to the bottom (in pixels) still counts as "following"
    /// chat. Scrolled further up, new lines don't yank the view down.</summary>
    private const double FollowSlack = 24;

    private ChatColumnViewModel? _column;

    /// <summary>Chat sticks to the bottom until the user scrolls up, and
    /// again once they scroll back down: whatever makes it taller (new
    /// lines, history arriving, emotes loading) keeps the end in view.</summary>
    private bool _following = true;

    public ChatColumnView()
    {
        InitializeComponent();
        ChatInput.KeyDown += OnChatInputKeyDown;
        // Tunnel, and handled too: buttons and the text box handle their own
        // presses, and those count as using this column.
        AddHandler(PointerPressedEvent, (_, _) => Activate(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Activate(), RoutingStrategies.Bubble, handledEventsToo: true);
        // The emote menu rebuilds as it opens: sets may have loaded since.
        EmoteButton.Click += (_, _) => _column?.Main.EmoteMenu.Open();
        ModMenuButton.Click += (_, _) => _ = _column?.Main.ModMenu.OpenAsync();
        PointsButton.Click += (_, _) => _column?.Main.RefreshPoints();
        RedeemButton.Click += (_, _) => _column?.Main.RedeemOnTwitchCommand.Execute(null);
        ChatScroll.ScrollChanged += OnChatScrolled;
        // What the chat box can draw and complete: emotes the user can send
        // in the channel this column shows.
        ChatInput.EmoteLookup = code => _column is { } c ? c.Main.Services.SendableEmote(code, c.Selected?.RoomId)?.ImageUrl : null;
        ChatInput.Completer = (typed, contains) => _column is { } c
            ? c.Main.Services.CompleteEmote(typed, c.Selected?.RoomId, contains)
            : [];
        // A click in the list picks that emote.
        SuggestionList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is Chat.Emotes.Emote emote)
                ChatInput.Accept(ChatInput.Suggestions.IndexOf(emote));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Activate()
    {
        if (_column is { IsActive: false } column) column.Main.Activate(column);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_column is not null) _column.PropertyChanged -= OnColumnChanged;
        _column = DataContext as ChatColumnViewModel;
        if (_column is not null) _column.PropertyChanged += OnColumnChanged;
        FollowFromTheBottom();
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatColumnViewModel.Chat)) return;
        // Another channel: other emotes are sendable.
        ChatInput.RefreshEmotes();
        FollowFromTheBottom();
    }

    /// <summary>A chat just shown starts at its newest line.</summary>
    private void FollowFromTheBottom()
    {
        _following = true;
        Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnChatScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            // Content or the pane changed size, not the user scrolling.
            if (_following) ChatScroll.ScrollToEnd();
        }
        else if (e.OffsetDelta.Y != 0)
        {
            _following = ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - FollowSlack;
        }
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _column is not { } column) return;
        column.Main.Activate(column);
        column.Main.SendCommand.Execute(null);
        e.Handled = true;
    }
}
