using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Clearspace.Services;
using Shapes = System.Windows.Shapes;

namespace Clearspace;

// NEW (edit in place): theme cards. Any theme reads as a deck (Services/ThemeCards.cs): its colors, panels,
// patterns, fonts, layout, stickers, effects and icons.
// REWRITTEN (side panel, hand of cards): the first hand lay over the bottom of the window, fanned and half
// hidden, with a bar over the card and a borrow sheet over the window; it covered the explorer, and the card
// options moved about and were hard to click. Now:
//   - The hand: every card of the theme, side by side and all showing, in a row of its own under the window
//     (MainWindow.ThemeEdit.cs puts it there). Point at a card to lift it; click it to pick it.
//   - The panel's Cards tab: the picked card, in a box that always sits at the top of the tab: what it is made
//     of (click a color, a font, its pattern or corners to change it), Keep in My cards and Take it out;
//   - then Borrow cards: a deck from another theme or My cards (the cards you kept). Click a card to add it to your
//     theme, or drag it onto a part of the window (a pattern lands on the part you drop it on).
// The decks are only read while the Cards tab is open, so the rest of the editor stays quick.
// CHANGED (cards first): the box at the top also shows the part or sticker picked on the window (the cards its
// look is on are lifted in the hand), each kind of card has its own rows, and Add a card makes new ones.
// REMOVED (side panel): ThemeEditor.CardBar.cs (the bar over a card in the hand) and the hand and sheet here.
internal sealed partial class ThemeEditor
{
    private const string CardFormat = "Clearspace.ThemeCard";
    private const double CardWidth = 142, CardHeight = 128;

    // CHANGED (cards first): the picked card is remembered by what it is (CardId), not by its name, so it stays
    // picked while it changes (a font card is named after its font, and changing the font renames it).
    private string? _pickedCard;   // the card whose box is open, found again after a change
    private string _borrowDeck = "Mine";
    private readonly Dictionary<string, IReadOnlyList<ThemeCard>> _decks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceDictionary?> _looks = new();
    private readonly Dictionary<string, ThemeCard> _dragging = new();

    private static readonly Dictionary<CardKind, (string Word, Color Color)> Kinds = new()
    {
        [CardKind.Palette] = ("Colors", Color.FromRgb(0xD3, 0xA1, 0x5F)), [CardKind.Panels] = ("Panels", Color.FromRgb(0xE8, 0xC0, 0x7A)),
        [CardKind.Pattern] = ("Pattern", Color.FromRgb(0x7F, 0xB2, 0xE5)), [CardKind.Font] = ("Font", Color.FromRgb(0xC9, 0xA0, 0xE8)),
        [CardKind.Layout] = ("Layout", Color.FromRgb(0x9B, 0xD3, 0x7F)), [CardKind.Sticker] = ("Sticker", Color.FromRgb(0x64, 0xD8, 0xCF)),
        [CardKind.Effect] = ("Effect", Color.FromRgb(0xE8, 0xA0, 0xB4)), [CardKind.Icons] = ("Icons", Color.FromRgb(0xF2, 0xCC, 0x8F))
    };

    private string DeckName => ThemeManifest.Tidy(_name.Text, 60) is { Length: > 0 } name ? name : "This theme";

    // ---------------------------------------------------------------- the Cards tab

    // CHANGED (cards first): the Cards tab is where things are changed. At the top, always in the same place, a
    // box with what you are changing: the card you clicked in your hand, the part you clicked on the window, a
    // sticker, or your own icon being added (or, with nothing picked, the parts of the window to pick from).
    // Then Add a card, then Borrow cards.
    private void BuildCardsTab()
    {
        _dragging.Clear();
        IReadOnlyList<ThemeCard> deck;
        try { deck = ThemeCards.Deck(_document, DeckName); }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException) { deck = []; }
        var picked = deck.FirstOrDefault(IsPicked);
        if (picked is null && _pickedCard is not null) { _pickedCard = null; PaintHand(); }

        if (_iconForm) Framed(IconForm, AccentBrush());                                         // ThemeEditor.Make.cs
        else if (CurrentSticker() is { } sticker) Framed(() => StickerBox(sticker), AccentBrush());
        else if (SelectedPart() is { } part) Framed(() => PartBox(part, deck), AccentBrush());
        else if (picked is not null) CardBox(picked);
        else
        {
            Title("Cards", deck.Count == 0 ? "This theme has no cards yet: add one below, or click a part of the window."
                                           : "Click a card in your hand, or a part of the window, to change it.");
            PartList();
        }

        Heading("Add a card");
        AddCardPills();                                                                          // ThemeEditor.Make.cs

