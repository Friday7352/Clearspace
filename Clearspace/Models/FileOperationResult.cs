// Clearspace | Structured outcomes for Windows shell file operations.

namespace Clearspace.Models;

public enum FileOperationKind { Copy, Move, Rename, Delete }
public enum FileOperationStatus { Succeeded, Canceled, Failed }
public enum FileOperationError { None, InvalidInput, AccessDenied, NotFound, Other }

public sealed record FileOperationResult
{
    private FileOperationResult(FileOperationKind operation, FileOperationStatus status,
        FileOperationError error, int? nativeErrorCode, bool anyOperationsAborted, string detail)
    {
        Operation = operation;
        Status = status;
        Error = error;
        NativeErrorCode = nativeErrorCode;
        AnyOperationsAborted = anyOperationsAborted;
        Message = $"{operation}: {detail}";
    }

    public FileOperationKind Operation { get; }
    public FileOperationStatus Status { get; }
    public FileOperationError Error { get; }
    // The return value from SHFileOperationW, never GetLastError. Null means it was not called.
    public int? NativeErrorCode { get; }
    public bool AnyOperationsAborted { get; }
    public bool Succeeded => Status == FileOperationStatus.Succeeded;
    public bool Canceled => Status == FileOperationStatus.Canceled;
    public string Message { get; }

    internal static FileOperationResult InvalidInput(FileOperationKind operation, string detail)
        => new(operation, FileOperationStatus.Failed, FileOperationError.InvalidInput, null, false, detail);

    internal static FileOperationResult FromShellResult(FileOperationKind operation, int code, bool aborted)
    {
        // Legacy shell codes overlap Win32 codes. Keep unknown values intact rather than
        // decoding them with Win32Exception and potentially displaying the wrong explanation.
        var error = code switch
        {
            0 or 0x75 or 1223 => FileOperationError.None,
            5 or 0x78 => FileOperationError.AccessDenied,
            2 or 3 => FileOperationError.NotFound,
            _ => FileOperationError.Other
        };

        if (aborted || code is 0x75 or 1223)
            return new(operation, FileOperationStatus.Canceled, error, code, aborted,
                $"Canceled or stopped before completion. Some items may already have changed. Windows code: {code} (0x{code:X}).");

        if (code == 0)
            return new(operation, FileOperationStatus.Succeeded, FileOperationError.None, code, false, "Completed.");

        var detail = error switch
        {
            FileOperationError.AccessDenied => "Windows reported access denied. Check your permissions.",
            FileOperationError.NotFound => "Windows reported a missing file or folder. Check the source and destination.",
            _ => "Windows could not complete the operation."
        };
        return new(operation, FileOperationStatus.Failed, error, code, false,
            $"{detail} Some items may already have changed. Windows code: {code} (0x{code:X}).");
    }
}
