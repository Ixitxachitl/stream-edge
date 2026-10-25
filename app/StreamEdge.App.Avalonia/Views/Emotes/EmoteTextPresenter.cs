// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>A text box's text with emote codes drawn as the emotes, the way
/// they'll look once sent. Each code is one unit to the caret and selection
/// (it moves over and deletes the code whole), and copying still gives the
/// code. Wraps like plain text when the box wraps. Says which words are
/// misspelled, for <see cref="SpellingUnderlines"/> to draw.</summary>
public sealed class EmoteTextPresenter : TextPresenter
{
    protected override Type StyleKeyOverride => typeof(TextPresenter);

    /// <summary>The emote image for a word, or null to leave it as text.</summary>
    public Func<string, Uri?>? EmoteLookup { get; set; }

    // The width the text is laid out in, for wrapping. The base class keeps
    // its own copy privately, so it's noted here on the way through.
    private double _maxWidth = double.PositiveInfinity;

    protected override Size MeasureOverride(Size availableSize)
    {
        _maxWidth = availableSize.Width;
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (TextWrapping != TextWrapping.NoWrap && finalSize.Width != _maxWidth)
        {
            _maxWidth = finalSize.Width;
            InvalidateTextLayout();
        }
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Re-lays out the text: emote sets loaded, or the channel changed.</summary>
    public void Refresh() => InvalidateTextLayout();

    /// <summary>The misspelled words in the text, given the caret; null for
    /// no spell check.</summary>
    public Func<string, int, IReadOnlyList<(int Start, int Length)>>? Misspellings { get; set; }

    // The last answer, kept while the text and caret stay (the caret's
    // blink redraws twice a second).
    private (string Text, int Caret, IReadOnlyList<(int Start, int Length)> Words)? _misspelled;

    /// <summary>Spelling changed (a word added, spell check turned on or
    /// off, the dictionary loaded): look again.</summary>
    public void RefreshSpelling()
    {
        _misspelled = null;
        SpellingChanged?.Invoke();
    }

    /// <summary>The underlines need drawing again.</summary>
    public event Action? SpellingChanged;

    /// <summary>The misspelled words as of now.</summary>
    public IReadOnlyList<(int Start, int Length)> Misspelled()
    {
        var text = Text ?? "";
        if (Misspellings is not { } find || text.Length == 0 || !string.IsNullOrEmpty(PreeditText)) return [];
        if (_misspelled is { } last && last.Text == text && last.Caret == CaretIndex) return last.Words;
        var words = find(text, CaretIndex);
        _misspelled = (text, CaretIndex, words);
        return words;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EmoteAnimator.Unregister(this);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>An animated emote was drawn: keep redrawing while it shows.
    /// A new layout drops this, and drawing it takes it up again only if an
    /// animated emote is still in the text.</summary>
    private void Animate()
    {
        if (TopLevel.GetTopLevel(this) is not null) EmoteAnimator.Register(this);
    }

    protected override TextLayout CreateTextLayout()
    {
        EmoteAnimator.Unregister(this);
        var text = Text;
        if (EmoteLookup is not { } lookup || string.IsNullOrEmpty(text) || PasswordChar != default
            || !string.IsNullOrEmpty(PreeditText))
            return base.CreateTextLayout();
        var spans = Find(text, lookup);
        if (spans.Count == 0) return base.CreateTextLayout();

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var run = new GenericTextRunProperties(typeface, FontSize, null, Foreground, null,
            BaselineAlignment.Baseline, null, FontFeatures);
        var paragraph = new GenericTextParagraphProperties(FlowDirection, TextAlignment, true, false, run,
            TextWrapping, LineHeight, 0, LetterSpacing);
        var width = TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : _maxWidth;
        return new TextLayout(new Source(text, spans, run, this), paragraph, TextTrimming.None,
            width, double.PositiveInfinity, 0);
    }

    /// <summary>Space-separated words that are emotes, in order.</summary>
    internal static List<(int Start, int Length, Uri Image)> Find(string text, Func<string, Uri?> lookup)
    {
        var spans = new List<(int, int, Uri)>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (lookup(text[start..i]) is { } image) spans.Add((start, i - start, image));
        }
        return spans;
    }

    /// <summary>An image arrived: lay out again, since its width may differ
    /// from the square placeholder.</summary>
    private void ImageLoaded() => Dispatcher.UIThread.Post(() =>
    {
        InvalidateTextLayout();
        InvalidateVisual();
    });

    private sealed class Source(string text, List<(int Start, int Length, Uri Image)> spans,
        TextRunProperties run, EmoteTextPresenter owner) : ITextSource
    {
        public TextRun? GetTextRun(int index)
        {
            if (index >= text.Length) return new TextEndOfParagraph();
            foreach (var span in spans)
            {
                if (span.Start == index) return new EmoteRun(text.AsMemory(span.Start, span.Length), span.Image, run, owner);
                if (span.Start > index) return new TextCharacters(text.AsMemory(index, span.Start - index), run);
            }
            return new TextCharacters(text.AsMemory(index), run);
        }
    }

    /// <summary>One emote in the line: as wide as its image at the line's
    /// height, standing on the text baseline.</summary>
    private sealed class EmoteRun : DrawableTextRun
    {
        private readonly Uri _url;
        private readonly EmoteTextPresenter _owner;
        private readonly double _height;
        private readonly double _descent;
        private EmoteImage? _image;

        public EmoteRun(ReadOnlyMemory<char> code, Uri url, TextRunProperties run, EmoteTextPresenter owner)
        {
            Text = code;
            Properties = run;
            _url = url;
            _owner = owner;
            _height = Math.Round(run.FontRenderingEmSize * 1.6);
            _descent = Math.Round(run.FontRenderingEmSize * 0.3);
            _image = EmoteImageCache.TryGet(url);
            if (_image is null) _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (await EmoteImageCache.GetAsync(_url) is not null) _owner.ImageLoaded();
        }

        public override ReadOnlyMemory<char> Text { get; }
        public override int Length => Text.Length;
        public override TextRunProperties? Properties { get; }

        public override Size Size
        {
            get
            {
                var px = _image?.PixelSize;
                var width = px is { Height: > 0 } p ? _height * p.Width / p.Height : _height;
                // A little air either side, as between words.
                return new Size(Math.Round(width) + 2, _height);
            }
        }

        public override double Baseline => _height - _descent;

        public override void Draw(DrawingContext context, Point origin)
        {
            var box = new Rect(origin.X + 1, origin.Y, Size.Width - 2, _height);
            _image ??= EmoteImageCache.TryGet(_url);
            if (_image is { } image)
            {
                context.DrawImage(image.FrameAt(EmoteAnimator.ElapsedMs), box);
                if (image.IsAnimated) _owner.Animate();
            }
            else
                context.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.3), null, box, 3, 3);
        }
    }
}
