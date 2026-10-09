// NEW (theme files): themes as files in the Themes folder (Services/ThemePackage.cs, ThemeMarkup.cs,
// ThemeLoader.cs, ThemeLibrary.cs).
//  - Every theme that comes with Clearspace loads from its source folder, uses only keys Clearspace knows,
//    and the list in ThemeLibrary.Shipped matches the folders. The Dark theme, which the app keeps
//    underneath every other theme, defines every key.
//  - theme.json: what may be left out, what is refused, and that a theme from a newer Clearspace says so.
//  - The zip: files at the top or one folder down; not a zip, no theme.json and no theme.xaml are refused.
//  - theme.xaml is checked before WPF sees it: anything that could run code, load another file or reach the
//    network is refused (bindings and x:Static, other namespaces, styles and controls, addresses in any
//    value); gradients, patterns, bundled fonts and bundled pictures are let through.
//  - A known key holding the wrong kind of value refuses the theme; unknown keys are dropped; a theme may
//    leave keys out. Plain themes are built without WPF's XAML reader and come out the same as with it.
//  - The Themes folder: zips and folders are listed, Clearspace's own first, a folder takes the place of a
//    zip of the same name, a broken theme is listed with its reason.
//  - Import: a good file is copied in, a bad one is not, an added theme of the same name is replaced only
//    when agreed to, and Clearspace's own themes are never replaced.
// Uses real files in a temporary folder that is removed again after each test. Nothing here opens a window.
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class ThemeTests
{
    private const string Open =
        "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";
    private const string Close = "</ResourceDictionary>";
    private const string Json = """{ "format": 1, "name": "Sunset", "author": "Ada" }""";

    private string _root = string.Empty;
    private string _elsewhere = string.Empty;   // stands in for Downloads: where a theme file comes from
    private string _oldThemes = string.Empty;
    private string _oldCache = string.Empty;

    [TestInitialize]
    public void MakeFolders()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clearspace themes " + Guid.NewGuid().ToString("N"));
        _elsewhere = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(_elsewhere);

        _oldThemes = ThemeLibrary.Folder;
        _oldCache = ThemeCache.Folder;
        ThemeLibrary.Folder = Path.Combine(_root, "Themes");
        ThemeCache.Folder = Path.Combine(_root, "Cache");
        Directory.CreateDirectory(ThemeLibrary.Folder);
    }

    [TestCleanup]
    public void RemoveFolders()
    {
        ThemeLibrary.Folder = _oldThemes;
        ThemeCache.Folder = _oldCache;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- helpers

    private static string Markup(string inside) => Open + inside + Close;

    // A theme .zip made of the given files ("theme.json", "fonts/a.ttf" ...). Null leaves a file out.
    private static string Zip(string folder, string name, string? json, string? markup, params (string Path, byte[] Data)[] more)
    {
        var path = Path.Combine(folder, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entryName, byte[] data)
        {
            using var stream = zip.CreateEntry(entryName).Open();
            stream.Write(data);
        }

        if (json is not null) Add("theme.json", Encoding.UTF8.GetBytes(json));
        if (markup is not null) Add("theme.xaml", Encoding.UTF8.GetBytes(markup));
        foreach (var (entry, data) in more) Add(entry, data);
        return path;
    }

    private string Sunset(string name = "Sunset", string? json = null) =>
        Zip(_elsewhere, name, json ?? Json, Markup("<SolidColorBrush x:Key=\"Accent\" Color=\"#FFFF8800\" />"));

    private static string Refused(string inside) =>
        Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(Markup(inside), null, _ => null)).Message;

    private static string Allowed(string inside) => ThemeMarkup.Prepare(Markup(inside), null, _ => null);

    private static string Workspace => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    // A 1 x 1 PNG.
    private static readonly byte[] Dot = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // ---------------------------------------------------------------- the themes that come with Clearspace

    [TestMethod]
    public void EveryThemeThatComesWithClearspaceLoads()
    {
        var sources = Path.Combine(Workspace, "Clearspace", "Themes");
        Assert.IsTrue(Directory.Exists(sources), sources);

        var folders = Directory.EnumerateDirectories(sources)
            .Where(folder => File.Exists(Path.Combine(folder, ThemePackage.ManifestName)))
            .ToArray();
        CollectionAssert.AreEquivalent(ThemeLibrary.Shipped, folders.Select(ThemePackage.NameOf).ToArray(),
            "ThemeLibrary.Shipped and the folders in Clearspace\\Themes must name the same themes");

        foreach (var folder in folders)
        {
            var name = ThemePackage.NameOf(folder);
            try
            {
                var package = ThemePackage.Open(folder);
                Assert.IsFalse(string.IsNullOrWhiteSpace(package.Manifest.Name), name);

                // Nothing the theme defines may be dropped as unknown: count the keys before and after.
                var written = System.Xml.Linq.XDocument.Parse(package.Markup).Root!.Elements().Count();
                var resources = ThemeLoader.Load(package);
                Assert.AreEqual(written, resources.Count, name + " defines a key Clearspace does not know");
                Assert.IsTrue(resources["Base"] is Brush, name);
                Assert.IsTrue(resources["Ink"] is SolidColorBrush { IsFrozen: true }, name);
            }
            catch (ThemeFormatException error)
            {
                Assert.Fail($"{name}: {error.Message}");
            }
        }
    }

    [TestMethod]
    public void PlainThemesAreBuiltJustAsWpfWouldBuildThem()
    {
        // A theme of flat colours, fonts and sizes is built without WPF's XAML reader, because that is much
        // quicker and this happens before the first window opens. Both ways must give the same resources.
        static string Show(object? value) => value switch
        {
            SolidColorBrush brush => $"brush {brush.Color} {brush.Opacity}",
            FontFamily family => "font " + family.Source,
            RotateTransform turn => "turn " + turn.Angle,
            null => "nothing",
            _ => value.GetType().Name + " " + value
        };

        var plainThemes = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(Path.Combine(Workspace, "Clearspace", "Themes")))
        {
            if (!File.Exists(Path.Combine(folder, ThemePackage.ManifestName)))
                continue;

            var package = ThemePackage.Open(folder);
            var assets = ThemeCache.Unpack(package);
            var markup = ThemeMarkup.Check(package.Markup, assets.FontsFolder, assets.Image);
            if (ThemeLoader.Plain(markup) is not { } quick)
                continue;

            var name = ThemePackage.NameOf(folder);
            plainThemes.Add(name);
            var slow = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(markup.ToString());
            CollectionAssert.AreEquivalent(slow.Keys.Cast<string>().ToArray(), quick.Keys.Cast<string>().ToArray(), name);
            foreach (string key in slow.Keys)
                Assert.AreEqual(Show(slow[key]), Show(quick[key]), $"{name}: {key}");
        }

        // The everyday themes are plain; the ones with gradients and textures are not.
        CollectionAssert.IsSubsetOf(new[] { "Dark", "Light", "OLED", "Retro", "Terminal", "Pixel" }, plainThemes);
        CollectionAssert.DoesNotContain(plainThemes, "Glass");
        CollectionAssert.DoesNotContain(plainThemes, "Sketchbook");

        // A bad value in a plain theme is reported with its line.
        var problem = Assert.ThrowsException<ThemeFormatException>(() => ThemeLoader.Load(ThemePackage.Open(
            Zip(_elsewhere, "Typo", Json, Markup("\n<SolidColorBrush x:Key=\"Base\" Color=\"#12\" />\n<CornerRadius x:Key=\"R8\">round</CornerRadius>"))))).Message;
        StringAssert.Contains(problem, "line 2");
        StringAssert.Contains(problem, "#12");
        StringAssert.Contains(
            Assert.ThrowsException<ThemeFormatException>(() => ThemeLoader.Load(ThemePackage.Open(
                Zip(_elsewhere, "Twice", Json, Markup("<Color x:Key=\"Theme.BaseColor\">Red</Color><Color x:Key=\"Theme.BaseColor\">Blue</Color>"))))).Message,
            "set twice");
    }

    [TestMethod]
    public void TheDarkThemeDefinesEveryKeyTheOthersCanLeaveOut()
    {
        // Dark is compiled into the app as the fallback under whichever theme is active, with Defaults.xaml
        // for the few extras. Between them every key must have a value.
        var themes = Path.Combine(Workspace, "Clearspace", "Themes");
        var dark = ThemeLoader.Load(ThemePackage.Open(Path.Combine(themes, "Dark")));
        var defaults = System.Xml.Linq.XDocument.Load(Path.Combine(themes, "Defaults.xaml")).Root!.Elements()
            .Select(element => element.Attribute(System.Xml.Linq.XName.Get("Key", ThemeMarkup.Xaml))!.Value)
            .ToHashSet();

        foreach (var key in ThemeKeys.All.Keys)
        {
            if (key.StartsWith("Theme.", StringComparison.Ordinal))
                continue;   // title bar and flat-background colours are optional everywhere
            Assert.IsTrue(dark.Contains(key) || defaults.Contains(key), key + " has no fallback value");
        }
    }

    [TestMethod]
    public void TheBuiltThemeFilesBesideTheAppLoadToo()
    {
        // The build zips each theme folder into Themes\<Name>.zip beside Clearspace.exe. A test run from the
        // repository finds them in the app's output folder when the app has been built.
        var built = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(Workspace, "Clearspace", "bin", configuration, "net10.0-windows", "win-x64", "Themes"))
            .FirstOrDefault(Directory.Exists);
        if (built is null)
            Assert.Inconclusive("Clearspace has not been built here, so there are no theme files to read.");

        var files = Directory.GetFiles(built, "*.zip");
        Assert.IsTrue(files.Length >= ThemeLibrary.Shipped.Length, $"{files.Length} theme files in {built}");
        foreach (var file in files.Where(file => ThemeLibrary.Shipped.Contains(ThemePackage.NameOf(file))))
            Assert.IsTrue(ThemeLoader.Load(ThemePackage.Open(file)).Contains("Base"), file);
    }

    // ---------------------------------------------------------------- theme.json

    [TestMethod]
    public void TheManifestGivesTheNameAndSwitches()
    {
        var manifest = ThemeManifest.Parse(
            """
            {
              // comments and a trailing comma are fine
              "format": 1,
              "name": "  Neon   HUD ",
              "author": "Ada",
              "description": "Bright.",
              "version": 1.2,
              "experimental": true,
              "icons": "neon",
              "fadeToolbar": true,
              "eInk": false,
              "crispText": true,
              "somethingFromALaterVersion": [1, 2, 3],
            }
            """, "file-name");

        Assert.AreEqual("Neon HUD", manifest.Name);
        Assert.AreEqual("Ada", manifest.Author);
        Assert.AreEqual("Bright.", manifest.Description);
        Assert.AreEqual("1.2", manifest.Version);
        Assert.IsTrue(manifest.Experimental);
        Assert.AreEqual(IconStyle.Neon, manifest.Icons);
        Assert.IsTrue(manifest.FadeToolbar);
        Assert.IsFalse(manifest.EInk);
        Assert.IsTrue(manifest.CrispText);
    }

    [TestMethod]
    public void AnEmptyManifestTakesTheFileNameAndOrdinaryValues()
    {
        var manifest = ThemeManifest.Parse("{}", "Sunset");

        Assert.AreEqual("Sunset", manifest.Name);
        Assert.AreEqual(IconStyle.System, manifest.Icons);
        Assert.IsFalse(manifest.Experimental || manifest.FadeToolbar || manifest.EInk || manifest.CrispText);
    }

    [TestMethod]
    public void AManifestThatIsWrongSaysWhatIsWrong()
    {
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("{ \"name\": ", "x")).Message, "not valid JSON");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("[]", "x")).Message, "{");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("{ \"icons\": \"Sparkly\" }", "x")).Message, "Sparkly");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("{ \"icons\": \"3\" }", "x")).Message, "icons");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("{ \"eInk\": \"yes\" }", "x")).Message, "true or false");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("{ \"name\": true }", "x")).Message, "name");
    }

    [TestMethod]
    public void AThemeMadeForANewerClearspaceSaysSo()
    {
        var error = Assert.ThrowsException<ThemeFormatException>(() => ThemeManifest.Parse("{ \"format\": 2 }", "x"));
        StringAssert.Contains(error.Message, "newer Clearspace");
    }

    // ---------------------------------------------------------------- the zip

    [TestMethod]
    public void AThemeIsReadFromTheTopOfTheZipOrOneFolderDown()
    {
        var flat = ThemePackage.Open(Sunset());
        Assert.AreEqual("Sunset", flat.Manifest.Name);
        StringAssert.Contains(flat.Markup, "Accent");

        // What Windows makes when you right-click the theme's folder and choose "Compress".
        var nested = Zip(_elsewhere, "Nested", null, null,
            ("My theme/theme.json", Encoding.UTF8.GetBytes(Json)),
            ("My theme/theme.xaml", Encoding.UTF8.GetBytes(Markup(string.Empty))),
            ("My theme/fonts/Some Font.ttf", [1, 2, 3]),
            ("My theme/fonts/licence.txt", [4]),
            ("My theme/images/paper.png", Dot),
            ("My theme/images/deeper/hidden.png", Dot));
        var package = ThemePackage.Open(nested);

        Assert.AreEqual("Sunset", package.Manifest.Name);
        Assert.AreEqual(1, package.Fonts.Count);            // the licence text is not a font
        Assert.AreEqual("Some Font.ttf", package.Fonts[0].FileName);
        CollectionAssert.AreEqual(new[] { "paper.png" }, package.Images.Keys.ToArray());
    }

    [TestMethod]
    public void WhatIsNotAThemeIsRefusedWithAReason()
    {
        var notAZip = Path.Combine(_elsewhere, "holiday.zip");
        File.WriteAllText(notAZip, "this is not a zip");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemePackage.Open(notAZip)).Message, "not a zip");

        var photos = Zip(_elsewhere, "photos", null, null, ("beach.png", Dot));
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemePackage.Open(photos)).Message, "theme.json");

        var noColours = Zip(_elsewhere, "half", Json, null);
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemePackage.Open(noColours)).Message, "theme.xaml");
        Assert.AreEqual("Sunset", ThemePackage.ReadManifest(noColours).Name);   // listing needs only theme.json

        // Two themes in one zip: which one is meant?
        var two = Zip(_elsewhere, "two", null, null,
            ("a/theme.json", Encoding.UTF8.GetBytes(Json)), ("b/theme.json", Encoding.UTF8.GetBytes(Json)));
        Assert.ThrowsException<ThemeFormatException>(() => ThemePackage.Open(two));
    }

    // ---------------------------------------------------------------- theme.xaml is checked first

    [TestMethod]
    public void OrdinaryThemeMarkupIsLetThrough()
    {
        var checkedMarkup = Allowed(
            """
            <!-- a comment -->
            <SolidColorBrush x:Key="Base" Color="#FF1A1917" Opacity="0.5" />
            <LinearGradientBrush x:Key="Hover" StartPoint="0,0" EndPoint="0,1">
                <GradientStop Color="#40FFFFFF" Offset="0" />
                <GradientStop Color="sc#1,0.5,0.5,0.5" Offset="1" />
            </LinearGradientBrush>
            <DrawingBrush x:Key="OverlayFill" TileMode="Tile" Viewport="0,0,3,3" ViewportUnits="Absolute">
                <DrawingBrush.Drawing>
                    <DrawingGroup RenderOptions.EdgeMode="Aliased">
                        <GeometryDrawing Brush="#22000000" Geometry="M0,0 L3,0 L3,1 L0,1 Z">
                            <GeometryDrawing.Pen><Pen Brush="Black" Thickness="1.5e-1" /></GeometryDrawing.Pen>
                        </GeometryDrawing>
                    </DrawingGroup>
                </DrawingBrush.Drawing>
            </DrawingBrush>
            <FontFamily x:Key="UIFont"> Segoe UI Variable Text , Segoe UI </FontFamily>
            <CornerRadius x:Key="RPlayerBar">0,0,11,11</CornerRadius>
            <Visibility x:Key="OverlayVisibility">Visible</Visibility>
            <Thickness x:Key="PageInset">26,0,0,0</Thickness>
            <RotateTransform x:Key="NoteTilt" Angle="-1.5" />
            <Color x:Key="Theme.BaseColor">#FF0F3F56</Color>
            """);

        Assert.IsFalse(checkedMarkup.Contains("a comment"));                          // comments are not passed on
        StringAssert.Contains(checkedMarkup, ">Segoe UI Variable Text, Segoe UI<");   // font lists are tidied
        Assert.IsNotNull(System.Windows.Markup.XamlReader.Parse(checkedMarkup) as ResourceDictionary);
    }

    [TestMethod]
    public void MarkupThatCouldRunCodeIsRefused()
    {
        // Anything in { } : bindings, static members, types.
        StringAssert.Contains(Refused("<SolidColorBrush x:Key=\"Base\" Color=\"{x:Static SystemColors.WindowColor}\" />"), "{ }");
        Refused("<SolidColorBrush x:Key=\"Base\" Color=\"{Binding Path=X}\" />");
        Refused("<SolidColorBrush x:Key=\"Base\" Color=\"{DynamicResource Ink}\" />");
        Refused("<SolidColorBrush x:Key=\"{x:Type Button}\" Color=\"Red\" />");

        // The classic: an object that calls a method while the file loads.
        StringAssert.Contains(Refused(
            "<ObjectDataProvider x:Key=\"Base\" ObjectType=\"{x:Type Process}\" MethodName=\"Start\" />"), "ObjectDataProvider");

        // Another namespace would let .NET types in by name.
        Refused("<s:String x:Key=\"Base\" xmlns:s=\"clr-namespace:System;assembly=System.Runtime\">x</s:String>");
        Refused("<SolidColorBrush x:Key=\"Base\" Color=\"Red\" xmlns:d=\"clr-namespace:System.Diagnostics;assembly=System.Diagnostics.Process\" />");
        Refused("<x:Code>System.Diagnostics.Process.Start(\"calc\");</x:Code>");
        Refused("<SolidColorBrush x:Key=\"Base\" x:FactoryMethod=\"System.Diagnostics.Process.Start\" Color=\"Red\" />");
        Refused("<SolidColorBrush x:Key=\"Base\" x:Name=\"b\" Color=\"Red\" />");

        // Styles, templates, controls, triggers, visuals: not a look, and a way in for event handlers.
        foreach (var part in new[] { "Style", "ControlTemplate", "DataTemplate", "Button", "Window", "VisualBrush", "Storyboard", "EventTrigger", "Frame", "WebBrowser" })
            StringAssert.Contains(Refused($"<{part} x:Key=\"Base\" />"), part);
        Refused("<DrawingBrush x:Key=\"Base\"><DrawingBrush.Drawing><ImageDrawing ImageSource=\"x.png\" /></DrawingBrush.Drawing></DrawingBrush>");

        // No document type, so no entities.
        var entity = "<!DOCTYPE d [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>" + Markup("<Color x:Key=\"Theme.BaseColor\">&e;</Color>");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(entity, null, _ => null)).Message, "theme.xaml");
    }

    [TestMethod]
    public void MarkupThatReachesOutsideTheThemeIsRefused()
    {
        // Other dictionaries.
        Refused("<ResourceDictionary x:Key=\"Base\" Source=\"pack://application:,,,/Other.xaml\" />");
        Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(
            Open.Replace(">", " Source=\"http://example.com/theme.xaml\">") + Close, null, _ => null));
        Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(
            Markup("<ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"C:\\x.xaml\" /></ResourceDictionary.MergedDictionaries>"), null, _ => null));

        // Addresses hiding in values: a network share (which would send the Windows sign-in to that server),
        // a web link, a file, a pack address, a colour profile.
        foreach (var address in new[] { @"\\server\share\x.png", "http://example.com/x.png", "file:///C:/x.png", "C:\\x.png", "pack://application:,,,/x.png", "../x.png", "images/../../x.png", "/x.png" })
        {
            Refused($"<ImageBrush x:Key=\"Base\" ImageSource=\"{address}\" />");
            Refused($"<SolidColorBrush x:Key=\"Base\" Color=\"{address}\" />");
        }
        Refused("<SolidColorBrush x:Key=\"Base\" Color=\"ContextColor file://server/profile.icc 1.0,0.5,0.5,0.5\" />");
        Refused("<SolidColorBrush x:Key=\"Base\" Color=\"ContextColor profile 1.0,0.5,0.5,0.5\" />");

        // Fonts: a name, or the theme's own. Never a path.
        Refused(@"<FontFamily x:Key=""UIFont"">\\server\share\#Evil</FontFamily>");
        Refused("<FontFamily x:Key=\"UIFont\">file:///C:/Windows/Fonts/#Arial</FontFamily>");
        Refused("<FontFamily x:Key=\"UIFont\">/Clearspace;component/Assets/Fonts/#Pixelify Sans</FontFamily>");
        Refused("<FontFamily x:Key=\"UIFont\">fonts/../../#Arial</FontFamily>");
        Refused("<FontFamily x:Key=\"UIFont\" Source=\"x\">Arial</FontFamily>");
    }

    [TestMethod]
    public void MarkupMistakesNameTheLine()
    {
        StringAssert.Contains(Refused("\n\n<SolidColorBrush Color=\"Red\" />"), "line 3");               // no x:Key
        StringAssert.Contains(Refused("<SolidColorBrush x:Key=\"Base\" Color=\"Red\">loose</SolidColorBrush>"), "loose text");
        StringAssert.Contains(Refused("<FontFamily x:Key=\"UIFont\">  </FontFamily>"), "empty");
        StringAssert.Contains(Refused("<SolidColorBrush x:Key=\"my key\" Color=\"Red\" />"), "my key");
        StringAssert.Contains(Refused("<SolidColorBrush x:Key=\"Base\" Color=\"Red\"><SolidColorBrush x:Key=\"Ink\" /></SolidColorBrush>"), "x:Key");
        StringAssert.Contains(
            Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(Open + "<SolidColorBrush", null, _ => null)).Message, "well-formed");
        StringAssert.Contains(
            Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare("<Window xmlns=\"" + ThemeMarkup.Presentation + "\" />", null, _ => null)).Message,
            "ResourceDictionary");

        // Nesting far deeper than any brush needs.
        var deep = string.Concat(Enumerable.Repeat("<DrawingGroup>", 40)) + string.Concat(Enumerable.Repeat("</DrawingGroup>", 40));
        StringAssert.Contains(Refused($"<DrawingBrush x:Key=\"Base\"><DrawingBrush.Drawing>{deep}</DrawingBrush.Drawing></DrawingBrush>"), "nested");
    }

    [TestMethod]
    public void BundledFontsAndPicturesPointIntoTheUnpackedTheme()
    {
        var markup = Markup(
            "<FontFamily x:Key=\"UIFont\">fonts/#Pixelify Sans, ./fonts/#Press Start 2P, Consolas</FontFamily>" +
            "<ImageBrush x:Key=\"Base\" ImageSource=\"images/paper (1).png\" Stretch=\"UniformToFill\" />");

        var ready = ThemeMarkup.Prepare(markup, "file:///C:/cache/fonts/", name => name == "paper (1).png" ? "file:///C:/cache/images/paper%20(1).png" : null);
        StringAssert.Contains(ready, ">file:///C:/cache/fonts/#Pixelify Sans, file:///C:/cache/fonts/#Press Start 2P, Consolas<");
        StringAssert.Contains(ready, "ImageSource=\"file:///C:/cache/images/paper%20(1).png\"");

        // Asking for a font or picture the theme does not carry is a mistake worth saying.
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(markup, null, _ => "x")).Message, "fonts folder");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeMarkup.Prepare(markup, "file:///C:/cache/fonts/", _ => null)).Message, "paper (1).png");
    }

    // ---------------------------------------------------------------- loading

    [TestMethod]
    public void AThemeMayLeaveKeysOutAndUnknownKeysAreDropped()
    {
        var file = Zip(_elsewhere, "Tiny", Json, Markup(
            "<SolidColorBrush x:Key=\"Accent\" Color=\"#FFFF8800\" />" +
            "<SolidColorBrush x:Key=\"SelectedRow.Ink\" Color=\"White\" />" +
            "<SolidColorBrush x:Key=\"KeyFromALaterClearspace\" Color=\"Red\" />" +
            "<SolidColorBrush x:Key=\"IconFont\" Color=\"Red\" />"));

        var resources = ThemeLoader.Load(ThemePackage.Open(file));

        Assert.AreEqual(2, resources.Count);
        Assert.AreEqual(Color.FromRgb(0xFF, 0x88, 0x00), ((SolidColorBrush)resources["Accent"]).Color);
        Assert.IsTrue(((SolidColorBrush)resources["Accent"]).IsFrozen);
        Assert.IsTrue(resources.Contains("SelectedRow.Ink"));
    }

    [TestMethod]
    public void AKeyHoldingTheWrongKindOfThingRefusesTheTheme()
    {
        // A brush where a corner size belongs would stop every window from drawing.
        string Problem(string inside) =>
            Assert.ThrowsException<ThemeFormatException>(() => ThemeLoader.Load(ThemePackage.Open(Zip(_elsewhere, Guid.NewGuid().ToString("N"), Json, Markup(inside))))).Message;

        StringAssert.Contains(Problem("<SolidColorBrush x:Key=\"R8\" Color=\"Red\" />"), "R8 must be a CornerRadius");
        StringAssert.Contains(Problem("<CornerRadius x:Key=\"Base\">4</CornerRadius>"), "Base must be a brush");
        StringAssert.Contains(Problem("<Color x:Key=\"UIFont\">Red</Color>"), "UIFont must be a FontFamily");
        StringAssert.Contains(Problem("<SolidColorBrush x:Key=\"Theme.BaseColor\" Color=\"Red\" />"), "Theme.BaseColor must be a Color");
        StringAssert.Contains(Problem("<Thickness x:Key=\"OverlayVisibility\">1</Thickness>"), "OverlayVisibility");
        StringAssert.Contains(Problem("<CornerRadius x:Key=\"SelectedRow.Ink\">4</CornerRadius>"), "SelectedRow.Ink");

        // Code reads the colour out of these, so they must be one flat colour.
        StringAssert.Contains(
            Problem("<LinearGradientBrush x:Key=\"Ink\"><GradientStop Color=\"Red\" Offset=\"0\" /></LinearGradientBrush>"), "Ink must be one flat color");

        // A value WPF cannot read.
        StringAssert.Contains(Problem("<SolidColorBrush x:Key=\"Base\" Color=\"#GGHHII\" />"), "cannot read");
        StringAssert.Contains(Problem("<SolidColorBrush x:Key=\"Base\" Colour=\"Red\" />"), "cannot read");
    }

    [TestMethod]
    public void FontsAndPicturesAreUnpackedOnceAndUsed()
    {
        var workspaceFonts = Path.Combine(Workspace, "Clearspace", "Themes", "Pixel", "fonts", "PixelifySans.ttf");
        var font = File.Exists(workspaceFonts) ? File.ReadAllBytes(workspaceFonts) : [0, 1, 0, 0];
        var file = Zip(_elsewhere, "Carried", Json, Markup(
                "<FontFamily x:Key=\"UIFont\">fonts/#Pixelify Sans, Consolas</FontFamily>" +
                "<ImageBrush x:Key=\"Base\" ImageSource=\"images/dot.png\" TileMode=\"Tile\" />"),
            ("fonts/Pixelify Sans!.ttf", font), ("images/dot.png", Dot));

        var resources = ThemeLoader.Load(ThemePackage.Open(file));

        var family = (FontFamily)resources["UIFont"];
        StringAssert.StartsWith(family.Source, "file:///");
        StringAssert.Contains(family.Source, "/fonts/#Pixelify Sans, Consolas");
        Assert.IsTrue(resources["Base"] is ImageBrush { ImageSource: not null });

        var unpacked = Directory.GetFiles(ThemeCache.Folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToArray();
        CollectionAssert.AreEqual(new[] { "dot.png", "font01.ttf" }, unpacked);

        // Loading it again (a colour was changed, say) uses the same copies rather than writing new ones.
        var before = Directory.GetDirectories(ThemeCache.Folder).Single();
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(before, "fonts", "font01.ttf"));
        ThemeLoader.Load(ThemePackage.Open(file));
        Assert.AreEqual(before, Directory.GetDirectories(ThemeCache.Folder).Single());
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(before, "fonts", "font01.ttf")));
    }

    // ---------------------------------------------------------------- the Themes folder

    [TestMethod]
    public void TheFolderListsItsThemesWithClearspacesOwnFirst()
    {
        Zip(ThemeLibrary.Folder, "Zebra", """{ "name": "Zebra" }""", Markup(string.Empty));
        Zip(ThemeLibrary.Folder, "apple", """{ "name": "Apple", "author": "Ada", "description": "Green." }""", Markup(string.Empty));
        Zip(ThemeLibrary.Folder, "Light", """{ "name": "Light" }""", Markup(string.Empty));
        Zip(ThemeLibrary.Folder, "Dark", """{ "name": "Dark" }""", Markup(string.Empty));
        Zip(ThemeLibrary.Folder, "Pixel", """{ "name": "Pixel", "experimental": true, "icons": "Pixel", "crispText": true }""", Markup(string.Empty));
        File.WriteAllText(Path.Combine(ThemeLibrary.Folder, "notes.txt"), "not a theme");
        Directory.CreateDirectory(Path.Combine(ThemeLibrary.Folder, "scraps"));   // a folder without theme.json is not a theme

        var themes = ThemeLibrary.Refresh();

        CollectionAssert.AreEqual(new[] { "Dark", "Light", "Pixel", "apple", "Zebra" }, themes.Select(theme => theme.Name).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, true, false, false }, themes.Select(theme => theme.Shipped).ToArray());

        var pixel = themes[2];
        Assert.IsTrue(pixel.Experimental && pixel.CrispText);
        Assert.AreEqual(IconStyle.Pixel, pixel.Icons);

        var apple = ThemeLibrary.Find("APPLE")!;
        Assert.AreEqual("Apple", apple.Label);
        Assert.AreEqual("Ada", apple.Author);
        Assert.AreEqual("Green.", apple.Description);
        Assert.IsNull(apple.Problem);
        Assert.IsNull(ThemeLibrary.Find("Nowhere"));
    }

    [TestMethod]
    public void AFolderTakesThePlaceOfAZipWithTheSameName()
    {
        Zip(ThemeLibrary.Folder, "Sunset", """{ "name": "From the zip" }""", Markup(string.Empty));
        var folder = Path.Combine(ThemeLibrary.Folder, "Sunset");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "theme.json"), """{ "name": "From the folder" }""");
        File.WriteAllText(Path.Combine(folder, "theme.xaml"), Markup("<SolidColorBrush x:Key=\"Accent\" Color=\"Red\" />"));

        // CHANGED (themes page): picked out by name; Dark is always in the list as well.
        var theme = ThemeLibrary.Refresh().Single(listed => listed.Name == "Sunset");
        Assert.AreEqual("From the folder", theme.Label);
        Assert.AreEqual(folder, theme.Path);
        Assert.IsTrue(ThemeLoader.Load(ThemePackage.Open(theme.Path)).Contains("Accent"));

        // Saving the folder's files again is noticed (this is what shows a theme again while it is being made).
        var before = theme.Stamp;
        File.WriteAllText(Path.Combine(folder, "theme.xaml"), Markup("<SolidColorBrush x:Key=\"Accent\" Color=\"#FF00FF00\" />"));
        Assert.AreNotEqual(before, ThemeLibrary.Refresh().Single(listed => listed.Name == "Sunset").Stamp);
    }

    [TestMethod]
    public void AThemeThatCannotBeReadIsListedWithItsReason()
    {
        File.WriteAllText(Path.Combine(ThemeLibrary.Folder, "Broken.zip"), "not a zip");
        Zip(ThemeLibrary.Folder, "Future", """{ "format": 99 }""", Markup(string.Empty));

        var themes = ThemeLibrary.Refresh();

        Assert.AreEqual(3, themes.Count); // CHANGED (themes page): was 2; Dark is always listed too
        StringAssert.Contains(themes.Single(theme => theme.Name == "Broken").Problem, "not a zip");
        StringAssert.Contains(themes.Single(theme => theme.Name == "Future").Problem, "newer Clearspace");
    }

    // ---------------------------------------------------------------- NEW (themes page): Dark is always there

    [TestMethod]
    public void DarkIsListedEvenWhenTheFolderIsEmptyOrGone()
    {
        var themes = ThemeLibrary.Refresh();

        Assert.AreEqual(1, themes.Count);
        Assert.AreEqual("Dark", themes[0].Name);
        Assert.IsTrue(themes[0].BuiltIn && themes[0].Shipped);
        Assert.IsNull(themes[0].Problem);
        Assert.AreEqual(string.Empty, themes[0].Path, "the built-in Dark has no file");
        Assert.IsTrue(ThemeLibrary.Find("dark")!.BuiltIn);

        Directory.Delete(ThemeLibrary.Folder, recursive: true);
        Assert.AreEqual("Dark", ThemeLibrary.Refresh().Single().Name);
        Assert.IsTrue(ThemeLibrary.Find("Dark")!.BuiltIn);
    }

    [TestMethod]
    public void DarkIsReadFromItsFileWhileTheFileIsThereAndBuiltInOnceItIsNot()
    {
        var file = Zip(ThemeLibrary.Folder, "Dark", """{ "name": "Dark", "description": "From the file." }""", Markup(string.Empty));
        Zip(ThemeLibrary.Folder, "Light", """{ "name": "Light" }""", Markup(string.Empty));

        var dark = ThemeLibrary.Refresh()[0];
        Assert.AreEqual("Dark", dark.Name);
        Assert.IsFalse(dark.BuiltIn);
        Assert.AreEqual(file, dark.Path);
        Assert.AreEqual("From the file.", dark.Description);

        // Somebody deletes Dark.zip: Dark is still the first theme, now the one built into the app.
        File.Delete(file);
        var themes = ThemeLibrary.Refresh();
        CollectionAssert.AreEqual(new[] { "Dark", "Light" }, themes.Select(theme => theme.Name).ToArray());
        Assert.IsTrue(themes[0].BuiltIn);
        Assert.IsTrue(ThemeLibrary.Find("Dark")!.BuiltIn);

        // And when the file is put back, it is read again (also by Find alone, before the folder is listed again).
        Zip(ThemeLibrary.Folder, "Dark", """{ "name": "Dark" }""", Markup(string.Empty));
        Assert.IsFalse(ThemeLibrary.Find("Dark")!.BuiltIn);
        Assert.IsFalse(ThemeLibrary.Refresh()[0].BuiltIn);
    }

    [TestMethod]
    public void ADarkFileThatCannotBeReadDoesNotTakeDarkAway()
    {
        File.WriteAllText(Path.Combine(ThemeLibrary.Folder, "Dark.zip"), "not a zip");

        var dark = ThemeLibrary.Refresh().Single();
        Assert.IsTrue(dark.BuiltIn);
        Assert.IsNull(dark.Problem, "Dark is never listed as a theme that cannot be used");
        Assert.IsTrue(ThemeLibrary.Find("Dark")!.BuiltIn);
    }

    [TestMethod]
    public void EveryThemeCanBeRemovedExceptDark()
    {
        Zip(ThemeLibrary.Folder, "Dark", """{ "name": "Dark" }""", Markup(string.Empty));
        Zip(ThemeLibrary.Folder, "Light", """{ "name": "Light" }""", Markup(string.Empty));
        ThemeLibrary.Import(Sunset());
        ThemeLibrary.Refresh();

        var dark = ThemeLibrary.Find("Dark")!;
        Assert.IsFalse(ThemeService.CanRemove(dark), "Dark's file is there, and it still cannot be removed");
        Assert.IsFalse(ThemeService.CanRemove(ThemeLibrary.BuiltInDark));
        Assert.IsTrue(ThemeService.CanRemove(ThemeLibrary.Find("Light")!));
        Assert.IsTrue(ThemeService.CanRemove(ThemeLibrary.Find("Sunset")!));

        // Dark is refused before anything is deleted.
        var asked = new List<string>();
        bool Delete(string path) { asked.Add(path); File.Delete(path); return true; }
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeLibrary.Remove(dark, Delete)).Message, "part of Clearspace");
        Assert.ThrowsException<ThemeFormatException>(() => ThemeLibrary.Remove(ThemeLibrary.BuiltInDark, Delete));
        Assert.AreEqual(0, asked.Count);
        Assert.IsTrue(File.Exists(dark.Path));

        // Answering No to the question Windows asks removes nothing.
        var sunset = ThemeLibrary.Find("Sunset")!;
        Assert.IsFalse(ThemeLibrary.Remove(sunset, _ => false));
        Assert.IsNotNull(ThemeLibrary.Find("Sunset"));
        Assert.IsTrue(File.Exists(sunset.Path));

        // Removed: gone from the folder and from the list. One of Clearspace's own can go as well.
        Assert.IsTrue(ThemeLibrary.Remove(sunset, Delete));
        Assert.IsTrue(ThemeLibrary.Remove(ThemeLibrary.Find("Light")!, Delete));
        CollectionAssert.AreEqual(new[] { "Dark" }, ThemeLibrary.Themes.Select(theme => theme.Name).ToArray());
        Assert.AreEqual(2, asked.Count);

        // A theme whose file has gone already counts as removed, without asking for anything to be deleted.
        Assert.IsTrue(ThemeLibrary.Remove(sunset, _ => throw new InvalidOperationException("nothing to delete")));
    }

    [TestMethod]
    public void ImportingAThemeCalledDarkNeverTakesDarksPlace()
    {
        // Dark's own file is not in the folder: Dark is the built-in one, and still nothing may be called Dark.
        ThemeLibrary.Refresh();
        var imported = ThemeLibrary.Import(Sunset("Dark", """{ "name": "My dark" }"""), _ => true);

        Assert.AreEqual("Dark (2)", imported.Name);
        Assert.IsFalse(File.Exists(Path.Combine(ThemeLibrary.Folder, "Dark.zip")));
        Assert.IsTrue(ThemeLibrary.Find("Dark")!.BuiltIn);
        Assert.IsTrue(ThemeService.CanRemove(imported));
    }

    // ---------------------------------------------------------------- importing

    [TestMethod]
    public void ImportingCopiesAGoodThemeIntoTheFolder()
    {
        var theme = ThemeLibrary.Import(Sunset());

        Assert.AreEqual("Sunset", theme.Name);
        Assert.IsFalse(theme.Shipped);
        Assert.IsTrue(File.Exists(Path.Combine(ThemeLibrary.Folder, "Sunset.zip")));
        Assert.AreEqual(Path.Combine(ThemeLibrary.Folder, "Sunset.zip"), theme.Path);
        Assert.IsTrue(File.Exists(Path.Combine(_elsewhere, "Sunset.zip")), "the file that was imported stays where it was");

        // Importing the copy that is already in the folder changes nothing.
        Assert.AreEqual("Sunset", ThemeLibrary.Import(theme.Path).Name);
        Assert.AreEqual(1, Directory.GetFiles(ThemeLibrary.Folder).Length);
    }

    [TestMethod]
    public void ImportingAThemeThatWouldNotShowAddsNothing()
    {
        var wrongKind = Zip(_elsewhere, "Wrong", Json, Markup("<SolidColorBrush x:Key=\"R8\" Color=\"Red\" />"));
        var unsafeMarkup = Zip(_elsewhere, "Unsafe", Json, Markup("<Style x:Key=\"Base\" />"));
        var notATheme = Path.Combine(_elsewhere, "picture.png");
        File.WriteAllBytes(notATheme, Dot);

        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeLibrary.Import(wrongKind)).Message, "R8");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeLibrary.Import(unsafeMarkup)).Message, "Style");
        StringAssert.Contains(Assert.ThrowsException<ThemeFormatException>(() => ThemeLibrary.Import(notATheme)).Message, ".zip");
        Assert.ThrowsException<ThemeFormatException>(() => ThemeLibrary.Import(Path.Combine(_elsewhere, "gone.zip")));

        Assert.AreEqual(0, Directory.GetFileSystemEntries(ThemeLibrary.Folder).Length);
    }

    [TestMethod]
    public void ImportingANameThatIsTakenReplacesOnlyWhenAgreed()
    {
        ThemeLibrary.Import(Sunset(json: """{ "name": "First" }"""));
        File.Delete(Path.Combine(_elsewhere, "Sunset.zip"));

        // Not agreed (or not asked): both are kept.
        var second = ThemeLibrary.Import(Sunset(json: """{ "name": "Second" }"""), _ => false);
        Assert.AreEqual("Sunset (2)", second.Name);
        Assert.AreEqual("First", ThemeLibrary.Find("Sunset")!.Label);
        File.Delete(Path.Combine(_elsewhere, "Sunset.zip"));

        // Agreed: the one in the folder is replaced.
        ThemeInfo? asked = null;
        var third = ThemeLibrary.Import(Sunset(json: """{ "name": "Third" }"""), existing => { asked = existing; return true; });
        Assert.AreEqual("Sunset", third.Name);
        Assert.AreEqual("Third", third.Label);
        Assert.AreEqual("First", asked!.Label);
        Assert.AreEqual(2, Directory.GetFiles(ThemeLibrary.Folder).Length);
    }

    [TestMethod]
    public void ImportingNeverReplacesAThemeThatCameWithClearspace()
    {
        Zip(ThemeLibrary.Folder, "Dark", """{ "name": "Dark" }""", Markup(string.Empty));
        var original = File.ReadAllBytes(Path.Combine(ThemeLibrary.Folder, "Dark.zip"));

        var asked = false;
        var imported = ThemeLibrary.Import(Sunset("Dark", """{ "name": "My dark" }"""), _ => { asked = true; return true; });

        Assert.IsFalse(asked);
        Assert.AreEqual("Dark (2)", imported.Name);
        Assert.IsFalse(imported.Shipped);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(ThemeLibrary.Folder, "Dark.zip")));
    }
}
