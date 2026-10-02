// NEW (tabs): the tab strip's bookkeeping (ViewModels/ExplorerTabs.cs) and the per-tab history
// (Services/NavigationService.cs). No window is involved: "the window loaded a folder" is the
// NavigationService's Navigated event, which these tests record.
using System.IO;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class TabTests
{
    private const string One = @"C:\one";
    private const string Two = @"C:\two";
    private const string Three = @"C:\three";
    private const string Home = @"C:\home";
    private const string Secret = @"C:\secret";

    // A window that has just opened on C:\one. `loaded` lists every folder the window was told to show.
    private static (NavigationService Navigation, ExplorerTabs Tabs, List<string> Loaded) Start()
    {
        var navigation = new NavigationService();
        var tabs = new ExplorerTabs(navigation, () => Home);
        var loaded = new List<string>();
        navigation.Navigated += (_, path) => loaded.Add(path);
        navigation.Navigate(One);
        return (navigation, tabs, loaded);
    }

    [TestMethod]
    public void WindowStartsWithOneTabThatFollowsNavigation()
    {
        var (navigation, tabs, _) = Start();

        Assert.AreEqual(1, tabs.Count);
        Assert.IsTrue(tabs.Active.IsActive);
        Assert.AreEqual(One, tabs.Active.Location);
        Assert.AreSame(navigation.History, tabs.Active.History);

        navigation.Navigate(Two);
        Assert.AreEqual(Two, tabs.Active.Location);
        CollectionAssert.AreEqual(new[] { Two }, tabs.ShownLocations.ToArray());
    }

    [TestMethod]
    public void NewTabOpensAtTheStartLocationAndTakesOver()
    {
        var (navigation, tabs, loaded) = Start();
        var first = tabs.Active;

        var second = tabs.Open();

        Assert.IsNotNull(second);
        Assert.AreEqual(2, tabs.Count);
        Assert.AreSame(second, tabs.Active);
        Assert.AreSame(second, tabs.Items[1]);
        Assert.IsTrue(second.IsActive);
        Assert.IsFalse(first.IsActive);
        Assert.AreEqual(Home, navigation.CurrentPath);
        Assert.AreEqual(Home, loaded[^1]);
        Assert.IsFalse(navigation.CanGoBack, "a new tab has no history behind it");
        Assert.AreEqual(One, first.Location, "the first tab stays where it was");
    }

    [TestMethod]
    public void EachTabKeepsItsOwnBackAndForward()
    {
        var (navigation, tabs, loaded) = Start();
        var first = tabs.Active;
        navigation.Navigate(Two);

        var second = tabs.Open()!;
        navigation.Navigate(Three);
        navigation.GoBack();
        Assert.AreEqual(Home, navigation.CurrentPath);

        Assert.IsTrue(tabs.Activate(first));
        Assert.AreEqual(Two, navigation.CurrentPath);
        Assert.AreEqual(Two, loaded[^1], "switching tabs shows that tab's folder");
        Assert.IsTrue(navigation.CanGoBack);
        Assert.IsFalse(navigation.CanGoForward);
        navigation.GoBack();
        Assert.AreEqual(One, first.Location);

        Assert.IsTrue(tabs.Activate(second));
        Assert.AreEqual(Home, navigation.CurrentPath);
        Assert.IsTrue(navigation.CanGoForward);
        navigation.GoForward();
        Assert.AreEqual(Three, second.Location);
        Assert.AreEqual(One, first.Location);
    }

    [TestMethod]
    public void BackgroundTabDoesNotMoveTheWindow()
    {
        var (navigation, tabs, loaded) = Start();
        var first = tabs.Active;
        var loads = loaded.Count;

        var background = tabs.Open(Two, activate: false)!;

        Assert.AreSame(first, tabs.Active);
        Assert.AreEqual(One, navigation.CurrentPath);
        Assert.AreEqual(loads, loaded.Count, "nothing was loaded for a tab that is not shown");
        Assert.AreEqual(Two, background.Location);
        Assert.IsFalse(background.IsActive);
        CollectionAssert.AreEqual(new[] { One }, tabs.ShownLocations.ToArray(),
            "a tab that was never shown does not count as showing its folder");

        tabs.Activate(background);
        tabs.Activate(first);
        CollectionAssert.AreEquivalent(new[] { One, Two }, tabs.ShownLocations.ToArray());
    }

    [TestMethod]
    public void SwitchingRaisesDeactivatingBeforeTheFolderChangesAndActivatedAfter()
    {
        var (navigation, tabs, _) = Start();
        var first = tabs.Active;
        var second = tabs.Open(Two, activate: false)!;
        var order = new List<string>();
        tabs.Deactivating += (_, tab) => order.Add($"leave {tab.Location} while showing {navigation.CurrentPath}");
        navigation.Navigated += (_, path) => order.Add($"load {path}");
        tabs.Activated += (_, tab) => order.Add($"arrive {tab.Location}");

        tabs.Activate(second);

        CollectionAssert.AreEqual(new[] { $"leave {One} while showing {One}", $"load {Two}", $"arrive {Two}" }, order);

        order.Clear();
        Assert.IsTrue(tabs.Activate(second), "activating the tab you are on is fine");
        Assert.AreEqual(0, order.Count, "and does nothing");
        Assert.IsFalse(first.IsActive);
    }

    [TestMethod]
    public void ClosingTheActiveTabMovesToTheRightNeighbourOrElseTheLeft()
    {
        var (navigation, tabs, _) = Start();
        var a = tabs.Active;
        var b = tabs.Open(Two)!;
        var c = tabs.Open(Three)!;

        tabs.Activate(b);
        Assert.IsTrue(tabs.Close(b));
        Assert.AreSame(c, tabs.Active);
        Assert.AreEqual(Three, navigation.CurrentPath);

        Assert.IsTrue(tabs.Close(c));
        Assert.AreSame(a, tabs.Active);
        Assert.AreEqual(One, navigation.CurrentPath);
        CollectionAssert.AreEqual(new[] { a }, tabs.Items.ToArray());
    }

    [TestMethod]
    public void ClosingABackgroundTabLeavesTheWindowAlone()
    {
        var (navigation, tabs, loaded) = Start();
        var background = tabs.Open(Two, activate: false)!;
        var loads = loaded.Count;

        Assert.IsTrue(tabs.Close(background));

        Assert.AreEqual(1, tabs.Count);
        Assert.AreEqual(One, navigation.CurrentPath);
        Assert.AreEqual(loads, loaded.Count);
    }

    [TestMethod]
    public void ClosingTheOnlyTabAsksToCloseTheWindow()
    {
        var (_, tabs, _) = Start();
        var asked = 0;
        tabs.LastTabClosed += (_, _) => asked++;

        tabs.Close(tabs.Active);

        Assert.AreEqual(1, asked);
        Assert.AreEqual(1, tabs.Count, "the tab itself stays until the window goes");
    }

    [TestMethod]
    public void ReopenBringsBackTheLastClosedTabWhereItWasWithItsHistory()
    {
        var (navigation, tabs, _) = Start();
        var a = tabs.Active;
        var b = tabs.Open(Two)!;
        navigation.Navigate(Three);
        var c = tabs.Open(Home, activate: false)!;
        Assert.IsFalse(tabs.CanReopen);

        tabs.Close(b);
        Assert.IsTrue(tabs.CanReopen);
        CollectionAssert.AreEqual(new[] { a, c }, tabs.Items.ToArray());

        var reopened = tabs.ReopenClosed()!;

        Assert.AreSame(reopened, tabs.Items[1], "back in the middle, where it was closed");
        Assert.AreSame(reopened, tabs.Active);
        Assert.AreEqual(Three, navigation.CurrentPath);
        Assert.IsTrue(navigation.CanGoBack);
        navigation.GoBack();
        Assert.AreEqual(Two, navigation.CurrentPath);
        Assert.IsFalse(tabs.CanReopen);
        Assert.IsNull(tabs.ReopenClosed());
    }

    [TestMethod]
    public void CloseOthersAndCloseToRight()
    {
        var (navigation, tabs, _) = Start();
        var a = tabs.Active;
        var b = tabs.Open(Two, activate: false)!;
        var c = tabs.Open(Three, activate: false)!;
        var d = tabs.Open(Home)!;

        Assert.IsTrue(tabs.HasTabsToRight(b));
        Assert.IsFalse(tabs.HasTabsToRight(d));

        tabs.CloseToRight(b); // closes c and d; d was active, so b takes over
        CollectionAssert.AreEqual(new[] { a, b }, tabs.Items.ToArray());
        Assert.AreSame(b, tabs.Active);
        Assert.AreEqual(Two, navigation.CurrentPath);

        tabs.ReopenClosed(); // d
        tabs.ReopenClosed(); // c
        Assert.AreEqual(4, tabs.Count);

        tabs.CloseOthers(a);
        CollectionAssert.AreEqual(new[] { a }, tabs.Items.ToArray());
        Assert.AreSame(a, tabs.Active);
        Assert.AreEqual(One, navigation.CurrentPath);
    }

    [TestMethod]
    public void MoveReordersWithoutSwitchingAndCycleWrapsAround()
    {
        var (_, tabs, loaded) = Start();
        var a = tabs.Active;
        var b = tabs.Open(Two, activate: false)!;
        var c = tabs.Open(Three, activate: false)!;
        var loads = loaded.Count;

        tabs.Move(a, 2);
        CollectionAssert.AreEqual(new[] { b, c, a }, tabs.Items.ToArray());
        tabs.Move(a, 99); // already last: stays
        tabs.Move(c, -5); // clamps to the front
        CollectionAssert.AreEqual(new[] { c, b, a }, tabs.Items.ToArray());
        Assert.AreSame(a, tabs.Active);
        Assert.AreEqual(loads, loaded.Count, "reordering loads nothing");

        Assert.IsTrue(tabs.Cycle(1)); // from the last tab, forward wraps to the first
        Assert.AreSame(c, tabs.Active);
        Assert.IsTrue(tabs.Cycle(-1));
        Assert.AreSame(a, tabs.Active);

        Assert.IsTrue(tabs.ActivateAt(1));
        Assert.AreSame(b, tabs.Active);
        Assert.IsFalse(tabs.ActivateAt(7));
        Assert.IsTrue(tabs.ActivateLast());
        Assert.AreSame(a, tabs.Active);
    }

    [TestMethod]
    public void DuplicateSitsBesideTheOriginalWithItsOwnCopyOfTheHistory()
    {
        var (navigation, tabs, _) = Start();
        var original = tabs.Active;
        navigation.Navigate(Two);
        tabs.Open(Home, activate: false);

        var copy = tabs.Duplicate(original)!;

        Assert.AreSame(copy, tabs.Items[1]);
        Assert.AreSame(copy, tabs.Active);
        Assert.AreEqual(Two, navigation.CurrentPath);
        navigation.GoBack();
        Assert.AreEqual(One, copy.Location);
        Assert.AreEqual(Two, original.Location, "going back in the copy does not move the original");
    }

    [TestMethod]
    public void TabInALockedFolderAsksBeforeItIsShownAndCancellingStaysPut()
    {
        var (navigation, tabs, loaded) = Start();
        var first = tabs.Active;
        var asked = new List<string>();
        var allow = false;
        navigation.CanEnter = path =>
        {
            asked.Add(path);
            return allow || !path.StartsWith(Secret, StringComparison.OrdinalIgnoreCase);
        };

        var locked = tabs.Open(Secret, activate: false)!; // opening in the background asks nothing
        Assert.AreEqual(0, asked.Count);

        var deactivations = 0;
        tabs.Deactivating += (_, _) => deactivations++;
        var loads = loaded.Count;

        Assert.IsFalse(tabs.Activate(locked));
        Assert.AreSame(first, tabs.Active);
        Assert.AreEqual(One, navigation.CurrentPath);
        Assert.AreEqual(0, deactivations);
        Assert.AreEqual(loads, loaded.Count);
        CollectionAssert.AreEqual(new[] { Secret }, asked);

        Assert.IsNull(tabs.Open(Secret), "a new tab that may not be shown is not kept");
        Assert.AreEqual(2, tabs.Count);

        Assert.IsFalse(tabs.Close(first), "the only other tab can't be shown, so this one stays");
        Assert.AreEqual(2, tabs.Count);

        allow = true;
        Assert.IsTrue(tabs.Activate(locked));
        Assert.AreEqual(Secret, navigation.CurrentPath);
    }

    [TestMethod]
    public void LockingAFolderMovesEveryTabOutOfIt()
    {
        var (navigation, tabs, _) = Start();
        var outside = tabs.Active;
        var background = tabs.Open(Secret + @"\deep", activate: false)!;
        var active = tabs.Open(Secret)!;
        tabs.Activate(background); // shown once, then left in the background
        tabs.Activate(active);
        background.ScrollOffset = 120;
        background.SelectedPaths = [Secret + @"\deep\file.txt"];

        tabs.MoveOut(path => path.StartsWith(Secret, StringComparison.OrdinalIgnoreCase), Home);

        Assert.AreEqual(Home, navigation.CurrentPath, "the tab on screen went there the normal way");
        Assert.AreEqual(Home, active.Location);
        Assert.AreEqual(Home, background.Location);
        Assert.AreEqual(Home, background.History.CurrentPath);
        Assert.AreEqual(0, background.ScrollOffset);
        Assert.AreEqual(0, background.SelectedPaths.Count);
        Assert.AreEqual(One, outside.Location);
        Assert.IsFalse(tabs.ShownLocations.Any(path => path.StartsWith(Secret, StringComparison.OrdinalIgnoreCase)));

        tabs.Activate(background);
        Assert.IsTrue(navigation.CanGoBack, "the locked folder is still behind it in the history");
    }

    // NEW (tab to new window)
    [TestMethod]
    public void DetachedTabLeavesWithItsHistoryAndIsNotKeptForReopen()
    {
        var (navigation, tabs, _) = Start();
        var stays = tabs.Active;
        var leaves = tabs.Open(Two)!;
        navigation.Navigate(Three);
        leaves.SearchText = "report";
        leaves.ScrollOffset = 240;

        Assert.IsTrue(tabs.Detach(leaves));

        CollectionAssert.AreEqual(new[] { stays }, tabs.Items.ToArray());
        Assert.AreSame(stays, tabs.Active, "the window moved on to a tab it still has");
        Assert.AreEqual(One, navigation.CurrentPath);
        Assert.IsFalse(leaves.IsActive);
        Assert.IsFalse(tabs.CanReopen, "a tab that moved to another window was not closed");

        Assert.IsFalse(tabs.Detach(stays), "the only tab can't be taken out");
        Assert.IsFalse(tabs.Detach(leaves), "nor one that is already gone");
        Assert.AreEqual(1, tabs.Count);

        // The window made for it starts on the history that came with the tab.
        var other = new NavigationService(leaves.History);
        var otherTabs = new ExplorerTabs(other);
        Assert.AreEqual(Three, otherTabs.Active.Location);
        Assert.AreEqual(Three, other.CurrentPath);
        Assert.IsTrue(other.CanGoBack);
        other.GoBack();
        Assert.AreEqual(Two, otherTabs.Active.Location);
        Assert.AreEqual(One, navigation.CurrentPath, "and the first window is not affected by what the other does");
        Assert.AreEqual("report", leaves.SearchText);
        Assert.AreEqual(240, leaves.ScrollOffset);
    }

    // NEW (tab to new window)
    [TestMethod]
    public void DetachingATabWhoseNeighboursCannotBeShownLeavesItInPlace()
    {
        var (navigation, tabs, _) = Start();
        var first = tabs.Active;
        var locked = tabs.Open(Secret, activate: false)!;
        navigation.CanEnter = path => !path.StartsWith(Secret, StringComparison.OrdinalIgnoreCase);

        Assert.IsFalse(tabs.Detach(first), "the only other tab is in a locked folder and the prompt was cancelled");
        CollectionAssert.AreEqual(new[] { first, locked }, tabs.Items.ToArray());
        Assert.AreSame(first, tabs.Active);

        Assert.IsTrue(tabs.Detach(locked), "a background tab leaves without the window having to go anywhere");
        Assert.AreEqual(1, tabs.Count);
    }

    [TestMethod]
    public void TabNamesAndSymbolsFollowTheLocation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clearspace tabs", "Project files");
        var navigation = new NavigationService();
        var tabs = new ExplorerTabs(navigation);
        Assert.AreEqual("New tab", tabs.Active.Title);

        navigation.Navigate(folder);
        Assert.AreEqual("Project files", tabs.Active.Title);
        Assert.AreEqual(folder, tabs.Active.Hint);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "Clearspace tabs"), ExplorerTabs.ParentOf(folder));

        navigation.Navigate(ExplorerLocations.MyPcPath);
        Assert.AreEqual("This PC", tabs.Active.Title);
        Assert.AreEqual("This PC", tabs.Active.Hint, "places without a path show their name, not clearspace://...");
        Assert.AreNotEqual(ExplorerTab.GlyphFor(folder), tabs.Active.Glyph);
        Assert.AreEqual(ExplorerTab.GlyphFor(@"D:\"), ExplorerTab.GlyphFor("C:"));

        var named = new ExplorerTabs(new NavigationService(), titleOf: path => "Custom " + path.Length);
        Assert.AreEqual("Custom 5", named.Open("C:\\ab", activate: false)!.Title);
    }

    [TestMethod]
    public void AttachedHistoryIsTheOneBackAndForwardWorkOn()
    {
        var navigation = new NavigationService();
        navigation.Navigate(One);
        navigation.Navigate(Two);
        var other = NavigationService.CreateHistory(Three + "\\");
        Assert.AreEqual(Three, other.CurrentPath, "tidied the same way Navigate tidies a path");

        var shown = new List<string>();
        navigation.Navigated += (_, path) => shown.Add(path);
        var original = navigation.History;
        navigation.Attach(other);

        CollectionAssert.AreEqual(new[] { Three }, shown);
        Assert.IsFalse(navigation.CanGoBack);
        navigation.Navigate(Home);
        Assert.AreEqual(2, other.Count);
        Assert.AreEqual(2, original.Count, "the history that was detached is untouched");

        navigation.Attach(original);
        Assert.AreEqual(Two, navigation.CurrentPath);
        Assert.IsTrue(navigation.CanGoBack);

        Assert.IsTrue(navigation.MayEnter(Two), "the place already on screen is never gated");
        navigation.CanEnter = _ => false;
        Assert.IsTrue(navigation.MayEnter(Two));
        Assert.IsFalse(navigation.MayEnter(Three));
    }
}
