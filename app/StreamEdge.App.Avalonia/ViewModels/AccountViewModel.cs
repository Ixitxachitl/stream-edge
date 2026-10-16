// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Auth;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>Sign-in (OAuth device code) and the optional playback auth-token.
/// The two are independent: sign-in powers the stream list and sending chat;
/// the auth-token only changes which HLS stream GQL hands back.</summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private CancellationTokenSource? _signIn;

    public AccountViewModel(AppSettings settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
        _hasPlaybackToken = SecretStore.Get(SecretStore.PlaybackAuthToken) is not null;
    }

    /// <summary>Raised on the UI thread after sign-in or sign-out.</summary>
    public event Action? AccountChanged;

    /// <summary>The access token was renewed (same account): whatever holds
    /// a copy, like the chat connection, takes the new one.</summary>
    public event Action<string>? TokenRenewed;

    public string? Login => _settings.AccountLogin;
    public string? UserId => _settings.AccountUserId;
    public bool IsSignedIn => Login is not null && SecretStore.Get(SecretStore.OAuthToken) is not null;
    public string Summary => IsSignedIn ? "Signed in as " + Login : "Not signed in";
    public string? AccessToken => SecretStore.Get(SecretStore.OAuthToken);

    /// <summary>The device code, shown small as a fallback in case the browser
    /// page didn't pre-fill it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private string? _userCode;

    /// <summary>A sign-in is waiting for the user to authorise in the browser.</summary>
    public bool IsPending => UserCode is not null;

    [ObservableProperty]
    private string? _verificationUri;

    [ObservableProperty]
    private string _message = "";

    /// <summary>Bound to the auth-token box. Never read back from storage:
    /// the box only accepts a new value.</summary>
    [ObservableProperty]
    private string _playbackTokenDraft = "";

    [ObservableProperty]
    private bool _hasPlaybackToken;

    /// <summary>"Log in with Twitch": opens Twitch's own sign-in page in the
    /// browser with the device code already filled in; the user just logs in
    /// and clicks Authorize, and this picks the token up by polling.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        var ct = _signIn.Token;
        var auth = new TwitchAuth(_http, _settings.EffectiveClientId);
        try
        {
            var code = await auth.StartAsync(ct);
            VerificationUri = code.VerificationUri;
            UserCode = code.UserCode;
            Message = UrlLauncher.TryOpen(code.VerificationUri)
                ? "Finish logging in in your browser…"
                : "Open " + code.VerificationUri + " and enter the code below.";
            var tokens = await auth.WaitForApprovalAsync(code, ct);
            if (tokens is null)
            {
                Message = "Sign-in expired or was declined.";
                return;
            }
            var who = await auth.ValidateAsync(tokens.AccessToken, ct);
            if (who is null)
            {
                Message = "Twitch rejected the new token.";
                return;
            }
            NoteScopes(who);
            NoteValidated(who);
            SecretStore.Set(SecretStore.OAuthToken, tokens.AccessToken);
            SecretStore.Set(SecretStore.RefreshToken, tokens.RefreshToken);
            _settings.AccountLogin = who.Login;
            _settings.AccountUserId = who.UserId;
            _settings.Save();
            Message = "";
            RaiseAccountChanged();
        }
        catch (OperationCanceledException)
        {
            Message = "";
        }
        catch (HttpRequestException ex)
        {
            Message = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            UserCode = null;
            VerificationUri = null;
        }
    }

    [RelayCommand]
    private void OpenSignInPage()
    {
        if (VerificationUri is { } url) UrlLauncher.TryOpen(url);
    }

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand]
    private void SignOut()
    {
        _signIn?.Cancel();
        SecretStore.Set(SecretStore.OAuthToken, null);
        SecretStore.Set(SecretStore.RefreshToken, null);
        _settings.AccountLogin = null;
        _settings.AccountUserId = null;
        _settings.Save();
        RaiseAccountChanged();
    }

    /// <summary>Re-checks the stored token at start-up (Twitch requires it),
    /// refreshing it if it has expired. Signs out if neither works.</summary>
    /// <summary>The token can read the user's own emotes. Sign-ins from
    /// before that permission was requested can't, until the user logs in again.</summary>
    public bool CanUseOwnEmotes { get; private set; }

    /// <summary>The token carries the moderation scopes (logins from
    /// before they were requested don't).</summary>
    public bool CanModerate { get; private set; }

    /// <summary>The token may read the full viewer list where the user is a mod.</summary>
    public bool CanReadChatters { get; private set; }

    private void NoteScopes(TwitchAuth.Identity identity)
    {
        CanUseOwnEmotes = identity.Scopes?.Contains(TwitchAuth.EmotesScope) == true;
        OnPropertyChanged(nameof(CanUseOwnEmotes));
        CanModerate = identity.Scopes?.Contains(TwitchAuth.ModerationScope) == true;
        OnPropertyChanged(nameof(CanModerate));
        CanReadChatters = identity.Scopes?.Contains(TwitchAuth.ChattersScope) == true;
        OnPropertyChanged(nameof(CanReadChatters));
    }

    public Task RestoreAsync(CancellationToken ct) => FreshTokenAsync(ct, force: true);

    // ----- Keeping the token valid -------------------------------------------

    /// <summary>Twitch asks apps to validate tokens hourly.</summary>
    private static readonly TimeSpan ValidateEvery = TimeSpan.FromHours(1);

    /// <summary>Renewed this long before it runs out, not after.</summary>
    private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _renewing = new(1, 1);
    private DateTimeOffset _validatedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _expiresAt = DateTimeOffset.MaxValue;

    private void NoteValidated(TwitchAuth.Identity identity)
    {
        _validatedAt = DateTimeOffset.UtcNow;
        // 0 means it doesn't expire.
        _expiresAt = identity.ExpiresIn > 0 ? _validatedAt.AddSeconds(identity.ExpiresIn) : DateTimeOffset.MaxValue;
    }

    /// <summary>A token Twitch will accept now, renewed with the refresh
    /// token when it has run out or is about to (user tokens last about four
    /// hours, and this app runs all day); null when signed out, or signed
    /// out now because Twitch no longer takes either token. Offline keeps
    /// the token for later. <paramref name="force"/>: check now (after a 401).</summary>
    public async Task<string?> FreshTokenAsync(CancellationToken ct, bool force = false)
    {
        await _renewing.WaitAsync(ct);
        try
        {
            if (AccessToken is not { } token) return null;
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _validatedAt < ValidateEvery && _expiresAt - now > RenewAhead) return token;

            var auth = new TwitchAuth(_http, _settings.EffectiveClientId);
            try
            {
                if (await auth.ValidateAsync(token, ct) is { } valid && valid.ExpiresIn is 0 or > 600)
                {
                    NoteScopes(valid);
                    NoteValidated(valid);
                    return token;
                }
                if (SecretStore.Get(SecretStore.RefreshToken) is { } refresh
                    && await auth.RefreshAsync(refresh, ct) is { } fresh)
                {
                    SecretStore.Set(SecretStore.OAuthToken, fresh.AccessToken);
                    SecretStore.Set(SecretStore.RefreshToken, fresh.RefreshToken ?? refresh);
                    if (await auth.ValidateAsync(fresh.AccessToken, ct) is { } renewed)
                    {
                        NoteScopes(renewed);
                        NoteValidated(renewed);
                    }
                    TokenRenewed?.Invoke(fresh.AccessToken);
                    return fresh.AccessToken;
                }
            }
            catch (HttpRequestException)
            {
                // Offline: keep the token and try again later.
                return token;
            }
            SignOut();
            return null;
        }
        finally
        {
            _renewing.Release();
        }
    }

    [RelayCommand]
    private void SavePlaybackToken()
    {
        // Accept a pasted cookie line ("auth-token=abc") as well as the bare value.
        var value = PlaybackTokenDraft.Trim();
        if (value.StartsWith("auth-token=", StringComparison.OrdinalIgnoreCase))
            value = value["auth-token=".Length..].Split(';')[0];
        SecretStore.Set(SecretStore.PlaybackAuthToken, value);
        PlaybackTokenDraft = "";
        HasPlaybackToken = value.Length > 0;
    }

    [RelayCommand]
    private void ClearPlaybackToken()
    {
        SecretStore.Set(SecretStore.PlaybackAuthToken, null);
        HasPlaybackToken = false;
    }

    private void RaiseAccountChanged()
    {
        OnPropertyChanged(nameof(Login));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(Summary));
        AccountChanged?.Invoke();
    }
}
