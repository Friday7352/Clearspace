// NEW (productivity): tab groups, the last session and folder visits (Services/ProductivityStore.cs), and
// opening a group's tabs beside the open ones (ExplorerTabs.OpenTogether). Each test uses a file of its
// own under the temporary folder, so nothing touches your real productivity.json.
// CHANGED (groups stay as saved): the test that a group followed its window is gone with that behaviour.
using System.IO;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
[DoNotParallelize] // ProductivityStore is one store for the whole process
public sealed class ProductivityTests
{
    private string _file = string.Empty;

    [TestInitialize]
    public void UseTestFile()
    {
        _file = Path.Combine(Path.GetTempPath(), "Clearspace productivity test " + Guid.NewGuid().ToString("N") + ".json");
        ProductivityStore.TestFilePath = _file;
        ProductivityStore.ResetForTests();
    }

    [TestCleanup]
    public void RemoveTestFile()
    {
        ProductivityStore.ResetForTests();
        ProductivityStore.TestFilePath = null;
        try { File.Delete(_file); } catch (IOException) { }
    }

    [TestMethod]
    public void GroupsAreSavedNamedUniquelyAndEdited()
    {
        var first = ProductivityStore.Add("Work", [@"C:\a", ExplorerLocations.ProductivityPath, @"C:\b"]);
        var second = ProductivityStore.Add("work", [@"C:\c"]);

        CollectionAssert.AreEqual(new[] { @"C:\a", @"C:\b" }, first.Tabs, "the Productivity page itself is never one of a group's tabs");
        Assert.AreEqual("work (2)", second.Name);
        Assert.AreEqual(second.Id, ProductivityStore.Groups[0].Id, "newest first");
        Assert.AreNotEqual(first.Color, second.Color, "each new group gets the next colour");

        Assert.IsTrue(ProductivityStore.Update(first.Id, group => { group.Tabs.RemoveAt(0); group.Tabs.Add(@"D:\new"); group.Name = "  Clients  "; }));
        var edited = ProductivityStore.Find(first.Id)!;
        CollectionAssert.AreEqual(new[] { @"C:\b", @"D:\new" }, edited.Tabs);
        Assert.AreEqual("Clients", edited.Name);

        // What a caller gets is a copy: changing it changes nothing saved.
        edited.Tabs.Clear();
        Assert.AreEqual(2, ProductivityStore.Find(first.Id)!.Tabs.Count);

        Assert.IsTrue(ProductivityStore.Remove(second.Id));
        Assert.IsNull(ProductivityStore.Find(second.Id));

        // It was written to the file: a fresh start reads the same group back.
        ProductivityStore.ResetForTests();
        Assert.AreEqual("Clients", ProductivityStore.Find(first.Id)?.Name);
    }

    [TestMethod]
    public void TheSessionFromStartupIsKeptAsideFromTheOneBeingWritten()
    {
        ProductivityStore.SaveSession([@"C:\old1", @"C:\old2"], 1, now: true);

        ProductivityStore.ResetForTests(); // Clearspace starts again
        var previous = ProductivityStore.PreviousSession;
        CollectionAssert.AreEqual(new[] { @"C:\old1", @"C:\old2" }, previous.Tabs.ToArray());
        Assert.AreEqual(1, previous.Active);

        ProductivityStore.SaveSession([@"C:\new"], 0, now: true);
        CollectionAssert.AreEqual(new[] { @"C:\old1", @"C:\old2" }, ProductivityStore.PreviousSession.Tabs.ToArray(),
            "Reopen last session brings back what was open before this start, not the tabs open now");
    }

