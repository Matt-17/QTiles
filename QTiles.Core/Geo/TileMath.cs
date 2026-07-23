namespace QTiles.Core.Geo;

public static class TileMath
{
    public static TileRange BoundsToTileRange(GeoBounds bounds, int zoom)
    {
        if (bounds.West > bounds.East)
        {
            throw new ArgumentException("Bounds crossing the antimeridian are not supported in v1.", nameof(bounds));
        }

        var nw = WebMercator.LonLatToNormalized(bounds.West, bounds.North);
        var se = WebMercator.LonLatToNormalized(bounds.East, bounds.South);
        return NormalizedBoundsToTileRange(
            Math.Min(nw.X, se.X),
            Math.Min(nw.Y, se.Y),
            Math.Max(nw.X, se.X),
            Math.Max(nw.Y, se.Y),
            zoom);
    }

    public static TileRange NormalizedBoundsToTileRange(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int zoom)
    {
        var limit = (1 << zoom) - 1;
        var xMin = ClampTile((int)Math.Floor(Math.Min(minX, maxX) * (1 << zoom)), limit);
        var yMin = ClampTile((int)Math.Floor(Math.Min(minY, maxY) * (1 << zoom)), limit);
        // Treat the max edge exclusively: bounds ending exactly on a tile boundary must
        // not pull in an extra zero-coverage row/column. Zero-area bounds still map to
        // the single tile containing the edge (xMax/yMax never drop below xMin/yMin).
        var xMax = Math.Max(xMin, ClampTile(ExclusiveMaxTile(Math.Max(minX, maxX), zoom), limit));
        var yMax = Math.Max(yMin, ClampTile(ExclusiveMaxTile(Math.Max(minY, maxY), zoom), limit));
        return new TileRange(zoom, xMin, yMin, xMax, yMax);
    }

    private static int ExclusiveMaxTile(double max, int zoom)
    {
        var scaled = max * (1 << zoom);
        var floor = (int)Math.Floor(scaled);
        return scaled == floor ? floor - 1 : floor;
    }

    private static int ClampTile(int value, int limit) => Math.Clamp(value, 0, limit);
}
