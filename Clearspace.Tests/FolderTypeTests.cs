using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

// NEW (folder types, step 1): the definitions reproduce the old hard-coded behavior, and custom types
// are built from saved data. (Assignments and inheritance live in the user's settings file, so they are
// covered by the manual check rather than by tests that would write to it.)
// CHANGED (folder types, step 2): the six new types, their behaviors, date groups and Git parsing.
// CHANGED (folder types, step 3): content detection, a reason for every type, Desktop groups, download
// sources and PDF properties.
[TestClass]
public sealed class FolderTypeTests
{
    [TestMethod]
    public void BuiltInTypesKeepTheirOldLayoutsColumnsAndIds()
    {
        Assert.AreEqual(LayoutMode.Grid, FolderTypes.ForProfile(DirectoryViewProfile.Photos).Layout);
        Assert.AreEqual(LayoutMode.Grid, FolderTypes.ForProfile(DirectoryViewProfile.Videos).Layout);
        foreach (var profile in new[] { DirectoryViewProfile.General, DirectoryViewProfile.Music, DirectoryViewProfile.Desktop,
                     DirectoryViewProfile.Documents, DirectoryViewProfile.Downloads })
            Assert.AreEqual(LayoutMode.Details, FolderTypes.ForProfile(profile).Layout, profile.ToString());
        Assert.IsNull(FolderTypes.Automatic.Layout, "Automatic decides per folder.");

        CollectionAssert.AreEqual(ColumnCatalog.DefaultsFor(DirectoryViewProfile.Music).ToArray(),
            FolderTypes.DefaultColumns(FolderTypes.ForProfile(DirectoryViewProfile.Music), false).ToArray());
        CollectionAssert.AreEqual(ColumnCatalog.DefaultsFor(DirectoryViewProfile.General, true).ToArray(),
            FolderTypes.DefaultColumns(FolderTypes.General, true).ToArray(), "Cloud folders keep their status column.");

        // Saved settings store these IDs; they must not change.
        foreach (var profile in Enum.GetValues<DirectoryViewProfile>())
            Assert.AreEqual(profile.ToString(), FolderTypes.ForProfile(profile).Id);
    }

    [TestMethod]
    public void BuiltInsAreFoundByIdOrNameIgnoringCase()
    {
        Assert.AreEqual("Photos", FolderTypes.Find("photos")!.Id);
        Assert.AreEqual("Music", FolderTypes.Find("MUSIC")!.Id);
        Assert.AreEqual("Design", FolderTypes.Find("design & 3d")!.Id);          // NEW (step 2)
        Assert.AreEqual("Archives", FolderTypes.Find("Archives & Backups")!.Id); // NEW (step 2)
        Assert.IsNull(FolderTypes.Find("999"));
        Assert.IsNull(FolderTypes.Find(""));
    }

    [TestMethod]
    public void CustomTypesComeFromSavedDataAndFallBackSafely()
    {
        var school = FolderTypes.FromData(new CustomFolderTypeData
        {
            Id = "custom:school", Name = "School", Base = "Documents", Layout = "Grid",
            Columns = ["name", "datemodified", "not-a-column", "name"], Subfolders = true
        });
        Assert.IsTrue(school.IsCustom);
        Assert.AreEqual(DirectoryViewProfile.Documents, school.Base);
        Assert.AreEqual(LayoutMode.Grid, school.Layout);
        CollectionAssert.AreEqual(new[] { "name", "datemodified" }, FolderTypes.DefaultColumns(school, false).ToArray(),
            "Unknown and repeated columns are dropped.");
        Assert.IsTrue(school.SubfoldersByDefault);
        CollectionAssert.Contains(FolderTypes.Words(school).ToArray(), "School");

        var damaged = FolderTypes.FromData(new CustomFolderTypeData { Id = "custom:x", Name = "X", Base = "Automatic", Layout = "Sideways" });
        Assert.AreEqual(DirectoryViewProfile.General, damaged.Base, "A custom type is never Automatic.");
        Assert.IsNull(damaged.Layout);
        CollectionAssert.AreEqual(ColumnCatalog.DefaultsFor(DirectoryViewProfile.General).ToArray(), FolderTypes.DefaultColumns(damaged, false).ToArray());
    }

