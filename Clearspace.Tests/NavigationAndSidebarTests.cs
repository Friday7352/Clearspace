using System.IO;
using Clearspace.Models;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class NavigationAndSidebarTests
{
    [TestMethod]
    public void NewNavigationCancelsOldLoadAndOldDisposalCannotClearNewLoad()
    {
        using var coordinator = new NavigationCoordinator();
        var first = coordinator.BeginLoad();
        var token = first.Token;
        using var second = coordinator.BeginLoad();
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(first.IsCurrent);
        first.Dispose();
        Assert.IsTrue(second.IsCurrent);
        Assert.IsFalse(second.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void DisposeCancelsPendingNavigation()
    {
        var coordinator = new NavigationCoordinator();
        using var load = coordinator.BeginLoad();
        coordinator.Dispose();
        Assert.IsTrue(load.Token.IsCancellationRequested);
        Assert.IsFalse(load.IsCurrent);
        Assert.ThrowsException<ObjectDisposedException>(() => coordinator.BeginLoad());
    }

    [TestMethod]
    public async Task FolderLoadingSortsAndPreparesResultsWithoutTouchingDisk()
    {
        var prepared = 0;
        using var coordinator = new NavigationCoordinator((path, hidden, token) =>
        {
            Assert.AreEqual(@"C:\example", path);
            Assert.IsTrue(hidden);
            return [SearchCoordinatorTests.Item(@"C:\z.txt"), SearchCoordinatorTests.Item(@"C:\a.txt")];
        }, (items, grid) => { prepared++; Assert.IsTrue(grid); });
        using var load = coordinator.BeginLoad();
        var result = await coordinator.LoadDirectoryAsync(load, @"C:\example",
            new(true, SortColumn.Name, false, true, false), new InlineProgress<IReadOnlyList<FileSystemItem>>(_ => Assert.Fail()));
        CollectionAssert.AreEqual(new[] { "a.txt", "z.txt" }, result.Select(item => item.Name).ToArray());
        Assert.AreEqual(1, prepared);
    }

    [TestMethod]
    public async Task PermissionFailureRemainsDistinctForUserFeedback()
    {
        using var coordinator = new NavigationCoordinator((_, _, _) => throw new UnauthorizedAccessException(), (_, _) => { });
        using var load = coordinator.BeginLoad();
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => coordinator.LoadDirectoryAsync(load, "denied",
            new(false, SortColumn.Name, false, false, false), new InlineProgress<IReadOnlyList<FileSystemItem>>(_ => { })));
    }

    [TestMethod]
    public async Task CanceledFolderLoadDoesNotEnumerate()
    {
        using var coordinator = new NavigationCoordinator((_, _, _) => throw new AssertFailedException("Must not enumerate"));
        using var first = coordinator.BeginLoad();
        using var next = coordinator.BeginLoad();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => coordinator.LoadDirectoryAsync(first, "old",
            new(false, SortColumn.Name, false, false, false), new InlineProgress<IReadOnlyList<FileSystemItem>>(_ => { })));
    }

    [TestMethod]
    public void HistoryTruncatesForwardBranchAfterNewNavigation()
    {
        var history = new NavigationService();
        history.Navigate(@"C:\one"); history.Navigate(@"C:\two"); history.GoBack();
        Assert.IsTrue(history.CanGoForward);
        history.Navigate(@"C:\three");
        Assert.IsFalse(history.CanGoForward);
        history.GoBack(); Assert.AreEqual(@"C:\one", history.CurrentPath);
        history.GoForward(); Assert.AreEqual(@"C:\three", history.CurrentPath);
    }

    [TestMethod]
    public void SamePathRefreshDoesNotDuplicateHistoryAndVirtualHubCannotGoUp()
    {
        var history = new NavigationService();
        history.Navigate(@"C:\one"); history.Navigate(@"c:\ONE\");
        Assert.IsFalse(history.CanGoBack);
        history.Navigate(ExplorerLocations.MyPcPath);
        Assert.IsFalse(history.CanGoUp);
        history.GoUp();
        Assert.AreEqual(ExplorerLocations.MyPcPath, history.CurrentPath);
    }

    [TestMethod]
    public void BreadcrumbsCoverRootFoldersAndNamedVirtualCategories()
    {
        var crumbs = NavigationBreadcrumbs.BuildBreadcrumbs(@"C:\one\two");
        CollectionAssert.AreEqual(new[] { @"C:\", @"C:\one", @"C:\one\two" }, crumbs.Select(c => c.Path).ToArray());
        var category = NavigationBreadcrumbs.BuildBreadcrumbs(ExplorerLocations.CategoryPathPrefix + "work", _ => "Work files");
        Assert.AreEqual("Work files", category.Single().Name);
        Assert.AreEqual(0, NavigationBreadcrumbs.BuildBreadcrumbs("").Count);
    }

    [TestMethod]
    public void SidebarKeepsSectionOrderAndSeparatesNetworkDrives()
    {
        SidebarSectionInfo[] sections = [new("network", "Network", false, false), new("this-pc", "PC", false, false)];
        SidebarEntry[] drives = [new("Local", @"C:\"), new("Share", @"Z:\", IsNetworkDrive: true)];
        var entries = SidebarComposer.Build(drives, sections, _ => [], [], []).ToArray();
        CollectionAssert.AreEqual(new[] { "Network", "Share", "PC", "Local" }, entries.Select(e => e.Name).ToArray());
        Assert.IsTrue(entries[1].IsChild && entries[3].IsChild);
    }

    [TestMethod]
    public void CollapsedCategoryDoesNotLoadItsChildren()
    {
        SidebarSectionInfo[] sections = [new("category:work", "Work", true, true)];
        var entries = SidebarComposer.Build([], sections, _ => throw new AssertFailedException("Collapsed category must not load pins"), [], []).ToArray();
        Assert.AreEqual(1, entries.Length);
        Assert.IsTrue(entries[0].IsCollapsed);
        Assert.AreEqual(ExplorerLocations.CategoryPathPrefix + "work", entries[0].Path);
    }

    [TestMethod]
    public void ExpandedCategoryPreservesPinOwnershipAndEmptyCloudIsOmitted()
    {
        SidebarSectionInfo[] sections = [new("cloud", "Cloud", false, false), new("category:work", "Work", false, true)];
        var entries = SidebarComposer.Build([], sections, id =>
        {
            Assert.AreEqual("work", id);
            return [new(@"C:\project", "Project")];
        }, [], []).ToArray();
        Assert.AreEqual(2, entries.Length);
        Assert.AreEqual("work", entries[1].CategoryId);
        Assert.IsTrue(entries[1].IsPinned && entries[1].IsChild);
    }
}
