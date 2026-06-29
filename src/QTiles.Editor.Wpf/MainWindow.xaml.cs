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
using QTiles.Editor.Wpf.Services;
using QTiles.Editor.Wpf.ViewModels;

namespace QTiles.Editor.Wpf;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel = new();
    private readonly EditorSettingsService settingsService = new();
    private readonly Dictionary<ControlPointViewModel, PropertyChangedEventHandler> pointHandlers = [];
    private EditorSettings editorSettings = new();
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
    private bool startupProjectLoaded;
    private bool initialWorldViewApplied;
    private const double WebMercatorHalfWorld = 20037508.342789244;
    private const double WebMercatorWorldWidth = WebMercatorHalfWorld * 2.0;
    private const double MapTileSize = 256.0;
    private const double MinVisibleWindowSize = 96.0;

    public MainWindow()
    {
        InitializeComponent();
        editorSettings = settingsService.Load();
        ApplyWindowSettings(editorSettings.Window);
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
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
        startupProjectLoaded = true;
        RefreshSourceImage();
        SubscribePointHandlers();
        ApplyInitialWorldView();
        RedrawMarkers();
    }

    private void InitializeWorldMap()
    {
        var map = new Mapsui.Map();
        map.Widgets.Clear();
        map.Performance.IsActive = Mapsui.Widgets.ActiveMode.No;
        Mapsui.Utilities.Performance.DefaultIsActive = Mapsui.Widgets.ActiveMode.No;
        AddBaseMapLayer(map);
        map.ViewportInitialized += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplyInitialWorldView();
            RedrawPreviewOverlay();
            RedrawMarkers();
        });
        map.Navigator.ViewportChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RedrawPreviewOverlay();
            RedrawMarkers();
        });
        WorldMapControl.Map = map;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (viewModel.HasUnsavedChanges)
        {
            var result = MessageBox.Show(
                this,
                "There are unsaved project changes. Close without saving?",
                "Unsaved changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        CaptureEditorSettings();
        settingsService.Save(editorSettings);
    }

    private void ApplyWindowSettings(EditorWindowSettings settings)
    {
        if (!settings.HasBounds
            || !double.IsFinite(settings.Left)
            || !double.IsFinite(settings.Top)
            || !IsFinitePositive(settings.Width)
            || !IsFinitePositive(settings.Height))
        {
            return;
        }

        var width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        var height = Math.Clamp(settings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        var bounds = new Rect(settings.Left, settings.Top, width, height);

        Width = width;
        Height = height;
        if (IsVisibleOnVirtualScreen(bounds))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (settings.State.Equals(nameof(WindowState.Maximized), StringComparison.OrdinalIgnoreCase))
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CaptureEditorSettings()
    {
        var bounds = RestoreBounds;
        if (!IsUsableBounds(bounds))
        {
            bounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        }

        editorSettings.Window = new EditorWindowSettings
        {
            HasBounds = IsUsableBounds(bounds),
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            State = WindowState == WindowState.Maximized ? nameof(WindowState.Maximized) : nameof(WindowState.Normal)
        };

        var savedMapView = CaptureMapViewSettings();
        if (savedMapView is not null)
        {
            editorSettings.LastMapView = savedMapView;
        }
    }

    private EditorMapViewSettings? CaptureMapViewSettings()
    {
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        if (viewport is not { } activeViewport
            || activeViewport.Width <= 0
            || activeViewport.Height <= 0
            || !IsFinitePositive(activeViewport.Resolution))
        {
            return null;
        }

        var (lon, lat) = SphericalMercator.ToLonLat(activeViewport.CenterX, activeViewport.CenterY);
        var zoom = ResolutionToMapZoom(activeViewport.Resolution);
        if (!double.IsFinite(lon) || !double.IsFinite(lat) || !double.IsFinite(zoom))
        {
            return null;
        }

        return new EditorMapViewSettings
        {
            HasValue = true,
            Longitude = Math.Clamp(lon, -180.0, 180.0),
            Latitude = Math.Clamp(lat, -WebMercator.MaxLatitude, WebMercator.MaxLatitude),
            Zoom = Math.Clamp(zoom, 0.0, 24.0)
        };
    }

    private static bool IsVisibleOnVirtualScreen(Rect bounds)
    {
        if (!IsUsableBounds(bounds))
        {
            return false;
        }

        var visibleArea = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        visibleArea.Intersect(bounds);
        return visibleArea.Width >= MinVisibleWindowSize && visibleArea.Height >= MinVisibleWindowSize;
    }

    private static bool IsUsableBounds(Rect bounds) =>
        double.IsFinite(bounds.Left)
        && double.IsFinite(bounds.Top)
        && IsFinitePositive(bounds.Width)
        && IsFinitePositive(bounds.Height);

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;

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
                UpdateSourceImageElementSize();
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
            UpdateSourceImageElementSize();
            FitImage();
        }
        catch
        {
            SourceImageElement.Source = null;
            imagePixelWidth = 0;
            imagePixelHeight = 0;
            UpdateSourceImageElementSize();
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
        double? imageX = null;
        double? imageY = null;
        if (TryGetCurrentImageViewCenter(out var imageCenter))
        {
            imageX = imageCenter.X;
            imageY = imageCenter.Y;
        }

        viewModel.HandleWorldPaneClick(geo.Lon, geo.Lat, imageX, imageY);
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
        if (!TryScreenToImage(position, out var image))
        {
            return;
        }

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
        if (sender == WorldOverlay && !initialWorldViewApplied)
        {
            ApplyInitialWorldView();
        }

        if (sender == WorldOverlay || sender == PreviewOverlay)
        {
            RedrawPreviewOverlay();
        }

        if (sender == ImageOverlay)
        {
            UpdateSourceImageElementSize();
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

        var screen = ImageToScreen(point.ImageX, point.ImageY);
        AddMarker(ImageOverlay, point, screen.X, screen.Y, "image");
    }

    private void AddMarker(Canvas overlay, ControlPointViewModel point, double x, double y, string pane)
    {
        var selected = ReferenceEquals(point, viewModel.SelectedPoint);
        var size = selected ? 30.0 : 24.0;
        var markerScale = pane == "image" && IsFinitePositive(imageZoom) ? 1.0 / imageZoom : 1.0;
        var marker = new Grid
        {
            Width = size,
            Height = size,
            Tag = point,
            ToolTip = $"{point.Id} {point.Name}".Trim(),
            Cursor = Cursors.Hand,
            RenderTransform = new ScaleTransform(markerScale, markerScale),
            RenderTransformOrigin = new Point(0.5, 0.5)
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
        var rect = GetImageDisplayRect();
        if (rect.IsEmpty)
        {
            return new Point(0, 0);
        }

        var x = rect.Width <= 0 ? 0 : (point.X - rect.X) / rect.Width * imagePixelWidth;
        var y = rect.Height <= 0 ? 0 : (point.Y - rect.Y) / rect.Height * imagePixelHeight;
        return new Point(
            Math.Clamp(x, 0, Math.Max(0, imagePixelWidth)),
            Math.Clamp(y, 0, Math.Max(0, imagePixelHeight)));
    }

    private bool TryScreenToImage(Point point, out Point image)
    {
        var rect = GetImageDisplayRect();
        if (rect.IsEmpty || !rect.Contains(point))
        {
            image = default;
            return false;
        }

        image = ScreenToImage(point);
        return true;
    }

    private bool TryGetCurrentImageViewCenter(out Point image)
    {
        image = default;
        if (ImagePane.ActualWidth <= 0 || ImagePane.ActualHeight <= 0)
        {
            return false;
        }

        var imagePaneCenter = new Point(ImagePane.ActualWidth / 2.0, ImagePane.ActualHeight / 2.0);
        return TryImagePanePointToImage(imagePaneCenter, out image);
    }

    private bool TryImagePanePointToImage(Point imagePanePoint, out Point image)
    {
        image = default;
        try
        {
            var overlayPoint = ImagePane.TransformToDescendant(ImageOverlay).Transform(imagePanePoint);
            return TryScreenToImage(overlayPoint, out image);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private Point ImageToScreen(double imageX, double imageY)
    {
        var rect = GetImageDisplayRect();
        if (rect.IsEmpty || imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            return new Point(0, 0);
        }

        return new Point(
            rect.X + imageX / imagePixelWidth * rect.Width,
            rect.Y + imageY / imagePixelHeight * rect.Height);
    }

    private void UpdateSourceImageElementSize()
    {
        var rect = GetImageDisplayRect();
        if (rect.IsEmpty)
        {
            SourceImageElement.Width = double.NaN;
            SourceImageElement.Height = double.NaN;
            return;
        }

        SourceImageElement.Width = rect.Width;
        SourceImageElement.Height = rect.Height;
    }

    private Rect GetImageDisplayRect()
    {
        if (ImageOverlay.ActualWidth <= 0
            || ImageOverlay.ActualHeight <= 0
            || imagePixelWidth <= 0
            || imagePixelHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(ImageOverlay.ActualWidth / imagePixelWidth, ImageOverlay.ActualHeight / imagePixelHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            return Rect.Empty;
        }

        var width = imagePixelWidth * scale;
        var height = imagePixelHeight * scale;
        return new Rect(
            (ImageOverlay.ActualWidth - width) / 2.0,
            (ImageOverlay.ActualHeight - height) / 2.0,
            width,
            height);
    }

    private void ImagePane_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomImage(e.Delta > 0 ? 1.12 : 1.0 / 1.12, e.GetPosition(ImagePane));
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

    private void SourceImageDropTarget_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedImagePath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void SourceImageDropTarget_Drop(object sender, DragEventArgs e)
    {
        var imagePath = GetDroppedImagePath(e);
        if (imagePath is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        viewModel.SetSourceImage(imagePath);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void ZoomImage(double factor)
    {
        ZoomImage(factor, new Point(ImagePane.ActualWidth / 2.0, ImagePane.ActualHeight / 2.0));
    }

    private void ZoomImage(double factor, Point imagePaneAnchor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        var nextZoom = Math.Clamp(imageZoom * factor, 0.25, 8);
        if (Math.Abs(nextZoom - imageZoom) < 0.000001)
        {
            return;
        }

        if (!TryImagePanePointToImageFrame(imagePaneAnchor, out var imageFrameAnchor))
        {
            imageZoom = nextZoom;
            ApplyImageTransform();
            return;
        }

        imageZoom = nextZoom;
        ApplyImageTransform(redraw: false);
        if (TryImageFramePointToImagePane(imageFrameAnchor, out var zoomedAnchor))
        {
            imagePanX += imagePaneAnchor.X - zoomedAnchor.X;
            imagePanY += imagePaneAnchor.Y - zoomedAnchor.Y;
        }

        ApplyImageTransform();
    }

    private bool TryImagePanePointToImageFrame(Point imagePanePoint, out Point imageFramePoint)
    {
        imageFramePoint = default;
        try
        {
            imageFramePoint = ImagePane.TransformToDescendant(ImageFrame).Transform(imagePanePoint);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryImageFramePointToImagePane(Point imageFramePoint, out Point imagePanePoint)
    {
        imagePanePoint = default;
        try
        {
            imagePanePoint = ImageFrame.TransformToAncestor(ImagePane).Transform(imageFramePoint);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void FitImage()
    {
        imageZoom = 1;
        imageRotation = 0;
        imagePanX = 0;
        imagePanY = 0;
        ApplyImageTransform();
    }

    private void ApplyImageTransform(bool redraw = true)
    {
        ImageScaleTransform.ScaleX = imageZoom;
        ImageScaleTransform.ScaleY = imageZoom;
        ImageRotateTransform.Angle = imageRotation;
        ImageTranslateTransform.X = imagePanX;
        ImageTranslateTransform.Y = imagePanY;
        if (redraw)
        {
            RedrawMarkers();
        }
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

    private void ApplyInitialWorldView()
    {
        if (initialWorldViewApplied
            || !startupProjectLoaded
            || WorldOverlay.ActualWidth <= 0
            || WorldOverlay.ActualHeight <= 0
            || !IsWorldViewportReady())
        {
            return;
        }

        initialWorldViewApplied = true;
        if (!TryRestoreWorldView())
        {
            FitWorldView();
        }
    }

    private bool IsWorldViewportReady()
    {
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        return viewport is { } activeViewport
            && activeViewport.Width > 0
            && activeViewport.Height > 0;
    }

    private bool TryRestoreWorldView()
    {
        var map = WorldMapControl.Map;
        var savedView = editorSettings.LastMapView;
        if (map is null
            || !savedView.HasValue
            || !double.IsFinite(savedView.Longitude)
            || !double.IsFinite(savedView.Latitude)
            || !double.IsFinite(savedView.Zoom))
        {
            return false;
        }

        var longitude = Math.Clamp(savedView.Longitude, -180.0, 180.0);
        var latitude = Math.Clamp(savedView.Latitude, -WebMercator.MaxLatitude, WebMercator.MaxLatitude);
        var zoom = Math.Clamp(savedView.Zoom, 0.0, 24.0);
        var resolution = MapZoomToResolution(zoom);
        if (!IsFinitePositive(resolution))
        {
            return false;
        }

        var (worldX, worldY) = SphericalMercator.FromLonLat(longitude, latitude);
        map.Navigator.CenterOnAndZoomTo(new MPoint(worldX, worldY), resolution, 0, null!);
        return true;
    }

    private static double ResolutionToMapZoom(double resolution) =>
        Math.Log(WebMercatorWorldWidth / (MapTileSize * resolution), 2.0);

    private static double MapZoomToResolution(double zoom) =>
        WebMercatorWorldWidth / (MapTileSize * Math.Pow(2.0, zoom));

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

    private static string? GetDroppedImagePath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(path => File.Exists(path) && MainWindowViewModel.IsSupportedSourceImageFile(path));
    }
}
