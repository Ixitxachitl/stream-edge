// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>A channel search box (its DataContext a
/// <see cref="ChannelSearchViewModel"/>). Up/Down pick a suggestion, Enter
/// takes it (Shift+Enter: in the background, when joining), Esc clears the
/// box; a click on a suggestion takes it too.</summary>
public partial class ChannelPicker : UserControl
{
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<ChannelPicker, string?>(nameof(Placeholder));

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public ChannelPicker()
    {
        InitializeComponent();
        // Tunnel: the text box would otherwise take Up/Down for the caret.
        ChannelSearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Search is not { } search) return;
            switch (e.Key)
            {
                case Key.Down:
                    search.Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    search.Move(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    search.Accept(search.SelectedIndex,
                        e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? OpenMode.Background : OpenMode.NewTab);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    search.Cancel();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);
        ChannelSearchBox.LostFocus += (_, _) =>
        {
            // Not while clicking a suggestion, which takes focus for a moment.
            if (!ChannelSearchBox.IsKeyboardFocusWithin) Search?.Close();
        };
        ChannelSuggestionList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is Streams.ChannelSuggestion picked && Search is { } search)
                search.Accept(search.Suggestions.IndexOf(picked));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private ChannelSearchViewModel? Search => DataContext as ChannelSearchViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlaceholderProperty) ChannelSearchBox.PlaceholderText = Placeholder;
    }

    /// <summary>The cursor in the box.</summary>
    public void FocusBox() => ChannelSearchBox.Focus();
}
