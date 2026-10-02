// Clearspace | Themes: which one is active, switching between them, and the theme colours that code needs.
// NEW (themes): the colours, fonts and corner sizes live in Themes/Dark.xaml, Light.xaml and Retro.xaml.
// App.xaml merges Dark.xaml; Apply() swaps that one merged dictionary for another theme's, and every
// window, menu, popup and dialog follows because the XAML refers to the keys with DynamicResource.
// Code that draws by hand (the disk usage map, status colours in the file list) reads the frozen
// brushes below instead, which are refreshed on every switch.
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Clearspace.Native;

namespace Clearspace.Services;

// NEW (experimental themes): everything code needs to know about one theme. "Name" is its file in the
// Themes folder and what is saved in settings; "Label" is what the Settings menu shows. Experimental themes
// are listed in their own submenu and may draw their own icons or change how the toolbar behaves.
// CHANGED (e-ink): EInk = make the window look and refresh like an e-ink panel (Services/EInkScreen.cs).
public sealed record ThemeInfo(string Name, string Label, bool Experimental = false,
    IconStyle Icons = IconStyle.System, bool FadeToolbar = false, bool EInk = false, bool CrispText = false);
// CHANGED (pixel font): CrispText = draw text with hard pixel edges instead of smoothing (Pixel).

public static class ThemeService
{
    public const string DefaultTheme = "Dark";

    // The themes offered in Settings. Each name is a file in the Themes folder.
    // CHANGED (themes): added OLED, Paper, Blueprint and Terminal.
    // CHANGED (experimental themes): now a list of ThemeInfo, with the nine experimental themes at the end.
    public static readonly IReadOnlyList<ThemeInfo> Themes =
    [
        new ThemeInfo("Dark", "Dark"),
        new ThemeInfo("OLED", "OLED Black"),
        new ThemeInfo("Light", "Light"),
        new ThemeInfo("Blueprint", "Blueprint"),
        new ThemeInfo("Terminal", "Terminal"),
        new ThemeInfo("Retro", "Retro"),
        new ThemeInfo("Pixel", "Pixel", Experimental: true, Icons: IconStyle.Pixel, CrispText: true),
        new ThemeInfo("DosCommander", "DOS Commander", Experimental: true, Icons: IconStyle.Mark),
        new ThemeInfo("Wireframe", "Wireframe", Experimental: true, Icons: IconStyle.Line),
        new ThemeInfo("NeonHud", "Neon HUD", Experimental: true, Icons: IconStyle.Neon),
        new ThemeInfo("Glass", "Glass", Experimental: true),
        new ThemeInfo("Sketchbook", "Sketchbook", Experimental: true, Icons: IconStyle.Sketch),
        new ThemeInfo("SketchbookDark", "Sketchbook (dark)", Experimental: true, Icons: IconStyle.Sketch),   // NEW (dark sketchbook)
        new ThemeInfo("TypeTiles", "Type tiles", Experimental: true, Icons: IconStyle.Tile),
        new ThemeInfo("Zen", "Zen", Experimental: true, Icons: IconStyle.Dot, FadeToolbar: true),
        new ThemeInfo("Mac1Bit", "1-bit Mac", Experimental: true, Icons: IconStyle.OneBit),
        new ThemeInfo("Mac1BitDark", "1-bit Mac (dark)", Experimental: true, Icons: IconStyle.OneBitDark),   // NEW (1-bit Mac dark)
        // CHANGED (e-reader): Paper moved down here from the normal themes; its file is still Paper.xaml.
        new ThemeInfo("Paper", "E-reader", Experimental: true, Icons: IconStyle.Ink, EInk: true),
    ];

    public static readonly IReadOnlyList<string> Names = Themes.Select(theme => theme.Name).ToArray();

    public static string Current { get; private set; } = DefaultTheme;

    // Raised after a switch, once the new colours are in place.
    public static event Action? Changed;

    // NEW (experimental themes): which icons the active theme uses (System = Windows' own), whether the
    // last switch changed that (the file list then has to be read again to pick up the new icons), and
    // whether the toolbar should fade while the pointer is elsewhere.
    public static IconStyle Icons { get; private set; } = IconStyle.System;
    public static bool IconsChanged { get; private set; }
    public static bool FadeToolbar { get; private set; }
    public static bool EInk { get; private set; }   // NEW (e-ink)
    public static bool CrispText { get; private set; }   // NEW (pixel font)

