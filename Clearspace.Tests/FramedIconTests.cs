// FIXED (tiny tile icons): spotting a small icon that Windows returned inside a big, empty picture
// (Services/FramedIcon.cs). The pictures here are made in memory: see-through, with a thin frame at the
// edge like the one Windows draws, and a block of colour standing in for the icon.
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class FramedIconTests
{
    private const int Solid = unchecked((int)0xFF3366CC);
    private const int FrameLine = unchecked((int)0xFFD8D8D8);

    // A see-through square picture with Windows' thin frame round its edge.
    private static int[] Framed(int side)
    {
        var pixels = new int[side * side];
        for (var i = 0; i < side; i++)
        {
            pixels[i] = FrameLine;                       // top
            pixels[(side - 1) * side + i] = FrameLine;   // bottom
            pixels[i * side] = FrameLine;                // left
            pixels[i * side + side - 1] = FrameLine;     // right
        }
        return pixels;
    }

    private static void Block(int[] pixels, int side, int left, int top, int width, int height, int colour = Solid)
    {
        for (var y = top; y < top + height; y++)
            for (var x = left; x < left + width; x++)
                pixels[y * side + x] = colour;
    }

    [TestMethod]
    public void ASmallIconInTheMiddleOfAnEmptyPictureIsCutOut()
    {
        var pixels = Framed(256);
        Block(pixels, 256, 120, 120, 16, 16);

        var found = FramedIcon.Find(pixels, 256, 256);

        Assert.IsNotNull(found);
        // The icon, and one pixel all round for its soft edge.
        Assert.AreEqual(18, found.Value.Width);
        Assert.AreEqual(18, found.Value.Height);
        Assert.AreEqual(0, found.Value.Pixels[0], "The corner of the cut-out is the empty pixel beside the icon.");
        Assert.AreEqual(Solid, found.Value.Pixels[1 * 18 + 1]);
        Assert.AreEqual(Solid, found.Value.Pixels[16 * 18 + 16]);
        Assert.AreEqual(16 * 16, found.Value.Pixels.Count(pixel => pixel == Solid), "The frame is not part of the cut-out.");
    }

    [TestMethod]
    public void AnIconThatIsNotSquareKeepsItsShape()
    {
        var pixels = Framed(256);
        Block(pixels, 256, 104, 116, 48, 24);

        var found = FramedIcon.Find(pixels, 256, 256);

        Assert.IsNotNull(found);
        Assert.AreEqual(50, found.Value.Width);
        Assert.AreEqual(26, found.Value.Height);
    }

    [TestMethod]
    public void ASmallIconAwayFromTheMiddleIsFoundToo()
    {
        var pixels = new int[256 * 256]; // and no frame: it is the emptiness that is looked for
        Block(pixels, 256, 40, 40, 32, 32);

        var found = FramedIcon.Find(pixels, 256, 256);

        Assert.IsNotNull(found);
        Assert.AreEqual(34, found.Value.Width);
    }

    [TestMethod]
    public void AnIconThatFillsItsPictureIsLeftAlone()
    {
        var pixels = new int[256 * 256];
        Block(pixels, 256, 40, 40, 176, 176); // empty corners, like most real icons

        Assert.IsNull(FramedIcon.Find(pixels, 256, 256));
    }

    [TestMethod]
    public void AnIconJustOverTheLimitIsLeftAlone()
    {
        var limit = (int)(256 * FramedIcon.TinyShare);

        var under = new int[256 * 256];
        Block(under, 256, 60, 60, limit, limit);
        Assert.IsNotNull(FramedIcon.Find(under, 256, 256));

        var over = new int[256 * 256];
        Block(over, 256, 60, 60, limit + 1, limit + 1);
        Assert.IsNull(FramedIcon.Find(over, 256, 256));
    }

    [TestMethod]
    public void APictureWithAColouredBackgroundIsLeftAlone()
    {
        // A flat plate with a small mark in the middle is an icon's own design, not Windows' padding.
        var pixels = new int[256 * 256];
        Array.Fill(pixels, unchecked((int)0xFF808080));
        Block(pixels, 256, 120, 120, 16, 16);

        Assert.IsNull(FramedIcon.Find(pixels, 256, 256));
    }

    [TestMethod]
    public void AnEmptyPictureAndASmallPictureHaveNothingToCutOut()
    {
        Assert.IsNull(FramedIcon.Find(Framed(256), 256, 256), "Only a frame.");

        var small = new int[48 * 48];
        Block(small, 48, 20, 20, 8, 8);
        Assert.IsNull(FramedIcon.Find(small, 48, 48), "A 48-pixel picture is a small icon shown small.");
    }

    [TestMethod]
    public void AFaintShadowDoesNotCountAsPartOfTheIcon()
    {
        var pixels = Framed(256);
        Block(pixels, 256, 60, 60, 136, 136, unchecked((int)0x14000000)); // a wide, barely visible shadow
        Block(pixels, 256, 112, 112, 32, 32);

        var found = FramedIcon.Find(pixels, 256, 256);

        Assert.IsNotNull(found, "The shadow alone must not make the icon look big.");
        Assert.AreEqual(34, found.Value.Width);
    }

    [TestMethod]
    public void SpecksAreNotIcons()
    {
        var pixels = Framed(256);
        Block(pixels, 256, 126, 126, 4, 4);

        Assert.IsNull(FramedIcon.Find(pixels, 256, 256));
    }
}
