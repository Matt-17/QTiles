using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using QTiles.Core.Config;

namespace QTiles.ViewModels;

public sealed class ProjectSourceViewModel : INotifyPropertyChanged
{
    private string id = "";
    private string? name;
    private string image = "";
    private bool enabled = true;
    private double opacity = 1.0;
    private string origin = "top-left";
    private string units = "pixels";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id
    {
        get => id;
        set
        {
            if (SetField(ref id, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string? Name
    {
        get => name;
        set
        {
            if (SetField(ref name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Image
    {
        get => image;
        set
        {
            if (SetField(ref image, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (SetField(ref enabled, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public double Opacity
    {
        get => opacity;
        set
        {
            if (SetField(ref opacity, value))
            {
                OnPropertyChanged(nameof(OpacityPercentText));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Origin
    {
        get => origin;
        set => SetField(ref origin, value);
    }

    public string Units
    {
        get => units;
        set => SetField(ref units, value);
    }

    public GeoreferenceConfig Georeference { get; set; } = new();

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            if (!string.IsNullOrWhiteSpace(Image))
            {
                return Path.GetFileNameWithoutExtension(Image);
            }

            return string.IsNullOrWhiteSpace(Id) ? "Source" : Id;
        }
    }

    public string OpacityPercentText => $"{Math.Clamp(Opacity, 0.0, 1.0):P0}";

    public string Summary
    {
        get
        {
            var opacitySuffix = Math.Abs(Opacity - 1.0) > 0.0000001 ? $" ({OpacityPercentText})" : "";
            return Enabled
                ? $"{Image}{opacitySuffix}"
                : $"Disabled - {Image}{opacitySuffix}";
        }
    }

    public static ProjectSourceViewModel FromConfig(ProjectSourceConfig source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Image = source.Image,
        Enabled = source.Enabled,
        Opacity = source.Opacity,
        Origin = source.Origin,
        Units = source.Units,
        Georeference = source.Georeference
    };

    public ProjectSourceConfig ToConfig()
    {
        var config = new ProjectSourceConfig
        {
            Id = Id,
            Name = Name,
            Image = Image,
            Enabled = Enabled,
            Origin = Origin,
            Units = Units,
            Georeference = Georeference
        };
        config.Opacity = Opacity;
        return config;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
