// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>The chat box: emotes drawn inline as they're typed, and emote
/// completion. Tab completes the word at the caret and, pressed again,
/// cycles through the other matches (Shift+Tab back); typing ':' and a few
/// letters lists matches as you go, as on twitch.tv, for Tab, Enter or a
/// click to pick. Up and Down move through the list, Esc closes it.</summary>
public sealed class EmoteTextBox : TextBox
{
    public static readonly DirectProperty<EmoteTextBox, bool> IsSuggestingProperty =
        AvaloniaProperty.RegisterDirect<EmoteTextBox, bool>(nameof(IsSuggesting), o => o.IsSuggesting);

    public static readonly DirectProperty<EmoteTextBox, int> SuggestionIndexProperty =
        AvaloniaProperty.RegisterDirect<EmoteTextBox, int>(nameof(SuggestionIndex), o => o.SuggestionIndex,
            (o, v) => o.SuggestionIndex = v);

    /// <summary>Shortest ':'-word, colon excluded, that lists suggestions.</summary>
    public const int ColonMinimum = 2;

    private EmoteTextPresenter? _presenter;
    private Func<string, Uri?>? _emoteLookup;
    private bool _isSuggesting;
    private int _suggestionIndex = -1;

    // The word being completed: where it starts and how long it is now.
    private int _wordStart;
    private int _wordLength;
    // Tab has replaced the word with a suggestion; further Tabs cycle.
    private bool _cycling;
    // Esc closed the ':' list for this word; reopens when the word changes.
    private string? _dismissed;
    // Text changes made here, not by the user.
    private bool _applying;

    public EmoteTextBox()
    {
        // Tunnel: Tab, Enter and the arrows must be seen before focus
        // navigation, sending, or the caret moving take them.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Emote image for a word, for drawing it inline.</summary>
    public Func<string, Uri?>? EmoteLookup
    {
        get => _emoteLookup;
        set
        {
            _emoteLookup = value;
            if (_presenter is not null) _presenter.EmoteLookup = value;
        }
    }

    /// <summary>Matching emotes for a typed word, best first; the bool is
    /// "contains" (after ':') rather than "starts with".</summary>
    public Func<string, bool, IReadOnlyList<Emote>>? Completer { get; set; }

    public ObservableCollection<Emote> Suggestions { get; } = [];

    public bool IsSuggesting
    {
        get => _isSuggesting;
        private set => SetAndRaise(IsSuggestingProperty, ref _isSuggesting, value);
    }

    /// <summary>The highlighted suggestion; set by the list as well.</summary>
    public int SuggestionIndex
    {
        get => _suggestionIndex;
        set => SetAndRaise(SuggestionIndexProperty, ref _suggestionIndex, value);
    }

    /// <summary>Redraws inline emotes, e.g. after emote sets have loaded.</summary>
    public void RefreshEmotes() => _presenter?.Refresh();

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<EmoteTextPresenter>("PART_TextPresenter");
        if (_presenter is not null) _presenter.EmoteLookup = _emoteLookup;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The caret moves after the text changes as the user types, so
        // either one changing is worth a look.
        if ((change.Property == TextProperty || change.Property == CaretIndexProperty) && !_applying) OnUserTyped();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        // Not while clicking a suggestion, which takes focus for a moment.
        if (!IsKeyboardFocusWithin) Close();
    }

    /// <summary>The user typed or moved the caret: a ':' word lists
    /// suggestions as it grows; anything else ends a Tab cycle.</summary>
    private void OnUserTyped()
    {
        _cycling = false;
        var text = Text ?? "";
        var (start, length) = EmoteCompletion.WordAt(text, CaretIndex);
        var word = text.Substring(start, length);
        if (word.Length > ColonMinimum && word[0] == ':' && word != _dismissed && Completer is { } complete)
        {
            _wordStart = start;
            _wordLength = length;
            Show(complete(word[1..], true));
        }
        else
        {
            Close();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Tab when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                Cycle(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                // Never moves focus out of the chat box.
                e.Handled = true;
                break;
            case Key.Down when IsSuggesting:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up when IsSuggesting:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter when IsSuggesting && !_cycling:
                Accept(SuggestionIndex);
                e.Handled = true;
                break;
            case Key.Escape when IsSuggesting:
                if (!_cycling) _dismissed = (Text ?? "").Substring(_wordStart, _wordLength);
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Tab: the first press puts the best match in place of the
    /// word (or the highlighted one, from a ':' list); later presses step
    /// through the rest.</summary>
    private void Cycle(int step)
    {
        if (_cycling && Suggestions.Count > 0)
        {
            Move(step);
            return;
        }
        if (IsSuggesting && SuggestionIndex >= 0)
        {
            _cycling = true;
            Replace(Suggestions[SuggestionIndex].Code, space: false);
            return;
        }
        var text = Text ?? "";
        var (start, length) = EmoteCompletion.WordAt(text, CaretIndex);
        var word = text.Substring(start, length);
        var colon = word.StartsWith(':');
        var typed = colon ? word[1..] : word;
        if (typed.Length == 0 || Completer is not { } complete) return;
        var matches = complete(typed, colon);
        if (matches.Count == 0) return;
        _wordStart = start;
        _wordLength = length;
        Show(matches);
        if (step < 0) SuggestionIndex = matches.Count - 1;
        _cycling = true;
        Replace(Suggestions[SuggestionIndex].Code, space: false);
    }

    private void Move(int step)
    {
        if (Suggestions.Count == 0) return;
        SuggestionIndex = (SuggestionIndex + step + Suggestions.Count) % Suggestions.Count;
        if (_cycling) Replace(Suggestions[SuggestionIndex].Code, space: false);
    }

    /// <summary>A suggestion picked (Enter, or a click in the list): it
    /// replaces the word, followed by a space, and the list closes.</summary>
    public void Accept(int index)
    {
        if (index < 0 || index >= Suggestions.Count) return;
        Replace(Suggestions[index].Code, space: true);
        Close();
        Focus();
    }

    private void Replace(string code, bool space)
    {
        var text = Text ?? "";
        var start = Math.Clamp(_wordStart, 0, text.Length);
        var length = Math.Clamp(_wordLength, 0, text.Length - start);
        var rest = text[(start + length)..];
        if (space && !rest.StartsWith(' ')) code += " ";
        _applying = true;
        try
        {
            Text = text[..start] + code + rest;
            CaretIndex = start + code.Length;
            ClearSelection();
        }
        finally
        {
            _applying = false;
        }
        _wordLength = space ? code.Length - 1 : code.Length;
    }

    private void Show(IReadOnlyList<Emote> matches)
    {
        Suggestions.Clear();
        foreach (var m in matches) Suggestions.Add(m);
        SuggestionIndex = Suggestions.Count > 0 ? 0 : -1;
        IsSuggesting = Suggestions.Count > 0;
    }

    private void Close()
    {
        _cycling = false;
        IsSuggesting = false;
        Suggestions.Clear();
        SuggestionIndex = -1;
    }
}
