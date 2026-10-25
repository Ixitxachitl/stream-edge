// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    /// <summary>GIFs are pictures, not emotes: bigger, but kept short enough
    /// that one doesn't fill a 480-pixel-tall chat.</summary>
    private const double GifHeight = 100;
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
        if (m.CustomRewardId is not null && !entry.IsDeleted) stack.Children.Add(Redemption(entry));

        if (m.IsReply && !entry.IsDeleted)
            stack.Children.Add(new TextBlock
            {
                Text = $"↪ Replying to @{m.ReplyParentName}: {m.ReplyParentBody}",
                Foreground = DimBrush,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

        // No Copy-only menu of its own: right-click opens the chat's menu
        // (Reply, Copy, Users in chat). A local null beats the theme's.
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, ContextFlyout = null };
        text.Inlines = BuildInlines(entry);
        stack.Children.Add(text);
        _replyButton = CanReply(entry) ? ReplyButton(entry) : null;
        if (_replyButton is null)
        {
            Child = stack;
            return;
        }
        // The reply arrow sits in the top-right corner, shown while the
        // mouse is over the line, as on twitch.tv.
        var panel = new Panel();
        panel.Children.Add(stack);
        panel.Children.Add(_replyButton);
        Child = panel;
    }

    // ----- Replying ---------------------------------------------------------

    /// <summary>The ↩ on the line under the mouse.</summary>
    private Control? _replyButton;

    /// <summary>Someone else's message (or a whisper to the user) that a
    /// reply can answer.</summary>
    private static bool CanReply(ChatEntry entry)
    {
        var m = entry.Message;
        if (entry.IsDeleted || entry.IsFromSelf || m.Login is null) return false;
        return m.Kind == ChatMessageKind.Whisper ? m.WhisperTo is null
            : m.Kind is ChatMessageKind.Message or ChatMessageKind.Action && m.Id is not null;
    }

    private Control ReplyButton(ChatEntry entry)
    {
        var button = new Border
        {
            Child = new TextBlock { Text = "↩", Foreground = TextBrush, FontSize = 14 },
            Background = Brush("#CC18181B"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = IsPointerOver,
        };
        ToolTip.SetTip(button, entry.Message.Kind == ChatMessageKind.Whisper ? "Whisper back" : "Reply");
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            RaiseEvent(new ReplyRequestedEventArgs(ReplyRequestedEvent, entry));
            e.Handled = true;
        };
        return button;
    }

    /// <summary>What answering this line is called ("Reply", "Whisper
    /// back"), or null if it can't be answered.</summary>
    public string? ReplyLabel => Entry is { } entry && CanReply(entry)
        ? entry.Message.Kind == ChatMessageKind.Whisper ? "Whisper back" : "Reply"
        : null;

    /// <summary>Answer this line, as its ↩ does (the right-click menu's Reply).</summary>
    public void RequestReply()
    {
        if (Entry is { } entry && CanReply(entry)) RaiseEvent(new ReplyRequestedEventArgs(ReplyRequestedEvent, entry));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (_replyButton is not null) _replyButton.IsVisible = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_replyButton is not null) _replyButton.IsVisible = false;
    }

    /// <summary>The ↩ on a line was clicked: answer it.</summary>
    public static readonly RoutedEvent<ReplyRequestedEventArgs> ReplyRequestedEvent =
        RoutedEvent.Register<ChatLine, ReplyRequestedEventArgs>(nameof(ReplyRequested), RoutingStrategies.Bubble);

    public event EventHandler<ReplyRequestedEventArgs>? ReplyRequested
    {
        add => AddHandler(ReplyRequestedEvent, value);
        remove => RemoveHandler(ReplyRequestedEvent, value);
    }

    /// <summary>Row background and left bar for the special message types,
    /// in Twitch's colours.</summary>
    private void StyleRow(ChatEntry entry, StackPanel stack)
    {
        var m = entry.Message;
        if (m.Kind == ChatMessageKind.Whisper)
        {
            Bar("#BF94FF", "#241A33");
        }
        else if (entry.IsSelfMention)
        {
            Bar("#E91916", "#3B1416");
        }
        else if (m.MsgId == "highlighted-message")
        {
            Bar("#9147FF", "#2D2245");
        }
        else if (m.CustomRewardId is not null)
        {
            // A reward that asks for text, as twitch.tv shows it: a grey
            // card with the reward's own colour down the side.
            Bar(entry.Reward?.Color is { Length: > 0 } c && Color.TryParse(c, out _) ? c : "#9147FF", "#26262C");
        }
        else if (m.Kind == ChatMessageKind.System && m.Login is not null)
        {
            // USERNOTICE: subs, resubs, gifts, raids, announcements, shared
            // watch streaks (Twitch's text says how many streams).
            Bar("#9147FF", "#1F1F23");
            if (m.MsgId == "viewermilestone")
                stack.Children.Add(new TextBlock
                {
                    Text = "Watch Streak",
                    Foreground = Brush("#BF94FF"),
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                });
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

    /// <summary>"Redeemed Hydrate · ◉ 500" above a reward's message.</summary>
    private static Control Redemption(ChatEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = entry.Reward is { } r ? $"Redeemed {r.Title}" : "Redeemed a reward",
            Foreground = DimBrush,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (entry.Reward is { Cost: > 0 } reward)
        {
            row.Children.Add(PointsGlyph(entry.PointsIcon, 14));
            row.Children.Add(new TextBlock
            {
                Text = reward.Cost.ToString("N0"),
                Foreground = DimBrush,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return row;
    }

    /// <summary>The channel's own points icon, or Twitch's default.</summary>
    internal static Control PointsGlyph(Uri? icon, double size) => icon is not null
        ? new RemoteImage { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center }
        : new TextBlock { Text = "◉", Foreground = Brush("#BF94FF"), FontSize = size - 2, VerticalAlignment = VerticalAlignment.Center };

    private void Bar(string bar, string background)
    {
        BorderBrush = Brush(bar);
        BorderThickness = new Thickness(4, 0, 0, 0);
        Background = Brush(background);
    }

    private InlineCollection BuildInlines(ChatEntry entry)
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

        // Mod tools first, as Chatterino puts them: one click, no menu.
        if (entry.CanModerate && !entry.IsDeleted && m.Login is not null
            && m.Kind is ChatMessageKind.Message or ChatMessageKind.Action)
        {
            // Only what Twitch would do: nothing on the broadcaster's lines,
            // no deleting another mod's, timeouts and bans of mods for the
            // broadcaster only; on the user's own lines, just delete.
            if (entry.CanDelete) inlines.Add(ModButton(TrashIcon, "Delete message", ModAction.Delete, entry));
            // Our own line, its id still to come: its place, kept empty.
            else if (entry.WillBeDeletable) inlines.Add(ModButton(TrashIcon, null, ModAction.Delete, entry));
            if (entry.CanRemove)
            {
                inlines.Add(ModButton(TimeoutIcon, "Timeout 10 minutes", ModAction.Timeout, entry));
                inlines.Add(ModButton(BanIcon, "Ban", ModAction.Ban, entry));
            }
        }

        // Shared chat: the icon of the channel the line was sent in, first,
        // as twitch.tv draws it.
        if (m.SourceRoomId is not null && m.Login is not null) inlines.Add(SourceIcon(entry.Source));
        foreach (var badge in entry.Badges) inlines.Add(Badge(badge));

        var nameBrush = new SolidColorBrush(Readable(Color.Parse(UserColors.For(m.Login, m.Color))));
        var isAction = m.Kind == ChatMessageKind.Action;
        var isNotice = m.Kind == ChatMessageKind.System;
        inlines.Add(m.Login is { } login
            ? Clickable(m.Sender, nameBrush, FontWeight.Bold, () => RaiseUserClicked(login, entry))
            : new Run(m.Sender) { Foreground = nameBrush, FontWeight = FontWeight.Bold });
        if (entry.Pronoun is { } pronoun)
            inlines.Add(new Run(" (" + pronoun + ")") { Foreground = DimBrush, FontSize = 11.5 });
        if (m.Bits > 0)
            inlines.Add(new Run($" ♦{m.Bits:N0}") { Foreground = Brush("#BF94FF"), FontWeight = FontWeight.SemiBold });
        inlines.Add(new Run(m.Kind == ChatMessageKind.Whisper
                ? m.WhisperTo is { } to ? $" ▸ {to}: " : " whispers: "
                : isAction ? " " : isNotice ? " · " : ": ")
            { Foreground = m.Kind == ChatMessageKind.Whisper ? LinkBrush : TextBrush });

        if (entry.IsDeleted && !entry.ShowsDeletedText)
        {
            inlines.Add(new Run("<message deleted>") { Foreground = DimBrush, FontStyle = FontStyle.Italic });
            return inlines;
        }
        var bodyStart = inlines.Count;

        var textBrush = isAction ? nameBrush : isNotice ? SystemBrush : TextBrush;
        // Twitch's "gigantify an emote" power-up enlarges the last Twitch
        // emote (it can't take BTTV, FFZ or 7TV ones, even after it).
        var gigantic = m.MsgId == "gigantified-emote-message"
            ? entry.Tokens.LastOrDefault(t => t.Kind == ChatTokenKind.Emote && t.Emote?.Provider == EmoteProvider.Twitch)
            : null;
        foreach (var token in entry.Tokens)
        {
            switch (token.Kind)
            {
                case ChatTokenKind.Emote when token.Emote is { } emote:
                    inlines.Add(Copies(token == gigantic ? GiganticEmote(emote, token.Overlays) : Emote(emote, token.Overlays, EmoteHeight), token.Text));
                    break;
                case ChatTokenKind.Cheer when token.Cheer is { } cheer:
                    inlines.Add(Copies(Cheer(cheer), token.Text));
                    break;
                case ChatTokenKind.Gif when token.Url is { } gif:
                    inlines.Add(Copies(Gif(gif, token.Text), token.Text));
                    break;
                case ChatTokenKind.Link when token.Url is { } url:
                    inlines.Add(Clickable(token.Text, LinkBrush, FontWeight.Normal, () => UrlLauncher.TryOpen(url.AbsoluteUri), underline: true));
                    break;
                case ChatTokenKind.Mention when token.MentionLogin is { } mentioned:
                    inlines.Add(Clickable(token.Text, token.IsSelfMention ? Brushes.White : TextBrush, FontWeight.Bold,
                        () => RaiseUserClicked(mentioned, null)));
                    break;
                default:
                    inlines.Add(new Run(token.Text) { Foreground = textBrush });
                    break;
            }
        }
        if (entry.IsDeleted) CrossOut(inlines, bodyStart);
        return inlines;
    }

    /// <summary>A deleted message kept on show: its text struck through and
    /// dimmed, its emotes and links faded.</summary>
    private static void CrossOut(InlineCollection inlines, int from)
    {
        for (var i = from; i < inlines.Count; i++)
        {
            switch (inlines[i])
            {
                case Run run:
                    run.TextDecorations = TextDecorations.Strikethrough;
                    run.Foreground = DimBrush;
                    break;
                case InlineUIContainer { Child: { } child }:
                    child.Opacity = 0.4;
                    break;
            }
        }
    }

    // ----- Interaction ----------------------------------------------------

    /// <summary>A name or @mention was clicked: show that user's card. The
    /// window handles it, anchoring the popup to <see cref="UserClickedEventArgs.Anchor"/>.</summary>
    public static readonly RoutedEvent<UserClickedEventArgs> UserClickedEvent =
        RoutedEvent.Register<ChatLine, UserClickedEventArgs>(nameof(UserClicked), RoutingStrategies.Bubble);

    /// <summary>A moderator icon on the line was clicked.</summary>
    public static readonly RoutedEvent<ModActionEventArgs> ModActionEvent =
        RoutedEvent.Register<ChatLine, ModActionEventArgs>(nameof(ModActionRequested), RoutingStrategies.Bubble);

    public event EventHandler<UserClickedEventArgs>? UserClicked
    {
        add => AddHandler(UserClickedEvent, value);
        remove => RemoveHandler(UserClickedEvent, value);
    }

    public event EventHandler<ModActionEventArgs>? ModActionRequested
    {
        add => AddHandler(ModActionEvent, value);
        remove => RemoveHandler(ModActionEvent, value);
    }

    private Control? _lastClicked;

    private void RaiseUserClicked(string login, ChatEntry? entry) =>
        RaiseEvent(new UserClickedEventArgs(UserClickedEvent, login, entry, _lastClicked ?? this));

    /// <summary>Text that acts like a link: a name, mention or URL. Inlines
    /// can't take clicks, so it's a small TextBlock inside the text flow.</summary>
    private Inline Clickable(string text, IBrush brush, FontWeight weight, Action onClick, bool underline = false)
    {
        var block = new InlineTextBlock
        {
            Text = text,
            Foreground = brush,
            FontWeight = weight,
            Cursor = new Cursor(StandardCursorType.Hand),
            TextDecorations = underline ? Avalonia.Media.TextDecorations.Underline : null,
        };
        block.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) return;
            _lastClicked = block;
            onClick();
            e.Handled = true;
        };
        return Copies(new InlineUIContainer(block) { BaselineAlignment = BaselineAlignment.Baseline }, text);
    }

    // ----- Copying ----------------------------------------------------------

    /// <summary>What an inline control (a name, an emote) stands for when
    /// copied. Those without (badges, mod icons) are left out.</summary>
    public static readonly AttachedProperty<string?> CopyTextProperty =
        AvaloniaProperty.RegisterAttached<ChatLine, Inline, string?>("CopyText");

    private static Inline Copies(Inline inline, string text)
    {
        inline.SetValue(CopyTextProperty, text);
        return inline;
    }

    /// <summary>The text selected in a line, as it was written: the text
    /// itself only has a placeholder for each inline control.</summary>
    public static string SelectedCopyText(SelectableTextBlock block)
    {
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        var copied = new System.Text.StringBuilder();
        var at = 0;
        foreach (var inline in block.Inlines ?? [])
        {
            if (at >= end) break;
            switch (inline)
            {
                case Run { Text: { } text }:
                    var from = Math.Clamp(start - at, 0, text.Length);
                    copied.Append(text, from, Math.Clamp(end - at, 0, text.Length) - from);
                    at += text.Length;
                    break;
                case InlineUIContainer:
                    // One placeholder character in the text.
                    if (at >= start) copied.Append(inline.GetValue(CopyTextProperty));
                    at++;
                    break;
            }
        }
        return copied.ToString();
    }

    // The mod icons, in the shapes twitch.tv uses (a bin, a clock, a
    // no-entry sign), drawn as outlines on a 24-unit grid.
    private static readonly Geometry TrashIcon =
        Geometry.Parse("M4,7 L20,7 M9.5,7 L9.5,4 L14.5,4 L14.5,7 M6,7 L7,20 L17,20 L18,7 M10,11 L10,16 M14,11 L14,16");
    private static readonly Geometry TimeoutIcon =
        Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M12,7 L12,12 L15.5,14");
    private static readonly Geometry BanIcon =
        Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M5.64,5.64 L18.36,18.36");

    /// <param name="tip">Null for a placeholder: the icon's room, invisible
    /// and inert.</param>
    private Inline ModButton(Geometry icon, string? tip, ModAction action, ChatEntry entry)
    {
        var button = new Border
        {
            // Clicks land anywhere in the box, not just on the strokes.
            Background = Brushes.Transparent,
            Padding = new Thickness(1),
            Margin = new Thickness(0, 0, 3, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = 0.7,
            Child = new Path
            {
                Data = icon,
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Stroke = TextBrush,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            },
        };
        if (tip is null)
        {
            button.Opacity = 0;
            button.Cursor = null;
            return new InlineUIContainer(new InlineMiddle(button)) { BaselineAlignment = BaselineAlignment.Baseline };
        }
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) => button.Opacity = 1;
        button.PointerExited += (_, _) => button.Opacity = 0.7;
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            RaiseEvent(new ModActionEventArgs(ModActionEvent, action, entry));
            e.Handled = true;
        };
        return new InlineUIContainer(new InlineMiddle(button)) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    private static Inline Emote(Emote emote, IReadOnlyList<Emote> overlays, double height) =>
        new InlineUIContainer(new InlineMiddle(NewEmoteView(emote, emote.ImageUrl, overlays, height)))
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        };

    /// <summary>A gigantified emote: four times the size, from the largest
    /// image Twitch has (the usual 2.0 goes soft at that size), and standing
    /// on the baseline like the text beside it. Centred on the text like an
    /// ordinary emote, it hung most of its height below the line.</summary>
    private static Inline GiganticEmote(Emote emote, IReadOnlyList<Emote> overlays) =>
        new InlineUIContainer(NewEmoteView(emote, emote.LargestImageUrl, overlays, EmoteHeight * 4))
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        };

    private static EmoteView NewEmoteView(Emote emote, Uri image, IReadOnlyList<Emote> overlays, double height)
    {
        var tip = emote.Code + "\n" + emote.Provider switch
        {
            EmoteProvider.Bttv => "BTTV emote",
            EmoteProvider.Ffz => "FFZ emote",
            EmoteProvider.SevenTv => "7TV emote",
            _ => "Twitch emote",
        };
        if (overlays.Count > 0) tip += "\n+ " + string.Join(", ", overlays.Select(o => o.Code));
        return new EmoteView(image, [.. overlays.Select(o => o.ImageUrl)], tip)
        {
            EmoteHeight = height,
            FallbackText = emote.Code,
            // No negative margins to tuck emotes into the text line: they
            // clipped the descenders of names and text on the same line.
            Margin = new Thickness(1, 0, 1, 0),
        };
    }

    /// <summary>A cheer as twitch.tv draws it: the tier's picture, then the
    /// amount in the tier's colour.</summary>
    private static Inline Cheer(Cheer cheer)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new EmoteView(cheer.Image, null, $"{cheer.Prefix} {cheer.Bits:N0} bits")
        {
            EmoteHeight = EmoteHeight,
            FallbackText = cheer.Prefix,
            Margin = new Thickness(1, 0, 1, 0),
        });
        var color = Color.TryParse(cheer.Color, out var c) ? c : Color.Parse("#9147FF");
        row.Children.Add(new TextBlock
        {
            Text = cheer.Bits.ToString("N0"),
            Foreground = new SolidColorBrush(Readable(color)),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new InlineUIContainer(new InlineMiddle(row)) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    /// <summary>A GIF in the line, its title as the tooltip and as the text
    /// shown if it can't be loaded.</summary>
    private static Inline Gif(Uri url, string title)
    {
        var view = new EmoteView(url, null, title.Trim('[', ']') + "\nGIF via GIPHY")
        {
            EmoteHeight = GifHeight,
            FallbackText = title,
            Margin = new Thickness(0, 2),
        };
        // Standing on the baseline, like the text beside it.
        return new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    /// <summary>A round channel icon (its initial until the picture loads).</summary>
    private static Inline SourceIcon(StreamEdge.Streams.ChannelInfo? source)
    {
        var name = source?.DisplayName ?? "";
        var icon = new Panel { Width = BadgeHeight, Height = BadgeHeight, Margin = new Thickness(0, 0, 3, 0) };
        icon.Children.Add(new Border
        {
            Background = Brush("#3A3A3D"),
            CornerRadius = new CornerRadius(BadgeHeight / 2),
            Child = new TextBlock
            {
                Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        if (source?.ProfileImage is { } image)
            icon.Children.Add(new RemoteImage
            {
                Source = image,
                Stretch = Stretch.UniformToFill,
                Clip = new EllipseGeometry(new Rect(0, 0, BadgeHeight, BadgeHeight)),
            });
        ToolTip.SetTip(icon, name.Length > 0 ? "Sent from " + name : "Sent from a channel in this shared chat");
        return new InlineUIContainer(new InlineMiddle(icon)) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    private static Inline Badge(ChatServices.Badge badge)
    {
        Control child;
        string Tip(string name) => badge.Detail is { } detail ? name + "\n" + detail : name;
        if (badge.Image is { } image)
        {
            child = new EmoteView(image.ImageUrl, null, Tip(image.Title)) { EmoteHeight = BadgeHeight };
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
            ToolTip.SetTip(child, Tip(badge.Set));
        }
        child.Margin = new Thickness(0, 0, 3, 0);
        return new InlineUIContainer(new InlineMiddle(child)) { BaselineAlignment = BaselineAlignment.Baseline };
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

public enum ModAction
{
    Delete,
    Timeout,
    Ban,
}

public sealed class UserClickedEventArgs(RoutedEvent routedEvent, string login, ChatEntry? entry, Control anchor)
    : RoutedEventArgs(routedEvent)
{
    public string Login { get; } = login;

    /// <summary>The line clicked on, when it was the sender's name (null for
    /// an @mention): lets the card offer "delete this message".</summary>
    public ChatEntry? Entry { get; } = entry;

    public Control Anchor { get; } = anchor;
}

public sealed class ReplyRequestedEventArgs(RoutedEvent routedEvent, ChatEntry entry) : RoutedEventArgs(routedEvent)
{
    public ChatEntry Entry { get; } = entry;
}

public sealed class ModActionEventArgs(RoutedEvent routedEvent, ModAction action, ChatEntry entry)
    : RoutedEventArgs(routedEvent)
{
    public ModAction Action { get; } = action;
    public ChatEntry Entry { get; } = entry;
}
