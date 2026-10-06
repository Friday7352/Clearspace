// NEW (map network drive): the rules behind the Map network drive dialog (Services/NetworkMapping.cs).
// Nothing here goes out to a network or changes this PC's drives: only what was typed, which letters are
// free, and how Windows' answers are worded. Connecting and disconnecting themselves are checked by hand.
using System.IO;
using Clearspace.Services;
using Clearspace.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class NetworkMappingTests
{
    private const string Share = @"\\nas\media";

    [TestMethod]
    [DataRow(@"\\nas\media", @"\\nas\media")]
    [DataRow(@"  \\nas\media\  ", @"\\nas\media")]
    [DataRow("\"\\\\nas\\media\"", @"\\nas\media")]
    [DataRow("//nas/media/films", @"\\nas\media\films")]
    [DataRow("smb://nas/media", @"\\nas\media")]
    [DataRow(@"\\nas\\media", @"\\nas\media")]
    [DataRow(@"\\192.168.1.20\backup$", @"\\192.168.1.20\backup$")]
    public void AcceptsTheWaysANetworkFolderIsWritten(string typed, string expected)
    {
        Assert.IsTrue(NetworkMapping.TryNormalize(typed, out var unc, out var problem), problem);
        Assert.AreEqual(expected, unc);
        Assert.AreEqual(string.Empty, problem);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(@"C:\Users")]
    [DataRow(@"nas\media")]
    [DataRow(@"\\")]
    [DataRow(@"\\nas")]
    [DataRow(@"\\nas\")]
    [DataRow(@"\\?\UNC\nas\media")]
    [DataRow(@"\\.\C:")]
    [DataRow(@"\\nas\me|dia")]
    public void RefusesWhatIsNotANetworkFolderAndSaysWhy(string typed)
    {
        Assert.IsFalse(NetworkMapping.TryNormalize(typed, out var unc, out var problem));
        Assert.AreEqual(string.Empty, unc);
        Assert.IsTrue(problem.Length > 0, "Every refusal comes with a reason to show.");
    }

    [TestMethod]
    public void AServerOnItsOwnAsksForTheSharedFolder()
    {
        NetworkMapping.TryNormalize(@"\\nas", out _, out var problem);
        StringAssert.Contains(problem, @"\\nas\share");
    }

    [TestMethod]
    public void AFolderOnThisPcIsCalledThat()
    {
        NetworkMapping.TryNormalize(@"D:\Films", out _, out var problem);
        StringAssert.Contains(problem, "on this PC");
    }

    [TestMethod]
    public void TheServerIsTheFirstPartOfTheName()
    {
        Assert.AreEqual("nas", NetworkMapping.ServerOf(@"\\nas\media\films"));
        Assert.IsNull(NetworkMapping.ServerOf(@"C:\Users"));
        Assert.IsNull(NetworkMapping.ServerOf(null));
    }

    [TestMethod]
    public void FreeLettersStartAtZAndLeaveOutTheOnesInUse()
    {
        var free = NetworkMapping.FreeLetters(['C', 'd', 'Z']);

        Assert.AreEqual(23, free.Length);
        Assert.AreEqual('Y', free[0]);
        Assert.AreEqual('A', free[^1]);
        CollectionAssert.DoesNotContain(free, 'C');
        CollectionAssert.DoesNotContain(free, 'D', "A letter in use counts whichever way it is written.");
        CollectionAssert.DoesNotContain(free, 'Z');
        CollectionAssert.AreEqual(free.OrderByDescending(letter => letter).ToArray(), free);
    }

    [TestMethod]
    public void NoLetterIsOfferedWhenEveryOneIsTaken()
        => Assert.AreEqual(0, NetworkMapping.FreeLetters("ABCDEFGHIJKLMNOPQRSTUVWXYZ").Length);

    [TestMethod]
    public void TheLettersOfThisPcsDrivesAreNotOffered()
    {
        var free = NetworkMapping.FreeLetters();

        foreach (var root in Environment.GetLogicalDrives())
            CollectionAssert.DoesNotContain(free, char.ToUpperInvariant(root[0]));
    }

    [TestMethod]
    public void WindowsAnswersAreWordedForTheUser()
    {
        Assert.AreEqual(string.Empty, NetworkMapping.Describe(0, 'Z', Share));
        StringAssert.Contains(NetworkMapping.Describe(53, 'Z', Share), "couldn't find nas");
        StringAssert.Contains(NetworkMapping.Describe(67, 'Z', Share), "no shared folder");
        StringAssert.Contains(NetworkMapping.Describe(85, 'Z', Share), "Z: is already in use");
        StringAssert.Contains(NetworkMapping.Describe(1326, 'Z', Share), "user name and password");
        StringAssert.Contains(NetworkMapping.Describe(1219, 'Z', Share), "different user name");

        // An answer with no wording of its own still says something, and carries Windows' number.
        StringAssert.Contains(NetworkMapping.Describe(1272, 'Z', Share), "1272");
    }

    [TestMethod]
    public void ClosingWindowsSignInBoxIsNotAnError()
    {
        var code = NetworkMapping.ErrorCancelled;
        var result = new NetworkMappingResult(code, NetworkMapping.Describe(code, 'Z', Share));

        Assert.IsTrue(result.Cancelled);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(string.Empty, result.Message);
    }

    [TestMethod]
    public void OpenFilesAreToldApartFromOtherRefusals()
    {
        Assert.IsTrue(new NetworkMappingResult(NetworkMapping.ErrorOpenFiles, string.Empty).InUse);
        Assert.IsTrue(new NetworkMappingResult(NetworkMapping.ErrorDeviceInUse, string.Empty).InUse);
        Assert.IsFalse(new NetworkMappingResult(5, string.Empty).InUse);
    }

    [TestMethod]
    public void WhatIsNotADriveLetterIsNeverDisconnected()
    {
        Assert.IsFalse(NetworkMapping.Disconnect(string.Empty, force: true).Succeeded);
        Assert.IsFalse(NetworkMapping.Disconnect(Share, force: true).Succeeded);
    }

    [TestMethod]
    public void ADriveOfThisPcHasNoNetworkName()
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.IsNull(NetworkMapping.RemoteName(systemDrive));
        Assert.IsNull(NetworkMapping.RemoteName(string.Empty));
    }

    [TestMethod]
    public void TheThisPcAndNetworkHeadingsOfferMapping()
    {
        SidebarSectionInfo[] sections =
        [
            new("network", "Network", false, false),
            new("this-pc", "PC", false, false),
            new("files", "Your files", false, false)
        ];
        SidebarEntry[] drives = [new("Share", @"Z:\", IsNetworkDrive: true)];

        var entries = SidebarComposer.Build(drives, sections, _ => [], [], []).ToArray();

        CollectionAssert.AreEqual(new[] { "Network", "Share", "PC", "Your files" }, entries.Select(entry => entry.Name).ToArray());
        Assert.IsTrue(entries[0].OffersMapDrive);
        Assert.IsFalse(entries[1].OffersMapDrive, "A drive is not a heading.");
        Assert.IsTrue(entries[1].IsNetworkDrive, "A network drive keeps its mark as a sidebar child: Disconnect depends on it.");
        Assert.IsTrue(entries[2].OffersMapDrive);
        Assert.IsFalse(entries[3].OffersMapDrive);
    }
}
