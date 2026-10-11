namespace Clearspace.Services;

internal static class StudioColorOptions
{
    internal static readonly (string Group, string Key, string Label)[] All =
    [
        ("Backgrounds", "Base", "Window background"), ("Backgrounds", "Surface", "File area & cards"),
        ("Backgrounds", "Overlay", "Menus & popups"), ("Backgrounds", "ListFill", "Behind the file list"),
        ("Backgrounds", "NavFill", "Navigation background"), ("Backgrounds", "CardFill", "Information cards"),
        ("Backgrounds", "Well", "Inset panels"), ("Backgrounds", "Tint", "Subtle panel tint"),
        ("Backgrounds", "RowAlt", "Alternating rows"), ("Backgrounds", "BadgeFill", "Small badges"),
        ("Text", "Ink", "File names & main text"), ("Text", "InkMuted", "Dates, sizes & secondary text"),
        ("Text", "InkFaint", "Hints & quiet labels"), ("Text", "InkDisabled", "Unavailable controls"),
        ("Text", "Quiet", "Status messages"),
        ("Accents & selection", "Accent", "Accent color"), ("Accents & selection", "AccentInk", "Text on accent buttons"),
        ("Accents & selection", "AccentSoft", "Soft accent backgrounds"), ("Accents & selection", "AccentWash", "Faint accent backgrounds"),
        ("Accents & selection", "Selected", "Selected file background"),
        ("Accents & selection", "SelectedRow.Ink", "Selected file names"),
        ("Accents & selection", "SelectedRow.InkMuted", "Selected file dates & sizes"),
        ("Accents & selection", "SelectedRow.InkFaint", "Selected file hints"),
        ("Buttons & menus", "Hover", "Button hover background"), ("Buttons & menus", "Pressed", "Pressed buttons"),
        ("Buttons & menus", "ChipHover", "Small button hover background"), ("Buttons & menus", "MenuHover", "Highlighted menu item"),
        ("Buttons & menus", "MenuHoverInk", "Highlighted menu text"), ("Buttons & menus", "MenuHoverMuted", "Highlighted menu descriptions"),
        ("Buttons & menus", "MenuHoverFaint", "Highlighted menu hints"), ("Buttons & menus", "MenuHoverAccent", "Highlighted menu icons"),
        ("Borders & sliders", "Line", "Standard borders"), ("Borders & sliders", "LineFaint", "Subtle borders"),
        ("Borders & sliders", "EdgeRaised", "Raised edges"), ("Borders & sliders", "EdgeSunken", "Inset edges"),
        ("Borders & sliders", "CardEdge", "Card outlines"), ("Borders & sliders", "Divider", "Section dividers"),
        ("Borders & sliders", "Track", "Progress tracks"), ("Borders & sliders", "SliderRail", "Slider tracks"),
        ("Borders & sliders", "ThumbRest", "Scrollbar handles"), ("Borders & sliders", "ThumbHover", "Scrollbar handles on hover"),
        ("Status & special effects", "Good", "Success messages"), ("Status & special effects", "Warn", "Warnings"),
        ("Status & special effects", "Bad", "Error indicators"), ("Status & special effects", "Danger", "Delete & danger actions"),
        ("Status & special effects", "Ground3D", "Disk map background"), ("Status & special effects", "Scrim", "Dimmed background behind dialogs"),
        ("Status & special effects", "OverlayFill", "Decorative screen overlay"), ("Status & special effects", "EdgeShade", "Shade over the window"), ("Status & special effects", "BindingFill", "Notebook binding decoration"),
        // NEW (complex themes): each part's own fill and outline (the Parts card styles them too).
        ("Parts", "Part.Sidebar.Fill", "Sidebar panel"), ("Parts", "Part.Sidebar.Edge", "Sidebar outline"),
        ("Parts", "Part.Toolbar.Fill", "Toolbar panel"), ("Parts", "Part.Toolbar.Edge", "Toolbar outline"),
        ("Parts", "Part.Files.Fill", "File area"), ("Parts", "Part.Files.Edge", "File area outline"),
        ("Parts", "Part.Status.Fill", "Status bar"), ("Parts", "Part.Status.Edge", "Status bar line"),
        ("Title bar", "Theme.CaptionColor", "Title bar background"), ("Title bar", "Theme.CaptionTextColor", "Title bar text"),
        ("Title bar", "Theme.BaseColor", "Background color for window effects")
    ];
    internal static string Label(string key) => All.First(o => o.Key == key).Label;
}
