using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clearspace.Services;

// NEW (custom layouts): puts the explorer window together the way a theme's layout.json says (ThemeLayout.cs).
// The window's own parts (tabs, toolbar, sidebar, the main file list, the status bar, the preview pane, the
// shortcut keys) are the same objects as always, so everything they do keeps working; they are only moved:
// taken out of where MainWindow.xaml put them and placed in a tree of rows and columns built in LayoutHost.
// Where each part came from is remembered, and with no layout (or a different one) everything goes back first,
// exactly as it was. Extra file panes come from the window (makePane), so it can keep them and tidy them up.
// Used by the real window and by the theme studio's preview, which share MainWindow.xaml.
internal static class ThemeLayoutEngine
{
    // Where a moved part lived: its parent, its place among the parent's children, its grid cell, margin and size.
    private sealed record Home(Panel Parent, int Index, int Row, int Column, int RowSpan, int ColumnSpan, Thickness Margin, double Width, bool KeepsVisibility);

    private static readonly ConditionalWeakTable<FrameworkElement, Home> Homes = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> CustomRoots = new();

    // The parts a layout can place, by block name, and the element's x:Name in MainWindow.xaml.
    private static readonly (string Block, string Name)[] Parts =
    [
        ("tabs", "TabRow"), ("toolbar", "ToolbarRow"), ("sidebar", "SidebarScroll"), ("files", "ContentCard"),
        ("status", "StatusBar"), ("preview", "PreviewCard"), ("dock", "DockCard"), ("keys", "KeyBar")   // CHANGED (shelf and inspector): + dock
    ];

    /// <summary>True while a custom layout is shown in this window (ThemeExplorerLayout then leaves things alone).</summary>
    internal static bool IsCustom(FrameworkElement root) => CustomRoots.TryGetValue(root, out _);

    /// <param name="root">The window (or the studio's copy of it).</param>
    /// <param name="layout">The layout, or null for the usual one.</param>
    /// <param name="makePane">Makes an extra file pane for a "pane" block.</param>
    /// <param name="image">The address of a picture in the theme's images folder, or null.</param>
    internal static void Apply(FrameworkElement root, LayoutNode? layout, Func<LayoutNode, FrameworkElement> makePane, Func<string, string?> image)
    {
        if (root.FindName("LayoutHost") is not Grid host || root.FindName("NavigationArea") is not FrameworkElement navigation ||
            root.FindName("BrowserBody") is not FrameworkElement body) return;

        // Everything back where it came from, and the old tree taken down.
        foreach (var (_, name) in Parts)
            if (root.FindName(name) is FrameworkElement part) GoHome(part);
        host.Children.Clear(); host.RowDefinitions.Clear(); host.ColumnDefinitions.Clear();
        if (root.FindName("SidebarPanel") is FrameworkElement panel) panel.ClearValue(UIElement.VisibilityProperty);

        if (layout is null)
        {
            CustomRoots.Remove(root);
            host.Visibility = Visibility.Collapsed;
            navigation.Visibility = body.Visibility = Visibility.Visible;
            return;
        }

        CustomRoots.AddOrUpdate(root, true);
        navigation.Visibility = body.Visibility = Visibility.Collapsed;
        host.Visibility = Visibility.Visible;
        host.Children.Add(Build(root, layout, makePane, image));
    }

