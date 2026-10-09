using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace.Controls;

// The build embeds the SAME MainWindow.xaml used by Explorer. Remove event hookups only;
// never instantiate MainWindow, its view model, watchers, or file-operation commands.
internal sealed class ExplorerThemePreview : ContentControl
{
    private readonly UserControl _explorer;
    private readonly ListView _files;
    internal FrameworkElement Explorer => _explorer;
    /// <summary>NEW (complex themes): show the preview pane beside the sample files, so a theme can be seen
    /// with it (and with it on the left or the right).</summary>
    internal bool ShowPreviewPane { get; set; }
    private string? _layoutShown; // NEW (custom layouts): the layout.json last laid out
    /// <summary>NEW (everything): the draft as last shown, and what lies under it, so the studio can tell which
    /// theme key a clicked part is drawn with.</summary>
    internal ResourceDictionary? Theme => _theme;
    internal ResourceDictionary Defaults => _defaults;
    private readonly ResourceDictionary _defaults;
    private ResourceDictionary? _theme;
    private readonly FileSystemItem[] _items;
    private static readonly string[] SampleNames = ["Projects", "Photos", "Notes.txt", "Report.pdf", "Budget.xlsx", "Presentation.pptx", "Archive.zip", "Song.mp3", "Design.png", "Readme.md"];

