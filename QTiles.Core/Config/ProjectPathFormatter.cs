namespace QTiles.Core.Config;

public static class ProjectPathFormatter
{
    public static string FormatForProject(string? rootDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootDirectory))
        {
            return path;
        }

        try
        {
            var root = Path.GetFullPath(rootDirectory);
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, fullPath);
            if (!IsRelativeSubpath(relative))
            {
                return fullPath;
            }

            return NormalizeRelativeProjectPath(relative);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsRelativeSubpath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized != ".."
            && !normalized.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizeRelativeProjectPath(string path)
    {
        if (path == ".")
        {
            return ".";
        }

        var normalized = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized : $"./{normalized}";
    }
}
