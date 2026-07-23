using System.Diagnostics;
using System.Text.Json;
using QTiles.Core.Config;
using QTiles.Core.Imaging;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;
using QTiles.Core.Validation;

namespace QTiles.Cli.Commands;

public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "init" => await InitCommand.RunAsync(args[1..], cancellationToken),
                "validate" => await ValidateCommand.RunAsync(args[1..], cancellationToken),
                "solve" => await SolveCommand.RunAsync(args[1..], cancellationToken),
                "render" => await RenderCommand.RunAsync(args[1..], cancellationToken),
                "tilejson" => await TileJsonCommand.RunAsync(args[1..], cancellationToken),
                "editor" => EditorCommand.Run(args[1..]),
                _ => Unknown(args[0])
            };
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("qtiles init|validate|solve|render|tilejson|editor");
    }
}

internal sealed class ArgumentReader
{
    // Flags that never take a value. Without this list, "--overwrite map.yaml"
    // would swallow the project path as the flag's value.
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "verbose", "overwrite", "no-tilejson"
    };

    private readonly Queue<string> args;

    public ArgumentReader(IEnumerable<string> args) => this.args = new Queue<string>(args);

    public string? ProjectPath { get; private set; }
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Parse()
    {
        while (args.TryDequeue(out var arg))
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg[2..];
                var separatorIndex = key.IndexOf('=');
                if (separatorIndex >= 0)
                {
                    Options[key[..separatorIndex]] = key[(separatorIndex + 1)..];
                }
                else if (!BooleanFlags.Contains(key)
                    && args.TryPeek(out var next)
                    && !next.StartsWith("-", StringComparison.Ordinal))
                {
                    Options[key] = args.Dequeue();
                }
                else
                {
                    Options[key] = null;
                }
            }
            else if (ProjectPath is null)
            {
                ProjectPath = arg;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected argument: {arg}");
            }
        }
    }

    public string? Get(string name) => Options.TryGetValue(name, out var value) ? value : null;
    public bool Has(string name) => Options.ContainsKey(name);

    public int? GetInt(string name)
    {
        if (!Options.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value is null || !int.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException($"--{name} requires an integer value.");
        }

        return parsed;
    }
}

public static class InitCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        reader.Parse();
        var image = reader.Get("image") ?? throw new InvalidOperationException("--image is required.");
        if (!File.Exists(image))
        {
            throw new FileNotFoundException($"Source image does not exist: {image}", image);
        }

        var minZoom = reader.GetInt("min-zoom");
        var maxZoom = reader.GetInt("max-zoom");
        if ((minZoom is null) != (maxZoom is null))
        {
            throw new InvalidOperationException("--min-zoom and --max-zoom must be used together.");
        }

        var hasZoomOverride = minZoom is not null;
        var project = new QTilesProject
        {
            Project = new ProjectInfo { Name = reader.Get("name") ?? Path.GetFileNameWithoutExtension(image) },
            Source = new SourceConfig { Image = image },
            Render = new RenderConfig
            {
                AutoZoom = !hasZoomOverride,
                MinZoom = minZoom ?? 0,
                MaxZoom = maxZoom ?? 0,
                Format = reader.Get("format") ?? "png"
            }
        };

        await new QTilesYamlSerializer().WriteAsync(project, reader.Get("out") ?? "qtiles.yaml", cancellationToken);
        return 0;
    }
}

public static class ValidateCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var (project, path, json) = await LoadAsync(args, cancellationToken);
        var messages = new ProjectValidator().Validate(project, path);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Severity}: {message.Message}");
            }
        }

        return ProjectValidator.HasErrors(messages) ? 1 : 0;
    }

    internal static async Task<(QTilesProject Project, string Path, bool Json)> LoadAsync(string[] args, CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        reader.Parse();
        var path = reader.ProjectPath ?? throw new InvalidOperationException("Project YAML path is required.");
        var project = await new QTilesYamlSerializer().ReadAsync(path, cancellationToken);
        WarnIfZoomClamped(project);
        return (project, path, reader.Has("json"));
    }

    internal static void WarnIfZoomClamped(QTilesProject project)
    {
        if (ZoomRangeCalculator.ClampToSupportedRange(project.Render))
        {
            Console.Error.WriteLine($"Warning: zoom range was limited to 0..{ZoomRangeCalculator.MaxSupportedZoom}.");
        }
    }
}

