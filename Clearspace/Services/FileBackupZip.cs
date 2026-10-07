// Clearspace | NEW (resumable zip): a zip that is saved as it grows, so a run that is cut short can be carried on with.
// A Zip archive backup of a large folder can take hours. Before this, the zip was one long write: when the
// PC lost power, was shut down, or the run was stopped near the end, everything packed so far was thrown
// away and the next run started from nothing. FileBackupZipWriter writes the same kind of zip (any program
// opens the finished file), but in stretches, and notes after each stretch what is safely on the drive.
// The next run reads those notes and goes on from there.
//
// While a zip is being made there are three files in the backup's folder, all ending in ".cspart":
//     Photos 2026-10-06 12.00.00.zip.cspart        the zip so far: the packed files, one after the other
//     Photos 2026-10-06 12.00.00.zip.dir.cspart    the zip's list of contents so far (its "central
//                                                  directory", which in a finished zip comes at the end)
//     Photos 2026-10-06 12.00.00.zip.log.cspart    the notes: one line per packed file with its size and
//                                                  date, and after each stretch a line saying how long the
//                                                  two files above were when everything in them was whole
// When the last file is packed, the list of contents is written to the end of the zip, the zip is opened
// once to check it, ".cspart" comes off its name and the other two files are deleted.
//
// A stretch (Save, by itself every 30 seconds):
//     1. the files packed since the last one are closed off as a zip of their own inside the file
//        (System.IO.Compression does the packing, as before);
//     2. the list of contents that gives is read back, checked, and added to the ".dir" file;
//     3. the zip is cut back to the end of the packed files, where the next stretch goes on;
//     4. both files are written through to the drive, and only then is the line added to the notes.
// So the notes never say more than what is on the drive. After a power cut the files may be longer than
// the notes say (a stretch that was under way); carrying on cuts them back to what the notes say.
//
// Carrying on (Resume) trusts nothing it has not checked against the folder as it is now. The run walks
// the folder again, and for every file asks Keep: it stays in the zip only when its size and date are
// exactly what they were when it was packed. A file that has changed since is packed again, and a file
// that is gone is left out: its old entry is simply not written to the finished zip's list of contents
// (its packed bytes stay in the file, unused, which every zip reader skips). The finished zip is therefore
// the folder as it was during the run that finished it.
//
// What is still lost when a run is cut short: the file that was being packed at that moment, and at most
// the last 30 seconds of files. One very large file is packed in one go, so cutting the run in the middle
// of a 50 GB video packs that video again from its start.
// A run stopped with Stop, or closed with Clearspace, saves once more first and loses only the file it was on.
//
// When the notes or the files are not as expected (damaged, from a newer version), nothing is guessed:
// the unfinished zip is deleted and made again from the start.
//
// Tests: Clearspace.Tests/FileBackupTests.cs (stretches, carrying on after a cut at every kind of place,
// changed and deleted files, more than 65,535 files, and whole runs through FileBackupEngine).
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Clearspace.Services;

/// <summary>A file of the original could not be read to its end while it was being packed. The zip is fine; the file is not in it.</summary>
internal sealed class FileBackupUnreadableException(Exception inner) : IOException(inner.Message, inner);

internal sealed class FileBackupZipWriter : IDisposable
{
    private const uint LocalSignature = 0x04034b50;      // a packed file
    private const uint DirectorySignature = 0x02014b50;  // its line in the list of contents
    private const uint EndSignature = 0x06054b50;        // the end of a zip
    private const uint End64Signature = 0x06064b50;      // the same for very large zips (Zip64)
    private const uint Locator64Signature = 0x07064b50;
    private const int DirectoryFixed = 46;               // a line in the list of contents, without its name and extras
    private const string Header = "Clearspace zip\t1";

    private sealed class Entry
    {
        public required string Name;
        public bool Folder;
        public long Size;
        public long Ticks;          // modified, UTC
        public long DirOffset;      // where its line is in the ".dir" file
        public int DirLength;
        public bool Wanted;         // goes into the finished zip: packed by this run, or kept by it
    }

