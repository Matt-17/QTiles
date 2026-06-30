using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QTiles.Core.Rendering;
using QTiles.ViewModels;

namespace QTiles;

public partial class RenderOptionsDialog : Window
{
    public RenderOptionsDialog(RenderOptionsSnapshot snapshot)
    {
        InitializeComponent();
        Options = new RenderOptionsDialogModel(snapshot);
        DataContext = Options;
    }

    public RenderOptionsDialogModel Options { get; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Options.TryValidate(out var message))
        {
            MessageBox.Show(this, message, "Invalid render options", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}

public sealed class RenderOptionsDialogModel : INotifyPropertyChanged
{
    private int tileSize;
    private string format = "png";
    private int quality = 90;
    private string resampling = RenderResampling.Default;
    private string background = "white";
    private bool skipEmptyTiles = true;
    private bool overwrite = true;
    private bool writeTileJson = true;

    public RenderOptionsDialogModel(RenderOptionsSnapshot snapshot)
    {
        tileSize = snapshot.TileSize;
        format = NormalizeFormat(snapshot.Format);
        quality = Math.Clamp(snapshot.Quality, 1, 100);
        resampling = RenderResampling.Normalize(snapshot.Resampling);
        background = NormalizeJpegMatte(snapshot.Background);
        skipEmptyTiles = snapshot.SkipEmptyTiles;
        overwrite = snapshot.Overwrite;
        writeTileJson = snapshot.WriteTileJson;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<RenderResamplingChoice> ResamplingChoices { get; } =
    [
        new(RenderResampling.NoHalo, "Maximum quality"),
        new(RenderResampling.VertexSplitQuadraticBspline, "Very smooth"),
        new(RenderResampling.LocallyBoundedBicubic, "Sharp"),
        new(RenderResampling.Bicubic, "Bicubic"),
        new(RenderResampling.Bilinear, "Bilinear"),
        new(RenderResampling.Nearest, "Nearest")
    ];

    public IReadOnlyList<string> Formats { get; } = ["png", "jpg", "webp"];

    public IReadOnlyList<string> Backgrounds { get; } = ["white", "black"];

    public int TileSize
    {
        get => tileSize;
        set => SetField(ref tileSize, value);
    }

    public string Format
    {
        get => format;
        set
        {
            if (SetField(ref format, NormalizeFormat(value)))
            {
                OnPropertyChanged(nameof(IsLossyQualityVisible));
                OnPropertyChanged(nameof(IsJpegMatteVisible));
                OnPropertyChanged(nameof(QualityLabel));
            }
        }
    }

    public bool IsLossyQualityVisible => Format is "jpg" or "webp";

    public bool IsJpegMatteVisible => Format == "jpg";

    public string QualityLabel => Format == "webp" ? "WebP quality" : "JPEG quality";

    public int Quality
    {
        get => quality;
        set => SetField(ref quality, value);
    }

    public string Resampling
    {
        get => resampling;
        set => SetField(ref resampling, RenderResampling.Normalize(value));
    }

    public string Background
    {
        get => background;
        set => SetField(ref background, NormalizeJpegMatte(value));
    }

    public bool SkipEmptyTiles
    {
        get => skipEmptyTiles;
        set => SetField(ref skipEmptyTiles, value);
    }

    public bool Overwrite
    {
        get => overwrite;
        set => SetField(ref overwrite, value);
    }

    public bool WriteTileJson
    {
        get => writeTileJson;
        set => SetField(ref writeTileJson, value);
    }

    public RenderOptionsSnapshot ToSnapshot() => new(
        TileSize,
        Format,
        Quality,
        Resampling,
        Background,
        SkipEmptyTiles,
        Overwrite,
        WriteTileJson);

    public bool TryValidate(out string message)
    {
        if (TileSize <= 0 || TileSize > 4096)
        {
            message = "Tile size must be between 1 and 4096.";
            return false;
        }

        if (!Formats.Contains(Format))
        {
            message = "Format must be png, jpg, or webp.";
            return false;
        }

        if (Quality is < 1 or > 100)
        {
            message = "Quality must be between 1 and 100.";
            return false;
        }

        if (!RenderResampling.IsSupported(Resampling))
        {
            message = "Choose a supported resampling mode.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static string NormalizeFormat(string? value)
    {
        var format = (value ?? "png").Trim().ToLowerInvariant();
        return format == "jpeg" ? "jpg" : format;
    }

    private static string NormalizeJpegMatte(string? value)
    {
        var background = (value ?? "white").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(background) || background == "transparent" ? "white" : background;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record RenderResamplingChoice(string Value, string Label);
