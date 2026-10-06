// NEW (menu by type): which right-click entries fit what is selected (Services/ItemMenu.cs).
// ItemMenu.For reads nothing from the disk or the registry: whether a kind of file can be printed is
// handed in. Carrying the entries out (playing, printing, running as administrator) is checked by hand.
using Clearspace.Models;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class ItemMenuTests
{
    private static MenuSubject FileOf(string ending) => new(ending, IsFolder: false, OnDisk: true);
    private static readonly MenuSubject Folder = new(string.Empty, IsFolder: true, OnDisk: true);

    private static bool PrintsEverything(string ending) => true;
    private static bool PrintsNothing(string ending) => false;

    private static TypeActions For(params MenuSubject[] selection) => ItemMenu.For(selection, PrintsNothing);

    [TestMethod]
    public void ASoundFilePlays()
    {
        Assert.AreEqual(TypeActions.Play | TypeActions.OpenWith, For(FileOf(".mp3")));
        Assert.AreEqual(TypeActions.Play | TypeActions.OpenWith, For(FileOf(".WAV")), "Capitals in the ending make no difference.");
    }

    [TestMethod]
    public void APictureIsViewedCopiedAndPutOnTheDesktop()
    {
        var fits = For(FileOf(".png"));

        Assert.AreEqual(TypeActions.ViewPhoto | TypeActions.CopyImage | TypeActions.SetWallpaper | TypeActions.OpenWith, fits);
    }

    [TestMethod]
    public void APictureWindowsMayNotReadIsOnlyViewed()
    {
        // A camera's raw file: the viewer shows it when a decoder is installed, but the clipboard and the
        // desktop are not promised it.
        var fits = For(FileOf(".cr3"));

        Assert.IsTrue(fits.HasFlag(TypeActions.ViewPhoto));
        Assert.IsFalse(fits.HasFlag(TypeActions.CopyImage));
        Assert.IsFalse(fits.HasFlag(TypeActions.SetWallpaper));
    }

    [TestMethod]
    public void AProgramRunsAsAdministratorAndIsNotOpenedWithAnother()
    {
        Assert.AreEqual(TypeActions.RunAsAdministrator, For(FileOf(".exe")));
        Assert.AreEqual(TypeActions.RunAsAdministrator, For(FileOf(".bat")));
    }

    [TestMethod]
    public void AShortcutShowsWhereItPoints()
        => Assert.AreEqual(TypeActions.RunAsAdministrator | TypeActions.OpenLocation, For(FileOf(".lnk")));

    [TestMethod]
    public void AFolderOpensInTheTerminalAndNothingElse()
        => Assert.AreEqual(TypeActions.Terminal, ItemMenu.For([Folder], PrintsEverything));

    [TestMethod]
    public void AnyOtherFileCanBeOpenedWithAnotherProgram()
    {
        Assert.AreEqual(TypeActions.OpenWith, For(FileOf(".zip")));
        Assert.AreEqual(TypeActions.OpenWith, For(FileOf(string.Empty)), "A file with no ending.");
        Assert.AreEqual(TypeActions.None, For(FileOf(".cslock")), "A locked file is Clearspace's own to open.");
    }

    [TestMethod]
    public void ADocumentPrintsWhenItsProgramCan()
    {
        Assert.AreEqual(TypeActions.Print | TypeActions.OpenWith, ItemMenu.For([FileOf(".pdf")], PrintsEverything));
        Assert.AreEqual(TypeActions.OpenWith, ItemMenu.For([FileOf(".pdf")], PrintsNothing));
        Assert.AreEqual(TypeActions.OpenWith, ItemMenu.For([FileOf(".zip")], PrintsEverything), "Not a kind of file that is printed.");
    }

    [TestMethod]
    public void SeveralDocumentsPrintTogetherButNotAMixedSelection()
    {
        Assert.AreEqual(TypeActions.Print, ItemMenu.For([FileOf(".pdf"), FileOf(".docx"), FileOf(".png")], PrintsEverything));
        Assert.AreEqual(TypeActions.None, ItemMenu.For([FileOf(".pdf"), FileOf(".zip")], PrintsEverything));
        Assert.AreEqual(TypeActions.None, ItemMenu.For([FileOf(".pdf"), Folder], PrintsEverything));
        Assert.AreEqual(TypeActions.None, ItemMenu.For([FileOf(".pdf"), FileOf(".docx")], ending => ending == ".pdf"), "One of them can't be printed.");
    }

    [TestMethod]
    public void TooManyFilesAreNotSentToThePrinterAtOnce()
    {
        var many = Enumerable.Repeat(FileOf(".pdf"), ItemMenu.MostToPrint + 1).ToArray();
        var few = Enumerable.Repeat(FileOf(".pdf"), ItemMenu.MostToPrint).ToArray();

        Assert.AreEqual(TypeActions.None, ItemMenu.For(many, PrintsEverything));
        Assert.AreEqual(TypeActions.Print, ItemMenu.For(few, PrintsEverything));
    }

    [TestMethod]
    public void SeveralItemsGetNoneOfTheSingleFileEntries()
    {
        Assert.AreEqual(TypeActions.None, For(FileOf(".mp3"), FileOf(".mp3")));
        Assert.AreEqual(TypeActions.None, For(Folder, Folder));
        Assert.AreEqual(TypeActions.None, For());
    }

    // ---- CHANGED (only what applies): the general entries

    private static MenuSubject Locked(MenuSubject item, LockState state) => item with { Lock = state };
    private static MenuSubject InCloud(MenuSubject item, CloudSyncState state) => item with { Cloud = state };

    [TestMethod]
    public void AnUnlockedFileOffersOnlyToBeLocked()
    {
        var fits = ItemMenu.Common([FileOf(".jpg")]);

        Assert.AreEqual(CommonActions.Lock, fits, "No Unlock, Remove lock or Change password without a lock; no folder entries on a file.");
    }

    [TestMethod]
    public void ALockedFileOffersEverythingButLocking()
    {
        var fits = ItemMenu.Common([Locked(FileOf(".cslock"), LockState.Locked)]);

        Assert.AreEqual(CommonActions.Unlock | CommonActions.RemoveLock | CommonActions.ChangePassword, fits);
    }

    [TestMethod]
    public void SomethingUnlockedForAVisitIsNeitherLockedNorUnlockedAgain()
    {
        var fits = ItemMenu.Common([Locked(FileOf(".jpg"), LockState.Open)]);

        Assert.AreEqual(CommonActions.RemoveLock | CommonActions.ChangePassword, fits);
    }

    [TestMethod]
    public void SeveralItemsOfferLockRemovalOnlyWhenOneOfThemHasALock()
    {
        Assert.AreEqual(CommonActions.None, ItemMenu.Common([FileOf(".jpg"), FileOf(".png")]));
        Assert.AreEqual(CommonActions.RemoveLock | CommonActions.ChangePassword,
            ItemMenu.Common([FileOf(".jpg"), Locked(FileOf(".cslock"), LockState.Locked)]),
            "Locking and unlocking are for one item at a time.");
    }

    [TestMethod]
    public void AFolderCanBePinnedMeasuredSearchedAndGivenAType()
    {
        var all = CommonActions.Lock | CommonActions.Pin | CommonActions.FolderTools | CommonActions.SetFolderType;

        Assert.AreEqual(all, ItemMenu.Common([Folder]));
        Assert.AreEqual(all & ~CommonActions.Pin, ItemMenu.Common([Folder with { IsPinned = true }]), "Already in the sidebar.");
        Assert.AreEqual(CommonActions.SetFolderType, ItemMenu.Common([Folder, Folder]), "The rest are for one folder.");
    }

    [TestMethod]
    public void ADriveIsAFolderThatCannotBeLockedOrGivenAType()
        => Assert.AreEqual(CommonActions.Pin | CommonActions.FolderTools, ItemMenu.Common([Folder with { IsDriveRoot = true }]));

    [TestMethod]
    public void CloudEntriesFollowWhereTheFileIsNow()
    {
        Assert.IsFalse(ItemMenu.Common([FileOf(".jpg")]).HasFlag(CommonActions.KeepOnDevice), "Not a cloud file.");
        Assert.IsFalse(ItemMenu.Common([FileOf(".jpg")]).HasFlag(CommonActions.FreeUpSpace));

        var onlineOnly = ItemMenu.Common([InCloud(FileOf(".jpg"), CloudSyncState.OnlineOnly)]);
        Assert.IsTrue(onlineOnly.HasFlag(CommonActions.KeepOnDevice));
        Assert.IsFalse(onlineOnly.HasFlag(CommonActions.FreeUpSpace), "There is nothing on this PC to free.");

        var here = ItemMenu.Common([InCloud(FileOf(".jpg"), CloudSyncState.Available)]);
        Assert.IsTrue(here.HasFlag(CommonActions.KeepOnDevice) && here.HasFlag(CommonActions.FreeUpSpace));

        var kept = ItemMenu.Common([InCloud(FileOf(".jpg"), CloudSyncState.AlwaysAvailable)]);
        Assert.IsFalse(kept.HasFlag(CommonActions.KeepOnDevice), "It is kept already.");
        Assert.IsTrue(kept.HasFlag(CommonActions.FreeUpSpace));

        var mixed = ItemMenu.Common([InCloud(FileOf(".jpg"), CloudSyncState.AlwaysAvailable), InCloud(FileOf(".png"), CloudSyncState.OnlineOnly)]);
        Assert.IsTrue(mixed.HasFlag(CommonActions.KeepOnDevice) && mixed.HasFlag(CommonActions.FreeUpSpace), "Each applies to one of them.");
    }

    [TestMethod]
    public void NothingSelectedAndThingsNotOnADiskGetNoGeneralEntries()
    {
        Assert.AreEqual(CommonActions.None, ItemMenu.Common([]));
        Assert.AreEqual(CommonActions.None, ItemMenu.Common([new MenuSubject(string.Empty, IsFolder: true, OnDisk: false)]));
    }

    [TestMethod]
    public void SomethingThatIsNotOnADiskGetsNothing()
    {
        // An entry of one of Clearspace's own pages, not a real path.
        Assert.AreEqual(TypeActions.None, ItemMenu.For([new MenuSubject(".mp3", IsFolder: false, OnDisk: false)], PrintsEverything));
        Assert.AreEqual(TypeActions.None, ItemMenu.For([new MenuSubject(string.Empty, IsFolder: true, OnDisk: false)], PrintsEverything));
    }
}