    private readonly string _partial;
    private readonly FileStream _data;
    private readonly FileStream _dir;
    private readonly FileStream _log;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Entry> _pending = [];     // packed in the stretch that is under way
    private readonly byte[] _buffer = new byte[1 << 16];
    private ZipArchive? _zip;                       // the stretch that is under way
    private long _dataLength;                       // how much of the zip is whole and noted
    private long _dirLength;                        // and of its list of contents
    private long _savedAt = Environment.TickCount64;
    private bool _finished;
    private bool _broken;                           // the drive refused a write: nothing more is saved

    /// <summary>How often what has been packed is made safe.</summary>
    internal TimeSpan SaveEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The unfinished zip this writes to.</summary>
    internal string Path => _partial;

    /// <summary>How many files and folders were in the zip when it was carried on with. 0 for a new one.</summary>
    internal int Carried { get; }

    private FileBackupZipWriter(string partial, FileStream data, FileStream dir, FileStream log, long dataLength, long dirLength, List<Entry> carried)
    {
        _partial = partial;
        _data = data;
        _dir = dir;
        _log = log;
        _dataLength = dataLength;
        _dirLength = dirLength;
        foreach (var entry in carried)
            _entries[entry.Name] = entry; // a file packed twice (it had changed): the later one counts
        Carried = _entries.Count;
    }

    // "X.zip.cspart" -> "X.zip.dir.cspart": still ends in ".cspart", so everything that passes over
    // unfinished files passes over these too.
    private static string Beside(string partial, string what)
    {
        var whole = partial.EndsWith(FileBackupLayout.Part, StringComparison.OrdinalIgnoreCase) ? partial[..^FileBackupLayout.Part.Length] : partial;
        return whole + what + FileBackupLayout.Part;
    }

    internal static string DirPath(string partial) => Beside(partial, ".dir");
    internal static string LogPath(string partial) => Beside(partial, ".log");

    /// <summary>Deletes an unfinished zip and what is kept beside it.</summary>
    internal static void Discard(string partial)
    {
        FileBackupStore.TryDelete(partial);
        FileBackupStore.TryDelete(DirPath(partial));
        FileBackupStore.TryDelete(LogPath(partial));
    }

