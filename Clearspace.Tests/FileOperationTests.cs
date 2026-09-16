using Clearspace.Commands;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FileOperationTests
{
    [TestMethod]
    [DataRow(0, false, FileOperationStatus.Succeeded, FileOperationError.None)]
    [DataRow(0, true, FileOperationStatus.Canceled, FileOperationError.None)]
    [DataRow(0x75, false, FileOperationStatus.Canceled, FileOperationError.None)]
    [DataRow(1223, false, FileOperationStatus.Canceled, FileOperationError.None)]
    [DataRow(5, false, FileOperationStatus.Failed, FileOperationError.AccessDenied)]
    [DataRow(0x78, false, FileOperationStatus.Failed, FileOperationError.AccessDenied)]
    [DataRow(2, false, FileOperationStatus.Failed, FileOperationError.NotFound)]
    [DataRow(3, false, FileOperationStatus.Failed, FileOperationError.NotFound)]
    [DataRow(5, true, FileOperationStatus.Canceled, FileOperationError.AccessDenied)]
    [DataRow(0xB7, false, FileOperationStatus.Failed, FileOperationError.Other)]
    [DataRow(0x10074, false, FileOperationStatus.Failed, FileOperationError.Other)]
    [DataRow(9876, false, FileOperationStatus.Failed, FileOperationError.Other)]
    public void NativeOutcomePreservesStatusAndDiagnostics(int code, bool aborted,
        FileOperationStatus status, FileOperationError error)
    {
        var result = FileOperationService.Run(FileOperationKind.Copy, [@"C:\source\file.txt"],
            @"C:\destination", NativeMethods.FOF_ALLOWUNDO, IntPtr.Zero,
            (ref NativeMethods.SHFILEOPSTRUCT op) =>
            {
                op.fAnyOperationsAborted = aborted;
                return code;
            });

        Assert.AreEqual(status, result.Status);
        Assert.AreEqual(error, result.Error);
        Assert.AreEqual((int?)code, result.NativeErrorCode);
        Assert.AreEqual(aborted, result.AnyOperationsAborted);
        Assert.AreEqual(status == FileOperationStatus.Succeeded, result.Succeeded);
        Assert.AreEqual(status == FileOperationStatus.Canceled, result.Canceled);
        if (!result.Succeeded)
        {
            StringAssert.Contains(result.Message, "Some items may already have changed.");
            StringAssert.Contains(result.Message, $"0x{code:X}");
        }
    }

    [TestMethod]
    [DataRow(FileOperationKind.Copy, 2u)]
    [DataRow(FileOperationKind.Move, 1u)]
    [DataRow(FileOperationKind.Rename, 4u)]
    [DataRow(FileOperationKind.Delete, 3u)]
    public void NativeRequestRetainsPathsFlagsAndOwner(FileOperationKind kind, uint function)
    {
        var calls = 0;
        var destination = kind == FileOperationKind.Delete ? null : @"C:\destination";
        var paths = kind == FileOperationKind.Rename
            ? new[] { @"C:\source\résumé.txt" }
            : new[] { @"C:\source\résumé.txt", @"C:\source\second.txt" };
        var owner = new IntPtr(123);
        var flags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_WANTNUKEWARNING);
        var result = FileOperationService.Run(kind, paths, destination, flags, owner,
            (ref NativeMethods.SHFILEOPSTRUCT op) =>
            {
                calls++;
                Assert.AreEqual(function, op.wFunc);
                Assert.AreEqual(string.Join('\0', paths) + "\0\0", op.pFrom);
                Assert.AreEqual(destination is null ? null : destination + "\0\0", op.pTo);
                Assert.AreEqual(flags, op.fFlags);
                Assert.AreEqual(owner, op.hwnd);
                return 0;
            });
        Assert.AreEqual(1, calls);
        Assert.AreEqual(kind, result.Operation);
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("relative.txt")]
    [DataRow("C:\\source\\*.txt")]
    [DataRow("C:\\source\\fi?e.txt")]
    [DataRow("C:\\source\\file.txt\0C:\\other.txt")]
    public void InvalidSourceNeverCallsWindows(string source)
    {
        var result = FileOperationService.Run(FileOperationKind.Delete, [source], null, 0, IntPtr.Zero,
            (ref NativeMethods.SHFILEOPSTRUCT _) => throw new AssertFailedException("Windows must not be called."));
        AssertInvalid(result);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("relative")]
    [DataRow("C:\\dest\0hidden")]
    public void InvalidDestinationNeverCallsWindows(string? destination)
    {
        var result = FileOperationService.Run(FileOperationKind.Copy, [@"C:\source.txt"], destination, 0, IntPtr.Zero,
            (ref NativeMethods.SHFILEOPSTRUCT _) => throw new AssertFailedException("Windows must not be called."));
        AssertInvalid(result);
    }

    [TestMethod]
    public void EmptySelectionIsRejectedByEveryBatchOperation()
    {
        AssertInvalid(FileOperationService.Copy([], @"C:\destination", IntPtr.Zero));
        AssertInvalid(FileOperationService.Move([], @"C:\destination", IntPtr.Zero));
        AssertInvalid(FileOperationService.Delete([], IntPtr.Zero));
        AssertInvalid(FileOperationService.Delete([], IntPtr.Zero, permanent: true));
        AssertInvalid(FileOperationService.Rename("", @"C:\destination.txt", IntPtr.Zero));
    }

    [TestMethod]
    public void RefreshDoesNotEraseOperationFeedback()
    {
        var context = new ExplorerContext { Navigation = new NavigationService() };
        var changes = new List<string?>();
        context.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var failure = FileOperationResult.FromShellResult(FileOperationKind.Move, 5, false);
        context.ReportFileOperation(failure);
        context.RequestRefresh();
        Assert.AreSame(failure, context.LastFileOperation);
        Assert.IsTrue(context.HasFileOperationResult);
        CollectionAssert.Contains(changes, nameof(ExplorerContext.LastFileOperation));
        CollectionAssert.Contains(changes, nameof(ExplorerContext.HasFileOperationResult));

        var success = FileOperationResult.FromShellResult(FileOperationKind.Copy, 0, false);
        context.ReportFileOperation(success);
        Assert.AreSame(success, context.LastFileOperation);
    }

    private static void AssertInvalid(FileOperationResult result)
    {
        Assert.AreEqual(FileOperationStatus.Failed, result.Status);
        Assert.AreEqual(FileOperationError.InvalidInput, result.Error);
        Assert.IsNull(result.NativeErrorCode);
        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Canceled);
    }
}
