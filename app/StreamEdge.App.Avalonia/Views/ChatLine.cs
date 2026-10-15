// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.Chat;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>One chat line as a single wrapping text run sequence:
/// time, badges, coloured name, message, links underlined.
/// Built in code because inlines can't be bound from XAML.</summary>
public sealed partial class ChatLine : SelectableTextBlock
{
    public static readonly StyledProperty<ChatEntry?> EntryProperty =
        AvaloniaProperty.Register<ChatLine, ChatEntry?>(nameof(Entry));

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public ChatEntry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    public ChatLine()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EntryProperty) Rebuild();
    }

    private static readonly IBrush TimeBrush = new SolidColorBrush(Color.Parse("#7A7A85"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#8E8E99"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#EFEFF1"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#BF94FF"));
    private static readonly IBrush SystemBrush = new SolidColorBrush(Color.Parse("#7FE0C4"));

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        if (Entry is not { } entry)
        {
            Inlines = inlines;
            return;
        }
        var m = entry.Message;

        if (entry.Time.Length > 0)
            inlines.Add(new Run(entry.Time + " ") { Foreground = TimeBrush });

        switch (m.Kind)
        {
            case ChatMessageKind.Membership:
                inlines.Add(new Run(m.Text) { Foreground = DimBrush });
                break;
            case ChatMessageKind.System when m.Login is null:
                inlines.Add(new Run(m.Text) { Foreground = SystemBrush });
                break;
            default:
            {
                foreach (var badge in m.Badges)
                    inlines.Add(Badge(badge));
                var nameBrush = new SolidColorBrush(Readable(Color.Parse(UserColors.For(m.Login, m.Color))));
                var isAction = m.Kind == ChatMessageKind.Action;
                inlines.Add(new Run(isAction ? "* " + m.Sender + " " : m.Sender)
                {
                    Foreground = nameBrush,
                    FontWeight = FontWeight.SemiBold,
                });
                if (!isAction) inlines.Add(new Run(": ") { Foreground = TextBrush });
                AddText(inlines, m.Text, isAction ? nameBrush : m.Kind == ChatMessageKind.System ? SystemBrush : TextBrush);
                break;
            }
        }
        Inlines = inlines;
    }

    private static void AddText(InlineCollection inlines, string text, IBrush brush)
    {
        var at = 0;
        foreach (Match url in UrlPattern().Matches(text))
        {
            if (url.Index > at) inlines.Add(new Run(text[at..url.Index]) { Foreground = brush });
            inlines.Add(new Run(url.Value) { Foreground = LinkBrush, TextDecorations = Avalonia.Media.TextDecorations.Underline });
            at = url.Index + url.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]) { Foreground = brush });
    }

    /// <summary>Placeholder badge: a coloured chip with the set's initial.
    /// Real badge images come from Helix chat/badges (see docs/PLAN.md).</summary>
    private static Inline Badge(string badge)
    {
        var set = badge.Split('/')[0];
        var (color, glyph) = set switch
        {
            "broadcaster" => ("#E91916", "B"),
            "moderator" => ("#00AD03", "M"),
            "vip" => ("#E005B9", "V"),
            "subscriber" => ("#8205B4", "S"),
            "partner" or "verified" => ("#9147FF", "✓"),
            _ => ("#53535F", set.Length > 0 ? set[..1].ToUpperInvariant() : "?"),
        };
        return new InlineUIContainer(new Border
        {
            Background = new SolidColorBrush(Color.Parse(color)),
            CornerRadius = new CornerRadius(3),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 3, -3),
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
    }

    /// <summary>Lifts names users chose for a light background (pure blue,
    /// dark green) until they read on the dark chat pane.</summary>
    internal static Color Readable(Color c)
    {
        static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        for (var i = 0; i < 6 && Lum(c) < 0.45; i++)
            c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));
        return c;
    }
}
