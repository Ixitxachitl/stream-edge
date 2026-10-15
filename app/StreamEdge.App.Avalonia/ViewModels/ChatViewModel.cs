// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StreamEdge.Chat;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>A chat line plus its formatted time, so the view needn't know
/// the user's timestamp format.</summary>
public sealed record ChatEntry(ChatMessage Message, string Time);

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ChatViewModel(AppSettings settings) => _settings = settings;

    public ObservableCollection<ChatEntry> Lines { get; } = [];

    [ObservableProperty]
    private string _channel = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _draft = "";

    /// <summary>Sending needs a signed-in account; anonymous chat is read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private bool _canSend;

    public string Placeholder => CanSend ? "Send a message" : "Log in to chat";

    public string Header => Channel.Length == 0 ? "Chat" : "#" + Channel;

    partial void OnChannelChanged(string value) => OnPropertyChanged(nameof(Header));

    public void Add(ChatMessage message)
    {
        var time = _settings.TimestampFormat.Length == 0 ? "" : message.Timestamp.ToString(_settings.TimestampFormat);
        Lines.Add(new ChatEntry(message, time));
        while (Lines.Count > Math.Max(50, _settings.ChatScrollback))
            Lines.RemoveAt(0);
    }

    public void Clear() => Lines.Clear();
}
