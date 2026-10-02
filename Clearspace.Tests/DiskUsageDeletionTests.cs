using System.IO;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DiskUsageDeletionTests
{
    private static VolumeIndex Sample()
    {
        var index = new VolumeIndex(@"C:\", 1);
        index.Add(-1, @"C:\", 0, 0, 0, FileAttributes.Directory);
        index.Add(0, "folder.with.dots", 0, 0, 0, FileAttributes.Directory);
        index.Add(1, "one.txt", 100, 0, 0, FileAttributes.Normal);
        index.Add(1, "two.txt", 200, 0, 0, FileAttributes.Normal);
        index.Add(0, "root.txt", 300, 0, 0, FileAttributes.Normal);
        return index;
    }

    [TestMethod]
    public void ConfirmedPermanentDeleteBypassesRecycleBinAndPassesOnlyExactPaths()
    {
        var paths = new[] { @"C:\folder.with.dots", @"C:\root.txt" };
        var result = FileOperationService.DeletePermanentlyConfirmed(paths, new IntPtr(42),
            (ref NativeMethods.SHFILEOPSTRUCT request) =>
            {
                Assert.AreEqual(NativeMethods.FO_DELETE, request.wFunc);
                Assert.AreEqual(0, request.fFlags & NativeMethods.FOF_ALLOWUNDO);
                Assert.AreNotEqual(0, request.fFlags & NativeMethods.FOF_NOCONFIRMATION);
                Assert.AreEqual(string.Join('\0', paths) + "\0\0", request.pFrom);
                Assert.AreEqual(new IntPtr(42), request.hwnd);
                return 0;
            });
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void RequestsRejectRootsGroupsForeignChildrenAndUnsafeNames()
    {
        var snapshot = DiskUsageSnapshot.Build(Sample(), default);
        Assert.IsNull(DiskUsageDeletionService.CreateRequest(snapshot, 0, [0]));
        Assert.IsNull(DiskUsageDeletionService.CreateRequest(snapshot, 0, [-1]));
        Assert.IsNull(DiskUsageDeletionService.CreateRequest(snapshot, 0, [2]));
        Assert.IsNull(DiskUsageDeletionService.CreateRequest(snapshot, 0, [999]));
        Assert.IsNull(DiskUsageDeletionService.CreateRequest(snapshot, 0, []));
        var index = Sample();
        var bad = index.Add(0, "..", 0, 0, 0, FileAttributes.Directory);
        Assert.IsNull(DiskUsageDeletionService.CreateRequest(DiskUsageSnapshot.Build(index, default), 0, [bad]));
        var request = DiskUsageDeletionService.CreateRequest(snapshot, 0, [1, 4, 1])!;
        Assert.AreEqual(2, request.Targets.Count);
        StringAssert.Contains(request.ConfirmationMessage, @"C:\folder.with.dots");
        StringAssert.Contains(request.ConfirmationMessage, "will not go to the Recycle Bin");
        StringAssert.Contains(request.ConfirmationMessage, "everything currently inside");
    }

    [TestMethod]
    public async Task RejectingConfirmationNeverCallsDeleteOrChecksFiles()
    {
        var service = new DiskUsageDeletionService((_, _) => throw new AssertFailedException("Delete called."),
            _ => throw new AssertFailedException("Probe called."));
        using var vm = new DiskUsageViewModel(() => [Sample()], service);
        await vm.LoadAsync();
        vm.SetSelection(vm.Items);
        await vm.DeletePermanentlyAsync(IntPtr.Zero, _ => false);
        Assert.AreEqual(2, vm.Items.Count);
        Assert.IsTrue(vm.CanDelete);
        Assert.IsFalse(vm.IsBusy);
        StringAssert.Contains(vm.Status, "No files were changed");
    }

    [TestMethod]
    public async Task SuccessfulFolderDeleteUpdatesTotalsHistoryAndDoesNotReappearOnRefresh()
    {
        var index = Sample();
        var deleted = false;
        var calls = 0;
        var service = new DiskUsageDeletionService((paths, _) =>
        {
            calls++;
            CollectionAssert.AreEqual(new[] { @"C:\folder.with.dots" }, paths.ToArray());
            deleted = true;
            return FileOperationResult.FromShellResult(FileOperationKind.Delete, 0, false);
        }, path => deleted && path == @"C:\folder.with.dots" ? DiskUsagePathState.Missing : DiskUsagePathState.Exists);
        using var vm = new DiskUsageViewModel(() => [index], service);
        await vm.LoadAsync();
        await vm.NavigateAsync(1);
        await vm.UpAsync();
        vm.SetSelection(vm.Items.Where(i => i.Id == 1));
        var completed = 0;
        vm.FileOperationCompleted += (_, _) => completed++;
        await vm.DeletePermanentlyAsync(IntPtr.Zero, request => request.Targets.Count == 1);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, completed);
        Assert.AreEqual("root.txt", vm.Items.Single().Name);
        Assert.AreEqual("300 B", vm.TotalSize);
        Assert.IsFalse(vm.CanDelete);
        await vm.BackAsync(); // Deleted folder must not block history.
        Assert.AreEqual(@"C:\", vm.CurrentPath);
        await vm.LoadAsync(@"C:\");
        Assert.AreEqual(1, vm.Items.Count);
        using var reopened = new DiskUsageViewModel(() => [index], service);
        await reopened.LoadAsync();
        Assert.AreEqual(1, reopened.Items.Count);
    }

    [TestMethod]
    public async Task PartialCancellationRemovesOnlyConfirmedMissingDescendants()
    {
        var index = Sample();
        var service = new DiskUsageDeletionService((_, _) => FileOperationResult.FromShellResult(FileOperationKind.Delete, 0, true),
            path => path.EndsWith("one.txt") ? DiskUsagePathState.Missing : DiskUsagePathState.Exists);
        using var vm = new DiskUsageViewModel(() => [index], service);
        await vm.LoadAsync();
        vm.SetSelection(vm.Items.Where(i => i.Id == 1));
        await vm.DeletePermanentlyAsync(IntPtr.Zero, _ => true);
        Assert.AreEqual(200L, vm.Items.Single(i => i.Id == 1).Bytes);
        Assert.AreEqual("500 B", vm.TotalSize);
        StringAssert.Contains(vm.Status, "Canceled or stopped");
        await vm.NavigateAsync(1);
        Assert.AreEqual("two.txt", vm.Items.Single().Name);
    }

    [TestMethod]
    public async Task AccessErrorsDoNotRemoveItemsOrChangeTotals()
    {
        var service = new DiskUsageDeletionService((_, _) => FileOperationResult.FromShellResult(FileOperationKind.Delete, 5, false),
            _ => DiskUsagePathState.Unknown);
        using var vm = new DiskUsageViewModel(() => [Sample()], service);
        await vm.LoadAsync();
        vm.SetSelection(vm.Items);
        await vm.DeletePermanentlyAsync(IntPtr.Zero, _ => true);
        Assert.AreEqual(2, vm.Items.Count);
        Assert.AreEqual("600 B", vm.TotalSize);
        StringAssert.Contains(vm.Status, "access denied");
        StringAssert.Contains(vm.Status, "could not be checked");
    }

    [TestMethod]
    public async Task DeletionLocksNavigationAndRepeatedDeleteRequests()
    {
        using var vm = new DiskUsageViewModel(() => [Sample()], new DiskUsageDeletionService());
        await vm.LoadAsync();
        vm.SetSelection(vm.Items);
        await vm.DeletePermanentlyAsync(IntPtr.Zero, _ =>
        {
            Assert.IsFalse(vm.CanDelete);
            Assert.IsFalse(vm.CanCancel);
            vm.NavigateAsync(1).GetAwaiter().GetResult();
            Assert.AreEqual(@"C:\", vm.CurrentPath);
            vm.DeletePermanentlyAsync(IntPtr.Zero, _ => throw new AssertFailedException("Nested confirmation.")).GetAwaiter().GetResult();
            return false;
        });
    }

    [TestMethod]
    public void ExclusionsDoNotHideSameNamedSiblingsOrNewerIndexVersions()
    {
        var index = Sample();
        index.Add(0, "folder.with.dots2", 0, 0, 0, FileAttributes.Directory);
        var snapshot = DiskUsageSnapshot.Build(index, default);
        var service = new DiskUsageDeletionService(probe: _ => DiskUsagePathState.Missing);
        var request = DiskUsageDeletionService.CreateRequest(snapshot, 0, [1])!;
        var refreshed = service.Reconcile(snapshot, request, default);
        Assert.AreEqual(-1, refreshed.Snapshot.FindFolder(@"C:\folder.with.dots"));
        Assert.IsTrue(refreshed.Snapshot.FindFolder(@"C:\folder.with.dots2") > 0);
        var newer = new VolumeIndex(index.Root, index.SerialNumber, DateTime.UtcNow.AddSeconds(1),
            index.Entries, index.Count, index.Names, index.PoolLength);
        Assert.AreEqual(0, service.ExclusionsFor(newer).Count);
    }
}
