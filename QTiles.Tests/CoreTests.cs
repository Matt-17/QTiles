using QTiles.Core.Config;
using QTiles.Core.Geo;
using QTiles.Core.Imaging;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;
using QTiles.Core.Validation;
using NetVips;

namespace QTiles.Tests;

[TestClass]
public sealed class GeoTests
{
    [TestMethod]
    public void WebMercator_LonLatToNormalized_KnownValues()
    {
        var point = WebMercator.LonLatToNormalized(0, 0);
        Assert.AreEqual(0.5, point.X, 1e-9);
        Assert.AreEqual(0.5, point.Y, 1e-9);
    }

    [TestMethod]
    public void WebMercator_ClampsLatitude()
    {
        var high = WebMercator.LonLatToNormalized(0, 120);
        var max = WebMercator.LonLatToNormalized(0, WebMercator.MaxLatitude);
        Assert.AreEqual(max.Y, high.Y, 1e-9);
    }

    [TestMethod]
    public void TileMath_BoundsToTileRange_Zoom0()
    {
        var range = TileMath.BoundsToTileRange(new GeoBounds(-180, -85, 180, 85), 0);
        Assert.AreEqual(new TileRange(0, 0, 0, 0, 0), range);
    }

    [TestMethod]
    public void TileMath_BoundsToTileRange_KnownCityBounds()
    {
        var range = TileMath.BoundsToTileRange(new GeoBounds(13.70, 51.02, 13.80, 51.08), 10);
        Assert.IsTrue(range.MinX <= range.MaxX);
        Assert.IsTrue(range.MinY <= range.MaxY);
        TestAssert.InRange(range.MinX, 0, 1023);
    }

    [TestMethod]
    public void TileMath_MaxEdgeOnTileBoundary_DoesNotIncludeExtraColumn()
    {
        var range = TileMath.NormalizedBoundsToTileRange(0.25, 0.25, 0.5, 0.5, zoom: 2);

        Assert.AreEqual(1, range.MinX);
        Assert.AreEqual(1, range.MaxX);
        Assert.AreEqual(1, range.MinY);
        Assert.AreEqual(1, range.MaxY);
    }

    [TestMethod]
    public void TileMath_ZeroAreaBoundsOnTileBoundary_YieldsSingleTile()
    {
        var range = TileMath.NormalizedBoundsToTileRange(0.5, 0.5, 0.5, 0.5, zoom: 2);

        Assert.AreEqual(2, range.MinX);
        Assert.AreEqual(2, range.MaxX);
        Assert.AreEqual(2, range.MinY);
        Assert.AreEqual(2, range.MaxY);
    }

    [TestMethod]
    public void TileMath_FullWorld_StillCoversAllTiles()
    {
        var range = TileMath.NormalizedBoundsToTileRange(0.0, 0.0, 1.0, 1.0, zoom: 3);

        Assert.AreEqual(0, range.MinX);
        Assert.AreEqual(7, range.MaxX);
        Assert.AreEqual(0, range.MinY);
        Assert.AreEqual(7, range.MaxY);
    }
}

[TestClass]
public sealed class TransformTests
{
    [TestMethod]
    public void AffineSolver_ThreePoints_Exact()
    {
        var transform = new TransformSolver().SolveAffine(new[]
        {
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20),
            Point("3", 0, 100, 0.10, 0.30)
        });

