namespace QTiles.Core.Geo;

public sealed record GeoPoint(double Lon, double Lat);
public sealed record NormalizedMercatorPoint(double X, double Y);
public sealed record TileCoord(int Z, int X, int Y);
public sealed record TileRange(int Z, int MinX, int MinY, int MaxX, int MaxY)
{
    public int Count => (MaxX - MinX + 1) * (MaxY - MinY + 1);

    public IEnumerable<TileCoord> Tiles()
    {
        for (var x = MinX; x <= MaxX; x++)
        {
            for (var y = MinY; y <= MaxY; y++)
            {
                yield return new TileCoord(Z, x, y);
            }
        }
    }
}

public sealed record GeoBounds(double West, double South, double East, double North)
{
    public GeoPoint Center => new((West + East) / 2.0, (South + North) / 2.0);
}
