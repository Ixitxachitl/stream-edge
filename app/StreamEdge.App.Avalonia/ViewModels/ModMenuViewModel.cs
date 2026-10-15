// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Chat;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>The 🛡 menu for channels the user moderates: chat modes, shield
/// mode and announcements, through Helix. Modes are read from Twitch when
/// the menu opens and each toggle applies at once.</summary>
public sealed partial class ModMenuViewModel : ObservableObject
{
    private readonly Func<ChannelTabViewModel?> _front;
    private readonly Func<(TwitchModeration? Mod, string? Why)> _moderation;
    // Set while loading, so filling the toggles doesn't send them back.
    private bool _loading;

    public ModMenuViewModel(Func<ChannelTabViewModel?> front, Func<(TwitchModeration? Mod, string? Why)> moderation)
    {
        _front = front;
        _moderation = moderation;
    }

    [ObservableProperty]
    private bool _slowMode;

    [ObservableProperty]
    private bool _followersOnly;

    [ObservableProperty]
    private bool _subscribersOnly;

    [ObservableProperty]
    private bool _emoteOnly;

    [ObservableProperty]
    private bool _uniqueChat;

    [ObservableProperty]
    private bool _shieldMode;

    [ObservableProperty]
    private string _announcement = "";

    [ObservableProperty]
    private string? _status;

    public string[] AnnouncementColors { get; } = ["primary", "blue", "green", "orange", "purple"];

    [ObservableProperty]
    private string _announcementColor = "primary";

    public async Task OpenAsync()
    {
        Status = null;
        if (Target() is not { } target) return;
        var settings = await target.Mod.GetChatSettingsAsync(target.Room, default);
        if (settings is null)
        {
            Status = "Couldn't read this channel's chat settings.";
            return;
        }
        _loading = true;
        SlowMode = settings.SlowSeconds is not null;
        FollowersOnly = settings.FollowerMinutes is not null;
        SubscribersOnly = settings.SubscriberOnly;
        EmoteOnly = settings.EmoteOnly;
        UniqueChat = settings.UniqueChat;
        _loading = false;
    }

    partial void OnSlowModeChanged(bool value) => _ = ApplyAsync();
    partial void OnFollowersOnlyChanged(bool value) => _ = ApplyAsync();
    partial void OnSubscribersOnlyChanged(bool value) => _ = ApplyAsync();
    partial void OnEmoteOnlyChanged(bool value) => _ = ApplyAsync();
    partial void OnUniqueChatChanged(bool value) => _ = ApplyAsync();

    partial void OnShieldModeChanged(bool value)
    {
        if (_loading || Target() is not { } target) return;
        _ = Report(target.Mod.SetShieldModeAsync(target.Room, value, default), value ? "Shield mode on." : "Shield mode off.");
    }

    /// <summary>Slow mode 30 s and followers-only 10 minutes, the presets
    /// twitch.tv's own quick menu offers.</summary>
    private async Task ApplyAsync()
    {
        if (_loading || Target() is not { } target) return;
        var settings = new ChatSettings(SlowMode ? 30 : null, FollowersOnly ? 10 : null, SubscribersOnly, EmoteOnly, UniqueChat);
        await Report(target.Mod.SetChatSettingsAsync(target.Room, settings, default), "Chat settings updated.");
    }

    [RelayCommand]
    private async Task AnnounceAsync()
    {
        if (string.IsNullOrWhiteSpace(Announcement) || Target() is not { } target) return;
        var text = Announcement.Trim();
        Announcement = "";
        await Report(target.Mod.AnnounceAsync(target.Room, text, AnnouncementColor, default), "Announced.");
    }

    private async Task Report(Task<ModResult> call, string done)
    {
        Status = "…";
        var result = await call;
        Status = result.Ok ? done : "Twitch refused: " + result.Error;
    }

    private (TwitchModeration Mod, string Room)? Target()
    {
        var (mod, why) = _moderation();
        if (mod is null)
        {
            Status = why;
            return null;
        }
        if (_front()?.RoomId is not { } room)
        {
            Status = "Still joining this channel.";
            return null;
        }
        return (mod, room);
    }
}