    /// <summary>Starts a new zip at <paramref name="partial"/>, replacing an unfinished one of that name.</summary>
    internal static FileBackupZipWriter Start(string partial)
    {
        Discard(partial);
        FileStream? data = null, dir = null, log = null;
        try
        {
            data = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            dir = new FileStream(DirPath(partial), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            log = new FileStream(LogPath(partial), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            log.Write(Encoding.UTF8.GetBytes(Header + "\n"));
            log.Flush(flushToDisk: true);
            return new FileBackupZipWriter(partial, data, dir, log, 0, 0, []);
        }
        catch
        {
            data?.Dispose();
            dir?.Dispose();
            log?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an unfinished zip to carry on with it. Null when there is nothing to carry on with: a file
    /// is missing, nothing in it was made safe yet, or the notes and the files do not fit together.
    /// Nothing from it is in the finished zip until Keep or KeepFolder said so.
    /// </summary>
    internal static FileBackupZipWriter? Resume(string partial)
    {
        var dirPath = DirPath(partial);
        var logPath = LogPath(partial);
        if (!File.Exists(partial) || !File.Exists(dirPath) || !File.Exists(logPath))
            return null;

        if (!ReadNotes(File.ReadAllBytes(logPath), out var entries, out var dataLength, out var dirLength, out var notesLength))
            return null;

        FileStream? data = null, dir = null, log = null;
        try
        {
            data = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            dir = new FileStream(dirPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            log = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (data.Length < dataLength || dir.Length < dirLength || !LooksRight(data, dataLength, dir, dirLength, entries))
            {
                data.Dispose();
                dir.Dispose();
                log.Dispose();
                return null;
            }

            // Whatever a stretch that was under way left behind goes.
            data.SetLength(dataLength);
            dir.SetLength(dirLength);
            log.SetLength(notesLength);
            log.Position = notesLength;
            return new FileBackupZipWriter(partial, data, dir, log, dataLength, dirLength, entries);
        }
        catch
        {
            data?.Dispose();
            dir?.Dispose();
            log?.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ the notes

    private static string Escape(string name)
        => name.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;

        var name = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var letter = text[index];
            if (letter != '\\' || index + 1 >= text.Length)
            {
                name.Append(letter);
                continue;
            }

            name.Append(text[++index] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', var other => other });
        }

        return name.ToString();
    }

    // Reads the notes up to the last line that says "whole up to here". False when there is no such line.
    private static bool ReadNotes(byte[] notes, out List<Entry> entries, out long dataLength, out long dirLength, out long notesLength)
    {
        entries = [];
        dataLength = dirLength = notesLength = 0;
        var sinceSaved = new List<Entry>();
        var first = true;
        var found = false;
        var at = 0;

        while (at < notes.Length)
        {
            var end = Array.IndexOf(notes, (byte)'\n', at);
            if (end < 0)
                break; // a line that was being written when the run was cut

            var line = Encoding.UTF8.GetString(notes, at, end - at);
            at = end + 1;
            if (first)
            {
                if (line != Header)
                    return false;
                first = false;
                continue;
            }

            var parts = line.Split('\t', 6);
            if (parts.Length == 4 && parts[0] == "C" &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var data) &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var dir))
            {
                entries.AddRange(sinceSaved);
                sinceSaved.Clear();
                (dataLength, dirLength, notesLength, found) = (data, dir, at, true);
                continue;
            }

            if (parts.Length == 6 && parts[0] is ("F" or "D") &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) &&
                int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                sinceSaved.Add(new Entry { Name = Unescape(parts[5]), Folder = parts[0] == "D", Size = size, Ticks = ticks, DirOffset = offset, DirLength = length });
                continue;
            }

            break; // not a line of these notes: nothing after it is believed
        }

        return found;
    }

    // The zip starts with a packed file, and every noted line of the list of contents is where the notes say.
    private static bool LooksRight(FileStream data, long dataLength, FileStream dir, long dirLength, List<Entry> entries)
    {
        Span<byte> four = stackalloc byte[4];
        if (dataLength >= 4)
        {
            data.Position = 0;
            data.ReadExactly(four);
            if (BinaryPrimitives.ReadUInt32LittleEndian(four) != LocalSignature)
                return false;
        }
        else if (entries.Count > 0)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.DirLength < DirectoryFixed || entry.DirOffset < 0 || entry.DirOffset + entry.DirLength > dirLength)
                return false;

            dir.Position = entry.DirOffset;
            dir.ReadExactly(four);
            if (BinaryPrimitives.ReadUInt32LittleEndian(four) != DirectorySignature)
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ what is kept from before

    /// <summary>
    /// Whether a file is in the zip already, packed by a run before this one, exactly as it is now. When
    /// it is, it stays in the finished zip and need not be packed again.
    /// </summary>
    internal bool Keep(string name, long size, DateTime modifiedUtc)
    {
        if (!_entries.TryGetValue(name, out var entry) || entry.Folder || entry.Size != size || entry.Ticks != modifiedUtc.Ticks)
            return false;
        entry.Wanted = true;
        return true;
    }

    /// <summary>The same for a folder (<paramref name="name"/> ends in "/").</summary>
    internal bool KeepFolder(string name)
    {
        if (!_entries.TryGetValue(name, out var entry) || !entry.Folder)
            return false;
        entry.Wanted = true;
        return true;
    }

    // ------------------------------------------------------------------ packing

    private ZipArchive Stretch()
    {
        if (_zip is not null)
            return _zip;

        _data.Position = _dataLength;
        return _zip = new ZipArchive(_data, ZipArchiveMode.Create, leaveOpen: true);
    }

    /// <summary>Puts a folder in the zip, so an empty one is in it too. <paramref name="name"/> ends in "/".</summary>
    internal void AddFolder(string name)
    {
        try
        {
            // Opened and closed at once: an entry that was never opened is only written when the stretch
            // ends, after the place the list of contents is expected at.
            using (Stretch().CreateEntry(name).Open())
            {
            }
        }
        catch
        {
            _broken = true;
            throw;
        }

        _pending.Add(new Entry { Name = name, Folder = true, Wanted = true });
        SaveIfDue();
    }

    /// <summary>
    /// Packs a file. Throws FileBackupUnreadableException when the file cannot be read to its end (it is
    /// then not in the zip, and packing can go on with the next), and OperationCanceledException when the
    /// run is stopped; anything else means the zip itself could not be written.
    /// </summary>
    internal void Add(string name, Stream from, long size, DateTime modifiedUtc, DateTimeOffset zipDate, CancellationToken token)
    {
        var zip = Stretch();
        var start = _data.Position; // where this file's packed bytes begin
        try
        {
            var packed = zip.CreateEntry(name, CompressionLevel.Fastest);
            packed.LastWriteTime = zipDate;
            using var into = packed.Open();
            while (true)
            {
                int read;
                try
                {
                    read = from.Read(_buffer, 0, _buffer.Length);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new FileBackupUnreadableException(error);
                }

                if (read == 0)
                    break;
                into.Write(_buffer, 0, read);
                token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception error) when (error is FileBackupUnreadableException or OperationCanceledException)
        {
            // Part of the file is in the zip. The stretch ends here without it, and its bytes are cut off.
            try
            {
                EndStretch(cutAt: start);
            }
            catch
            {
                _broken = true;
                throw;
            }

            throw;
        }
        catch
        {
            _broken = true;
            throw;
        }

        _pending.Add(new Entry { Name = name, Size = size, Ticks = modifiedUtc.Ticks, Wanted = true });
        SaveIfDue();
    }

    private void SaveIfDue()
    {
        if (Environment.TickCount64 - _savedAt >= SaveEvery.TotalMilliseconds)
            Save();
    }

    /// <summary>Makes everything packed so far safe: after this, a power cut loses none of it.</summary>
    internal void Save()
    {
        try
        {
            EndStretch(cutAt: null);
        }
        catch
        {
            _broken = true;
            throw;
        }
    }

    // Ends the stretch that is under way (see the top of the file). cutAt: the last file of the stretch
    // was not packed whole; it is left out and the zip is cut back to where it began.
    private void EndStretch(long? cutAt)
    {
        if (_zip is null)
            return;

        var count = _pending.Count;
        var listed = count + (cutAt is null ? 0 : 1);
        var dirStart = _data.Position;
        _zip.Dispose(); // writes this stretch's list of contents and end at dirStart
        _zip = null;
        _data.Flush();

        // Read the list back: one line per packed file, in the order they were packed.
        var lines = new MemoryStream();
        var lengths = new int[count];
        var fixedPart = new byte[DirectoryFixed];
        _data.Position = dirStart;
        for (var index = 0; index < listed; index++)
        {
            _data.ReadExactly(fixedPart);
            if (BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != DirectorySignature)
                throw new InvalidDataException("The zip's list of contents is not where it was expected.");

            var rest = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(28)) +   // name
                       BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(30)) +   // extras
                       BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(32));    // comment
            var more = new byte[rest];
            _data.ReadExactly(more);
            if (index >= count)
                continue; // the file that was cut off

            lines.Write(fixedPart);
            lines.Write(more);
            lengths[index] = DirectoryFixed + rest;
        }

        Span<byte> four = stackalloc byte[4];
        _data.ReadExactly(four);
        if (BinaryPrimitives.ReadUInt32LittleEndian(four) is not (EndSignature or End64Signature))
            throw new InvalidDataException("The zip's list of contents is not as long as expected.");

        // The list first, then the zip cut back to its packed files, both through to the drive ...
        if (count > 0)
        {
            _dir.Position = _dirLength;
            lines.WriteTo(_dir);
            _dir.Flush(flushToDisk: true);
        }

        var end = cutAt ?? dirStart;
        _data.SetLength(end);
        _data.Position = end;
        _data.Flush(flushToDisk: true);

        // ... and only then the notes.
        if (count > 0)
        {
            var notes = new StringBuilder();
            var offset = _dirLength;
            for (var index = 0; index < count; index++)
            {
                var entry = _pending[index];
                entry.DirOffset = offset;
                entry.DirLength = lengths[index];
                offset += lengths[index];
                notes.Append(entry.Folder ? 'D' : 'F').Append('\t')
                    .Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(entry.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(entry.DirOffset.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(entry.DirLength.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Escape(entry.Name)).Append('\n');
            }

            notes.Append("C\t").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(offset.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            _log.Write(Encoding.UTF8.GetBytes(notes.ToString()));
            _log.Flush(flushToDisk: true);

            foreach (var entry in _pending)
                _entries[entry.Name] = entry;
            _dirLength = offset;
            _pending.Clear();
        }

        _dataLength = end;
        _savedAt = Environment.TickCount64;
    }

    // ------------------------------------------------------------------ finishing

    /// <summary>
    /// Makes the zip whole: writes its list of contents (the files this run packed or kept) and its end,
    /// opens it once to check, gives it its name and deletes what was kept beside it. Returns how many
    /// files and folders are in it.
    /// </summary>
    internal int Finish(string final)
    {
        Save();

        var wanted = _entries.Values.Where(entry => entry.Wanted).OrderBy(entry => entry.DirOffset).ToList();
        try
        {
            _data.Position = _dataLength;
            long size = 0;
            var line = new byte[1 << 12];
            foreach (var entry in wanted)
            {
                if (line.Length < entry.DirLength)
                    line = new byte[entry.DirLength];
                _dir.Position = entry.DirOffset;
                _dir.ReadExactly(line, 0, entry.DirLength);
                _data.Write(line, 0, entry.DirLength);
                size += entry.DirLength;
            }

            WriteEnd(_data, wanted.Count, size, _dataLength);
            _data.SetLength(_data.Position);
            _data.Flush(flushToDisk: true);
        }
        catch
        {
            _broken = true; // the notes still say where the packed files end: the next run carries on from there
            throw;
        }

        _finished = true;
        Close();

        using (var check = ZipFile.OpenRead(_partial))
        {
            if (check.Entries.Count != wanted.Count)
                throw new InvalidDataException("The finished zip does not hold what was packed into it.");
        }

        File.Move(_partial, final, overwrite: true);
        FileBackupStore.TryDelete(DirPath(_partial));
        FileBackupStore.TryDelete(LogPath(_partial));
        return wanted.Count;
    }

    /// <summary>The end of a zip: how many entries its list of contents has, how long the list is and where it starts.</summary>
    internal static void WriteEnd(Stream to, long count, long dirSize, long dirOffset)
    {
        var large = count >= 0xFFFF || dirSize >= 0xFFFFFFFF || dirOffset >= 0xFFFFFFFF;
        var bytes = new byte[56 + 20 + 22];
        var at = 0;
        void Put16(int value) { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)value); at += 2; }
        void Put32(long value) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)value); at += 4; }
        void Put64(long value) { BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value); at += 8; }

        if (large)
        {
            // Zip64: the same three numbers with room for more than 65,535 files and 4 GB.
            Put32(End64Signature);
            Put64(44);          // how much of this record follows
            Put16(45);          // written by, and readable from, version 4.5 of the format
            Put16(45);
            Put32(0);           // one file, not a set of disks
            Put32(0);
            Put64(count);
            Put64(count);
            Put64(dirSize);
            Put64(dirOffset);

            Put32(Locator64Signature);
            Put32(0);
            Put64(dirOffset + dirSize); // where the record above starts
            Put32(1);
        }

        Put32(EndSignature);
        Put16(0);
        Put16(0);
        Put16((int)Math.Min(count, 0xFFFF));
        Put16((int)Math.Min(count, 0xFFFF));
        Put32(Math.Min(dirSize, 0xFFFFFFFF));
        Put32(Math.Min(dirOffset, 0xFFFFFFFF));
        Put16(0);               // no comment
        to.Write(bytes, 0, at);
    }

    private void Close()
    {
        _zip?.Dispose();
        _zip = null;
        _data.Dispose();
        _dir.Dispose();
        _log.Dispose();
    }

    /// <summary>
    /// Closes the files. An unfinished zip is saved once more first (unless the drive refused a write)
    /// and stays where it is for the next run.
    /// </summary>
    public void Dispose()
    {
        if (_finished)
            return;

        _finished = true;
        if (!_broken)
        {
            try
            {
                EndStretch(cutAt: null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or NotSupportedException)
            {
                // what the notes say is still true: the next run carries on from the stretch before
            }
        }

        try
        {
            Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or NotSupportedException)
        {
        }
    }

    /// <summary>Tests: closes the files as a power cut would leave them, without saving first.</summary>
    internal void Cut()
    {
        _finished = true;
        // The stretch under way is written out as far as it got, then left: its end is past what the notes say.
        try { _zip?.Dispose(); }
        catch (IOException) { }
        _zip = null;
        _data.Dispose();
        _dir.Dispose();
        _log.Dispose();
    }
}
