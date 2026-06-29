using QTiles.Core.Config;
using QTiles.Core.Geo;
using QTiles.Core.Imaging;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;
using NetVips;

namespace QTiles.Core.Tests;

public sealed class GeoTests
{
    [Fact]
    public void WebMercator_LonLatToNormalized_KnownValues()
    {
        var point = WebMercator.LonLatToNormalized(0, 0);
        Assert.Equal(0.5, point.X, 9);
        Assert.Equal(0.5, point.Y, 9);
    }

    [Fact]
    public void WebMercator_ClampsLatitude()
    {
        var high = WebMercator.LonLatToNormalized(0, 120);
        var max = WebMercator.LonLatToNormalized(0, WebMercator.MaxLatitude);
        Assert.Equal(max.Y, high.Y, 9);
    }

    [Fact]
    public void TileMath_BoundsToTileRange_Zoom0()
    {
        var range = TileMath.BoundsToTileRange(new GeoBounds(-180, -85, 180, 85), 0);
        Assert.Equal(new TileRange(0, 0, 0, 0, 0), range);
    }

    [Fact]
    public void TileMath_BoundsToTileRange_KnownCityBounds()
    {
        var range = TileMath.BoundsToTileRange(new GeoBounds(13.70, 51.02, 13.80, 51.08), 10);
        Assert.True(range.MinX <= range.MaxX);
        Assert.True(range.MinY <= range.MaxY);
        Assert.InRange(range.MinX, 0, 1023);
    }
}

public sealed class TransformTests
{
    [Fact]
    public void AffineSolver_ThreePoints_Exact()
    {
        var transform = new TransformSolver().SolveAffine(new[]
        {
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20),
            Point("3", 0, 100, 0.10, 0.30)
        });

        var actual = transform.ImageToWorld(new ImagePoint { X = 50, Y = 50 });
        Assert.Equal(0.15, actual.X, 8);
        Assert.Equal(0.25, actual.Y, 8);
    }

    [Fact]
    public void AffineTransform_Inverse_RoundTrips()
    {
        var transform = new AffineTransform(0.01, 0.001, 0.2, -0.002, 0.02, 0.3);
        var original = new ImagePoint { X = 22, Y = 45 };
        var world = transform.ImageToWorld(original);
        var roundTrip = transform.WorldToImage(world);
        Assert.Equal(original.X, roundTrip.X, 8);
        Assert.Equal(original.Y, roundTrip.Y, 8);
    }

    [Fact]
    public void SimilaritySolver_TwoPoints_Exact()
    {
        var transform = new TransformSolver().SolveSimilarity(new[]
        {
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20)
        });

        var actual = transform.ImageToWorld(new ImagePoint { X = 100, Y = 0 });
        Assert.Equal(0.20, actual.X, 8);
        Assert.Equal(0.20, actual.Y, 8);
    }

    [Fact]
    public void Solver_DisabledPoints_AreIgnored()
    {
        var project = ProjectWithPoints(
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20),
            Point("3", 0, 100, 0.10, 0.30),
            Point("bad", 5000, 5000, 0.90, 0.90, enabled: false));

        var result = new TransformSolver().Solve(project, 100, 100);
        Assert.DoesNotContain(result.Errors, e => e.Id.Value == "bad");
    }

    [Fact]
    public void Solver_ReportsPerPointErrors()
    {
        var result = new TransformSolver().Solve(ProjectWithPoints(
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20),
            Point("3", 0, 100, 0.10, 0.30)), 100, 100);

        Assert.Equal(3, result.Errors.Count);
        Assert.True(result.RmsPixelsAtMaxZoom < 1e-6);
    }

    private static QTilesProject ProjectWithPoints(params ControlPointConfig[] points) => new()
    {
        Source = new SourceConfig { Image = "sample.png" },
        Georeference = new GeoreferenceConfig { ControlPoints = points.ToList() },
        Render = new RenderConfig { MaxZoom = 1 }
    };

    private static ControlPointConfig Point(string id, double x, double y, double u, double v, bool enabled = true)
    {
        var lonLat = WebMercator.NormalizedToLonLat(u, v);
        return new ControlPointConfig
        {
            Id = new ControlPointId(id),
            Enabled = enabled,
            Image = new ImagePoint { X = x, Y = y },
            World = new WorldPoint { Lon = lonLat.Lon, Lat = lonLat.Lat }
        };
    }
}

