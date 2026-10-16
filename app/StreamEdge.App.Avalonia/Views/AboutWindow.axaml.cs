// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace StreamEdge.AvaloniaApp.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version " + AppInfo.Version;
        CommitText.Text = AppInfo.Commit is { } commit ? "Built from commit " + commit : "";
        CommitText.IsVisible = AppInfo.Commit is not null;
        RuntimeText.Text = AppInfo.Runtime;
        LicenseText.Text = $"{AppInfo.Copyright} · {AppInfo.License}";
        SourceButton.Click += (_, _) => UrlLauncher.TryOpen(AppInfo.RepositoryUrl);
        // For bug reports: the exact build and where it runs.
        CopyButton.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync($"StreamEdge {AppInfo.FullVersion} ({AppInfo.Runtime})");
        };
        CloseButton.Click += (_, _) => Close();
    }
}
