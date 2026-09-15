namespace TokkDb.Assistant.Diagram;

/// <summary>
/// The one transform between the layout's points and the device's pixels (TR-5a): produced from
/// the view's scale at draw time and used for both drawing and hit testing, so that a click
/// lands on the block it is over whatever the display's scale is, and a display change is a new
/// transform rather than a new layout.
/// </summary>
public readonly record struct DiagramTransform(double Scale, double OffsetX = 0, double OffsetY = 0)
{
    public static readonly DiagramTransform Identity = new(1);

    public (double X, double Y) ToDevice(double x, double y) => (x * Scale + OffsetX, y * Scale + OffsetY);

    public (double X, double Y) ToLogical(double deviceX, double deviceY) => ((deviceX - OffsetX) / Scale, (deviceY - OffsetY) / Scale);

    /// <summary>The scale that fits the layout's width into the view's, never more than one to one, and the offset that centres it.</summary>
    public static DiagramTransform FitWidth(DiagramLayout layout, double viewWidth, double maxScale = 1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Width <= 0 || viewWidth <= 0) return Identity;

        var scale = Math.Min(maxScale, viewWidth / layout.Width);
        var offset = Math.Max(0, (viewWidth - layout.Width * scale) / 2);
        return new DiagramTransform(scale, offset, 0);
    }
}

/// <summary>Which block a point is over: the innermost, since a nested block lies inside its parent's.</summary>
public static class HitTesting
{
    public static Block? BlockAt(DiagramLayout layout, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(layout);

        Block? hit = null;
        foreach (var block in layout.Blocks)
        {
            if (block.Contains(x, y) && (hit is null || block.Depth > hit.Depth)) hit = block;
        }

        return hit;
    }

    /// <summary>The block under a device point, through the transform the drawing used.</summary>
    public static Block? BlockAt(DiagramLayout layout, DiagramTransform transform, double deviceX, double deviceY)
    {
        var (x, y) = transform.ToLogical(deviceX, deviceY);
        return BlockAt(layout, x, y);
    }
}
