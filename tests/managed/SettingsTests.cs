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
    public void OldFilesGetChatExtraDefaults()
    {
        // A settings.json from before the chat-extras toggles existed.
        File.WriteAllText(AppData.PathFor(AppSettings.FileName), """{ "Channel": "foo", "Volume": 40 }""");
        var loaded = AppSettings.Load();
        Assert.Equal("foo", loaded.Channel);
        Assert.True(loaded.ShowPronouns);
        Assert.True(loaded.UseBttv);
        Assert.True(loaded.UseFfz);
        Assert.True(loaded.UseSevenTv);
        Assert.False(loaded.ShowJoinPart);
    }

    [Fact]
    public void ChatExtraTogglesRoundTrip()
    {
        new AppSettings { ShowPronouns = false, UseBttv = false, UseFfz = false, UseSevenTv = false, ShowJoinPart = true }.Save();
        var loaded = AppSettings.Load();
        Assert.False(loaded.ShowPronouns);
        Assert.False(loaded.UseBttv);
        Assert.False(loaded.UseFfz);
        Assert.False(loaded.UseSevenTv);
        Assert.True(loaded.ShowJoinPart);
    }

    [Fact]
    public void WindowPlacementAndProportionsRoundTrip()
    {
        new AppSettings { Window = new WindowPlacement(1920, 0, 1920, 480, WindowMode.Maximized), ChatShare = 0.3 }.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(new WindowPlacement(1920, 0, 1920, 480, WindowMode.Maximized), loaded.Window);
        Assert.Equal(0.3, loaded.ChatShare);
    }

    [Fact]
    public void PanePlacesRoundTrip()
    {
        var settings = new AppSettings();
        settings.SetPanePlaces(PaneArrangement.MoveStacked(settings.PanePlaces, PaneId.Panel, PaneId.List, below: false));
        settings.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(settings.PanePlaces, loaded.PanePlaces);
        Assert.True(loaded.PanePlaces[PaneId.List].Below);
        Assert.Equal(DockSide.Left, loaded.PanePlaces[PaneId.Panel].Side);
    }

    [Fact]
    public void ThumbnailCellsRoundTripPerChannel()
    {
        var settings = new AppSettings();
        settings.PipCells["SomeOne"] = new PipCell(2, 1);
        settings.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(new PipCell(2, 1), loaded.PipCells["someone"]);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndIsKept()
    {
        File.WriteAllText(AppData.PathFor(AppSettings.FileName), "{ not json");
        Assert.Null(AppSettings.Load().Channel);
        Assert.True(File.Exists(AppData.PathFor(AppSettings.FileName) + ".bad"));
    }

    [Fact]
    public void FileZeroedByACrashFallsBackToThePreviousSave()
    {
        new AppSettings { Channel = "first" }.Save();
        new AppSettings { Channel = "second" }.Save();
        // What NTFS leaves after a power cut: the new length, but NULs.
        var path = AppData.PathFor(AppSettings.FileName);
        File.WriteAllBytes(path, new byte[1000]);
        Assert.Equal("first", AppSettings.Load().Channel);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void SigningOutRemovesTheSecretsBackupToo()
    {
        SecretStore.Set(SecretStore.OAuthToken, "old");
        SecretStore.Set(SecretStore.OAuthToken, "new");
        Assert.Equal("new", SecretStore.Get(SecretStore.OAuthToken));
        SecretStore.Set(SecretStore.OAuthToken, null);
        Assert.Null(SecretStore.Get(SecretStore.OAuthToken));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "secrets")));
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
