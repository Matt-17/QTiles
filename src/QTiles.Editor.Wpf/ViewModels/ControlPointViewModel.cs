using System.ComponentModel;
using System.Runtime.CompilerServices;
using QTiles.Core.Config;

namespace QTiles.Editor.Wpf.ViewModels;

public sealed class ControlPointViewModel : INotifyPropertyChanged
{
    private bool enabled = true;
    private string id = "";
    private string? name;
    private double imageX;
    private double imageY;
    private double longitude;
    private double latitude;
    private double errorPixels;
    private double errorMeters;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    public string Id
    {
        get => id;
        set => SetField(ref id, value);
    }

    public string? Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public double ImageX
    {
        get => imageX;
        set => SetField(ref imageX, value);
    }

    public double ImageY
    {
        get => imageY;
        set => SetField(ref imageY, value);
    }

    public double Longitude
    {
        get => longitude;
        set => SetField(ref longitude, value);
    }

    public double Latitude
    {
        get => latitude;
        set => SetField(ref latitude, value);
    }

    public double ErrorPixels
    {
        get => errorPixels;
        set
        {
            errorPixels = value;
            OnPropertyChanged();
        }
    }

    public double ErrorMeters
    {
        get => errorMeters;
        set
        {
            errorMeters = value;
            OnPropertyChanged();
        }
    }

    public static ControlPointViewModel FromConfig(ControlPointConfig point) => new()
    {
        Enabled = point.Enabled,
        Id = point.Id.Value,
        Name = point.Name,
        ImageX = point.Image.X,
        ImageY = point.Image.Y,
        Longitude = point.World.Lon,
        Latitude = point.World.Lat
    };

    public ControlPointConfig ToConfig() => new()
    {
        Enabled = Enabled,
        Id = new ControlPointId(Id),
        Name = Name,
        Image = new ImagePoint { X = ImageX, Y = ImageY },
        World = new WorldPoint { Lon = Longitude, Lat = Latitude }
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
