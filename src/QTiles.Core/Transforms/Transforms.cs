using MathNet.Numerics.LinearAlgebra;
using QTiles.Core.Config;
using QTiles.Core.Geo;

namespace QTiles.Core.Transforms;

public interface IGeoTransform
{
    NormalizedMercatorPoint ImageToWorld(ImagePoint p);
    ImagePoint WorldToImage(NormalizedMercatorPoint p);
}

public sealed record AffineTransform(
    double A,
    double B,
    double C,
    double D,
    double E,
    double F) : IGeoTransform
{
    public NormalizedMercatorPoint ImageToWorld(ImagePoint p) => new(A * p.X + B * p.Y + C, D * p.X + E * p.Y + F);

    public ImagePoint WorldToImage(NormalizedMercatorPoint p)
    {
        var determinant = A * E - B * D;
        if (Math.Abs(determinant) < 1e-18)
        {
            throw new InvalidOperationException("Affine transform is singular.");
        }

        var x = (E * (p.X - C) - B * (p.Y - F)) / determinant;
        var y = (-D * (p.X - C) + A * (p.Y - F)) / determinant;
        return new ImagePoint { X = x, Y = y };
    }
}

public sealed record SimilarityTransform(
    double Scale,
    double Cos,
    double Sin,
    double Tx,
    double Ty) : IGeoTransform
{
    public NormalizedMercatorPoint ImageToWorld(ImagePoint p)
        => new(Scale * (Cos * p.X - Sin * p.Y) + Tx, Scale * (Sin * p.X + Cos * p.Y) + Ty);

    public ImagePoint WorldToImage(NormalizedMercatorPoint p)
    {
        var x = p.X - Tx;
        var y = p.Y - Ty;
        var denominator = Scale == 0 ? throw new InvalidOperationException("Similarity transform has zero scale.") : Scale;
        return new ImagePoint
        {
            X = (Cos * x + Sin * y) / denominator,
            Y = (-Sin * x + Cos * y) / denominator
        };
    }

    public AffineTransform ToAffineTransform() => new(
        Scale * Cos,
        -Scale * Sin,
        Tx,
        Scale * Sin,
        Scale * Cos,
        Ty);
}

public sealed record ControlPointError(
    ControlPointId Id,
    double NormalizedError,
    double EstimatedMeters,
    double PixelsAtMaxZoom);

public sealed record TransformSolveResult(
    string TransformType,
    IGeoTransform Transform,
    IReadOnlyList<ControlPointError> Errors,
    double RmsPixelsAtMaxZoom,
    double MaxPixelsAtMaxZoom,
    GeoBounds Bounds);

public sealed class TransformSolver
{
    public TransformSolveResult Solve(QTilesProject project, int imageWidth, int imageHeight)
    {
        var enabled = project.Georeference.ControlPoints.Where(p => p.Enabled).ToList();
        var type = project.Georeference.Transform.Type.Trim().ToLowerInvariant();
        IGeoTransform transform = type switch
        {
            "similarity" => SolveSimilarity(enabled),
            _ => SolveAffine(enabled)
        };

        return CreateResult(type == "similarity" ? "similarity" : "affine", transform, enabled, project.Render.MaxZoom, imageWidth, imageHeight);
    }

    public AffineTransform SolveAffine(IReadOnlyList<ControlPointConfig> points)
    {
        if (points.Count < 3)
        {
            throw new InvalidOperationException("Affine transform requires at least 3 enabled control points.");
        }

        var matrix = Matrix<double>.Build.Dense(points.Count, 3);
        var u = Vector<double>.Build.Dense(points.Count);
        var v = Vector<double>.Build.Dense(points.Count);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var world = WebMercator.LonLatToNormalized(point.World.Lon, point.World.Lat);
            matrix[i, 0] = point.Image.X;
            matrix[i, 1] = point.Image.Y;
            matrix[i, 2] = 1.0;
            u[i] = world.X;
            v[i] = world.Y;
        }

