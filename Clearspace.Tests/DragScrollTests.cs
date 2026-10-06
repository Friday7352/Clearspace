// NEW (scroll while dragging): how fast a list scrolls while something is dragged near its top or bottom
// edge (Services/EdgeScroll.cs). Only the arithmetic; the dragging itself needs a mouse and is checked by hand.
using Clearspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clearspace.Tests;

[TestClass]
public sealed class DragScrollTests
{
    private const double Height = 600;

    [TestMethod]
    public void TheMiddleOfTheListDoesNotScroll()
    {
        Assert.AreEqual(0, EdgeScroll.Speed(300, top: 0, Height));
        Assert.AreEqual(0, EdgeScroll.Speed(EdgeScroll.Band, top: 0, Height), "the band's inner side is still the middle");
        Assert.AreEqual(0, EdgeScroll.Speed(Height - EdgeScroll.Band, top: 0, Height));
        Assert.AreEqual(0, EdgeScroll.Speed(-5, top: 0, Height), "above the list is not the list");
        Assert.AreEqual(0, EdgeScroll.Speed(Height + 5, top: 0, Height));
    }

    [TestMethod]
    public void NearAnEdgeItScrollsThatWayFasterTheCloserItIs()
    {
        var justInside = EdgeScroll.Speed(Height - EdgeScroll.Band + 1, top: 0, Height);
        var halfway = EdgeScroll.Speed(Height - EdgeScroll.Band / 2, top: 0, Height);
        var atEdge = EdgeScroll.Speed(Height, top: 0, Height);

        Assert.IsTrue(justInside > 0 && justInside < halfway && halfway < atEdge, "down, and faster towards the edge");
        Assert.IsTrue(justInside >= EdgeScroll.Slowest && justInside < EdgeScroll.Slowest + 10);
        Assert.AreEqual(EdgeScroll.Fastest, atEdge, 0.001);

        Assert.AreEqual(-halfway, EdgeScroll.Speed(EdgeScroll.Band / 2, top: 0, Height), 0.001, "the top band mirrors the bottom one");
        Assert.AreEqual(-EdgeScroll.Fastest, EdgeScroll.Speed(0, top: 0, Height), 0.001);
    }

    [TestMethod]
    public void TheTopBandStartsBelowTheColumnHeaders()
    {
        const double headers = 30;

        Assert.AreEqual(0, EdgeScroll.Speed(headers + EdgeScroll.Band + 1, headers, Height));
        Assert.IsTrue(EdgeScroll.Speed(headers + EdgeScroll.Band - 10, headers, Height) < 0);
        Assert.AreEqual(-EdgeScroll.Fastest, EdgeScroll.Speed(headers / 2, headers, Height), 0.001, "over the headers counts as the very edge");
        Assert.AreEqual(EdgeScroll.Speed(Height - 10, 0, Height), EdgeScroll.Speed(Height - 10, headers, Height), 0.001, "the bottom band is unchanged");
    }

    [TestMethod]
    public void AVeryShortListKeepsAStillMiddle()
    {
        // 60 pixels tall: two full bands would overlap, so each takes a third.
        Assert.AreEqual(0, EdgeScroll.Speed(30, top: 0, height: 60));
        Assert.IsTrue(EdgeScroll.Speed(5, top: 0, height: 60) < 0);
        Assert.IsTrue(EdgeScroll.Speed(55, top: 0, height: 60) > 0);
        Assert.AreEqual(0, EdgeScroll.Speed(10, top: 40, height: 40), "nothing but headers: nothing to scroll");
    }

    [TestMethod]
    public void AStepFollowsTheTimeThatPassedButNeverJumps()
    {
        Assert.AreEqual(10, EdgeScroll.Step(1000, TimeSpan.FromMilliseconds(10)), 0.001);
        Assert.AreEqual(-10, EdgeScroll.Step(-1000, TimeSpan.FromMilliseconds(10)), 0.001);
        Assert.AreEqual(50, EdgeScroll.Step(1000, TimeSpan.FromSeconds(3)), 0.001, "a long gap counts as one frame");
        Assert.AreEqual(0, EdgeScroll.Step(1000, TimeSpan.Zero));
    }
}
