using System.IO;
using System.Globalization;
using System.Windows.Media;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class ThemeDesignTests
{
    [TestMethod]
    public void EveryMoodRoundTripsThroughTheRealLoader()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clearspace-studio-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, design) in ThemeDesign.Presets)
            {
                var id = design.Save(folder, name);
                var package = ThemePackage.Open(Path.Combine(folder, id + ".zip"));
                var loaded = ThemeLoader.Load(package);
                Assert.AreEqual(name, package.Manifest.Name);
                ThemeDesign.TryColor(design.Accent, out var expected);
                Assert.AreEqual(expected, ((SolidColorBrush)loaded["Accent"]).Color);
                var ink = ((SolidColorBrush)loaded["Ink"]).Color;
                var surface = ((SolidColorBrush)loaded["Surface"]).Color;
                Assert.IsTrue(ThemeDesign.Contrast(ink, surface) >= 4.5, name);
                Assert.IsTrue(ThemeDesign.Contrast(((SolidColorBrush)loaded["AccentInk"]).Color, expected) >= 4.5, name);
            }
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.tmp").Length);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void LabelsCannotEscapeFolderOrOverwriteAnotherTheme()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clearspace-studio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var design = ThemeDesign.Presets[0].Design;
            var first = design.Save(folder, "../Dark");
            var second = design.Save(folder, "../Dark");
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(2, Directory.GetFiles(folder, "*.zip").Length);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void MarkupUsesInvariantNumbersAndRejectsInvalidInput()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var design = new ThemeDesign("#123456", "#ABCDEF", Roundness: 3);
            var checkedMarkup = ThemeMarkup.Check(design.Markup(), null, _ => null);
            Assert.IsNotNull(ThemeLoader.Plain(checkedMarkup));
            Assert.ThrowsException<ArgumentException>(() => (design with { Accent = "{Binding}" }).Resources());
            Assert.ThrowsException<ArgumentException>(() => (design with { Roundness = double.NaN }).Resources());
            Assert.IsFalse(ThemeDesign.TryColor("#12345Z", out _));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
