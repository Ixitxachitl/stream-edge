// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;

namespace StreamEdge.AvaloniaApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Options = StartupOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
