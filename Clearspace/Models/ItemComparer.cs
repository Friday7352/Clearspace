// Clearspace | Sorting for file and folder items.

using Clearspace.Native;

namespace Clearspace.Models;

public enum SortColumn
{
    Name,
    DateModified,
    DateCreated,
    Type,
    Size,
    Title,
    Artist,
    Album,
    Duration,
    Track
}

public sealed class ItemComparer : IComparer<FileSystemItem>
{
    private readonly SortColumn _column;
    private readonly bool _descending;
    private readonly bool _foldersFirst;

    public ItemComparer(SortColumn column, bool descending, bool foldersFirst = true)
    {
        _column = column;
        _descending = descending;
        _foldersFirst = foldersFirst;
    }

    public int Compare(FileSystemItem? x, FileSystemItem? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (_foldersFirst && x.IsFolder != y.IsFolder)
            return x.IsFolder ? -1 : 1;

        var result = _column switch
        {
            SortColumn.DateModified => x.DateModified.CompareTo(y.DateModified),
            SortColumn.DateCreated => x.DateCreated.CompareTo(y.DateCreated),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.Type => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            SortColumn.Title => NativeMethods.StrCmpLogicalW(x.DisplayTitle, y.DisplayTitle),
            SortColumn.Artist => string.Compare(x.Artist, y.Artist, StringComparison.OrdinalIgnoreCase),
            SortColumn.Duration => x.Duration.CompareTo(y.Duration),
            SortColumn.Track => x.TrackNumber.CompareTo(y.TrackNumber),

            SortColumn.Album => AlbumThenTrack(x, y),
            _ => 0
        };

        if (result == 0)
            result = NativeMethods.StrCmpLogicalW(x.Name, y.Name);

        return _descending ? -result : result;
    }

    private static int AlbumThenTrack(FileSystemItem x, FileSystemItem y)
    {
        var album = string.Compare(x.Album, y.Album, StringComparison.OrdinalIgnoreCase);
        return album != 0 ? album : x.TrackNumber.CompareTo(y.TrackNumber);
    }
}
