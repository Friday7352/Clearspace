using System.Windows;
using System.Windows.Threading;

namespace Clearspace;

// NEW (shelf and inspector): the "Panels" part of the Arrange panel. The preview pane, the Inspector and the Shelf
// are opened and closed here as well as with their keys (Alt+P, Alt+I, Alt+S). They belong to you, not to the
// theme: they stay as they are whichever theme you pick, and saving a theme does not save them. While editing they
// open in the real window, so you see straight away where they sit with this theme's layout.
internal sealed partial class ThemeEditor
{
    private void ArrangePanelsSection()
    {
        if (_owner is not MainWindow window) return;
        Heading("Panels");
        _body.Children.Add(Note("Open in every theme. They sit on the side away from the sidebar."));
        Check("Preview pane  ·  Alt+P", window.PreviewShown, on => TogglePanel(() => window.ShowPreviewPane(on)), "A big preview of the selected file");
        Check("Inspector  ·  Alt+I", window.InspectorShown, on => TogglePanel(() => window.SetInspectorShown(on)), "Everything about the selected file: its real size, pixels, dates, the program that opens it, its fingerprint");
        Check("Shelf  ·  Alt+S", window.ShelfShown, on => TogglePanel(() => window.SetShelfShown(on)), "Collect files from anywhere, then move, copy or zip them all at once");
        if (_document.LayoutJson is { } layout && !layout.Contains("\"dock\"", StringComparison.Ordinal))
            _body.Children.Add(Note("This theme's custom layout has no place for the Inspector and the Shelf. Add \"Shelf and Inspector\" in the layout builder."));
    }

    // Opens or closes a panel, then moves the outlines once the window has made room for it.
    private void TogglePanel(Action change)
    {
        change();
        Dispatcher.BeginInvoke(new Action(RefreshCanvas), DispatcherPriority.Loaded);
    }
}