    [TestMethod]
    public void AutomaticLabelsNameTheDetectedType()
    {
        Assert.AreEqual("Auto (Photos)", FolderTypes.Label(FolderTypes.Automatic, @"C:\Users\x\Vacation Photos"));
        Assert.AreEqual("Auto", FolderTypes.Label(FolderTypes.Automatic, @"C:\Work"));
        // CHANGED (step 2): screenshot folders are their own type now. CHANGED (step 3): labels read "Auto (...)".
        Assert.AreEqual("Screenshots", FolderTypes.Detected(@"C:\Users\x\Screenshots").Id);
        Assert.AreEqual("Screenshots", FolderTypes.Detected(@"C:\Users\x\Pictures\Screen Captures").Id);
        Assert.AreEqual("Photos", FolderTypes.Detected(@"C:\Users\x\Camera Roll").Id);
        Assert.AreEqual("General", FolderTypes.Detected(@"C:\Work").Id);
    }

    // ------------------------------------------------------------------ NEW (step 2)

    [TestMethod]
    public void NewTypesCarryTheirBehaviors()
    {
        var screenshots = FolderTypes.ForProfile(DirectoryViewProfile.Screenshots);
        Assert.AreEqual(SortColumn.DateModified, screenshots.Sort);
        Assert.IsTrue(screenshots.SortDescending);
        Assert.IsTrue(screenshots.GroupByDate);

        var code = FolderTypes.ForProfile(DirectoryViewProfile.Code);
        Assert.IsTrue(code.DimGenerated);
        CollectionAssert.Contains(FolderTypes.DefaultColumns(code, false).ToArray(), "git");

        Assert.IsTrue(FolderTypes.ForProfile(DirectoryViewProfile.Projects).ShowProjectStrip);
        Assert.AreEqual(1.6, FolderTypes.ForProfile(DirectoryViewProfile.Design).TileScale);
        CollectionAssert.Contains(FolderTypes.DefaultColumns(FolderTypes.ForProfile(DirectoryViewProfile.Design), false).ToArray(), "dimensions");
        Assert.IsTrue(FolderTypes.ForProfile(DirectoryViewProfile.Archives).RankLower);

        // Every default column set only names real columns (a missing column would silently vanish).
        foreach (var profile in Enum.GetValues<DirectoryViewProfile>())
            foreach (var id in ColumnCatalog.DefaultsFor(profile))
                Assert.IsNotNull(ColumnCatalog.Find(id), $"{profile}: {id}");
    }

    [TestMethod]
    public void CustomTypesKeepTheirBaseBehaviorsAndSavedSort()
    {
        var receipts = FolderTypes.FromData(new CustomFolderTypeData
        {
            Id = "custom:receipts", Name = "Receipts", Base = "Screenshots", Sort = "Name", SortDescending = false, TileScale = 1.3
        });
        Assert.IsTrue(receipts.GroupByDate, "Behaviors come from the base type.");
        Assert.AreEqual(SortColumn.Name, receipts.Sort, "The saved sort wins.");
        Assert.AreEqual(1.3, receipts.TileScale);

        var backups = FolderTypes.FromData(new CustomFolderTypeData { Id = "custom:backups", Name = "Old backups", Base = "Archives" });
        Assert.IsTrue(backups.RankLower);
        Assert.AreEqual(SortColumn.DateModified, backups.Sort, "No saved sort keeps the base type's.");
        Assert.IsTrue(backups.SortDescending);
    }

    [TestMethod]
    public void DateGroupsReadLikeExplorer()
    {
        var now = new DateTime(2026, 9, 30, 15, 0, 0); // a Wednesday
        Assert.AreEqual("Today", FileSystemItem.DateGroupFor(now.AddHours(-3), now));
        Assert.AreEqual("Yesterday", FileSystemItem.DateGroupFor(now.AddDays(-1), now));
        Assert.AreEqual("Earlier this month", FileSystemItem.DateGroupFor(new DateTime(2026, 9, 3), now));
        Assert.AreEqual("Last month", FileSystemItem.DateGroupFor(new DateTime(2026, 8, 12), now));
        Assert.AreEqual("Earlier this year", FileSystemItem.DateGroupFor(new DateTime(2026, 2, 1), now));
        Assert.AreEqual("2024", FileSystemItem.DateGroupFor(new DateTime(2024, 5, 5), now));
        Assert.AreEqual("Date unknown", FileSystemItem.DateGroupFor(DateTime.MinValue, now));
    }

