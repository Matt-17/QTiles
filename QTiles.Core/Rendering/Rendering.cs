using System.Diagnostics;
using System.Text.Json;
using QTiles.Core.Config;
using QTiles.Core.Geo;
using QTiles.Core.Imaging;
using QTiles.Core.Transforms;
using QTiles.Core.Validation;

namespace QTiles.Core.Rendering;

public sealed record TileRenderJob(QTilesProject Project, string ProjectPath);

public sealed record TileRenderProgress(int Zoom, int CompletedTiles, int TotalTiles, string? CurrentPath, double Percent);

public sealed record RenderSummary(int TilesWritten, int TilesSkipped, int MinZoom, int MaxZoom, GeoBounds Bounds, TimeSpan Duration);

public interface ITileRenderer
{
    Task<RenderSummary> RenderAsync(
        QTilesProject project,
        IProgress<TileRenderProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class TileRenderer : ITileRenderer
{
    private readonly IImageInfoReader imageInfoReader;
    private readonly IRenderedTileWriter tileWriter;
    private readonly ProjectValidator validator;
    private readonly TransformSolver solver;
    private readonly ProjectRenderPlanner planner;

    public TileRenderer()
        : this(new NetVipsImageInfoReader(), new NetVipsTileRenderer(), new ProjectValidator(), new TransformSolver())
    {
    }

    public TileRenderer(
        IImageInfoReader imageInfoReader,
        IRenderedTileWriter tileWriter,
        ProjectValidator validator,
        TransformSolver solver)
    {
        this.imageInfoReader = imageInfoReader;
        this.tileWriter = tileWriter;
        this.validator = validator;
        this.solver = solver;
        planner = new ProjectRenderPlanner(imageInfoReader, solver);
    }

    public async Task<RenderSummary> RenderAsync(
        QTilesProject project,
        IProgress<TileRenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var messages = validator.Validate(project);
        if (ProjectValidator.HasErrors(messages))
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, messages.Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Message)));
        }

        var stopwatch = Stopwatch.StartNew();
        var outputDirectory = ProjectPaths.Resolve(project, project.Output.Directory);
        var plan = planner.CreatePlan(project);
        var tiles = ResolveTiles(plan).ToList();
        var total = tiles.Count;
        var completed = 0;
        var written = 0;
        var skipped = 0;
        var options = new TileRenderOptions(
            project.Render.Format,
            project.Render.Quality,
            project.Render.Overwrite,
            project.Render.SkipEmptyTiles,
            project.Render.Resampling,
            project.Render.Background);

        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(outputDirectory, tile.Z.ToString(), tile.X.ToString(), $"{tile.Y}.{NormalizeExtension(project.Render.Format)}");
            var request = new RenderedTileRequest(GetTileSources(plan.Sources, tile), tile, project.Render.TileSize, options);
            if (await tileWriter.WriteAsync(path, request, cancellationToken))
            {
                written++;
            }
            else
            {
                skipped++;
            }

            completed++;
            progress?.Report(new TileRenderProgress(tile.Z, completed, total, path, total == 0 ? 100 : completed * 100.0 / total));
        }

        stopwatch.Stop();
        var summary = new RenderSummary(written, skipped, plan.ZoomRange.MinZoom, plan.ZoomRange.MaxZoom, plan.Bounds, stopwatch.Elapsed);
        if (project.Output.TileJson)
        {
            await TileJsonWriter.WriteAsync(project, plan, summary, ProjectPaths.Resolve(project, project.Output.TileJsonPath), cancellationToken);
        }

        return summary;
    }

    private static IEnumerable<TileCoord> ResolveTiles(ProjectRenderPlan plan)
    {
        var tiles = new HashSet<TileCoord>();
        foreach (var source in plan.Sources)
        {
            for (var zoom = plan.ZoomRange.MinZoom; zoom <= plan.ZoomRange.MaxZoom; zoom++)
            {
                foreach (var tile in TileMath.BoundsToTileRange(source.SolveResult.Bounds, zoom).Tiles())
                {
                    tiles.Add(tile);
                }
            }
        }

        return tiles
            .OrderBy(tile => tile.Z)
            .ThenBy(tile => tile.X)
            .ThenBy(tile => tile.Y);
    }

    private static IReadOnlyList<RenderedTileSource> GetTileSources(
        IReadOnlyList<RenderSourceContext> sources,
        TileCoord tile)
    {
        var layers = new List<RenderedTileSource>();
        foreach (var source in sources)
        {
            var range = TileMath.BoundsToTileRange(source.SolveResult.Bounds, tile.Z);
            if (tile.X >= range.MinX
                && tile.X <= range.MaxX
                && tile.Y >= range.MinY
                && tile.Y <= range.MaxY)
            {
                layers.Add(new RenderedTileSource(source.SourceImagePath, source.RenderTransform, source.Source.Opacity));
            }
        }

        return layers;
    }

    private static string NormalizeExtension(string format) => format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : format.ToLowerInvariant();
}

public static class TileJsonWriter
{
    public static async Task WriteAsync(
        QTilesProject project,
        TransformSolveResult solveResult,
        RenderSummary renderSummary,
        string path,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bounds = renderSummary.Bounds;
        var center = bounds.Center;
        var tileTemplate = $"./{{z}}/{{x}}/{{y}}.{project.Render.Format.ToLowerInvariant()}";
        var payload = new Dictionary<string, object?>
        {
            ["tilejson"] = "3.0.0",
            ["name"] = project.Project.Name,
            ["scheme"] = "xyz",
            ["tiles"] = new[] { tileTemplate },
            ["minzoom"] = renderSummary.MinZoom,
            ["maxzoom"] = renderSummary.MaxZoom,
            ["bounds"] = new[] { bounds.West, bounds.South, bounds.East, bounds.North },
            ["center"] = new object[] { center.Lon, center.Lat, renderSummary.MinZoom },
            ["attribution"] = "Generated by QTiles",
            ["rms"] = solveResult.RmsPixelsAtMaxZoom
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public static async Task WriteAsync(
        QTilesProject project,
        ProjectRenderPlan renderPlan,
        RenderSummary renderSummary,
        string path,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bounds = renderSummary.Bounds;
        var center = bounds.Center;
        var tileTemplate = $"./{{z}}/{{x}}/{{y}}.{project.Render.Format.ToLowerInvariant()}";
        var payload = new Dictionary<string, object?>
        {
            ["tilejson"] = "3.0.0",
            ["name"] = project.Project.Name,
            ["scheme"] = "xyz",
            ["tiles"] = new[] { tileTemplate },
            ["minzoom"] = renderSummary.MinZoom,
            ["maxzoom"] = renderSummary.MaxZoom,
            ["bounds"] = new[] { bounds.West, bounds.South, bounds.East, bounds.North },
            ["center"] = new object[] { center.Lon, center.Lat, renderSummary.MinZoom },
            ["attribution"] = "Generated by QTiles",
            ["rms"] = renderPlan.RmsPixelsAtMaxZoom,
            ["sources"] = renderPlan.Sources.Count
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
}