public static class SolveCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var (project, _, json) = await ValidateCommand.LoadAsync(args, cancellationToken);
        var plan = new ProjectRenderPlanner().CreatePlan(project);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Sources: {plan.Sources.Count}");
            foreach (var source in plan.Sources)
            {
                Console.WriteLine($"Source {source.Source.Id}: {ProjectSources.DisplayName(source.Source)}");
                Console.WriteLine($"  Transform: {source.SolveResult.TransformType}");
                Console.WriteLine($"  Enabled points: {source.Source.Georeference.ControlPoints.Count(p => p.Enabled)}");
                Console.WriteLine($"  RMS error: {source.SolveResult.RmsPixelsAtMaxZoom:0.###} px at max zoom {plan.ZoomRange.MaxZoom}");
                Console.WriteLine($"  Max error: {source.SolveResult.MaxPixelsAtMaxZoom:0.###} px");
                Console.WriteLine($"  Bounds: [{source.SolveResult.Bounds.West:0.######}, {source.SolveResult.Bounds.South:0.######}, {source.SolveResult.Bounds.East:0.######}, {source.SolveResult.Bounds.North:0.######}]");
            }

            Console.WriteLine($"Project bounds: [{plan.Bounds.West:0.######}, {plan.Bounds.South:0.######}, {plan.Bounds.East:0.######}, {plan.Bounds.North:0.######}]");
            Console.WriteLine($"Recommended zoom range: {plan.ZoomRange.MinZoom}..{plan.ZoomRange.MaxZoom}");
        }

        return 0;
    }
}

public static class RenderCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        reader.Parse();
        var path = reader.ProjectPath ?? throw new InvalidOperationException("Project YAML path is required.");
        var project = await new QTilesYamlSerializer().ReadAsync(path, cancellationToken);
        ValidateCommand.WarnIfZoomClamped(project);
        ApplyOverrides(project, reader);
        var summary = await new TileRenderer().RenderAsync(project, new Progress<TileRenderProgress>(p =>
        {
            if (reader.Has("verbose") && p.CurrentPath is not null)
            {
                var images = p.CurrentImages is { Count: > 0 }
                    ? $" [{string.Join(", ", p.CurrentImages)}]"
                    : string.Empty;
                Console.WriteLine($"{p.Percent:0.0}% {p.CurrentPath}{images}");
            }
        }), cancellationToken);

        if (reader.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Tiles written: {summary.TilesWritten}");
            Console.WriteLine($"Tiles skipped: {summary.TilesSkipped}");
        }

        return 0;
    }

    private static void ApplyOverrides(QTilesProject project, ArgumentReader reader)
    {
        if (reader.Get("out") is { } output)
        {
            project.Output.Directory = output;
            project.Output.TileJsonPath = "";
        }

        var minZoom = reader.GetInt("min-zoom");
        var maxZoom = reader.GetInt("max-zoom");
        if (minZoom is not null || maxZoom is not null)
        {
            // On an auto-zoom project the stored MinZoom/MaxZoom are meaningless (0/0):
            // --max-zoom alone renders 0..N, but --min-zoom alone would leave MaxZoom at 0.
            if (ZoomRangeCalculator.UsesAutoZoom(project.Render) && minZoom is not null && maxZoom is null)
            {
                throw new InvalidOperationException("Overriding --min-zoom on an auto-zoom project also requires --max-zoom.");
            }

            project.Render.MinZoom = minZoom ?? (ZoomRangeCalculator.UsesAutoZoom(project.Render) ? 0 : project.Render.MinZoom);
            project.Render.MaxZoom = maxZoom ?? project.Render.MaxZoom;
            project.Render.AutoZoom = false;
        }

        if (reader.Get("format") is { } format) project.Render.Format = format;
        if (reader.GetInt("quality") is { } quality) project.Render.Quality = quality;
        if (reader.Get("resampling") is { } resampling) project.Render.Resampling = resampling;
        if (reader.Has("overwrite")) project.Render.Overwrite = true;
        if (reader.Has("no-tilejson")) project.Output.TileJson = false;
    }
}

public static class TileJsonCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var reader = new ArgumentReader(args);
        reader.Parse();
        var path = reader.ProjectPath ?? throw new InvalidOperationException("Project YAML path is required.");
        var project = await new QTilesYamlSerializer().ReadAsync(path, cancellationToken);
        ValidateCommand.WarnIfZoomClamped(project);
        var plan = new ProjectRenderPlanner().CreatePlan(project);
        var summary = new RenderSummary(0, 0, plan.ZoomRange.MinZoom, plan.ZoomRange.MaxZoom, plan.Bounds, TimeSpan.Zero);
        var tileJsonPath = TileJsonWriter.ResolvePath(project);
        await TileJsonWriter.WriteAsync(project, plan, summary, tileJsonPath, cancellationToken);
        Console.WriteLine(tileJsonPath);
        return 0;
    }
}

public static class EditorCommand
{
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The WPF editor is only available on Windows.");
            return 1;
        }

        var projectPath = args.FirstOrDefault() ?? "";
        var editorDll = Path.Combine(AppContext.BaseDirectory, "QTiles.exe");
        if (!File.Exists(editorDll))
        {
            Console.Error.WriteLine("QTiles is not available next to the CLI.");
            return 2;
        }

        var startInfo = new ProcessStartInfo(editorDll) { UseShellExecute = true };
        if (projectPath.Length > 0)
        {
            startInfo.ArgumentList.Add(projectPath);
        }

        Process.Start(startInfo);
        return 0;
    }
}