    [TestMethod]
    public void GitStatusIsParsedFromPorcelainOutput()
    {
        var output = " M src/app.cs\0?? notes/\0R  new name.txt\0old name.txt\0A  lib/deep/x.cs\0UU merge.txt\0";
        var (files, folders, count) = GitService.ParsePorcelain(output);

        Assert.AreEqual(5, count);
        Assert.AreEqual('M', files["src/app.cs"]);
        Assert.AreEqual('?', files["notes"]);
        Assert.AreEqual('R', files["new name.txt"]);
        Assert.IsFalse(files.ContainsKey("old name.txt"), "A rename's original path is not a change of its own.");
        Assert.AreEqual('A', files["lib/deep/x.cs"]);
        Assert.AreEqual('U', files["merge.txt"]);
        Assert.IsTrue(folders.Contains("lib") && folders.Contains("lib/deep") && folders.Contains("src"));

        var snapshot = new GitSnapshot(@"C:\Repo", "main", files, folders, count, HasStatus: true);
        Assert.AreEqual('M', GitService.StatusOf(snapshot, @"C:\Repo\src\app.cs", isFolder: false));
        Assert.AreEqual('C', GitService.StatusOf(snapshot, @"C:\Repo\lib", isFolder: true));
        Assert.AreEqual('?', GitService.StatusOf(snapshot, @"C:\Repo\notes", isFolder: true));
        Assert.AreEqual('\0', GitService.StatusOf(snapshot, @"C:\Repo\README.md", isFolder: false));
        Assert.AreEqual('\0', GitService.StatusOf(snapshot, @"C:\Elsewhere\a.cs", isFolder: false));

        Assert.AreEqual("main", GitService.ParseHead("ref: refs/heads/main"));
        Assert.AreEqual("feature/x", GitService.ParseHead("ref: refs/heads/feature/x"));
        Assert.AreEqual("a1b2c3d (detached)", GitService.ParseHead("a1b2c3d4e5f6a7b8"));
    }

    [TestMethod]
    public void GeneratedFoldersAreRecognized()
    {
        foreach (var name in new[] { "bin", "obj", "node_modules", ".git", "__pycache__", "Target" })
            Assert.IsTrue(GeneratedFolders.IsGenerated(name), name);
        foreach (var name in new[] { "src", "docs", "binary" })
            Assert.IsFalse(GeneratedFolders.IsGenerated(name), name);
    }
    // ------------------------------------------------------------------ NEW (step 3)

