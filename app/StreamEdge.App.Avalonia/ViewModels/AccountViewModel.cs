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
        _clientId = settings.ClientId ?? "";
        _hasPlaybackToken = SecretStore.Get(SecretStore.PlaybackAuthToken) is not null;
    }

    /// <summary>Raised on the UI thread after sign-in or sign-out.</summary>
    public event Action? AccountChanged;

    public string? Login => _settings.AccountLogin;
    public string? UserId => _settings.AccountUserId;
    public bool IsSignedIn => Login is not null && SecretStore.Get(SecretStore.OAuthToken) is not null;
    public string Summary => IsSignedIn ? "Signed in as " + Login : "Not signed in";
    public string? AccessToken => SecretStore.Get(SecretStore.OAuthToken);

    [ObservableProperty]
    private string _clientId;

    /// <summary>The code to type at <see cref="VerificationUri"/> while a
    /// sign-in is pending.</summary>
    [ObservableProperty]
    private string? _userCode;

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

    partial void OnClientIdChanged(string value) =>
        _settings.ClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            Message = "Enter the Client ID of your Twitch app (Public client) first.";
            return;
        }
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        var ct = _signIn.Token;
        var auth = new TwitchAuth(_http, _settings.ClientId);
        try
        {
            var code = await auth.StartAsync(ct);
            UserCode = code.UserCode;
            VerificationUri = code.VerificationUri;
            Message = "Approve the code at " + code.VerificationUri;
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
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (_settings.ClientId is null || AccessToken is not { } token) return;
        var auth = new TwitchAuth(_http, _settings.ClientId);
        try
        {
            if (await auth.ValidateAsync(token, ct) is not null) return;
            if (SecretStore.Get(SecretStore.RefreshToken) is { } refresh
                && await auth.RefreshAsync(refresh, ct) is { } fresh)
            {
                SecretStore.Set(SecretStore.OAuthToken, fresh.AccessToken);
                SecretStore.Set(SecretStore.RefreshToken, fresh.RefreshToken ?? refresh);
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Offline: keep the token and try again next start.
            return;
        }
        SignOut();
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
