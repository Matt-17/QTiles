using QTiles.Core.Config;
using QTiles.Core.Transforms;

namespace QTiles.Core.Rendering;

public sealed record ZoomRangeRecommendation(int MinZoom, int MaxZoom);

public static class ZoomRangeCalculator
{
    public const int MaxSupportedZoom = 24;
    private const int MaxRecommendedLevels = 7;
    private const int MinimumUsefulLongSidePixels = 64;

    public static bool UsesAutoZoom(RenderConfig render) =>
        render.AutoZoom ?? (render.MinZoom == 0 && render.MaxZoom == 0);

    public static ZoomRangeRecommendation Resolve(
        RenderConfig render,
        TransformSolveResult solveResult,
        int imageWidth,
        int imageHeight)
    {
        if (!UsesAutoZoom(render))
        {
            return new ZoomRangeRecommendation(render.MinZoom, render.MaxZoom);
        }

        return Recommend(render.TileSize, solveResult, imageWidth, imageHeight);
    }

    public static ZoomRangeRecommendation Recommend(
        int tileSize,
        TransformSolveResult solveResult,
        int imageWidth,
        int imageHeight)
    {
        if (tileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), "Tile size must be positive.");
        }

        var transform = TransformSolver.ToAffineTransform(solveResult.Transform);
        var maxZoom = CalculateMaxZoom(transform, tileSize);
        var minZoom = CalculateMinZoom(transform, imageWidth, imageHeight, tileSize, maxZoom);
        return new ZoomRangeRecommendation(minZoom, maxZoom);
    }

    private static int CalculateMaxZoom(AffineTransform transform, int tileSize)
    {
        var xAxis = Math.Sqrt(transform.A * transform.A + transform.D * transform.D);
        var yAxis = Math.Sqrt(transform.B * transform.B + transform.E * transform.E);
        var normalizedSourcePixel = Math.Max(xAxis, yAxis);
        if (!double.IsFinite(normalizedSourcePixel) || normalizedSourcePixel <= 0)
        {
            return 0;
        }

        var rawZoom = Math.Log2(1.0 / (tileSize * normalizedSourcePixel));
        return ClampZoom((int)Math.Floor(rawZoom));
    }

    private static int CalculateMinZoom(
        AffineTransform transform,
        int imageWidth,
        int imageHeight,
        int tileSize,
        int maxZoom)
    {
        var longestSide = CalculateLongestNormalizedSide(transform, imageWidth, imageHeight);
        if (!double.IsFinite(longestSide) || longestSide <= 0)
        {
            return maxZoom;
        }

        var visibleZoom = (int)Math.Ceiling(Math.Log2(MinimumUsefulLongSidePixels / (tileSize * longestSide)));
        var levelBudgetZoom = maxZoom - (MaxRecommendedLevels - 1);
        return Math.Clamp(Math.Max(visibleZoom, levelBudgetZoom), 0, maxZoom);
    }

    private static double CalculateLongestNormalizedSide(AffineTransform transform, int imageWidth, int imageHeight)
    {
        var corners = new[]
        {
            transform.ImageToWorld(new ImagePoint { X = 0, Y = 0 }),
            transform.ImageToWorld(new ImagePoint { X = imageWidth, Y = 0 }),
            transform.ImageToWorld(new ImagePoint { X = imageWidth, Y = imageHeight }),
            transform.ImageToWorld(new ImagePoint { X = 0, Y = imageHeight })
        };

        var minX = corners.Min(p => p.X);
        var maxX = corners.Max(p => p.X);
        var minY = corners.Min(p => p.Y);
        var maxY = corners.Max(p => p.Y);
        return Math.Max(maxX - minX, maxY - minY);
    }

    private static int ClampZoom(int zoom) => Math.Clamp(zoom, 0, MaxSupportedZoom);
}
