// Clearspace | Reading an NTFS drive's master file table (MFT) directly.
//
// NEW (instant first index). Shared by ClearspaceIndexHelper and an elevated Clearspace; base class
// library only. Instead of opening every folder, the drive's file table is read front to back in
// large sequential chunks: one 1 KB record per file or folder, holding its name(s), parent folder,
// attributes, dates and real size. A few million files take seconds rather than minutes.
//
// What the result matches: the same entries a folder walk by the *calling account* would find.
//   * NTFS metadata files ($MFT, $Extend, ...) are left out, as a folder listing leaves them out.
//   * Folder links (junctions, symbolic links, mount points) are listed but not entered; cloud-sync
//     placeholder folders are entered - the same rule as FileIndexBuilder.CanDescend.
//   * Folders the caller may not list keep their own entry but not their contents (FolderAccess).
//   * Hard links appear once per name, like a listing of each folder they are in.
// Clearspace compares the first file-table scan of each drive with a normal walk and falls back to
// walking that drive if they disagree (FileTableTrust), so a parsing mistake cannot go unnoticed.

using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Journal;

// The scan result, parent-first: Parent[i] < i for every entry but the root (entry 0, parent -1).
internal sealed class FileTable
{
    public int Count;
    public int[] Parent = [];
    public uint[] Attributes = [];
    public long[] Size = [];
    public long[] Modified = [];
    public long[] Created = [];
    public ushort[] NameLength = [];
    public char[] Names = [];   // entry names back to back, in entry order
    public int NameChars;
    public int DeniedFolders;   // folders listed but not entered because the caller may not list them
    public long RecordsRead;

    public void Add(int parent, ReadOnlySpan<char> name, uint attributes, long size, long modified, long created)
    {
        if (Count == Parent.Length)
        {
            var capacity = Math.Max(1024, Count * 2);
            Array.Resize(ref Parent, capacity);
            Array.Resize(ref Attributes, capacity);
            Array.Resize(ref Size, capacity);
            Array.Resize(ref Modified, capacity);
            Array.Resize(ref Created, capacity);
            Array.Resize(ref NameLength, capacity);
        }

        while (NameChars + name.Length > Names.Length)
            Array.Resize(ref Names, Math.Max(64 * 1024, Names.Length * 2));

        Parent[Count] = parent;
        Attributes[Count] = attributes;
        Size[Count] = size;
        Modified[Count] = modified;
        Created[Count] = created;
        NameLength[Count] = (ushort)name.Length;
        name.CopyTo(Names.AsSpan(NameChars));
        NameChars += name.Length;
        Count++;
    }
}

internal static class MftReader
{
    private const long RootRecord = 5;
    private const long FirstUserRecord = 16;          // 0-15 are NTFS metadata files
    private const uint FileSignature = 0x454C4946;    // "FILE"
    private const int ChunkBytes = 8 * 1024 * 1024;

    private const uint DirectoryAttribute = 0x10;
    private const uint ReparseAttribute = 0x400;
    private const uint NormalAttribute = 0x80;

    // Per MFT record (indexed by record number). Kept as parallel arrays to stay compact.
    internal sealed class Records(long capacity)
    {
        public readonly bool[] InUse = new bool[capacity];
        public readonly bool[] IsFolder = new bool[capacity];
        public readonly ushort[] Sequence = new ushort[capacity];
        public readonly uint[] Attributes = new uint[capacity];
        public readonly uint[] SecurityId = new uint[capacity];
        public readonly uint[] ReparseTag = new uint[capacity];
        public readonly long[] Modified = new long[capacity];
        public readonly long[] Created = new long[capacity];
        public readonly long[] Size = new long[capacity];

        // Names (one per hard link; DOS short names dropped), in the order read.
        public int Links;
        public int[] LinkOwner = new int[1024];
        public int[] LinkParent = new int[1024];
        public int[] LinkNameStart = new int[1024];
        public ushort[] LinkNameLength = new ushort[1024];
        public char[] NamePool = new char[64 * 1024];
        public int NameChars;

        public void AddLink(int owner, int parent, ReadOnlySpan<char> name)
        {
            if (Links == LinkOwner.Length)
            {
                var size = Links * 2;
                Array.Resize(ref LinkOwner, size);
                Array.Resize(ref LinkParent, size);
                Array.Resize(ref LinkNameStart, size);
                Array.Resize(ref LinkNameLength, size);
            }

            while (NameChars + name.Length > NamePool.Length)
                Array.Resize(ref NamePool, NamePool.Length * 2);

            LinkOwner[Links] = owner;
            LinkParent[Links] = parent;
            LinkNameStart[Links] = NameChars;
            LinkNameLength[Links] = (ushort)name.Length;
            name.CopyTo(NamePool.AsSpan(NameChars));
            NameChars += name.Length;
            Links++;
        }
    }