    // ---------------------------------------------------------------- colours for code
    // These start as the dark theme's values so anything drawn before (or without) an Application,
    // such as the unit tests, looks the way it always did.
    public static SolidColorBrush Ink { get; private set; } = Solid(0xFF, 0xEC, 0xE9, 0xE3);
    public static SolidColorBrush InkMuted { get; private set; } = Solid(0xFF, 0x9C, 0x96, 0x8D);
    public static SolidColorBrush InkFaint { get; private set; } = Solid(0xFF, 0x6E, 0x68, 0x62);
    public static SolidColorBrush InkWash { get; private set; } = Solid(0x40, 0xEC, 0xE9, 0xE3);   // a quarter-strength Ink
    public static SolidColorBrush Accent { get; private set; } = Solid(0xFF, 0xD3, 0xA1, 0x5F);
    public static SolidColorBrush AccentSoft { get; private set; } = Solid(0x33, 0xD3, 0xA1, 0x5F);
    public static SolidColorBrush AccentWash { get; private set; } = Solid(0x20, 0xD3, 0xA1, 0x5F);
    public static SolidColorBrush Good { get; private set; } = Solid(0xFF, 0x70, 0xB2, 0x84);
    public static SolidColorBrush Warn { get; private set; } = Solid(0xFF, 0xD3, 0xA1, 0x5F);
    public static SolidColorBrush Bad { get; private set; } = Solid(0xFF, 0xD6, 0x5F, 0x54);
    public static SolidColorBrush Quiet { get; private set; } = Solid(0xFF, 0x9C, 0x96, 0x8D);
    public static SolidColorBrush CardFill { get; private set; } = Solid(0xF4, 0x23, 0x22, 0x20);
    public static SolidColorBrush CardEdge { get; private set; } = Solid(0xFF, 0x3A, 0x37, 0x32);
    public static SolidColorBrush Ground3D { get; private set; } = Solid(0xFF, 0x2A, 0x28, 0x25);
    public static Pen CardEdgePen { get; private set; } = FrozenPen(Solid(0xFF, 0x3A, 0x37, 0x32), 1);
    public static Pen AccentEdgePen { get; private set; } = FrozenPen(Solid(0xFF, 0xD3, 0xA1, 0x5F), 2);
    public static Color BaseColor { get; private set; } = Color.FromRgb(0x1A, 0x19, 0x17);

    // True when the window background is dark (decides the dark or light Windows title bar).
    public static bool IsDark { get; private set; } = true;

    // True when the theme has square corners (Retro).
    public static bool IsSquare { get; private set; }

    // A corner size for code-built or hand-drawn boxes: the given size, or 0 in a square-cornered theme.
    public static double Radius(double normal) => IsSquare ? 0 : normal;
    public static CornerRadius Corner(double normal) => new(Radius(normal));

    // ---------------------------------------------------------------- state
    private const string ThemeFolder = "/Clearspace;component/Themes/";
    private const string SelectedRowPrefix = "SelectedRow.";

    private static ResourceDictionary? _active;        // the theme dictionary currently merged into App.Resources
    private static ResourceDictionary? _selectedRow;   // overrides for selected rows (themes with a solid highlight), or null
    private static Color? _captionColor;
    private static Color? _captionTextColor;
    private static bool _hooked;

    // Rows that currently carry the selected-row overrides, so a theme switch can take them back off.
    private static readonly ConditionalWeakTable<ListBoxItem, object> FlippedRows = new();
    private static readonly object Marker = new();

    // ---------------------------------------------------------------- start-up and switching

    // Called once from App.OnStartup, before any window opens: applies the theme saved in settings.
    public static void Initialize()
    {
        HookSelection();

        var saved = SettingsService.GetTheme();
        var name = Resolve(saved);
        if (name == DefaultTheme)
        {
            // App.xaml already merged Dark.xaml; just read it.
            if (Application.Current is { } app && FindThemeDictionary(app.Resources.MergedDictionaries) is { } dark)
            {
                _active = dark;
                ReadTheme(dark);
            }
            Current = DefaultTheme;
            return;
        }

        Apply(name, save: false);
    }

