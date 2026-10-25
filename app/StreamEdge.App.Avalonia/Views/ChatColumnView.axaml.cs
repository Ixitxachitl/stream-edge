// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>One chat column: tab strip, chat lines and chat box.
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
        // Tunnel, so the caret never sees them; an open emote list keeps them.
        ChatInput.AddHandler(KeyDownEvent, OnChatInputRecall, RoutingStrategies.Tunnel);
        // Tunnel, and handled too: buttons and the text box handle their own
        // presses, and those count as using this column.
        AddHandler(PointerPressedEvent, (_, _) => Activate(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Activate(), RoutingStrategies.Bubble, handledEventsToo: true);
        // The emote menu rebuilds as it opens: sets may have loaded since.
        EmoteButton.Click += (_, _) => _column?.Main.EmoteMenu.Open();
        PointsButton.Click += (_, _) => _column?.Main.RefreshPoints();
        RedeemButton.Click += (_, _) => _column?.Main.RedeemOnTwitchCommand.Execute(null);
        ChatScroll.ScrollChanged += OnChatScrolled;
        UsersInChatItem.Click += (_, _) => { if (_column is { } c) c.Main.ShowChatters(c); };
        // Right-clicking a line offers to answer it: easier to hit than its ↩
        // while busy chat moves the line along.
        ChatScroll.AddHandler(ContextRequestedEvent, OnChatContextRequested, RoutingStrategies.Tunnel, handledEventsToo: true);
        // What's selected is read at the right press: the text would take the
        // press as the start of a new selection, and the menu taking focus
        // as it opens clears it too.
        ChatScroll.AddHandler(PointerPressedEvent, OnChatPointerPressed, RoutingStrategies.Tunnel);
        ReplyItem.Click += (_, _) => _menuLine?.RequestReply();
        CopyItem.Click += async (_, _) =>
        {
            if (_menuSelection is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };
        // What the chat box can draw and complete: emotes the user can send
        // in the channel this column shows.
        ChatInput.EmoteLookup = code => _column is { } c ? c.Main.Services.SendableEmote(code, c.Selected?.RoomId)?.ImageUrl : null;
        ChatInput.Completer = (typed, contains) => _column is { } c
            ? c.Main.Services.CompleteEmote(typed, c.Selected?.RoomId, contains)
            : [];
        // Moderator commands only where the user moderates.
        ChatInput.CommandCompleter = typed => Chat.ChatCommand.Complete(typed, _column?.Chat.IsModerator == true);
        // Spell check; the channel's and its chatters' names aren't misspellings.
        ChatInput.Spelling = () => _column?.Main.Services.Spelling;
        ChatInput.IsName = word => _column?.Chat is { } chat
            && (chat.Roster.Contains(word) || string.Equals(word, chat.Channel, StringComparison.OrdinalIgnoreCase));
        ChatInput.AddToDictionary = word => _column?.Main.Services.AddSpellingWord(word);
        // A click in the list picks that emote or command.
        SuggestionList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is { } item && ChatInput.Suggestions.IndexOf(item) is >= 0 and var index)
                ChatInput.Accept(index);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Activate()
    {
        if (_column is { IsActive: false } column) column.Main.Activate(column);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_column is not null)
        {
            _column.PropertyChanged -= OnColumnChanged;
            _column.Main.Services.SpellingChanged -= OnSpellingChanged;
        }
        _column = DataContext as ChatColumnViewModel;
        if (_column is not null)
        {
            _column.PropertyChanged += OnColumnChanged;
            _column.Main.Services.SpellingChanged += OnSpellingChanged;
        }
        FollowFromTheBottom();
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatColumnViewModel.Chat)) return;
        // Another channel: other emotes are sendable.
        ChatInput.RefreshEmotes();
        FollowFromTheBottom();
    }

    private void OnSpellingChanged() => Dispatcher.UIThread.Post(ChatInput.RefreshSpelling);

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

    /// <summary>The line the right-click menu was opened on.</summary>
    private ChatLine? _menuLine;
    /// <summary>The text selected in that line, for Copy.</summary>
    private string? _menuSelection;

    private void OnChatPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ChatScroll).Properties.IsRightButtonPressed) return;
        var text = (e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        _menuSelection = text is null ? null : ChatLine.SelectedCopyText(text);
        if (text is not null) e.Handled = true;
    }

    private void OnChatContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _menuLine = (e.Source as Visual)?.FindAncestorOfType<ChatLine>(includeSelf: true);
        CopyItem.IsVisible = !string.IsNullOrEmpty(_menuSelection);
        var label = _menuLine?.ReplyLabel;
        ReplyItem.IsVisible = label is not null;
        if (label is not null) ReplyItem.Header = label;
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _column is not { } column) return;
        column.Main.Activate(column);
        column.Main.SendCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Up and Down step through what was sent in this chat.</summary>
    private void OnChatInputRecall(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || ChatInput.IsSuggesting || _column?.Chat is not { } chat) return;
        var step = e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 };
        if (step == 0) return;
        e.Handled = true;
        if (!chat.Recall(step)) return;
        ChatInput.CaretIndex = ChatInput.Text?.Length ?? 0;
        // A recalled "/clear" would open the command list, and the next Up
        // would move through that instead.
        ChatInput.Dismiss();
    }
}
