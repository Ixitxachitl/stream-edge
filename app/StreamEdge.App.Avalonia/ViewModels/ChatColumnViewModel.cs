// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>One chat column: its own tab strip, the selected tab's chat, and
/// a chat box. Several sit side by side (Chatterino-style splits) once a tab
/// is dragged out beside another. Which tab is on video is separate: the
/// last tab clicked in any column (<see cref="ChannelTabViewModel.IsFront"/>),
/// so typing in one column doesn't switch the video away from another.</summary>
public sealed partial class ChatColumnViewModel : ObservableObject
{
    private readonly ChatViewModel _idleChat;

    public ChatColumnViewModel(MainViewModel main, ChatViewModel idleChat)
    {
        Main = main;
        _idleChat = idleChat;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>For what's shared across columns: the player, the menus.</summary>
    public MainViewModel Main { get; }

    public ObservableCollection<ChannelTabViewModel> Tabs { get; } = [];

    /// <summary>The tab whose chat this column shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chat), nameof(HasChannel))]
    private ChannelTabViewModel? _selected;

    partial void OnSelectedChanged(ChannelTabViewModel? oldValue, ChannelTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        // The viewer list is the old channel's.
        Chatters = null;
    }

    /// <summary>"Users in chat", open over this column's chat; null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChatters))]
    private ChatterListViewModel? _chatters;

    public bool ShowsChatters => Chatters is not null;

    /// <summary>The column the chat box, emote menu and mod menu act on: the
    /// last one clicked or typed in.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Sits under the chat before it, in the same column, rather
    /// than beside it.</summary>
    [ObservableProperty]
    private bool _stacksBelow;

    public ChatViewModel Chat => Selected?.Chat ?? _idleChat;

    public bool HasChannel => Selected is not null;

    public bool HasTabs => Tabs.Count > 0;
}
