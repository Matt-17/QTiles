namespace QTiles.Core.Config;

public sealed class QTilesProject
{
    [YamlDotNet.Serialization.YamlIgnore]
    public string? BaseDirectory { get; set; }

    public int Version { get; set; } = 1;
    public ProjectInfo Project { get; set; } = new();
    public SourceConfig Source { get; set; } = new();
    public GeoreferenceConfig Georeference { get; set; } = new();
    public RenderConfig Render { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public EditorConfig Editor { get; set; } = new();
}

public sealed class ProjectInfo
{
    public string Name { get; set; } = "QTiles project";
    public string? Description { get; set; }
}

public sealed class SourceConfig
{
    public string Image { get; set; } = "";
    public string Origin { get; set; } = "top-left";
    public string Units { get; set; } = "pixels";
}

public sealed class GeoreferenceConfig
{
    public string InputCrs { get; set; } = "EPSG:4326";
    public string InternalCrs { get; set; } = "EPSG:3857";
    public TransformConfig Transform { get; set; } = new();
    public List<ControlPointConfig> ControlPoints { get; set; } = [];
}

public sealed class TransformConfig
{
    public string Type { get; set; } = "affine";
    public string Solve { get; set; } = "least-squares";
}

public sealed class RenderConfig
{
    public string Scheme { get; set; } = "xyz";
    public int TileSize { get; set; } = 256;
    public int MinZoom { get; set; }
    public int MaxZoom { get; set; }
    public string Format { get; set; } = "png";
    public int Quality { get; set; } = 90;
    public string Resampling { get; set; } = "lanczos3";
    public string Background { get; set; } = "transparent";
    public bool SkipEmptyTiles { get; set; } = true;
    public bool Overwrite { get; set; } = true;
    public string Bounds { get; set; } = "auto";
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "./tiles";
    public bool TileJson { get; set; } = true;
    public string TileJsonPath { get; set; } = "./tiles/tilejson.json";
}

public sealed class EditorConfig
{
    public BasemapConfig Basemap { get; set; } = new();
    public ImagePaneConfig ImagePane { get; set; } = new();
    public PreviewConfig Preview { get; set; } = new();
}

public sealed class BasemapConfig
{
    public string Type { get; set; } = "osm";
    public string Url { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
}

public sealed class ImagePaneConfig
{
    public bool LockWithTwoPoints { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
}

public sealed class PreviewConfig
{
    public double Opacity { get; set; } = 0.65;
}

public sealed class ControlPointConfig
{
    public ControlPointId Id { get; set; } = new("");
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public ImagePoint Image { get; set; } = new();
    public WorldPoint World { get; set; } = new();
}

public sealed record ControlPointId(string Value)
{
    public override string ToString() => Value;
}

public sealed class ImagePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class WorldPoint
{
    public double Lon { get; set; }
    public double Lat { get; set; }
}
