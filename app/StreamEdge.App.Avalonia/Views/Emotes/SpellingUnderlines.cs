// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;

namespace StreamEdge.AvaloniaApp.Views.Emotes;

/// <summary>Red wavy lines under the misspelled words of an
/// <see cref="EmoteTextPresenter"/>, as a browser draws them. A layer over
/// the presenter, since a text presenter's own drawing can't be added to;
/// redrawn whenever the text is laid out again or the caret moves.</summary>
public sealed class SpellingUnderlines : Control
{
    private static readonly Pen Squiggle = new(new SolidColorBrush(Color.Parse("#F5464C")), 1.2);

    private EmoteTextPresenter? _presenter;

    public EmoteTextPresenter? Presenter
    {
        get => _presenter;
        set
        {
            if (_presenter is not null)
            {
                _presenter.LayoutUpdated -= OnPresenterChanged;
                _presenter.PropertyChanged -= OnPresenterPropertyChanged;
                _presenter.SpellingChanged -= InvalidateVisual;
            }
            _presenter = value;
            if (_presenter is not null)
            {
                _presenter.LayoutUpdated += OnPresenterChanged;
                _presenter.PropertyChanged += OnPresenterPropertyChanged;
                _presenter.SpellingChanged += InvalidateVisual;
            }
            InvalidateVisual();
        }
    }

    private void OnPresenterChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // The word being typed is judged once the caret leaves it.
        if (e.Property == TextPresenter.CaretIndexProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_presenter is not { } presenter || presenter.TranslatePoint(default, this) is not { } origin) return;
        foreach (var (start, length) in presenter.Misspelled())
            foreach (var box in presenter.TextLayout.HitTestTextRange(start, length))
                context.DrawGeometry(null, Squiggle, Wave(origin.X + box.Left, origin.X + box.Right, origin.Y + box.Bottom - 1.5));
    }

    /// <summary>A zigzag two pixels to a step, along the bottom of a word.</summary>
    private static StreamGeometry Wave(double from, double to, double y)
    {
        const double step = 2, height = 1.5;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(from, y), false);
            var up = true;
            for (var x = from + step; x <= to; x += step, up = !up)
                g.LineTo(new Point(x, up ? y - height : y));
            g.EndFigure(false);
        }
        return geometry;
    }
}
