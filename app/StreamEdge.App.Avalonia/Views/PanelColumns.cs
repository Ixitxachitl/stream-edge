// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using StreamEdge.Streams;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>About panels as twitch.tv lays them out: columns of a fixed
/// width, as many as fit, each panel going into the shortest column so far,
/// so they read top to bottom and scroll vertically. Narrower than one
/// column (docked beside the video), the one column takes the width.</summary>
public sealed class PanelColumns : Panel
{
    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<PanelColumns, double>(nameof(ColumnWidth), 320);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<PanelColumns, double>(nameof(Gap), 16);

    static PanelColumns() => AffectsMeasure<PanelColumns>(ColumnWidthProperty, GapProperty);

    public double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>Columns and their width for an available width.</summary>
    public static (int Count, double Width) Columns(double available, double columnWidth, double gap)
    {
        if (!double.IsFinite(available) || available <= 0) return (1, columnWidth);
        var count = Math.Max(1, (int)Math.Floor((available + gap) / (columnWidth + gap)));
        return (count, Math.Min(columnWidth, available));
    }

    /// <summary>Where each child goes, given the children's heights.</summary>
    public static IReadOnlyList<Rect> Place(IReadOnlyList<double> heights, double available, double columnWidth, double gap)
    {
        var (count, width) = Columns(available, columnWidth, gap);
        var bottoms = new double[count];
        var rects = new Rect[heights.Count];
        for (var i = 0; i < heights.Count; i++)
        {
            var column = Array.IndexOf(bottoms, bottoms.Min());
            rects[i] = new Rect(column * (width + gap), bottoms[column], width, heights[i]);
            bottoms[column] += heights[i] + gap;
        }
        return rects;
    }

    private IReadOnlyList<Rect> _placed = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var (_, width) = Columns(availableSize.Width, ColumnWidth, Gap);
        foreach (var child in Children) child.Measure(new Size(width, double.PositiveInfinity));
        _placed = Place([.. Children.Select(c => c.DesiredSize.Height)], availableSize.Width, ColumnWidth, Gap);
        var right = _placed.Count == 0 ? 0 : _placed.Max(r => r.Right);
        var bottom = _placed.Count == 0 ? 0 : _placed.Max(r => r.Bottom);
        return new Size(right, bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _placed = Place([.. Children.Select(c => c.DesiredSize.Height)], finalSize.Width, ColumnWidth, Gap);
        for (var i = 0; i < Children.Count; i++) Children[i].Arrange(_placed[i]);
        return finalSize;
    }
}

/// <summary>A panel's description as twitch.tv draws it: bold where the
/// Markdown said, line breaks kept, links clickable (opening in the browser).</summary>
public sealed class PanelText : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<PanelRun>?> RunsProperty =
        AvaloniaProperty.Register<PanelText, IReadOnlyList<PanelRun>?>(nameof(Runs));

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public IReadOnlyList<PanelRun>? Runs { get => GetValue(RunsProperty); set => SetValue(RunsProperty, value); }

    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#BF94FF"));

    public PanelText() => TextWrapping = TextWrapping.Wrap;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RunsProperty) return;
        var inlines = new InlineCollection();
        foreach (var run in Runs ?? [])
        {
            if (run.Link is { } link)
            {
                // Inlines can't take clicks: a small text block in the flow can.
                var block = new InlineTextBlock
                {
                    Text = run.Text,
                    Foreground = LinkBrush,
                    FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
                    TextDecorations = Avalonia.Media.TextDecorations.Underline,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(block, link.AbsoluteUri);
                block.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) return;
                    UrlLauncher.TryOpen(link.AbsoluteUri);
                    e.Handled = true;
                };
                inlines.Add(new InlineUIContainer(block) { BaselineAlignment = BaselineAlignment.Baseline });
            }
            else
            {
                inlines.Add(new Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal });
            }
        }
        Inlines = inlines;
    }
}
