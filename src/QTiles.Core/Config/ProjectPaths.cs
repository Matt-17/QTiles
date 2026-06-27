namespace QTiles.Core.Config;

public static class ProjectPaths
{
    public static string Resolve(QTilesProject project, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(project.BaseDirectory ?? Environment.CurrentDirectory, path));
    }
}
