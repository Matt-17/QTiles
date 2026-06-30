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
        => new TileRenderer().RenderAsync(project, progress, cancellationToken);
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
    public string Describe(QTilesProject project) => project.Source.Image;
}

public sealed class PointPairingService
{
    public int NextIntegerId(IEnumerable<ControlPointConfig> points) =>
        points.Select(p => int.TryParse(p.Id.Value, out var id) ? id : 0).DefaultIfEmpty().Max() + 1;
}

public sealed class DialogService
{
}
