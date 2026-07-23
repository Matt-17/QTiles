using System.Globalization;
using QTiles.Core.Geo;
using QTiles.Core.Imaging;
using QTiles.Core.Transforms;

namespace QTiles.Core.Rendering;

public sealed record PreviewTileViewport(
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double Resolution);

public sealed record PreviewTileWorldBounds(double Left, double Top, double Right, double Bottom);

public sealed record PreviewTileKey(
    string SourceImagePath,
    long SourceLastWriteTicks,
    string TransformFingerprint,
    int TileSize,
    int Z,
    int X,
    int Y);

public sealed record PreviewTileWorkItem(PreviewTileKey Key, RenderedTileRequest RenderRequest);

public sealed class PreviewTileService
{
    private const double WebMercatorHalfWorld = 20037508.342789244;
    private const double WebMercatorWorldWidth = WebMercatorHalfWorld * 2.0;
    private readonly ITileImageRenderer tileImageRenderer;
    private readonly SemaphoreSlim renderGate = new(1, 1);

    public PreviewTileService()
        : this(new NetVipsTileRenderer(loadSourceIntoMemory: true))
    {
    }

    public PreviewTileService(ITileImageRenderer tileImageRenderer)
    {
        this.tileImageRenderer = tileImageRenderer;
    }

    // Upper bound on preview tiles per redraw. Without it an unclamped zoom level over a
    // large area can enumerate millions of tiles and freeze the UI thread.
    public const int MaxPreviewTiles = 1024;

    public IReadOnlyList<TileCoord> GetVisibleTiles(
        PreviewTileViewport viewport,
        int minZoom,
        int maxZoom,
        int tileSize,
        bool limitToZoomRange = true,
        GeoBounds? contentBounds = null)
    {
        if (!IsFinitePositive(viewport.Width)
            || !IsFinitePositive(viewport.Height)
            || !IsFinitePositive(viewport.Resolution))
        {
            return [];
        }

        tileSize = Math.Max(1, tileSize);
        var zoom = ViewportResolutionToZoom(viewport.Resolution, minZoom, maxZoom, tileSize, limitToZoomRange);
        if (zoom is null)
        {
            return [];
        }

        var zoomLevel = zoom.Value;
        var resolution = TileResolution(zoomLevel, tileSize);
        var minWorldX = viewport.CenterX - viewport.Width * viewport.Resolution / 2.0;
        var maxWorldX = viewport.CenterX + viewport.Width * viewport.Resolution / 2.0;
        var minWorldY = viewport.CenterY - viewport.Height * viewport.Resolution / 2.0;
        var maxWorldY = viewport.CenterY + viewport.Height * viewport.Resolution / 2.0;
        var maxTile = (int)Math.Min(int.MaxValue, Math.Pow(2.0, zoomLevel) - 1.0);
        var minTileX = ClampTile((int)Math.Floor((minWorldX + WebMercatorHalfWorld) / (tileSize * resolution)), maxTile);
        var maxTileX = ClampTile((int)Math.Floor((maxWorldX + WebMercatorHalfWorld) / (tileSize * resolution)), maxTile);
        var minTileY = ClampTile((int)Math.Floor((WebMercatorHalfWorld - maxWorldY) / (tileSize * resolution)), maxTile);
        var maxTileY = ClampTile((int)Math.Floor((WebMercatorHalfWorld - minWorldY) / (tileSize * resolution)), maxTile);

        if (contentBounds is { } bounds)
        {
            var contentRange = TileMath.BoundsToTileRange(bounds, zoomLevel);
            minTileX = Math.Max(minTileX, contentRange.MinX);
            maxTileX = Math.Min(maxTileX, contentRange.MaxX);
            minTileY = Math.Max(minTileY, contentRange.MinY);
            maxTileY = Math.Min(maxTileY, contentRange.MaxY);
            if (minTileX > maxTileX || minTileY > maxTileY)
            {
                return [];
            }
        }

        var totalTiles = (long)(maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);
        if (totalTiles > MaxPreviewTiles)
        {
            return EnumerateCenteredTiles(zoomLevel, minTileX, maxTileX, minTileY, maxTileY, viewport, tileSize, resolution);
        }

        var tiles = new List<TileCoord>((int)totalTiles);
        for (var x = minTileX; x <= maxTileX; x++)
        {
            for (var y = minTileY; y <= maxTileY; y++)
            {
                tiles.Add(new TileCoord(zoomLevel, x, y));
            }
        }

        return tiles;
    }