    private static FileSystemItem File(string name) => SearchCoordinatorTests.Item(@"C:\F\" + name);

    [TestMethod]
    public void AutomaticRecognizesAFolderByWhatItContains()
    {
        DirectoryViewProfile? Guess(params string[] names)
            => AutomaticFolderTypeDetector.FromContents(names.Select(File).ToArray());

        Assert.AreEqual(DirectoryViewProfile.Photos, Guess("a.jpg", "b.jpg", "c.png", "notes.txt"));
        Assert.AreEqual(DirectoryViewProfile.Screenshots, Guess("Screenshot 2026-09-01.png", "Screenshot 2026-09-02.png", "Screen Shot 1.png"));
        Assert.AreEqual(DirectoryViewProfile.Music, Guess("1.mp3", "2.mp3", "3.flac", "cover.jpg"));
        Assert.AreEqual(DirectoryViewProfile.Videos, Guess("a.mp4", "b.mkv", "c.mov"));
        Assert.AreEqual(DirectoryViewProfile.Archives, Guess("2019.zip", "2020.zip", "db.bak"));
        Assert.AreEqual(DirectoryViewProfile.Design, Guess("robot.blend", "render1.png", "render2.png", "base.stl"));
        Assert.AreEqual(DirectoryViewProfile.Documents, Guess("a.pdf", "b.docx", "c.xlsx"));
        Assert.IsNull(Guess("a.jpg", "b.mp3", "c.zip", "d.exe"), "A mix is nothing in particular.");
        Assert.IsNull(Guess("a.jpg", "b.jpg"), "Too few files to tell.");
    }

    [TestMethod]
    public void EveryTypeDoesSomethingGeneralDoesNot()
    {
        var general = FolderTypes.General;
        foreach (var type in FolderTypes.BuiltIn.Where(type => !type.IsAutomatic && type.Id != general.Id))
        {
            var different =
                type.Layout != general.Layout || type.Sort is not null || type.TileScale is not null ||
                type.Grouping != FolderGrouping.None || type.PhotoViewer || type.DimGenerated || type.ShowProjectStrip ||
                type.RankLower || !FolderTypes.DefaultColumns(type, false).SequenceEqual(FolderTypes.DefaultColumns(general, false));
            Assert.IsTrue(different, type.Name);
        }

        Assert.IsTrue(FolderTypes.ForProfile(DirectoryViewProfile.Screenshots).PhotoViewer, "Screenshots work like Photos.");
        Assert.AreEqual(FolderGrouping.Kind, FolderTypes.ForProfile(DirectoryViewProfile.Desktop).Grouping);
    }

    [TestMethod]
    public void DesktopGroupsFoldersShortcutsAndFiles()
    {
        Assert.AreEqual("Folders", SearchCoordinatorTests.Item(@"C:\D\Work", System.IO.FileAttributes.Directory).KindGroup);
        Assert.AreEqual("Shortcuts", File("Steam.lnk").KindGroup);
        Assert.AreEqual("Files", File("todo.txt").KindGroup);
    }

    [TestMethod]
    public void DownloadSourceIsTheSiteNotTheServer()
    {
        var note = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://www.github.com/owner/repo/releases\r\nHostUrl=https://objects.githubusercontent.com/x.zip\r\n";
        Assert.AreEqual("github.com", MediaPropertyService.ParseZoneIdentifier(note));
        Assert.AreEqual("example.org", MediaPropertyService.ParseZoneIdentifier("[ZoneTransfer]\nZoneId=3\nHostUrl=https://example.org/a.pdf\n"));
        Assert.AreEqual("Internet", MediaPropertyService.ParseZoneIdentifier("[ZoneTransfer]\nZoneId=3\n"));
        Assert.IsNull(MediaPropertyService.ParseZoneIdentifier("[ZoneTransfer]\nZoneId=0\n"));
    }

    [TestMethod]
    public void PdfTitleAuthorsAndPagesAreRead()
    {
        var pdf = "%PDF-1.4\n1 0 obj << /Type /Pages /Kids [3 0 R 4 0 R] /Count 12 >> endobj\n" +
                  "5 0 obj << /Title (Chapter one) /Parent 6 0 R >> endobj\n" +
                  "9 0 obj << /Title (Attention Is All You Need \\(v2\\)) /Author (A. Vaswani; N. Shazeer) /Producer (pdfTeX) >> endobj\n";
        var info = PdfInfo.Parse(pdf, wholeFile: true)!.Value;
        Assert.AreEqual("Attention Is All You Need (v2)", info.Title, "A bookmark's /Title is not the document title.");
        Assert.AreEqual("A. Vaswani; N. Shazeer", info.Authors);
        Assert.AreEqual(12u, info.PageCount);

        var xmp = "%PDF-1.7\n<x:xmpmeta><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Deep Residual Learning</rdf:li></rdf:Alt></dc:title>" +
                  "<dc:creator><rdf:Seq><rdf:li>Kaiming He</rdf:li><rdf:li>Xiangyu Zhang</rdf:li></rdf:Seq></dc:creator></x:xmpmeta>\n" +
                  "<< /Type /Page >> << /Type /Page >> << /Type /Page >>";
        var fromXmp = PdfInfo.Parse(xmp, wholeFile: true)!.Value;
        Assert.AreEqual("Deep Residual Learning", fromXmp.Title);
        Assert.AreEqual("Kaiming He; Xiangyu Zhang", fromXmp.Authors);
        Assert.AreEqual(3u, fromXmp.PageCount);

        Assert.IsNull(PdfInfo.Parse("not a pdf", wholeFile: true));
    }
}
