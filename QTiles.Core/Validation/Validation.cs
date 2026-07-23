using QTiles.Core.Config;
using QTiles.Core.Rendering;

namespace QTiles.Core.Validation;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ValidationMessage(ValidationSeverity Severity, string Code, string Message);

public sealed class ProjectValidator
{
    public IReadOnlyList<ValidationMessage> Validate(QTilesProject project, string? projectPath = null)
    {
        var messages = new List<ValidationMessage>();
        if (project.Version != 1)
        {
            messages.Add(Error("unsupported-version", $"YAML version {project.Version} is not supported."));
        }

        var sources = ProjectSources.GetEffectiveSources(project);
        if (ProjectSources.HasExplicitSources(project))
        {
            var duplicateIds = sources
                .Where(source => !string.IsNullOrWhiteSpace(source.Id))
                .GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            foreach (var id in duplicateIds)
            {
                messages.Add(Error("source-id-duplicate", $"Source id is used more than once: {id}"));
            }
        }

        var enabledSources = sources.Where(source => source.Enabled).ToList();
        if (enabledSources.Count == 0)
        {
            messages.Add(Error("source-none-enabled", "At least one source image must be enabled."));
        }

        foreach (var source in sources)
        {
            if (!double.IsFinite(source.Opacity) || source.Opacity is < 0.0 or > 1.0)
            {
                messages.Add(Error("source-opacity", $"Source {ProjectSources.DisplayName(source)} opacity must be between 0 and 1."));
            }
        }

        foreach (var source in enabledSources)
        {
            ValidateSource(project, projectPath, source, messages);
        }

        if (project.Render.TileSize <= 0)
        {
            messages.Add(Error("tile-size", "render.tileSize must be positive."));
        }

        if (!ZoomRangeCalculator.UsesAutoZoom(project.Render))
        {
            if (project.Render.MinZoom < 0 || project.Render.MinZoom > ZoomRangeCalculator.MaxSupportedZoom
                || project.Render.MaxZoom < 0 || project.Render.MaxZoom > ZoomRangeCalculator.MaxSupportedZoom)
            {
                messages.Add(Error("zoom-bounds", $"render.minZoom and render.maxZoom must be between 0 and {ZoomRangeCalculator.MaxSupportedZoom}."));
            }
            else if (project.Render.MinZoom > project.Render.MaxZoom)
            {
                messages.Add(Error("zoom-range", "render.minZoom must be less than or equal to render.maxZoom."));
            }
        }

        if (!new[] { "png", "jpg", "jpeg", "webp" }.Contains(project.Render.Format.Trim().ToLowerInvariant()))
        {
            messages.Add(Error("format", "render.format must be png, jpg, or webp."));
        }

        if (!RenderResampling.IsSupported(project.Render.Resampling))
        {
            messages.Add(Error("resampling", $"render.resampling must be one of: {string.Join(", ", RenderResampling.Values)}."));
        }

        if (project.Output.TileJson)
        {
            messages.Add(Info("tilejson", "TileJSON will be written."));
        }

        if (project.Editor.Basemap.Type.Equals("osm", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(Warning("osm-basemap", "OSM basemap is for interactive editor viewing only; rendering does not fetch basemap tiles."));
        }

        return messages;
    }

    private static void ValidateSource(
        QTilesProject project,
        string? projectPath,
        ProjectSourceConfig source,
        List<ValidationMessage> messages)
    {
        var imagePath = !string.IsNullOrWhiteSpace(projectPath)
            ? Resolve(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory, source.Image)
            : ProjectPaths.Resolve(project, source.Image);
        var sourceName = ProjectSources.DisplayName(source);
        if (string.IsNullOrWhiteSpace(source.Image) || !File.Exists(imagePath))
        {
            messages.Add(Error("source-image-missing", $"Source {sourceName} image does not exist: {source.Image}"));
        }

        var enabled = source.Georeference.ControlPoints.Where(p => p.Enabled).ToList();
        var transform = source.Georeference.Transform.Type.Trim().ToLowerInvariant();
        if (transform is not ("affine" or "similarity"))
        {
            messages.Add(Error("transform-type", $"Source {sourceName} has unknown transform type '{source.Georeference.Transform.Type}'. Supported types: affine, similarity."));
        }

        if (transform == "affine" && enabled.Count < 3)
        {
            messages.Add(Error("affine-points", $"Source {sourceName} affine transform requires at least 3 enabled control points."));
        }

        if (transform == "similarity" && enabled.Count < 2)
        {
            messages.Add(Error("similarity-points", $"Source {sourceName} similarity transform requires at least 2 enabled control points."));
        }

        if (transform == "affine" && enabled.Count < 4)
        {
            messages.Add(Warning("affine-redundancy", $"Source {sourceName} has fewer than 4 enabled points, leaving no redundancy for affine error checking."));
        }

        foreach (var point in enabled)
        {
            // Negated range checks so NaN (which compares false to everything) is rejected too.
            if (!(point.World.Lon is >= -180 and <= 180) || !(point.World.Lat is >= -90 and <= 90))
            {
                messages.Add(Error("lon-lat", $"Source {sourceName} control point {point.Id} has invalid lon/lat."));
            }

            if (!double.IsFinite(point.Image.X) || !double.IsFinite(point.Image.Y))
            {
                messages.Add(Error("image-point", $"Source {sourceName} control point {point.Id} has invalid image coordinates."));
            }
        }
    }

    public static bool HasErrors(IEnumerable<ValidationMessage> messages) => messages.Any(m => m.Severity == ValidationSeverity.Error);

    private static string Resolve(string baseDirectory, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

    private static ValidationMessage Error(string code, string message) => new(ValidationSeverity.Error, code, message);
    private static ValidationMessage Warning(string code, string message) => new(ValidationSeverity.Warning, code, message);
    private static ValidationMessage Info(string code, string message) => new(ValidationSeverity.Info, code, message);
}
