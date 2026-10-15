// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

public partial class MainWindow : Window
{
    /// <summary>How close to the bottom (in pixels) still counts as "following"
    /// chat. Scrolled further up, new lines don't yank the view down.</summary>
    private const double FollowSlack = 24;

    public MainWindow()
    {
        InitializeComponent();
        ChatInput.KeyDown += OnChatInputKeyDown;
        Opened += (_, _) => PlaceOnScreen();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
            vm.Chat.Lines.CollectionChanged += OnChatLinesChanged;
    }

    private void OnChatLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var following = ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - FollowSlack;
        if (following)
            Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel vm) return;
        vm.SendCommand.Execute(null);
        e.Handled = true;
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
