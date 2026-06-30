namespace QTiles.Core.Geo;

public static class WebMercator
{
    public const double MaxLatitude = 85.05112878;

    public static NormalizedMercatorPoint LonLatToNormalized(double lon, double lat)
    {
        var clampedLat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        var latRad = DegreesToRadians(clampedLat);
        var sinLat = Math.Sin(latRad);
        var x = (lon + 180.0) / 360.0;
        var y = 0.5 - Math.Log((1.0 + sinLat) / (1.0 - sinLat)) / (4.0 * Math.PI);
        return new NormalizedMercatorPoint(x, y);
    }

    public static GeoPoint NormalizedToLonLat(double x, double y)
    {
        var lon = x * 360.0 - 180.0;
        var mercator = Math.PI * (1.0 - 2.0 * y);
        var lat = RadiansToDegrees(Math.Atan(Math.Sinh(mercator)));
        return new GeoPoint(lon, Math.Clamp(lat, -MaxLatitude, MaxLatitude));
    }

    public static (double GlobalX, double GlobalY) NormalizedToGlobalPixel(
        double normalizedX,
        double normalizedY,
        int zoom,
        int tileSize)
    {
        var scale = tileSize * Math.Pow(2.0, zoom);
        return (normalizedX * scale, normalizedY * scale);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
}
