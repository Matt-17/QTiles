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
        var sourceImagePath = ProjectPaths.Resolve(project, project.Source.Image);
        var outputDirectory = ProjectPaths.Resolve(project, project.Output.Directory);
        var imageInfo = imageInfoReader.Read(sourceImagePath);
        var initialSolveResult = solver.Solve(project, imageInfo.Width, imageInfo.Height);
        var zoomRange = ZoomRangeCalculator.Resolve(project.Render, initialSolveResult, imageInfo.Width, imageInfo.Height);
        var solveResult = zoomRange.MaxZoom == project.Render.MaxZoom
            ? initialSolveResult
            : solver.Solve(project, imageInfo.Width, imageInfo.Height, zoomRange.MaxZoom);
        var renderTransform = TransformSolver.ToAffineTransform(solveResult.Transform);
        var ranges = Enumerable.Range(zoomRange.MinZoom, zoomRange.MaxZoom - zoomRange.MinZoom + 1)
            .Select(z => TileMath.BoundsToTileRange(solveResult.Bounds, z))
            .ToList();
        var total = ranges.Sum(r => r.Count);
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

        foreach (var range in ranges)
        {
            foreach (var tile in range.Tiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(outputDirectory, tile.Z.ToString(), tile.X.ToString(), $"{tile.Y}.{NormalizeExtension(project.Render.Format)}");
                var request = new RenderedTileRequest(sourceImagePath, renderTransform, tile, project.Render.TileSize, options);
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
        }

        stopwatch.Stop();
        var summary = new RenderSummary(written, skipped, zoomRange.MinZoom, zoomRange.MaxZoom, solveResult.Bounds, stopwatch.Elapsed);
        if (project.Output.TileJson)
        {
            await TileJsonWriter.WriteAsync(project, solveResult, summary, ProjectPaths.Resolve(project, project.Output.TileJsonPath), cancellationToken);
        }

        return summary;
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
}