    private static IReadOnlyList<TileCoord> EnumerateCenteredTiles(
        int zoomLevel,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        PreviewTileViewport viewport,
        int tileSize,
        double resolution)
    {
        // Over the cap: keep the tiles closest to the viewport center so the visible
        // middle of the map fills in first and the overall count stays bounded.
        var centerTileX = Math.Clamp((viewport.CenterX + WebMercatorHalfWorld) / (tileSize * resolution), minTileX, maxTileX);
        var centerTileY = Math.Clamp((WebMercatorHalfWorld - viewport.CenterY) / (tileSize * resolution), minTileY, maxTileY);
        var candidates = new List<(double DistanceSquared, TileCoord Tile)>();
        var radius = (int)Math.Ceiling(Math.Sqrt(MaxPreviewTiles)) + 1;
        var xStart = Math.Max(minTileX, (int)centerTileX - radius);
        var xEnd = Math.Min(maxTileX, (int)centerTileX + radius);
        var yStart = Math.Max(minTileY, (int)centerTileY - radius);
        var yEnd = Math.Min(maxTileY, (int)centerTileY + radius);
        for (var x = xStart; x <= xEnd; x++)
        {
            for (var y = yStart; y <= yEnd; y++)
            {
                var dx = x + 0.5 - centerTileX;
                var dy = y + 0.5 - centerTileY;
                candidates.Add((dx * dx + dy * dy, new TileCoord(zoomLevel, x, y)));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.DistanceSquared)
            .Take(MaxPreviewTiles)
            .Select(candidate => candidate.Tile)
            .ToList();
    }

    public PreviewTileWorkItem CreateWorkItem(
        string sourceImagePath,
        long sourceLastWriteTicks,
        TransformSolveResult solveResult,
        TileCoord tile,
        int tileSize)
    {
        tileSize = Math.Max(1, tileSize);
        var transform = TransformSolver.ToAffineTransform(solveResult.Transform);
        var key = new PreviewTileKey(
            sourceImagePath,
            sourceLastWriteTicks,
            CreateTransformFingerprint(transform),
            tileSize,
            tile.Z,
            tile.X,
            tile.Y);
        var options = new TileRenderOptions(
            "png",
            80,
            Overwrite: true,
            SkipEmptyTiles: true,
            RenderResampling.Bilinear,
            "transparent");
        return new PreviewTileWorkItem(
            key,
            new RenderedTileRequest(sourceImagePath, transform, tile, tileSize, options));
    }

    public PreviewTileWorkItem CreateWorkItem(ProjectRenderPlan renderPlan, TileCoord tile, int tileSize)
    {
        tileSize = Math.Max(1, tileSize);
        var sources = renderPlan.Sources
            .Where(source => SourceContributesToTile(source, tile))
            .ToList();
        var requestSources = sources
            .Select(source => new RenderedTileSource(source.SourceImagePath, source.RenderTransform, source.Source.Opacity))
            .ToList();
        var latestSourceTicks = sources
            .Select(source => File.GetLastWriteTimeUtc(source.SourceImagePath).Ticks)
            .DefaultIfEmpty(0)
            .Max();
        var key = new PreviewTileKey(
            "merged",
            latestSourceTicks,
            CreateSourcesFingerprint(sources),
            tileSize,
            tile.Z,
            tile.X,
            tile.Y);
        return new PreviewTileWorkItem(
            key,
            new RenderedTileRequest(requestSources, tile, tileSize, CreateOptions()));
    }

    public async Task<RenderedTileImage?> RenderAsync(PreviewTileWorkItem workItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await renderGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => tileImageRenderer.RenderAsync(workItem.RenderRequest, cancellationToken),
                cancellationToken);
        }
        finally
        {
            renderGate.Release();
        }
    }

    public static PreviewTileWorldBounds GetWorldBounds(TileCoord tile, int tileSize)
    {
        tileSize = Math.Max(1, tileSize);
        var resolution = TileResolution(tile.Z, tileSize);
        var leftWorld = -WebMercatorHalfWorld + tile.X * tileSize * resolution;
        var rightWorld = leftWorld + tileSize * resolution;
        var topWorld = WebMercatorHalfWorld - tile.Y * tileSize * resolution;
        var bottomWorld = topWorld - tileSize * resolution;
        return new PreviewTileWorldBounds(leftWorld, topWorld, rightWorld, bottomWorld);
    }

    private static int? ViewportResolutionToZoom(
        double resolution,
        int minZoom,
        int maxZoom,
        int tileSize,
        bool limitToZoomRange)
    {
        if (minZoom > maxZoom)
        {
            return null;
        }

        var rawZoom = Math.Log(WebMercatorWorldWidth / (tileSize * resolution), 2.0);
        if (!double.IsFinite(rawZoom))
        {
            return null;
        }

        var roundedZoom = (int)Math.Round(rawZoom);
        if (limitToZoomRange && (roundedZoom < minZoom || roundedZoom > maxZoom))
        {
            return null;
        }

        return Math.Clamp(roundedZoom, minZoom, maxZoom);
    }

    private static double TileResolution(int zoom, int tileSize) =>
        WebMercatorWorldWidth / (tileSize * Math.Pow(2.0, zoom));

    private static int ClampTile(int tile, int maxTile) => Math.Clamp(tile, 0, Math.Max(0, maxTile));

    private static TileRenderOptions CreateOptions() => new(
        "png",
        80,
        Overwrite: true,
        SkipEmptyTiles: true,
        RenderResampling.Bilinear,
        "transparent");

    private static bool SourceContributesToTile(RenderSourceContext source, TileCoord tile)
    {
        var range = TileMath.BoundsToTileRange(source.SolveResult.Bounds, tile.Z);
        return tile.X >= range.MinX
            && tile.X <= range.MaxX
            && tile.Y >= range.MinY
            && tile.Y <= range.MaxY;
    }

    private static string CreateSourcesFingerprint(IReadOnlyList<RenderSourceContext> sources) =>
        string.Join(
            ";",
            sources.Select(source =>
            {
                var ticks = File.GetLastWriteTimeUtc(source.SourceImagePath).Ticks;
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source.Source.Id}|{source.SourceImagePath}|{ticks}|{source.Source.Opacity:R}|{CreateTransformFingerprint(source.RenderTransform)}");
            }));

    private static string CreateTransformFingerprint(AffineTransform transform)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{transform.A:R}|{transform.B:R}|{transform.C:R}|{transform.D:R}|{transform.E:R}|{transform.F:R}");

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;
}
