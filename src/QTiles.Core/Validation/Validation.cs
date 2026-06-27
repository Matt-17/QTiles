using QTiles.Core.Config;

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

        var imagePath = !string.IsNullOrWhiteSpace(projectPath)
            ? Resolve(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory, project.Source.Image)
            : ProjectPaths.Resolve(project, project.Source.Image);
        if (string.IsNullOrWhiteSpace(project.Source.Image) || !File.Exists(imagePath))
        {
            messages.Add(Error("source-image-missing", $"Source image does not exist: {project.Source.Image}"));
        }

        if (project.Render.TileSize <= 0)
        {
            messages.Add(Error("tile-size", "render.tileSize must be positive."));
        }

        if (project.Render.MinZoom > project.Render.MaxZoom)
        {
            messages.Add(Error("zoom-range", "render.minZoom must be less than or equal to render.maxZoom."));
        }

        if (!new[] { "png", "jpg", "jpeg", "webp" }.Contains(project.Render.Format.Trim().ToLowerInvariant()))
        {
            messages.Add(Error("format", "render.format must be png, jpg, or webp."));
        }

        var enabled = project.Georeference.ControlPoints.Where(p => p.Enabled).ToList();
        var transform = project.Georeference.Transform.Type.Trim().ToLowerInvariant();
        if (transform == "affine" && enabled.Count < 3)
        {
            messages.Add(Error("affine-points", "Affine transform requires at least 3 enabled control points."));
        }

        if (transform == "similarity" && enabled.Count < 2)
        {
            messages.Add(Error("similarity-points", "Similarity transform requires at least 2 enabled control points."));
        }

        if (transform == "affine" && enabled.Count < 4)
        {
            messages.Add(Warning("affine-redundancy", "Fewer than 4 enabled points leaves no redundancy for affine error checking."));
        }

        foreach (var point in enabled)
        {
            if (point.World.Lon is < -180 or > 180 || point.World.Lat is < -90 or > 90)
            {
                messages.Add(Error("lon-lat", $"Control point {point.Id} has invalid lon/lat."));
            }
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

    public static bool HasErrors(IEnumerable<ValidationMessage> messages) => messages.Any(m => m.Severity == ValidationSeverity.Error);

    private static string Resolve(string baseDirectory, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

    private static ValidationMessage Error(string code, string message) => new(ValidationSeverity.Error, code, message);
    private static ValidationMessage Warning(string code, string message) => new(ValidationSeverity.Warning, code, message);
    private static ValidationMessage Info(string code, string message) => new(ValidationSeverity.Info, code, message);
}
