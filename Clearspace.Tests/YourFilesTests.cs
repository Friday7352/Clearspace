// NEW (your files): the list of folders you added to Your files (Services/LibraryFolders.cs).
using System.IO;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class YourFilesTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "Clearspace your files");
    private static readonly string Projects = Path.Combine(Home, "Projects");
    private static readonly string School = Path.Combine(Home, "School");

    [TestMethod]
    public void AddKeepsOrderAndIgnoresTheSameFolderWrittenDifferently()
    {
        var folders = new List<string>();

        Assert.IsTrue(LibraryFolders.Add(folders, Projects));
        Assert.IsTrue(LibraryFolders.Add(folders, School + Path.DirectorySeparatorChar));
        Assert.IsFalse(LibraryFolders.Add(folders, Projects.ToUpperInvariant()), "same folder, different case");
        Assert.IsFalse(LibraryFolders.Add(folders, Projects + Path.DirectorySeparatorChar), "same folder, trailing separator");
        Assert.IsFalse(LibraryFolders.Add(folders, "  "));

        CollectionAssert.AreEqual(new[] { Projects, School }, folders, "stored without the trailing separator, in the order added");
        Assert.IsTrue(LibraryFolders.Contains(folders, School));
        Assert.AreEqual("Projects", LibraryFolders.NameOf(Projects + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void RenamedFolderKeepsItsPlace()
    {
        var folders = new List<string> { Projects, School };
        var renamed = Path.Combine(Home, "Work");

        Assert.IsTrue(LibraryFolders.Move(folders, Projects, renamed));
        CollectionAssert.AreEqual(new[] { renamed, School }, folders);

        Assert.IsFalse(LibraryFolders.Move(folders, Path.Combine(Home, "Elsewhere"), Projects), "not one of yours: nothing changes");
        CollectionAssert.AreEqual(new[] { renamed, School }, folders);

        Assert.IsTrue(LibraryFolders.Move(folders, renamed, School), "renamed onto one already listed: no duplicate");
        CollectionAssert.AreEqual(new[] { School }, folders);
    }

    [TestMethod]
    public void RemoveTakesAFolderOffTheListOnly()
    {
        var folders = new List<string> { Projects, School };

        Assert.IsTrue(LibraryFolders.Remove(folders, School.ToLowerInvariant()));
        Assert.IsFalse(LibraryFolders.Remove(folders, School));
        CollectionAssert.AreEqual(new[] { Projects }, folders);
    }

    [TestMethod]
    public void PruneDropsDeletedFoldersButKeepsOnesOnADriveThatIsNotConnected()
    {
        var unplugged = Path.Combine(Path.GetTempPath(), "Clearspace unplugged drive", "Archive");
        var folders = new List<string> { Projects, School, unplugged };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Home, Projects };

        Assert.IsTrue(LibraryFolders.Prune(folders, existing.Contains));

        CollectionAssert.AreEqual(new[] { Projects, unplugged }, folders,
            "School is gone (its parent exists, it does not); Archive stays (its whole drive is missing)");
        Assert.IsFalse(LibraryFolders.Prune(folders, existing.Contains), "nothing more to drop");
    }
}