    // `denied`: given folder groups (one key per security descriptor, a few file IDs each), returns the
    // keys the caller may not list. The helper runs it while impersonating the caller.
    public static FileTable Read(SafeFileHandle volume, string root,
        Func<IReadOnlyList<(ulong Key, ulong[] FileIds)>, HashSet<ulong>> denied, CancellationToken token)
    {
        var info = VolumeData(volume);
        var recordSize = info.BytesPerRecord;

        if (recordSize < 1024 || recordSize > 65536 || info.BytesPerCluster <= 0)
            throw new JournalException(JournalError.NotSupported, "Unexpected NTFS layout; the drive is scanned by walking folders.");

        // Record 0 describes the file table itself; its $DATA runs say where the table lives on disk.
        var first = new byte[Math.Max(recordSize, info.BytesPerSector)];
        ReadExactly(volume, first, info.MftStartLcn * info.BytesPerCluster);
        var runs = TableRuns(first.AsSpan(0, recordSize));
        var capacity = info.MftValidDataLength / recordSize;

        if (capacity <= RootRecord || capacity > int.MaxValue)
            throw new JournalException(JournalError.NotSupported, "Unexpected NTFS file table size.");

        var records = new Records(capacity);
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes + recordSize);
        var carry = 0;
        long consumed = 0;
        long number = 0;

        foreach (var (lcn, clusters) in runs)
        {
            var runOffset = lcn * info.BytesPerCluster;
            var runBytes = clusters * info.BytesPerCluster;

            for (long done = 0; done < runBytes && consumed < info.MftValidDataLength;)
            {
                token.ThrowIfCancellationRequested();
                var want = (int)Math.Min(ChunkBytes, runBytes - done);
                var read = RandomAccess.Read(volume, buffer.AsSpan(carry, want), runOffset + done);

                if (read <= 0)
                    throw new JournalException(JournalError.Failed, "The file table ended early.");

                var meaningful = (int)Math.Min(read, info.MftValidDataLength - consumed);
                done += read;
                consumed += read;

                var available = carry + meaningful;
                var offset = 0;

                while (available - offset >= recordSize && number < capacity)
                {
                    Parse(buffer.AsSpan(offset, recordSize), number++, records);
                    offset += recordSize;
                }

                carry = available - offset;
                if (carry > 0) Buffer.BlockCopy(buffer, offset, buffer, 0, carry);
            }
        }

