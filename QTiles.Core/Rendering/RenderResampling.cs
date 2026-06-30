namespace QTiles.Core.Rendering;

public static class RenderResampling
{
    public const int HighQualityOversampleFactor = 4;
    public const string Nearest = "nearest";
    public const string Bilinear = "bilinear";
    public const string Bicubic = "bicubic";
    public const string LocallyBoundedBicubic = "lbb";
    public const string NoHalo = "nohalo";
    public const string VertexSplitQuadraticBspline = "vsqbs";
    public const string Default = NoHalo;

    public static IReadOnlyList<string> Values { get; } =
    [
        Nearest,
        Bilinear,
        Bicubic,
        LocallyBoundedBicubic,
        NoHalo,
        VertexSplitQuadraticBspline
    ];

    public static string Normalize(string? resampling)
    {
        var value = (resampling ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "" => Default,
            "nearest-neighbor" or "nearest-neighbour" => Nearest,
            "linear" => Bilinear,
            "cubic" => Bicubic,
            "lanczos2" or "lanczos-2" => LocallyBoundedBicubic,
            "lanczos" or "lanczos3" or "lanczos-3" or "best" or "high" or "high-quality" => NoHalo,
            "no-halo" => NoHalo,
            _ => value
        };
    }

    public static bool IsSupported(string? resampling) => Values.Contains(Normalize(resampling));

    public static string ToVipsInterpolatorName(string? resampling)
    {
        var normalized = Normalize(resampling);
        return Values.Contains(normalized) ? normalized : Default;
    }

    public static int OversampleFactor(string? resampling) =>
        Normalize(resampling) == NoHalo ? HighQualityOversampleFactor : 1;

    public static string DisplayName(string? resampling) => Normalize(resampling) switch
    {
        Nearest => "Nearest",
        Bilinear => "Bilinear",
        Bicubic => "Bicubic",
        LocallyBoundedBicubic => "Sharp",
        NoHalo => "Maximum quality",
        VertexSplitQuadraticBspline => "Very smooth",
        _ => "Maximum quality"
    };
}
