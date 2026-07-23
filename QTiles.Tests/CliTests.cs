using QTiles.Cli.Commands;
using QTiles.Core.Config;
using QTiles.Core.Geo;
using NetVips;

namespace QTiles.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    public async Task Cli_Init_CreatesYaml()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        await File.WriteAllTextAsync(image, "placeholder");

        var exitCode = await CliApplication.RunAsync(["init", "--image", image, "--out", yaml, "--name", "CLI test"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(yaml));
        StringAssert.Contains(await File.ReadAllTextAsync(yaml), "CLI test");
    }

    [TestMethod]
    public async Task Cli_Validate_ReturnsNonZeroForInvalidProject()
    {
        using var temp = new TempFolder();
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        await new QTilesYamlSerializer().WriteAsync(new QTilesProject(), yaml);

        var exitCode = await CliApplication.RunAsync(["validate", yaml, "--json"], CancellationToken.None);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Cli_Solve_PrintsRms()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        await CreateImageAsync(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        await new QTilesYamlSerializer().WriteAsync(SolvableProject(image), yaml);

        var exitCode = await CliApplication.RunAsync(["solve", yaml], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Cli_Render_WritesTiles()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        WriteRgbSource(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        var project = SolvableProject(image);
        project.Render.TileSize = 2;
        project.Render.AutoZoom = false;
        project.Render.MinZoom = 0;
        project.Render.MaxZoom = 0;
        project.Render.Resampling = "nearest";
        project.Output.Directory = Path.Combine(temp.Path, "tiles");
        project.Output.TileJsonPath = Path.Combine(temp.Path, "tiles", "tilejson.json");
        await new QTilesYamlSerializer().WriteAsync(project, yaml);

        var exitCode = await CliApplication.RunAsync(["render", yaml], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(Path.Combine(project.Output.Directory, "0", "0", "0.png")));
        Assert.IsTrue(File.Exists(project.Output.TileJsonPath));
    }

    [TestMethod]
    public async Task Cli_Render_MultipleSources_WritesTiles()
    {
        using var temp = new TempFolder();
        var first = Path.Combine(temp.Path, "first.png");
        var second = Path.Combine(temp.Path, "second.png");
        WriteRgbSource(first);
        WriteRgbSource(second);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        var project = SolvableMultiSourceProject(first, second);
        project.Render.TileSize = 2;
        project.Render.AutoZoom = false;
        project.Render.MinZoom = 0;
        project.Render.MaxZoom = 0;
        project.Render.Resampling = "nearest";
        project.Output.Directory = Path.Combine(temp.Path, "tiles");
        project.Output.TileJsonPath = Path.Combine(temp.Path, "tiles", "tilejson.json");
        await new QTilesYamlSerializer().WriteAsync(project, yaml);

        var exitCode = await CliApplication.RunAsync(["render", yaml], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(Path.Combine(project.Output.Directory, "0", "0", "0.png")));
        Assert.IsTrue(File.Exists(project.Output.TileJsonPath));
    }

    [TestMethod]
    public async Task Cli_Validate_BooleanFlagBeforePath_DoesNotSwallowPath()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        await CreateImageAsync(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        await new QTilesYamlSerializer().WriteAsync(SolvableProject(image), yaml);

        var exitCode = await CliApplication.RunAsync(["validate", "--json", yaml], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Cli_Render_OverwriteFlagBeforePath_DoesNotSwallowPath()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        WriteRgbSource(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        var project = SolvableProject(image);
        project.Render.TileSize = 2;
        project.Render.AutoZoom = false;
        project.Render.MinZoom = 0;
        project.Render.MaxZoom = 0;
        project.Render.Resampling = "nearest";
        project.Output.Directory = Path.Combine(temp.Path, "tiles");
        project.Output.TileJsonPath = Path.Combine(temp.Path, "tiles", "tilejson.json");
        await new QTilesYamlSerializer().WriteAsync(project, yaml);

        var exitCode = await CliApplication.RunAsync(["render", "--overwrite", yaml], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(Path.Combine(project.Output.Directory, "0", "0", "0.png")));
    }

    [TestMethod]
    public async Task Cli_Validate_LegacyZoom24Project_IsClampedInsteadOfInvalid()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        await CreateImageAsync(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        var project = SolvableProject(image);
        project.Render.AutoZoom = false;
        project.Render.MinZoom = 0;
        project.Render.MaxZoom = 24;
        await new QTilesYamlSerializer().WriteAsync(project, yaml);

        var exitCode = await CliApplication.RunAsync(["validate", yaml], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Cli_Render_MaxZoomAloneOnAutoZoomProject_Works()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        WriteRgbSource(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");
        var project = SolvableProject(image);
        project.Render.TileSize = 2;
        project.Render.Resampling = "nearest";
        project.Output.Directory = Path.Combine(temp.Path, "tiles");
        project.Output.TileJsonPath = Path.Combine(temp.Path, "tiles", "tilejson.json");
        await new QTilesYamlSerializer().WriteAsync(project, yaml);

        var exitCode = await CliApplication.RunAsync(["render", yaml, "--max-zoom", "0"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(Path.Combine(project.Output.Directory, "0", "0", "0.png")));
    }

    [TestMethod]
    public async Task Cli_Init_MinZoomWithoutMaxZoom_Fails()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        await CreateImageAsync(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");

        var exitCode = await CliApplication.RunAsync(["init", "--image", image, "--out", yaml, "--min-zoom", "3"], CancellationToken.None);

        Assert.AreEqual(3, exitCode);
        Assert.IsFalse(File.Exists(yaml));
    }

    [TestMethod]
    public async Task Cli_Init_BothZoomFlags_WritesFixedZoomRange()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        await CreateImageAsync(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");

        var exitCode = await CliApplication.RunAsync(["init", "--image", image, "--out", yaml, "--min-zoom", "3", "--max-zoom", "5"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        var content = await File.ReadAllTextAsync(yaml);
        StringAssert.Contains(content, "minZoom: 3");
        StringAssert.Contains(content, "maxZoom: 5");
    }

    [TestMethod]
    public async Task Cli_Init_NonNumericZoom_Fails()
    {
        using var temp = new TempFolder();
        var image = Path.Combine(temp.Path, "sample.png");
        await CreateImageAsync(image);
        var yaml = Path.Combine(temp.Path, "qtiles.yaml");

        var exitCode = await CliApplication.RunAsync(["init", "--image", image, "--out", yaml, "--min-zoom", "abc", "--max-zoom", "5"], CancellationToken.None);

        Assert.AreEqual(3, exitCode);
        Assert.IsFalse(File.Exists(yaml));
    }

    private static QTilesProject SolvableProject(string image) => new()
    {
        Source = new SourceConfig { Image = image },
        Georeference = new GeoreferenceConfig
        {
            ControlPoints =
            [
                NormalizedPoint("1", 0, 0, 0.49, 0.49),
                NormalizedPoint("2", 2, 0, 0.51, 0.49),
                NormalizedPoint("3", 0, 2, 0.49, 0.51)
            ]
        }
    };

    private static QTilesProject SolvableMultiSourceProject(string first, string second) => new()
    {
        Sources =
        [
            new ProjectSourceConfig
            {
                Id = "first",
                Image = first,
                Georeference = SolvableGeoreference()
            },
            new ProjectSourceConfig
            {
                Id = "second",
                Image = second,
                Georeference = SolvableGeoreference()
            }
        ]
    };

    private static GeoreferenceConfig SolvableGeoreference() => new()
    {
        ControlPoints =
        [
            NormalizedPoint("1", 0, 0, 0.49, 0.49),
            NormalizedPoint("2", 2, 0, 0.51, 0.49),
            NormalizedPoint("3", 0, 2, 0.49, 0.51)
        ]
    };

    private static Task CreateImageAsync(string path) => File.WriteAllBytesAsync(path, Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));

    private static ControlPointConfig NormalizedPoint(string id, double x, double y, double u, double v)
    {
        var lonLat = WebMercator.NormalizedToLonLat(u, v);
        return new ControlPointConfig
        {
            Id = new(id),
            Image = new() { X = x, Y = y },
            World = new() { Lon = lonLat.Lon, Lat = lonLat.Lat }
        };
    }

    private static void WriteRgbSource(string path)
    {
        byte[] data =
        [
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255
        ];
        using var image = Image.NewFromMemory(data, 2, 2, 3, Enums.BandFormat.Uchar);
        image.Pngsave(path);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qtiles-cli-{Guid.NewGuid():N}");
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
