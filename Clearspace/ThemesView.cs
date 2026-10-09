// Clearspace | NEW (themes page): the "Themes" page.
// Opens over the file list from Settings > "Choose a theme…", like Backup and restore and Find duplicates.
// The Settings menu used to list every theme by name, six in the menu itself and the rest in two submenus;
// with eighteen themes and more to import, a name said little about what you would get. The page shows
// each theme as a small window drawn in its own colours, fonts and corners (Controls/ThemePreview.cs):
//     Your themes (the ones that were imported or put in the Themes folder)
//     Clearspace's own themes
//     Experimental (the ones that also change icons or the layout)
// Click a theme to use it; the one in use has the accent-coloured outline and a tick. "Import…" adds a
// theme file, "Open themes folder" shows the folder they are all in. A theme's right-click menu (and the
// bin that appears on its card under the pointer) removes it: its file goes to the Recycle Bin, so it can
// be put back. Dark has neither: it is part of Clearspace, so there is always one theme left.
//
// The page lists the folder as it is (ThemeService.Themes) and follows it while it is open: a theme file
// dropped into the folder appears, and one saved again is drawn again. The cards go up at once and their
// previews are filled in one at a time while the app is idle, because reading a theme means opening its
// file. A theme that cannot be read still has a card, saying so, and can still be removed.
// Built in code from the shared styles in App.xaml and the theme's colours, so it follows every theme.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Controls;
using Clearspace.Services;

namespace Clearspace;

internal sealed class ThemesView : UserControl, IDisposable
{
    private const double CardPadding = 10;

    private readonly StackPanel _sections = new();
    private readonly Dictionary<string, bool> _expandedSections = new();
    private readonly TextBlock _status;
    private readonly List<Card> _cards = [];
    private readonly Queue<Card> _waiting = new();

    // What has been read for a preview already, by theme file and the state it was in, so listing the
    // folder again does not open every theme again.
    private readonly Dictionary<string, (ResourceDictionary? Resources, string? Problem)> _read = new(StringComparer.OrdinalIgnoreCase);

    private bool _filling;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    /// <summary>"Import…": the window asks for the file (its chooser needs the sidebar's places).</summary>
    public event EventHandler? ImportRequested;

    /// <summary>"Open themes folder": the window closes this page and goes there.</summary>
    public event EventHandler? FolderRequested;

    /// <summary>"Show its file": the window closes this page and selects the theme's file in the Themes folder.</summary>
    public event EventHandler<string>? ShowFileRequested;

    // One theme on the page.
    private sealed class Card(ThemeInfo theme, Button button, Border frame, Border hover, Border stage, TextBlock name, TextBlock tick, Button? remove)
    {
        public ThemeInfo Theme => theme;
        public Button Button => button;
        public Border Frame => frame;      // the outline: accent-coloured for the theme in use
        public Border Hover => hover;      // the wash that shows under the pointer
        public Border Stage => stage;      // where the preview goes
        public TextBlock Name => name;
        public TextBlock Tick => tick;
        public Button? Remove => remove;
    }

