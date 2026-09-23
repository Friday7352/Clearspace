namespace Clearspace.Services;

// NEW (round 39): the few reads the flat whole-drive layout needs, without allocating a record per
// entry the way DiskUsageSnapshot.Children does.
/// <summary>What Controls.FlatTreemapLayout reads. Implemented by <see cref="DiskUsageSnapshot"/>.</summary>
internal interface IFlatSource
{
    int Count { get; }
    long BytesOf(int id);
    long FilesOf(int id);
    bool IsFolderEntry(int id);
    ReadOnlySpan<char> NameOf(int id);
    /// <summary>Appends the ids of <paramref name="folder"/>'s children that are not excluded, in any order.</summary>
    void ChildIds(int folder, List<int> into);
}
