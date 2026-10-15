// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;

namespace StreamEdge.AvaloniaApp;

public partial class App : Application
{
    /// <summary>Set by Program before Avalonia starts; tests leave the default.</summary>
    public static StartupOptions Options { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var vm = new MainViewModel(settings, Options, AvaloniaUiDispatcher.Instance);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.SaveSettings();
                vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