    // Switches to the named theme now. Unknown names fall back to the default theme.
    public static void Apply(string? name, bool save = true)
    {
        var app = Application.Current;
        if (app is null)
            return;

        HookSelection();
        var theme = Resolve(name);
        if (_active is not null && theme == Current)
            return; // already showing it

        ResourceDictionary next;
        try
        {
            next = new ResourceDictionary { Source = new Uri(ThemeFolder + theme + ".xaml", UriKind.Relative) };
        }
        catch (Exception)
        {
            return; // the theme file could not be read; keep the look we have
        }

        UnflipAllRows();

        var merged = app.Resources.MergedDictionaries;
        var current = _active ?? FindThemeDictionary(merged);
        var index = current is null ? -1 : merged.IndexOf(current);
        if (index >= 0) merged[index] = next;
        else merged.Add(next);

        _active = next;
        Current = theme;
        ReadTheme(next);

        // NEW (experimental themes): the theme's icon set and toolbar behaviour. Drawn icons are thrown away
        // on every switch because some styles take their colour from the theme.
        var info = Themes.First(known => known.Name == theme);
        // CHANGED (dark sketchbook): two themes can share a drawn style and still colour it differently
        // (Sketchbook's graphite outline is chalk-white on black paper), so any switch to a drawn style counts.
        IconsChanged = info.Icons != Icons || info.Icons != IconStyle.System;
        Icons = info.Icons;
        FadeToolbar = info.FadeToolbar;
        EInk = info.EInk;
        CrispText = info.CrispText;
        EInkScreen.SetActive(EInk);   // NEW (e-ink): builds the screen effect the first time, hands menus and dialogs theirs
        ThemeIcons.Reset();

        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
            FlipSelectedRows(window);
        }

        if (save)
            SettingsService.SetTheme(theme);

