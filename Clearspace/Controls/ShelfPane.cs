// Clearspace | NEW (shelf): a place to collect files from anywhere, then do one thing with all of them.
// Drop files on it (from the list, the desktop, another program), or press "Add selected". Go to another folder,
// and "Move here", "Copy here" or "Zip here" deals with everything on the shelf at once. Things can also be
// dragged off the shelf, one at a time or all together ("Drag all"), onto any folder or program.
// The shelf only holds paths: nothing is copied until you say so. It is remembered (settings.json), so it is
// still there after a restart. Something that has since been moved or deleted shows faded, and is left out.
// The work itself (moving, copying, zipping, refreshing the list) is done by the window (MainWindow.Dock.cs).
// Built in code from the theme's colours (DynamicResource keys), so it follows every theme.
//
// CHANGED (shelf look): the shelf was redrawn. What it does is the same; how it looks is new:
//   - Header: the count is a small badge beside the title, and Clear is an icon there (it was a word button
//     at the bottom, next to the things you do with the files, where it was easy to hit by mistake).
//   - Under the header, one quiet line: how many items, their total size, and how many are missing.
//   - Each item is a row with a larger icon, its name, and "folder · size" under it. A missing item says
//     "Missing" in the warning colour. The x to take it off shows on hover (and keyboard focus).
//   - Empty: a dashed drop zone with a short invitation. Dragging files over the shelf lights the zone in
//     the accent colour, whether it is empty or not.
//   - Bottom: "Into <folder>" says where the buttons will put things. Move is the main button (filled
//     with the accent); Copy and Zip sit beside it, all three the same width. Add selected and
//     Drag all are a quieter row under them. With no folder on screen, a line says to open one.
// The buttons' looks are PanelPrimary and PanelAction (App.xaml).
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.Controls;

internal enum ShelfJob { Move, Copy, Zip }

