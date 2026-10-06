// NEW (preview pane, folder opener): telling text from other files (Services/TextPreview.cs), and reading
// the path Windows passes when a drive is opened with Clearspace (Services/ShellCommands.cs).
using System.IO;
using System.Text;
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class PreviewAndOpenerTests
{
    [TestMethod]
    public void PlainTextIsReadWhateverItsEnding()
    {
        var sample = TextPreview.Decode(Encoding.UTF8.GetBytes("line one\r\n\tline two – café\n"), truncated: false);

        Assert.IsNotNull(sample);
        Assert.AreEqual("line one\r\n\tline two – café\n", sample.Text);
        Assert.IsFalse(sample.Truncated);

        Assert.AreEqual(string.Empty, TextPreview.Decode([], truncated: false)!.Text, "an empty file is empty text");
    }

    [TestMethod]
    public void ByteOrderMarksChooseTheEncoding()
    {
        byte[] utf8 = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo")];
        Assert.AreEqual("héllo", TextPreview.Decode(utf8, truncated: false)!.Text);

        // UTF-16 text is full of zero bytes, which would otherwise mark it as not text.
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("héllo")];
        Assert.AreEqual("héllo", TextPreview.Decode(utf16, truncated: false)!.Text);

        byte[] bigEndian = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("héllo")];
        Assert.AreEqual("héllo", TextPreview.Decode(bigEndian, truncated: false)!.Text);

        byte[] oddLength = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hi"), 0x41];
        Assert.AreEqual("hi", TextPreview.Decode(oddLength, truncated: true)!.Text, "half a letter at the cut is dropped");
    }

    [TestMethod]
    public void ProgramsAndPicturesAreNotText()
    {
        Assert.IsNull(TextPreview.Decode([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00], truncated: false), "a zero byte");
        Assert.IsTrue(TextPreview.LooksBinary([0x01, 0x02, 0x03, 0x04, 0x41, 0x42]), "mostly control characters");
        Assert.IsFalse(TextPreview.LooksBinary(Encoding.UTF8.GetBytes("\u001b[31mred\u001b[0m in a log\r\n")), "an escape code or two is still text");
        Assert.IsFalse(TextPreview.LooksBinary(Encoding.UTF8.GetBytes("日本語のテキスト")), "letters outside English are not control characters");
    }

    [TestMethod]
    public void OnlyTheStartOfALargeFileIsRead()
    {
        var path = Path.Combine(Path.GetTempPath(), "Clearspace preview " + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            // 'é' takes two bytes, so the cut at MaxBytes lands in the middle of one.
            File.WriteAllText(path, "x" + new string('é', TextPreview.MaxBytes));

            var sample = TextPreview.Read(path);

            Assert.IsNotNull(sample);
            Assert.IsTrue(sample.Truncated);
            Assert.IsTrue(sample.Text.Length < TextPreview.MaxBytes);
            Assert.IsFalse(sample.Text.Contains('\uFFFD'), "no broken letter at the cut");
            Assert.IsTrue(sample.Text.StartsWith("xé", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.IsNull(TextPreview.Read(path), "a file that can't be opened has no preview");
    }

    [TestMethod]
    public void ADriveOpenedFromWindowsArrivesWithItsBackslash()
    {
        // The registry command is: Clearspace.exe --open "%1". For a drive %1 is C:\ and the backslash in
        // front of the closing quote turns that quote into text, so the argument arrives as C:" .
        Assert.AreEqual(new ShellCommand(ShellVerb.Open, @"D:\"), ShellCommands.Parse(["--open", "D:\""]));
        Assert.AreEqual(new ShellCommand(ShellVerb.Open, @"C:\Users\Sam"), ShellCommands.Parse(["--open", @"C:\Users\Sam"]));
        Assert.IsNull(ShellCommands.Parse(["--open", "1:\""]), "not a drive letter");
    }
}
