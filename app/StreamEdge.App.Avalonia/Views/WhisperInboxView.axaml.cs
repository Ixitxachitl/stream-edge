// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StreamEdge.AvaloniaApp.ViewModels;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>The ✉ inbox, in the chat header's flyout.</summary>
public partial class WhisperInboxView : UserControl
{
    private WhisperInboxViewModel? _vm;

    public WhisperInboxView()
    {
        InitializeComponent();
        // Enter sends, Shift+Enter is a new line, as in the chat box.
        WhisperInput.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            if (_vm is { } vm && vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Emotes and spelling as in the chat box, with no channel's own emotes.
        WhisperInput.EmoteLookup = code => _vm?.Services?.SendableEmote(code, null)?.ImageUrl;
        WhisperInput.Completer = (typed, contains) => _vm?.Services?.CompleteEmote(typed, null, contains) ?? [];
        WhisperInput.Spelling = () => _vm?.Services?.Spelling;
        WhisperInput.IsName = word => _vm?.IsName(word) == true;
        WhisperInput.AddToDictionary = word => _vm?.Services?.AddSpellingWord(word);
        // Newest at the bottom, in view: a line arriving or another
        // conversation chosen grows or swaps what's scrolled.
        WhisperScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0) WhisperScroll.ScrollToEnd();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.RecipientPicked -= OnRecipientPicked;
            if (_vm.Services is { } old) old.SpellingChanged -= OnSpellingChanged;
        }
        _vm = DataContext as WhisperInboxViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.RecipientPicked += OnRecipientPicked;
            if (_vm.Services is { } services) services.SpellingChanged += OnSpellingChanged;
        }
        WhisperInput.RefreshEmotes();
    }

    private void OnSpellingChanged() => Dispatcher.UIThread.Post(WhisperInput.RefreshSpelling);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Once the boxes have swapped over.
        if (e.PropertyName == nameof(WhisperInboxViewModel.Composing)) Dispatcher.UIThread.Post(FocusInput);
    }

    /// <summary>A name picked (Enter or a click): on to the message.</summary>
    private void OnRecipientPicked() => WhisperInput.Focus();

    /// <summary>The cursor where the next thing goes: who to, when writing
    /// to someone new, else the reply box.</summary>
    public void FocusInput()
    {
        if (_vm is { Composing: true } && _vm.Recipients.Query.Length == 0) ToInput.FocusBox();
        else WhisperInput.Focus();
    }
}