        Changed?.Invoke();
    }

    private static string Resolve(string? name) =>
        Names.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) ?? DefaultTheme;

    // The merged dictionary that holds a theme is the one that defines "Base".
    private static ResourceDictionary? FindThemeDictionary(IEnumerable<ResourceDictionary> merged)
    {
        foreach (var dictionary in merged)
        {
            if (dictionary.Contains("Base"))
                return dictionary;
        }
        return null;
    }

    // Copies what code needs out of a theme dictionary.
    private static void ReadTheme(ResourceDictionary theme)
    {
        Ink = Read(theme, "Ink", Ink);
        InkMuted = Read(theme, "InkMuted", InkMuted);
        InkFaint = Read(theme, "InkFaint", InkFaint);
        InkWash = Solid(0x40, Ink.Color.R, Ink.Color.G, Ink.Color.B);
        Accent = Read(theme, "Accent", Accent);
        AccentSoft = Read(theme, "AccentSoft", AccentSoft);
        AccentWash = Read(theme, "AccentWash", AccentWash);
        Good = Read(theme, "Good", Good);
        Warn = Read(theme, "Warn", Warn);
        Bad = Read(theme, "Bad", Bad);
        Quiet = Read(theme, "Quiet", Quiet);
        CardFill = Read(theme, "CardFill", CardFill);
        CardEdge = Read(theme, "CardEdge", CardEdge);
        Ground3D = Read(theme, "Ground3D", Ground3D);
        CardEdgePen = FrozenPen(CardEdge, 1);
        AccentEdgePen = FrozenPen(Solid(0xFF, Accent.Color.R, Accent.Color.G, Accent.Color.B), 2);

        // CHANGED (experimental themes): a theme whose window background is not one flat colour (Glass is a
        // gradient, Sketchbook is graph paper) names a plain colour for code to use as "Theme.BaseColor".
        if (theme["Theme.BaseColor"] is Color plain)
            BaseColor = plain;
        else if (theme["Base"] is SolidColorBrush window)
            BaseColor = window.Color;
        IsDark = (BaseColor.R * 299 + BaseColor.G * 587 + BaseColor.B * 114) / 1000 < 128;
        IsSquare = theme["R8"] is CornerRadius corner && corner.TopLeft == 0;

        _captionColor = theme["Theme.CaptionColor"] is Color caption ? caption : null;
        _captionTextColor = theme["Theme.CaptionTextColor"] is Color captionText ? captionText : null;

        // "SelectedRow.Ink" etc. become a small dictionary keyed "Ink" etc. (see FlipRow).
        // (The keys are copied first: reading a value can make the dictionary rewrite its entry.)
        ResourceDictionary? row = null;
        foreach (var key in theme.Keys.Cast<object>().ToArray())
        {
            if (key is not string text || !text.StartsWith(SelectedRowPrefix, StringComparison.Ordinal))
                continue;

            var value = theme[text];
            if (value is Freezable { CanFreeze: true, IsFrozen: false } freezable)
                freezable.Freeze();

            row ??= new ResourceDictionary();
            row[text[SelectedRowPrefix.Length..]] = value;
        }
        _selectedRow = row;
    }

    private static SolidColorBrush Read(ResourceDictionary theme, string key, SolidColorBrush fallback) =>
        theme[key] is SolidColorBrush brush ? Solid(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B) : fallback;

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    // ---------------------------------------------------------------- title bars

    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);   // "let Windows choose"

    // Makes a window's title bar match the theme: dark or light, round or square corners, and (on
    // Windows 11) the theme's own caption colours when it sets them. Windows 10 ignores the colours.
    // Call it once the window has a handle (SourceInitialized or later); Apply() repeats it for every
    // open window when the theme changes.
    public static void ApplyTitleBar(Window window)
    {
        // NEW (pixel font): the window's text is drawn hard-edged in a theme that asks for it, smoothed otherwise.
        TextOptions.SetTextRenderingMode(window, CrispText ? TextRenderingMode.Aliased : TextRenderingMode.ClearType);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            ApplyTitleBar(handle);
    }

    public static void ApplyTitleBar(IntPtr handle)
    {
        var dark = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref dark, sizeof(int));

        var corners = IsSquare ? DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        var caption = _captionColor is { } fill ? ColorRef(fill) : DwmColorDefault;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

        var text = _captionTextColor is { } ink ? ColorRef(ink) : DwmColorDefault;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    // Windows wants 0x00BBGGRR.
    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    // ---------------------------------------------------------------- selected rows
    // In most themes a selected row is a slightly different shade and its text keeps its colours. In
    // Retro it is solid navy, so dark text would vanish. Rather than give every cell of every list a
    // second set of colours, the theme lists replacements ("SelectedRow.Ink" ...) and these handlers
    // merge them into a row's own resources while it is selected: each element inside the row looks a
    // key up through its parents, reaches the row first, and takes the replacement. Nothing happens in
    // themes that list no replacements.

    private static void HookSelection()
    {
        if (_hooked)
            return;
        _hooked = true;

        EventManager.RegisterClassHandler(typeof(ListBoxItem), ListBoxItem.SelectedEvent, new RoutedEventHandler(OnRowSelected), true);
        EventManager.RegisterClassHandler(typeof(ListBoxItem), ListBoxItem.UnselectedEvent, new RoutedEventHandler(OnRowUnselected), true);
    }

    private static void OnRowSelected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is ListBoxItem row)
            FlipRow(row, selected: true);
    }

    private static void OnRowUnselected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is ListBoxItem row)
            FlipRow(row, selected: false);
    }

    private static void FlipRow(ListBoxItem row, bool selected)
    {
        var overrides = _selectedRow;
        if (overrides is null || row is ComboBoxItem)   // drop-down entries keep their own highlight
            return;

        var merged = row.Resources.MergedDictionaries;
        if (selected)
        {
            if (!merged.Contains(overrides))
                merged.Add(overrides);
            FlippedRows.AddOrUpdate(row, Marker);
        }
        else
        {
            merged.Remove(overrides);
            FlippedRows.Remove(row);
        }
    }

    // Before a switch: take the old theme's overrides off every row that has them.
    private static void UnflipAllRows()
    {
        var overrides = _selectedRow;
        if (overrides is not null)
        {
            foreach (var pair in FlippedRows.ToArray())
                pair.Key.Resources.MergedDictionaries.Remove(overrides);
        }
        FlippedRows.Clear();
    }

    // After a switch: rows that were already selected never raised Selected under the new theme.
    private static void FlipSelectedRows(DependencyObject root)
    {
        if (_selectedRow is null)
            return;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ListBoxItem { IsSelected: true } row)
                FlipRow(row, selected: true);
            FlipSelectedRows(child);
        }
    }
}
