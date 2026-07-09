using QTiles.Core.Config;
using QTiles.Core.Rendering;

namespace QTiles.Services;

public sealed class EditorProjectService
{
    private readonly QTilesYamlSerializer serializer = new();

    public Task<QTilesProject> OpenAsync(string path) => serializer.ReadAsync(path);

    public Task SaveAsync(QTilesProject project, string path) => serializer.WriteAsync(project, path);
}

public sealed class RenderJobService
{
    public Task<RenderSummary> RenderAsync(
        QTilesProject project,
        IProgress<TileRenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var renderProject = Snapshot(project);
        return Task.Run(
            () => new TileRenderer().RenderAsync(renderProject, progress, cancellationToken),
            cancellationToken);
    }

    public Task<string> WriteTileJsonAsync(QTilesProject project, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(project);
        return Task.Run(async () =>
        {
            var plan = new ProjectRenderPlanner().CreatePlan(snapshot);
            var summary = new RenderSummary(0, 0, plan.ZoomRange.MinZoom, plan.ZoomRange.MaxZoom, plan.Bounds, TimeSpan.Zero);
            var path = TileJsonWriter.ResolvePath(snapshot);
            await TileJsonWriter.WriteAsync(snapshot, plan, summary, path, cancellationToken);
            return path;
        }, cancellationToken);
    }

    private static QTilesProject Snapshot(QTilesProject project)
    {
        var serializer = new QTilesYamlSerializer();
        var snapshot = serializer.Deserialize(serializer.Serialize(project));
        snapshot.BaseDirectory = project.BaseDirectory;
        return snapshot;
    }
}

public sealed class MapsuiWorldMapService
{
    public string BasemapDescription(QTilesProject project) =>
        project.Editor.Basemap.Type.Equals("osm", StringComparison.OrdinalIgnoreCase)
            ? "OpenStreetMap interactive basemap"
            : project.Editor.Basemap.Url;
}

public sealed class ImageMapService
{
    public string Describe(QTilesProject project) =>
        string.Join(", ", ProjectSources.GetEffectiveSources(project).Select(ProjectSources.DisplayName));
}

public sealed class PointPairingService
{
    public int NextIntegerId(IEnumerable<ControlPointConfig> points) =>
        points.Select(p => int.TryParse(p.Id.Value, out var id) ? id : 0).DefaultIfEmpty().Max() + 1;
}

public sealed class DialogService
{
}