internal sealed class ShelfPane : Grid
{
    private readonly List<string> _paths;
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }; // CHANGED: inside a badge
    private readonly Border _badge;                                                   // NEW: the count's badge
    private readonly Button _clear;                                                   // NEW: Clear, in the header
    private readonly TextBlock _summary = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, -4, 0, 10) }; // NEW
    private readonly Grid _dropZone = new() { AllowDrop = true, Background = Brushes.Transparent };   // CHANGED: was a Border with a solid outline
    private readonly Rectangle _dash = new() { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false }; // NEW
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _actions = new() { Margin = new Thickness(0, 12, 0, 0) }; // CHANGED: was a WrapPanel of equal-looking buttons
    private readonly Func<string?> _here;
    private readonly Action<ShelfJob, IReadOnlyList<string>> _run;
    private readonly Action _addSelected;
    private Point _pressAt;
    private string[]? _pressed;   // what a press on the shelf would drag
    private bool _draggingOut;    // files dragged off the shelf are not taken back by it
    private bool _dragOver;       // NEW: files are being dragged over the shelf right now

    /// <param name="close">Hides the shelf.</param>
    /// <param name="here">The folder on screen, or null when it is not a folder (search results, This PC).</param>
    /// <param name="run">Moves, copies or zips the paths into the folder on screen.</param>
    /// <param name="addSelected">Puts the list's selection on the shelf.</param>
    internal ShelfPane(Action close, Func<string?> here, Action<ShelfJob, IReadOnlyList<string>> run, Action addSelected)
    {
        _here = here; _run = run; _addSelected = addSelected;
        _paths = SettingsService.GetShelfItems().ToList();

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // header
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // NEW: summary line
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });     // the items
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // the buttons

        // ---- header: icon, "Shelf", the count badge, Clear, close
        _count.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _badge = new Border { Child = _count, Padding = new Thickness(7, 1, 7, 1), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _badge.SetResourceReference(Border.BackgroundProperty, "BadgeFill");
        // CHANGED (one x): Clear is the word, not a symbol. Its symbol drew as a second x right beside the
        // close button in some icon fonts, and a bin would look like it deletes the files themselves.
        var clearWord = new TextBlock { Text = "Clear", FontSize = 11.5 };
        clearWord.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont"); // the button's own font is the icon font
        _clear = new Button { Content = clearWord, Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Take everything off the shelf (the files themselves stay where they are)" };
        _clear.SetResourceReference(StyleProperty, "LabelButton");
        _clear.SetResourceReference(Control.ForegroundProperty, "InkMuted"); // the shelf is a Grid, which has no Foreground of its own
        AutomationProperties.SetName(_clear, "Clear the shelf");
        _clear.Click += (_, _) => Clear();
        Children.Add(DockUi.Heading("", "Shelf", "Hide the shelf  ·  Alt+S", close, _badge, _clear));

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        SetRow(_summary, 1);
        Children.Add(_summary);

        // ---- the items, in a zone that lights up when files are dragged over it
        _scroller = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        _dropZone.Children.Add(_dash);
        _dropZone.Children.Add(_scroller);
        _dropZone.DragEnter += (_, e) => { e.Effects = Effects(e); SetDragOver(e.Effects != DragDropEffects.None); e.Handled = true; };
        _dropZone.DragOver += (_, e) => { e.Effects = Effects(e); e.Handled = true; };
        _dropZone.DragLeave += (_, _) => SetDragOver(false);
        _dropZone.Drop += (_, e) =>
        {
            SetDragOver(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) Add(dropped);
            e.Effects = DragDropEffects.Link;   // the files stay where they are; the shelf only notes them
            e.Handled = true;
        };
        SetRow(_dropZone, 2);
        Children.Add(_dropZone);

        SetRow(_actions, 3);
        Children.Add(_actions);
        PreviewMouseLeftButtonUp += (_, _) => _pressed = null;
        Rebuild();
    }

    // The shelf takes files (never things from itself, which would only be added twice).
    private DragDropEffects Effects(DragEventArgs e) => !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;

    // ---------------------------------------------------------------- what is on it

    /// <summary>What is on the shelf, in order.</summary>
    internal IReadOnlyList<string> Paths => _paths;

    /// <summary>Puts paths on the shelf (each once).</summary>
    internal void Add(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || ArchiveLocation.Is(path)) continue;
            if (_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _paths.Add(path);
            added = true;
        }
        if (added) Changed();
    }

    /// <summary>After a move: each old path that is now somewhere else becomes its new path.</summary>
    internal void Moved(IReadOnlyDictionary<string, string> moves)
    {
        for (var i = 0; i < _paths.Count; i++)
            if (moves.TryGetValue(_paths[i], out var to)) _paths[i] = to;
        Changed();
    }

    internal void Remove(string path)
    {
        if (_paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Changed();
    }

    internal void Clear()
    {
        if (_paths.Count == 0) return;
        _paths.Clear();
        Changed();
    }

    /// <summary>Draws it again (things may have been moved or deleted since; the folder on screen may have changed).</summary>
    internal void Refresh() => Rebuild();

    private void Changed()
    {
        SettingsService.SetShelfItems(_paths);
        Rebuild();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private IReadOnlyList<string> Present() => _paths.Where(Exists).ToList();

    private static string Leaf(string path)
        => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } leaf ? leaf : path;

    // ---------------------------------------------------------------- drawing it

    // NEW: the zone's outline. Dashed and quiet when the shelf is empty (a place to drop things); hidden when it
    // holds items; dashed in the accent colour, over a faint wash, while files are dragged over it.
    private void SetDragOver(bool over)
    {
        _dragOver = over;
        if (over)
        {
            _dash.SetResourceReference(Shape.StrokeProperty, "Accent");
            _dash.SetResourceReference(Shape.FillProperty, "AccentWash");
            _dash.Visibility = Visibility.Visible;
        }
        else
        {
            _dash.SetResourceReference(Shape.StrokeProperty, "EdgeRaised");
            _dash.Fill = Brushes.Transparent;
            _dash.Visibility = _paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var present = Present();
        var missing = _paths.Count - present.Count;

        // ---- header and summary
        _count.Text = _paths.Count.ToString("N0");
        _badge.Visibility = _clear.Visibility = _paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_paths.Count == 0)
        {
            _summary.Visibility = Visibility.Collapsed;
        }
        else
        {
            var size = present.Where(File.Exists).Sum(path => { try { return new FileInfo(path).Length; } catch (Exception) { return 0L; } });
            var words = $"{_paths.Count:N0} {(_paths.Count == 1 ? "item" : "items")}";
            if (size > 0) words += $"  ·  {FileSystemItem.FormatSize(size)}";
            if (missing > 0) words += $"  ·  {missing:N0} missing";
            _summary.Text = words;
            _summary.Visibility = Visibility.Visible;
        }

        // ---- the items, or the empty drop zone
        if (_paths.Count == 0)
        {
            _list.Children.Add(EmptyZone());
            _list.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            _list.VerticalAlignment = VerticalAlignment.Top;
            foreach (var path in _paths) _list.Children.Add(Row(path));
        }
        SetDragOver(_dragOver);

        // ---- the buttons
        _actions.Children.Clear();
        var here = _here();
        var any = present.Count > 0;

        if (any && here is not null)
        {
            var into = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
            var folderGlyph = new TextBlock { Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            folderGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            folderGlyph.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");
            var intoWords = DockUi.Text($"Into {Leaf(here)}", "InkMuted", 11.5);
            intoWords.TextWrapping = TextWrapping.NoWrap;
            intoWords.TextTrimming = TextTrimming.CharacterEllipsis;
            into.Children.Add(folderGlyph);
            into.Children.Add(intoWords);
            into.ToolTip = here;
            _actions.Children.Add(into);

            // CHANGED (shelf fit): "Move", "Copy", "Zip" - "Into <folder>" above already says where. With "here"
            // on each, three side by side did not fit a narrow shelf and were cut off.
            _actions.Children.Add(EvenRow(
                PanelButton("", "Move", $"Move everything on the shelf into {Leaf(here)}", () => _run(ShelfJob.Move, Present()), primary: true),
                PanelButton("", "Copy", $"Copy everything on the shelf into {Leaf(here)}", () => _run(ShelfJob.Copy, Present())),
                PanelButton("", "Zip", $"Put everything on the shelf in one ZIP file in {Leaf(here)}", () => _run(ShelfJob.Zip, Present()))));
        }
        else if (any)
        {
            var hint = DockUi.Text("Open a folder to move, copy or zip these into it.", "InkFaint", 11.5);
            hint.Margin = new Thickness(2, 0, 0, 6);
            _actions.Children.Add(hint);
        }

        // The quieter row: add more, or take them all somewhere by dragging.
        var add = DockUi.TextButton("", "Add selected", "Put the selected files on the shelf", _addSelected);
        if (any)
        {
            var all = DockUi.TextButton("", "Drag all", "Drag everything on the shelf onto a folder or a program", () => { });
            all.Cursor = Cursors.SizeAll;
            all.PreviewMouseLeftButtonDown += (_, e) => { _pressAt = e.GetPosition(this); _pressed = Present().ToArray(); };
            all.PreviewMouseMove += DragOut;
            _actions.Children.Add(EvenRow(add, all));
        }
        else
        {
            _actions.Children.Add(EvenRow(add));
        }
    }

    // NEW: buttons side by side, each the same width, filling the row.
    private static Grid EvenRow(params Button[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = buttons[i];
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(i == 0 ? 0 : 3, 0, i == buttons.Length - 1 ? 0 : 3, 0);
            SetColumn(button, i);
            row.Children.Add(button);
        }
        return row;
    }

    // NEW: a symbol and a word, in the shelf's own button look (App.xaml: PanelPrimary, PanelAction).
    private static Button PanelButton(string glyph, string text, string tip, Action click, bool primary = false)
    {
        var symbol = new TextBlock { Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var words = new TextBlock { Text = text, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        words.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(symbol);
        content.Children.Add(words);

        var button = new Button { Content = content, ToolTip = tip };
        button.SetResourceReference(StyleProperty, primary ? "PanelPrimary" : "PanelAction");
        button.Padding = new Thickness(6, 0, 6, 0); // CHANGED (shelf fit): was 10 a side (PanelAction's own)

        // NEW (shelf fit): when the shelf is too narrow for the symbol and the word, the symbol goes, so the
        // word is never cut off.
        button.SizeChanged += (_, _) =>
        {
            words.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            symbol.Visibility = button.ActualWidth - 14 >= words.DesiredSize.Width + 20 ? Visibility.Visible : Visibility.Collapsed;
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    // NEW: what an empty shelf shows inside its dashed outline.
    private static StackPanel EmptyZone()
    {
        var symbol = new TextBlock { Text = "", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
        symbol.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "InkFaint");

        var title = DockUi.Text("Drop files here", "Ink", 13);
        title.FontWeight = FontWeights.SemiBold;
        title.TextAlignment = TextAlignment.Center;
        title.Margin = new Thickness(0, 10, 0, 4);

        var line = DockUi.Text("Collect them from anywhere, then move, copy or zip them all at once.", "InkMuted", 11.5);
        line.TextAlignment = TextAlignment.Center;

        var stack = new StackPanel { Margin = new Thickness(18, 28, 18, 28) };
        stack.Children.Add(symbol);
        stack.Children.Add(title);
        stack.Children.Add(line);
        return stack;
    }

    // CHANGED: a larger icon, the name, and "folder · size" under it; "Missing" when it is gone.
    private FrameworkElement Row(string path)
    {
        var there = Exists(path);
        var name = Leaf(path);
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path)) ?? "";
        var item = there ? FileSystemItem.FromLocation(path) : null;

        var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        if (item is not null) icon.Source = IconService.GetLargeIcon(item) ?? IconService.GetIcon(item);
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(icon, Dock.Left);

        var remove = DockUi.IconButton("", "Take it off the shelf", () => Remove(path));
        remove.Width = remove.Height = 24;
        remove.FontSize = 9;
        remove.Opacity = 0;
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.GotKeyboardFocus += (_, _) => remove.Opacity = 1;
        remove.LostKeyboardFocus += (_, _) => remove.Opacity = 0;
        DockPanel.SetDock(remove, Dock.Right);

        var title = DockUi.Text(name, there ? "Ink" : "InkFaint", 12.5);
        title.FontWeight = FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;

        var detail = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        if (!there)
        {
            var tag = DockUi.Text("Missing", "Warn", 11);
            tag.FontWeight = FontWeights.SemiBold;
            tag.Margin = new Thickness(0, 0, 6, 0);
            detail.Children.Add(tag);
        }
        var where = DockUi.Text(Leaf(folder) + (item is { IsFolder: false } ? $"  ·  {FileSystemItem.FormatSize(item.Size)}" : ""), "InkMuted", 11);
        where.TextWrapping = TextWrapping.NoWrap;
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Children.Add(where);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(title);
        words.Children.Add(detail);

        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(icon);
        row.Children.Add(remove);
        row.Children.Add(words);

        var box = new Border { Child = row, Padding = new Thickness(8, 6, 4, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, ToolTip = path, Opacity = there ? 1 : .7 };
        box.MouseEnter += (_, _) => { box.SetResourceReference(Border.BackgroundProperty, "Hover"); remove.Opacity = 1; };
        box.MouseLeave += (_, _) => { box.Background = Brushes.Transparent; if (!remove.IsKeyboardFocused) remove.Opacity = 0; };
        if (there)
        {
            box.Cursor = Cursors.Hand;
            box.PreviewMouseLeftButtonDown += (_, e) => { _pressAt = e.GetPosition(this); _pressed = remove.IsMouseOver ? null : [path]; };
            box.PreviewMouseMove += DragOut;
        }
        return box;
    }

    // A press that moves far enough drags the files out, as a drag from the list does.
    private void DragOut(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { Length: > 0 } paths) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressed = null;
        _draggingOut = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }   // the other program refused it part-way
        finally { _draggingOut = false; }
        Rebuild();   // a move elsewhere leaves some of them gone
    }
}