    internal ExplorerThemePreview()
    {
        using var source = typeof(MainWindow).Assembly.GetManifestResourceStream("Clearspace.ExplorerPreview.xaml")!;
        var xml = XElement.Load(source);
        XNamespace ns = ThemeMarkup.Presentation, x = ThemeMarkup.Xaml;
        xml.Name = ns + "UserControl";
        foreach (var key in new[] { "Title", "WindowStartupLocation", "MinHeight", "MinWidth" }) xml.Attribute(key)?.Remove();
        xml.Attribute(x + "Class")?.Remove();
        foreach (var element in xml.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName.StartsWith("clr-namespace:") && !element.Name.NamespaceName.Contains(";assembly="))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Clearspace");
            if (element.Name == ns + "Window.Resources") element.Name = ns + "UserControl.Resources";
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.Name == x + "Shared" || (!attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("On", StringComparison.Ordinal))) attribute.Remove();
                else if (attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("clr-namespace:") && !attribute.Value.Contains(";assembly="))
                    attribute.Value += ";assembly=Clearspace";
                // The preview is strictly read-only, including bindings that normally edit search/volume.
                else if (attribute.Value.StartsWith("{Binding"))
                    attribute.Value = attribute.Value.Contains("Mode=")
                        ? attribute.Value.Replace("Mode=TwoWay", "Mode=OneWay").Replace("Mode=OneWayToSource", "Mode=OneWay")
                        : attribute.Value.Insert(attribute.Value.Length - 1, ", Mode=OneWay");
            }
        }
        _explorer = (UserControl)XamlReader.Parse(xml.ToString());
        _explorer.Width = 1264; _explorer.Height = 760;
        _explorer.IsHitTestVisible = false; _explorer.Focusable = false;
        _explorer.Resources["ThemeStudio.Isolated"] = true;
        _defaults = new ResourceDictionary();
        _defaults.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Defaults.xaml", UriKind.Relative) });
        _defaults.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clearspace;component/Themes/Dark/theme.xaml", UriKind.Relative) });
        _explorer.Resources.MergedDictionaries.Add(_defaults);
        _items = SampleNames
            .Select((name, i) => new FileSystemItem { Name = name, FullPath = @"C:\Documents\" + name,
                Attributes = i < 2 ? FileAttributes.Directory : FileAttributes.Normal, Size = i < 2 ? 0 : 32768 * i,
                DateModified = new DateTime(2026, 10, 1, 14, 30, 0), TypeName = i < 2 ? "File folder" : Path.GetExtension(name).TrimStart('.').ToUpperInvariant() + " file" }).ToArray();
        _explorer.DataContext = new
        {
            Items = _items, Tabs = new { Items = new[] { new { Title = "Documents", Glyph = "\uE8B7", IsSelected = true, Hint = "Documents" } } },
            Breadcrumbs = new[] { new Breadcrumb("This PC", ""), new Breadcrumb("Documents", "") },
            Sidebar = new[] { new SidebarEntry("Your files", "", IsHeader: true), new SidebarEntry("Desktop", ""), new SidebarEntry("Documents", ""), new SidebarEntry("Downloads", ""), new SidebarEntry("Pictures", ""), new SidebarEntry("Music", ""), new SidebarEntry("Favorites", "", IsHeader: true), new SidebarEntry("Projects", ""), new SidebarEntry("This PC", "", IsHeader: true), new SidebarEntry("Local Disk (C:)", "") },
            AddressText = @"C:\Documents", SearchText = "", IsGrid = false, IsDetails = true, IsMusicProfile = false,
            CanSetFolderProfile = true, FolderProfileLabel = "General", IsHub = false, IsNetworkHub = false,
            ShowsProjectStrip = false, HasFolderContext = false, IsInArchive = false, IsAccessDenied = false,
            IsCloudFolder = false, ShowHiddenItems = false, SearchEverywhere = false, SearchFileContents = false, IsSearchingTree = false,
            StatusText = "2 folders, 8 files", IndexStatusText = "Index ready", IndexingStatus = "Index ready", HasUpdate = false,
            Context = new { HasFileOperationResult = false }, Viewer = new { IsOpen = false }, Player = new { IsActive = false, IsOpen = false, IsVisible = false }
        };
        _files = (ListView)_explorer.FindName("FileList");
        if (_files.View is GridView view)
            foreach (var key in new[] { "Col.Name", "Col.DateModified", "Col.Type", "Col.Size" })
                if (_explorer.TryFindResource(key) is GridViewColumn column) view.Columns.Add(column);
        Content = _explorer;
    }

    internal void Show(ThemeStudioDocument document)
    {
        var own = document.Resources(); // the same loader used on Save & use
        if (_theme is not null) _explorer.Resources.MergedDictionaries.Remove(_theme);
        _theme = own; _explorer.Resources.MergedDictionaries.Add(own);
        object? Read(string key) => own.Contains(key) ? own[key] : _defaults[key];
        var ink = ((SolidColorBrush)Read("Ink")!).Color;
        var background = Read("Theme.BaseColor") is Color plain ? plain : Read("Base") switch
        { SolidColorBrush b => b.Color, GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color, _ => Colors.Black };
        var dark = (background.R * 299 + background.G * 587 + background.B * 114) / 1000 < 128;
        var names = SampleNames.ToArray();
        var sampleIndex = names.Length - 1;
        foreach (var key in own.Keys.Cast<object>().OfType<string>().Where(ThemeFileIcons.IsKey).OrderBy(k => k))
        {
            var name = key.StartsWith("FileIcon.Name.") ? key["FileIcon.Name.".Length..] :
                key.StartsWith("FileIcon.Extension.") ? "Example." + key["FileIcon.Extension.".Length..] : null;
            if (name is null || sampleIndex < 2 || names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ||
                (key.StartsWith("FileIcon.Extension.") && names.Any(n => string.Equals(Path.GetExtension(n), Path.GetExtension(name), StringComparison.OrdinalIgnoreCase)))) continue;
            names[sampleIndex--] = name;
        }
        for (var i = 0; i < _items.Length; i++)
        {
            _items[i].Name = names[i]; _items[i].FullPath = @"C:\Documents\" + names[i];
            _items[i].TypeName = i < 2 ? "File folder" : Path.GetExtension(names[i]).TrimStart('.').ToUpperInvariant() + " file";
        }
        foreach (var item in _items)
        {
            item.Icon = ThemeFileIcons.Find(item, Read) ?? ThemeIcons.Preview(item, document.Manifest.Icons, ink, dark);
        }
        ShowSamplePreviewPane();
        // NEW (custom layouts): the draft's layout.json, laid out the same way the window does it. Its extra panes
        // list real folders but do not watch them. Done again only when the layout itself changes.
        if (!string.Equals(document.LayoutJson, _layoutShown, StringComparison.Ordinal))
        {
            _layoutShown = document.LayoutJson;
            ThemeLayout.TryParse(document.LayoutJson, out var layout, out _);
            var assets = layout is null ? null : ThemeCache.Unpack(document.Package);
            ThemeLayoutEngine.Apply(_explorer, layout,
                node => new FilePane(node.Folder, node.Title, () => KnownFolders.Documents, _ => { }, live: false),
                name => assets?.Image(name));
        }
        ThemeExplorerLayout.Apply(_explorer);
        TextOptions.SetTextRenderingMode(_explorer, document.Manifest.CrispText ? TextRenderingMode.Aliased : TextRenderingMode.ClearType);
        if (_explorer.FindName("ToolbarRow") is UIElement toolbar) toolbar.Opacity = document.Manifest.FadeToolbar ? .2 : 1;
        _files.SelectedIndex = 3;
        _explorer.Measure(new Size(1264, 760));
        _explorer.Arrange(new Rect(0, 0, 1264, 760));
        _explorer.UpdateLayout();
        for (var i = 0; i < _items.Length; i++)
        {
            if (_files.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem row) continue;
            row.Resources.Clear();
            foreach (var key in own.Keys.Cast<object>().OfType<string>().Where(k => k.StartsWith(ThemeKeys.SelectedRowPrefix)).ToArray())
            {
                if (row.IsSelected) row.Resources[key[ThemeKeys.SelectedRowPrefix.Length..]] = own[key];
            }
        }
    }

    // NEW (complex themes): the preview pane with a sample in it, sized the way MainWindow.Preview.cs sizes it.
    private void ShowSamplePreviewPane()
    {
        if (_explorer.FindName("PreviewCard") is not Border card || _explorer.FindName("PreviewColumn") is not ColumnDefinition column ||
            _explorer.FindName("PreviewSplitterColumn") is not ColumnDefinition gap) return;
        var shown = ShowPreviewPane;
        card.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (_explorer.FindName("PreviewSplitter") is UIElement splitter) splitter.Visibility = card.Visibility;
        column.MinWidth = 0; column.Width = new GridLength(shown ? 250 : 0);
        gap.Width = shown ? GridLength.Auto : new GridLength(0);
        if (!shown || card.Child is not null) return;
        var sample = new StackPanel { Margin = new Thickness(16) };
        var picture = new Border { Height = 150, Margin = new Thickness(0, 0, 0, 12) };
        picture.SetResourceReference(Border.BackgroundProperty, "Tint");
        picture.SetResourceReference(Border.CornerRadiusProperty, "R8");
        sample.Children.Add(picture);
        var name = new TextBlock { Text = "Report.pdf", FontSize = 14, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        name.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");
        var detail = new TextBlock { Text = "PDF file  ·  96 KB", FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        detail.SetResourceReference(TextBlock.FontFamilyProperty, "UIFont");
        sample.Children.Add(name); sample.Children.Add(detail);
        card.Child = sample;
    }
}
