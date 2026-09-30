// Clearspace | NTFS change journal access.
//
// NEW (journal catch-up): shared by Clearspace (when it runs as administrator) and ClearspaceIndexHelper
// (the optional service). Uses only the base class library so the helper can compile it too.
//
// Windows keeps a change journal on each NTFS volume: every create, delete, rename and content change
// gets a record with an increasing sequence number (USN). Saving the journal ID and the last USN with
// the index lets Clearspace read only what changed since, instead of walking the whole drive again.
// Opening the volume to read the journal needs administrator rights; turning file IDs into paths does
// not, so the helper does that part while impersonating the person who asked.

using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Journal;

internal readonly record struct JournalState(ulong JournalId, long FirstUsn, long NextUsn);

internal readonly record struct JournalRecord(UInt128 FileId, UInt128 ParentId, string Name, uint Reasons, uint Attributes, long Usn)
{
    public bool IsDirectory => (Attributes & 0x10) != 0;
}

internal enum JournalError
{
    AccessDenied,
    NotSupported,
    NotActive,
    HistoryLost,
    TooManyChanges,
    Failed
}

internal sealed class JournalException(JournalError error, string message, int win32Error = 0) : Exception(message)
{
    public JournalError Error { get; } = error;
    public int Win32Error { get; } = win32Error;
}

internal static class UsnJournal
{
    // USN_REASON flags that can change what the index holds (name, location, size, dates, attributes).
    internal const uint DataOverwrite = 0x00000001;
    internal const uint DataExtend = 0x00000002;
    internal const uint DataTruncation = 0x00000004;
    internal const uint FileCreate = 0x00000100;
    internal const uint FileDelete = 0x00000200;
    internal const uint RenameOldName = 0x00001000;
    internal const uint RenameNewName = 0x00002000;
    internal const uint BasicInfoChange = 0x00008000;
    internal const uint IndexRelevant = DataOverwrite | DataExtend | DataTruncation | FileCreate | FileDelete |
                                        RenameOldName | RenameNewName | BasicInfoChange;

    // A safety valve: past this many records a full rescan is cheaper than resolving every path.
    internal const int MaxRecords = 3_000_000;

    private const int BufferSize = 256 * 1024;

    // "C:\" only: the journal belongs to local volumes with a drive letter.
    internal static bool IsDriveRoot(string root)
        => root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\';

    internal static SafeFileHandle OpenVolume(string root)
    {
        if (!IsDriveRoot(root))
            throw new JournalException(JournalError.NotSupported, $"{root} is not a local drive.");

        var handle = Native.CreateFileW($@"\\.\{root[0]}:", Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

        if (!handle.IsInvalid)
            return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw error == Native.ERROR_ACCESS_DENIED
            ? new JournalException(JournalError.AccessDenied, "Reading the change journal needs administrator rights.", error)
            : new JournalException(JournalError.Failed, $"Could not open {root} ({error}).", error);
    }

    internal static JournalState Query(SafeFileHandle volume)
    {
        if (Native.DeviceIoControl(volume, Native.FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out Native.USN_JOURNAL_DATA_V0 data,
                Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
            return new JournalState(data.UsnJournalID, data.FirstUsn, data.NextUsn);

        throw Translate(Marshal.GetLastWin32Error());
    }

    // Every record from `fromUsn` up to (not including) `untilUsn`, oldest first.
    internal static List<JournalRecord> Read(SafeFileHandle volume, ulong journalId, long fromUsn, long untilUsn, CancellationToken token)
    {
        var records = new List<JournalRecord>();
        var buffer = new byte[BufferSize];
        var input = new Native.READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = fromUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId
        };

        while (input.StartUsn < untilUsn)
        {
            token.ThrowIfCancellationRequested();

            if (!Native.DeviceIoControl(volume, Native.FSCTL_READ_USN_JOURNAL, ref input, Marshal.SizeOf<Native.READ_USN_JOURNAL_DATA_V0>(),
                    buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();

                if (error == Native.ERROR_HANDLE_EOF)
                    break;

                throw Translate(error);
            }

            if (returned <= 8)
                break;

            var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            var offset = 8;

            while (offset + 8 <= returned)
            {
                var span = buffer.AsSpan(offset, returned - offset);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);

                if (length <= 0 || length > span.Length)
                    break;

                if (TryParse(span[..length], out var record) && record.Usn < untilUsn)
                {
                    records.Add(record);

                    if (records.Count > MaxRecords)
                        throw new JournalException(JournalError.TooManyChanges, "Too many changes to replay; a full scan is faster.");
                }

                offset += length;
            }

            if (next <= input.StartUsn)
                break;

            input.StartUsn = next;
        }

        return records;
    }

    // USN_RECORD_V2 (64-bit file IDs, NTFS) and USN_RECORD_V3 (128-bit IDs, ReFS).
    internal static bool TryParse(ReadOnlySpan<byte> span, out JournalRecord record)
    {
        record = default;

        if (span.Length < 8)
            return false;

        var major = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        UInt128 fileId, parentId;
        int at;

        if (major == 2)
        {
            if (span.Length < 60) return false;
            fileId = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
            parentId = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
            at = 24;
        }
        else if (major == 3)
        {
            if (span.Length < 76) return false;
            fileId = BinaryPrimitives.ReadUInt128LittleEndian(span[8..]);
            parentId = BinaryPrimitives.ReadUInt128LittleEndian(span[24..]);
            at = 40;
        }
        else
            return false;

        // Usn, TimeStamp, Reason, SourceInfo, SecurityId, FileAttributes, FileNameLength, FileNameOffset
        var usn = BinaryPrimitives.ReadInt64LittleEndian(span[at..]);
        var reasons = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 16)..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 28)..]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 32)..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 34)..]);

        if (nameOffset + nameLength > span.Length || nameLength % 2 != 0)
            return false;

        var name = new string(MemoryMarshal.Cast<byte, char>(span.Slice(nameOffset, nameLength)));
        record = new JournalRecord(fileId, parentId, name, reasons, attributes, usn);
        return true;
    }

    private static JournalException Translate(int error) => error switch
    {
        Native.ERROR_ACCESS_DENIED => new(JournalError.AccessDenied, "Reading the change journal needs administrator rights.", error),
        Native.ERROR_INVALID_FUNCTION or Native.ERROR_NOT_SUPPORTED =>
            new(JournalError.NotSupported, "This drive's file system has no change journal.", error),
        Native.ERROR_JOURNAL_NOT_ACTIVE or Native.ERROR_JOURNAL_DELETE_IN_PROGRESS or Native.ERROR_INVALID_PARAMETER =>
            new(JournalError.NotActive, "The change journal is off or was recreated.", error),
        Native.ERROR_JOURNAL_ENTRY_DELETED =>
            new(JournalError.HistoryLost, "The change journal no longer reaches back to the last catch-up.", error),
        _ => new(JournalError.Failed, $"The change journal could not be read ({error}).", error)
    };

    internal static class Native
    {
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FILE_READ_ATTRIBUTES = 0x80;
        internal const uint FILE_LIST_DIRECTORY = 0x01;
        internal const uint FILE_SHARE_READ = 0x1;
        internal const uint FILE_SHARE_WRITE = 0x2;
        internal const uint FILE_SHARE_DELETE = 0x4;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        internal const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
        internal const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

        internal const int ERROR_INVALID_FUNCTION = 1;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_HANDLE_EOF = 38;
        internal const int ERROR_NOT_SUPPORTED = 50;
        internal const int ERROR_INVALID_PARAMETER = 87;
        internal const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
        internal const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
        internal const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

        [StructLayout(LayoutKind.Sequential)]
        internal struct USN_JOURNAL_DATA_V0
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct READ_USN_JOURNAL_DATA_V0
        {
            public long StartUsn;
            public uint ReasonMask;
            public uint ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
        }

        // FILE_ID_DESCRIPTOR: DWORD size, FILE_ID_TYPE, then a union aligned to 8 bytes.
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILE_ID_DESCRIPTOR
        {
            public int dwSize;
            public int Type;
            public ulong Low;
            public ulong High;
        }

        internal const int FileIdType = 0;
        internal const int ExtendedFileIdType = 2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize,
            out USN_JOURNAL_DATA_V0 output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref READ_USN_JOURNAL_DATA_V0 input,
            int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id,
            uint access, uint share, IntPtr security, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, int length, uint flags);
    }
}