    [TestMethod]
    public void MostVisitedCountsTheLastTwoWeeks()
    {
        var now = new DateTime(2026, 11, 2, 10, 0, 0);
        for (var i = 0; i < 5; i++) ProductivityStore.RecordVisit(@"C:\daily", now.AddDays(-i));
        for (var i = 0; i < 9; i++) ProductivityStore.RecordVisit(@"C:\long ago", now.AddDays(-20));
        ProductivityStore.RecordVisit(@"C:\once\", now);
        ProductivityStore.RecordVisit(ExplorerLocations.MyPcPath, now);
        ProductivityStore.RecordVisit("relative", now);

        var most = ProductivityStore.MostVisited(10, now);
        CollectionAssert.AreEqual(new[] { @"C:\daily", @"C:\once" }, most.Select(folder => folder.Path).ToArray(),
            "visits from three weeks ago don't count; pages and relative paths are never recorded; the ending \\ is dropped");
        Assert.AreEqual(5, most[0].RecentVisits);

        ProductivityStore.ClearVisits();
        Assert.AreEqual(0, ProductivityStore.MostVisited(10, now).Count);
    }

    // CHANGED (groups add, not replace)
    [TestMethod]
    public void OpeningAGroupAddsItsTabsAndOnlyTheSamePlacesMakeWay()
    {
        var navigation = new NavigationService();
        var tabs = new ExplorerTabs(navigation, () => @"C:\home");
        navigation.Navigate(@"C:\mine");
        tabs.Open(@"C:\g2", activate: false);

        var shown = tabs.OpenTogether([@"C:\g1", @"c:\G2", @"C:\g3"], active: 1, groupId: "work");

        Assert.IsNotNull(shown);
        CollectionAssert.AreEqual(new[] { @"C:\mine", @"C:\g1", @"c:\G2", @"C:\g3" }, tabs.Locations.ToArray(),
            "your tab stays; the one already showing g2 made way for the group's own");
        Assert.AreEqual(@"c:\G2", tabs.Active.Location);
        Assert.AreEqual(3, tabs.InGroup("work").Count);
        Assert.IsNull(tabs.Items[0].GroupId);

        Assert.IsTrue(tabs.CanReopen, "the tab that made way can be brought back");
        Assert.IsNull(tabs.OpenTogether([]), "nothing to open changes nothing");
    }

    // NEW (group capsule): what each tab of a group draws.
    [TestMethod]
    public void AGroupsTabsShareOneCapsuleThatCollapsesToItsName()
    {
        var navigation = new NavigationService();
        var tabs = new ExplorerTabs(navigation, () => @"C:\home");
        navigation.Navigate(@"C:\a");
        var second = tabs.Open(@"C:\b", activate: false)!;
        var first = tabs.Active;

        first.SetGroupLook(new GroupLook("#D3A15F", "Work", Start: true, End: false, Collapsed: false, Count: 2, ChipWidth: 100));
        second.SetGroupLook(new GroupLook("#D3A15F", "Work", Start: false, End: true, Collapsed: false, Count: 2, ChipWidth: 0));

        Assert.AreEqual(107d, first.GroupLead, "room for the name before the first tab");
        Assert.AreEqual("12,0,0,12", first.CapsuleCorners);
        Assert.AreEqual("0,12,12,0", second.CapsuleCorners);
        Assert.AreEqual("#26D3A15F", first.GroupTint);
        Assert.IsTrue(first.ShowsTab && second.IsShown);

        first.SetGroupLook(new GroupLook("#D3A15F", "Work", Start: true, End: false, Collapsed: true, Count: 2, ChipWidth: 130));
        second.SetGroupLook(new GroupLook("#D3A15F", "Work", Start: false, End: true, Collapsed: true, Count: 2, ChipWidth: 0));
        Assert.IsTrue(first.LeadOnly && !first.ShowsTab, "collapsed: the first tab is just the name");
        Assert.AreEqual("12", first.CapsuleCorners, "and the capsule closes around it");
        Assert.IsFalse(second.IsShown, "the other tabs take no room");

        first.SetGroupLook(null);
        Assert.IsFalse(first.HasGroup);
        Assert.AreEqual(0d, first.GroupLead);
    }

    [TestMethod]
    public void ProductivityIsAPlaceAtTheTopWithTheGroupsUnderIt()
    {
        SidebarSectionInfo[] sections = [new("productivity", "Productivity", false, false), new("files", "Your files", false, false)];
        var entries = SidebarComposer.Build([], sections, _ => [], [], [], null,
            [new SidebarEntry("Work", string.Empty, Dot: "#D3A15F", TabGroupId: "g1")]).ToArray();

        Assert.AreEqual("Productivity", entries[0].Name);
        Assert.AreEqual(ExplorerLocations.ProductivityPath, entries[0].Path);
        Assert.IsTrue(entries[0].IsPlace && !entries[0].IsPlainHeader, "drawn as a place, not a small heading");
        Assert.IsTrue(entries[1].IsTabGroup && entries[1].IsChild && entries[1].HasDot);
        Assert.AreEqual(string.Empty, entries[1].Path, "a group is opened, never navigated to");
        Assert.IsTrue(entries[2].IsPlainHeader);
        Assert.AreEqual("Productivity", NavigationBreadcrumbs.BuildBreadcrumbs(ExplorerLocations.ProductivityPath).Single().Name);
    }
}
