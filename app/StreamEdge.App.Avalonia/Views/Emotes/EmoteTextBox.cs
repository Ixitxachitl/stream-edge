// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>The chat box: emotes drawn inline as they're typed, and emote
/// completion. Tab completes the word at the caret and, pressed again,
/// cycles through the other matches (Shift+Tab back); typing ':' and a few
/// letters lists matches as you go, as on twitch.tv, for Tab, Enter or a
/// click to pick. Up and Down move through the list, Esc closes it.
/// A '/' starting the message lists the commands the same way.
/// Pasted line breaks become spaces, since a chat message is one line.
/// With a spell checker, misspelled words are underlined, and right-clicking
/// one offers spellings and adding it to the dictionary.</summary>
public sealed partial class EmoteTextBox : TextBox
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
        PastingFromClipboard += OnPasting;
        // Before the text box's own Cut/Copy/Paste menu opens.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    /// <summary>A single-line TextBox keeps only the first line of a
    /// paste; this pastes it all, with each line break as a space.</summary>
    private async void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        e.Handled = true;
        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        if (string.IsNullOrEmpty(text)) return;
        // As if typed: replaces the selection, honours MaxLength, and undoes as one step.
        OnTextInput(new TextInputEventArgs { RoutedEvent = TextInputEvent, Source = this, Text = OneLine(text) });
    }

    /// <summary>Line breaks, and the spaces around them, as one space.</summary>
    public static string OneLine(string text) => LineBreaks().Replace(text, " ");

    [GeneratedRegex(@"[ \t]*[\r\n]+\s*")]
    private static partial Regex LineBreaks();

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

    /// <summary>Commands whose name starts with what's typed after '/'.</summary>
    public Func<string, IReadOnlyList<ChatCommandInfo>>? CommandCompleter { get; set; }

    /// <summary>Emotes, or commands while the message starts with '/'.</summary>
    public ObservableCollection<object> Suggestions { get; } = [];

    /// <summary>What picking a suggestion puts in the box.</summary>
    private static string CodeOf(object suggestion) => suggestion switch
    {
        Emote emote => emote.Code,
        ChatCommandInfo command => command.Slash,
        _ => "",
    };

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
    public void RefreshEmotes()
    {
        _presenter?.Refresh();
        _presenter?.RefreshSpelling();
    }

    /// <summary>The spell checker, or null for none (off, or not loaded yet).</summary>
    public Func<ChatSpelling?>? Spelling { get; set; }

    /// <summary>Someone's name, never underlined (emote codes never are either).</summary>
    public Func<string, bool>? IsName { get; set; }

    /// <summary>"Add to dictionary" was picked for this word.</summary>
    public Action<string>? AddToDictionary { get; set; }

    /// <summary>Underlines again: the checker or its words changed.</summary>
    public void RefreshSpelling() => _presenter?.RefreshSpelling();

    private IReadOnlyList<(int Start, int Length)> Misspellings(string text, int caret) =>
        Spelling?.Invoke() is { } spelling
            ? spelling.Misspelled(text, caret, word => _emoteLookup?.Invoke(word) is not null || IsName?.Invoke(word) == true)
            : [];

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<EmoteTextPresenter>("PART_TextPresenter");
        if (_presenter is null) return;
        _presenter.EmoteLookup = _emoteLookup;
        _presenter.Misspellings = Misspellings;
        if (e.NameScope.Find<SpellingUnderlines>("PART_Spelling") is { } underlines) underlines.Presenter = _presenter;
    }

    // ----- Spelling menu ----------------------------------------------------

    /// <summary>Right-click on an underlined word: its spellings, then "Add
    /// to dictionary", then the usual Cut, Copy and Paste. Anywhere else,
    /// the text box's own menu.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_presenter is null || !e.TryGetPosition(_presenter, out var point)) return;
        if (SpellingMenuAt(_presenter.TextLayout.HitTestPoint(point).TextPosition) is not { } menu) return;
        menu.ShowAt(this, showAtPointer: true);
        e.Handled = true;
    }

    /// <summary>The spelling menu for the misspelled word at this place in
    /// the text, or null when there's none there.</summary>
    public MenuFlyout? SpellingMenuAt(int at)
    {
        if (_presenter is null || Spelling?.Invoke() is not { } spelling) return null;
        var text = Text ?? "";
        foreach (var (start, length) in _presenter.Misspelled())
            if (at >= start && at <= start + length)
                return SpellingMenu(spelling, start, length, text.Substring(start, length));
        return null;
    }

    private MenuFlyout SpellingMenu(ChatSpelling spelling, int start, int length, string word)
    {
        var menu = new MenuFlyout();
        var suggestions = spelling.Suggest(word);
        foreach (var suggestion in suggestions)
        {
            var item = new MenuItem { Header = suggestion, FontWeight = Avalonia.Media.FontWeight.SemiBold };
            item.Click += (_, _) => ReplaceRange(start, length, suggestion);
            menu.Items.Add(item);
        }
        if (suggestions.Count == 0) menu.Items.Add(new MenuItem { Header = "No spellings found", IsEnabled = false });
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add to dictionary" };
        add.Click += (_, _) => AddToDictionary?.Invoke(word);
        menu.Items.Add(add);
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Cut", CanCut, Cut));
        menu.Items.Add(Command("Copy", CanCopy, Copy));
        menu.Items.Add(Command("Paste", CanPaste, Paste));
        return menu;
    }

    private static MenuItem Command(string name, bool enabled, Action run)
    {
        var item = new MenuItem { Header = name, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>A spelling picked: it takes the word's place, caret after it.</summary>
    private void ReplaceRange(int start, int length, string with)
    {
        var text = Text ?? "";
        if (start + length > text.Length) return;
        Text = text[..start] + with + text[(start + length)..];
        CaretIndex = start + with.Length;
        ClearSelection();
        Focus();
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
        if (IsCommandWord(start, word) && word != _dismissed && CommandCompleter is { } commands)
        {
            _wordStart = start;
            _wordLength = length;
            Show(commands(word[1..]));
        }
        else if (word.Length > ColonMinimum && word[0] == ':' && word != _dismissed && Completer is { } complete)
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
            // A command typed in full sends, rather than being picked again.
            case Key.Enter when IsSuggesting && !_cycling && !TypedInFull():
                Accept(SuggestionIndex);
                e.Handled = true;
                break;
            case Key.Escape when IsSuggesting:
                Dismiss();
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
            Replace(CodeOf(Suggestions[SuggestionIndex]), space: false);
            return;
        }
        var text = Text ?? "";
        var (start, length) = EmoteCompletion.WordAt(text, CaretIndex);
        var word = text.Substring(start, length);
        IReadOnlyList<object> matches;
        if (IsCommandWord(start, word))
        {
            if (CommandCompleter is not { } commands) return;
            matches = commands(word[1..]);
        }
        else
        {
            var colon = word.StartsWith(':');
            var typed = colon ? word[1..] : word;
            if (typed.Length == 0 || Completer is not { } complete) return;
            matches = complete(typed, colon);
        }
        if (matches.Count == 0) return;
        _wordStart = start;
        _wordLength = length;
        Show(matches);
        if (step < 0) SuggestionIndex = matches.Count - 1;
        _cycling = true;
        Replace(CodeOf(Suggestions[SuggestionIndex]), space: false);
    }

    private void Move(int step)
    {
        if (Suggestions.Count == 0) return;
        SuggestionIndex = (SuggestionIndex + step + Suggestions.Count) % Suggestions.Count;
        if (_cycling) Replace(CodeOf(Suggestions[SuggestionIndex]), space: false);
    }

    /// <summary>A suggestion picked (Enter, or a click in the list): it
    /// replaces the word, followed by a space, and the list closes.</summary>
    public void Accept(int index)
    {
        if (index < 0 || index >= Suggestions.Count) return;
        Replace(CodeOf(Suggestions[index]), space: true);
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

    /// <summary>Closes the list for the word at hand, as Esc does; it opens
    /// again once the word changes.</summary>
    public void Dismiss()
    {
        if (!IsSuggesting) return;
        if (!_cycling) _dismissed = (Text ?? "").Substring(_wordStart, _wordLength);
        Close();
    }

    /// <summary>The first word of the message, starting with '/'.</summary>
    private static bool IsCommandWord(int start, string word) => start == 0 && word.StartsWith('/');

    private bool TypedInFull() =>
        SuggestionIndex >= 0 && SuggestionIndex < Suggestions.Count
        && string.Equals((Text ?? "").Substring(_wordStart, _wordLength), CodeOf(Suggestions[SuggestionIndex]),
            StringComparison.OrdinalIgnoreCase);

    private void Show(IEnumerable<object> matches)
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