        Heading("Borrow cards");
        _body.Children.Add(Row("From", ValueText(DeckLabel(_borrowDeck)), DeckMenu, "My cards, any other theme, or a deck file"));
        var borrowPills = new List<Border> { Pill("Paste a code", PasteCode, tip: "Add a card, or a whole deck, from a code someone sent you") };   // NEW (theme codes)
        borrowPills.Add(Pill("Open a deck file…", () => OpenDeckFile(useWhole: false), tip: "Borrow from a deck file (.csdeck)"));   // NEW (deck files)
        var cards = DeckFor(_borrowDeck, out var problem);
        if (_borrowDeck != "Mine" && cards.Count > 0)
            borrowPills.Add(Pill("Use the whole deck", () => UseWholeDeck(_borrowDeck, cards), tip: "Your theme becomes this deck's cards (Undo brings yours back)"));
        Pills(borrowPills.ToArray());
        _body.Children.Add(Note(problem ?? (cards.Count == 0
            ? _borrowDeck == "Mine" ? "No cards kept yet. Keep a card from its box, or Keep as a card on a part." : "This deck has no cards."
            : "Click a card to add it to your theme, or drag it onto a part of the window.")));
        var borrow = new WrapPanel();
        foreach (var card in cards)
        {
            var view = CardView(card, picked: false);
            var chosen = card;
            Clicked(view, () => PlayCard(chosen, null));
            Draggable(view, chosen);
            var menu = new ContextMenu();
            Item(menu, "Add it to my theme", () => PlayCard(chosen, null));
            Item(menu, "Copy code", () => CopyCardCode(chosen));   // NEW (theme codes)
            if (_borrowDeck == "Mine") Item(menu, "Forget this card", () => ForgetCard(chosen));
            else Item(menu, "Keep in My cards", () => KeepCard(chosen));
            view.ContextMenu = menu;
            borrow.Children.Add(view);
        }
        _body.Children.Add(borrow);
    }

    private static Brush AccentBrush() => new SolidColorBrush(Color.FromRgb(0xF0, 0xB3, 0x5A));

    // ---------------------------------------------------------------- the hand (NEW, hand of cards)

    // CHANGED (organic hand): the cards are held like a real hand again, fanned and overlapping with their tops
    // showing, in the strip under the window. Point at one and it pops up straight and whole in a short eased
    // animation, nudging its neighbours aside; the picked card stays up. (The row of upright cards before this
    // was too stiff.) The strip is its own row, so a resting hand never covers the explorer.
    private const double HandCardWidth = 128, HandCardHeight = 184, HandHeight = 150, HandRest = 26, HandLift = 66, HandLeft = 118;
    private static readonly Duration HandSpeed = new(TimeSpan.FromMilliseconds(190));
    private static readonly IEasingFunction HandEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private readonly Canvas _handCards = new() { Background = Brushes.Transparent, ClipToBounds = false };
    private readonly TextBlock _handCount = new() { FontSize = 12 };
    private readonly List<(ThemeCard Card, Border Face)> _handViews = new();
    private string _handSignature = "";
    private int _hoverCard = -1;
    private double _handStep;

    private Border BuildHand()
    {
        var grid = new Grid { ClipToBounds = false };
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 20, 0, 0), IsHitTestVisible = false };
        side.Children.Add(new TextBlock { Text = "Your hand", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        _handCount.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        side.Children.Add(_handCount);
        grid.Children.Add(side);
        grid.Children.Add(_handCards);
        _handCards.SizeChanged += (_, _) => LayOutHand();
        // Which card is under the pointer goes by the strip of it that shows, so a card that has popped up
        // stays up while the pointer is anywhere on it, and the hand does not flicker between neighbours.
        _handCards.MouseMove += (_, e) => HoverAt(e.GetPosition(_handCards));
        _handCards.MouseLeave += (_, _) => { if (_hoverCard != -1) { _hoverCard = -1; AnimateHand(); } };
        var hand = new Border { Height = HandHeight, Child = grid, BorderThickness = new Thickness(0, 1, 0, 0), ClipToBounds = false };
        hand.Resources.MergedDictionaries.Add(Chrome());
        hand.SetResourceReference(Border.BackgroundProperty, "Base");
        hand.SetResourceReference(Border.BorderBrushProperty, "Line");
        hand.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "UIFont");
        hand.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Ink");
        System.Windows.Documents.TextElement.SetFontSize(hand, 13);
        AutomationProperties.SetName(hand, "Your hand of cards");
        return hand;
    }

    // Redraws the hand when the theme's cards changed; otherwise only shows which card is picked.
    private void SyncHand()
    {
        if (_finished) return;
        IReadOnlyList<ThemeCard> deck;
        try { deck = ThemeCards.Deck(_document, DeckName); }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException) { deck = []; }
        if (_pickedCard is not null && !deck.Any(IsPicked)) { _pickedCard = null; if (_tab == "Cards") SyncPanel(); }
        var signature = string.Join("\n", deck.Select(c => c.Signature));
        if (signature != _handSignature)
        {
            _handSignature = signature;
            _handCards.Children.Clear(); _handViews.Clear();
            foreach (var card in deck)
            {
                var face = HandCard(card);
                _handViews.Add((card, face));
                _handCards.Children.Add(face);
            }
            _handCount.Text = deck.Count == 1 ? "1 card" : $"{deck.Count} cards";
            _hoverCard = -1;
            LayOutHand();
        }
        PaintHand();
    }

    // Where each card rests: spread across the strip (overlapping when there are many), fanned out from the
    // middle, the outer ones a little lower, like cards held in a hand.
    private void LayOutHand()
    {
        var count = _handViews.Count;
        var width = _handCards.ActualWidth;
        if (count == 0 || width < 200) return;
        var room = width - HandLeft - 24;
        _handStep = count == 1 ? 0 : Math.Min(HandCardWidth + 12, Math.Max(26, (room - HandCardWidth) / (count - 1)));
        var spread = HandCardWidth + _handStep * (count - 1);
        var left = HandLeft + Math.Max(0, (room - spread) / 2);
        var middle = (count - 1) / 2d;
        for (var i = 0; i < count; i++)
        {
            var face = _handViews[i].Face;
            var off = i - middle;
            Canvas.SetLeft(face, left + i * _handStep);
            Canvas.SetTop(face, HandRest + off * off * 1.6);
            face.Tag = off * 2.6;   // its resting lean, in degrees
        }
        AnimateHand(instant: true);
    }

    private void HoverAt(Point point)
    {
        var count = _handViews.Count;
        if (count == 0) return;
        var first = Canvas.GetLeft(_handViews[0].Face);
        var index = -1;
        if (point.X >= first && point.X <= Canvas.GetLeft(_handViews[count - 1].Face) + HandCardWidth)
            index = _handStep <= 0 ? 0 : Math.Min(count - 1, (int)((point.X - first) / _handStep));
        if (index == _hoverCard) return;
        _hoverCard = index;
        AnimateHand();
    }

    // Every card eases to where it should be now: the one under the pointer up, straight and a little bigger,
    // its neighbours nudged aside; the picked one up; the rest back in the fan.
    private void AnimateHand(bool instant = false)
    {
        for (var i = 0; i < _handViews.Count; i++)
        {
            var (card, face) = _handViews[i];
            if (face.RenderTransform is not TransformGroup { Children.Count: 3 } group) continue;
            var scale = (ScaleTransform)group.Children[0];
            var turn = (RotateTransform)group.Children[1];
            var move = (TranslateTransform)group.Children[2];
            var hovered = i == _hoverCard;
            var picked = IsPicked(card);
            var related = !picked && IsRelated(card);   // NEW (cards first): a card the picked part's look is on
            var lean = face.Tag is double t ? t : 0;
            var nudge = _hoverCard < 0 || hovered ? 0 : Math.Sign(i - _hoverCard) * (Math.Abs(i - _hoverCard) == 1 ? 22 : Math.Abs(i - _hoverCard) == 2 ? 9 : 0);
            var rise = hovered ? -HandLift : picked ? -HandLift * .55 : related ? -HandLift * .3 : 0;
            Ease(move, TranslateTransform.YProperty, rise - (hovered || picked || related ? Canvas.GetTop(face) - HandRest : 0), instant);
            Ease(move, TranslateTransform.XProperty, nudge, instant);
            Ease(turn, RotateTransform.AngleProperty, hovered || picked || related ? 0 : lean, instant);
            Ease(scale, ScaleTransform.ScaleXProperty, hovered ? 1.06 : 1, instant);
            Ease(scale, ScaleTransform.ScaleYProperty, hovered ? 1.06 : 1, instant);
            Panel.SetZIndex(face, hovered ? 1000 : picked ? 900 : related ? 800 + i : i);
            if (face.Effect is DropShadowEffect shadow) Ease(shadow, DropShadowEffect.OpacityProperty, hovered ? .7 : .45, instant);
        }
    }

    private static void Ease(System.Windows.Media.Animation.IAnimatable target, DependencyProperty property, double to, bool instant)
    {
        if (instant) { target.BeginAnimation(property, null); ((DependencyObject)target).SetValue(property, to); return; }
        target.BeginAnimation(property, new System.Windows.Media.Animation.DoubleAnimation(to, HandSpeed) { EasingFunction = HandEase });
    }

    // Outlines the picked card (and the cards the picked part's look is on) and sends them up.
    private void PaintHand()
    {
        _relatedKeys = SelectedPart() is { } part ? PartKeys(part) : null;
        foreach (var (card, face) in _handViews)
        {
            var on = IsPicked(card) || IsRelated(card);
            face.BorderThickness = new Thickness(on ? 2 : 1);
            if (on) face.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else if (face.IsMouseOver) face.BorderBrush = new SolidColorBrush(Kinds[card.Kind].Color);
            else face.SetResourceReference(Border.BorderBrushProperty, "Line");
        }
        AnimateHand();
    }

    private IReadOnlySet<string>? _relatedKeys;   // NEW (cards first): the picked part's keys, while a part is picked
    private bool IsRelated(ThemeCard card) => _relatedKeys is { } keys && card.Keys.Any(keys.Contains);

    // A click on a card in the hand picks it (or lets go of it); a card named in a part's box is always picked.
    private void PickCard(ThemeCard card, bool toggle = true)
    {
        _pickedCard = toggle && IsPicked(card) ? null : CardId(card);
        _iconForm = false;
        if (_pickedCard is not null && (_selected is not null || _sticker is not null)) { _selected = null; _sticker = null; RefreshCanvas(); }
        PaintHand();
        if (_pickedCard is null) { SyncPanel(); return; }
        ShowTab("Cards");
        _scroll.ScrollToTop();
    }

    // A card in the hand: its kind and where it is from along the top, a picture of what it does (drawn from its
    // real colors, pattern, font or picture), its name and a line about it.
    private Border HandCard(ThemeCard card)
    {
        var look = LookOf(card);
        var (word, tone) = Kinds[card.Kind];
        var kind = new TextBlock { Text = word.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tone) };
        var from = new TextBlock { Text = card.From, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 60, HorizontalAlignment = HorizontalAlignment.Right };
        from.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        var head = new DockPanel();
        DockPanel.SetDock(from, Dock.Right); head.Children.Add(from); head.Children.Add(kind);
        var art = Art(card, look);
        art.Height = 66;
        art.Margin = new Thickness(0, 7, 0, 0);
        var name = new TextBlock { Text = card.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        var detail = new TextBlock { Text = card.Detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(art); stack.Children.Add(name); stack.Children.Add(detail);
        var face = new Border
        {
            Width = HandCardWidth, Height = HandCardHeight, Padding = new Thickness(10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = stack,
            Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null, ToolTip = $"{word}: {card.Name} · {card.Detail}\nClick to change it or take it out",
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = .45, Color = Colors.Black },
            RenderTransformOrigin = new Point(.5, 1),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new RotateTransform(0), new TranslateTransform(0, 0) } }
        };
        face.SetResourceReference(Border.BackgroundProperty, "Surface");
        face.SetResourceReference(Border.BorderBrushProperty, "Line");
        face.MouseEnter += (_, _) => { if (!IsPicked(card)) face.BorderBrush = new SolidColorBrush(tone); };
        face.MouseLeave += (_, _) => { if (!IsPicked(card)) face.SetResourceReference(Border.BorderBrushProperty, "Line"); };
        Clicked(face, () => PickCard(card));
        var menu = new ContextMenu();
        Item(menu, "Change it", () => { if (!IsPicked(card)) PickCard(card); });
        Item(menu, "Keep in My cards", () => KeepCard(card), tip: "So you can play it in any theme");
        Item(menu, "Copy code", () => CopyCardCode(card), tip: "A line of text to send; anyone can paste it into Clearspace");   // NEW (theme codes)
        if (!card.IsDefault) Item(menu, "Take it out of this theme", () => TakeOut(card));   // CHANGED (every card by default)
        face.ContextMenu = menu;
        AutomationProperties.SetName(face, $"{word} card: {card.Name}");
        return face;
    }

    private bool IsPicked(ThemeCard card) => _pickedCard is { } id && id == CardId(card);

    // What a card is, whatever it is called: there is one card of most kinds in a deck; a pattern, a sticker or an
    // effect is told apart by the setting it is about.
    private static readonly string[] EffectKeys = ["OverlayFill", "EdgeShade", "BindingFill", "NoteTilt"];
    private static string CardId(ThemeCard card) => card.Kind switch
    {
        CardKind.Effect => "Effect:" + (EffectKeys.FirstOrDefault(k => card.Keys.Contains(k)) ?? card.Name),
        CardKind.Pattern or CardKind.Sticker => card.Kind + ":" + (card.Keys.FirstOrDefault(k => k != "Theme.BaseColor") ?? card.Name),
        _ => card.Kind.ToString()
    };

    // A click on a card: a press and a release on it without a drag in between.
    private static void Clicked(Border view, Action click)
    {
        var pressed = false;
        view.PreviewMouseLeftButtonDown += (_, _) => pressed = true;
        view.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false; e.Handled = true; click();
        };
        view.MouseLeave += (_, _) => pressed = false;
        view.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; click(); } };
    }

    // A borrowed card can be dragged onto the window; it lands on the part it is dropped on.
    private void Draggable(Border view, ThemeCard card)
    {
        var id = Guid.NewGuid().ToString("N");
        _dragging[id] = card;
        Point? start = null;
        view.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(view);
        view.PreviewMouseLeftButtonUp += (_, _) => start = null;
        view.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(view);
            if (Math.Abs(p.X - s.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - s.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            DragDrop.DoDragDrop(view, new DataObject(CardFormat, id), DragDropEffects.Copy);
            HideSnap();
        };
    }

    private void DeckMenu(FrameworkElement target)
    {
        var menu = Menu(target);
        Item(menu, "My cards", () => { _borrowDeck = "Mine"; SyncPanel(); }, check: _borrowDeck == "Mine");
        foreach (var file in _decks.Keys.Where(k => k.StartsWith("file:", StringComparison.Ordinal)).ToList())   // NEW (deck files): ones opened this time
        {
            var deck = file;
            Item(menu, DeckLabel(deck) + " (file)", () => { _borrowDeck = deck; SyncPanel(); }, check: _borrowDeck == deck);
        }
        Item(menu, "Open a deck file…", () => OpenDeckFile(useWhole: false));
        menu.Items.Add(new Separator());
        foreach (var theme in ThemeService.Themes.Where(t => t.Problem is null && !t.BuiltIn && !string.Equals(t.Name, _editing?.Name, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = theme.Name;
            Item(menu, theme.Label, () => { _borrowDeck = name; SyncPanel(); }, check: string.Equals(_borrowDeck, name, StringComparison.OrdinalIgnoreCase));
        }
        menu.IsOpen = true;
    }

    private static string DeckLabel(string deck) => deck == "Mine" ? "My cards"
        : deck.StartsWith("file:", StringComparison.Ordinal) ? System.IO.Path.GetFileNameWithoutExtension(deck[5..])   // NEW (deck files)
        : ThemeService.Themes.FirstOrDefault(t => string.Equals(t.Name, deck, StringComparison.OrdinalIgnoreCase))?.Label ?? deck;

    private IReadOnlyList<ThemeCard> DeckFor(string deck, out string? problem)
    {
        problem = null;
        if (_decks.TryGetValue(deck, out var known)) return known;
        IReadOnlyList<ThemeCard> cards;
        if (deck == "Mine") cards = ThemeCards.Saved();
        else if (ThemeService.Themes.FirstOrDefault(t => string.Equals(t.Name, deck, StringComparison.OrdinalIgnoreCase)) is { } theme)
        {
            try { cards = ThemeCards.Deck(ThemeStudioDocument.Open(theme), theme.Label); }
            catch (Exception e) when (e is ThemeFormatException or System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { problem = $"{theme.Label} could not be read: {e.Message}"; return []; }
        }
        else cards = [];
        _decks[deck] = cards;
        return cards;
    }

    private void RefreshSavedCards() => _decks.Remove("Mine");

    // ---------------------------------------------------------------- the picked card's box

    // What the card is made of, as rows to change, then Keep, Copy code and Take it out. Always at the top of the tab.
    // CHANGED (cards first): each kind of card has what it needs here, so a card is where its things are changed
    // (they used to be spread over the Theme tab): the colors card its main colors, a font card its fonts and
    // pixel lettering, the layout card its layouts, size and fading toolbar, a pattern card its pattern or
    // gradient and where it goes, an effect card its effect, the icons card its style and your own icons.
    private void CardBox(ThemeCard card)
    {
        var (word, tone) = Kinds[card.Kind];
        Framed(() =>
        {
            var kind = new TextBlock { Text = word.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tone), Margin = new Thickness(0, 6, 0, 0) };
            _body.Children.Add(kind);
            _body.Children.Add(new TextBlock { Text = card.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            _body.Children.Add(Note(card.IsDefault ? "Clearspace's own, for now. Change anything here and it goes on this card." : card.Detail + " · from " + card.From));   // CHANGED (every card by default)
            CardRows(card);
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            actions.Children.Add(Pill("Keep in My cards", () => KeepCard(card), tip: "So you can play it in any theme"));
            actions.Children.Add(Pill("Copy code", () => CopyCardCode(card), tip: "A line of text to send; anyone can paste it into Clearspace"));   // NEW (theme codes)
            if (!card.IsDefault) actions.Children.Add(Pill("Take it out", () => TakeOut(card), tip: "Undo puts it back", danger: true));   // CHANGED (every card by default)
            actions.Children.Add(Pill("Done", () => { _pickedCard = null; PaintHand(); SyncPanel(); }));
            _body.Children.Add(actions);
        }, new SolidColorBrush(tone));
    }

    // The rows a card of each kind has.
    private void CardRows(ThemeCard card)
    {
        var keys = card.Keys.Where(k => k != "Theme.BaseColor").Distinct().ToList();
        switch (card.Kind)
        {
            case CardKind.Palette:
            {
                foreach (var (key, label) in new[] { ("Base", "Background"), ("Surface", "Panels"), ("Ink", "Text"), ("Accent", "Accent") }) ColorRow(label, key, label);
                var others = keys.Where(k => k is not ("Base" or "Surface" or "Ink" or "Accent") && Value(k) is SolidColorBrush or Color).ToList();
                if (others.Count > 0)
                    _body.Children.Add(Row("More colors", ValueText(others.Count.ToString()), row =>
                    {
                        var menu = Menu(row);
                        foreach (var key in others)
                        {
                            var label = ThemeSettingsWindow.SettingLabel(key);
                            Item(menu, label, () => PickColor(key, label)).Icon = Dot(Value(key) switch { Brush b => b, Color c => new SolidColorBrush(c), _ => Brushes.Transparent }, 14);
                        }
                        menu.IsOpen = true;
                    }, "Every other color this card holds"));
                _body.Children.Add(Row("Every setting…", null, _ => OpenSettings()));
                break;
            }
            case CardKind.Panels:
            {
                var corners = Value("R8") is CornerRadius r ? r.TopLeft : -1;
                _body.Children.Add(Label("Corners everywhere"));
                Choices(new (string, double)[] { ("Square", 0), ("Slight", 4), ("Round", 8), ("Very round", 14) }
                    .Select(c => (c.Item1, Math.Abs(corners - c.Item2) < .5, (Action)(() => TryChange(() => _document.Corners(c.Item2))))));
                _body.Children.Add(Label("The panels"));
                foreach (var id in new[] { "Toolbar", "Sidebar", "Files", "Status" })
                {
                    var part = PartById(id);
                    _body.Children.Add(Row(part.Label, Dot(Value(part.Fill!) switch { Brush b => b, _ => Brushes.Transparent }, 18), _ => Select(id), "Its fill, outline and corners"));
                }
                break;
            }
            case CardKind.Pattern:
                foreach (var key in keys.Where(k => Value(k) is Brush and not SolidColorBrush).Take(1))
                {
                    _body.Children.Add(Row("Pattern", ValueText(PatternName(key)), row => PatternMenu(row, key)));
                    GradientRows(key);   // ThemeEditor.Make.cs
                    _body.Children.Add(Row("Where it goes", ValueText(ThemeCards.PlaceName(key)), row => MovePatternMenu(row, card, key), "Put this card on another part"));
                }
                break;
            case CardKind.Font:
            {
                foreach (var (key, label) in new[] { ("UIFont", "Interface"), ("SerifFont", "File names"), ("DisplayFont", "Headings"), ("MonoFont", "Paths & code") })
                    _body.Children.Add(Row(label, ValueText(FontLabel(key), Value(key) as FontFamily), row => FontsMenu(row, key)));
                Border? all = null;
                all = Pill("One font for all…", () => FontsMenu(all!, "All"));
                Pills(all, Pill("Add a font file…", ImportFont));
                Check("Crisp pixel lettering", _document.Manifest.CrispText, on => Change(_document with { Manifest = _document.Manifest with { CrispText = on } }));
                break;
            }
            case CardKind.Layout:
            {
                if (_document.LayoutJson is not null)
                {
                    _body.Children.Add(Note("A custom layout."));
                    Pills(Pill("Change it…", OpenLayoutBuilder), Pill("Back to the usual layout", () => TryChange(() => (_document with { LayoutJson = null }).DropUnusedImages())));
                }
                else
                {
                    Choices(LayoutPresets.Select(p => (p.Name, false, (Action)(() => TryChange(() => WithLayout(_document, p.Layout))))));
                    Pills(Pill("Build a custom layout…", OpenLayoutBuilder, tip: "Put any part anywhere: extra file panes side by side, a browser look, a dashboard, text, pictures, a clock"));
                }
                var scale = Value("InterfaceScale") is ScaleTransform st ? st.ScaleX : 1;
                _body.Children.Add(Label("Size of everything"));
                Choices(new[] { 90, 100, 110, 125, 150 }.Select(percent => (percent + "%", Math.Abs(scale * 100 - percent) < 1, (Action)(() => TryChange(() => _document.Scale(percent / 100d))))));
                Check("Fade the toolbar when idle", _document.Manifest.FadeToolbar, on => Change(_document with { Manifest = _document.Manifest with { FadeToolbar = on } }));
                break;
            }
            case CardKind.Effect:
                if (keys.Contains("OverlayFill")) _body.Children.Add(Row("Over the window", null, OverlayMenu, "Scanlines, film grain, a fine grid, a picture…"));
                if (keys.Contains("EdgeShade")) _body.Children.Add(Row("Light and shade", null, ShadeMenu, "A vignette, a glow, a sunbeam, dusk"));
                if (keys.Contains("BindingFill")) _body.Children.Add(Row("Down the left edge", null, EdgeMenu, "A notebook spiral or warning tape"));
                if (keys.Contains("NoteTilt")) Check("Leaning tooltips", true, on => TryChange(() => on ? _document.Tilt(-1.4) : _document.Without("NoteTilt")));
                break;
            case CardKind.Sticker:
                if (keys.FirstOrDefault(k => k.StartsWith("Decor.", StringComparison.Ordinal)) is { } stickerKey && _document.Stickers().FirstOrDefault(s => s.Key == stickerKey) is { } sticker)
                {
                    _body.Children.Add(Row("Move or resize it on the window", null, _ => Select(null, sticker.Key)));
                    _body.Children.Add(Row("Edit the picture…", null, _ => EditSticker(sticker)));
                }
                break;
            case CardKind.Icons:
                _body.Children.Add(Row("Icon style", ValueText(ThemeCards.IconName(_document.Manifest.Icons)), IconsMenu));
                OwnIconRows();   // ThemeEditor.Make.cs
                break;
        }
    }

    private void TakeOut(ThemeCard card)
    {
        _pickedCard = null;
        TryChange(() => ThemeCards.TakeOut(_document, card));
        Report($"{card.Name} taken out. Undo puts it back.");
    }

    private void KeepCard(ThemeCard card)
    {
        try
        {
            ThemeCards.Keep(card);
            RefreshSavedCards();
            Report($"Kept {card.Name}. It is under Borrow cards › My cards.");
            if (_tab == "Cards") SyncPanel();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Report("Could not keep the card: " + e.Message, true); }
    }

    private void ForgetCard(ThemeCard card)
    {
        try { ThemeCards.Forget(card); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Report(e.Message, true); }
        RefreshSavedCards();
        SyncPanel();
    }

    // ---------------------------------------------------------------- playing cards

    private void PlayCard(ThemeCard card, CanvasPart? on)
    {
        var target = card.Kind == CardKind.Pattern && on?.PatternTarget is { } aim ? aim : null;
        try
        {
            Change(ThemeCards.Play(_document, card, target));
            Report(target is null ? $"Added {card.Name} from {card.From}. Undo takes it back." : $"Added {card.Name} {ThemeCards.PlaceName(target).ToLowerInvariant()}.");
        }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
        { Report($"{card.Name} could not be added: {error.Message}", true); }
    }

    /// <summary>Plays a card onto the theme, aimed at a part when it is a pattern. For tests.</summary>
    internal void Play(ThemeCard card, string? partId = null) => PlayCard(card, partId is null ? null : PartById(partId));

    private ThemeCard? DraggedCard(DragEventArgs e) =>
        e.Data.GetDataPresent(CardFormat) && e.Data.GetData(CardFormat) is string id && _dragging.TryGetValue(id, out var card) ? card : null;

    private void CardDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DraggedCard(e) is not { } card) { e.Effects = DragDropEffects.None; return; }
        var part = PartAt(e.GetPosition(_canvas));
        e.Effects = DragDropEffects.Copy;
        if (card.Kind == CardKind.Pattern && part?.PatternTarget is not null) ShowSnap(part, $"{card.Name} on the {part.Label.ToLowerInvariant()}");
        else if (part is not null) ShowSnap(PartById("Window"), "Add " + card.Name);
    }

    private void CardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideSnap();
        if (DraggedCard(e) is not { } card) return;
        var part = PartAt(e.GetPosition(_canvas));
        Dispatcher.BeginInvoke(new Action(() => PlayCard(card, part)));
    }

    // ---------------------------------------------------------------- drawing a card

    private ResourceDictionary? LookOf(ThemeCard card)
    {
        if (_looks.TryGetValue(card.Signature, out var look)) return look;
        look = ThemeCards.Look(card);
        if (_looks.Count > 200) _looks.Clear();
        _looks[card.Signature] = look;
        return look;
    }

    private object? From(ResourceDictionary? look, string key) => look is not null && look.Contains(key) ? look[key] : _fallback.Contains(key) ? _fallback[key] : null;

    private Color FlatOf(ResourceDictionary? look, string key)
    {
        var value = From(look, key);
        if (key == "Base" && value is not (SolidColorBrush or GradientBrush) && From(look, "Theme.BaseColor") is Color flat) return flat;
        return Raw(value) is { A: > 0 } c ? Color.FromRgb(c.R, c.G, c.B) : Raw(From(null, key));
    }

    // A card: its kind, a picture of what it does (drawn from its real colors, pattern, font or picture) and its name.
    private Border CardView(ThemeCard card, bool picked)
    {
        var look = LookOf(card);
        var (word, tone) = Kinds[card.Kind];
        var kind = new TextBlock { Text = word.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tone) };
        var name = new TextBlock { Text = card.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
        var from = new TextBlock { Text = card.From, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
        from.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
        var stack = new StackPanel();
        stack.Children.Add(kind);
        var art = Art(card, look);
        art.Margin = new Thickness(0, 5, 0, 0);
        stack.Children.Add(art);
        stack.Children.Add(name);
        stack.Children.Add(from);
        var view = new Border
        {
            Width = CardWidth, Height = CardHeight, Padding = new Thickness(9, 8, 9, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(picked ? 2 : 1), Child = stack,
            Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null, ToolTip = $"{word}: {card.Name} · {card.Detail}", Margin = new Thickness(0, 0, 8, 8)
        };
        view.SetResourceReference(Border.BackgroundProperty, "Base");
        if (picked) view.BorderBrush = new SolidColorBrush(tone); else view.SetResourceReference(Border.BorderBrushProperty, "Line");
        view.MouseEnter += (_, _) => { if (!picked) view.SetResourceReference(Border.BorderBrushProperty, "Accent"); };
        view.MouseLeave += (_, _) => { if (!picked) view.SetResourceReference(Border.BorderBrushProperty, "Line"); };
        AutomationProperties.SetName(view, $"{word} card: {card.Name}");
        return view;
    }

    private FrameworkElement Art(ThemeCard card, ResourceDictionary? look)
    {
        var art = new Border { Height = 48, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = new SolidColorBrush(FlatOf(look, "Base")) };
        var first = card.Keys.FirstOrDefault() ?? "";
        switch (card.Kind)
        {
            case CardKind.Palette:
            {
                var strip = new UniformGrid { Columns = 5 };
                foreach (var key in new[] { "Base", "Surface", "Accent", "InkMuted", "Ink" })
                    strip.Children.Add(new Border { Background = new SolidColorBrush(FlatOf(look, key)) });
                art.Child = strip;
                break;
            }
            case CardKind.Panels:
            {
                var grid = new Grid { Margin = new Thickness(5) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                grid.RowDefinitions.Add(new RowDefinition());
                Border Box(string part, int row, int column, int span)
                {
                    var corner = From(look, $"Part.{part}.Corner") is CornerRadius r ? new CornerRadius(Math.Min(5, r.TopLeft / 2)) : new CornerRadius(3);
                    var box = new Border { Margin = new Thickness(1.5), CornerRadius = corner, BorderThickness = new Thickness(1),
                        Background = From(look, $"Part.{part}.Fill") as Brush ?? Brushes.Transparent, BorderBrush = From(look, $"Part.{part}.Edge") as Brush ?? Brushes.Transparent };
                    Grid.SetRow(box, row); Grid.SetColumn(box, column); Grid.SetColumnSpan(box, span);
                    return box;
                }
                grid.Children.Add(Box("Toolbar", 0, 0, 2)); grid.Children.Add(Box("Sidebar", 1, 0, 1)); grid.Children.Add(Box("Files", 1, 1, 1));
                art.Child = grid;
                break;
            }
            case CardKind.Pattern or CardKind.Effect:
            {
                var key = card.Kind == CardKind.Effect ? card.Keys.FirstOrDefault(k => k is "OverlayFill" or "EdgeShade" or "BindingFill") ?? first : first;
                if (From(look, key) is Brush brush) art.Child = new Border { Background = brush };
                else if (key == "NoteTilt") art.Child = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(FlatOf(look, "Accent")), RenderTransform = new RotateTransform(-6), RenderTransformOrigin = new Point(.5, .5) };
                break;
            }
            case CardKind.Font:
                art.Child = new TextBlock { Text = "Aa", FontSize = 26, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = From(look, first.Length > 0 ? first : "UIFont") as FontFamily ?? new FontFamily("Georgia"),   // CHANGED (every card by default): Clearspace's own font when it sets none
                    Foreground = new SolidColorBrush(StudioUi.TextOn(FlatOf(look, "Base"))) };
                break;
            case CardKind.Layout:
                art.Child = LayoutArt(look, card.LayoutJson is not null);
                break;
            case CardKind.Sticker:
            {
                var element = System.Xml.Linq.XElement.Parse(card.Elements[0]);
                var source = ((string?)element.Attribute("ImageSource") ?? "").Replace("images/", "", StringComparison.Ordinal);
                if (card.Images.TryGetValue(source, out var bytes))
                {
                    art.Background = StudioUi.Checker;
                    try { art.Child = new Border { Background = new ImageBrush(StudioPicture.Load(bytes)) { Stretch = Stretch.Uniform } }; }
                    catch (Exception e) when (e is NotSupportedException or ArgumentException or System.IO.IOException or InvalidOperationException) { }
                }
                break;
            }
            case CardKind.Icons:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                foreach (var bytes in card.Images.Values.Take(3))
                    try { row.Children.Add(new Border { Width = 24, Height = 24, Margin = new Thickness(3), Background = new ImageBrush(StudioPicture.Load(bytes)) { Stretch = Stretch.Uniform } }); }
                    catch (Exception e) when (e is NotSupportedException or ArgumentException or System.IO.IOException or InvalidOperationException) { }
                if (row.Children.Count == 0)
                    for (var i = 0; i < 3; i++)
                        row.Children.Add(new Shapes.Path { Data = Geometry.Parse("M2,1 L10,1 L14,5 L14,19 L2,19 Z"), Width = 14, Height = 20, Margin = new Thickness(3), Stretch = Stretch.Uniform,
                            Stroke = new SolidColorBrush(FlatOf(look, "Accent")), StrokeThickness = 1.6 });
                art.Child = row;
                break;
            }
        }
        return art;
    }

    // Where the parts sit, as a little map.
    private FrameworkElement LayoutArt(ResourceDictionary? look, bool custom)
    {
        bool Shown(string key) => From(look, key) is Visibility.Visible;
        var accent = new SolidColorBrush(FlatOf(look, "Accent"));
        var ink = new SolidColorBrush(Color.FromArgb(0x55, 0xEC, 0xE9, 0xE3));
        var faint = new SolidColorBrush(Color.FromArgb(0x24, 0xEC, 0xE9, 0xE3));
        var map = new Canvas { Width = 108, Height = 66 };
        if (custom)
        {
            for (var i = 0; i < 3; i++) { var pane = new Border { Width = 30, Height = 46, CornerRadius = new CornerRadius(3), Background = i == 0 ? ink : faint }; Canvas.SetLeft(pane, 6 + i * 34); Canvas.SetTop(pane, 10); map.Children.Add(pane); }
            return new Viewbox { Child = map, Stretch = Stretch.Uniform };
        }
        var bottom = Shown("Explorer.ToolbarBottom");
        var toolbar = new Border { Width = 96, Height = 9, CornerRadius = new CornerRadius(2), Background = accent };
        Canvas.SetLeft(toolbar, 6); Canvas.SetTop(toolbar, bottom ? 51 : 6); map.Children.Add(toolbar);
        var bodyTop = bottom ? 6d : 19; var bodyHeight = 41d;
        double filesLeft = 6, filesWidth = 96;
        if (From(look, "Explorer.SidebarVisibility") is not Visibility.Collapsed)
        {
            var right = Shown("Explorer.SidebarOnRight");
            var side = new Border { Width = 24, Height = bodyHeight, CornerRadius = new CornerRadius(2), Background = ink };
            Canvas.SetLeft(side, right ? 78 : 6); Canvas.SetTop(side, bodyTop); map.Children.Add(side);
            filesWidth -= 28; if (!right) filesLeft += 28;
        }
        var files = new Border { Width = filesWidth, Height = bodyHeight, CornerRadius = new CornerRadius(2), Background = faint };
        Canvas.SetLeft(files, filesLeft); Canvas.SetTop(files, bodyTop); map.Children.Add(files);
        if (From(look, "Explorer.StatusBarVisibility") is not Visibility.Collapsed)
        {
            var status = new Border { Width = filesWidth - 6, Height = 4, CornerRadius = new CornerRadius(2), Background = ink };
            Canvas.SetLeft(status, filesLeft + 3); Canvas.SetTop(status, Shown("Explorer.StatusBarTop") ? bodyTop + 3 : bodyTop + bodyHeight - 7); map.Children.Add(status);
        }
        return new Viewbox { Child = map, Stretch = Stretch.Uniform };
    }
}
