// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Chat;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>The popup for a clicked name, like twitch.tv's viewer card:
/// who they are, how long they've been around, what they said here, and,
/// for moderators, what can be done about it.</summary>
public sealed partial class UserCardViewModel : ObservableObject
{
    private readonly ChannelTabViewModel _tab;
    private readonly Func<(TwitchModeration? Mod, string? Why)> _moderation;
    private readonly Action<string> _openChannel;
    private readonly ChatEntry? _clicked;
    private string? _userId;

    public UserCardViewModel(ChannelTabViewModel tab, string login, ChatEntry? clicked,
        Func<(TwitchModeration? Mod, string? Why)> moderation, Action<string> openChannel)
    {
        _tab = tab;
        _clicked = clicked;
        _moderation = moderation;
        _openChannel = openChannel;
        Login = login;

        // What the chat already knows, so the card has something at once.
        var lines = tab.Chat.Lines.Where(l => string.Equals(l.Message.Login, login, StringComparison.OrdinalIgnoreCase)).ToList();
        var latest = clicked ?? lines.LastOrDefault();
        _displayName = latest?.Message.Sender ?? login;
        // Lifted for the dark card, as names are in chat.
        NameBrush = new Avalonia.Media.SolidColorBrush(
            Views.ChatLine.Readable(Avalonia.Media.Color.Parse(UserColors.For(login, latest?.Message.Color))));
        Badges = latest?.Badges ?? [];
        _userId = latest?.Message.UserId;
        Pronoun = latest?.Pronoun;
        foreach (var line in lines.TakeLast(20)) RecentMessages.Add(line);
        CanModerate = tab.Chat.IsModerator;
        // What Twitch would refuse: anything over the broadcaster, and over
        // a moderator unless the user is the broadcaster.
        var role = ChatRoles.RoleOf(login, tab.Chat.Channel, latest?.Message.Badges ?? []);
        CanRemove = CanModerate && ChatRoles.Allows(ModPower.Remove, tab.Chat.IsBroadcaster, role);
        CanDeleteClicked = CanModerate && clicked is { IsDeleted: false, CanDelete: true };
    }

    public string Login { get; }
    public Avalonia.Media.IBrush NameBrush { get; }
    public IReadOnlyList<ChatServices.Badge> Badges { get; }
    public string? Pronoun { get; }
    public ObservableCollection<ChatEntry> RecentMessages { get; } = [];
    public bool HasRecentMessages => RecentMessages.Count > 0;
    public bool CanModerate { get; }
    public bool CanDeleteClicked { get; }

    /// <summary>Timeouts, bans and warnings: not for the broadcaster, a
    /// fellow moderator (unless the user is the broadcaster) or staff.</summary>
    public bool CanRemove { get; }

    /// <summary>Something in the moderation box applies to this user.</summary>
    public bool ShowsModeration => CanRemove || CanDeleteClicked;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private Uri? _avatar;

    [ObservableProperty]
    private string? _description;

    /// <summary>"Account created 6 years, 8 months ago".</summary>
    [ObservableProperty]
    private string? _accountAge;

    /// <summary>"Following for 1 year" (moderators only), "Not following", or null.</summary>
    [ObservableProperty]
    private string? _followAge;

    [ObservableProperty]
    private string? _role;

    /// <summary>Result of the last mod action, or why it couldn't happen.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>Reason sent with bans, timeouts and warnings.</summary>
    [ObservableProperty]
    private string _reason = "";

    public async Task LoadAsync(HttpClient http, string clientId, string? token, CancellationToken ct)
    {
        try
        {
            if (await UserCard.FetchAsync(http, Login, ct) is { } card)
            {
                _userId ??= card.Id;
                DisplayName = card.DisplayName;
                Avatar = card.ProfileImage;
                Description = card.Description;
                if (card.CreatedAt is { } created) AccountAge = $"Account created {UserCard.Age(created, DateTimeOffset.UtcNow)} ago";
                Role = card.IsPartner ? "Partner" : card.IsAffiliate ? "Affiliate" : null;
            }
            // Follow age: Helix gives it to the channel's moderators only.
            if (CanModerate && token is not null && _tab.RoomId is { } room && _userId is { } id)
            {
                var followed = await UserCard.FetchFollowedAtAsync(http, clientId, token, room, id, ct);
                FollowAge = followed is { } at ? $"Following for {UserCard.Age(at, DateTimeOffset.UtcNow)}" : "Not following";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The card still shows what the chat knew.
        }
    }

    [RelayCommand]
    private Task TimeoutAsync(string seconds) =>
        RunAsync((mod, room, id) => mod.BanAsync(room, id, int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), Reason, default),
            $"Timed out for {Duration(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture))}.");

    [RelayCommand]
    private Task BanAsync() => RunAsync((mod, room, id) => mod.BanAsync(room, id, null, Reason, default), "Banned.");

    [RelayCommand]
    private Task UnbanAsync() => RunAsync((mod, room, id) => mod.UnbanAsync(room, id, default), "Unbanned.");

    [RelayCommand]
    private Task WarnAsync()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            Status = "A warning needs a reason.";
            return Task.CompletedTask;
        }
        return RunAsync((mod, room, id) => mod.WarnAsync(room, id, Reason, default), "Warned.");
    }

    [RelayCommand]
    private Task DeleteClickedAsync()
    {
        if (_clicked?.Message.Id is not { } messageId) return Task.CompletedTask;
        return RunAsync((mod, room, _) => mod.DeleteMessageAsync(room, messageId, default), "Message deleted.");
    }

    /// <summary>"@name " into the chat box.</summary>
    [RelayCommand]
    private void Mention()
    {
        var draft = _tab.Chat.Draft;
        _tab.Chat.Draft = (draft.Length == 0 || draft.EndsWith(' ') ? draft : draft + " ") + "@" + DisplayName + " ";
    }

    [RelayCommand]
    private void OpenChannel() => _openChannel(Login);

    private async Task RunAsync(Func<TwitchModeration, string, string, Task<ModResult>> action, string done)
    {
        var (mod, why) = _moderation();
        if (mod is null)
        {
            Status = why;
            return;
        }
        if (_tab.RoomId is not { } room || _userId is not { } id)
        {
            Status = "Still loading this user; try again in a moment.";
            return;
        }
        Status = "…";
        var result = await action(mod, room, id);
        Status = result.Ok ? done : "Twitch refused: " + result.Error;
    }

    private static string Duration(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        < 3600 => $"{seconds / 60} minutes",
        < 86400 => $"{seconds / 3600} hours",
        _ => $"{seconds / 86400} days",
    };
}
