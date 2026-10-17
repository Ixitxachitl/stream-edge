// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

public class StreamListHighlightTests(HeadlessAvalonia ui) : UiTest(ui)
{
    [Fact]
    public void TheSelectedRowIsNotHighlighted() => Ui(() =>
    {
        var vm = new MainViewModel(new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        vm.Streams.Selected = vm.Streams.Items[2];
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("StreamList")!;
        var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(2));
        Assert.True(row.IsSelected);
        var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.True(presenter.Background is null or ISolidColorBrush { Color.A: 0 });
        window.Close();
    });
}
