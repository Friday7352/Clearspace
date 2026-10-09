using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace.Controls;

// NEW (custom layouts): another file list, for layouts with more than one (side by side, a dashboard, four at
// once). The window's main list keeps every feature; a pane is lighter, made for looking and moving files:
//   - Back, Up, Refresh and an address box (type a path and press Enter); it starts on the folder the layout
//     names ("Downloads", "Desktop", "Documents", "Pictures", "Music", "Videos", "Home", "This PC", or a path).
//   - Double-click or Enter opens a folder in the pane, or a file with its program; Backspace goes up.
//   - Drag files between panes, to and from the main list, and to and from File Explorer: a move on the same
//     drive, a copy to another one (hold Ctrl to copy, Shift to move). Delete sends them to the Recycle Bin.
//   - Ctrl+C / Ctrl+X / Ctrl+V use the Windows clipboard, so they work with the main list too.
//   - Right-click: open, open in the main list, copy or move to the main list's folder, delete, properties.
//   - It watches its folder and refreshes when files change.
internal sealed class FilePane : Border, IDisposable
{
    private readonly ListView _list = new() { BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Extended };
    private readonly TextBox _address = new();
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _count = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
    private readonly NavigationHistory _history = new();
    private readonly FolderWatcher _watcher;
    private readonly Func<string?> _mainFolder;
    private readonly Action<string> _openInMain;
    private readonly bool _live;
    private CancellationTokenSource? _loading;
    private Point _dragStart;
    private string _folder = string.Empty;   // "" is This PC

    internal string Folder => _folder;

