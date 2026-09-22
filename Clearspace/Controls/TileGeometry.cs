using System.Windows;

namespace Clearspace.Controls;

// Immutable camera-local geometry. Keeping coordinates near the reference
// viewport avoids losing float precision when zooming into very small files.
internal sealed class TileGeometry(DiskUsageTreemap.TileCommand[] commands)
{
    public DiskUsageTreemap.TileCommand[] Commands { get; } = commands;
}

internal readonly record struct TileBatch(TileGeometry Geometry, double Scale, double X, double Y, double Opacity)
{
    public Rect Transform(Rect rect) => new(rect.X * Scale + X, rect.Y * Scale + Y,
        rect.Width * Scale, rect.Height * Scale);

    // CHANGED (round 19): the geometry now also carries the viewport it was built for, so a resized
    // window can keep drawing it (stretched) instead of throwing it away and blanking the map. The
    // single scale stays correct because the camera is always clamped to the viewport's aspect ratio.
    public static TileBatch ForCamera(TileGeometry geometry, Rect reference, Size referenceViewport,
        Rect camera, Size viewport, double opacity = 1)
        => new(geometry, reference.Width / camera.Width * viewport.Width / referenceViewport.Width,
            (reference.X - camera.X) * viewport.Width / camera.Width,
            (reference.Y - camera.Y) * viewport.Height / camera.Height, opacity);
}
