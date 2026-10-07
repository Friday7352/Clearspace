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