public sealed class YamlTests
{
    [Fact]
    public void Yaml_RoundTrip_MinimalProject()
    {
        var serializer = new QTilesYamlSerializer();
        var yaml = serializer.Serialize(new QTilesProject { Project = new ProjectInfo { Name = "Old map" } });
        var roundTrip = serializer.Deserialize(yaml);
        Assert.Equal("Old map", roundTrip.Project.Name);
    }

    [Fact]
    public void Yaml_RoundTrip_ControlPointIds_IntLike()
    {
        var project = new QTilesProject();
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId("1") });
        var roundTrip = new QTilesYamlSerializer().Deserialize(new QTilesYamlSerializer().Serialize(project));
        Assert.Equal("1", roundTrip.Georeference.ControlPoints[0].Id.Value);
    }

    [Fact]
    public void Yaml_RoundTrip_ControlPointIds_GuidLike()
    {
        var id = Guid.NewGuid().ToString();
        var project = new QTilesProject();
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId(id) });
        var roundTrip = new QTilesYamlSerializer().Deserialize(new QTilesYamlSerializer().Serialize(project));
        Assert.Equal(id, roundTrip.Georeference.ControlPoints[0].Id.Value);
    }
}

public sealed class RendererTests
{
    [Fact]
    public async Task Renderer_WritesExpectedTilePaths()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var writer = new RecordingTileWriter();
        var renderer = new TileRenderer(new FixedImageInfoReader(256, 256), writer, new(), new());
        var project = Project(source, temp.Path);

        await renderer.RenderAsync(project, null, CancellationToken.None);

