// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace StreamEdge.Auth;

/// <summary>Named secrets (OAuth tokens, the playback auth-token) kept out of
/// settings.json, under <c>secrets/</c> in <see cref="AppData"/>.
///
/// Windows: DPAPI, current-user scope, so a copied file is useless on another
/// account or machine. Linux/macOS: a plain file with 0600 permissions; moving
/// these to libsecret / Keychain is on the roadmap (docs/PLAN.md).</summary>
public static class SecretStore
{
    public const string OAuthToken = "oauth-token";
    public const string RefreshToken = "refresh-token";
    /// <summary>The twitch.tv "auth-token" browser cookie, used only for the
    /// GQL playback-token request (ad-free for subs/Turbo).</summary>
    public const string PlaybackAuthToken = "playback-auth-token";

    private static readonly byte[] Entropy = "StreamEdge.SecretStore.v1"u8.ToArray();

    private static string PathOf(string name)
    {
        var dir = AppData.PathFor("secrets");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static string? Get(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? Unprotect(bytes) : bytes);
        }
        catch (CryptographicException)
        {
            // Written by another user or machine: treat as signed out.
            return null;
        }
    }

    public static void Set(string name, string? value)
    {
        var path = PathOf(name);
        if (string.IsNullOrEmpty(value))
        {
            File.Delete(path);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, Protect(bytes));
            return;
        }
        // Create with 0600 before any secret byte is written.
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(bytes);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
