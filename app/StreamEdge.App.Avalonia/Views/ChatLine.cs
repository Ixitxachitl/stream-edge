// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using StreamEdge.AvaloniaApp.ViewModels;
using StreamEdge.AvaloniaApp.Views.Emotes;
using StreamEdge.Chat;
using StreamEdge.Chat.Emotes;

namespace StreamEdge.AvaloniaApp.Views;

/// <summary>One chat line drawn the way twitch.tv draws it: badges, the
/// sender's colour, emotes inline (animated, with zero-width overlays),
/// reply context, and the highlight styles for mentions, highlighted and
/// first-time messages, subs and raids, and paid message effects.
/// Built in code because inlines can't be bound from XAML; rebuilt whenever
/// the entry changes (pronouns arriving, emotes loading, a deletion).</summary>
public sealed class ChatLine : Border
{
    public static readonly StyledProperty<ChatEntry?> EntryProperty =
        AvaloniaProperty.Register<ChatLine, ChatEntry?>(nameof(Entry));

    public ChatEntry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    private const double EmoteHeight = 28;
    private const double BadgeHeight = 18;

    private static readonly IBrush TimeBrush = Brush("#ADADB8");
    private static readonly IBrush DimBrush = Brush("#8E8E99");
    private static readonly IBrush TextBrush = Brush("#EFEFF1");
    private static readonly IBrush LinkBrush = Brush("#BF94FF");
    private static readonly IBrush SystemBrush = Brush("#ADADB8");

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    public ChatLine()
    {
        Padding = new Thickness(10, 4, 8, 4);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EntryProperty) return;
        if (change.OldValue is ChatEntry old) old.PropertyChanged -= OnEntryChanged;
        if (change.NewValue is ChatEntry now) now.PropertyChanged += OnEntryChanged;
        Rebuild();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Child = null;
        // Cleared, not set to null: a local null would beat the hover style.
        ClearValue(BackgroundProperty);
        ClearValue(BorderThicknessProperty);
        ClearValue(BorderBrushProperty);
        if (Entry is not { } entry) return;
        var m = entry.Message;

        var stack = new StackPanel { Spacing = 2 };
        StyleRow(entry, stack);

        if (m.IsReply && !entry.IsDeleted)
            stack.Children.Add(new TextBlock
            {
                Text = $"↪ Replying to @{m.ReplyParentName}: {m.ReplyParentBody}",
                Foreground = DimBrush,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines = BuildInlines(entry);
        stack.Children.Add(text);
        Child = stack;
    }

    /// <summary>Row background and left bar for the special message types,
    /// in Twitch's colours.</summary>
    private void StyleRow(ChatEntry entry, StackPanel stack)
    {
        var m = entry.Message;
        if (entry.IsSelfMention)
        {
            Bar("#E91916", "#3B1416");
        }
        else if (m.MsgId == "highlighted-message")
        {
            Bar("#9147FF", "#2D2245");
        }
        else if (m.Kind == ChatMessageKind.System && m.Login is not null)
        {
            // USERNOTICE: subs, resubs, gifts, raids, announcements.
            Bar("#9147FF", "#1F1F23");
        }
        else if (m.IsFirstMessage)
        {
            Bar("#00C8AF", "#12302C");
            stack.Children.Add(new TextBlock
            {
                Text = "✦ First-time chat",
                Foreground = Brush("#00C8AF"),
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
            });
        }

        // Paid "message effects": a tinted gradient behind the line.
        if (m.AnimationId is { } effect && !entry.IsDeleted)
        {
            string[] stops = effect switch
            {
                "simmer" => ["#55FF7B00", "#55FFD000"],
                "rainbow-eclipse" => ["#55FF0055", "#55FFC800", "#5500D084", "#550088FF", "#55A100FF"],
                "cosmic-abyss" => ["#55301A8A", "#552B6BD6", "#55130A3D"],
                _ => ["#55772CE8", "#5500C8AF"],
            };
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            };
            for (var i = 0; i < stops.Length; i++)
                gradient.GradientStops.Add(new GradientStop(Color.Parse(stops[i]), i / (double)(stops.Length - 1)));
            Background = gradient;
        }
    }

    private void Bar(string bar, string background)
    {
        BorderBrush = Brush(bar);
        BorderThickness = new Thickness(4, 0, 0, 0);
        Background = Brush(background);
    }

