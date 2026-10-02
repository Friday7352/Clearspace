using System.Windows;

namespace Clearspace.Controls;

// Immutable camera-local geometry. Keeping coordinates near the reference
// viewport avoids losing float precision when zooming into very small files.
// CHANGED (round 41): a geometry can own a pooled buffer that is longer than its command count. Every
// scene rebuild used to copy its commands into a new exact-length array - 15 MB or more per rebuild in
// "render everything", several rebuilds a second while zooming - and that churn, not the data, is what
// the collector's committed memory grew with. The control returns a buffer to its pool once no scene
// on screen uses the geometry any more (Release), and the next walk writes into it.
internal sealed class TileGeometry
{
    private DiskUsageTreemap.TileCommand[] _buffer;

    public TileGeometry(DiskUsageTreemap.TileCommand[] commands) : this(commands, commands.Length) { }

    public TileGeometry(DiskUsageTreemap.TileCommand[] buffer, int count)
    {
        _buffer = buffer;
        Count = count;
    }

    public int Count { get; private set; }

    /// <summary>The commands. What drawing and uploading read.</summary>
    public ReadOnlySpan<DiskUsageTreemap.TileCommand> Span => _buffer.AsSpan(0, Count);

    /// <summary>An exact-length array of the commands - a copy when the buffer is pooled. For tests and diagnostics.</summary>
    public DiskUsageTreemap.TileCommand[] Commands => _buffer.Length == Count ? _buffer : Span.ToArray();

    /// <summary>Hands the buffer back for reuse and leaves this geometry empty. Called once nothing draws it.</summary>
    internal DiskUsageTreemap.TileCommand[] Release()
    {
        var buffer = _buffer;
        _buffer = [];
        Count = 0;
        return buffer;
    }
}

// CHANGED (round 43): ColorBlend mixes each command's two colours, 0 = Color, 1 = ColorB.
internal readonly record struct TileBatch(TileGeometry Geometry, double Scale, double X, double Y, double Opacity, double ColorBlend = 0)
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
