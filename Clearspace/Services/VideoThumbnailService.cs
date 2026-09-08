// Clearspace | Video-frame thumbnail extraction.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearspace.Services;

internal static class VideoThumbnailService
{
    private const int MfVersion = 0x00020070;
    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int EndOfStream = 0x00000002;
    private const int CurrentMediaTypeChanged = 0x00000020;

    private static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid EnableHardwareTransforms = new("A634A91C-822B-41B9-A494-4DE4643612B0");

    private static readonly object StartupLock = new();
    private static bool _started;
    private static bool _decoderUnavailable;

    internal static BitmapSource? Extract(string path, int requestedSize)
    {
        if (_decoderUnavailable)
            return null;

        EnsureStarted();
        var stage = "startup";

        IMFSourceReader? reader = null;
        IMFMediaType? nativeType = null;
        IMFMediaType? requestedType = null;
        IMFMediaType? actualType = null;

        try
        {
            stage = "open source reader";
            ThrowIfFailed(MFCreateSourceReaderFromURL(path, null, out reader));

            stage = "select video stream";
            ThrowIfFailed(reader.SetStreamSelection(AllStreams, false));
            ThrowIfFailed(reader.SetStreamSelection(FirstVideoStream, true));

            stage = "get native media type";
            ThrowIfFailed(reader.GetNativeMediaType(FirstVideoStream, 0, out nativeType));
            var frameSizeKey = MfMtFrameSize;
            stage = "get native frame size";
            ThrowIfFailed(nativeType.GetUINT64(ref frameSizeKey, out var packedSize));
            var width = (int)(packedSize >> 32);
            var height = (int)(packedSize & 0xFFFFFFFF);

            stage = "create NV12 media type";
            ThrowIfFailed(MFCreateMediaType(out requestedType));
            var majorTypeKey = MfMtMajorType;
            var subtypeKey = MfMtSubtype;
            var videoType = MfMediaTypeVideo;
            var nv12Type = MfVideoFormatNv12;
            ThrowIfFailed(requestedType.SetGUID(ref majorTypeKey, ref videoType));
            ThrowIfFailed(requestedType.SetGUID(ref subtypeKey, ref nv12Type));
            stage = "set NV12 media type";
            ThrowIfFailed(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, requestedType));
            stage = "get NV12 media type";
            ThrowIfFailed(reader.GetCurrentMediaType(FirstVideoStream, out actualType));

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                return null;

            for (var attempt = 0; attempt < 180; attempt++)
            {
                IMFSample? sample = null;
                try
                {
                    stage = "decode sample";
                    ThrowIfFailed(reader.ReadSample(
                        FirstVideoStream,
                        0,
                        out _,
                        out var flags,
                        out _,
                        out sample));

                    if ((flags & EndOfStream) != 0)
                        return null;

                    if ((flags & CurrentMediaTypeChanged) != 0)
                    {
                        Release(actualType);
                        actualType = null;
                        ThrowIfFailed(reader.GetCurrentMediaType(FirstVideoStream, out actualType));
                        frameSizeKey = MfMtFrameSize;
                        ThrowIfFailed(actualType.GetUINT64(ref frameSizeKey, out packedSize));
                        width = (int)(packedSize >> 32);
                        height = (int)(packedSize & 0xFFFFFFFF);
                    }

                    if (sample is null)
                        continue;

                    return ReadNv12Frame(sample, width, height, requestedSize);
                }
                finally
                {
                    Release(sample);
                }
            }

            return null;
        }
        catch (COMException exception)
        {
            System.Diagnostics.Trace.WriteLine($"Video thumbnail failed at {stage} for {path}: 0x{exception.HResult:X8} {exception.Message}");
            if (exception.HResult == unchecked((int)0xC00D36E6))
                _decoderUnavailable = true;
            return null;
        }
        finally
        {
            Release(actualType);
            Release(requestedType);
            Release(nativeType);
            Release(reader);
        }
    }

    private static BitmapSource? ReadNv12Frame(IMFSample sample, int width, int height, int requestedSize)
    {
        IMFMediaBuffer? buffer = null;
        var data = IntPtr.Zero;

        try
        {
            ThrowIfFailed(sample.ConvertToContiguousBuffer(out buffer));
            ThrowIfFailed(buffer.Lock(out data, out _, out var currentLength));

            var chromaRows = (height + 1) / 2;
            var totalRows = height + chromaRows;
            if (currentLength < width * totalRows)
                return null;

            var sourceStride = currentLength / totalRows;
            if (sourceStride < width)
                sourceStride = width;

            var raw = new byte[currentLength];
            Marshal.Copy(data, raw, 0, Math.Min(raw.Length, currentLength));

            var outputSize = Math.Max(128, requestedSize);
            var scale = Math.Min((double)outputSize / width, (double)outputSize / height);
            var drawWidth = Math.Max(1, (int)Math.Round(width * scale));
            var drawHeight = Math.Max(1, (int)Math.Round(height * scale));
            var offsetX = (outputSize - drawWidth) / 2;
            var offsetY = (outputSize - drawHeight) / 2;
            var outputStride = outputSize * 4;
            var pixels = new byte[outputStride * outputSize];
            var uvOffset = sourceStride * height;

            for (var y = 0; y < drawHeight; y++)
            {
                var sourceY = Math.Min(height - 1, y * height / drawHeight);
                var yRow = sourceY * sourceStride;
                var uvRow = uvOffset + (sourceY / 2) * sourceStride;
                var destination = (offsetY + y) * outputStride + offsetX * 4;

                for (var x = 0; x < drawWidth; x++)
                {
                    var sourceX = Math.Min(width - 1, x * width / drawWidth);
                    var yValue = raw[yRow + sourceX];
                    var uvIndex = uvRow + (sourceX & ~1);
                    if (uvIndex + 1 >= raw.Length)
                        continue;

                    var u = raw[uvIndex];
                    var v = raw[uvIndex + 1];
                    var c = Math.Max(0, yValue - 16);
                    var d = u - 128;
                    var e = v - 128;

                    pixels[destination++] = Clamp((298 * c + 516 * d + 128) >> 8);
                    pixels[destination++] = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                    pixels[destination++] = Clamp((298 * c + 409 * e + 128) >> 8);
                    pixels[destination++] = 255;
                }
            }

            var thumbnail = BitmapSource.Create(
                outputSize,
                outputSize,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                outputStride);
            thumbnail.Freeze();
            return thumbnail;
        }
        finally
        {
            if (data != IntPtr.Zero && buffer is not null)
                buffer.Unlock();
            Release(buffer);
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void EnsureStarted()
    {
        if (_started)
            return;

        lock (StartupLock)
        {
            if (_started)
                return;

            ThrowIfFailed(MFStartup(MfVersion, 0));
            _started = true;
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes([MarshalAs(UnmanagedType.Interface)] out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMFMediaType mediaType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(
        string url,
        [MarshalAs(UnmanagedType.Interface)] IMFAttributes? attributes,
        [MarshalAs(UnmanagedType.Interface)] out IMFSourceReader sourceReader);

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, int size, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] new int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int GetItemType(ref Guid key, out int type);
        [PreserveSig] new int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] new int GetUINT32(ref Guid key, out int value);
        [PreserveSig] new int GetUINT64(ref Guid key, out long value);
        [PreserveSig] new int GetDouble(ref Guid key, out double value);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out int length);
        [PreserveSig] new int GetString(ref Guid key, System.Text.StringBuilder value, int size, out int length);
        [PreserveSig] new int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] new int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
        [PreserveSig] new int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] new int GetUnknown(ref Guid key, ref Guid iid, out object value);
        [PreserveSig] new int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] new int DeleteItem(ref Guid key);
        [PreserveSig] new int DeleteAllItems();
        [PreserveSig] new int SetUINT32(ref Guid key, int value);
        [PreserveSig] new int SetUINT64(ref Guid key, long value);
        [PreserveSig] new int SetDouble(ref Guid key, double value);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int SetString(ref Guid key, string value);
        [PreserveSig] new int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] new int SetUnknown(ref Guid key, object value);
        [PreserveSig] new int LockStore();
        [PreserveSig] new int UnlockStore();
        [PreserveSig] new int GetCount(out int count);
        [PreserveSig] new int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] new int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType mediaType, out int flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
        [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);
        [PreserveSig] int Flush(int streamIndex);
        [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid iid, out IntPtr value);
        [PreserveSig] int GetPresentationAttribute(int streamIndex, ref Guid attribute, IntPtr value);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample : IMFAttributes
    {
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int currentLength);
        [PreserveSig] int SetCurrentLength(int currentLength);
        [PreserveSig] int GetMaxLength(out int maxLength);
    }
}
