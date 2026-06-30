using NetVips;
using QTiles.Core.Geo;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;

namespace QTiles.Core.Imaging;

public sealed record ImageInfo(int Width, int Height);

public interface IImageInfoReader
{
    ImageInfo Read(string path);
}

public sealed class NetVipsImageInfoReader : IImageInfoReader
{
    public ImageInfo Read(string path)
    {
        using var image = Image.NewFromFile(path, access: Enums.Access.Sequential);
        return new ImageInfo(image.Width, image.Height);
    }
}

public sealed record TileRenderOptions(
    string Format,
    int Quality,
    bool Overwrite,
    bool SkipEmptyTiles,
    string Resampling,
    string Background);

public sealed record RenderedTileRequest(
    string SourceImagePath,
    AffineTransform Transform,
    TileCoord Tile,
    int TileSize,
    TileRenderOptions Options);

public sealed record RenderedTileImage(byte[] Bytes, string Format);

public interface ITileImageRenderer
{
    Task<RenderedTileImage?> RenderAsync(RenderedTileRequest request, CancellationToken cancellationToken);
}

public interface IRenderedTileWriter
{
    Task<bool> WriteAsync(string path, RenderedTileRequest request, CancellationToken cancellationToken);
}

public sealed class NetVipsTileRenderer : IRenderedTileWriter, ITileImageRenderer
{
    private readonly bool loadSourceIntoMemory;

    public NetVipsTileRenderer(bool loadSourceIntoMemory = false)
    {
        this.loadSourceIntoMemory = loadSourceIntoMemory;
    }

    public async Task<bool> WriteAsync(string path, RenderedTileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && !request.Options.Overwrite)
        {
            return false;
        }

        var tile = await RenderAsync(request, cancellationToken);
        if (tile is null)
        {
            return false;
        }

        await File.WriteAllBytesAsync(path, tile.Bytes, cancellationToken);
        return true;
    }

    public async Task<RenderedTileImage?> RenderAsync(RenderedTileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var source = await LoadSourceAsync(request.SourceImagePath, cancellationToken);
        using var renderSource = EnsureAlpha(source);
        using var tile = RenderTile(renderSource, request);
        if (request.Options.SkipEmptyTiles && HasTransparentAlpha(tile))
        {
            return null;
        }

        var format = NormalizeFormat(request.Options.Format);
        var bytes = Encode(tile, format, request.Options);
        return new RenderedTileImage(bytes, format);
    }

    private async Task<Image> LoadSourceAsync(string path, CancellationToken cancellationToken)
    {
        if (!loadSourceIntoMemory)
        {
            return Image.NewFromFile(path, access: Enums.Access.Random);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Image.NewFromBuffer(bytes, access: Enums.Access.Random);
    }

    private static Image RenderTile(Image source, RenderedTileRequest request)
    {
        var oversampleFactor = RenderResampling.OversampleFactor(request.Options.Resampling);
        if (oversampleFactor <= 1)
        {
            return RenderAffineTile(source, request, 1);
        }

        using var oversized = RenderAffineTile(source, request, oversampleFactor);
        using var premultiplied = oversized.Premultiply();
        using var resized = premultiplied.Resize(1.0 / oversampleFactor, kernel: Enums.Kernel.Lanczos3);
        return resized.Unpremultiply();
    }

    private static Image RenderAffineTile(Image source, RenderedTileRequest request, int oversampleFactor)
    {
        var transform = request.Transform;
        var scale = request.TileSize * Math.Pow(2.0, request.Tile.Z);
        var outputScale = scale * oversampleFactor;
        var outputTileSize = request.TileSize * oversampleFactor;
        var matrix = new[]
        {
            transform.A * outputScale,
            transform.B * outputScale,
            transform.D * outputScale,
            transform.E * outputScale
        };
        var odx = (transform.C * scale - request.Tile.X * request.TileSize) * oversampleFactor;
        var ody = (transform.F * scale - request.Tile.Y * request.TileSize) * oversampleFactor;
        var interpolator = Interpolate.NewFromName(RenderResampling.ToVipsInterpolatorName(request.Options.Resampling));

        return source.Affine(
            matrix,
            interpolate: interpolator,
            oarea: [0, 0, outputTileSize, outputTileSize],
            odx: odx,
            ody: ody,
            background: [0.0, 0.0, 0.0, 0.0],
            extend: Enums.Extend.Background);
    }

    private static Image EnsureAlpha(Image source)
    {
        var srgb = source.Bands is 3 or 4 ? source : source.Colourspace(Enums.Interpretation.Srgb);
        if (srgb.Bands == 4)
        {
            return srgb.Copy();
        }

        if (srgb.Bands == 3)
        {
            return srgb.Bandjoin([255.0]);
        }

        if (srgb.Bands > 4)
        {
            using var rgb = srgb.ExtractBand(0, n: 3);
            return rgb.Bandjoin([255.0]);
        }

        return srgb.Colourspace(Enums.Interpretation.Srgb).Bandjoin([255.0]);
    }

    private static bool HasTransparentAlpha(Image image)
    {
        if (image.Bands < 4)
        {
            return false;
        }

        using var alpha = image.ExtractBand(3);
        return alpha.Max() <= 0.0;
    }

    private static byte[] Encode(Image tile, string format, TileRenderOptions options)
    {
        if (format == "jpg")
        {
            using var flattened = tile.Flatten(background: ParseBackground(options.Background));
            return flattened.JpegsaveBuffer(q: options.Quality);
        }

        if (format == "webp")
        {
            return tile.WebpsaveBuffer(q: options.Quality);
        }

        return tile.PngsaveBuffer();
    }

    private static string NormalizeFormat(string format) =>
        format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : format.ToLowerInvariant();

    private static double[] ParseBackground(string background)
    {
        var value = background.Trim();
        if (value.Equals("black", StringComparison.OrdinalIgnoreCase))
        {
            return [0, 0, 0];
        }

        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("white", StringComparison.OrdinalIgnoreCase))
        {
            return [255, 255, 255];
        }

        if (value.StartsWith("#", StringComparison.Ordinal) &&
            value.Length == 7 &&
            int.TryParse(value[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            int.TryParse(value[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            int.TryParse(value[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return [r, g, b];
        }

        return [255, 255, 255];
    }
}
