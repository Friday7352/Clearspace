using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Clearspace.Services;

namespace Clearspace;

// NEW (cards first): making new cards, and the things a card can hold that had no place before.
//   - Add a card (Cards tab): a pattern, a gradient, a font, a layout, an effect, a sticker, a picture behind the
//     files, your own icon or an icon style. Whatever is made becomes a card in your hand.
//   - Gradients: on the window or on any panel. Two colors and a direction (down, across, diagonal, or glowing
//     from the middle). A gradient is a pattern card; its box has its two colors and its direction.
//   - Your own icons: a picture for one file type (.pdf), one file name (notes.txt) or all folders. They are on
//     the icons card, which lists them, with a way to add another or take one off. (The old theme studio had
//     this; the first edit-in-place editor did not.)
//   - Deck files: all your cards in one file (.csdeck), pictures and fonts included (Services/ThemeDeckFile.cs).
//     Save one from the Theme tab; open one to borrow from (Cards tab) or to start from (Theme tab).
internal sealed partial class ThemeEditor
{
    private bool _iconForm;                      // the box at the top of the Cards tab is adding your own icon
    private string _iconMatch = "Type";          // what the icon is for: "Type", "Name" or "Folder"
    private readonly TextBox _iconTarget = new() { Text = ".pdf", MinWidth = 140 };
    private readonly Dictionary<string, ThemeDeckFile.Deck> _deckFiles = new(StringComparer.OrdinalIgnoreCase);

    private static MenuItem Sub(ItemsControl menu, string header)
    {
        var item = new MenuItem { Header = header };
        menu.Items.Add(item);
        return item;
    }

    // The places a pattern or a gradient can go.
    private static readonly (string Key, string Label)[] PatternPlaces =
    [
        ("Base", "On the window"), ("Part.Files.Fill", "On the file area"), ("Part.Sidebar.Fill", "On the sidebar"),
        ("Part.Toolbar.Fill", "On the toolbar"), ("Part.Status.Fill", "On the status bar")
    ];

    // ---------------------------------------------------------------- Add a card

    private void AddCardPills()
    {
        Border? pattern = null, gradient = null, font = null, layout = null, effect = null, icons = null;
        pattern = Pill("Pattern…", () =>
        {
            var menu = Menu(pattern!);
            foreach (var (key, label) in PatternPlaces) FillPatternItems(Sub(menu, label), key);
            menu.IsOpen = true;
        }, tip: "Dots, grid, stripes, waves… or one you draw, on the window or a panel");
        gradient = Pill("Gradient…", () =>
        {
            var menu = Menu(gradient!);
            foreach (var (key, label) in PatternPlaces)
            {
                var sub = Sub(menu, label);
                foreach (var direction in GradientDirections) { var place = key; var way = direction; Item(sub, way, () => TryChange(() => WithGradient(place, GradientStart(place), GradientEnd(place), way))); }
            }
            menu.IsOpen = true;
        }, tip: "Two colors blending into each other, on the window or a panel");
        font = Pill("Font…", () => FontsMenu(font!, "All"), tip: "One typeface for all text (change each kind on the font card)");
        layout = Pill("Layout…", () =>
        {
            var menu = Menu(layout!);
            foreach (var (name, tip, made) in LayoutPresets) { var l = made; Item(menu, name, () => TryChange(() => WithLayout(_document, l)), tip: tip); }
            menu.Items.Add(new Separator());
            Item(menu, "Build a custom layout…", OpenLayoutBuilder);
            menu.IsOpen = true;
        }, tip: "Where the parts sit");
        effect = Pill("Effect…", () =>
        {
            var menu = Menu(effect!);
            FillOverlay(Sub(menu, "Over the window"));
            FillShade(Sub(menu, "Light and shade"));
            FillEdge(Sub(menu, "Down the left edge"));
            Item(menu, "Leaning tooltips", () => TryChange(() => _document.Tilt(-1.4)));
            menu.IsOpen = true;
        });
        icons = Pill("Icon style…", () => IconsMenu(icons!));
        Pills(pattern, gradient, font, layout, effect,
              Pill("Sticker…", AddSticker, tip: "A picture anywhere on the window"),
              Pill("Picture behind the files…", ChooseBackdrop),
              Pill("Own icon…", StartIconForm, tip: "Your own picture for a file type, a file name or all folders"),
              icons);
    }

