// NEW (drag to select): the arithmetic behind dragging a rectangle over the file list (Services/BandSelect.cs):
// where a point is among the rows, which rows or tiles a rectangle touches, what Ctrl and Shift do, and
// how fast the list scrolls with the pointer held at an edge. The mouse handling, the drawing and the
// scrolling themselves need a window and are checked by hand.
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class BandSelectTests
{
    // Rows 10, 11 and 12 on screen, 30 pixels each, with a 20 pixel group heading before row 12.
    private static readonly BandRow[] Rows =
    [
        new(10, 0, 30),
        new(11, 30, 60),
        new(12, 80, 110)
    ];

    [TestMethod]
    public void APointIsTiedToTheRowItIsInOrTheGapAfterTheRowAboveIt()
    {
        var inRow = BandSelect.Locate(Rows, 45);
        Assert.AreEqual(11, inRow.Row);
        Assert.AreEqual(15, inRow.Offset, 0.001);
        Assert.AreEqual(11.5, inRow.Position, 0.001);

        var onHeading = BandSelect.Locate(Rows, 70);
        Assert.AreEqual(11, onHeading.Row, "tied to the row above the heading");
        Assert.AreEqual(12, onHeading.Position, 0.001, "between rows 11 and 12: in neither");

        var below = BandSelect.Locate(Rows, 400);
        Assert.AreEqual(13, below.Position, 0.001, "the empty space under the last row");

        var above = BandSelect.Locate(Rows, -5);
        Assert.AreEqual(10, above.Position, 0.001, "before the first row on screen");

        Assert.AreEqual(0, BandSelect.Locate([], 50).Position, "an empty folder");
    }

    [TestMethod]
    public void TheRowsOnScreenMayComeInAnyOrder()
    {
        BandRow[] shuffled = [Rows[2], Rows[0], Rows[1]];
        Assert.AreEqual(11.5, BandSelect.Locate(shuffled, 45).Position, 0.001);
        Assert.AreEqual(12, BandSelect.Locate(shuffled, 70).Position, 0.001);
        Assert.AreEqual(10, BandSelect.Locate(shuffled, -5).Position, 0.001);
    }

    [TestMethod]
    public void AfterScrollingThePointIsStillWhereItWasInTheList()
    {
        var start = BandSelect.Locate(Rows, 45); // halfway down row 11

        // The list scrolled down by 100 pixels: row 11 is now above the screen.
        BandRow[] later = [new(14, 0, 30), new(15, 30, 60)];
        Assert.IsNull(BandSelect.Where(later, start, out var above));
        Assert.IsTrue(above, "it went off the top");

        // And back up a little: row 11 is on screen again, drawn at 200.
        BandRow[] back = [new(10, 170, 200), new(11, 200, 230)];
        Assert.AreEqual(215, BandSelect.Where(back, start, out _)!.Value, 0.001);

        // Scrolled the other way, it is off the bottom.
        BandRow[] earlier = [new(2, 0, 30), new(3, 30, 60)];
        Assert.IsNull(BandSelect.Where(earlier, start, out above));
        Assert.IsFalse(above);
    }

    [TestMethod]
    public void ARectangleSelectsEveryRowItReachesInto()
    {
        Assert.AreEqual((3, 5), BandSelect.Rows(3.5, 5.2, 100));
        Assert.AreEqual((3, 5), BandSelect.Rows(5.2, 3.5, 100), "dragged upwards");
        Assert.AreEqual((3, 3), BandSelect.Rows(3.5, 3.5, 100), "no height: the row it is in");
        Assert.AreEqual((3, 5), BandSelect.Rows(3.5, 6, 100), "ending in the gap after row 5 does not reach row 6");
        Assert.AreEqual((4, 5), BandSelect.Rows(4, 5.5, 100), "starting in the gap after row 3 does not reach row 3");
        Assert.IsNull(BandSelect.Rows(4, 4, 100), "drawn inside one gap (a group heading): nothing");
        Assert.AreEqual((98, 99), BandSelect.Rows(98.1, 100, 100), "down into the empty space under the last row");
        Assert.IsNull(BandSelect.Rows(100, 100, 100), "drawn entirely in that empty space");
        Assert.AreEqual((0, 99), BandSelect.Rows(0, 100, 100), "from above the first row to below the last");
        Assert.IsNull(BandSelect.Rows(0, 0, 0), "an empty folder");
    }

    [TestMethod]
    public void ARectangleSelectsTheTilesItTouches()
    {
        // Tiles 100 by 120 in cells, drawn 4 pixels inside them, five to a row, 12 tiles (the last row has two).
        static int[] Touched(double left, double top, double right, double bottom)
            => [.. BandSelect.Tiles(left, top, right, bottom, 100, 120, 4, 5, 12)];

        CollectionAssert.AreEqual(new[] { 0 }, Touched(10, 10, 20, 20));
        CollectionAssert.AreEqual(new[] { 0, 1, 5, 6 }, Touched(50, 50, 150, 150));
        CollectionAssert.AreEqual(new[] { 0, 1, 5, 6 }, Touched(150, 150, 50, 50), "dragged up and to the left");
        CollectionAssert.AreEqual(new int[0], Touched(97, 10, 103, 300), "drawn in the gap between two columns");
        CollectionAssert.AreEqual(new[] { 0 }, Touched(90, 10, 103, 20), "reaching the gap, not the next tile");
        CollectionAssert.AreEqual(new[] { 0, 1 }, Touched(90, 10, 105, 20), "reaching the next tile");
        CollectionAssert.AreEqual(new[] { 10, 11 }, Touched(0, 250, 499, 300), "the last row has only two tiles");
        CollectionAssert.AreEqual(new int[0], Touched(0, 400, 499, 900), "below the tiles");
        Assert.AreEqual(12, Touched(-50, -50, 2000, 2000).Length, "everything");
    }

    [TestMethod]
    public void CtrlFlipsShiftAddsAndNeitherReplaces()
    {
        Assert.IsTrue(BandSelect.Wanted(was: false, inside: true, BandMode.Replace));
        Assert.IsFalse(BandSelect.Wanted(was: true, inside: false, BandMode.Replace), "what was selected before goes");

        Assert.IsTrue(BandSelect.Wanted(was: true, inside: false, BandMode.Add), "what was selected stays");
        Assert.IsTrue(BandSelect.Wanted(was: false, inside: true, BandMode.Add));
        Assert.IsTrue(BandSelect.Wanted(was: true, inside: true, BandMode.Add));
        Assert.IsFalse(BandSelect.Wanted(was: false, inside: false, BandMode.Add));

        Assert.IsFalse(BandSelect.Wanted(was: true, inside: true, BandMode.Toggle), "a selected file under the rectangle is unselected");
        Assert.IsTrue(BandSelect.Wanted(was: false, inside: true, BandMode.Toggle));
        Assert.IsTrue(BandSelect.Wanted(was: true, inside: false, BandMode.Toggle), "and it comes back when the rectangle leaves it");
    }

    [TestMethod]
    public void TheListScrollsWhenThePointerIsHeldAtOrPastAnEdge()
    {
        const double height = 600;

        Assert.AreEqual(0, BandSelect.ScrollSpeed(300, 0, height));
        Assert.AreEqual(0, BandSelect.ScrollSpeed(height - BandSelect.Edge, 0, height), "just inside the bottom band");

        var atEdge = BandSelect.ScrollSpeed(height, 0, height);
        var outside = BandSelect.ScrollSpeed(height + 40, 0, height);
        Assert.IsTrue(atEdge > 0 && outside > atEdge, "down, and faster the further past the edge");
        Assert.AreEqual(BandSelect.Fastest, BandSelect.ScrollSpeed(height + 5000, 0, height), "but never faster than this");

        Assert.AreEqual(-atEdge, BandSelect.ScrollSpeed(0, 0, height), 0.001, "the top mirrors the bottom");
        Assert.AreEqual(-outside, BandSelect.ScrollSpeed(-40, 0, height), 0.001);

        Assert.AreEqual(0, BandSelect.ScrollSpeed(10, 0, 0), "nothing to scroll");
        Assert.AreEqual(0, BandSelect.ScrollSpeed(15, 0, 30), "a very short list keeps a still middle");
    }
}
