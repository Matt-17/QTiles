using QTiles.Core.Config;
using QTiles.Core.Geo;
using QTiles.Core.Imaging;
using QTiles.Core.Transforms;

namespace QTiles.Core.Rendering;

public sealed record RenderSourceContext(
    ProjectSourceConfig Source,
    string SourceImagePath,
    ImageInfo ImageInfo,
    TransformSolveResult SolveResult,
    AffineTransform RenderTransform);

public sealed record ProjectRenderPlan(
    IReadOnlyList<RenderSourceContext> Sources,
    ZoomRangeRecommendation ZoomRange,
    GeoBounds Bounds,
    double RmsPixelsAtMaxZoom,
    double MaxPixelsAtMaxZoom);

public sealed class ProjectRenderPlanner
{
    private readonly IImageInfoReader imageInfoReader;
    private readonly TransformSolver solver;

    public ProjectRenderPlanner()
        : this(new NetVipsImageInfoReader(), new TransformSolver())
    {
    }

    public ProjectRenderPlanner(IImageInfoReader imageInfoReader, TransformSolver solver)
    {
        this.imageInfoReader = imageInfoReader;
        this.solver = solver;
    }

    public ProjectRenderPlan CreatePlan(QTilesProject project)
    {
        var enabledSources = ProjectSources.GetEnabledSources(project);
        if (enabledSources.Count == 0)
        {
            throw new InvalidOperationException("Project has no enabled source images.");
        }

        var preliminarySources = enabledSources
            .Select(source =>
            {
                var path = ProjectPaths.Resolve(project, source.Image);
                var imageInfo = imageInfoReader.Read(path);
                var initialSolveResult = solver.Solve(source.Georeference, project.Render, imageInfo.Width, imageInfo.Height);
                var zoomRange = ZoomRangeCalculator.Resolve(project.Render, initialSolveResult, imageInfo.Width, imageInfo.Height);
                return new PreliminarySource(source, path, imageInfo, zoomRange);
            })
            .ToList();

        var projectZoomRange = ResolveProjectZoomRange(project.Render, preliminarySources.Select(source => source.ZoomRange));
        var renderSources = preliminarySources
            .Select(source =>
            {
                var solveResult = solver.Solve(
                    source.Source.Georeference,
                    project.Render,
                    source.ImageInfo.Width,
                    source.ImageInfo.Height,
                    projectZoomRange.MaxZoom);
                return new RenderSourceContext(
                    source.Source,
                    source.SourceImagePath,
                    source.ImageInfo,
                    solveResult,
                    TransformSolver.ToAffineTransform(solveResult.Transform));
            })
            .ToList();

        return new ProjectRenderPlan(
            renderSources,
            projectZoomRange,
            UnionBounds(renderSources.Select(source => source.SolveResult.Bounds)),
            renderSources.Max(source => source.SolveResult.RmsPixelsAtMaxZoom),
            renderSources.Max(source => source.SolveResult.MaxPixelsAtMaxZoom));
    }

    private static ZoomRangeRecommendation ResolveProjectZoomRange(
        RenderConfig render,
        IEnumerable<ZoomRangeRecommendation> sourceRanges)
    {
        if (!ZoomRangeCalculator.UsesAutoZoom(render))
        {
            return new ZoomRangeRecommendation(render.MinZoom, render.MaxZoom);
        }

        var ranges = sourceRanges.ToList();
        return new ZoomRangeRecommendation(
            ranges.Min(range => range.MinZoom),
            ranges.Max(range => range.MaxZoom));
    }

    private static GeoBounds UnionBounds(IEnumerable<GeoBounds> bounds)
    {
        var items = bounds.ToList();
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Cannot calculate bounds for a project with no source images.");
        }

        return new GeoBounds(
            items.Min(bound => bound.West),
            items.Min(bound => bound.South),
            items.Max(bound => bound.East),
            items.Max(bound => bound.North));
    }

    private sealed record PreliminarySource(
        ProjectSourceConfig Source,
        string SourceImagePath,
        ImageInfo ImageInfo,
        ZoomRangeRecommendation ZoomRange);
}