    /// <param name="start">Where it opens: a known folder name, a path, or empty for the main list's folder.</param>
    /// <param name="mainFolder">The main list's folder, for "Copy to the main list".</param>
    /// <param name="openInMain">Opens a folder in the main list.</param>
    /// <param name="live">False in the theme studio's preview: the pane lists its folder but does not watch it.</param>
    internal FilePane(string? start, string? title, Func<string?> mainFolder, Action<string> openInMain, bool live = true)
    {
        _mainFolder = mainFolder; _openInMain = openInMain; _live = live;
        _watcher = new FolderWatcher(() => Load(keepSelection: true));
        SetResourceReference(BackgroundProperty, "Part.Files.Fill");
        SetResourceReference(BorderBrushProperty, "Part.Files.Edge");
        SetResourceReference(BorderThicknessProperty, "Part.Files.EdgeWidth");
        SetResourceReference(CornerRadiusProperty, "Part.Files.Corner");
        ClipToBounds = true;

        var layout = new DockPanel();
        Child = layout;

        // Header: title, back, up, refresh, address.
        var header = new DockPanel { Margin = new Thickness(10, 8, 10, 6), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, tip, act) in new (string, string, Action)[]
        {
            ("", "Back  ·  Alt+Left", Back), ("", "Up  ·  Backspace", Up), ("", "Refresh  ·  F5", () => Load(keepSelection: true))
        })
        {
            var button = new Button { Content = glyph, ToolTip = tip, Width = 28, Height = 28 };
            button.SetResourceReference(StyleProperty, "IconButton");
            AutomationProperties.SetName(button, tip.Split(' ')[0]);
            button.Click += (_, _) => act();
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Left); header.Children.Add(buttons);
        _title.Margin = new Thickness(8, 0, 10, 0);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");
        DockPanel.SetDock(_title, Dock.Left); header.Children.Add(_title);
        _address.SetResourceReference(StyleProperty, "PlainTextBox");
        _address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Go(_address.Text.Trim()); _list.Focus(); } };
        AutomationProperties.SetName(_address, "Pane address");
        var addressFrame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 3, 8, 3), Child = _address, Height = 28 };
        addressFrame.SetResourceReference(Border.BackgroundProperty, "Base");
        addressFrame.SetResourceReference(Border.BorderBrushProperty, "EdgeSunken");
        addressFrame.SetResourceReference(Border.CornerRadiusProperty, "R8");
        header.Children.Add(addressFrame);
        layout.Children.Add(header);

        // Footer: how many items.
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6, 5, 6, 5), Child = _count };
        footer.SetResourceReference(Border.BorderBrushProperty, "Part.Status.Edge");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);

        // The list.
        _list.SetResourceReference(BackgroundProperty, "ListFill");
        _list.SetResourceReference(Control.ForegroundProperty, "Ink");
        _list.SetResourceReference(Control.FontFamilyProperty, "SerifFont");
        _list.FontSize = 12.5;
        _list.ItemContainerStyle = RowStyle();
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Name", Width = 260, CellTemplate = NameCell() });
        view.Columns.Add(new GridViewColumn { Header = "Date modified", Width = 140, DisplayMemberBinding = new Binding(nameof(FileSystemItem.DateModifiedText)) });
        view.Columns.Add(new GridViewColumn { Header = "Size", Width = 80, DisplayMemberBinding = new Binding(nameof(FileSystemItem.SizeText)) });
        _list.View = view;
        if (Application.Current?.TryFindResource("ColumnHeader") is Style headerStyle) view.ColumnHeaderContainerStyle = headerStyle;
        _list.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListViewItem) OpenSelected(); };
        _list.KeyDown += OnKey;
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        _list.PreviewMouseMove += OnDragOut;
        _list.AllowDrop = true;
        _list.DragOver += OnDragOver;
        _list.Drop += OnDrop;
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, _) => BuildMenu();
        layout.Children.Add(_list);

        Unloaded += (_, _) => _watcher.Watch(null);
        Loaded += (_, _) => { if (_live && _folder.Length > 0) _watcher.Watch(_folder); };
        if (!string.IsNullOrWhiteSpace(title)) _title.Text = title;
        Go(Resolve(start) ?? mainFolder() ?? KnownFolders.Profile);
    }

    // "Downloads", "Desktop", "This PC" ... or a path. Null for empty (the caller uses the main list's folder).
    internal static string? Resolve(string? start) => start?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "downloads" => KnownFolders.Downloads, "desktop" => KnownFolders.Desktop, "documents" => KnownFolders.Documents,
        "pictures" => KnownFolders.Pictures, "music" => KnownFolders.Music, "videos" => KnownFolders.Videos,
        "home" or "profile" => KnownFolders.Profile, "this pc" or "computer" => string.Empty,
        _ => Environment.ExpandEnvironmentVariables(start.Trim())
    };

    private static Style RowStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 3, 4, 3)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Hover")));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Selected")));
        style.Triggers.Add(hover); style.Triggers.Add(selected);
        // A plain template, so the theme's colours show (the stock one paints its own blue selection).
        var template = new ControlTemplate(typeof(ListViewItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetResourceReference(Border.CornerRadiusProperty, "R6");
        var presenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
        presenter.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(GridViewRowPresenter.ColumnsProperty, new TemplateBindingExtension(GridView.ColumnCollectionProperty));
        border.AppendChild(presenter);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static DataTemplate NameCell()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 16.0); icon.SetValue(FrameworkElement.HeightProperty, 16.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(FileSystemItem.Icon)));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileSystemItem.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(icon); panel.AppendChild(name);
        return new DataTemplate { VisualTree = panel };
    }

    // ---------------------------------------------------------------- moving around

    private void Go(string path, bool remember = true)
    {
        path = path.Trim();
        if (path.Length > 0 && !Directory.Exists(path))
        {
            _count.Text = "That folder could not be found.";
            _address.Text = _folder;
            return;
        }
        if (remember && !string.Equals(path, _history.CurrentPath, StringComparison.OrdinalIgnoreCase)) _history.Push(path);
        _folder = path;
        _address.Text = path.Length == 0 ? "This PC" : path;
        if (string.IsNullOrEmpty(_title.Text) || _title.Tag is true)
        {
            _title.Text = path.Length == 0 ? "This PC" : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } leaf ? leaf : path;
            _title.Tag = true; // the title follows the folder (a layout's own title does not)
        }
        if (_live && IsLoaded) _watcher.Watch(path.Length == 0 ? null : path);
        Load(keepSelection: false);
    }

    private void Back()
    {
        if (_history.Index <= 0) return;
        _history.Index--;
        Go(_history.CurrentPath ?? string.Empty, remember: false);
    }

    private void Up()
    {
        if (_folder.Length == 0) return;
        Go(Directory.GetParent(Path.TrimEndingDirectorySeparator(_folder))?.FullName ?? string.Empty);
    }

    private async void Load(bool keepSelection)
    {
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        var folder = _folder;
        var keep = keepSelection ? _list.SelectedItems.Cast<FileSystemItem>().Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        try
        {
            var items = await Task.Run(() =>
            {
                List<FileSystemItem> list;
                if (folder.Length == 0)
                    list = DriveInfo.GetDrives().Where(d => d.IsReady).Select(FileSystemItem.FromDrive).ToList();
                else
                {
                    list = DirectoryEnumerator.Enumerate(folder, false, cancel.Token).ToList();
                    list.Sort(new ItemComparer(SortColumn.Name, false, true));
                }
                IconService.Populate(list);
                return list;
            }, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _list.ItemsSource = items;
            if (keep is { Count: > 0 })
                foreach (var item in items.Where(i => keep.Contains(i.FullPath))) _list.SelectedItems.Add(item);
            var folders = items.Count(i => i.IsFolder);
            _count.Text = $"{folders} folder{(folders == 1 ? "" : "s")}, {items.Count - folders} file{(items.Count - folders == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _list.ItemsSource = Array.Empty<FileSystemItem>();
            _count.Text = "This folder could not be read: " + error.Message;
        }
    }

    // ---------------------------------------------------------------- opening, keys, menu

    private IReadOnlyList<FileSystemItem> Selected => _list.SelectedItems.Cast<FileSystemItem>().ToList();
    private string[] SelectedPaths => Selected.Select(i => i.FullPath).ToArray();
    private IntPtr Owner => Window.GetWindow(this) is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;

    private void OpenSelected()
    {
        foreach (var item in Selected)
        {
            if (item.IsFolder || item.IsDriveRoot) { Go(item.FullPath); return; }
            try { Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true })?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            { _count.Text = "Could not open " + item.Name + ": " + error.Message; }
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.System && e.SystemKey == Key.Left) { Back(); e.Handled = true; return; } // Alt+Left
        switch (e.Key)
        {
            case Key.Enter: OpenSelected(); break;
            case Key.Back: Up(); break;
            case Key.F5: Load(keepSelection: true); break;
            case Key.Delete: Delete(permanent: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
            case Key.C when ctrl: ToClipboard(cut: false); break;
            case Key.X when ctrl: ToClipboard(cut: true); break;
            case Key.V when ctrl: Paste(); break;
            case Key.A when ctrl: _list.SelectAll(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Delete(bool permanent)
    {
        if (SelectedPaths is not { Length: > 0 } paths || _folder.Length == 0) return;
        Report(FileOperationService.Delete(paths, Owner, permanent));
        Load(keepSelection: false);
    }

    private void ToClipboard(bool cut)
    {
        if (SelectedPaths is not { Length: > 0 } paths) return;
        var data = new DataObject();
        var list = new StringCollection(); list.AddRange(paths);
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, true); }
        catch (System.Runtime.InteropServices.ExternalException) { _count.Text = "The clipboard is busy; try again."; }
    }

    private void Paste()
    {
        if (_folder.Length == 0) return;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            var move = Clipboard.GetData("Preferred DropEffect") is MemoryStream effect && effect.Length >= 4 && (BitConverter.ToInt32(effect.ToArray(), 0) & 2) != 0;
            Report(move ? FileOperationService.Move(paths, _folder, Owner) : FileOperationService.Copy(paths, _folder, Owner));
            Load(keepSelection: false);
        }
        catch (System.Runtime.InteropServices.ExternalException) { _count.Text = "The clipboard is busy; try again."; }
    }

    private void Report(FileOperationResult result)
    {
        if (!result.Succeeded && !result.Canceled) _count.Text = result.Message;
    }

    private void BuildMenu()
    {
        var menu = _list.ContextMenu!;
        menu.Items.Clear();
        void Add(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        var any = Selected.Count > 0;
        var main = _mainFolder();
        Add("Open", OpenSelected, any);
        Add("Open in the main list", () => { if (Selected.FirstOrDefault(i => i.IsFolder || i.IsDriveRoot) is { } folder) _openInMain(folder.FullPath); else _openInMain(_folder); });
        menu.Items.Add(new Separator());
        Add("Copy to the main list's folder", () => { if (main is { Length: > 0 }) { Report(FileOperationService.Copy(SelectedPaths, main, Owner)); } }, any && main is { Length: > 0 } && _folder.Length > 0);
        Add("Move to the main list's folder", () => { if (main is { Length: > 0 }) { Report(FileOperationService.Move(SelectedPaths, main, Owner)); Load(false); } }, any && main is { Length: > 0 } && _folder.Length > 0);
        menu.Items.Add(new Separator());
        Add("Copy", () => ToClipboard(false), any); Add("Cut", () => ToClipboard(true), any); Add("Paste", Paste, _folder.Length > 0);
        Add("Delete", () => Delete(false), any && _folder.Length > 0);
        menu.Items.Add(new Separator());
        Add("Refresh", () => Load(true));
        Add("Properties", () => { if (Selected.FirstOrDefault() is { } item) FileOperationService.ShowProperties(item.FullPath, Owner); }, any);
    }

    // ---------------------------------------------------------------- drag and drop

    private void OnDragOut(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _folder.Length == 0) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(_list, d) is not ListViewItem) return;
        if (SelectedPaths is not { Length: > 0 } paths) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private string? DropTarget(DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListViewItem { Content: FileSystemItem { IsFolder: true } folder })
            return folder.FullPath;
        return _folder.Length > 0 ? _folder : null;
    }

    private DragDropEffects EffectFor(DragEventArgs e, string target, string[] paths)
    {
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        var sameDrive = paths.All(p => string.Equals(Path.GetPathRoot(p), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
        return sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || DropTarget(e) is not { } target || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        { e.Effects = DragDropEffects.None; return; }
        e.Effects = paths.Any(p => string.Equals(Path.GetDirectoryName(p), target, StringComparison.OrdinalIgnoreCase) || string.Equals(p, target, StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.None : EffectFor(e, target, paths);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DropTarget(e) is not { } target || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (paths.Any(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase))) return;
        var effect = EffectFor(e, target, paths);
        Report(effect == DragDropEffects.Move ? FileOperationService.Move(paths, target, Owner) : FileOperationService.Copy(paths, target, Owner));
        Load(keepSelection: false);
    }

    public void Dispose()
    {
        _loading?.Cancel();
        _watcher.Dispose();
    }
}
