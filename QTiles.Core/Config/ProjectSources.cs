namespace QTiles.Core.Config;

public static class ProjectSources
{
    public const string LegacySourceId = "source-1";

    public static IReadOnlyList<ProjectSourceConfig> GetEffectiveSources(QTilesProject project)
    {
        if (project.Sources is { Count: > 0 })
        {
            return project.Sources;
        }

        return
        [
            new ProjectSourceConfig
            {
                Id = LegacySourceId,
                Name = string.IsNullOrWhiteSpace(project.Project.Name) ? null : project.Project.Name,
                Image = project.Source.Image,
                Enabled = true,
                Origin = project.Source.Origin,
                Units = project.Source.Units,
                Georeference = project.Georeference
            }
        ];
    }

    public static IReadOnlyList<ProjectSourceConfig> GetEnabledSources(QTilesProject project) =>
        GetEffectiveSources(project)
            .Where(source => source.Enabled)
            .ToList();

    public static bool HasExplicitSources(QTilesProject project) => project.Sources is { Count: > 0 };

    public static string DisplayName(ProjectSourceConfig source)
    {
        if (!string.IsNullOrWhiteSpace(source.Name))
        {
            return source.Name;
        }

        if (!string.IsNullOrWhiteSpace(source.Image))
        {
            return Path.GetFileNameWithoutExtension(source.Image);
        }

        return string.IsNullOrWhiteSpace(source.Id) ? "source" : source.Id;
    }

    public static ProjectSourceConfig CreateLegacySource(QTilesProject project) => new()
    {
        Id = LegacySourceId,
        Name = string.IsNullOrWhiteSpace(project.Project.Name) ? null : project.Project.Name,
        Image = project.Source.Image,
        Enabled = true,
        Origin = project.Source.Origin,
        Units = project.Source.Units,
        Georeference = project.Georeference
    };
}