    internal ThemesView()
    {
        UseLayoutRounding = true;
        FontSize = 13;
        Focusable = true;
        SetResourceReference(BackgroundProperty, "Base");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UIFont");

        // ---- header: Back, the title, Open themes folder, Import
        var back = FileBackupUi.TextButton("Back", primary: false);
        back.Margin = new Thickness(0, 0, 18, 0);
        back.VerticalAlignment = VerticalAlignment.Top;
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(back, Dock.Left);

        var create = FileBackupUi.TextButton("Create a theme…", primary: true);
        create.VerticalAlignment = VerticalAlignment.Top;
        create.Click += (_, _) => OpenStudio(null);
        DockPanel.SetDock(create, Dock.Right);

        var import = FileBackupUi.TextButton("Import…", primary: false);
        import.VerticalAlignment = VerticalAlignment.Top;
        import.ToolTip = "Add a theme file somebody made";
        import.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(import, Dock.Right);

        var folder = FileBackupUi.TextButton("Open themes folder", primary: false);
        folder.VerticalAlignment = VerticalAlignment.Top;
        folder.Click += (_, _) => FolderRequested?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(folder, Dock.Right);

        var title = new TextBlock { Text = "Themes", FontSize = 26, FontWeight = FontWeights.SemiBold };
        var about = FileBackupUi.Muted("Click to use. Right-click to edit your themes or customize a copy.");
        about.Margin = new Thickness(0, 5, 0, 0);
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(about);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(back);
        header.Children.Add(create);
        header.Children.Add(import);
        header.Children.Add(folder);
        header.Children.Add(titles);

        // ---- the themes
        var scroll = new ScrollViewer
        {
            Content = _sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        // ---- what just happened (a theme removed, or why one cannot be used)
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };

        var page = new Grid { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 1040 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 1);
        Grid.SetRow(_status, 2);
        page.Children.Add(header);
        page.Children.Add(scroll);
        page.Children.Add(_status);
        Content = page;

        ThemeService.Changed += MarkCurrent;
        ThemeService.ListChanged += Rebuild;
        Loaded += (_, _) =>
        {
            if (!_disposed)
                Focus();
        };

        ThemeService.Refresh(); // the folder as it is now
        Rebuild();
    }

    public void Dispose()
    {
        _disposed = true;
        ThemeService.Changed -= MarkCurrent;
        ThemeService.ListChanged -= Rebuild;
        _waiting.Clear();
    }