        var qr = matrix.QR();
        var uc = qr.Solve(u);
        var vc = qr.Solve(v);
        return new AffineTransform(uc[0], uc[1], uc[2], vc[0], vc[1], vc[2]);
    }

    public SimilarityTransform SolveSimilarity(IReadOnlyList<ControlPointConfig> points)
    {
        if (points.Count < 2)
        {
            throw new InvalidOperationException("Similarity transform requires at least 2 enabled control points.");
        }

        var p1 = points[0];
        var p2 = points[1];
        var w1 = WebMercator.LonLatToNormalized(p1.World.Lon, p1.World.Lat);
        var w2 = WebMercator.LonLatToNormalized(p2.World.Lon, p2.World.Lat);
        var dxImage = p2.Image.X - p1.Image.X;
        var dyImage = p2.Image.Y - p1.Image.Y;
        var dxWorld = w2.X - w1.X;
        var dyWorld = w2.Y - w1.Y;
        var imageLength = Math.Sqrt(dxImage * dxImage + dyImage * dyImage);
        var worldLength = Math.Sqrt(dxWorld * dxWorld + dyWorld * dyWorld);
        if (imageLength == 0 || worldLength == 0)
        {
            throw new InvalidOperationException("Similarity transform cannot solve coincident points.");
        }

        var imageAngle = Math.Atan2(dyImage, dxImage);
        var worldAngle = Math.Atan2(dyWorld, dxWorld);
        var rotation = worldAngle - imageAngle;
        var scale = worldLength / imageLength;
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        var tx = w1.X - scale * (cos * p1.Image.X - sin * p1.Image.Y);
        var ty = w1.Y - scale * (sin * p1.Image.X + cos * p1.Image.Y);
        return new SimilarityTransform(scale, cos, sin, tx, ty);
    }

    public static AffineTransform ToAffineTransform(IGeoTransform transform) => transform switch
    {
        AffineTransform affine => affine,
        SimilarityTransform similarity => similarity.ToAffineTransform(),
        _ => throw new InvalidOperationException("Renderer requires an affine-compatible transform.")
    };

    private static TransformSolveResult CreateResult(
        string type,
        IGeoTransform transform,
        IReadOnlyList<ControlPointConfig> points,
        int maxZoom,
        int imageWidth,
        int imageHeight)
    {
        var pixelScale = 256.0 * Math.Pow(2.0, maxZoom);
        var errors = points.Select(point =>
        {
            var predicted = transform.ImageToWorld(point.Image);
            var actual = WebMercator.LonLatToNormalized(point.World.Lon, point.World.Lat);
            var dx = predicted.X - actual.X;
            var dy = predicted.Y - actual.Y;
            var normalized = Math.Sqrt(dx * dx + dy * dy);
            return new ControlPointError(
                point.Id,
                normalized,
                normalized * 40075016.68557849,
                normalized * pixelScale);
        }).ToList();

        var rms = errors.Count == 0 ? 0 : Math.Sqrt(errors.Average(e => e.PixelsAtMaxZoom * e.PixelsAtMaxZoom));
        var max = errors.Count == 0 ? 0 : errors.Max(e => e.PixelsAtMaxZoom);
        return new TransformSolveResult(type, transform, errors, rms, max, CalculateBounds(transform, imageWidth, imageHeight));
    }

    public static GeoBounds CalculateBounds(IGeoTransform transform, int imageWidth, int imageHeight)
    {
        var corners = new[]
        {
            new ImagePoint { X = 0, Y = 0 },
            new ImagePoint { X = imageWidth, Y = 0 },
            new ImagePoint { X = imageWidth, Y = imageHeight },
            new ImagePoint { X = 0, Y = imageHeight }
        };

        var geo = corners.Select(c => WebMercator.NormalizedToLonLat(transform.ImageToWorld(c).X, transform.ImageToWorld(c).Y)).ToList();
        return new GeoBounds(geo.Min(p => p.Lon), geo.Min(p => p.Lat), geo.Max(p => p.Lon), geo.Max(p => p.Lat));
    }
}
