using System.IO;
using System.Text.Json;

namespace QTiles.Services;

public sealed class EditorSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string settingsPath;

    public EditorSettingsService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QTiles",
            "editor-settings.json");
    }

    public EditorSettings Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new EditorSettings();
            }

            var json = File.ReadAllText(settingsPath);
            return Normalize(JsonSerializer.Deserialize<EditorSettings>(json, JsonOptions));
        }
        catch
        {
            return new EditorSettings();
        }
    }

    public void Save(EditorSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // Settings persistence should never prevent the editor from closing.
        }
    }

    private static EditorSettings Normalize(EditorSettings? settings)
    {
        settings ??= new EditorSettings();
        settings.Window ??= new EditorWindowSettings();
        settings.LastMapView ??= new EditorMapViewSettings();
        return settings;
    }
}

public sealed class EditorSettings
{
    public int Version { get; set; } = 1;
    public EditorWindowSettings Window { get; set; } = new();
    public EditorMapViewSettings LastMapView { get; set; } = new();
}

public sealed class EditorWindowSettings
{
    public bool HasBounds { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string State { get; set; } = "Normal";
}

public sealed class EditorMapViewSettings
{
    public bool HasValue { get; set; }
    public double Longitude { get; set; }
    public double Latitude { get; set; }
    public double Zoom { get; set; }
}
