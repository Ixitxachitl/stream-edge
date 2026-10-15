// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.Auth;
using StreamEdge.Streams;
using Xunit;

namespace StreamEdge.Tests;

[Collection(nameof(AppDataCollection))]
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "StreamEdge.Tests." + Guid.NewGuid().ToString("N"));

    public SettingsTests() => AppData.Override = _dir;

    public void Dispose()
    {
        AppData.Override = null;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RoundTrips()
    {
        new AppSettings { Channel = "foo", StreamSort = StreamSortOrder.Game, Favorites = ["a"] }.Save();
        var loaded = AppSettings.Load();
        Assert.Equal("foo", loaded.Channel);
        Assert.Equal(StreamSortOrder.Game, loaded.StreamSort);
        Assert.Equal(["a"], loaded.Favorites);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndIsKept()
    {
        File.WriteAllText(AppData.PathFor(AppSettings.FileName), "{ not json");
        Assert.Null(AppSettings.Load().Channel);
        Assert.True(File.Exists(AppData.PathFor(AppSettings.FileName) + ".bad"));
    }

    [Fact]
    public void SecretsRoundTripAndAreNotPlaintextOnWindows()
    {
        SecretStore.Set(SecretStore.PlaybackAuthToken, "hunter2token");
        Assert.Equal("hunter2token", SecretStore.Get(SecretStore.PlaybackAuthToken));
        var raw = File.ReadAllText(Path.Combine(_dir, "secrets", SecretStore.PlaybackAuthToken));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("hunter2token", raw);

        SecretStore.Set(SecretStore.PlaybackAuthToken, null);
        Assert.Null(SecretStore.Get(SecretStore.PlaybackAuthToken));
    }

    [Fact]
    public void SecretsNeverLandInSettings()
    {
        SecretStore.Set(SecretStore.OAuthToken, "tok123");
        new AppSettings { AccountLogin = "me" }.Save();
        Assert.DoesNotContain("tok123", File.ReadAllText(AppData.PathFor(AppSettings.FileName)));
    }
}

/// <summary>AppData.Override is process-wide; keep its users serial.</summary>
[CollectionDefinition(nameof(AppDataCollection), DisableParallelization = true)]
public sealed class AppDataCollection;
