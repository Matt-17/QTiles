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
        var xMin = ClampTile((int)Math.Floor(minX * (1 << zoom)), limit);
        var yMin = ClampTile((int)Math.Floor(minY * (1 << zoom)), limit);
        var xMax = ClampTile((int)Math.Floor(maxX * (1 << zoom)), limit);
        var yMax = ClampTile((int)Math.Floor(maxY * (1 << zoom)), limit);
        return new TileRange(zoom, Math.Min(xMin, xMax), Math.Min(yMin, yMax), Math.Max(xMin, xMax), Math.Max(yMin, yMax));
    }

    private static int ClampTile(int value, int limit) => Math.Clamp(value, 0, limit);
}