        Assert.Contains(writer.Paths, p => p.EndsWith(Path.Combine("0", "0", "0.png")));
    }

    [Fact]
    public async Task Renderer_TileJson_WritesBoundsAndZooms()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var renderer = new TileRenderer(new FixedImageInfoReader(256, 256), new RecordingTileWriter(), new(), new());
        var project = Project(source, temp.Path);

        await renderer.RenderAsync(project, null, CancellationToken.None);

        var json = await File.ReadAllTextAsync(project.Output.TileJsonPath);
        Assert.Contains("\"tilejson\": \"3.0.0\"", json);
        Assert.Contains("\"minzoom\": 0", json);
    }

    [Fact]
    public async Task Renderer_AffineIdentityLikeMapping_ProducesExpectedPixels()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        WriteRgbSource(source, 4, 4);
        var project = FullWorldProject(source, temp.Path, tileSize: 2);

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "1", "1", "0.png"));
        var pixel = rendered.Getpoint(0, 0);
        Assert.InRange(pixel[0], 85, 95);
        Assert.InRange(pixel[1], 5, 15);
        Assert.InRange(pixel[2], 195, 205);
        Assert.InRange(pixel[3], 250, 255);
    }

    [Fact]
    public async Task Renderer_SkipEmptyTiles_Works()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "transparent.png");
        WriteRgbaSource(source, 1, 1, [255, 0, 0, 0]);
        var project = FullWorldProject(source, temp.Path, tileSize: 1);
        project.Render.SkipEmptyTiles = true;

        var summary = await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        Assert.Equal(0, summary.TilesWritten);
        Assert.Equal(1, summary.TilesSkipped);
        Assert.False(File.Exists(Path.Combine(temp.Path, "0", "0", "0.png")));
    }

    [Fact]
    public async Task Renderer_PngPreservesTransparency()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "transparent.png");
        WriteRgbaSource(source, 1, 1, [255, 0, 0, 0]);
        var project = FullWorldProject(source, temp.Path, tileSize: 1);
        project.Render.SkipEmptyTiles = false;

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "0", "0", "0.png"));
        Assert.Equal(0, rendered.Getpoint(0, 0)[3]);
    }

    [Fact]
    public async Task Renderer_AutoZoom_UsesSolvedImageScale()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var writer = new RecordingTileWriter();
        var renderer = new TileRenderer(new FixedImageInfoReader(1000, 1000), writer, new(), new());
        var project = AutoZoomProject(source, temp.Path);

        var summary = await renderer.RenderAsync(project, null, CancellationToken.None);

        Assert.True(summary.MinZoom > 0);
        Assert.True(summary.MaxZoom >= summary.MinZoom);
        Assert.Equal(summary.TilesWritten, writer.Paths.Count);
        Assert.All(writer.Requests, request => Assert.InRange(request.Tile.Z, summary.MinZoom, summary.MaxZoom));
    }

    private static QTilesProject Project(string source, string output) => new()
    {
        Project = new ProjectInfo { Name = "Renderer test" },
        Source = new SourceConfig { Image = source },
        Output = new OutputConfig { Directory = output, TileJson = true, TileJsonPath = Path.Combine(output, "tilejson.json") },
        Render = new RenderConfig { AutoZoom = false, MinZoom = 0, MaxZoom = 0, Format = "png" },
        Georeference = new GeoreferenceConfig
        {
            ControlPoints =
            [
                TransformTestsPoint("1", 0, 0, 0.49, 0.49),
                TransformTestsPoint("2", 256, 0, 0.51, 0.49),
                TransformTestsPoint("3", 0, 256, 0.49, 0.51)
            ]
        }
    };

    private static QTilesProject FullWorldProject(string source, string output, int tileSize) => new()
    {
        Project = new ProjectInfo { Name = "Full world test" },
        Source = new SourceConfig { Image = source },
        Output = new OutputConfig { Directory = output, TileJson = true, TileJsonPath = Path.Combine(output, "tilejson.json") },
        Render = new RenderConfig { AutoZoom = false, TileSize = tileSize, MinZoom = tileSize == 1 ? 0 : 1, MaxZoom = tileSize == 1 ? 0 : 1, Format = "png", Resampling = "nearest", SkipEmptyTiles = false },
        Georeference = new GeoreferenceConfig
        {
            ControlPoints =
            [
                NormalizedPoint("1", 0, 0, 0, 0),
                NormalizedPoint("2", tileSize == 1 ? 1 : 4, 0, 1, 0),
                NormalizedPoint("3", 0, tileSize == 1 ? 1 : 4, 0, 1)
            ]
        }
    };

    private static QTilesProject AutoZoomProject(string source, string output) => new()
    {
        Project = new ProjectInfo { Name = "Auto zoom test" },
        Source = new SourceConfig { Image = source },
        Output = new OutputConfig { Directory = output, TileJson = true, TileJsonPath = Path.Combine(output, "tilejson.json") },
        Render = new RenderConfig { AutoZoom = true, MinZoom = 0, MaxZoom = 0, Format = "png" },
        Georeference = new GeoreferenceConfig
        {
            ControlPoints =
            [
                NormalizedPoint("1", 0, 0, 0.50, 0.50),
                NormalizedPoint("2", 1000, 0, 0.5001, 0.50),
                NormalizedPoint("3", 0, 1000, 0.50, 0.5001)
            ]
        }
    };

    private static ControlPointConfig NormalizedPoint(string id, double x, double y, double u, double v)
    {
        var lonLat = WebMercator.NormalizedToLonLat(u, v);
        return new ControlPointConfig
        {
            Id = new ControlPointId(id),
            Image = new ImagePoint { X = x, Y = y },
            World = new WorldPoint { Lon = lonLat.Lon, Lat = lonLat.Lat }
        };
    }

    private static void WriteRgbSource(string path, int width, int height)
    {
        var data = new byte[width * height * 3];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[offset++] = (byte)(x * 40 + 10);
                data[offset++] = (byte)(y * 40 + 10);
                data[offset++] = 200;
            }
        }

        using var image = Image.NewFromMemory(data, width, height, 3, Enums.BandFormat.Uchar);
        image.Pngsave(path);
    }

    private static void WriteRgbaSource(string path, int width, int height, byte[] pixel)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            Array.Copy(pixel, 0, data, i, 4);
        }

        using var image = Image.NewFromMemory(data, width, height, 4, Enums.BandFormat.Uchar);
        image.Pngsave(path);
    }

    private static ControlPointConfig TransformTestsPoint(string id, double x, double y, double u, double v)
    {
        var lonLat = WebMercator.NormalizedToLonLat(u, v);
        return new ControlPointConfig
        {
            Id = new ControlPointId(id),
            Image = new ImagePoint { X = x, Y = y },
            World = new WorldPoint { Lon = lonLat.Lon, Lat = lonLat.Lat }
        };
    }

    private sealed class FixedImageInfoReader(int width, int height) : IImageInfoReader
    {
        public ImageInfo Read(string path) => new(width, height);
    }

    private sealed class RecordingTileWriter : IRenderedTileWriter
    {
        public List<string> Paths { get; } = [];
        public List<RenderedTileRequest> Requests { get; } = [];
        public Task<bool> WriteAsync(string path, RenderedTileRequest request, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            Requests.Add(request);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "tile");
            return Task.FromResult(true);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qtiles-{Guid.NewGuid():N}");
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