    private static UIElement Build(FrameworkElement root, LayoutNode node, Func<LayoutNode, FrameworkElement> makePane, Func<string, string?> image)
    {
        if (node.IsContainer)
        {
            var grid = new Grid();
            var row = node.Split == "row";
            var children = node.Children ?? [];
            var index = 0;
            for (var i = 0; i < children.Count; i++)
            {
                if (i > 0 && node.Splitters == true)
                {
                    var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext, FocusVisualStyle = null };
                    if (row) { splitter.Width = 6; splitter.HorizontalAlignment = HorizontalAlignment.Stretch; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(splitter, index++); }
                    else { splitter.Height = 6; splitter.VerticalAlignment = VerticalAlignment.Stretch; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(splitter, index++); }
                    grid.Children.Add(splitter);
                }
                var size = ThemeLayout.Length(children[i]);
                var cell = Build(root, children[i], makePane, image);
                if (row)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = size, MinWidth = size.IsAbsolute || size.IsStar ? 40 : 0 });
                    Grid.SetColumn(cell, index++);
                }
                else
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = size, MinHeight = size.IsStar ? 40 : 0 });
                    Grid.SetRow(cell, index++);
                }
                grid.Children.Add(cell);
            }
            return Frame(node, grid, defaultMargin: 0);
        }

        FrameworkElement content;
        switch (node.Block)
        {
            case "pane":
                content = makePane(node);
                break;
            case "text":
            {
                var text = new TextBlock
                {
                    Text = node.Text ?? "", FontSize = node.FontSize ?? 16, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = node.Align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left }
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                text.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
                content = text;
                break;
            }
            case "clock":
            {
                var clock = new TextBlock { FontSize = node.FontSize ?? 22, VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = node.Align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left } };
                clock.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                clock.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                void Tick() => clock.Text = DateTime.Now.ToString("t") + "  ·  " + DateTime.Now.ToString("ddd d MMM");
                timer.Tick += (_, _) => Tick();
                clock.Loaded += (_, _) => { Tick(); timer.Start(); };
                clock.Unloaded += (_, _) => timer.Stop();
                content = clock;
                break;
            }
            case "image":
            {
                var picture = new Image { Stretch = Stretch.Uniform, MaxHeight = node.Size is null ? 160 : double.PositiveInfinity };
                if (node.Image is { } name && image(name) is { } address)
                {
                    try { picture.Source = new BitmapImage(new Uri(address)); }
                    catch (Exception e) when (e is UriFormatException or NotSupportedException or System.IO.IOException or ArgumentException) { }
                }
                content = picture;
                break;
            }
            case "spacer":
                content = new Border();
                break;
            default:
            {
                var name = Parts.First(p => p.Block == node.Block).Name;
                if (root.FindName(name) is not FrameworkElement part) { content = new Border(); break; }
                LeaveHome(part, keepVisibility: node.Block is "preview" or "dock");
                part.Margin = new Thickness(0);
                if (node.Block is "tabs" or "toolbar") part.Margin = node.Block == "tabs" ? new Thickness(4, 4, 4, 0) : new Thickness(4, 4, 4, 4);
                if (node.Block == "keys" || node.Block == "status" || node.Block == "tabs" || node.Block == "sidebar") part.Visibility = Visibility.Visible;
                if (node.Block == "status" && part is Border status) status.BorderThickness = new Thickness(0, 1, 0, 0);
                if (node.Block is "preview" or "dock")
                {
                    var length = ThemeLayout.Length(node);
                    part.Width = length.IsAbsolute ? length.Value : 300;
                    var frame = Frame(node, part, defaultMargin: 6);
                    // The preview pane is opened and closed with Alt+P (CHANGED: and the dock with Alt+I and Alt+S);
                    // its place shrinks away while it is closed.
                    frame.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(Visibility)) { Source = part });
                    return frame;
                }
                content = part;
                break;
            }
        }
        return Frame(node, content, defaultMargin: node.Block is "files" or "pane" or "sidebar" ? 6 : 0);
    }

    // A border round a part, carrying its margin, padding, fill, outline and corners.
    private static Border Frame(LayoutNode node, UIElement child, double defaultMargin)
    {
        var frame = new Border
        {
            Child = child, Margin = new Thickness(node.Margin ?? defaultMargin), Padding = new Thickness(node.Padding ?? 0),
            CornerRadius = new CornerRadius(node.Corner ?? 0), BorderThickness = new Thickness(node.EdgeWidth ?? (node.Edge is null ? 0 : 1))
        };
        Paint(frame, Border.BackgroundProperty, node.Fill);
        Paint(frame, Border.BorderBrushProperty, node.Edge);
        if (node.Corner is > 0 && node.Fill is not null) frame.ClipToBounds = true;
        return frame;
    }

    private static void Paint(FrameworkElement element, DependencyProperty property, string? brush)
    {
        if (brush is null) return;
        if (brush.StartsWith('#'))
        {
            try { element.SetValue(property, new SolidColorBrush((Color)ColorConverter.ConvertFromString(brush)!)); }
            catch (FormatException) { }
        }
        else element.SetResourceReference(property, brush);
    }

    private static void LeaveHome(FrameworkElement part, bool keepVisibility)
    {
        if (part.Parent is not Panel parent) return;
        if (!Homes.TryGetValue(part, out _))
            Homes.Add(part, new Home(parent, parent.Children.IndexOf(part), Grid.GetRow(part), Grid.GetColumn(part),
                Grid.GetRowSpan(part), Grid.GetColumnSpan(part), part.Margin, part.Width, keepVisibility));
        parent.Children.Remove(part);
    }

    private static void GoHome(FrameworkElement part)
    {
        if (!Homes.TryGetValue(part, out var home)) return;
        Homes.Remove(part);
        if (part.Parent is Decorator decorator) decorator.Child = null;
        else if (part.Parent is Panel panel) panel.Children.Remove(part);
        if (part.Parent is not null) return; // somewhere we did not put it; leave it
        home.Parent.Children.Insert(Math.Clamp(home.Index, 0, home.Parent.Children.Count), part);
        Grid.SetRow(part, home.Row); Grid.SetColumn(part, home.Column);
        Grid.SetRowSpan(part, home.RowSpan); Grid.SetColumnSpan(part, home.ColumnSpan);
        part.Margin = home.Margin; part.Width = home.Width;
        if (part.Name == "KeyBar") part.SetResourceReference(UIElement.VisibilityProperty, "FunctionKeysVisibility");
        else if (!home.KeepsVisibility) part.ClearValue(UIElement.VisibilityProperty);
    }
}
