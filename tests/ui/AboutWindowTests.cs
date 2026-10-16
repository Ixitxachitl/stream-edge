// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.Views;
using Xunit;

namespace StreamEdge.UiTests;

public class AboutWindowTests(HeadlessAvalonia ui) : UiTest(ui)
{
    [Fact]
    public void ShowsTheVersionAndWhereTheSourceIs() => Ui(() =>
    {
        var window = new AboutWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Version " + StreamEdge.AppInfo.Version, texts);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Source on GitHub");

        if (Environment.GetEnvironmentVariable("STREAMEDGE_SNAPSHOT") is { Length: > 0 } path)
#pragma warning disable CS0618
            frame!.Save(path.Replace(".png", "-about.png"));
#pragma warning restore CS0618
        window.Close();
    });
}