        var actual = transform.ImageToWorld(new ImagePoint { X = 50, Y = 50 });
        Assert.AreEqual(0.15, actual.X, 1e-8);
        Assert.AreEqual(0.25, actual.Y, 1e-8);
    }

    [TestMethod]
    public void AffineTransform_Inverse_RoundTrips()
    {
        var transform = new AffineTransform(0.01, 0.001, 0.2, -0.002, 0.02, 0.3);
        var original = new ImagePoint { X = 22, Y = 45 };
        var world = transform.ImageToWorld(original);
        var roundTrip = transform.WorldToImage(world);
        Assert.AreEqual(original.X, roundTrip.X, 1e-8);
        Assert.AreEqual(original.Y, roundTrip.Y, 1e-8);
    }

    [TestMethod]
    public void SimilaritySolver_TwoPoints_Exact()
    {
        var transform = new TransformSolver().SolveSimilarity(new[]
        {
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20)
        });

        var actual = transform.ImageToWorld(new ImagePoint { X = 100, Y = 0 });
        Assert.AreEqual(0.20, actual.X, 1e-8);
        Assert.AreEqual(0.20, actual.Y, 1e-8);
    }

    [TestMethod]
    public void Solver_DisabledPoints_AreIgnored()
    {
        var project = ProjectWithPoints(
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20),
            Point("3", 0, 100, 0.10, 0.30),
            Point("bad", 5000, 5000, 0.90, 0.90, enabled: false));

        var result = new TransformSolver().Solve(project, 100, 100);
        Assert.IsFalse(result.Errors.Any(e => e.Id.Value == "bad"));
    }

    [TestMethod]
    public void Solver_ReportsPerPointErrors()
    {
        var result = new TransformSolver().Solve(ProjectWithPoints(
            Point("1", 0, 0, 0.10, 0.20),
            Point("2", 100, 0, 0.20, 0.20),
            Point("3", 0, 100, 0.10, 0.30)), 100, 100);

        Assert.AreEqual(3, result.Errors.Count);
        Assert.IsTrue(result.RmsPixelsAtMaxZoom < 1e-6);
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

[TestClass]
public sealed class YamlTests
{
    [TestMethod]
    public void Yaml_RoundTrip_MinimalProject()
    {
        var serializer = new QTilesYamlSerializer();
        var yaml = serializer.Serialize(new QTilesProject { Project = new ProjectInfo { Name = "Old map" } });
        var roundTrip = serializer.Deserialize(yaml);
        Assert.AreEqual("Old map", roundTrip.Project.Name);
    }

    [TestMethod]
    public void Yaml_RoundTrip_ControlPointIds_IntLike()
    {
        var project = new QTilesProject();
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId("1") });
        var roundTrip = new QTilesYamlSerializer().Deserialize(new QTilesYamlSerializer().Serialize(project));
        Assert.AreEqual("1", roundTrip.Georeference.ControlPoints[0].Id.Value);
    }

    [TestMethod]
    public void Yaml_RoundTrip_ControlPointIds_GuidLike()
    {
        var id = Guid.NewGuid().ToString();
        var project = new QTilesProject();
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId(id) });
        var roundTrip = new QTilesYamlSerializer().Deserialize(new QTilesYamlSerializer().Serialize(project));
        Assert.AreEqual(id, roundTrip.Georeference.ControlPoints[0].Id.Value);
    }

    [TestMethod]
    public void Yaml_RoundTrip_ControlPointLocked_OnlyWhenTrue()
    {
        var serializer = new QTilesYamlSerializer();
        var project = new QTilesProject();
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId("1"), Locked = true });
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId("2") });

        var yaml = serializer.Serialize(project);
        var roundTrip = serializer.Deserialize(yaml);

        StringAssert.Contains(yaml, "locked: true");
        Assert.IsFalse(yaml.Contains("locked: false", StringComparison.Ordinal));
        Assert.IsTrue(roundTrip.Georeference.ControlPoints[0].Locked is true);
        Assert.IsNull(roundTrip.Georeference.ControlPoints[1].Locked);
    }

    [TestMethod]
    public void Yaml_RoundTrip_MultipleSources_PreservesOrderAndPoints()
    {
        var project = new QTilesProject
        {
            Sources =
            [
                new ProjectSourceConfig
                {
                    Id = "sheet-a",
                    Image = "./a.png",
                    Georeference = new GeoreferenceConfig
                    {
                        ControlPoints = [new ControlPointConfig { Id = new ControlPointId("a1") }]
                    }
                },
                new ProjectSourceConfig
                {
                    Id = "sheet-b",
                    Image = "./b.png",
                    Georeference = new GeoreferenceConfig
                    {
                        ControlPoints = [new ControlPointConfig { Id = new ControlPointId("b1") }]
                    }
                }
            ]
        };

        var yaml = new QTilesYamlSerializer().Serialize(project);
        var roundTrip = new QTilesYamlSerializer().Deserialize(yaml);

        StringAssert.Contains(yaml, "sources:");
        Assert.IsNotNull(roundTrip.Sources);
        Assert.AreEqual("sheet-a", roundTrip.Sources[0].Id);
        Assert.AreEqual("sheet-b", roundTrip.Sources[1].Id);
        Assert.AreEqual("b1", roundTrip.Sources[1].Georeference.ControlPoints[0].Id.Value);
    }

    [TestMethod]
    public void Yaml_MultiSourceProject_DoesNotWriteLegacySourceBlocks()
    {
        var project = new QTilesProject
        {
            Source = new SourceConfig { Image = "./legacy-old.png" },
            Georeference = new GeoreferenceConfig
            {
                ControlPoints = [new ControlPointConfig { Id = new ControlPointId("legacy") }]
            },
            Sources =
            [
                new ProjectSourceConfig { Id = "sheet-a", Image = "./a.png" }
            ]
        };

        var yaml = new QTilesYamlSerializer().Serialize(project);
        var topLevelKeys = yaml.Split('\n').Where(line => line.Length > 0 && !char.IsWhiteSpace(line[0])).ToList();

        StringAssert.Contains(yaml, "sources:");
        Assert.IsFalse(topLevelKeys.Any(line => line.StartsWith("source:", StringComparison.Ordinal)));
        Assert.IsFalse(topLevelKeys.Any(line => line.StartsWith("georeference:", StringComparison.Ordinal)));
        Assert.IsFalse(yaml.Contains("legacy-old.png", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Yaml_SingleSourceProject_StillWritesLegacyBlocks()
    {
        var project = new QTilesProject
        {
            Source = new SourceConfig { Image = "./map.png" }
        };
        project.Georeference.ControlPoints.Add(new ControlPointConfig { Id = new ControlPointId("1") });

        var yaml = new QTilesYamlSerializer().Serialize(project);
        var roundTrip = new QTilesYamlSerializer().Deserialize(yaml);

        StringAssert.Contains(yaml, "source:");
        StringAssert.Contains(yaml, "georeference:");
        Assert.AreEqual("./map.png", roundTrip.Source.Image);
        Assert.AreEqual("1", roundTrip.Georeference.ControlPoints[0].Id.Value);
    }

    [TestMethod]
    public void Yaml_RoundTrip_SourceOpacity_PreservesNonDefaultAndOmitsDefault()
    {
        var project = new QTilesProject
        {
            Sources =
            [
                new ProjectSourceConfig { Id = "full", Image = "./full.png" },
                new ProjectSourceConfig { Id = "half", Image = "./half.png", Opacity = 0.5 }
            ]
        };

        var yaml = new QTilesYamlSerializer().Serialize(project);
        var roundTrip = new QTilesYamlSerializer().Deserialize(yaml);

        StringAssert.Contains(yaml, "opacity: 0.5");
        Assert.IsFalse(yaml.Contains("opacity: 1", StringComparison.Ordinal));
        Assert.IsNotNull(roundTrip.Sources);
        Assert.AreEqual(1.0, roundTrip.Sources[0].Opacity, 0.000001);
        Assert.AreEqual(0.5, roundTrip.Sources[1].Opacity, 0.000001);
    }

    [TestMethod]
    public void ProjectPathFormatter_UsesRelativePathForSubdirectory()
    {
        using var temp = new YamlTempFolder();
        var root = Path.Combine(temp.Path, "project");
        var output = Path.Combine(root, "tiles2");
        Directory.CreateDirectory(root);

        var formatted = ProjectPathFormatter.FormatForProject(root, output);

        Assert.AreEqual("./tiles2", formatted);
    }

    [TestMethod]
    public void ProjectPathFormatter_UsesAbsolutePathOutsideProjectRoot()
    {
        using var temp = new YamlTempFolder();
        var root = Path.Combine(temp.Path, "project");
        var output = Path.Combine(temp.Path, "tiles2");
        Directory.CreateDirectory(root);

        var formatted = ProjectPathFormatter.FormatForProject(root, output);

        Assert.AreEqual(Path.GetFullPath(output), formatted);
        Assert.IsFalse(formatted.Contains("..", StringComparison.Ordinal));
    }

    private sealed class YamlTempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qtiles-yaml-{Guid.NewGuid():N}");
        public YamlTempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

[TestClass]
public sealed class ValidationTests
{
    [TestMethod]
    public void Validator_MultipleSources_RequiresUniqueIdsButIgnoresDisabledMissingImages()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        File.WriteAllText(source, "placeholder");
        var project = new QTilesProject
        {
            BaseDirectory = temp.Path,
            Sources =
            [
                new ProjectSourceConfig
                {
                    Id = "sheet",
                    Image = source,
                    Georeference = ValidGeoreference()
                },
                new ProjectSourceConfig
                {
                    Id = "sheet",
                    Image = "./missing.png",
                    Enabled = false
                }
            ]
        };

        var messages = new ProjectValidator().Validate(project);

        Assert.IsTrue(messages.Any(message => message.Code == "source-id-duplicate"));
        Assert.IsFalse(messages.Any(message => message.Code == "source-image-missing"));
    }

    [TestMethod]
    public void Validator_SourceOpacity_MustBeBetweenZeroAndOne()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        File.WriteAllText(source, "placeholder");
        var project = new QTilesProject
        {
            BaseDirectory = temp.Path,
            Sources =
            [
                new ProjectSourceConfig
                {
                    Id = "sheet",
                    Image = source,
                    Opacity = 1.2,
                    Georeference = ValidGeoreference()
                }
            ]
        };

        var messages = new ProjectValidator().Validate(project);

        Assert.IsTrue(messages.Any(message => message.Code == "source-opacity"));
    }

    [TestMethod]
    public void Validator_ManualZoom_OutsideSupportedRange_IsError()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        File.WriteAllText(source, "placeholder");
        var project = new QTilesProject
        {
            BaseDirectory = temp.Path,
            Source = new SourceConfig { Image = source },
            Georeference = ValidGeoreference(),
            Render = new RenderConfig { AutoZoom = false, MinZoom = 0, MaxZoom = 32 }
        };

        var messages = new ProjectValidator().Validate(project);

        Assert.IsTrue(messages.Any(message => message.Code == "zoom-bounds"));
    }

    [TestMethod]
    public void Validator_NegativeManualZoom_IsError()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        File.WriteAllText(source, "placeholder");
        var project = new QTilesProject
        {
            BaseDirectory = temp.Path,
            Source = new SourceConfig { Image = source },
            Georeference = ValidGeoreference(),
            Render = new RenderConfig { AutoZoom = false, MinZoom = -1, MaxZoom = 5 }
        };

        var messages = new ProjectValidator().Validate(project);

        Assert.IsTrue(messages.Any(message => message.Code == "zoom-bounds"));
    }

    [TestMethod]
    public void Validator_NaNCoordinates_AreErrors()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        File.WriteAllText(source, "placeholder");
        var georeference = ValidGeoreference();
        georeference.ControlPoints[0].World.Lon = double.NaN;
        georeference.ControlPoints[1].Image.X = double.NaN;
        var project = new QTilesProject
        {
            BaseDirectory = temp.Path,
            Source = new SourceConfig { Image = source },
            Georeference = georeference
        };

        var messages = new ProjectValidator().Validate(project);

        Assert.IsTrue(messages.Any(message => message.Code == "lon-lat"));
        Assert.IsTrue(messages.Any(message => message.Code == "image-point"));
    }

    [TestMethod]
    public void ZoomRangeCalculator_ClampToSupportedRange_ClampsOutOfRangeValues()
    {
        var render = new RenderConfig { AutoZoom = false, MinZoom = -1, MaxZoom = 24 };

        var clamped = ZoomRangeCalculator.ClampToSupportedRange(render);

        Assert.IsTrue(clamped);
        Assert.AreEqual(0, render.MinZoom);
        Assert.AreEqual(ZoomRangeCalculator.MaxSupportedZoom, render.MaxZoom);
    }

    [TestMethod]
    public void ZoomRangeCalculator_ClampToSupportedRange_LeavesValidValues()
    {
        var render = new RenderConfig { AutoZoom = false, MinZoom = 2, MaxZoom = 18 };

        var clamped = ZoomRangeCalculator.ClampToSupportedRange(render);

        Assert.IsFalse(clamped);
        Assert.AreEqual(2, render.MinZoom);
        Assert.AreEqual(18, render.MaxZoom);
    }

    [TestMethod]
    public void Validator_UnknownTransformType_IsError()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        File.WriteAllText(source, "placeholder");
        var georeference = ValidGeoreference();
        georeference.Transform.Type = "similarty";
        var project = new QTilesProject
        {
            BaseDirectory = temp.Path,
            Source = new SourceConfig { Image = source },
            Georeference = georeference
        };

        var messages = new ProjectValidator().Validate(project);

        Assert.IsTrue(messages.Any(message => message.Code == "transform-type"));
    }

    private static GeoreferenceConfig ValidGeoreference() => new()
    {
        ControlPoints =
        [
            NormalizedPoint("1", 0, 0, 0.49, 0.49),
            NormalizedPoint("2", 10, 0, 0.51, 0.49),
            NormalizedPoint("3", 0, 10, 0.49, 0.51)
        ]
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

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qtiles-validation-{Guid.NewGuid():N}");
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

[TestClass]
public sealed class RendererTests
{
    [TestMethod]
    public async Task Renderer_WritesExpectedTilePaths()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var writer = new RecordingTileWriter();
        var renderer = new TileRenderer(new FixedImageInfoReader(256, 256), writer, new(), new());
        var project = Project(source, temp.Path);

        await renderer.RenderAsync(project, null, CancellationToken.None);

        Assert.IsTrue(writer.Paths.Any(p => p.EndsWith(Path.Combine("0", "0", "0.png"))));
    }

    [TestMethod]
    public async Task Renderer_TileJson_WritesBoundsAndZooms()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var renderer = new TileRenderer(new FixedImageInfoReader(256, 256), new RecordingTileWriter(), new(), new());
        var project = Project(source, temp.Path);

        await renderer.RenderAsync(project, null, CancellationToken.None);

        var json = await File.ReadAllTextAsync(project.Output.TileJsonPath);
        StringAssert.Contains(json, "\"tilejson\": \"3.0.0\"");
        StringAssert.Contains(json, "\"minzoom\": 0");
    }

    [TestMethod]
    public async Task Renderer_TileJson_JpegFormat_UsesJpgExtensionInTileTemplate()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var writer = new RecordingTileWriter();
        var renderer = new TileRenderer(new FixedImageInfoReader(256, 256), writer, new(), new());
        var project = Project(source, temp.Path);
        project.Render.Format = "jpeg";

        await renderer.RenderAsync(project, null, CancellationToken.None);

        Assert.IsTrue(writer.Paths.Any(p => p.EndsWith(Path.Combine("0", "0", "0.jpg"))));
        var json = await File.ReadAllTextAsync(project.Output.TileJsonPath);
        StringAssert.Contains(json, "{y}.jpg");
        Assert.IsFalse(json.Contains("{y}.jpeg"));
    }

    [TestMethod]
    public async Task Renderer_AffineIdentityLikeMapping_ProducesExpectedPixels()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        WriteRgbSource(source, 4, 4);
        var project = FullWorldProject(source, temp.Path, tileSize: 2);

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "1", "1", "0.png"));
        var pixel = rendered.Getpoint(0, 0);
        TestAssert.InRange(pixel[0], 85, 95);
        TestAssert.InRange(pixel[1], 5, 15);
        TestAssert.InRange(pixel[2], 195, 205);
        TestAssert.InRange(pixel[3], 250, 255);
    }

    [TestMethod]
    public async Task Renderer_MultipleSources_CompositesLaterSourceOverEarlierSource()
    {
        using var temp = new TempFolder();
        var red = Path.Combine(temp.Path, "red.png");
        var blue = Path.Combine(temp.Path, "blue.png");
        WriteRgbaSource(red, 1, 1, [255, 0, 0, 255]);
        WriteRgbaSource(blue, 1, 1, [0, 0, 255, 255]);
        var project = MultiSourceFullWorldProject(red, blue, temp.Path);

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "0", "0", "0.png"));
        var pixel = rendered.Getpoint(0, 0);
        TestAssert.InRange(pixel[0], 0, 5);
        TestAssert.InRange(pixel[1], 0, 5);
        TestAssert.InRange(pixel[2], 250, 255);
        TestAssert.InRange(pixel[3], 250, 255);
    }

    [TestMethod]
    public async Task Renderer_MultipleSources_BlendsLaterSourceByOpacity()
    {
        using var temp = new TempFolder();
        var red = Path.Combine(temp.Path, "red.png");
        var blue = Path.Combine(temp.Path, "blue.png");
        WriteRgbaSource(red, 1, 1, [255, 0, 0, 255]);
        WriteRgbaSource(blue, 1, 1, [0, 0, 255, 255]);
        var project = MultiSourceFullWorldProject(red, blue, temp.Path);
        project.Sources![1].Opacity = 0.5;

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "0", "0", "0.png"));
        var pixel = rendered.Getpoint(0, 0);
        TestAssert.InRange(pixel[0], 120, 135);
        TestAssert.InRange(pixel[1], 0, 5);
        TestAssert.InRange(pixel[2], 120, 135);
        TestAssert.InRange(pixel[3], 250, 255);
    }

    [TestMethod]
    public async Task Renderer_DefaultHighQualityPath_WritesExpectedTileSize()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        WriteRgbSource(source, 4, 4);
        var project = FullWorldProject(source, temp.Path, tileSize: 2);
        project.Render.Resampling = RenderResampling.Default;

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "1", "1", "0.png"));
        Assert.AreEqual(2, rendered.Width);
        Assert.AreEqual(2, rendered.Height);
    }

    [TestMethod]
    public async Task Renderer_SkipEmptyTiles_Works()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "transparent.png");
        WriteRgbaSource(source, 1, 1, [255, 0, 0, 0]);
        var project = FullWorldProject(source, temp.Path, tileSize: 1);
        project.Render.SkipEmptyTiles = true;

        var summary = await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        Assert.AreEqual(0, summary.TilesWritten);
        Assert.AreEqual(1, summary.TilesSkipped);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "0", "0", "0.png")));
    }

    [TestMethod]
    public async Task Renderer_DefaultTileJsonPath_WritesIntoOutputDirectory()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        WriteRgbSource(source, 4, 4);
        var output = Path.Combine(temp.Path, "out");
        var project = FullWorldProject(source, output, tileSize: 2);
        project.Output.TileJsonPath = "";

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        Assert.IsTrue(File.Exists(Path.Combine(output, "tilejson.json")));
    }

    [TestMethod]
    public async Task Renderer_ClearOutputDirectory_RemovesStaleOutputBeforeRender()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        WriteRgbSource(source, 4, 4);
        var output = Path.Combine(temp.Path, "tiles");
        var project = FullWorldProject(source, output, tileSize: 2);
        project.Render.ClearOutputDirectory = true;
        Directory.CreateDirectory(Path.Combine(output, "9", "0"));
        var staleTile = Path.Combine(output, "9", "0", "0.png");
        await File.WriteAllTextAsync(staleTile, "stale");
        var staleFile = Path.Combine(output, "stale.txt");
        await File.WriteAllTextAsync(staleFile, "stale");

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        Assert.IsFalse(Directory.Exists(Path.Combine(output, "9")));
        Assert.IsFalse(File.Exists(staleFile));
        Assert.IsTrue(File.Exists(Path.Combine(output, "1", "1", "0.png")));
    }

    [TestMethod]
    public async Task Renderer_PngPreservesTransparency()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "transparent.png");
        WriteRgbaSource(source, 1, 1, [255, 0, 0, 0]);
        var project = FullWorldProject(source, temp.Path, tileSize: 1);
        project.Render.SkipEmptyTiles = false;

        await new TileRenderer().RenderAsync(project, null, CancellationToken.None);

        using var rendered = Image.NewFromFile(Path.Combine(temp.Path, "0", "0", "0.png"));
        Assert.AreEqual(0.0, rendered.Getpoint(0, 0)[3], 0.0001);
    }

    [TestMethod]
    public async Task Renderer_AutoZoom_UsesSolvedImageScale()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "sample.png");
        await File.WriteAllTextAsync(source, "placeholder");
        var writer = new RecordingTileWriter();
        var renderer = new TileRenderer(new FixedImageInfoReader(1000, 1000), writer, new(), new());
        var project = AutoZoomProject(source, temp.Path);

        var summary = await renderer.RenderAsync(project, null, CancellationToken.None);

        Assert.IsTrue(summary.MinZoom > 0);
        Assert.IsTrue(summary.MaxZoom >= summary.MinZoom);
        Assert.AreEqual(summary.TilesWritten, writer.Paths.Count);

        foreach (var request in writer.Requests)
        {
            TestAssert.InRange(request.Tile.Z, summary.MinZoom, summary.MaxZoom);
        }
    }

    [TestMethod]
    public void RenderConfig_DefaultResampling_UsesHighQualityInterpolator()
    {
        var render = new RenderConfig();

        Assert.AreEqual(RenderResampling.NoHalo, render.Resampling);
        Assert.AreEqual("nohalo", RenderResampling.ToVipsInterpolatorName(render.Resampling));
        Assert.AreEqual(4, RenderResampling.OversampleFactor(render.Resampling));
    }

    [TestMethod]
    public void RenderResampling_NormalizesLegacyLanczosAlias()
    {
        Assert.AreEqual(RenderResampling.NoHalo, RenderResampling.Normalize("lanczos3"));
        Assert.IsTrue(RenderResampling.IsSupported("lanczos3"));
        Assert.AreEqual("nohalo", RenderResampling.ToVipsInterpolatorName("lanczos3"));
    }

    [TestMethod]
    public async Task TileImageRenderer_RenderAsync_ProducesPngBytesWithoutWritingFile()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        WriteRgbSource(source, 1, 1);
        var outputTilePath = Path.Combine(temp.Path, "0", "0", "0.png");
        var request = FullWorldTileRequest(source, skipEmptyTiles: false);

        var rendered = await new NetVipsTileRenderer(loadSourceIntoMemory: true).RenderAsync(request, CancellationToken.None);

        Assert.IsNotNull(rendered);
        Assert.AreEqual("png", rendered.Format);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71 }, rendered.Bytes.Take(4).ToArray());
        Assert.IsFalse(File.Exists(outputTilePath));
    }

    [TestMethod]
    public async Task TileImageRenderer_RenderAsync_ReturnsNullForSkippedEmptyTile()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "transparent.png");
        WriteRgbaSource(source, 1, 1, [255, 0, 0, 0]);
        var request = FullWorldTileRequest(source, skipEmptyTiles: true);

        var rendered = await new NetVipsTileRenderer().RenderAsync(request, CancellationToken.None);

        Assert.IsNull(rendered);
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

    private static QTilesProject MultiSourceFullWorldProject(string firstSource, string secondSource, string output) => new()
    {
        Project = new ProjectInfo { Name = "Multi-source full world test" },
        Sources =
        [
            new ProjectSourceConfig
            {
                Id = "red",
                Image = firstSource,
                Georeference = FullWorldGeoreference(1)
            },
            new ProjectSourceConfig
            {
                Id = "blue",
                Image = secondSource,
                Georeference = FullWorldGeoreference(1)
            }
        ],
        Output = new OutputConfig { Directory = output, TileJson = true, TileJsonPath = Path.Combine(output, "tilejson.json") },
        Render = new RenderConfig { AutoZoom = false, TileSize = 1, MinZoom = 0, MaxZoom = 0, Format = "png", Resampling = "nearest", SkipEmptyTiles = false }
    };

    private static GeoreferenceConfig FullWorldGeoreference(int imageSize) => new()
    {
        ControlPoints =
        [
            NormalizedPoint("1", 0, 0, 0, 0),
            NormalizedPoint("2", imageSize, 0, 1, 0),
            NormalizedPoint("3", 0, imageSize, 0, 1)
        ]
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

    private static RenderedTileRequest FullWorldTileRequest(string source, bool skipEmptyTiles) => new(
        source,
        new AffineTransform(1, 0, 0, 0, 1, 0),
        new TileCoord(0, 0, 0),
        1,
        new TileRenderOptions(
            "png",
            90,
            Overwrite: true,
            skipEmptyTiles,
            RenderResampling.Nearest,
            "transparent"));

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

[TestClass]
public sealed class PreviewTileServiceTests
{
    private const double WebMercatorWorldWidth = 40075016.68557849;

    [TestMethod]
    public void GetVisibleTiles_ReturnsCurrentViewportTileCoordinates()
    {
        var service = new PreviewTileService(new RecordingTileImageRenderer());
        var resolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 1));
        var viewport = new PreviewTileViewport(0, 0, 256, 256, resolution);

        var tiles = service.GetVisibleTiles(viewport, minZoom: 0, maxZoom: 4, tileSize: 256);

        Assert.AreEqual(4, tiles.Count);
        Assert.IsTrue(tiles.Contains(new TileCoord(1, 0, 0)));
        Assert.IsTrue(tiles.Contains(new TileCoord(1, 0, 1)));
        Assert.IsTrue(tiles.Contains(new TileCoord(1, 1, 0)));
        Assert.IsTrue(tiles.Contains(new TileCoord(1, 1, 1)));
    }

    [TestMethod]
    public void GetVisibleTiles_ClipsToContentBounds()
    {
        var service = new PreviewTileService(new RecordingTileImageRenderer());
        var resolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 2));
        var viewport = new PreviewTileViewport(0, 0, 1024, 1024, resolution);
        var contentBounds = new GeoBounds(1, 1, 44, 44);

        var tiles = service.GetVisibleTiles(viewport, minZoom: 0, maxZoom: 4, tileSize: 256, limitToZoomRange: true, contentBounds);

        Assert.IsTrue(tiles.Count > 0);
        Assert.IsTrue(tiles.All(tile => tile is { X: 2, Y: 1 }));
    }

    [TestMethod]
    public void GetVisibleTiles_ElongatedRangeOverCap_FillsUpToCapAcrossFullWidth()
    {
        var service = new PreviewTileService(new RecordingTileImageRenderer());
        var zoom = 11;
        var resolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, zoom));
        var viewport = new PreviewTileViewport(0, 0, 256 * Math.Pow(2.0, zoom), 512, resolution);
        var thinFullWidthBounds = new GeoBounds(-179.999, -0.01, 179.999, 0.01);

        var tiles = service.GetVisibleTiles(viewport, zoom, zoom, tileSize: 256, limitToZoomRange: true, thinFullWidthBounds);

        Assert.AreEqual(PreviewTileService.MaxPreviewTiles, tiles.Count);
        Assert.AreEqual(tiles.Count, tiles.Distinct().Count());
        // The old square-window logic stopped ~33 columns from the center; the ring
        // expansion must spread across far more columns for a 2-row strip.
        Assert.IsTrue(tiles.Select(tile => tile.X).Distinct().Count() > 100);
    }

    [TestMethod]
    public void GetVisibleTiles_ReturnsNoTilesOutsideConfiguredZoomRange()
    {
        var service = new PreviewTileService(new RecordingTileImageRenderer());
        var belowMinResolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 1));
        var aboveMaxResolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 5));

        var belowMinTiles = service.GetVisibleTiles(
            new PreviewTileViewport(0, 0, 256, 256, belowMinResolution),
            minZoom: 2,
            maxZoom: 4,
            tileSize: 256);
        var aboveMaxTiles = service.GetVisibleTiles(
            new PreviewTileViewport(0, 0, 256, 256, aboveMaxResolution),
            minZoom: 2,
            maxZoom: 4,
            tileSize: 256);

        Assert.AreEqual(0, belowMinTiles.Count);
        Assert.AreEqual(0, aboveMaxTiles.Count);
    }

    [TestMethod]
    public void GetVisibleTiles_ClampsToConfiguredZoomRangeWhenLimitDisabled()
    {
        var service = new PreviewTileService(new RecordingTileImageRenderer());
        var belowMinResolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 1));
        var aboveMaxResolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 5));

        var belowMinTiles = service.GetVisibleTiles(
            new PreviewTileViewport(0, 0, 256, 256, belowMinResolution),
            minZoom: 2,
            maxZoom: 4,
            tileSize: 256,
            limitToZoomRange: false);
        var aboveMaxTiles = service.GetVisibleTiles(
            new PreviewTileViewport(0, 0, 256, 256, aboveMaxResolution),
            minZoom: 2,
            maxZoom: 4,
            tileSize: 256,
            limitToZoomRange: false);

        Assert.IsTrue(belowMinTiles.Count > 0);
        Assert.IsTrue(aboveMaxTiles.Count > 0);
        Assert.IsTrue(belowMinTiles.All(tile => tile.Z == 2));
        Assert.IsTrue(aboveMaxTiles.All(tile => tile.Z == 4));
    }

    [TestMethod]
    public void GetVisibleTiles_ReturnsNoTilesForInvalidZoomRange()
    {
        var service = new PreviewTileService(new RecordingTileImageRenderer());
        var resolution = WebMercatorWorldWidth / (256 * Math.Pow(2.0, 3));
        var viewport = new PreviewTileViewport(0, 0, 256, 256, resolution);

        var tiles = service.GetVisibleTiles(viewport, minZoom: 4, maxZoom: 2, tileSize: 256);

        Assert.AreEqual(0, tiles.Count);
    }

    [TestMethod]
    public void CreateWorkItem_UsesCurrentSourceAndPreviewOptionsWithoutOutputDirectory()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "source.png");
        var staleTile = Path.Combine(temp.Path, "tiles", "0", "0", "0.png");
        Directory.CreateDirectory(Path.GetDirectoryName(staleTile)!);
        File.WriteAllText(source, "source");
        File.WriteAllText(staleTile, "stale");
        var solve = IdentitySolveResult();
        var service = new PreviewTileService(new RecordingTileImageRenderer());

        var workItem = service.CreateWorkItem(
            source,
            File.GetLastWriteTimeUtc(source).Ticks,
            solve,
            new TileCoord(0, 0, 0),
            256);

        Assert.AreEqual(source, workItem.RenderRequest.SourceImagePath);
        Assert.AreEqual("png", workItem.RenderRequest.Options.Format);
        Assert.AreEqual(RenderResampling.Bilinear, workItem.RenderRequest.Options.Resampling);
        Assert.IsTrue(workItem.RenderRequest.Options.SkipEmptyTiles);
        Assert.AreEqual(256, workItem.RenderRequest.TileSize);
    }

    [TestMethod]
    public void CreateWorkItem_ProjectRenderPlan_UsesAllContributingSources()
    {
        using var temp = new TempFolder();
        var first = Path.Combine(temp.Path, "first.png");
        var second = Path.Combine(temp.Path, "second.png");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var solve = IdentitySolveResult();
        var plan = new ProjectRenderPlan(
            [
                new RenderSourceContext(
                    new ProjectSourceConfig { Id = "first", Image = first },
                    first,
                    new ImageInfo(1, 1),
                    solve,
                    new AffineTransform(1, 0, 0, 0, 1, 0)),
                new RenderSourceContext(
                    new ProjectSourceConfig { Id = "second", Image = second, Opacity = 0.5 },
                    second,
                    new ImageInfo(1, 1),
                    solve,
                    new AffineTransform(1, 0, 0, 0, 1, 0))
            ],
            new ZoomRangeRecommendation(0, 0),
            solve.Bounds,
            0,
            0);
        var service = new PreviewTileService(new RecordingTileImageRenderer());

        var workItem = service.CreateWorkItem(plan, new TileCoord(0, 0, 0), 256);

        Assert.AreEqual("merged", workItem.Key.SourceImagePath);
        Assert.AreEqual(2, workItem.RenderRequest.Sources.Count);
        Assert.AreEqual(first, workItem.RenderRequest.Sources[0].SourceImagePath);
        Assert.AreEqual(second, workItem.RenderRequest.Sources[1].SourceImagePath);
        Assert.AreEqual(0.5, workItem.RenderRequest.Sources[1].Opacity, 0.000001);

        var changedSources = plan.Sources.ToList();
        changedSources[1] = changedSources[1] with
        {
            Source = new ProjectSourceConfig { Id = "second", Image = second, Opacity = 0.75 }
        };
        var changedWorkItem = service.CreateWorkItem(plan with { Sources = changedSources }, new TileCoord(0, 0, 0), 256);
        Assert.AreNotEqual(workItem.Key, changedWorkItem.Key);
    }


    [TestMethod]
    public async Task RenderAsync_CancelledBeforeDispatch_DoesNotInvokeRenderer()
    {
        var renderer = new RecordingTileImageRenderer();
        var service = new PreviewTileService(renderer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var workItem = service.CreateWorkItem("source.png", 1, IdentitySolveResult(), new TileCoord(0, 0, 0), 256);

        try
        {
            await service.RenderAsync(workItem, cts.Token);
            Assert.Fail("Expected preview rendering to observe the cancelled token.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(0, renderer.Requests.Count);
    }

    private static TransformSolveResult IdentitySolveResult() => new(
        "affine",
        new AffineTransform(1, 0, 0, 0, 1, 0),
        [],
        0,
        0,
        new GeoBounds(-180, -85, 180, 85));

    private sealed class RecordingTileImageRenderer : ITileImageRenderer
    {
        public List<RenderedTileRequest> Requests { get; } = [];

        public Task<RenderedTileImage?> RenderAsync(RenderedTileRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<RenderedTileImage?>(new RenderedTileImage([1, 2, 3, 4], "png"));
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qtiles-preview-{Guid.NewGuid():N}");
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

internal static class TestAssert
{
    public static void InRange(double actual, double minimum, double maximum)
    {
        Assert.IsTrue(
            actual >= minimum && actual <= maximum,
            $"Expected {actual} to be in range [{minimum}, {maximum}].");
    }
}
