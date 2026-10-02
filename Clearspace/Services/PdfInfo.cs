// Clearspace | Title, authors and page count of a PDF.
//
// NEW (folder types, step 3). Windows has no built-in reader for PDF properties, so Research and
// Documents folders would show nothing for most papers. This reads what a PDF usually states near its
// start or end: the Info dictionary (/Title, /Author), the XMP metadata block (dc:title, dc:creator),
// and the page tree's /Count. Best effort: PDFs that keep these in compressed object streams show only
// what Windows or the file name give.

using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Clearspace.Services;

internal static partial class PdfInfo
{
    internal readonly record struct Result(string? Title, string? Authors, uint PageCount);

    private const int Window = 256 * 1024;

    public static Result? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

            var length = stream.Length;
            var head = ReadAt(stream, 0, (int)Math.Min(length, Window));
            var tail = length > Window ? ReadAt(stream, Math.Max(Window, length - Window), (int)Math.Min(length - Window, Window)) : [];

            // Latin-1 keeps one char per byte, so offsets and binary data survive the conversion.
            var text = Encoding.Latin1.GetString(head) + "\n" + Encoding.Latin1.GetString(tail);
            return Parse(text, wholeFile: length <= Window);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        var read = 0;

        while (read < count)
        {
            var got = stream.Read(buffer, read, count - read);
            if (got == 0) break;
            read += got;
        }

        return read == count ? buffer : buffer[..read];
    }

    internal static Result? Parse(string text, bool wholeFile)
    {
        if (!text.StartsWith("%PDF", StringComparison.Ordinal))
            return null;

        var title = Clean(InfoString(text, "/Title")) ?? Clean(XmpTitle(text));
        var authors = Clean(InfoString(text, "/Author")) ?? Clean(XmpCreators(text));
        var pages = PageCount(text, wholeFile);

        return title is null && authors is null && pages == 0 ? null : new Result(title, authors, pages);
    }

    // ---------------------------------------------------------------- Info dictionary

    private static string? InfoString(string text, string key)
    {
        for (var at = text.LastIndexOf(key, StringComparison.Ordinal); at >= 0; at = at == 0 ? -1 : text.LastIndexOf(key, at - 1, StringComparison.Ordinal))
        {
            // Bookmarks have a /Title too; only the document's Info dictionary counts.
            if (!IsInfoDictionary(text, at)) continue;

            var i = at + key.Length;
            while (i < text.Length && text[i] is ' ' or '\r' or '\n' or '\t') i++;
            if (i >= text.Length) continue;

            if (text[i] == '(' && Literal(text, i) is { } literal) return Decode(literal);
            if (text[i] == '<' && i + 1 < text.Length && text[i + 1] != '<' && Hex(text, i) is { } hex) return Decode(hex);
        }

        return null;
    }

    private static bool IsInfoDictionary(string text, int at)
    {
        var open = text.LastIndexOf("<<", at, StringComparison.Ordinal);
        var close = text.IndexOf(">>", at, StringComparison.Ordinal);
        if (open < 0 || close < 0 || close - open > 16_384) return false;

        var dictionary = text.AsSpan(open, close - open);
        return !dictionary.Contains("/Parent", StringComparison.Ordinal) &&
               (dictionary.Contains("/Producer", StringComparison.Ordinal) || dictionary.Contains("/Creator", StringComparison.Ordinal) ||
                dictionary.Contains("/CreationDate", StringComparison.Ordinal) || dictionary.Contains("/ModDate", StringComparison.Ordinal) ||
                dictionary.Contains("/Author", StringComparison.Ordinal));
    }

    // A (...) string: balanced parentheses, backslash escapes, octal codes.
    private static byte[]? Literal(string text, int open)
    {
        var bytes = new List<byte>();
        var depth = 0;

        for (var i = open; i < text.Length && bytes.Count < 4096; i++)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                switch (next)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add(8); break;
                    case 'f': bytes.Add(12); break;
                    case '\r' or '\n': break; // line continuation
                    case >= '0' and <= '7':
                        var value = next - '0';
                        for (var digits = 1; digits < 3 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7'; digits++)
                            value = value * 8 + (text[++i] - '0');
                        bytes.Add((byte)value);
                        break;
                    default: bytes.Add((byte)next); break;
                }
                continue;
            }

            if (c == '(' && depth++ == 0) continue;
            if (c == ')' && --depth == 0) return [.. bytes];
            bytes.Add((byte)c);
        }

        return null;
    }

    private static byte[]? Hex(string text, int open)
    {
        var close = text.IndexOf('>', open);
        if (close < 0 || close - open > 8192) return null;

        var digits = new StringBuilder();
        for (var i = open + 1; i < close; i++)
            if (Uri.IsHexDigit(text[i])) digits.Append(text[i]);
        if (digits.Length % 2 == 1) digits.Append('0');

        try { return Convert.FromHexString(digits.ToString()); }
        catch (FormatException) { return null; }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }

    // ---------------------------------------------------------------- XMP metadata

    [GeneratedRegex(@"<dc:title>.*?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Singleline)]
    private static partial Regex XmpTitleRegex();

    [GeneratedRegex(@"<dc:creator>(.*?)</dc:creator>", RegexOptions.Singleline)]
    private static partial Regex XmpCreatorRegex();

    [GeneratedRegex(@"<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Singleline)]
    private static partial Regex XmpItemRegex();

    private static string? XmpTitle(string text)
        => XmpTitleRegex().Match(text) is { Success: true } match ? Xml(match.Groups[1].Value) : null;

    private static string? XmpCreators(string text)
    {
        if (XmpCreatorRegex().Match(text) is not { Success: true } creators)
            return null;

        var names = XmpItemRegex().Matches(creators.Groups[1].Value).Select(item => Xml(item.Groups[1].Value))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        return names.Length == 0 ? null : string.Join("; ", names);
    }

    // XMP is UTF-8 text; the file was read as Latin-1, so turn the chars back into bytes first.
    private static string Xml(string latin1) => WebUtility.HtmlDecode(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(latin1)));

    // ---------------------------------------------------------------- pages

    [GeneratedRegex(@"/Type\s*/Pages\b")]
    private static partial Regex PagesNodeRegex();

    [GeneratedRegex(@"/Count\s+(\d+)")]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageRegex();

    private static uint PageCount(string text, bool wholeFile)
    {
        // The page tree's root has the largest /Count of the /Pages nodes near it.
        uint best = 0;

        foreach (Match node in PagesNodeRegex().Matches(text))
        {
            var start = Math.Max(0, node.Index - 400);
            var window = text.Substring(start, Math.Min(text.Length - start, 800));

            foreach (Match count in CountRegex().Matches(window))
                if (uint.TryParse(count.Groups[1].Value, out var value) && value > best && value < 200_000)
                    best = value;
        }

        // Small files read whole: count the pages themselves when the tree was not found.
        if (best == 0 && wholeFile)
            best = (uint)PageRegex().Matches(text).Count;

        return best;
    }

    // ---------------------------------------------------------------- tidy

    private static string? Clean(string? value)
    {
        if (value is null) return null;

        value = value.Replace('\0', ' ').Trim();
        if (value.StartsWith("Microsoft Word - ", StringComparison.OrdinalIgnoreCase)) value = value[17..].Trim();

        // Placeholders and file names are not titles.
        if (value.Length == 0 || value.Equals("untitled", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".dvi", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".doc", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return null;

        return value.Length > 300 ? value[..300] : value;
    }
}
