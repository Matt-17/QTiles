using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BruTile.Predefined;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Manipulations;
using Mapsui.Projections;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using QTiles.Core.Config;
using QTiles.Core.Geo;
using QTiles.Editor.Wpf.ViewModels;

namespace QTiles.Editor.Wpf;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel = new();
    private readonly Dictionary<ControlPointViewModel, PropertyChangedEventHandler> pointHandlers = [];
    private ControlPointViewModel? draggedPoint;
    private string? draggedPane;
    private int imagePixelWidth;
    private int imagePixelHeight;
    private double imageZoom = 1;
    private double imageRotation;
    private double imagePanX;
    private double imagePanY;
    private bool isPanningImage;
    private bool isPanningWorld;
    private Point imagePanStart;
    private Point imagePanOrigin;
    private Point worldPanStartScreen;
    private Point worldPanOriginMercator;
    private double worldPanStartResolution;
    private bool previewMissingStatusShown;
    private const double WebMercatorHalfWorld = 20037508.342789244;
    private const double WebMercatorWorldWidth = WebMercatorHalfWorld * 2.0;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        viewModel.ControlPoints.CollectionChanged += ControlPointsOnCollectionChanged;
        ImageOverlay.MouseMove += OverlayOnMouseMove;
        ImageOverlay.MouseLeftButtonUp += OverlayOnMouseLeftButtonUp;
        WorldOverlay.MouseMove += OverlayOnMouseMove;
        WorldOverlay.MouseLeftButtonUp += OverlayOnMouseLeftButtonUp;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeWorldMap();
        await viewModel.LoadStartupProjectAsync(Environment.GetCommandLineArgs().Skip(1).ToArray());
        RefreshSourceImage();
        SubscribePointHandlers();
        FitWorldView();
        RedrawMarkers();
    }

    private void InitializeWorldMap()
    {
        var map = new Mapsui.Map();
        map.Widgets.Clear();
        map.Performance.IsActive = Mapsui.Widgets.ActiveMode.No;
        Mapsui.Utilities.Performance.DefaultIsActive = Mapsui.Widgets.ActiveMode.No;
        AddBaseMapLayer(map);
        map.ViewportInitialized += (_, _) =>
        {
            FitWorldView();
            RedrawPreviewOverlay();
            RedrawMarkers();
        };
        map.Navigator.ViewportChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RedrawPreviewOverlay();
            RedrawMarkers();
        });
        WorldMapControl.Map = map;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SourceImage))
        {
            RefreshSourceImage();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.UseSatelliteMap))
        {
            RefreshBaseMapLayer();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsPreviewEnabled))
        {
            previewMissingStatusShown = false;
            RedrawPreviewOverlay();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.OutputDirectory)
            or nameof(MainWindowViewModel.Format)
            or nameof(MainWindowViewModel.RenderSummaryText))
        {
            previewMissingStatusShown = false;
            RedrawPreviewOverlay();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedPoint) or nameof(MainWindowViewModel.CurrentSolveResult))
        {
            RedrawMarkers();
        }
    }

    private void ControlPointsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ControlPointViewModel point in e.OldItems)
            {
                if (pointHandlers.Remove(point, out var handler))
                {
                    point.PropertyChanged -= handler;
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ControlPointViewModel point in e.NewItems)
            {
                SubscribePointHandler(point);
            }
        }

        RedrawMarkers();
    }

    private void SubscribePointHandlers()
    {
        foreach (var point in viewModel.ControlPoints)
        {
            SubscribePointHandler(point);
        }
    }

    private void SubscribePointHandler(ControlPointViewModel point)
    {
        if (pointHandlers.ContainsKey(point))
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, _) => RedrawMarkers();
        point.PropertyChanged += handler;
        pointHandlers[point] = handler;
    }

    private void RefreshSourceImage()
    {
        try
        {
            var path = viewModel.ResolveSourceImagePath();
            if (!File.Exists(path))
            {
                SourceImageElement.Source = null;
                imagePixelWidth = 0;
                imagePixelHeight = 0;
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            SourceImageElement.Source = bitmap;
            imagePixelWidth = bitmap.PixelWidth;
            imagePixelHeight = bitmap.PixelHeight;
            FitImage();
        }
        catch
        {
            SourceImageElement.Source = null;
            imagePixelWidth = 0;
            imagePixelHeight = 0;
        }

        RedrawMarkers();
    }

    private void WorldOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != WorldOverlay)
        {
            return;
        }

        if (e.ClickCount < 2)
        {
            var viewport = WorldMapControl.Map?.Navigator.Viewport;
            if (viewport is null)
            {
                return;
            }

            isPanningWorld = true;
            worldPanStartScreen = e.GetPosition(WorldOverlay);
            worldPanOriginMercator = new Point(viewport.Value.CenterX, viewport.Value.CenterY);
            worldPanStartResolution = viewport.Value.Resolution;
            WorldOverlay.CaptureMouse();
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(WorldOverlay);
        var geo = ScreenToWorld(position);
        viewModel.HandleWorldPaneClick(geo.Lon, geo.Lat);
        RedrawMarkers();
        e.Handled = true;
    }

    private void ImageOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != ImageOverlay || imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            return;
        }

        if (e.ClickCount < 2)
        {
            return;
        }

        var position = e.GetPosition(ImageOverlay);
        var image = ScreenToImage(position);
        viewModel.HandleImagePaneClick(image.X, image.Y);
        RedrawMarkers();
        e.Handled = true;
    }

    private void WorldOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var map = WorldMapControl.Map;
        if (map is null)
        {
            return;
        }

        var position = e.GetPosition(WorldOverlay);
        var factor = e.Delta > 0 ? 1.08 : 1.0 / 1.08;
        map.Navigator.ZoomTo(map.Navigator.Viewport.Resolution / factor, new ScreenPosition(position.X, position.Y), 0, null!);
        e.Handled = true;
    }

    private void OverlayOnMouseMove(object sender, MouseEventArgs e)
    {
        if (isPanningWorld && sender == WorldOverlay)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                isPanningWorld = false;
                WorldOverlay.ReleaseMouseCapture();
                return;
            }

            PanWorldTo(e.GetPosition(WorldOverlay));
            return;
        }

        if (draggedPoint is null || draggedPane is null)
        {
            return;
        }

        if (draggedPane == "image" && sender == ImageOverlay && imagePixelWidth > 0 && imagePixelHeight > 0)
        {
            var image = ScreenToImage(e.GetPosition(ImageOverlay));
            draggedPoint.ImageX = image.X;
            draggedPoint.ImageY = image.Y;
        }

        if (draggedPane == "world" && sender == WorldOverlay)
        {
            var geo = ScreenToWorld(e.GetPosition(WorldOverlay));
            draggedPoint.Longitude = geo.Lon;
            draggedPoint.Latitude = geo.Lat;
        }

        RedrawMarkers();
    }

    private void OverlayOnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (isPanningWorld && sender == WorldOverlay)
        {
            isPanningWorld = false;
            WorldOverlay.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (draggedPoint is null || draggedPane is null)
        {
            return;
        }

        if (draggedPane == "image")
        {
            viewModel.MoveImagePoint(draggedPoint, draggedPoint.ImageX, draggedPoint.ImageY);
            ImageOverlay.ReleaseMouseCapture();
        }
        else
        {
            viewModel.MoveWorldPoint(draggedPoint, draggedPoint.Longitude, draggedPoint.Latitude);
            WorldOverlay.ReleaseMouseCapture();
        }

        draggedPoint = null;
        draggedPane = null;
        RedrawMarkers();
    }

    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender == WorldOverlay)
        {
            FitWorldView();
        }

        if (sender == WorldOverlay || sender == PreviewOverlay)
        {
            RedrawPreviewOverlay();
        }

        RedrawMarkers();
    }

    private void ZoomSelectedPoint_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedPoint is not null)
        {
            ZoomToWorldPoint(viewModel.SelectedPoint);
        }
    }

    private void RedrawMarkers()
    {
        if (!IsLoaded)
        {
            return;
        }

        WorldOverlay.Children.Clear();
        ImageOverlay.Children.Clear();
        AddImageFootprint();
        foreach (var point in viewModel.ControlPoints)
        {
            AddWorldMarker(point);
            AddImageMarker(point);
        }
    }

    private void RedrawPreviewOverlay()
    {
        if (!IsLoaded)
        {
            return;
        }

        PreviewOverlay.Children.Clear();
        if (!viewModel.IsPreviewEnabled)
        {
            return;
        }

        var map = WorldMapControl.Map;
        var viewport = map?.Navigator.Viewport;
        if (viewport is not { } activeViewport
            || activeViewport.Width <= 0
            || activeViewport.Height <= 0
            || PreviewOverlay.ActualWidth <= 0
            || PreviewOverlay.ActualHeight <= 0)
        {
            return;
        }

        var outputDirectory = viewModel.ResolveOutputDirectory();
        var extension = NormalizeExtension(viewModel.Format);
        if (!Directory.Exists(outputDirectory) || !Directory.EnumerateFiles(outputDirectory, $"*.{extension}", SearchOption.AllDirectories).Any())
        {
            ShowPreviewMissingStatus();
            return;
        }

        previewMissingStatusShown = false;
        var zoom = ViewportResolutionToZoom(activeViewport.Resolution);
        var tileSize = Math.Max(1, viewModel.TileSize);
        var resolution = WebMercatorWorldWidth / (tileSize * Math.Pow(2.0, zoom));
        var minWorldX = activeViewport.CenterX - activeViewport.Width * activeViewport.Resolution / 2.0;
        var maxWorldX = activeViewport.CenterX + activeViewport.Width * activeViewport.Resolution / 2.0;
        var minWorldY = activeViewport.CenterY - activeViewport.Height * activeViewport.Resolution / 2.0;
        var maxWorldY = activeViewport.CenterY + activeViewport.Height * activeViewport.Resolution / 2.0;
        var maxTile = (int)Math.Min(int.MaxValue, Math.Pow(2.0, zoom) - 1.0);
        var minTileX = ClampTile((int)Math.Floor((minWorldX + WebMercatorHalfWorld) / (tileSize * resolution)), maxTile);
        var maxTileX = ClampTile((int)Math.Floor((maxWorldX + WebMercatorHalfWorld) / (tileSize * resolution)), maxTile);
        var minTileY = ClampTile((int)Math.Floor((WebMercatorHalfWorld - maxWorldY) / (tileSize * resolution)), maxTile);
        var maxTileY = ClampTile((int)Math.Floor((WebMercatorHalfWorld - minWorldY) / (tileSize * resolution)), maxTile);

        for (var x = minTileX; x <= maxTileX; x++)
        {
            for (var y = minTileY; y <= maxTileY; y++)
            {
                AddPreviewTile(outputDirectory, extension, zoom, x, y, tileSize, resolution, activeViewport);
            }
        }
    }

    private void AddPreviewTile(
        string outputDirectory,
        string extension,
        int zoom,
        int x,
        int y,
        int tileSize,
        double resolution,
        Viewport viewport)
    {
        var path = System.IO.Path.Combine(outputDirectory, zoom.ToString(), x.ToString(), $"{y}.{extension}");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var leftWorld = -WebMercatorHalfWorld + x * tileSize * resolution;
            var rightWorld = leftWorld + tileSize * resolution;
            var topWorld = WebMercatorHalfWorld - y * tileSize * resolution;
            var bottomWorld = topWorld - tileSize * resolution;
            var (left, top) = viewport.WorldToScreenXY(leftWorld, topWorld);
            var (right, bottom) = viewport.WorldToScreenXY(rightWorld, bottomWorld);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new System.Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new Image
            {
                Source = bitmap,
                Width = Math.Abs(right - left),
                Height = Math.Abs(bottom - top),
                Opacity = Math.Clamp(viewModel.PreviewOpacity, 0, 1),
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(image, Math.Min(left, right));
            Canvas.SetTop(image, Math.Min(top, bottom));
            PreviewOverlay.Children.Add(image);
        }
        catch
        {
            // Ignore corrupt or unsupported tile images so the editor remains usable.
        }
    }

    private int ViewportResolutionToZoom(double resolution)
    {
        var tileSize = Math.Max(1, viewModel.TileSize);
        var rawZoom = Math.Log(WebMercatorWorldWidth / (tileSize * resolution), 2.0);
        return Math.Clamp((int)Math.Round(rawZoom), viewModel.MinZoom, viewModel.MaxZoom);
    }

    private static int ClampTile(int tile, int maxTile) => Math.Clamp(tile, 0, Math.Max(0, maxTile));

    private static string NormalizeExtension(string format) =>
        format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : format.ToLowerInvariant();

    private void ShowPreviewMissingStatus()
    {
        if (previewMissingStatusShown)
        {
            return;
        }

        previewMissingStatusShown = true;
        viewModel.Status = "No rendered tiles found in output directory";
    }

    private void AddImageFootprint()
    {
        var result = viewModel.CurrentSolveResult;
        if (result is null || imagePixelWidth <= 0 || imagePixelHeight <= 0 || WorldOverlay.ActualWidth <= 0 || WorldOverlay.ActualHeight <= 0)
        {
            return;
        }

        var corners = new[]
        {
            new ImagePoint { X = 0, Y = 0 },
            new ImagePoint { X = imagePixelWidth, Y = 0 },
            new ImagePoint { X = imagePixelWidth, Y = imagePixelHeight },
            new ImagePoint { X = 0, Y = imagePixelHeight }
        };
        var screenPoints = new PointCollection();
        foreach (var corner in corners)
        {
            var normalized = result.Transform.ImageToWorld(corner);
            var geo = WebMercator.NormalizedToLonLat(normalized.X, normalized.Y);
            screenPoints.Add(WorldToScreen(geo.Lon, geo.Lat));
        }

        WorldOverlay.Children.Add(new Polygon
        {
            Points = screenPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(59, 170, 141)),
            Fill = new SolidColorBrush(Color.FromArgb(42, 59, 170, 141)),
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            IsHitTestVisible = false
        });
    }

    private void AddWorldMarker(ControlPointViewModel point)
    {
        if (WorldOverlay.ActualWidth <= 0 || WorldOverlay.ActualHeight <= 0)
        {
            return;
        }

        var screen = WorldToScreen(point.Longitude, point.Latitude);
        AddMarker(WorldOverlay, point, screen.X, screen.Y, "world");
    }

    private void AddImageMarker(ControlPointViewModel point)
    {
        if (ImageOverlay.ActualWidth <= 0 || ImageOverlay.ActualHeight <= 0 || imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            return;
        }

        var x = point.ImageX / imagePixelWidth * ImageOverlay.ActualWidth;
        var y = point.ImageY / imagePixelHeight * ImageOverlay.ActualHeight;
        AddMarker(ImageOverlay, point, x, y, "image");
    }

    private void AddMarker(Canvas overlay, ControlPointViewModel point, double x, double y, string pane)
    {
        var selected = ReferenceEquals(point, viewModel.SelectedPoint);
        var size = selected ? 30.0 : 24.0;
        var marker = new Grid
        {
            Width = size,
            Height = size,
            Tag = point,
            ToolTip = $"{point.Id} {point.Name}".Trim(),
            Cursor = Cursors.Hand
        };
        marker.Children.Add(new Ellipse
        {
            Fill = MarkerFill(point),
            Stroke = selected ? Brushes.White : MarkerStroke(point),
            StrokeThickness = selected ? 3 : 2
        });
        marker.Children.Add(new TextBlock
        {
            Text = point.Id,
            Foreground = Brushes.White,
            FontSize = selected ? 12 : 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        marker.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            viewModel.SelectedPoint = point;
            if (viewModel.EditorMode == "Delete point")
            {
                viewModel.DeletePoint(point);
                return;
            }

            draggedPoint = point;
            draggedPane = pane;
            overlay.CaptureMouse();
        };

        Canvas.SetLeft(marker, x - size / 2.0);
        Canvas.SetTop(marker, y - size / 2.0);
        overlay.Children.Add(marker);
    }

    private void AddBaseMapLayer(Mapsui.Map map)
    {
        try
        {
            map.Layers.Add(viewModel.UseSatelliteMap
                ? new TileLayer(KnownTileSources.Create(KnownTileSource.BingAerial)) { Name = "Satellite" }
                : OpenStreetMap.CreateTileLayer("QTiles.Editor"));
        }
        catch
        {
            map.Layers.Add(OpenStreetMap.CreateTileLayer("QTiles.Editor"));
        }
    }

    private void RefreshBaseMapLayer()
    {
        var map = WorldMapControl.Map;
        if (map is null)
        {
            return;
        }

        map.Layers.Clear();
        AddBaseMapLayer(map);
        RedrawMarkers();
    }

    private static Brush MarkerFill(ControlPointViewModel point)
    {
        if (!point.Enabled)
        {
            return Brushes.DimGray;
        }

        return point.ErrorPixels switch
        {
            > 8 => new SolidColorBrush(Color.FromRgb(239, 100, 97)),
            > 2 => new SolidColorBrush(Color.FromRgb(224, 173, 68)),
            _ => new SolidColorBrush(Color.FromRgb(59, 170, 141))
        };
    }

    private static Brush MarkerStroke(ControlPointViewModel point) =>
        point.Enabled ? Brushes.White : Brushes.Gray;

    private Point WorldToScreen(double lon, double lat)
    {
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        if (viewport is not { } activeViewport || activeViewport.Width <= 0 || activeViewport.Height <= 0)
        {
            return new Point(WorldOverlay.ActualWidth / 2.0, WorldOverlay.ActualHeight / 2.0);
        }

        var (worldX, worldY) = SphericalMercator.FromLonLat(lon, lat);
        var (screenX, screenY) = activeViewport.WorldToScreenXY(worldX, worldY);
        return new Point(screenX, screenY);
    }

    private GeoPoint ScreenToWorld(Point point)
    {
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        if (viewport is not { } activeViewport || activeViewport.Width <= 0 || activeViewport.Height <= 0)
        {
            return new GeoPoint(0, 0);
        }

        var (worldX, worldY) = activeViewport.ScreenToWorldXY(point.X, point.Y);
        var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);
        return new GeoPoint(lon, lat);
    }

    private Point ScreenToImage(Point point)
    {
        var x = ImageOverlay.ActualWidth <= 0 ? 0 : point.X / ImageOverlay.ActualWidth * imagePixelWidth;
        var y = ImageOverlay.ActualHeight <= 0 ? 0 : point.Y / ImageOverlay.ActualHeight * imagePixelHeight;
        return new Point(
            Math.Clamp(x, 0, Math.Max(0, imagePixelWidth)),
            Math.Clamp(y, 0, Math.Max(0, imagePixelHeight)));
    }

    private void ImagePane_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomImage(e.Delta > 0 ? 1.12 : 1.0 / 1.12);
        e.Handled = true;
    }

    private void ImagePane_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        isPanningImage = true;
        imagePanStart = e.GetPosition(ImagePane);
        imagePanOrigin = new Point(imagePanX, imagePanY);
        ImagePane.CaptureMouse();
        e.Handled = true;
    }

    private void ImagePane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || e.OriginalSource != ImageOverlay)
        {
            return;
        }

        isPanningImage = true;
        imagePanStart = e.GetPosition(ImagePane);
        imagePanOrigin = new Point(imagePanX, imagePanY);
        ImagePane.CaptureMouse();
        e.Handled = true;
    }

    private void ImagePane_MouseMove(object sender, MouseEventArgs e)
    {
        if (!isPanningImage)
        {
            return;
        }

        if (e.RightButton != MouseButtonState.Pressed && e.LeftButton != MouseButtonState.Pressed)
        {
            isPanningImage = false;
            ImagePane.ReleaseMouseCapture();
            return;
        }

        var current = e.GetPosition(ImagePane);
        imagePanX = imagePanOrigin.X + current.X - imagePanStart.X;
        imagePanY = imagePanOrigin.Y + current.Y - imagePanStart.Y;
        ApplyImageTransform();
    }

    private void ImagePane_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isPanningImage)
        {
            return;
        }

        isPanningImage = false;
        ImagePane.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ImagePane_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        isPanningImage = false;
        ImagePane.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ImageFit_Click(object sender, RoutedEventArgs e) => FitImage();

    private void ImageZoomOut_Click(object sender, RoutedEventArgs e) => ZoomImage(1.0 / 1.18);

    private void ImageZoomIn_Click(object sender, RoutedEventArgs e) => ZoomImage(1.18);

    private void ImageRotate_Click(object sender, RoutedEventArgs e)
    {
        imageRotation = (imageRotation + 90) % 360;
        ApplyImageTransform();
    }

    private void ZoomImage(double factor)
    {
        imageZoom = Math.Clamp(imageZoom * factor, 0.25, 8);
        ApplyImageTransform();
    }

    private void FitImage()
    {
        imageZoom = 1;
        imageRotation = 0;
        imagePanX = 0;
        imagePanY = 0;
        ApplyImageTransform();
    }

    private void ApplyImageTransform()
    {
        ImageScaleTransform.ScaleX = imageZoom;
        ImageScaleTransform.ScaleY = imageZoom;
        ImageRotateTransform.Angle = imageRotation;
        ImageTranslateTransform.X = imagePanX;
        ImageTranslateTransform.Y = imagePanY;
        RedrawMarkers();
    }

    private void PanWorldTo(Point currentScreen)
    {
        var map = WorldMapControl.Map;
        if (map is null)
        {
            return;
        }

        var deltaX = currentScreen.X - worldPanStartScreen.X;
        var deltaY = currentScreen.Y - worldPanStartScreen.Y;
        var centerX = worldPanOriginMercator.X - deltaX * worldPanStartResolution;
        var centerY = worldPanOriginMercator.Y + deltaY * worldPanStartResolution;
        map.Navigator.CenterOn(centerX, centerY, 0, null!);
    }

    private void FitWorldView()
    {
        var map = WorldMapControl.Map;
        if (map is null || WorldOverlay.ActualWidth <= 0 || WorldOverlay.ActualHeight <= 0)
        {
            return;
        }

        var enabled = viewModel.ControlPoints.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            var (worldX, worldY) = SphericalMercator.FromLonLat(0, 0);
            map.Navigator.CenterOnAndZoomTo(new MPoint(worldX, worldY), 125000, 0, null!);
            return;
        }

        if (enabled.Count == 1)
        {
            ZoomToWorldPoint(enabled[0]);
            return;
        }

        var points = enabled
            .Select(p => SphericalMercator.FromLonLat(p.Longitude, p.Latitude))
            .ToList();
        var minX = points.Min(p => p.x);
        var maxX = points.Max(p => p.x);
        var minY = points.Min(p => p.y);
        var maxY = points.Max(p => p.y);
        var padX = Math.Max(700, (maxX - minX) * 0.22);
        var padY = Math.Max(700, (maxY - minY) * 0.22);
        var extent = new MRect(minX - padX, minY - padY, maxX + padX, maxY + padY);
        map.Navigator.ZoomToBox(extent, MBoxFit.Fit, 0, null!);
    }

    private void ZoomToWorldPoint(ControlPointViewModel point)
    {
        var map = WorldMapControl.Map;
        if (map is null)
        {
            return;
        }

        var (worldX, worldY) = SphericalMercator.FromLonLat(point.Longitude, point.Latitude);
        map.Navigator.CenterOnAndZoomTo(new MPoint(worldX, worldY), 20, 0, null!);
    }
}
