// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.Playback.Mpv;

namespace StreamEdge.AvaloniaApp.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        StreamList.DoubleTapped += OnStreamDoubleTapped;
        WatchTabDrags();
        // Names, mentions and mod icons in chat lines (and in user cards,
        // whose recent messages are chat lines too).
        AddHandler(ChatLine.UserClickedEvent, OnUserClicked);
        AddHandler(ChatLine.ModActionEvent, OnModAction);
        Opened += (_, _) =>
        {
            // 1x emote images at up to 125% scaling, 2x above.
            Emotes.EmoteImageCache.Scale = RenderScaling;
            if (!_restored) PlaceOnScreen();
            _tracking = true;
        };
        PositionChanged += (_, _) => Remember();
        Closing += (_, _) =>
        {
            _rememberTimer?.Stop();
            RememberNow();
            _vm?.SaveSettings();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Tabs.CollectionChanged += OnTabsChanged;
        RestorePlacement();
        // Tabs opened before the window existed (demo, or restored at start-up).
        foreach (var tab in _vm.Tabs) AddVideo(tab);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.FrontTab):
            case nameof(MainViewModel.ShowPip):
                Videos.InvalidateArrange();
                break;
            case nameof(MainViewModel.VideoOnly):
                ApplyVideoOnly();
                break;
        }
    }

    private WindowState? _stateBeforeVideoOnly;

    /// <summary>Video-only: the window goes full screen with just the
    /// player in it, and comes back as it was.</summary>
    private void ApplyVideoOnly()
    {
        if (_vm is null) return;
        if (_vm.VideoOnly)
        {
            _stateBeforeVideoOnly ??= WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (_stateBeforeVideoOnly is { } before)
        {
            _stateBeforeVideoOnly = null;
            WindowState = before;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is { VideoOnly: true })
        {
            _vm.VideoOnly = false;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ----- Video surfaces: one per tab ------------------------------------

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (ChannelTabViewModel tab in e.OldItems ?? Array.Empty<ChannelTabViewModel>()) RemoveVideo(tab);
        foreach (ChannelTabViewModel tab in e.NewItems ?? Array.Empty<ChannelTabViewModel>()) AddVideo(tab);
        Videos.InvalidateArrange();
    }

    private void AddVideo(ChannelTabViewModel tab)
    {
        if (_vm is not { PlaybackEnabled: true } vm) return;
        var surface = new PlayerSurface { DataContext = tab };
        MpvPlayer? mpv = null;
        surface.HandleCreated += wid =>
        {
            try
            {
                mpv = new MpvPlayer(wid);
                vm.AttachPlayer(tab, mpv);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                vm.PlayerFailed(tab, ex.Message);
            }
        };
        surface.HandleDestroying += () =>
        {
            // mpv must let go of the window before it is destroyed.
            vm.AttachPlayer(tab, null);
            mpv?.Dispose();
            mpv = null;
        };
        // Clicks, double-clicks and the wheel over the video land on the
        // native window, never on Avalonia.
        surface.Input += input => vm.OnVideoInput(tab, input);
        tab.PropertyChanged += OnTabChanged;
        tab.Player.PropertyChanged += OnTabPlayerChanged;
        Videos.Children.Add(surface);
    }

    private void RemoveVideo(ChannelTabViewModel tab)
    {
        tab.PropertyChanged -= OnTabChanged;
        tab.Player.PropertyChanged -= OnTabPlayerChanged;
        var surface = Videos.Children.OfType<PlayerSurface>().FirstOrDefault(s => s.DataContext == tab);
        if (surface is not null) Videos.Children.Remove(surface);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelTabViewModel.IsFront)) Videos.InvalidateArrange();
    }

    private void OnTabPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsVideoVisible)) Videos.InvalidateArrange();
    }

    // ----- Stream list ----------------------------------------------------

    /// <summary>Double-click opens; a single click only selects, so browsing
    /// the list never interrupts what's playing.</summary>
    private void OnStreamDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (StreamList.SelectedItem is StreamItemViewModel item) item.OpenCommand.Execute(null);
    }

    // ----- Chat -----------------------------------------------------------

    /// <summary>A name or : show that user's card next to it.</summary>
    private void OnUserClicked(object? sender, UserClickedEventArgs e)
    {
        if (_vm?.OpenUserCard(e.Login, e.Entry) is not { } card) return;
        var flyout = new Flyout
        {
            Content = new UserCardView { DataContext = card },
            Placement = PlacementMode.Bottom,
        };
        flyout.ShowAt(e.Anchor);
        e.Handled = true;
    }

    private void OnModAction(object? sender, ModActionEventArgs e)
    {
        // The line's own channel: it may be in any column.
        var tab = _vm?.Tabs.FirstOrDefault(t => string.Equals(t.Login, e.Entry.Message.Channel, StringComparison.OrdinalIgnoreCase))
            ?? _vm?.ActiveTab;
        if (tab is not null) _ = _vm!.ModerateAsync(tab, e.Entry, e.Action);
        e.Handled = true;
    }

    // ----- Remembered placement -------------------------------------------

    private bool _restored;
    // Off until the window is open, so the restore itself and the platform's
    // first layout don't overwrite what was saved.
    private bool _tracking;

    /// <summary>Puts the window back where it was last time, before it is
    /// first shown. Skipped for --screen, and when the saved spot is no
    /// longer on any screen (a monitor was unplugged).</summary>
    private void RestorePlacement()
    {
        if (_vm is not { HasExplicitScreen: false, SavedWindow: { } saved }) return;
        var screens = Screens.All;
        var scaling = Screens.ScreenFromPoint(new PixelPoint(saved.X, saved.Y))?.Scaling ?? 1;
        var areas = screens.Select(s => (s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height));
        if (!saved.IsVisibleOn(areas, scaling)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(saved.X, saved.Y);
        Width = Math.Max(MinWidth, saved.Width);
        Height = Math.Max(MinHeight, saved.Height);
        WindowState = saved.Mode switch
        {
            WindowMode.Maximized => WindowState.Maximized,
            WindowMode.FullScreen => WindowState.FullScreen,
            _ => WindowState.Normal,
        };
        _restored = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty || change.Property == WindowStateProperty) Remember();
    }

    private DispatcherTimer? _rememberTimer;

    /// <summary>Records the placement once it has settled. Maximizing
    /// changes the size a moment before the state; recording straight away
    /// saved the maximized size as the one to return to.</summary>
    private void Remember()
    {
        if (!_tracking) return;
        _rememberTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _rememberTimer!.Stop();
            RememberNow();
        });
        _rememberTimer.Stop();
        _rememberTimer.Start();
    }

    /// <summary>Bounds only update while the window is normal-sized, so
    /// maximizing keeps the size to return to.</summary>
    private void RememberNow()
    {
        if (!_tracking || _vm is null || _vm.VideoOnly || WindowState == WindowState.Minimized) return;
        var mode = WindowState switch
        {
            WindowState.Maximized => WindowMode.Maximized,
            WindowState.FullScreen => WindowMode.FullScreen,
            _ => WindowMode.Normal,
        };
        var saved = _vm.SavedWindow;
        _vm.SavedWindow = mode == WindowMode.Normal || saved is null
            ? new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height, mode)
            : saved with { Mode = mode };
    }

    /// <summary>Moves the window onto the configured monitor, the small
    /// secondary display this app is for, and optionally fills it.</summary>
    private void PlaceOnScreen()
    {
        if (DataContext is not MainViewModel vm || vm.ScreenIndex is not { } index) return;
        var screens = Screens.All;
        if (index < 0 || index >= screens.Count) return;
        var area = screens[index].WorkingArea;
        Position = area.Position;
        if (vm.Fullscreen)
        {
            WindowState = WindowState.FullScreen;
        }
        else
        {
            var scale = screens[index].Scaling;
            Width = area.Width / scale;
            Height = area.Height / scale;
        }
    }
}
