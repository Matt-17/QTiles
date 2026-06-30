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

    public IReadOnlyList<TileCoord> GetVisibleTiles(
        PreviewTileViewport viewport,
        int minZoom,
        int maxZoom,
        int tileSize)
    {
        if (!IsFinitePositive(viewport.Width)
            || !IsFinitePositive(viewport.Height)
            || !IsFinitePositive(viewport.Resolution))
        {
            return [];
        }

        tileSize = Math.Max(1, tileSize);
        var zoom = ViewportResolutionToZoom(viewport.Resolution, minZoom, maxZoom, tileSize);
        var resolution = TileResolution(zoom, tileSize);
        var minWorldX = viewport.CenterX - viewport.Width * viewport.Resolution / 2.0;
        var maxWorldX = viewport.CenterX + viewport.Width * viewport.Resolution / 2.0;
        var minWorldY = viewport.CenterY - viewport.Height * viewport.Resolution / 2.0;
        var maxWorldY = viewport.CenterY + viewport.Height * viewport.Resolution / 2.0;
        var maxTile = (int)Math.Min(int.MaxValue, Math.Pow(2.0, zoom) - 1.0);
        var minTileX = ClampTile((int)Math.Floor((minWorldX + WebMercatorHalfWorld) / (tileSize * resolution)), maxTile);
        var maxTileX = ClampTile((int)Math.Floor((maxWorldX + WebMercatorHalfWorld) / (tileSize * resolution)), maxTile);
        var minTileY = ClampTile((int)Math.Floor((WebMercatorHalfWorld - maxWorldY) / (tileSize * resolution)), maxTile);
        var maxTileY = ClampTile((int)Math.Floor((WebMercatorHalfWorld - minWorldY) / (tileSize * resolution)), maxTile);

        var tiles = new List<TileCoord>((maxTileX - minTileX + 1) * (maxTileY - minTileY + 1));
        for (var x = minTileX; x <= maxTileX; x++)
        {
            for (var y = minTileY; y <= maxTileY; y++)
            {
                tiles.Add(new TileCoord(zoom, x, y));
            }
        }

        return tiles;
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

    private static int ViewportResolutionToZoom(double resolution, int minZoom, int maxZoom, int tileSize)
    {
        var rawZoom = Math.Log(WebMercatorWorldWidth / (tileSize * resolution), 2.0);
        return Math.Clamp((int)Math.Round(rawZoom), minZoom, maxZoom);
    }

    private static double TileResolution(int zoom, int tileSize) =>
        WebMercatorWorldWidth / (tileSize * Math.Pow(2.0, zoom));

    private static int ClampTile(int tile, int maxTile) => Math.Clamp(tile, 0, Math.Max(0, maxTile));

    private static string CreateTransformFingerprint(AffineTransform transform)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{transform.A:R}|{transform.B:R}|{transform.C:R}|{transform.D:R}|{transform.E:R}|{transform.F:R}");

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;
}
