// Clearspace | Themes: which one is active, switching between them, and the theme colours that code needs.
// NEW (themes): every window, menu, popup and dialog refers to the theme's keys with DynamicResource, so
// swapping one merged dictionary in App.Resources repaints the whole app. Code that draws by hand (the
// disk usage map, status colours in the file list) reads the frozen brushes below instead, which are
// refreshed on every switch.
// CHANGED (theme files): themes are no longer compiled into the app. Each one is a file in the Themes folder
// beside Clearspace.exe (ThemePackage.cs describes what is inside one), including the ones that come with
// Clearspace, and anyone can add their own. This class asks ThemeLibrary what is in the folder and
// ThemeLoader to read a theme; it no longer knows the themes by name.
//   - App.xaml keeps Themes/Defaults.xaml and the Dark theme's theme.xaml merged underneath as the fallback:
//     a theme that leaves keys out gets Dark's values for them, and a Themes folder that is missing or
//     empty still leaves a working (dark) Clearspace.
//   - The active theme's dictionary is merged on top of those. Apply() swaps that one entry.
//   - A theme that cannot be read never replaces the look on screen; LastProblem says why.
//   - When the active theme's file is saved again, the theme is shown again (someone is working on it).
// CHANGED (themes page): Dark can never go missing. It is always in the list, and when its file has been
// deleted or cannot be read, picking it shows the Dark look built into the app (the fallback above, with
// no theme merged on top). Any other theme can be removed (Remove); the Themes page (ThemesView.cs) shows
// each theme as a small window drawn in its own colours (PreviewResources) and is told when the list
// changes (ListChanged).
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clearspace.Native;

namespace Clearspace.Services;

