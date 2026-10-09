using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class ThemeStudioTests
{
    private string _folder = "";
    [TestInitialize] public void Setup() { _folder = Path.Combine(Path.GetTempPath(), "Clearspace-editor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_folder); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_folder, true); }
    private ThemeInfo Info(string id) => new(id, "Custom") { Path = Path.Combine(_folder, id + ".zip") };

    [TestMethod]
    public void EditingReplacesExistingThemeAndCopyLeavesItAlone()
    {
        var original = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design);
        var id = original.Save(_folder, "Original");
        var edit = ThemeStudioDocument.Open(Info(id)).Color("Accent", "#123456");
        Assert.AreEqual(id, edit.Save(_folder, "Renamed", Info(id)));
        var package = ThemePackage.Open(Info(id).Path);
        Assert.AreEqual("Renamed", package.Manifest.Name);
        Assert.AreEqual(Color.FromRgb(18, 52, 86), ((SolidColorBrush)ThemeLoader.Load(package)["Accent"]).Color);
        var copy = edit.Color("Accent", "#FEDCBA").Save(_folder, "Copy");
        Assert.AreNotEqual(id, copy);
        Assert.AreEqual(2, Directory.GetFiles(_folder, "*.zip").Length);
        Assert.AreEqual(package.Markup, ThemePackage.Open(Info(id).Path).Markup);
    }

    [TestMethod]
    public void EditPreservesArtworkManifestAndLicenseFiles()
    {
        var original = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design).Gradient("#112233", "#556677", true);
        original = original with { Manifest = original.Manifest with { Icons = IconStyle.Neon, CrispText = true, Author = "Artist", FadeToolbar = true } };
        var id = original.Save(_folder, "Art");
        using (var zip = ZipFile.Open(Info(id).Path, ZipArchiveMode.Update))
        { using var writer = new StreamWriter(zip.CreateEntry("fonts/LICENSE.txt").Open()); writer.Write("Keep this license"); }
        var edit = ThemeStudioDocument.Open(Info(id)).Color("Ink", "#FFEEDD");
        edit.Save(_folder, "Art revised", Info(id));
        var package = ThemePackage.Open(Info(id).Path);
        Assert.AreEqual(IconStyle.Neon, package.Manifest.Icons);
        Assert.IsTrue(package.Manifest.CrispText && package.Manifest.FadeToolbar);
        Assert.AreEqual("Artist", package.Manifest.Author);
        Assert.IsInstanceOfType<LinearGradientBrush>(ThemeLoader.Load(package)["Base"]);
        using var saved = ZipFile.OpenRead(Info(id).Path);
        using var read = new StreamReader(saved.GetEntry("fonts/LICENSE.txt")!.Open());
        Assert.AreEqual("Keep this license", read.ReadToEnd());
    }

    [TestMethod]
    public void EditingFolderPreservesUnrelatedFiles()
    {
        var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[1].Design);
        var folder = Path.Combine(_folder, "Personal"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "theme.xaml"), doc.Markup);
        File.WriteAllText(Path.Combine(folder, "theme.json"), "{\"name\":\"Personal\"}");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "untouched");
        var info = new ThemeInfo("Personal", "Personal") { Path = folder };
        ThemeStudioDocument.Open(info).Color("Surface", "#ABCDEF").Save(_folder, "Personal edited", info);
        Assert.AreEqual("Personal edited", ThemePackage.Open(folder).Manifest.Name);
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(folder, "notes.txt")));
    }

    [TestMethod]
    public void BuiltInsAndOutsidePathsAreNeverOverwritten()
    {
        var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design);
        Assert.ThrowsException<ArgumentException>(() => doc.Save(_folder, "No", new ThemeInfo("Dark", "Dark") { Shipped = true, Path = Path.Combine(_folder, "Dark.zip") }));
        Assert.ThrowsException<ArgumentException>(() => doc.Save(_folder, "No", new ThemeInfo("Outside", "Outside") { Path = Path.Combine(_folder, "..", "Outside.zip") }));
        Assert.AreEqual(0, Directory.GetFiles(_folder).Length);
    }

    [TestMethod]
    public void ImportedAssetsAndLayoutSurviveEditingAndSaving()
    {
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        Assert.IsTrue(File.Exists(fontPath), "Windows font fixture must exist.");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aE1EAAAAASUVORK5CYII=");
        var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design)
            .ImportFont(File.ReadAllBytes(fontPath), ".ttf", "UIFont")
            .FileIcon(".PDF", true, png).FileIcon("Report.pdf", false, png).FileIcon("My_notes (final)-2.txt", false, png)
            .Layout("Explorer.SidebarOnRight", 1).Layout("Explorer.RowPadding", 7);
        var id = doc.Save(_folder, "Assets");
        var loaded = ThemeStudioDocument.Open(Info(id));
        Assert.AreEqual(1, loaded.Package.Fonts.Count);
        Assert.AreEqual(1, loaded.Package.Images.Count);
        var resources = loaded.Resources();
        Assert.AreEqual(System.Windows.Visibility.Visible, resources["Explorer.SidebarOnRight"]);
        Assert.AreEqual(7d, ((System.Windows.Thickness)resources["Explorer.RowPadding"]).Top);
        Assert.IsInstanceOfType<FontFamily>(resources["UIFont"]);
        Assert.IsInstanceOfType<ImageBrush>(resources["FileIcon.Name.my_notes (final)-2.txt"]);
        var item = new Clearspace.Models.FileSystemItem { Name = "Report.pdf", FullPath = @"C:\Report.pdf" };
        object? Read(string key) => resources.Contains(key) ? resources[key] : null;
        Assert.AreSame(((ImageBrush)resources["FileIcon.Name.report.pdf"]).ImageSource, ThemeFileIcons.Find(item, Read));
        var edited = loaded.Remove("FileIcon.Name.report.pdf");
        edited.Save(_folder, "Assets edited", Info(id));
        Assert.IsFalse(ThemeStudioDocument.Open(Info(id)).Resources().Contains("FileIcon.Name.report.pdf"));
        Assert.ThrowsException<ArgumentException>(() => doc.Layout("Explorer.RowPadding", double.NaN));
        var folder = Path.Combine(_folder, "FolderTheme"); Directory.CreateDirectory(folder);
        var basic = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design);
        File.WriteAllText(Path.Combine(folder, "theme.xaml"), basic.Markup);
        File.WriteAllText(Path.Combine(folder, "theme.json"), "{\"name\":\"Folder theme\"}");
        var info = new ThemeInfo("FolderTheme", "Folder theme") { Path = folder };
        doc.Save(_folder, "Folder with assets", info);
        var folderTheme = ThemeStudioDocument.Open(info);
        Assert.AreEqual(1, folderTheme.Package.Fonts.Count);
        Assert.AreEqual(1, folderTheme.Package.Images.Count);
        Assert.IsInstanceOfType<ImageBrush>(folderTheme.Resources()["FileIcon.Extension.pdf"]);
    }

    [TestMethod]
    public void StudioUsesPlainLabelsAndSharedFontsAndColors()
    {
        var colorKeys = ThemeKeys.All.Where(p => p.Value is ThemeValue.Brush or ThemeValue.FlatColor or ThemeValue.Color).Select(p => p.Key);
        // CHANGED (stickers): Decor.* hold pictures, placed with the Stickers card rather than picked as colours.
        foreach (var key in colorKeys.Where(k => !k.StartsWith("Decor.", StringComparison.Ordinal))) Assert.IsTrue(StudioColorOptions.All.Any(o => o.Key == key), key);
        Assert.IsFalse(StudioColorOptions.All.Any(o => o.Label.Contains("Ink") || o.Label.Contains("SelectedRow")));
        var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[0].Design).AllFonts("Georgia");
        foreach (var key in new[] { "UIFont", "SerifFont", "DisplayFont", "MonoFont" }) Assert.AreEqual("Georgia", ((FontFamily)doc.Resources()[key]).Source);
        var individual = doc.Font("SerifFont", "Arial");
        Assert.AreEqual("Georgia", ((FontFamily)individual.Resources()["UIFont"]).Source);
        Assert.AreEqual("Arial", ((FontFamily)individual.Resources()["SerifFont"]).Source);
        var name = StudioLibrary.ImportFont(File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")), ".ttf", _folder);
        Assert.IsTrue(StudioLibrary.Fonts(_folder).Any(f => f.Name == name), "Imported fonts must be discoverable in every font menu.");
        StudioLibrary.SaveColors(["#FF112233", "#FF112233", "#ABCDEF"], _folder);
        CollectionAssert.AreEqual(new[] { "#FF112233", "#ABCDEF" }, StudioLibrary.Colors(_folder));
        Assert.AreEqual(Colors.Red, Clearspace.Controls.StudioColorWheel.FromHsv(0, 1, 1));
        Assert.AreEqual(Colors.Lime, Clearspace.Controls.StudioColorWheel.FromHsv(120, 1, 1));
        Assert.AreEqual(Colors.Blue, Clearspace.Controls.StudioColorWheel.FromHsv(240, 1, 1));
        Assert.AreEqual(Colors.White, Clearspace.Controls.StudioColorWheel.FromHsv(10, 0, 1));
        Assert.AreEqual(Colors.Black, Clearspace.Controls.StudioColorWheel.FromHsv(10, 1, 0));
    }

    [TestMethod]
    public void CustomizedResourcesUseTheNormalThemeLoader()
    {
        var doc = ThemeStudioDocument.FromDesign(ThemeDesign.Presets[2].Design)
            .Color("Hover", "#40123456").Font("SerifFont", "Consolas").Corners(13).Gradient("#001122", "#334455", false);
        var preview = doc.Resources();
        var saved = ThemeLoader.Load(ThemePackage.Open(Info(doc.Save(_folder, "Full design")).Path));
        foreach (var key in new[] { "Hover", "SerifFont", "R8", "Base" })
            Assert.AreEqual(preview[key].ToString(), saved[key].ToString(), key);
        Assert.ThrowsException<ArgumentException>(() => doc.Color("UIFont", "#123456"));
        Assert.ThrowsException<ArgumentException>(() => doc.Color("Accent", "invalid"));
    }
}