// NEW: turns folder IDs into current paths. Opens nothing but the drive root and the IDs asked about.
// FIXED (permission review): a folder is opened with "list folder" access, not just "read attributes".
// The names returned under it come from the journal, so the caller must be allowed to list that folder -
// exactly what a folder walk by the same account would need to see those names.
internal sealed class FileIdResolver : IDisposable
{
    private readonly SafeFileHandle _hint;
    private char[] _buffer = new char[512];

    public FileIdResolver(string root)
    {
        _hint = UsnJournal.Native.CreateFileW(root, UsnJournal.Native.FILE_READ_ATTRIBUTES,
            UsnJournal.Native.FILE_SHARE_READ | UsnJournal.Native.FILE_SHARE_WRITE | UsnJournal.Native.FILE_SHARE_DELETE,
            IntPtr.Zero, UsnJournal.Native.OPEN_EXISTING, UsnJournal.Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
    }

    // The item's current full path ("C:\Users\…"), or null when it no longer exists or cannot be opened.
    public string? PathOf(UInt128 id)
    {
        if (_hint.IsInvalid)
            return null;

        var high = (ulong)(id >> 64);
        var descriptor = new UsnJournal.Native.FILE_ID_DESCRIPTOR
        {
            dwSize = Marshal.SizeOf<UsnJournal.Native.FILE_ID_DESCRIPTOR>(),
            Type = high == 0 ? UsnJournal.Native.FileIdType : UsnJournal.Native.ExtendedFileIdType,
            Low = (ulong)id,
            High = high
        };

        using var handle = UsnJournal.Native.OpenFileById(_hint, ref descriptor, UsnJournal.Native.FILE_LIST_DIRECTORY | UsnJournal.Native.FILE_READ_ATTRIBUTES,
            UsnJournal.Native.FILE_SHARE_READ | UsnJournal.Native.FILE_SHARE_WRITE | UsnJournal.Native.FILE_SHARE_DELETE,
            IntPtr.Zero, UsnJournal.Native.FILE_FLAG_BACKUP_SEMANTICS);

        if (handle.IsInvalid)
            return null;

        var length = UsnJournal.Native.GetFinalPathNameByHandleW(handle, _buffer, _buffer.Length, 0);

        if (length > _buffer.Length)
        {
            _buffer = new char[length + 1];
            length = UsnJournal.Native.GetFinalPathNameByHandleW(handle, _buffer, _buffer.Length, 0);
        }

        if (length <= 0 || length > _buffer.Length)
            return null;

        var path = new string(_buffer, 0, length);
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    public void Dispose() => _hint.Dispose();
}
