// NEW (live drives): telling what changed between two looks at the drives (Services/DriveWatch.cs).
// Nothing here asks Windows about a real drive.
using System.IO;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DriveWatchTests
{
    private static readonly DriveSeen C = new(@"C:\", "Local Disk", DriveType.Fixed);
    private static readonly DriveSeen D = new(@"D:\", "Data", DriveType.Fixed);
    private static readonly DriveSeen Stick = new(@"E:\", "USB DISK", DriveType.Removable);

    [TestMethod]
    public void NothingChangedIsNoChange()
    {
        var change = DriveWatch.Diff([C, D], [D, C]);
        Assert.IsFalse(change.Any, "The order drives are listed in does not matter.");
    }

    [TestMethod]
    public void ADrivePluggedInHasArrived()
    {
        var change = DriveWatch.Diff([C, D], [C, D, Stick], id: 7);

        Assert.IsTrue(change.Any);
        Assert.AreEqual(7, change.Id);
        CollectionAssert.AreEqual(new[] { Stick }, change.Arrived.ToArray());
        Assert.AreEqual(0, change.Removed.Count);
        Assert.AreEqual("USB DISK (E:)", change.Arrived[0].Title);
    }

    [TestMethod]
    public void ADriveTakenOutIsRemoved()
    {
        var change = DriveWatch.Diff([C, D, Stick], [C, D]);

        CollectionAssert.AreEqual(new[] { Stick }, change.Removed.ToArray());
        Assert.AreEqual(0, change.Arrived.Count);
    }

    [TestMethod]
    public void AnotherStickUnderTheSameLetterCountsAsRenamed()
    {
        var other = new DriveSeen(@"e:\", "PHOTOS", DriveType.Removable);
        var change = DriveWatch.Diff([C, Stick], [C, other]);

        Assert.AreEqual(0, change.Arrived.Count, "The letter is the same, whatever its capitals.");
        Assert.AreEqual(0, change.Removed.Count);
        CollectionAssert.AreEqual(new[] { other }, change.Renamed.ToArray());
    }

    [TestMethod]
    public void SeveralThingsCanChangeAtOnce()
    {
        var change = DriveWatch.Diff([C, D], [C, Stick]);

        CollectionAssert.AreEqual(new[] { Stick }, change.Arrived.ToArray());
        CollectionAssert.AreEqual(new[] { D }, change.Removed.ToArray());
    }
}
