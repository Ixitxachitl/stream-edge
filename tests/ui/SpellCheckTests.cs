// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views;
using StreamEdge.AvaloniaApp.Views.Emotes;
using StreamEdge.Ui;
using Xunit;

namespace StreamEdge.UiTests;

/// <summary>Spell check in the chat box: underlines, the right-click
/// spellings, and adding a word.</summary>
public class SpellCheckTests(HeadlessAvalonia ui) : UiTest(ui)
{
    private static (MainViewModel Vm, MainWindow Window, EmoteTextBox Box) Open(AppSettings? settings = null)
    {
        var vm = new MainViewModel(settings ?? new AppSettings(), new StartupOptions { Demo = true }, InlineDispatcher.Instance);
        var window = new MainWindow { DataContext = vm, Width = 1920, Height = 480 };
        window.Show();
        // The dictionary loads in the background.
        var wait = DateTime.Now + TimeSpan.FromSeconds(10);
        while (vm.Services.Spelling is null && DateTime.Now < wait) Thread.Sleep(20);
        Assert.NotNull(vm.Services.Spelling);
        Dispatcher.UIThread.RunJobs();
        var box = window.GetVisualDescendants().OfType<EmoteTextBox>().First(b => b.Name == "ChatInput");
        return (vm, window, box);
    }

    private static List<string> Underlined(EmoteTextBox box)
    {
        var presenter = box.GetVisualDescendants().OfType<EmoteTextPresenter>().Single();
        var text = box.Text ?? "";
        return [.. presenter.Misspelled().Select(w => text.Substring(w.Start, w.Length))];
    }

    private static void Type(EmoteTextBox box, string text)
    {
        box.Text = text;
        box.CaretIndex = text.Length;
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void MisspelledWordsAreUnderlinedOnceTyped() => Ui(() =>
    {
        var (vm, window, box) = Open();
        vm.FrontTab!.Chat.CanSend = true;

        // The word still being typed waits; names in the chat don't count.
        Type(box, "hello wrold, ChattyNarwhal thsi");
        Assert.Equal(["wrold"], Underlined(box));
        Type(box, "hello wrold, ChattyNarwhal thsi ");
        Assert.Equal(["wrold", "thsi"], Underlined(box));

        // Off in settings: nothing underlined.
        vm.SpellCheck = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Underlined(box));
        window.Close();
    });

    [Fact]
    public void RightClickOffersSpellingsAndAddingTheWord() => Ui(() =>
    {
        var settings = new AppSettings();
        var (vm, window, box) = Open(settings);
        vm.FrontTab!.Chat.CanSend = true;
        Type(box, "did you recieve it ");

        Assert.Null(box.SpellingMenuAt(1));             // "did" is fine
        var menu = box.SpellingMenuAt(10)!;
        var items = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Assert.Equal("receive", items[0]);
        Assert.Contains("Add to dictionary", items);
        Assert.Contains("Paste", items);

        // A spelling takes the word's place.
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("did you receive it ", box.Text);
        Assert.Empty(Underlined(box));

        // Adding a word: never underlined again, and remembered.
        Type(box, "a blorpus appears ");
        Assert.Equal(["blorpus"], Underlined(box));
        box.SpellingMenuAt(4)!.Items.OfType<MenuItem>().Single(i => i.Header as string == "Add to dictionary")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Underlined(box));
        Assert.Equal(["blorpus"], settings.SpellingWords);
        window.Close();
    });

    [Fact]
    public void YourWordsAreListedInSettingsAndCanBeRemoved() => Ui(() =>
    {
        var settings = new AppSettings { SpellingWords = ["zorp", "blorpus"] };
        var (vm, window, box) = Open(settings);
        vm.FrontTab!.Chat.CanSend = true;
        Type(box, "a blorpus and a zorp ");
        Assert.Empty(Underlined(box));

        var panel = new Window { Content = new SettingsView { DataContext = vm }, Width = 420, Height = 900 };
        panel.Show();
        Dispatcher.UIThread.RunJobs();
        var list = panel.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "SpellingWords");
        Assert.True(list.IsEffectivelyVisible);
        // A to Z, each with its ×.
        Assert.Equal(["blorpus", "zorp"], list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => t != "×"));

        var remove = list.GetVisualDescendants().OfType<Button>().First();
        remove.Command!.Execute(remove.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["zorp"], settings.SpellingWords);
        Assert.Equal(["blorpus"], Underlined(box));

        // The last one gone: so is the list.
        remove = list.GetVisualDescendants().OfType<Button>().Single();
        remove.Command!.Execute(remove.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.False(list.IsEffectivelyVisible);
        panel.Close();
        window.Close();
    });
}
