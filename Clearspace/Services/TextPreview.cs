// Clearspace | NEW (preview pane): reads the start of a file and decides whether it is text.
// Any file can be asked: there is no list of endings to keep up to date, so LICENSE, .gitignore and a
// .log all preview, while a program or a picture is recognised as not being text from what is in it.
// Only the first part of the file is read, however large it is.
using System.IO;
using System.Text;

namespace Clearspace.Services;

internal sealed record TextSample(string Text, bool Truncated);

internal static class TextPreview
{
    internal const int MaxBytes = 48 * 1024;

    // The start of the file as text, or null when it is not text or can't be read.
    internal static TextSample? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, MaxBytes)];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Decode(buffer.AsSpan(0, length), truncated: stream.Length > length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static TextSample? Decode(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (bytes.Length == 0)
            return new TextSample(string.Empty, false);

        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes[3..]);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes[2..(bytes.Length - bytes.Length % 2)]);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(bytes[2..(bytes.Length - bytes.Length % 2)]);
        }
        else
        {
            if (LooksBinary(bytes))
                return null;

            text = Encoding.UTF8.GetString(bytes);
        }

        // A cut in the middle of a letter that takes several bytes leaves a replacement mark at the end.
        if (truncated)
            text = text.TrimEnd('�');

        return new TextSample(text, truncated);
    }

    // Text has no zero bytes and next to no control characters (tab, new line, form feed and escape are fine).
    internal static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var control = 0;
        foreach (var value in bytes)
        {
            if (value == 0)
                return true;
            if (value < 0x20 && value is not (9 or 10 or 12 or 13 or 27))
                control++;
        }

        return control * 20 > bytes.Length;
    }
}