// NEW (experimental themes): everything code needs to know about one theme.
// CHANGED (theme files): "Name" is the theme's file name without ".zip" (what is saved in settings) and
// "Label" is "name" from its theme.json (what the Themes page shows); the switches come from theme.json
// too. Experimental themes are listed under their own heading and may draw their own icons or change how
// the toolbar behaves.
// CHANGED (e-ink): EInk = make the window look and refresh like an e-ink panel (Services/EInkScreen.cs).
public sealed record ThemeInfo(string Name, string Label, bool Experimental = false,
    IconStyle Icons = IconStyle.System, bool FadeToolbar = false, bool EInk = false, bool CrispText = false)
{
    // CHANGED (pixel font): CrispText = draw text with hard pixel edges instead of smoothing (Pixel).

    // NEW (theme files): where the theme is and what its theme.json says about it.
    /// <summary>The theme file (or theme folder) in the Themes folder.</summary>
    public string Path { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;

    /// <summary>True for the themes that come with Clearspace; false for one somebody added.</summary>
    public bool Shipped { get; init; }

    /// <summary>Why the theme cannot be used (its theme.json could not be read), or null.</summary>
    public string? Problem { get; init; }

    /// <summary>NEW (themes page): true for Dark while its file is not in the Themes folder (or cannot be
    /// read): the look built into the app. It has no Path.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>Changes when the theme's files change (ThemeLibrary).</summary>
    internal string Stamp { get; init; } = string.Empty;
}

public static class ThemeService
{
    public const string DefaultTheme = "Dark";

    // CHANGED (theme files): the themes offered in Settings are whatever is in the Themes folder right now
    // (this used to be a fixed list written out here). Clearspace's own come first, then added ones.
    public static IReadOnlyList<ThemeInfo> Themes => ThemeLibrary.Themes;

    public static IReadOnlyList<string> Names => ThemeLibrary.Themes.Select(theme => theme.Name).ToArray();

    /// <summary>NEW (theme files): the folder theme files live in ("Themes" beside Clearspace.exe).</summary>
    public static string Folder => ThemeLibrary.Folder;

    public static string Current { get; private set; } = DefaultTheme;

    /// <summary>NEW (theme files): the active theme's name as its theme.json gives it ("E-reader" for Paper).</summary>
    public static string CurrentLabel { get; private set; } = DefaultTheme;

    // Raised after a switch, once the new colours are in place.
    public static event Action? Changed;

    // NEW (theme files): why the last Apply() or Import() did not work, in words for the person using
    // Clearspace; and an event for when the theme on screen could not be shown again after its file changed
    // (raised on the UI thread; the window puts the message in its status bar).
    public static string? LastProblem { get; private set; }
    public static event Action<string>? ReloadFailed;

    // NEW (themes page): raised on the UI thread after the list of themes changed: a theme was imported or
    // removed, or the Themes folder changed on the disk.
    public static event Action? ListChanged;

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
    private static readonly Color DarkBase = Color.FromRgb(0x1A, 0x19, 0x17);   // (declared first: BaseColor below starts as this)
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
    public static Color BaseColor { get; private set; } = DarkBase;

    // True when the window background is dark (decides the dark or light Windows title bar).
    public static bool IsDark { get; private set; } = true;

    // True when the theme has square corners (Retro).
    public static bool IsSquare { get; private set; }

    // A corner size for code-built or hand-drawn boxes: the given size, or 0 in a square-cornered theme.
    public static double Radius(double normal) => IsSquare ? 0 : normal;
    public static CornerRadius Corner(double normal) => new(Radius(normal));

    // ---------------------------------------------------------------- state
    // REMOVED (theme files): ThemeFolder ("/Clearspace;component/Themes/"); themes are not inside the app now.
    private static ResourceDictionary? _active;        // the theme dictionary merged on top in App.Resources, or null (fallback only)
    private static bool _shown;                        // NEW (themes page): a theme has been put in place (the built-in Dark has no dictionary)
    private static string _activeStamp = string.Empty; // NEW (theme files): its files as they were when it was read
    private static ResourceDictionary? _selectedRow;   // overrides for selected rows (themes with a solid highlight), or null
    private static Color? _captionColor;
    private static Color? _captionTextColor;
    private static bool _hooked;

    // Rows that currently carry the selected-row overrides, so a theme switch can take them back off.
    private static readonly ConditionalWeakTable<ListBoxItem, object> FlippedRows = new();
    private static readonly object Marker = new();

    // ---------------------------------------------------------------- start-up and switching

    // Called once from App.OnStartup, before any window opens: applies the theme saved in settings.
    // CHANGED (theme files): the saved theme is read from its file. When it is gone or cannot be read, Dark
    // is used; when that cannot be read either, the Dark values built into the app (App.xaml) stay.
    public static void Initialize()
    {
        HookSelection();

        // Only the saved theme's file is read before the first window opens. The rest of the Themes folder
        // is listed once the app is idle (and again whenever the Themes page opens).
        var saved = SettingsService.GetTheme();
        var shown = Show(saved, save: false, again: false);
        if (!shown && !string.Equals(saved, DefaultTheme, StringComparison.OrdinalIgnoreCase))
            shown = Show(DefaultTheme, save: false, again: false);

        if (!shown && Application.Current is { } app)
        {
            Current = DefaultTheme;
            CurrentLabel = DefaultTheme;
            ReadTheme(app, active: null);
        }

        // From here on, a theme dropped into the folder appears in the menu, and saving the active theme's
        // file shows it again.
        ThemeLibrary.Watch(() => Application.Current?.Dispatcher.BeginInvoke(new Action(OnFolderChanged)));
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ThemeLibrary.Refresh()));
        ThemeCache.TidyLater();
    }

    // Switches to the named theme now.
    // CHANGED (theme files): returns whether it did. A name that is not in the Themes folder, or a theme
    // that cannot be read, leaves the look as it is (it used to fall back to the default theme) and puts
    // the reason in LastProblem.
    public static bool Apply(string? name, bool save = true) => Show(name, save, again: false);

    internal static bool ApplyEdited(string name) => Show(name, save: true, again: true);

    /// <summary>NEW (theme files): reads the Themes folder again and returns what is in it (the Themes
    /// page does this each time it opens).</summary>
    public static IReadOnlyList<ThemeInfo> Refresh() => ThemeLibrary.Refresh();

    /// <summary>NEW (theme files): checks a theme file from anywhere on the PC and copies it into the
    /// Themes folder. Returns the theme as now listed, or null with the reason in LastProblem.
    /// <paramref name="replace"/> is asked when an added theme of that name is already there.</summary>
    public static ThemeInfo? Import(string file, Func<ThemeInfo, bool>? replace = null)
    {
        LastProblem = null;
        try
        {
            var theme = ThemeLibrary.Import(file, replace);
            ListChanged?.Invoke(); // NEW (themes page)
            return theme;
        }
        catch (ThemeFormatException error)
        {
            LastProblem = error.Message;
            return null;
        }
    }

    /// <summary>NEW (themes page): whether a theme can be removed. Every theme can, except Dark: it is part
    /// of Clearspace, so there is always one theme left.</summary>
    public static bool CanRemove(ThemeInfo theme) => !theme.BuiltIn && !ThemeLibrary.IsDefault(theme.Name);

    /// <summary>NEW (themes page): removes a theme by moving its file to the Recycle Bin (so it can be put
    /// back). When it was the theme on screen, Dark takes its place. Returns false, with the reason in
    /// LastProblem, when it is Dark or the file could not be moved; false with no reason when the question
    /// Windows asked was answered No.</summary>
    public static bool Remove(string name, IntPtr owner)
    {
        LastProblem = null;
        if (ThemeLibrary.Find(name) is not { } theme)
            return true; // it is gone already

        var wasCurrent = string.Equals(theme.Name, Current, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!ThemeLibrary.Remove(theme, path => FileOperationService.Delete([path], owner).Succeeded))
                return false;
        }
        catch (ThemeFormatException error)
        {
            LastProblem = error.Message;
            return false;
        }

        if (wasCurrent)
            Show(DefaultTheme, save: true, again: false);

        ListChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// NEW (themes page): everything a theme defines, with Clearspace's fallback values underneath, for
    /// drawing a preview of it: put the result in an element's Resources and whatever is inside that
    /// element looks its colours, fonts and corners up in this theme instead of the one on screen.
    /// Null, with the reason in <paramref name="problem"/>, when the theme cannot be read.
    /// Call it on the UI thread; nothing on screen changes.
    /// </summary>
    internal static ResourceDictionary? PreviewResources(ThemeInfo theme, out string? problem)
    {
        problem = theme.Problem;
        if (problem is not null || Application.Current is not { } app)
            return null;

        ResourceDictionary? own = null;
        if (!theme.BuiltIn)
        {
            try
            {
                own = ThemeLoader.Load(ThemePackage.Open(theme.Path));
            }
            catch (Exception) when (ThemeLibrary.IsDefault(theme.Name))
            {
                own = null; // Dark's file is damaged: Dark is the built-in look
            }
            catch (ThemeFormatException error)
            {
                problem = error.Message;
                return null;
            }
            catch (Exception error)
            {
                problem = "The theme could not be read: " + error.Message;
                return null;
            }
        }

        // The fallback first (everything merged into the app except the theme on screen), then the theme.
        var all = new ResourceDictionary();
        foreach (var dictionary in app.Resources.MergedDictionaries)
        {
            if (!ReferenceEquals(dictionary, _active))
                all.MergedDictionaries.Add(dictionary);
        }
        if (own is not null)
            all.MergedDictionaries.Add(own);
        return all;
    }

    // "again": show it even though it is the theme on screen (its file changed).
    private static bool Show(string? name, bool save, bool again)
    {
        LastProblem = null;
        var app = Application.Current;
        if (app is null)
            return false;

        HookSelection();
        var theme = ThemeLibrary.Find(name);
        if (theme is null)
        {
            LastProblem = $"There is no theme called \"{name}\" in the Themes folder.";
            return false;
        }
        if (theme.Problem is { } problem)
        {
            LastProblem = problem;
            return false;
        }
        // CHANGED (themes page): was "_active is not null"; the built-in Dark is shown without a dictionary.
        if (!again && _shown && string.Equals(theme.Name, Current, StringComparison.OrdinalIgnoreCase) &&
            theme.BuiltIn == (_active is null))
            return true; // already showing it

        // NEW (themes page): null is the Dark look built into the app: nothing merged on top of the fallback.
        ResourceDictionary? next = null;
        if (!theme.BuiltIn)
        {
            try
            {
                next = ThemeLoader.Load(ThemePackage.Open(theme.Path));
            }
            catch (Exception) when (!again && ThemeLibrary.IsDefault(theme.Name))
            {
                // NEW (themes page): Dark's own file is damaged. Dark still works: the built-in look is used.
                // (Not while its file is being saved again and again by someone changing it; they are told
                // what is wrong instead, below.)
                next = null;
            }
            catch (ThemeFormatException error)
            {
                LastProblem = error.Message;
                return false; // the theme file could not be read; keep the look we have
            }
            catch (Exception error)
            {
                LastProblem = "The theme could not be read: " + error.Message;
                return false;
            }
        }

        Install(theme, next, save);
        return true;
    }

    private static void Install(ThemeInfo theme, ResourceDictionary? next, bool save)
    {
        var app = Application.Current;
        UnflipAllRows();

        // CHANGED (theme files): the active theme is its own entry, after the fallback dictionaries from
        // App.xaml (it used to replace the Dark dictionary, found by looking for the key "Base").
        // CHANGED (themes page): for the built-in Dark that entry is taken away again.
        var merged = app.Resources.MergedDictionaries;
        var index = _active is null ? -1 : merged.IndexOf(_active);
        if (next is null)
        {
            if (index >= 0) merged.RemoveAt(index);
        }
        else if (index >= 0) merged[index] = next;
        else merged.Add(next);

        _active = next;
        _shown = true;
        _activeStamp = theme.Stamp;
        Current = theme.Name;
        CurrentLabel = theme.Label;
        ReadTheme(app, next);

        // NEW (experimental themes): the theme's icon set and toolbar behaviour. Drawn icons are thrown away
        // on every switch because some styles take their colour from the theme.
        // CHANGED (dark sketchbook): two themes can share a drawn style and still colour it differently
        // (Sketchbook's graphite outline is chalk-white on black paper), so any switch to a drawn style counts.
        IconsChanged = theme.Icons != Icons || theme.Icons != IconStyle.System;
        Icons = theme.Icons;
        FadeToolbar = theme.FadeToolbar;
        EInk = theme.EInk;
        CrispText = theme.CrispText;
        EInkScreen.SetActive(EInk);   // NEW (e-ink): builds the screen effect the first time, hands menus and dialogs theirs
        ThemeIcons.Reset();

        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
            FlipSelectedRows(window);
        }

        if (save)
            SettingsService.SetTheme(theme.Name);

        Changed?.Invoke();
    }

    // Uses precisely the same application path as a saved theme, without persisting settings.
    // Keep the original dictionary in memory so Cancel also works if its file changes or disappears.
    internal sealed class PreviewSession : IDisposable
    {
        private readonly ResourceDictionary? _original = _active;
        private readonly ThemeInfo _theme = new(Current, CurrentLabel, Icons: Icons,
            FadeToolbar: FadeToolbar, EInk: EInk, CrispText: CrispText) { Stamp = _activeStamp };
        private bool _disposed;

        internal void Show(ThemePackage package)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var next = ThemeLoader.Load(package);
            var m = package.Manifest;
            Install(new ThemeInfo("studio-preview", m.Name, Icons: m.Icons,
                FadeToolbar: m.FadeToolbar, EInk: m.EInk, CrispText: m.CrispText), next, false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Install(_theme, _original, false);
        }
    }

    // NEW (theme files): something in the Themes folder changed (on the UI thread, a moment after the last
    // change). The list is read again; when it was the theme on screen that changed, it is shown again. A
    // save that left the theme unreadable keeps the look and tells the window why.
    private static void OnFolderChanged()
    {
        ThemeLibrary.Refresh();
        ListChanged?.Invoke(); // NEW (themes page)

        // CHANGED (themes page): was "_active is null"; Dark may be on screen without a dictionary, and its
        // file may just have been put back (or taken away).
        if (!_shown || ThemeLibrary.Find(Current) is not { } theme || theme.Stamp == _activeStamp)
            return;

        _activeStamp = theme.Stamp;   // one try per save, whether or not it works
        if (!Show(theme.Name, save: false, again: true) && LastProblem is { } problem)
            ReloadFailed?.Invoke($"{theme.Label} was not reloaded. {problem}");
    }

    // REMOVED (theme files): Resolve (unknown names became the default theme) and FindThemeDictionary (the
    // active dictionary was the merged one that defined "Base"). ThemeLibrary.Find and _active replace them.

    // Copies what code needs out of the theme.
    // CHANGED (theme files): every key is looked up through the application's resources, so one the active
    // theme leaves out comes from the fallback underneath (it used to keep the previous theme's value).
    // "active" is the theme's own dictionary, or null when only the fallback is in place.
    private static void ReadTheme(Application app, ResourceDictionary? active)
    {
        Ink = Read(app, "Ink", Ink);
        InkMuted = Read(app, "InkMuted", InkMuted);
        InkFaint = Read(app, "InkFaint", InkFaint);
        InkWash = Solid(0x40, Ink.Color.R, Ink.Color.G, Ink.Color.B);
        Accent = Read(app, "Accent", Accent);
        AccentSoft = Read(app, "AccentSoft", AccentSoft);
        AccentWash = Read(app, "AccentWash", AccentWash);
        Good = Read(app, "Good", Good);
        Warn = Read(app, "Warn", Warn);
        Bad = Read(app, "Bad", Bad);
        Quiet = Read(app, "Quiet", Quiet);
        CardFill = Read(app, "CardFill", CardFill);
        CardEdge = Read(app, "CardEdge", CardEdge);
        Ground3D = Read(app, "Ground3D", Ground3D);
        CardEdgePen = FrozenPen(CardEdge, 1);
        AccentEdgePen = FrozenPen(Solid(0xFF, Accent.Color.R, Accent.Color.G, Accent.Color.B), 2);

        // CHANGED (experimental themes): a theme whose window background is not one flat colour (Glass is a
        // gradient, Sketchbook is graph paper) names a plain colour for code to use as "Theme.BaseColor".
        // CHANGED (theme files): a gradient without one counts as its first colour; anything else as dark.
        BaseColor = app.TryFindResource("Theme.BaseColor") is Color plain ? plain : app.TryFindResource("Base") switch
        {
            SolidColorBrush window => window.Color,
            GradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
            _ => DarkBase
        };
        IsDark = (BaseColor.R * 299 + BaseColor.G * 587 + BaseColor.B * 114) / 1000 < 128;
        IsSquare = app.TryFindResource("R8") is CornerRadius corner && corner.TopLeft == 0;

        _captionColor = app.TryFindResource("Theme.CaptionColor") is Color caption ? caption : null;
        _captionTextColor = app.TryFindResource("Theme.CaptionTextColor") is Color captionText ? captionText : null;

        // "SelectedRow.Ink" etc. become a small dictionary keyed "Ink" etc. (see FlipRow).
        // (The keys are copied first: reading a value can make the dictionary rewrite its entry.)
        ResourceDictionary? row = null;
        if (active is not null)
        {
            foreach (var key in active.Keys.Cast<object>().ToArray())
            {
                if (key is not string text || !text.StartsWith(ThemeKeys.SelectedRowPrefix, StringComparison.Ordinal))
                    continue;

                var value = active[text];
                if (value is Freezable { CanFreeze: true, IsFrozen: false } freezable)
                    freezable.Freeze();

                row ??= new ResourceDictionary();
                row[text[ThemeKeys.SelectedRowPrefix.Length..]] = value;
            }
        }
        _selectedRow = row;
    }

    private static SolidColorBrush Read(Application app, string key, SolidColorBrush fallback) =>
        app.TryFindResource(key) is SolidColorBrush brush ? Solid(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B) : fallback;

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
