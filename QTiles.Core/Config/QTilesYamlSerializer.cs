using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace QTiles.Core.Config;

public sealed class QTilesYamlSerializer
{
    private readonly ISerializer serializer;
    private readonly IDeserializer deserializer;

    public QTilesYamlSerializer()
    {
        serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithTypeConverter(new ControlPointIdYamlConverter())
            .Build();

        deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .WithTypeConverter(new ControlPointIdYamlConverter())
            .Build();
    }

    public async Task<QTilesProject> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        var yaml = await reader.ReadToEndAsync(cancellationToken);
        var project = Deserialize(yaml);
        project.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        return project;
    }

    public async Task WriteAsync(QTilesProject project, string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write via temp file + rename so a crash mid-write cannot destroy the
        // user's project file.
        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, Serialize(project), cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public string Serialize(QTilesProject project) => serializer.Serialize(project);

    public QTilesProject Deserialize(string yaml)
    {
        var project = deserializer.Deserialize<QTilesProject>(yaml)
            ?? throw new InvalidDataException("YAML did not contain a QTiles project.");
        return project;
    }

    private sealed class ControlPointIdYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ControlPointId);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = parser.Consume<Scalar>();
            return new ControlPointId(scalar.Value);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var id = ((ControlPointId?)value)?.Value ?? "";
            if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                emitter.Emit(new Scalar(null, null, id, ScalarStyle.Plain, true, false));
                return;
            }

            emitter.Emit(new Scalar(id));
        }
    }
}