    /// <summary>Puts a sentence under the themes; null takes it away.</summary>
    internal void Report(string? message, bool problem = false)
    {
        _status.Text = message ?? string.Empty;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "InkMuted");
    }

    // ---------------------------------------------------------------- the list

    // Lays the page out again from the list of themes: on opening, and whenever the list changes.
    private void Rebuild()
    {
        if (_disposed)
            return;

        _sections.Children.Clear();
        _cards.Clear();
        _waiting.Clear();

        var themes = ThemeService.Themes;
        var yours = themes.Where(theme => !theme.Shipped).ToList();
        AddSection("Your themes", yours.Count == 0 ? "Make something yours with Create a theme, or import one." : null, yours, evenWhenEmpty: true);
        AddSection("Clearspace themes", null, themes.Where(theme => theme.Shipped && !theme.Experimental));
        AddSection("Experimental themes", "These change the icons or the layout too.", themes.Where(theme => theme.Shipped && theme.Experimental));

        // Forget what was read for themes that have gone or changed.
        var keep = _cards.Select(card => Key(card.Theme)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _read.Keys.Where(key => !keep.Contains(key)).ToArray())
            _read.Remove(key);

        MarkCurrent();
        FillLater();
    }

    private void AddSection(string heading, string? note, IEnumerable<ThemeInfo> themes, bool evenWhenEmpty = false)
    {
        var listed = themes.ToList();
        if (listed.Count == 0 && !evenWhenEmpty)
            return;

        var content = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var title = new TextBlock
        {
            Text = $"{heading} ({listed.Count})",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var section = new Expander
        {
            Header = title, Content = content,
            IsExpanded = _expandedSections.GetValueOrDefault(heading, true),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 14, 0, 0)
        };
        section.SetResourceReference(ForegroundProperty, "Ink");
        System.Windows.Automation.AutomationProperties.SetName(section, heading);
        section.Expanded += (_, _) => _expandedSections[heading] = true;
        section.Collapsed += (_, _) => _expandedSections[heading] = false;
        _sections.Children.Add(section);

        if (note is not null)
        {
            var line = FileBackupUi.Muted(note);
            line.Margin = new Thickness(0, 0, 0, 12);
            content.Children.Add(line);
        }

        if (listed.Count == 0)
            return;

        var cards = new WrapPanel();
        foreach (var theme in listed)
        {
            var card = MakeCard(theme);
            _cards.Add(card);
            _waiting.Enqueue(card);
            cards.Children.Add(card.Button);
        }
        content.Children.Add(cards);
    }

    // A theme's card: its preview, its name, a tick when it is the one in use, and a bin when it can be removed.
    private Card MakeCard(ThemeInfo theme)
    {
        var stage = new Border { Width = ThemePreview.PreviewWidth, Height = ThemePreview.PreviewHeight };
        stage.SetResourceReference(Border.BackgroundProperty, "Tint");
        stage.SetResourceReference(Border.CornerRadiusProperty, "R8");

        var name = new TextBlock
        {
            Text = theme.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = ThemePreview.PreviewWidth - 56 // room for the tick and the bin beside it
        };

        var tick = new TextBlock
        {
            Text = "\uE73E", // CheckMark
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        tick.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        tick.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        Grid.SetColumn(tick, 1);

        Button? remove = null;
        if (ThemeService.CanRemove(theme))
        {
            remove = FileBackupUi.IconButton("\uE74D", "Remove"); // Delete
            remove.Width = 24;
            remove.Height = 24;
            remove.FontSize = 12;
            remove.Margin = new Thickness(4, 0, 0, 0);
            remove.Opacity = 0; // shown while the pointer or the keyboard is on the card
            remove.Click += (_, _) => Remove(theme);
            Grid.SetColumn(remove, 3);
        }

        // The name, the tick straight after it, and the bin at the far right.
        var caption = new Grid { Height = 24, Margin = new Thickness(2, 8, 0, 0) };
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.Children.Add(name);
        caption.Children.Add(tick);
        if (remove is not null)
            caption.Children.Add(remove);

        var content = new StackPanel { Margin = new Thickness(CardPadding - 2) };
        content.Children.Add(stage);
        content.Children.Add(caption);

        var hover = new Border { Opacity = 0, IsHitTestVisible = false };
        hover.SetResourceReference(Border.BackgroundProperty, "Hover");
        hover.SetResourceReference(Border.CornerRadiusProperty, "R10");

        var layers = new Grid();
        layers.Children.Add(hover);
        layers.Children.Add(content);

        var frame = new Border { BorderThickness = new Thickness(2), Child = layers };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        frame.SetResourceReference(Border.CornerRadiusProperty, "R12");

        // A button, so the keyboard reaches it too (Tab, then Enter or Space), drawn entirely by the card.
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        var button = new Button
        {
            Template = template,
            Content = frame,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = Tip(theme)
        };
        button.SetResourceReference(ForegroundProperty, "Ink"); // a Button brings its own text colour; the name is the page's
        ToolTipService.SetInitialShowDelay(button, 700);
        System.Windows.Automation.AutomationProperties.SetName(button, theme.Label);

        var card = new Card(theme, button, frame, hover, stage, name, tick, remove);
        button.Click += (_, _) => Pick(theme);
        button.MouseEnter += (_, _) => Paint(card);
        button.MouseLeave += (_, _) => Paint(card);
        button.IsKeyboardFocusWithinChanged += (_, _) => Paint(card);
        button.ContextMenu = MenuFor(theme);
        return card;
    }

    // What a theme's theme.json says about it, or why it cannot be used.
    private static string? Tip(ThemeInfo theme)
    {
        if (theme.Problem is { } problem)
            return problem;

        var about = theme.Description;
        if (!theme.Shipped && theme.Author.Length > 0)
            about = about.Length > 0 ? $"{about}\nBy {theme.Author}" : $"By {theme.Author}";
        return about.Length > 0 ? about : null;
    }

    // Only what applies: no "Use this theme" on the one in use or on one that cannot be read, nothing
    // about a file for the built-in Dark, and no "Remove" on Dark. No entries, no menu.
    private ContextMenu? MenuFor(ThemeInfo theme)
    {
        var menu = new ContextMenu();

        if (theme.Problem is null && !theme.BuiltIn)
        {
            var edit = new MenuItem { Header = theme.Shipped ? "Customize a copy…" : "Edit theme…" };
            edit.Click += (_, _) => OpenStudio(theme);
            menu.Items.Add(edit);
        }

        if (theme.Problem is null && !IsCurrent(theme))
        {
            var use = new MenuItem { Header = "Use this theme" };
            use.Click += (_, _) => Pick(theme);
            menu.Items.Add(use);
        }

        if (!theme.BuiltIn)
        {
            var show = new MenuItem { Header = "Show its file" };
            show.Click += (_, _) => ShowFileRequested?.Invoke(this, theme.Path);
            menu.Items.Add(show);
        }

        if (ThemeService.CanRemove(theme))
        {
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += (_, _) => Remove(theme);
            menu.Items.Add(remove);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    private void OpenStudio(ThemeInfo? theme)
    {
        try
        {
            var studio = new ThemeStudioWindow(theme) { Owner = Window.GetWindow(this) };
            if (studio.ShowDialog() == true)
            {
                Rebuild();
                Report($"{studio.SavedName} is saved and now in use.");
            }
        }
        catch (Exception error) when (error is ThemeFormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            Report("This theme could not be opened: " + error.Message, true);
        }
    }

    private static bool IsCurrent(ThemeInfo theme) =>
        string.Equals(theme.Name, ThemeService.Current, StringComparison.OrdinalIgnoreCase);

    // The theme in use gets the outline and the tick. Also called when the theme changes.
    private void MarkCurrent()
    {
        if (_disposed)
            return;

        foreach (var card in _cards)
        {
            Paint(card);
            card.Button.ContextMenu = MenuFor(card.Theme); // "Use this theme" comes and goes with it
        }
    }

    private static void Paint(Card card)
    {
        var current = IsCurrent(card.Theme);
        var reached = card.Button.IsMouseOver || card.Button.IsKeyboardFocusWithin;

        card.Tick.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
        card.Name.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        card.Hover.Opacity = reached ? 1 : 0;
        if (card.Remove is { } remove)
            remove.Opacity = reached ? 1 : 0;

        if (current)
            card.Frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        else if (card.Button.IsKeyboardFocusWithin)
            card.Frame.SetResourceReference(Border.BorderBrushProperty, "InkFaint");
        else
            card.Frame.BorderBrush = Brushes.Transparent;
    }

    // ---------------------------------------------------------------- previews

    private static string Key(ThemeInfo theme) => $"{theme.Name}|{theme.Path}|{theme.Stamp}";

    // One preview each time the app has nothing else to do, so the page opens at once and stays smooth.
    private void FillLater()
    {
        if (_filling || _waiting.Count == 0)
            return;

        _filling = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FillNext));
    }

    private void FillNext()
    {
        _filling = false;
        if (_disposed)
            return;

        // A theme that was read before is drawn straight away; the first one that has to be read ends this turn.
        while (_waiting.Count > 0)
        {
            var card = _waiting.Dequeue();
            if (!_cards.Contains(card))
                continue; // the list was laid out again since

            var key = Key(card.Theme);
            var known = _read.TryGetValue(key, out var read);
            if (!known)
            {
                var resources = ThemeService.PreviewResources(card.Theme, out var problem);
                _read[key] = read = (resources, problem);
            }

            card.Stage.Child = read.Resources is { } theme
                ? new ThemePreview(theme, card.Theme.CrispText)
                : Unreadable();
            if (read.Resources is null && read.Problem is { } why)
                card.Button.ToolTip = why;

            if (!known)
                break;
        }

        FillLater();
    }

    // In place of the preview of a theme that cannot be read.
    private static UIElement Unreadable()
    {
        var symbol = new TextBlock { Text = "", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center }; // Warning
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");

        var words = FileBackupUi.Muted("Can't be read");
        words.FontSize = 11.5;
        words.Margin = new Thickness(0, 6, 0, 0);
        words.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(symbol);
        stack.Children.Add(words);
        return stack;
    }

    // ---------------------------------------------------------------- actions

    private void Pick(ThemeInfo theme)
    {
        if (theme.Problem is null && ThemeService.Apply(theme.Name))
        {
            Report(null);
            return;
        }

        Report($"{theme.Label} can't be used. {theme.Problem ?? ThemeService.LastProblem ?? "The theme could not be read."}", problem: true);
    }

    // The theme's file goes to the Recycle Bin. When it was the theme in use, Dark takes over.
    private void Remove(ThemeInfo theme)
    {
        var owner = Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        if (ThemeService.Remove(theme.Name, owner))
            Report($"Removed {theme.Label}. It is in the Recycle Bin if you want it back.");
        else if (ThemeService.LastProblem is { } problem)
            Report(problem, problem: true);
    }
}
