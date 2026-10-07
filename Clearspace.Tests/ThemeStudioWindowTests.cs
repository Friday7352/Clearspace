using System.Windows;
using System.Windows.Media;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

// Called on the existing WPF test application's dispatcher: WPF permits one Application per process.
internal static class ThemeStudioWindowChecks
{
    internal static void Verify(App app)
    {
        var original = ThemeService.Current;
        var brush = ((SolidColorBrush)app.FindResource("Base")).Color;
        var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var editor = new ThemeStudioWindow();
        try
        {
            Assert.IsNotNull(editor.Content);
            var page = (FrameworkElement)editor.Content;
            page.Measure(new Size(872, 525)); page.Arrange(new Rect(0, 0, 872, 525)); page.UpdateLayout();
            using (var preview = new ThemeService.PreviewSession())
            {
                var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[1].Design).Color("Base", "#123456");
                preview.Show(doc.Package);
                Assert.AreEqual(Color.FromRgb(18, 52, 86), ((SolidColorBrush)app.FindResource("Base")).Color);
                Assert.AreEqual("studio-preview", ThemeService.Current);
            }
            Assert.AreEqual(original, ThemeService.Current);
            Assert.AreEqual(brush, ((SolidColorBrush)app.FindResource("Base")).Color);
        }
        finally { editor.Close(); app.ShutdownMode = shutdown; }
    }
}