    private static InlineCollection BuildInlines(ChatEntry entry)
    {
        var m = entry.Message;
        var inlines = new InlineCollection();
        if (entry.Time.Length > 0)
            inlines.Add(new Run(entry.Time + " ") { Foreground = TimeBrush, FontSize = 12 });

        switch (m.Kind)
        {
            case ChatMessageKind.Membership:
                inlines.Add(new Run(m.Text) { Foreground = DimBrush });
                return inlines;
            case ChatMessageKind.System when m.Login is null:
                inlines.Add(new Run(m.Text) { Foreground = SystemBrush, FontStyle = FontStyle.Italic });
                return inlines;
        }

        foreach (var badge in entry.Badges) inlines.Add(Badge(badge));

        var nameBrush = new SolidColorBrush(Readable(Color.Parse(UserColors.For(m.Login, m.Color))));
        var isAction = m.Kind == ChatMessageKind.Action;
        var isNotice = m.Kind == ChatMessageKind.System;
        inlines.Add(new Run(m.Sender) { Foreground = nameBrush, FontWeight = FontWeight.Bold });
        if (entry.Pronoun is { } pronoun)
            inlines.Add(new Run(" (" + pronoun + ")") { Foreground = DimBrush, FontSize = 11.5 });
        if (m.Bits > 0)
            inlines.Add(new Run($" ♦{m.Bits:N0}") { Foreground = Brush("#BF94FF"), FontWeight = FontWeight.SemiBold });
        inlines.Add(new Run(isAction ? " " : isNotice ? " · " : ": ") { Foreground = TextBrush });

        if (entry.IsDeleted)
        {
            inlines.Add(new Run("<message deleted>") { Foreground = DimBrush, FontStyle = FontStyle.Italic });
            return inlines;
        }

        var textBrush = isAction ? nameBrush : isNotice ? SystemBrush : TextBrush;
        // Twitch's "gigantify an emote" power-up enlarges the last emote.
        var gigantic = m.MsgId == "gigantified-emote-message"
            ? entry.Tokens.LastOrDefault(t => t.Kind == ChatTokenKind.Emote)
            : null;
        foreach (var token in entry.Tokens)
        {
            switch (token.Kind)
            {
                case ChatTokenKind.Emote when token.Emote is { } emote:
                    inlines.Add(Emote(emote, token.Overlays, token == gigantic ? EmoteHeight * 4 : EmoteHeight));
                    break;
                case ChatTokenKind.Link:
                    inlines.Add(new Run(token.Text) { Foreground = LinkBrush, TextDecorations = Avalonia.Media.TextDecorations.Underline });
                    break;
                case ChatTokenKind.Mention:
                    inlines.Add(new Run(token.Text)
                    {
                        Foreground = token.IsSelfMention ? Brushes.White : TextBrush,
                        FontWeight = FontWeight.Bold,
                    });
                    break;
                default:
                    inlines.Add(new Run(token.Text) { Foreground = textBrush });
                    break;
            }
        }
        return inlines;
    }

    private static Inline Emote(Emote emote, IReadOnlyList<Emote> overlays, double height)
    {
        var tip = emote.Code + "\n" + emote.Provider switch
        {
            EmoteProvider.Bttv => "BTTV emote",
            EmoteProvider.Ffz => "FFZ emote",
            EmoteProvider.SevenTv => "7TV emote",
            _ => "Twitch emote",
        };
        if (overlays.Count > 0) tip += "\n+ " + string.Join(", ", overlays.Select(o => o.Code));
        var view = new EmoteView(emote.ImageUrl, [.. overlays.Select(o => o.ImageUrl)], tip)
        {
            EmoteHeight = height,
            // No negative margins to tuck emotes into the text line: they
            // clipped the descenders of names and text on the same line.
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static Inline Badge(ChatServices.Badge badge)
    {
        Control child;
        if (badge.Image is { } image)
        {
            child = new EmoteView(image.ImageUrl, null, image.Title) { EmoteHeight = BadgeHeight };
            if (image.BackgroundColor is { } bg)
                child = new Border { Background = Brush(bg), CornerRadius = new CornerRadius(2), Child = child };
        }
        else
        {
            // No badge art (not signed in): a coloured chip with the initial.
            var (color, glyph) = badge.Set switch
            {
                "broadcaster" => ("#E91916", "B"),
                "moderator" => ("#00AD03", "M"),
                "vip" => ("#E005B9", "V"),
                "subscriber" or "founder" => ("#8205B4", "S"),
                "partner" or "verified" => ("#9147FF", "✓"),
                _ => ("#53535F", badge.Set.Length > 0 ? badge.Set[..1].ToUpperInvariant() : "?"),
            };
            child = new Border
            {
                Background = Brush(color),
                CornerRadius = new CornerRadius(3),
                Width = BadgeHeight,
                Height = BadgeHeight,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(child, badge.Set);
        }
        child.Margin = new Thickness(0, 0, 3, 0);
        return new InlineUIContainer(child) { BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>Lifts names chosen for a light background (pure blue, dark
    /// green) until they read on the dark chat, as twitch.tv's dark theme does.</summary>
    internal static Color Readable(Color c)
    {
        static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        for (var i = 0; i < 6 && Lum(c) < 0.45; i++)
            c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));
        return c;
    }
}