        var table = Assemble(root, records, capacity, denied, token);
        table.RecordsRead = number;
        return table;
    }

    // ------------------------------------------------------------------ records

    internal static void Parse(Span<byte> record, long number, Records records)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature || !ApplyFixups(record))
            return;

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);

        if ((flags & 0x01) == 0)
            return; // not in use

        var baseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & 0x0000FFFFFFFFFFFF);
        var owner = baseRecord != 0 ? baseRecord : number;

        if (owner >= records.InUse.Length)
            return;

        if (baseRecord == 0)
        {
            records.InUse[owner] = true;
            records.IsFolder[owner] = (flags & 0x02) != 0;
            records.Sequence[owner] = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
        }

        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

        while (at + 16 <= used)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);

            if (type == 0xFFFFFFFF)
                break;

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(at + 4)..]);

            if (length < 16 || at + length > used)
                break;

            var attribute = record.Slice(at, length);
            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];

            switch (type)
            {
                case 0x10 when !nonResident: // $STANDARD_INFORMATION
                {
                    var value = Resident(attribute);
                    if (value.Length >= 48)
                    {
                        records.Created[owner] = BinaryPrimitives.ReadInt64LittleEndian(value);
                        records.Modified[owner] = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
                        records.Attributes[owner] = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
                    }
                    if (value.Length >= 56)
                        records.SecurityId[owner] = BinaryPrimitives.ReadUInt32LittleEndian(value[52..]);
                    break;
                }

                case 0x30 when !nonResident: // $FILE_NAME
                {
                    var value = Resident(attribute);
                    if (value.Length < 66) break;
                    var parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value) & 0x0000FFFFFFFFFFFF);
                    var fileFlags = BinaryPrimitives.ReadUInt32LittleEndian(value[0x38..]);
                    var chars = value[64];
                    var nameSpace = value[65];

                    if ((fileFlags & ReparseAttribute) != 0)
                        records.ReparseTag[owner] = BinaryPrimitives.ReadUInt32LittleEndian(value[0x3C..]);

                    // 2 = DOS 8.3 alias only; the long name is its own attribute (namespace 1 or 3).
                    if (nameSpace == 2 || 66 + chars * 2 > value.Length || parent >= records.InUse.Length)
                        break;

                    records.AddLink((int)owner, (int)parent, MemoryMarshal.Cast<byte, char>(value.Slice(66, chars * 2)));
                    break;
                }

                case 0x80 when nameLength == 0: // unnamed $DATA: the file's size
                {
                    if (!nonResident)
                        records.Size[owner] = BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
                    else if (attribute.Length >= 0x40 && BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x10..]) == 0)
                        records.Size[owner] = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
                    break;
                }
            }

            at += length;
        }
    }

    // Each 512-byte stride ends with the update sequence number; the real two bytes are kept in the
    // update sequence array. A mismatch means the record was torn or damaged: it is skipped.
    internal static bool ApplyFixups(Span<byte> record)
    {
        var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);

        if (arrayCount < 2 || arrayOffset + arrayCount * 2 > record.Length)
            return false;

        var stride = record.Length / (arrayCount - 1);

        if (stride < 512 || stride * (arrayCount - 1) != record.Length)
            return false;

        var check = BinaryPrimitives.ReadUInt16LittleEndian(record[arrayOffset..]);

        for (var i = 1; i < arrayCount; i++)
        {
            var end = i * stride - 2;

            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != check)
                return false;

            BinaryPrimitives.WriteUInt16LittleEndian(record[end..],
                BinaryPrimitives.ReadUInt16LittleEndian(record[(arrayOffset + 2 * i)..]));
        }

        return true;
    }

    private static ReadOnlySpan<byte> Resident(ReadOnlySpan<byte> attribute)
    {
        if (attribute.Length < 0x18) return default;
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        return offset + length <= attribute.Length && length >= 0 ? attribute.Slice(offset, length) : default;
    }

    // Where the file table lives: the data runs of record 0's unnamed $DATA attribute.
    internal static List<(long Lcn, long Clusters)> TableRuns(Span<byte> record)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature || !ApplyFixups(record))
            throw new JournalException(JournalError.Failed, "The file table's own record is damaged.");

        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        var at = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

        while (at + 16 <= used)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
            if (type == 0xFFFFFFFF) break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(at + 4)..]);
            if (length < 16 || at + length > used) break;

            // An attribute list here means the table is too fragmented to describe in one record.
            if (type == 0x20)
                throw new JournalException(JournalError.NotSupported, "The file table is heavily fragmented; the drive is scanned by walking folders.");

            if (type == 0x80 && record[at + 8] != 0 && record[at + 9] == 0)
            {
                var runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 0x20)..]);
                return DecodeRuns(record.Slice(at + runsOffset, length - runsOffset));
            }

            at += length;
        }

        throw new JournalException(JournalError.Failed, "The file table's location could not be read.");
    }

    // Data runs: a header byte (low nibble = bytes of length, high nibble = bytes of signed LCN delta).
    internal static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        var p = 0;

        while (p < runs.Length && runs[p] != 0)
        {
            var header = runs[p++];
            var lengthBytes = header & 0x0F;
            var offsetBytes = header >> 4;

            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || p + lengthBytes + offsetBytes > runs.Length)
                throw new JournalException(JournalError.Failed, "The file table's location is damaged.");

            long clusters = 0;
            for (var i = 0; i < lengthBytes; i++) clusters |= (long)runs[p + i] << (8 * i);
            p += lengthBytes;

            if (offsetBytes == 0)
                throw new JournalException(JournalError.NotSupported, "The file table is sparse.");

            long delta = 0;
            for (var i = 0; i < offsetBytes; i++) delta |= (long)runs[p + i] << (8 * i);
            if ((runs[p + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8) delta -= 1L << (8 * offsetBytes);
            p += offsetBytes;

            lcn += delta;
            result.Add((lcn, clusters));
        }

        return result;
    }

    // ------------------------------------------------------------------ tree

    internal static FileTable Assemble(string root, Records records, long capacity,
        Func<IReadOnlyList<(ulong Key, ulong[] FileIds)>, HashSet<ulong>> denied, CancellationToken token)
    {
        // Children of each folder, as indexes into the link list (compressed adjacency).
        var childStart = new int[capacity + 1];
        for (var l = 0; l < records.Links; l++) childStart[records.LinkParent[l] + 1]++;
        for (var i = 0; i < capacity; i++) childStart[i + 1] += childStart[i];
        var fill = (int[])childStart.Clone();
        var children = new int[records.Links];
        for (var l = 0; l < records.Links; l++) children[fill[records.LinkParent[l]]++] = l;

        // One listing-permission check per distinct security descriptor, not per folder.
        var groups = new Dictionary<ulong, List<ulong>>();
        for (long r = FirstUserRecord; r < capacity; r++)
        {
            if (!records.InUse[r] || !records.IsFolder[r]) continue;
            var key = GroupKey(records, r);
            if (!groups.TryGetValue(key, out var ids)) groups[key] = ids = [];
            if (ids.Count < 3) ids.Add(FileId(records, r));
        }
        token.ThrowIfCancellationRequested();
        var blocked = denied([.. groups.Select(pair => (pair.Key, pair.Value.ToArray()))]);

        var table = new FileTable();
        table.Add(-1, root, DirectoryAttribute, 0, 0, 0);

        var visited = new bool[capacity];
        visited[RootRecord] = true;
        var queue = new Queue<(long Record, int Entry)>();
        queue.Enqueue((RootRecord, 0));

        while (queue.Count > 0)
        {
            var (folder, entry) = queue.Dequeue();

            if (folder != RootRecord && blocked.Contains(GroupKey(records, folder)))
            {
                table.DeniedFolders++;
                continue;
            }

            for (var c = childStart[folder]; c < childStart[folder + 1]; c++)
            {
                var link = children[c];
                var owner = records.LinkOwner[link];

                if (owner < FirstUserRecord || !records.InUse[owner])
                    continue;

                var isFolder = records.IsFolder[owner];
                var attributes = records.Attributes[owner] & 0x7FFFFFFF;
                if (isFolder) attributes |= DirectoryAttribute;
                if (attributes == 0) attributes = NormalAttribute;

                var name = records.NamePool.AsSpan(records.LinkNameStart[link], records.LinkNameLength[link]);
                var index = table.Count;
                table.Add(entry, name, attributes, isFolder ? 0 : records.Size[owner], records.Modified[owner], records.Created[owner]);

                if (isFolder && !visited[owner] && CanDescend(attributes, records.ReparseTag[owner]))
                {
                    visited[owner] = true;
                    queue.Enqueue((owner, index));
                }
            }

            if ((table.Count & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
        }

        return table;
    }

    // Same rule as FileIndexBuilder.CanDescend: plain folders, and cloud-sync placeholder folders.
    private static bool CanDescend(uint attributes, uint reparseTag)
        => (attributes & DirectoryAttribute) != 0 &&
           ((attributes & ReparseAttribute) == 0 || (reparseTag & 0xFFFF0FFF) == 0x9000001A);

    // Folders sharing a security descriptor share their listing permission. Without one (very old
    // NTFS versions), each folder is its own group.
    private static ulong GroupKey(Records records, long record)
        => records.SecurityId[record] != 0 ? records.SecurityId[record] : (1UL << 40) | (ulong)record;

    private static ulong FileId(Records records, long record) => ((ulong)records.Sequence[record] << 48) | (ulong)record;

    // ------------------------------------------------------------------ volume

    internal readonly record struct NtfsInfo(int BytesPerSector, long BytesPerCluster, int BytesPerRecord,
        long MftValidDataLength, long MftStartLcn);

    private static NtfsInfo VolumeData(SafeFileHandle volume)
    {
        var buffer = new byte[128];

        if (!DeviceIoControl(volume, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 96)
            throw new JournalException(JournalError.NotSupported, "This drive is not NTFS; it is scanned by walking folders.", Marshal.GetLastWin32Error());

        var span = buffer.AsSpan();
        return new NtfsInfo(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(span[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[44..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(span[48..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[56..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[64..]));
    }

    private static void ReadExactly(SafeFileHandle volume, byte[] buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(volume, buffer.AsSpan(total), offset + total);
            if (read <= 0) throw new JournalException(JournalError.Failed, "Could not read the file table.");
            total += read;
        }
    }

    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

// NEW: which folders the caller may list, checked once per security descriptor by opening up to three
// folders that use it (by file ID, with only "list folder" access). Run as the caller.
internal static class FolderAccess
{
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const int ERROR_ACCESS_DENIED = 5;

    public static HashSet<ulong> Denied(string root, IReadOnlyList<(ulong Key, ulong[] FileIds)> groups)
    {
        var blocked = new HashSet<ulong>();
        using var hint = UsnJournal.Native.CreateFileW(root, UsnJournal.Native.FILE_READ_ATTRIBUTES,
            UsnJournal.Native.FILE_SHARE_READ | UsnJournal.Native.FILE_SHARE_WRITE | UsnJournal.Native.FILE_SHARE_DELETE,
            IntPtr.Zero, UsnJournal.Native.OPEN_EXISTING, UsnJournal.Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

        if (hint.IsInvalid)
        {
            // Cannot even open the root: nothing below it would be listed by a walk either.
            foreach (var (key, _) in groups) blocked.Add(key);
            return blocked;
        }

        foreach (var (key, ids) in groups)
        {
            foreach (var id in ids)
            {
                var descriptor = new UsnJournal.Native.FILE_ID_DESCRIPTOR
                {
                    dwSize = Marshal.SizeOf<UsnJournal.Native.FILE_ID_DESCRIPTOR>(),
                    Type = UsnJournal.Native.FileIdType,
                    Low = id
                };

                using var folder = UsnJournal.Native.OpenFileById(hint, ref descriptor, FILE_LIST_DIRECTORY,
                    UsnJournal.Native.FILE_SHARE_READ | UsnJournal.Native.FILE_SHARE_WRITE | UsnJournal.Native.FILE_SHARE_DELETE,
                    IntPtr.Zero, UsnJournal.Native.FILE_FLAG_BACKUP_SEMANTICS);

                if (!folder.IsInvalid)
                    break; // allowed

                if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                {
                    blocked.Add(key);
                    break;
                }
                // Any other failure (the folder was just deleted, ...): try another folder with this descriptor.
            }
        }

        return blocked;
    }
}

// NEW: the binary form of a FileTable on the helper's pipe, after its JSON header line.
// Per entry: int32 parent, uint32 attributes, int64 size, int64 modified, int64 created, uint16 name length;
// then all names as UTF-16. Little-endian throughout.
internal static class FileTableTransfer
{
    public static void Write(Stream stream, FileTable table)
    {
        // FIXED (review): disposing a BufferedStream closes the stream under it, so the buffer is only
        // flushed, never disposed; the caller (the helper's pipe) still needs its stream afterwards.
        var buffered = new BufferedStream(stream, 1 << 20);
        using var writer = new BinaryWriter(buffered, System.Text.Encoding.Unicode, leaveOpen: true);

        for (var i = 0; i < table.Count; i++)
        {
            writer.Write(table.Parent[i]);
            writer.Write(table.Attributes[i]);
            writer.Write(table.Size[i]);
            writer.Write(table.Modified[i]);
            writer.Write(table.Created[i]);
            writer.Write(table.NameLength[i]);
        }

        writer.Write(MemoryMarshal.AsBytes(table.Names.AsSpan(0, table.NameChars)));
        writer.Flush();
        buffered.Flush();
    }

    public static FileTable Read(Stream stream, int count, int nameChars, CancellationToken token)
    {
        if (count < 1 || nameChars < 0)
            throw new InvalidDataException("The helper sent an empty file table.");

        using var reader = new BinaryReader(new BufferedStream(stream, 1 << 20), System.Text.Encoding.Unicode, leaveOpen: true);
        var table = new FileTable
        {
            Count = count,
            Parent = new int[count],
            Attributes = new uint[count],
            Size = new long[count],
            Modified = new long[count],
            Created = new long[count],
            NameLength = new ushort[count],
            Names = new char[nameChars],
            NameChars = nameChars
        };

        long total = 0;
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            table.Parent[i] = reader.ReadInt32();
            table.Attributes[i] = reader.ReadUInt32();
            table.Size[i] = reader.ReadInt64();
            table.Modified[i] = reader.ReadInt64();
            table.Created[i] = reader.ReadInt64();
            table.NameLength[i] = reader.ReadUInt16();
            total += table.NameLength[i];

            if (i > 0 && (table.Parent[i] < 0 || table.Parent[i] >= i))
                throw new InvalidDataException("The helper sent entries out of order.");
        }

        if (total != nameChars)
            throw new InvalidDataException("The helper's names do not add up.");

        var bytes = MemoryMarshal.AsBytes(table.Names.AsSpan());
        for (var read = 0; read < bytes.Length;)
        {
            var got = reader.Read(bytes[read..]);
            if (got <= 0) throw new EndOfStreamException("The helper stopped sending the file table.");
            read += got;
        }

        return table;
    }
}