    // The patterns a place can have, as menu items.
    private void FillPatternItems(ItemsControl into, string key)
    {
        Color? under = key == "Base" ? Solid("Base") : key == "Part.Files.Fill" ? Solid("Surface") : null;
        foreach (var kind in Enum.GetValues<StudioPattern>())
            Item(into, StudioPatterns.Label(kind), () => TryChange(() => WithPattern(key, StudioPatterns.Pattern(kind, Solid("Ink"), Solid("Accent"), under, 1, .3))));
        var mine = StudioLibrary.Patterns();
        if (mine.Count > 0)
        {
            into.Items.Add(new Separator());
            foreach (var drawn in mine)
                Item(into, drawn.Name, () => TryChange(() => WithPattern(key, StudioPatterns.Custom(drawn, Solid("Ink"), Solid("Accent"), under, 1, .3))));
        }
        into.Items.Add(new Separator());
        Item(into, "Draw a pattern…", () => DrawPattern(key));
    }

    // A pattern card moved to another part.
    private void MovePatternMenu(FrameworkElement target, ThemeCard card, string from)
    {
        var menu = Menu(target);
        // A picture (not a pattern) only goes on the window or the file area.
        var picture = card.Elements.Any(e => e.Contains("ImageBrush", StringComparison.Ordinal));
        foreach (var (key, label) in PatternPlaces)
        {
            if (key == from || picture && key is not ("Base" or "Part.Files.Fill")) continue;
            var place = key;
            Item(menu, label, () =>
            {
                TryChange(() => ThemeCards.Play(ThemeCards.TakeOut(_document, card), card, place));
                _pickedCard = "Pattern:" + place;
                SyncPanel();
                PaintHand();
            });
        }
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- gradients

    private static readonly string[] GradientDirections = ["Down", "Across", "Diagonal", "From the middle"];

    private Color GradientStart(string key) => Value(key) is GradientBrush { GradientStops.Count: > 0 } g ? g.GradientStops[0].Color : Solid(key);

    private Color GradientEnd(string key)
    {
        if (Value(key) is GradientBrush { GradientStops.Count: > 1 } g) return g.GradientStops[^1].Color;
        var start = Solid(key); var accent = Solid("Accent");
        return StudioUi.Over(Color.FromArgb(0x88, accent.R, accent.G, accent.B), start);
    }

    private static string DirectionOf(GradientBrush brush) => brush switch
    {
        RadialGradientBrush => "From the middle",
        LinearGradientBrush { EndPoint: { X: 0d, Y: > 0d } } => "Down",
        LinearGradientBrush { EndPoint: { X: > 0d, Y: 0d } } => "Across",
        _ => "Diagonal"
    };

    private ThemeStudioDocument WithGradient(string key, Color from, Color to, string direction)
    {
        static XElement Stop(Color color, double offset) => new(ThemeCards.Ns + "GradientStop", new XAttribute("Color", StudioUi.Hex(color)), new XAttribute("Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var brush = direction == "From the middle"
            ? new XElement(ThemeCards.Ns + "RadialGradientBrush", new XAttribute("Center", "0.5,0.5"), new XAttribute("GradientOrigin", "0.5,0.5"),
                new XAttribute("RadiusX", "0.8"), new XAttribute("RadiusY", "0.8"), Stop(from, 0), Stop(to, 1))
            : new XElement(ThemeCards.Ns + "LinearGradientBrush", new XAttribute("StartPoint", "0,0"),
                new XAttribute("EndPoint", direction == "Down" ? "0,1" : direction == "Across" ? "1,0" : "1,1"), Stop(from, 0), Stop(to, 1));
        var next = WithPattern(key, brush);
        // The window's flat colour (used where a gradient cannot be: the title bar, a few small places) follows
        // the gradient's first colour.
        return key == "Base" ? next.Color("Theme.BaseColor", Hex(from)) : next;
    }

    // In a part's box or a pattern card's box: the gradient's colors and direction, or a way to add one.
    private void GradientRows(string key)
    {
        if (Value(key) is not GradientBrush gradient)
        {
            _body.Children.Add(Row("Gradient", ValueText("None"), row =>
            {
                var menu = Menu(row);
                foreach (var direction in GradientDirections) { var way = direction; Item(menu, way, () => TryChange(() => WithGradient(key, GradientStart(key), GradientEnd(key), way))); }
                menu.IsOpen = true;
            }, "Two colors blending into each other"));
            return;
        }
        var direction = DirectionOf(gradient);
        _body.Children.Add(Row(direction == "From the middle" ? "Middle color" : "First color", Dot(new SolidColorBrush(GradientStart(key)), 18), _ => PickGradientColor(key, first: true)));
        _body.Children.Add(Row(direction == "From the middle" ? "Edge color" : "Second color", Dot(new SolidColorBrush(GradientEnd(key)), 18), _ => PickGradientColor(key, first: false)));
        _body.Children.Add(Label("Direction"));
        Choices(GradientDirections.Select(way => (way, way == direction, (Action)(() => TryChange(() => WithGradient(key, GradientStart(key), GradientEnd(key), way))))));
        Pills(Pill("No gradient", () => TryChange(() => SetColor(_document, key, Hex(GradientStart(key)))), tip: "Back to one plain color"));
    }

    private void PickGradientColor(string key, bool first)
    {
        if (Value(key) is not GradientBrush gradient) return;
        var direction = DirectionOf(gradient);
        var now = first ? GradientStart(key) : GradientEnd(key);
        var picker = new StudioColorWindow(_owner, StudioUi.Hex(now), true, null, first ? "Gradient · first color" : "Gradient · second color");
        if (picker.ShowDialog() != true || !TryHex(picker.SelectedColor, out var chosen)) return;
        var start = first ? chosen : GradientStart(key);
        var end = first ? GradientEnd(key) : chosen;
        TryChange(() => WithGradient(key, start, end, direction));
    }

    // ---------------------------------------------------------------- your own icons

    private void StartIconForm()
    {
        _iconForm = true;
        _pickedCard = null;
        if (_selected is not null || _sticker is not null) { _selected = null; _sticker = null; RefreshCanvas(); }
        PaintHand();
        ShowTab("Cards");
        _scroll.ScrollToTop();
    }

    // The box at the top of the Cards tab while an icon is being added.
    private void IconForm()
    {
        Title("Your own icon", "A picture shown instead of the usual icon.");
        _body.Children.Add(Label("For"));
        Choices(new (string, bool, Action)[]
        {
            ("A file type", _iconMatch == "Type", () => { _iconMatch = "Type"; _iconTarget.Text = ".pdf"; SyncPanel(); }),
            ("A file name", _iconMatch == "Name", () => { _iconMatch = "Name"; _iconTarget.Text = "notes.txt"; SyncPanel(); }),
            ("All folders", _iconMatch == "Folder", () => { _iconMatch = "Folder"; SyncPanel(); })
        });
        if (_iconMatch != "Folder")
        {
            _iconTarget.ToolTip = _iconMatch == "Type" ? "A file type, such as .pdf" : "A file name, such as notes.txt, in any folder";
            System.Windows.Automation.AutomationProperties.SetName(_iconTarget, _iconMatch == "Type" ? "File type" : "File name");
            if (_iconTarget.Parent is Decorator old) old.Child = null;   // the box is rebuilt; the text box moves into the new one
            var frame = FileBackupUi.Framed(_iconTarget);
            frame.Margin = new Thickness(0, 6, 0, 4);
            _body.Children.Add(frame);
        }
        Pills(Pill("Choose a picture…", ChooseOwnIcon), Pill("Cancel", () => { _iconForm = false; SyncPanel(); }));
    }

    private void ChooseOwnIcon()
    {
        var target = _iconMatch == "Folder" ? "folder" : _iconTarget.Text.Trim();
        try { ThemeFileIcons.Key(target, _iconMatch != "Name"); }   // a wrong file type or name is said before the picture is chosen
        catch (ArgumentException error) { Report(error.Message, true); return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture for the icon", Filter = "Pictures|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(_owner) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new ArgumentException("Choose a picture smaller than 16 MB.");
            var bytes = File.ReadAllBytes(picker.FileName);
            Change(_document.FileIcon(target, _iconMatch != "Name", bytes));
            _iconForm = false;
            _pickedCard = CardKind.Icons.ToString();   // the icons card, where it now is
            Report(_iconMatch == "Folder" ? "Your folder icon is on the icons card." : $"Your icon for {target} is on the icons card.");
            SyncPanel();
            PaintHand();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or ThemeFormatException or InvalidOperationException)
        { Report("Could not use that icon: " + error.Message, true); }
    }

    // On the icons card: the own icons it holds, each with a way to take it off, and a way to add another.
    private void OwnIconRows()
    {
        var own = (_values ??= SafeResources()).Keys.Cast<object>().OfType<string>().Where(ThemeFileIcons.IsKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (own.Count > 0) _body.Children.Add(Label("Your own icons"));
        foreach (var key in own)
        {
            var what = key == ThemeFileIcons.Prefix + "Folder" ? "All folders"
                : key.StartsWith(ThemeFileIcons.Prefix + "Extension.", StringComparison.Ordinal) ? "." + key[(ThemeFileIcons.Prefix + "Extension.").Length..]
                : key[(ThemeFileIcons.Prefix + "Name.").Length..];
            var picture = new Border { Width = 22, Height = 22, Background = Value(key) as ImageBrush };
            var taken = key;
            _body.Children.Add(Row(what, picture, row =>
            {
                var menu = Menu(row);
                Item(menu, "Take this icon off", () => TryChange(() => _document.Without(taken).DropUnusedImages()));
                menu.IsOpen = true;
            }, "Click to take it off"));
        }
        Pills(Pill("Add your own icon…", StartIconForm));
    }

    // ---------------------------------------------------------------- deck files

    // Saves the deck (all your cards, pictures and fonts included) in a folder chosen in Clearspace's own chooser.
    private void SaveDeckFile()
    {
        var picker = new FolderPickerWindow(new FolderPickerOptions("Save deck file", "Open the folder to save the deck file in, then choose Save here.", "Save here", KnownFolders.Documents))
        { Owner = _owner };
        if (picker.ShowDialog() != true || picker.Chosen is not { } folder) return;
        var name = DeckName;
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "My deck";
        var path = Path.Combine(folder, safe + ThemeDeckFile.Extension);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{safe} ({n}){ThemeDeckFile.Extension}");
        try
        {
            ThemeDeckFile.Write(path, _document, name);
            Report($"Saved {Path.GetFileName(path)}: all your cards, pictures and fonts included. Anyone can import it on their Themes page.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ThemeFormatException or ArgumentException or InvalidOperationException)
        { Report("Could not save the deck file: " + error.Message, true); }
    }

    // Opens a deck file, in Clearspace's own chooser: to borrow from (its cards show under Borrow cards), or, with
    // useWhole, to start from.
    private void OpenDeckFile(bool useWhole)
    {
        var picker = new FolderPickerWindow(new FolderPickerOptions("Open a deck file", "Open the folder the deck file is in, click it, then choose Open.", "Open", KnownFolders.Downloads)
        { FileExtension = ThemeDeckFile.Extension, FileWord = "deck file" })
        { Owner = _owner };
        if (picker.ShowDialog() != true || picker.Chosen is not { } path) return;
        try
        {
            var deck = ThemeDeckFile.Read(path);
            var key = "file:" + path;
            _deckFiles[key] = deck;
            _decks[key] = deck.Cards;
            if (useWhole) { UseWholeDeck(key, deck.Cards); return; }
            _borrowDeck = key;
            ShowTab("Cards");
            Report($"{deck.Name}: {deck.Cards.Count} cards to borrow.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ThemeFormatException)
        { Report(error.Message, true); }
    }

    // Your theme becomes a deck's cards (Undo brings yours back).
    private void UseWholeDeck(string deck, IReadOnlyList<ThemeCard> cards)
    {
        try
        {
            var manifest = _deckFiles.TryGetValue(deck, out var file) ? file.Manifest : _document.Manifest;
            Change(ThemeCards.Build(manifest with { Name = _document.Manifest.Name }, cards));
            Report($"Your theme is now the {DeckLabel(deck)} deck. Undo brings yours back.");
        }
        catch (Exception error) when (error is ThemeFormatException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
        { Report("That deck could not be used: " + error.Message, true); }
    }
}
