using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using BruTile.Predefined;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Manipulations;
using Mapsui.Projections;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using QTiles.Core.Config;
using QTiles.Core.Geo;
using QTiles.Core.Imaging;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;
using QTiles.Services;
using QTiles.ViewModels;

namespace QTiles;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel = new();
    private readonly EditorSettingsService settingsService = new();
    private readonly PreviewTileService previewTileService = new();
    private readonly Dictionary<ControlPointViewModel, PropertyChangedEventHandler> pointHandlers = [];
    private readonly Dictionary<ControlPointViewModel, Grid> worldMarkers = [];
    private readonly Dictionary<ControlPointViewModel, Grid> imageMarkers = [];
    private readonly Dictionary<TileCoord, Image> previewTileImages = [];
    private readonly Dictionary<TileCoord, PreviewTileKey> previewTileKeys = [];
    private readonly HashSet<PreviewTileKey> pendingPreviewTiles = [];
    private readonly PreviewBitmapCache previewBitmapCache = new(512);
    private EditorSettings editorSettings = new();
    private Polygon? imageFootprintPolygon;
    private ControlPointViewModel? draggedPoint;
    private string? draggedPane;
    private int imagePixelWidth;
    private int imagePixelHeight;
    private double imageZoom = 1;
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
    private CancellationTokenSource previewTileCancellation = new();
    private long previewStateVersion;
    private bool startupProjectLoaded;
    private bool initialWorldViewApplied;
    private bool forceClose;
    private bool isSyncingImageToWorld;
    private bool isSyncingWorldToImage;
    private const double WebMercatorHalfWorld = 20037508.342789244;
    private const double WebMercatorWorldWidth = WebMercatorHalfWorld * 2.0;
    private const double MapTileSize = 256.0;
    private const double MinVisibleWindowSize = 96.0;
    private const double MinImageZoom = 0.05;
    private const double MaxImageZoom = 64.0;

    private sealed class PreviewBitmapCache(int capacity)
    {
        private readonly Dictionary<PreviewTileKey, LinkedListNode<PreviewBitmapCacheEntry>> entries = [];
        private readonly LinkedList<PreviewBitmapCacheEntry> order = [];

        public bool TryGet(PreviewTileKey key, out BitmapSource? bitmap)
        {
            if (!entries.TryGetValue(key, out var node))
            {
                bitmap = null;
                return false;
            }

            order.Remove(node);
            order.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }

        public void Set(PreviewTileKey key, BitmapSource? bitmap)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                order.Remove(existing);
                entries.Remove(key);
            }

            var node = new LinkedListNode<PreviewBitmapCacheEntry>(new PreviewBitmapCacheEntry(key, bitmap));
            order.AddFirst(node);
            entries[key] = node;

            while (entries.Count > capacity && order.Last is { } last)
            {
                entries.Remove(last.Value.Key);
                order.RemoveLast();
            }
        }

        public void Clear()
        {
            entries.Clear();
            order.Clear();
        }
    }

    private sealed record PreviewBitmapCacheEntry(PreviewTileKey Key, BitmapSource? Bitmap);

    public MainWindow()
    {
        InitializeComponent();
        TaskbarItemInfo = new TaskbarItemInfo();
        editorSettings = settingsService.Load();
        viewModel.UseEditorSettings(editorSettings, () => settingsService.Save(editorSettings));
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
        SyncImageViewToWorldViewport(redraw: false);
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
            if (viewModel.IsViewSyncEnabled && !isSyncingWorldToImage)
            {
                SyncImageViewToWorldViewport(redraw: false);
            }

            RedrawMarkers();
        });
        WorldMapControl.Map = map;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!forceClose && viewModel.HasUnsavedChanges)
        {
            var result = MessageBox.Show(
                this,
                "Save changes to the project before closing?",
                "Unsaved changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            switch (result)
            {
                case MessageBoxResult.Yes:
                    e.Cancel = true;
                    _ = SaveAndCloseAsync();
                    return;
                case MessageBoxResult.No:
                    break;
                default:
                    e.Cancel = true;
                    return;
            }
        }

        CancelPreviewTiles();
        CaptureEditorSettings();
        settingsService.Save(editorSettings);
    }

    private async Task SaveAndCloseAsync()
    {
        if (await viewModel.SaveProjectForCloseAsync())
        {
            forceClose = true;
            Close();
        }
    }

    private void RecentDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = button.Tag switch
        {
            "Projects" => CreateRecentFileMenu(viewModel.RecentProjects, viewModel.OpenRecentProjectCommand),
            "Images" => CreateRecentFileMenu(viewModel.RecentImages, viewModel.OpenRecentImageCommand),
            "OutputDirectories" => CreateRecentFileMenu(
                viewModel.RecentOutputDirectories,
                viewModel.OpenRecentOutputDirectoryCommand),
            _ => null
        };

        if (menu is null || menu.Items.Count == 0)
        {
            return;
        }

        e.Handled = true;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RenderDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = CreateStyledContextMenu();
        var tileJsonItem = CreateStyledMenuItem("TileJSON only", "");
        tileJsonItem.ToolTip = "Writes tilejson.json into the output directory without rendering tiles";
        tileJsonItem.Command = viewModel.WriteTileJsonCommand;
        menu.Items.Add(tileJsonItem);

        e.Handled = true;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RenderOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenderOptionsDialog(viewModel.CreateRenderOptionsSnapshot())
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            viewModel.ApplyRenderOptions(dialog.Options.ToSnapshot());
        }
    }

    private ContextMenu CreateRecentFileMenu(
        IEnumerable<RecentFileViewModel> recentFiles,
        ICommand command)
    {
        var menu = CreateStyledContextMenu();
        foreach (var recentFile in recentFiles)
        {
            var item = CreateStyledMenuItem(recentFile.DisplayName, "");
            item.ToolTip = recentFile.FilePath;
            item.Command = command;
            item.CommandParameter = recentFile.FilePath;
            menu.Items.Add(item);
        }

        return menu;
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
        if (e.PropertyName is nameof(MainWindowViewModel.IsRendering) or nameof(MainWindowViewModel.RenderPercent))
        {
            UpdateTaskbarProgress();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsRendering))
        {
            // The live map preview pauses during a render job so preview tiles do not
            // compete with it for image decoding and CPU.
            if (viewModel.IsRendering)
            {
                ClearPreviewOverlay();
            }
            else
            {
                RedrawPreviewOverlay();
            }
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SourceImage))
        {
            RefreshSourceImage();
            ClearPreviewOverlay(clearBitmapCache: true);
            RedrawPreviewOverlay();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.UseSatelliteMap))
        {
            RefreshBaseMapLayer();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsPreviewEnabled))
        {
            previewMissingStatusShown = false;
            if (!viewModel.IsPreviewEnabled)
            {
                ClearPreviewOverlay();
            }

            RedrawPreviewOverlay();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsViewSyncEnabled) && viewModel.IsViewSyncEnabled)
        {
            SyncImageViewToWorldViewport(redraw: false);
            RedrawMarkers();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.TileSize)
            or nameof(MainWindowViewModel.MinZoom)
            or nameof(MainWindowViewModel.MaxZoom)
            or nameof(MainWindowViewModel.LimitPreviewTilesToZoomLevel))
        {
            previewMissingStatusShown = false;
            ClearPreviewOverlay(clearBitmapCache: true);
            RedrawPreviewOverlay();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.CurrentPreviewPlan))
        {
            previewMissingStatusShown = false;
            ClearPreviewOverlay(clearBitmapCache: true);
            RedrawPreviewOverlay();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedPoint)
            && viewModel.SelectedPoint is { } selectedPoint
            && ControlPointsGrid.Items.Contains(selectedPoint))
        {
            ControlPointsGrid.ScrollIntoView(selectedPoint);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedPoint) or nameof(MainWindowViewModel.CurrentSolveResult))
        {
            if (e.PropertyName is nameof(MainWindowViewModel.CurrentSolveResult))
            {
                if (viewModel.IsViewSyncEnabled)
                {
                    SyncImageViewToWorldViewport(redraw: false);
                }

                ClearPreviewOverlay(clearBitmapCache: true);
                RedrawPreviewOverlay();
            }

            RedrawMarkers();
        }
    }

    private void UpdateTaskbarProgress()
    {
        if (TaskbarItemInfo is not { } taskbar)
        {
            return;
        }

        if (viewModel.IsRendering)
        {
            taskbar.ProgressState = TaskbarItemProgressState.Normal;
            taskbar.ProgressValue = Math.Clamp(viewModel.RenderPercent / 100.0, 0.0, 1.0);
        }
        else
        {
            taskbar.ProgressState = TaskbarItemProgressState.None;
            taskbar.ProgressValue = 0;
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
            if (!viewModel.IsViewSyncEnabled || !SyncImageViewToWorldViewport())
            {
                FitImage();
            }
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
        if (TryPredictImageFromWorld(geo, out var predictedImage))
        {
            imageX = predictedImage.X;
            imageY = predictedImage.Y;
        }
        else if (TryGetCurrentImageViewCenter(out var imageCenter))
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

        double? lon = null;
        double? lat = null;
        if (TryPredictWorldFromImage(image, out var predictedGeo))
        {
            lon = predictedGeo.Lon;
            lat = predictedGeo.Lat;
        }
        else if (TryGetCurrentWorldViewCenter(out var worldCenter))
        {
            lon = worldCenter.Lon;
            lat = worldCenter.Lat;
        }

        viewModel.HandleImagePaneClick(image.X, image.Y, lon, lat);
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
        RedrawPreviewOverlay();
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
            if (viewModel.IsViewSyncEnabled)
            {
                SyncImageViewToWorldViewport(redraw: false);
            }
        }

        RedrawMarkers();
    }

    private void CenterSelectedPoint_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedPoint is not null)
        {
            CenterWorldOnPoint(viewModel.SelectedPoint);
        }
    }

    private void RedrawMarkers()
    {
        if (!IsLoaded)
        {
            return;
        }

        var visibleWorldMarkers = new HashSet<ControlPointViewModel>();
        var visibleImageMarkers = new HashSet<ControlPointViewModel>();
        UpdateImageFootprint();
        foreach (var point in viewModel.ControlPoints)
        {
            if (AddOrUpdateWorldMarker(point))
            {
                visibleWorldMarkers.Add(point);
            }

            if (AddOrUpdateImageMarker(point))
            {
                visibleImageMarkers.Add(point);
            }
        }

        RemoveStaleMarkers(WorldOverlay, worldMarkers, visibleWorldMarkers);
        RemoveStaleMarkers(ImageOverlay, imageMarkers, visibleImageMarkers);
    }

    private void RedrawPreviewOverlay()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!viewModel.IsPreviewEnabled || viewModel.IsRendering)
        {
            ClearPreviewOverlay();
            return;
        }

        if (IsDraggingMarker())
        {
            ClearPreviewOverlay();
            return;
        }

        var previewPlan = viewModel.CurrentPreviewPlan;
        if (previewPlan is null)
        {
            ClearPreviewOverlay();
            ShowPreviewNeedsSolvedStatus();
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

        previewMissingStatusShown = false;
        var visibleTiles = new HashSet<TileCoord>();
        var tileSize = Math.Max(1, viewModel.TileSize);
        var previewViewport = new PreviewTileViewport(
            activeViewport.CenterX,
            activeViewport.CenterY,
            activeViewport.Width,
            activeViewport.Height,
            activeViewport.Resolution);
        var tiles = previewTileService.GetVisibleTiles(
            previewViewport,
            viewModel.MinZoom,
            viewModel.MaxZoom,
            tileSize,
            viewModel.LimitPreviewTilesToZoomLevel);
        foreach (var tile in tiles)
        {
            try
            {
                var workItem = previewTileService.CreateWorkItem(previewPlan, tile, tileSize);
                AddOrUpdatePreviewTile(workItem, activeViewport, visibleTiles);
            }
            catch (InvalidOperationException ex)
            {
                ClearPreviewOverlay();
                ShowPreviewMissingStatus($"Preview failed: {ex.Message}");
                return;
            }
        }

        foreach (var key in previewTileImages.Keys.Where(key => !visibleTiles.Contains(key)).ToList())
        {
            PreviewOverlay.Children.Remove(previewTileImages[key]);
            previewTileImages.Remove(key);
        }

        foreach (var key in previewTileKeys.Keys.Where(key => !visibleTiles.Contains(key)).ToList())
        {
            previewTileKeys.Remove(key);
        }
    }

    private void AddOrUpdatePreviewTile(
        PreviewTileWorkItem workItem,
        Viewport viewport,
        HashSet<TileCoord> visibleTiles)
    {
        var tile = workItem.RenderRequest.Tile;
        visibleTiles.Add(tile);
        previewTileKeys[tile] = workItem.Key;

        if (previewBitmapCache.TryGet(workItem.Key, out var cachedBitmap) && cachedBitmap is null)
        {
            RemovePreviewTile(tile);
            return;
        }

        if (!previewTileImages.TryGetValue(tile, out var image))
        {
            image = new Image
            {
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            previewTileImages[tile] = image;
            PreviewOverlay.Children.Add(image);
        }

        if (cachedBitmap is not null && !ReferenceEquals(image.Source, cachedBitmap))
        {
            image.Source = cachedBitmap;
        }

        LayoutPreviewTile(image, tile, viewport);

        if (cachedBitmap is null && pendingPreviewTiles.Add(workItem.Key))
        {
            _ = RenderPreviewTileAsync(workItem, previewStateVersion, previewTileCancellation.Token);
        }
    }

    private void LayoutPreviewTile(Image image, TileCoord tile, Viewport viewport)
    {
        var bounds = PreviewTileService.GetWorldBounds(tile, Math.Max(1, viewModel.TileSize));
        var (left, top) = viewport.WorldToScreenXY(bounds.Left, bounds.Top);
        var (right, bottom) = viewport.WorldToScreenXY(bounds.Right, bounds.Bottom);
        image.Width = Math.Abs(right - left);
        image.Height = Math.Abs(bottom - top);
        image.Opacity = Math.Clamp(viewModel.PreviewOpacity, 0, 1);
        Canvas.SetLeft(image, Math.Min(left, right));
        Canvas.SetTop(image, Math.Min(top, bottom));
    }

    private async Task RenderPreviewTileAsync(
        PreviewTileWorkItem workItem,
        long stateVersion,
        CancellationToken cancellationToken)
    {
        RenderedTileImage? renderedTile = null;
        BitmapSource? bitmap = null;
        try
        {
            renderedTile = await previewTileService.RenderAsync(workItem, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (renderedTile is not null)
            {
                bitmap = CreatePreviewBitmap(renderedTile);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                pendingPreviewTiles.Remove(workItem.Key);
                if (previewStateVersion == stateVersion)
                {
                    RemovePreviewTile(workItem.RenderRequest.Tile);
                    ShowPreviewMissingStatus($"Preview failed: {ex.Message}");
                }
            });
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            pendingPreviewTiles.Remove(workItem.Key);
            if (previewStateVersion != stateVersion
                || !previewTileKeys.TryGetValue(workItem.RenderRequest.Tile, out var currentKey)
                || currentKey != workItem.Key)
            {
                return;
            }

            previewBitmapCache.Set(workItem.Key, bitmap);
            if (bitmap is null)
            {
                RemovePreviewTile(workItem.RenderRequest.Tile);
                return;
            }

            if (previewTileImages.TryGetValue(workItem.RenderRequest.Tile, out var image))
            {
                image.Source = bitmap;
            }
        });
    }

    private static BitmapSource CreatePreviewBitmap(RenderedTileImage tile)
    {
        using var stream = new MemoryStream(tile.Bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ClearPreviewOverlay(bool clearBitmapCache = false)
    {
        CancelPreviewTiles();
        PreviewOverlay.Children.Clear();
        previewTileImages.Clear();
        previewTileKeys.Clear();
        pendingPreviewTiles.Clear();
        if (clearBitmapCache)
        {
            previewBitmapCache.Clear();
        }
    }

    private void HidePreviewDuringMarkerDrag()
    {
        if (viewModel.IsPreviewEnabled)
        {
            ClearPreviewOverlay();
        }
    }

    private bool IsDraggingMarker() => draggedPoint is not null && draggedPane is not null;

    private void CancelPreviewTiles()
    {
        previewStateVersion++;
        previewTileCancellation.Cancel();
        previewTileCancellation.Dispose();
        previewTileCancellation = new CancellationTokenSource();
    }

    private void RemovePreviewTile(TileCoord tile)
    {
        if (previewTileImages.Remove(tile, out var image))
        {
            PreviewOverlay.Children.Remove(image);
        }

        previewTileKeys.Remove(tile);
    }

    private void ShowPreviewNeedsSolvedStatus()
    {
        ShowPreviewMissingStatus("Preview needs a solved transform");
    }

    private void ShowPreviewMissingStatus(string message)
    {
        if (previewMissingStatusShown)
        {
            return;
        }

        previewMissingStatusShown = true;
        viewModel.Status = message;
    }

    private void UpdateImageFootprint()
    {
        var result = viewModel.CurrentSolveResult;
        if (result is null || imagePixelWidth <= 0 || imagePixelHeight <= 0 || WorldOverlay.ActualWidth <= 0 || WorldOverlay.ActualHeight <= 0)
        {
            RemoveImageFootprint();
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

        if (imageFootprintPolygon is null)
        {
            imageFootprintPolygon = new Polygon
            {
                Stroke = new SolidColorBrush(Color.FromRgb(59, 170, 141)),
                Fill = new SolidColorBrush(Color.FromArgb(42, 59, 170, 141)),
                StrokeThickness = 2,
                StrokeDashArray = [4, 3],
                IsHitTestVisible = false
            };
            Panel.SetZIndex(imageFootprintPolygon, 0);
            WorldOverlay.Children.Insert(0, imageFootprintPolygon);
        }

        imageFootprintPolygon.Points = screenPoints;
    }

    private void RemoveImageFootprint()
    {
        if (imageFootprintPolygon is not null)
        {
            WorldOverlay.Children.Remove(imageFootprintPolygon);
            imageFootprintPolygon = null;
        }
    }

    private bool AddOrUpdateWorldMarker(ControlPointViewModel point)
    {
        if (WorldOverlay.ActualWidth <= 0 || WorldOverlay.ActualHeight <= 0)
        {
            return false;
        }

        var screen = WorldToScreen(point.Longitude, point.Latitude);
        AddOrUpdateMarker(WorldOverlay, worldMarkers, point, screen.X, screen.Y, "world");
        return true;
    }

    private bool AddOrUpdateImageMarker(ControlPointViewModel point)
    {
        if (ImageOverlay.ActualWidth <= 0 || ImageOverlay.ActualHeight <= 0 || imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            return false;
        }

        var screen = ImageToScreen(point.ImageX, point.ImageY);
        AddOrUpdateMarker(ImageOverlay, imageMarkers, point, screen.X, screen.Y, "image");
        return true;
    }

    private void AddOrUpdateMarker(
        Canvas overlay,
        Dictionary<ControlPointViewModel, Grid> markers,
        ControlPointViewModel point,
        double x,
        double y,
        string pane)
    {
        var selected = ReferenceEquals(point, viewModel.SelectedPoint);
        var size = selected ? 30.0 : 24.0;
        var markerScale = pane == "image" && IsFinitePositive(imageZoom) ? 1.0 / imageZoom : 1.0;
        if (!markers.TryGetValue(point, out var marker))
        {
            marker = CreateMarker(overlay, point, pane);
            markers[point] = marker;
            Panel.SetZIndex(marker, 10);
            overlay.Children.Add(marker);
        }

        marker.Width = size;
        marker.Height = size;
        marker.Opacity = point.Enabled ? 1.0 : 0.4;
        marker.ToolTip = point.IsLocked ? $"{point.Id} {point.Name} (locked)".Trim() : $"{point.Id} {point.Name}".Trim();
        marker.Cursor = point.IsLocked ? Cursors.Arrow : Cursors.Hand;
        marker.RenderTransform = new ScaleTransform(markerScale, markerScale);
        if (marker.Children[0] is Ellipse ellipse)
        {
            ellipse.Fill = MarkerFill(point);
            ellipse.Stroke = MarkerStroke(point, selected);
            ellipse.StrokeThickness = selected || point.IsLocked ? 3 : 2;
        }

        if (marker.Children[1] is TextBlock label)
        {
            label.Text = point.Id;
            label.FontSize = selected ? 12 : 11;
        }

        Canvas.SetLeft(marker, x - size / 2.0);
        Canvas.SetTop(marker, y - size / 2.0);
    }

    private Grid CreateMarker(Canvas overlay, ControlPointViewModel point, string pane)
    {
        var marker = new Grid
        {
            Tag = point,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        marker.Children.Add(new Ellipse());
        marker.Children.Add(new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        marker.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            viewModel.SelectedPoint = point;
            if (viewModel.IsRendering)
            {
                return;
            }

            if (viewModel.EditorMode == "Delete point")
            {
                viewModel.DeletePoint(point);
                return;
            }

            if (point.IsLocked)
            {
                draggedPoint = null;
                draggedPane = null;
                viewModel.Status = $"Point {point.Id} is locked";
                return;
            }

            draggedPoint = point;
            draggedPane = pane;
            HidePreviewDuringMarkerDrag();
            overlay.CaptureMouse();
        };
        marker.ContextMenu = CreateMarkerContextMenu(point);
        marker.MouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            viewModel.SelectedPoint = point;
            if (viewModel.IsRendering)
            {
                return;
            }

            if (marker.ContextMenu is { } menu)
            {
                menu.PlacementTarget = marker;
                menu.IsOpen = true;
            }
        };
        return marker;
    }

    private void ControlPointRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: ControlPointViewModel point })
        {
            return;
        }

        viewModel.SelectedPoint = point;
        CenterWorldOnPoint(point);
        RedrawMarkers();
    }

    private void ControlPointRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: ControlPointViewModel point } row)
        {
            return;
        }

        e.Handled = true;
        row.Focus();
        row.IsSelected = true;
        viewModel.SelectedPoint = point;

        var menu = CreateMarkerContextMenu(point);
        menu.PlacementTarget = row;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static void RemoveStaleMarkers(
        Canvas overlay,
        Dictionary<ControlPointViewModel, Grid> markers,
        HashSet<ControlPointViewModel> visibleMarkers)
    {
        foreach (var point in markers.Keys.Where(point => !visibleMarkers.Contains(point)).ToList())
        {
            overlay.Children.Remove(markers[point]);
            markers.Remove(point);
        }
    }

    private ContextMenu CreateMarkerContextMenu(ControlPointViewModel point)
    {
        var menu = CreateStyledContextMenu();
        var enabledItem = CreateMarkerMenuItem("", "");
        UpdateEnabledMenuItem(enabledItem, point);
        menu.Opened += (_, _) => UpdateEnabledMenuItem(enabledItem, point);
        enabledItem.Click += (_, _) =>
        {
            point.Enabled = !point.Enabled;
            viewModel.SelectedPoint = point;
            viewModel.Status = point.Enabled ? $"Enabled point {point.Id}" : $"Disabled point {point.Id}";
            RedrawMarkers();
        };

        var lockItem = CreateMarkerMenuItem("", "");
        UpdateLockMenuItem(lockItem, point);
        menu.Opened += (_, _) => UpdateLockMenuItem(lockItem, point);
        lockItem.Click += (_, _) =>
        {
            viewModel.SetPointLocked(point, !point.IsLocked);
            RedrawMarkers();
        };

        var centerItem = CreateMarkerMenuItem("Center marker", "\uE8A3");
        centerItem.Click += (_, _) =>
        {
            viewModel.SelectedPoint = point;
            CenterWorldOnPoint(point);
            RedrawMarkers();
        };

        var deleteItem = CreateMarkerMenuItem("Delete marker", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            viewModel.DeletePoint(point);
            RedrawMarkers();
        };

        menu.Items.Add(enabledItem);
        menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(centerItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        return menu;
    }

    private ContextMenu CreateStyledContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenuStyle")
        };
        menu.Resources[typeof(MenuItem)] = FindResource("DarkMenuItemStyle");
        menu.Resources[typeof(Separator)] = FindResource("DarkMenuSeparatorStyle");
        return menu;
    }

    private static void UpdateEnabledMenuItem(MenuItem enabledItem, ControlPointViewModel point)
    {
        enabledItem.Header = point.Enabled ? "Disable marker" : "Enable marker";
        if (enabledItem.Icon is TextBlock icon)
        {
            icon.Text = point.Enabled ? "\uE711" : "\uE8FB";
        }
    }

    private static void UpdateLockMenuItem(MenuItem lockItem, ControlPointViewModel point)
    {
        lockItem.Header = point.IsLocked ? "Unlock marker" : "Lock marker";
        if (lockItem.Icon is TextBlock icon)
        {
            icon.Text = point.IsLocked ? "\uE785" : "\uE72E";
        }
    }

    private MenuItem CreateMarkerMenuItem(string header, string icon)
        => CreateStyledMenuItem(header, icon);

    private MenuItem CreateStyledMenuItem(string header, string icon)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("DarkMenuItemStyle")
        };
        if (!string.IsNullOrEmpty(icon))
        {
            item.Icon = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return item;
    }

    private void AddBaseMapLayer(Mapsui.Map map)
    {
        try
        {
            map.Layers.Add(viewModel.UseSatelliteMap
                ? new TileLayer(KnownTileSources.Create(KnownTileSource.BingAerial)) { Name = "Satellite" }
                : OpenStreetMap.CreateTileLayer("QTiles"));
        }
        catch
        {
            map.Layers.Add(OpenStreetMap.CreateTileLayer("QTiles"));
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

    private static Brush MarkerStroke(ControlPointViewModel point, bool selected)
    {
        if (point.IsLocked)
        {
            return new SolidColorBrush(Color.FromRgb(19, 24, 33));
        }

        return selected || point.Enabled ? Brushes.White : Brushes.Gray;
    }

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

    private bool TryGetCurrentWorldViewCenter(out GeoPoint geo)
    {
        geo = new GeoPoint(0, 0);
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        if (viewport is not { } activeViewport
            || activeViewport.Width <= 0
            || activeViewport.Height <= 0)
        {
            return false;
        }

        var (lon, lat) = SphericalMercator.ToLonLat(activeViewport.CenterX, activeViewport.CenterY);
        if (!double.IsFinite(lon) || !double.IsFinite(lat))
        {
            return false;
        }

        geo = new GeoPoint(
            Math.Clamp(lon, -180.0, 180.0),
            Math.Clamp(lat, -WebMercator.MaxLatitude, WebMercator.MaxLatitude));
        return true;
    }

    private bool TryPredictWorldFromImage(Point image, out GeoPoint geo)
    {
        geo = new GeoPoint(0, 0);
        if (!TryCreatePredictionTransform(out var transform))
        {
            return false;
        }

        try
        {
            var normalized = transform.ImageToWorld(ToImagePoint(image));
            if (!IsFinitePoint(normalized.X, normalized.Y))
            {
                return false;
            }

            var predicted = WebMercator.NormalizedToLonLat(normalized.X, normalized.Y);
            if (!IsFinitePoint(predicted.Lon, predicted.Lat))
            {
                return false;
            }

            geo = new GeoPoint(
                Math.Clamp(predicted.Lon, -180.0, 180.0),
                Math.Clamp(predicted.Lat, -WebMercator.MaxLatitude, WebMercator.MaxLatitude));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryPredictImageFromWorld(GeoPoint geo, out Point image)
    {
        image = default;
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0 || !TryCreatePredictionTransform(out var transform))
        {
            return false;
        }

        try
        {
            var normalized = WebMercator.LonLatToNormalized(geo.Lon, geo.Lat);
            var predicted = transform.WorldToImage(normalized);
            if (!IsFinitePoint(predicted.X, predicted.Y))
            {
                return false;
            }

            image = new Point(
                Math.Clamp(predicted.X, 0, imagePixelWidth),
                Math.Clamp(predicted.Y, 0, imagePixelHeight));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryCreatePredictionTransform(out IGeoTransform transform)
    {
        transform = null!;
        if (viewModel.CurrentSolveResult is { } solved)
        {
            transform = solved.Transform;
            return true;
        }

        var anchors = viewModel.ControlPoints.Where(p => p.Enabled).ToList();
        if (anchors.Count >= 2)
        {
            try
            {
                transform = new TransformSolver().SolveSimilarity([anchors[0].ToConfig(), anchors[1].ToConfig()]);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Coincident points; fall back to the single-anchor estimate below.
            }
        }

        return anchors.Count >= 1 && TryCreateSingleAnchorTransform(anchors[0], out transform);
    }

    private bool TryCreateSingleAnchorTransform(ControlPointViewModel anchor, out IGeoTransform transform)
    {
        transform = null!;
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        var rect = GetImageDisplayRect();
        if (viewport is not { } activeViewport
            || !IsFinitePositive(activeViewport.Resolution)
            || rect.IsEmpty
            || imagePixelWidth <= 0)
        {
            return false;
        }

        // Assume both panes currently show the same area: an offset in image pixels maps to the
        // same on-screen offset in the world pane, i.e. same direction and distance at current zoom.
        var dipPerImagePixel = rect.Width / imagePixelWidth * imageZoom;
        var normalizedPerImagePixel = dipPerImagePixel * activeViewport.Resolution / WebMercatorWorldWidth;
        if (!IsFinitePositive(normalizedPerImagePixel))
        {
            return false;
        }

        var world = WebMercator.LonLatToNormalized(anchor.Longitude, anchor.Latitude);
        transform = new SimilarityTransform(
            normalizedPerImagePixel,
            1.0,
            0.0,
            world.X - normalizedPerImagePixel * anchor.ImageX,
            world.Y - normalizedPerImagePixel * anchor.ImageY);
        return true;
    }

    private Point ScreenToImage(Point point)
    {
        var image = ScreenToImageUnbounded(point);
        return new Point(
            Math.Clamp(image.X, 0, Math.Max(0, imagePixelWidth)),
            Math.Clamp(image.Y, 0, Math.Max(0, imagePixelHeight)));
    }

    private Point ScreenToImageUnbounded(Point point)
    {
        var rect = GetImageDisplayRect();
        if (rect.IsEmpty)
        {
            return new Point(0, 0);
        }

        var x = rect.Width <= 0 ? 0 : (point.X - rect.X) / rect.Width * imagePixelWidth;
        var y = rect.Height <= 0 ? 0 : (point.Y - rect.Y) / rect.Height * imagePixelHeight;
        return new Point(x, y);
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

    private bool TryGetCurrentImageViewCenterUnbounded(out Point image)
    {
        image = default;
        if (ImagePane.ActualWidth <= 0 || ImagePane.ActualHeight <= 0)
        {
            return false;
        }

        var imagePaneCenter = new Point(ImagePane.ActualWidth / 2.0, ImagePane.ActualHeight / 2.0);
        return TryImagePanePointToImageUnbounded(imagePaneCenter, out image);
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

    private bool TryImagePanePointToImageUnbounded(Point imagePanePoint, out Point image)
    {
        image = default;
        try
        {
            var overlayPoint = ImagePane.TransformToDescendant(ImageOverlay).Transform(imagePanePoint);
            var rect = GetImageDisplayRect();
            if (rect.IsEmpty)
            {
                return false;
            }

            image = ScreenToImageUnbounded(overlayPoint);
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

    private bool SyncImageViewToWorldViewport(bool redraw = true)
    {
        if (!viewModel.IsViewSyncEnabled
            || isSyncingImageToWorld
            || isSyncingWorldToImage
            || isPanningImage
            || draggedPane == "image"
            || imagePixelWidth <= 0
            || imagePixelHeight <= 0
            || ImagePane.ActualWidth <= 0
            || ImagePane.ActualHeight <= 0)
        {
            return false;
        }

        var result = viewModel.CurrentSolveResult;
        var viewport = WorldMapControl.Map?.Navigator.Viewport;
        var baseRect = GetImageDisplayRect();
        if (result is null
            || !result.TransformType.Equals("affine", StringComparison.OrdinalIgnoreCase)
            || viewport is not { } activeViewport
            || !IsFinitePositive(activeViewport.Resolution)
            || baseRect.IsEmpty)
        {
            return false;
        }

        var normalizedCenter = ScreenPointToNormalizedMercator(
            activeViewport,
            new Point(WorldOverlay.ActualWidth / 2.0, WorldOverlay.ActualHeight / 2.0));
        ImagePoint imageCenter;
        try
        {
            imageCenter = result.Transform.WorldToImage(normalizedCenter);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!IsFinitePoint(imageCenter.X, imageCenter.Y))
        {
            return false;
        }

        double syncedZoom;
        try
        {
            syncedZoom = CalculateSyncedImageZoom(result.Transform, activeViewport, baseRect);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!IsFinitePositive(syncedZoom))
        {
            return false;
        }

        var previousZoom = imageZoom;
        var previousPanX = imagePanX;
        var previousPanY = imagePanY;
        isSyncingImageToWorld = true;
        try
        {
            imageZoom = Math.Clamp(syncedZoom, MinImageZoom, MaxImageZoom);
            imagePanX = 0;
            imagePanY = 0;
            ApplyImageTransform(redraw: false);

            var imageOverlayPoint = ImageToScreen(imageCenter.X, imageCenter.Y);
            if (!TryImageOverlayPointToImagePane(imageOverlayPoint, out var imagePanePoint))
            {
                imageZoom = previousZoom;
                imagePanX = previousPanX;
                imagePanY = previousPanY;
                ApplyImageTransform(redraw: false);
                return false;
            }

            imagePanX = ImagePane.ActualWidth / 2.0 - imagePanePoint.X;
            imagePanY = ImagePane.ActualHeight / 2.0 - imagePanePoint.Y;
            ApplyImageTransform(redraw);
            return true;
        }
        finally
        {
            isSyncingImageToWorld = false;
        }
    }

    private double CalculateSyncedImageZoom(
        IGeoTransform transform,
        Viewport viewport,
        Rect baseRect)
    {
        var baseDipPerImagePixel = baseRect.Width / imagePixelWidth;
        if (!IsFinitePositive(baseDipPerImagePixel)
            || WorldOverlay.ActualWidth <= 0
            || WorldOverlay.ActualHeight <= 0)
        {
            return double.NaN;
        }

        var center = new Point(WorldOverlay.ActualWidth / 2.0, WorldOverlay.ActualHeight / 2.0);
        var sampleDistance = CalculateSampleDistance(WorldOverlay.ActualWidth, WorldOverlay.ActualHeight);
        var right = OffsetWithinBounds(center, sampleDistance, 0, WorldOverlay.ActualWidth, WorldOverlay.ActualHeight);
        var down = OffsetWithinBounds(center, 0, sampleDistance, WorldOverlay.ActualWidth, WorldOverlay.ActualHeight);
        var horizontalZoom = CalculateWorldSampleImageZoom(transform, viewport, baseDipPerImagePixel, center, right);
        var verticalZoom = CalculateWorldSampleImageZoom(transform, viewport, baseDipPerImagePixel, center, down);
        return AveragePositive(horizontalZoom, verticalZoom);
    }

    private static double CalculateWorldSampleImageZoom(
        IGeoTransform transform,
        Viewport viewport,
        double baseDipPerImagePixel,
        Point firstScreen,
        Point secondScreen)
    {
        var screenDistance = Distance(firstScreen, secondScreen);
        if (!IsFinitePositive(screenDistance) || !IsFinitePositive(baseDipPerImagePixel))
        {
            return double.NaN;
        }

        var firstImage = transform.WorldToImage(ScreenPointToNormalizedMercator(viewport, firstScreen));
        var secondImage = transform.WorldToImage(ScreenPointToNormalizedMercator(viewport, secondScreen));
        var imagePixels = Distance(firstImage, secondImage);
        return IsFinitePositive(imagePixels)
            ? screenDistance / (imagePixels * baseDipPerImagePixel)
            : double.NaN;
    }

    private static NormalizedMercatorPoint ScreenPointToNormalizedMercator(Viewport viewport, Point screen)
    {
        var (worldX, worldY) = viewport.ScreenToWorldXY(screen.X, screen.Y);
        return MercatorMetersToNormalized(worldX, worldY);
    }

    private bool TryImageOverlayPointToImagePane(Point imageOverlayPoint, out Point imagePanePoint)
    {
        imagePanePoint = default;
        try
        {
            imagePanePoint = ImageOverlay.TransformToAncestor(ImagePane).Transform(imageOverlayPoint);
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

    private static NormalizedMercatorPoint MercatorMetersToNormalized(double x, double y) =>
        new((x + WebMercatorHalfWorld) / WebMercatorWorldWidth, (WebMercatorHalfWorld - y) / WebMercatorWorldWidth);

    private static Point NormalizedToMercatorMeters(NormalizedMercatorPoint point) =>
        new(point.X * WebMercatorWorldWidth - WebMercatorHalfWorld, WebMercatorHalfWorld - point.Y * WebMercatorWorldWidth);

    private static bool IsFinitePoint(double x, double y) => double.IsFinite(x) && double.IsFinite(y);

    private static double Distance(ImagePoint first, ImagePoint second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Distance(Point first, Point second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double CalculateSampleDistance(double width, double height)
    {
        var shorterSide = Math.Min(width, height);
        return IsFinitePositive(shorterSide) ? Math.Max(24.0, shorterSide * 0.25) : double.NaN;
    }

    private static Point OffsetWithinBounds(Point point, double offsetX, double offsetY, double width, double height)
    {
        return new Point(
            Math.Clamp(point.X + offsetX, 0, Math.Max(0, width)),
            Math.Clamp(point.Y + offsetY, 0, Math.Max(0, height)));
    }

    private static double AveragePositive(double first, double second)
    {
        var firstValid = IsFinitePositive(first);
        var secondValid = IsFinitePositive(second);
        return (firstValid, secondValid) switch
        {
            (true, true) => Math.Sqrt(first * second),
            (true, false) => first,
            (false, true) => second,
            _ => double.NaN
        };
    }

    private bool SyncWorldViewportToImage(bool syncZoom = true)
    {
        if (!viewModel.IsViewSyncEnabled
            || isSyncingWorldToImage
            || isSyncingImageToWorld
            || draggedPane == "world"
            || ImagePane.ActualWidth <= 0
            || ImagePane.ActualHeight <= 0)
        {
            return false;
        }

        var map = WorldMapControl.Map;
        var result = viewModel.CurrentSolveResult;
        if (map is null || result is null || !TryGetCurrentImageViewCenterUnbounded(out var imageCenter))
        {
            return false;
        }

        NormalizedMercatorPoint normalizedCenter;
        try
        {
            normalizedCenter = result.Transform.ImageToWorld(ToImagePoint(imageCenter));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!IsFinitePoint(normalizedCenter.X, normalizedCenter.Y))
        {
            return false;
        }

        var mercatorCenter = NormalizedToMercatorMeters(normalizedCenter);
        if (!IsFinitePoint(mercatorCenter.X, mercatorCenter.Y))
        {
            return false;
        }

        isSyncingWorldToImage = true;
        try
        {
            var center = new MPoint(mercatorCenter.X, mercatorCenter.Y);
            if (!syncZoom)
            {
                map.Navigator.CenterOn(center.X, center.Y, 0, null!);
                return true;
            }

            if (!TryCalculateMapResolutionFromImage(result.Transform, out var resolution) || !IsFinitePositive(resolution))
            {
                return false;
            }

            map.Navigator.CenterOnAndZoomTo(center, resolution, 0, null!);
            return true;
        }
        finally
        {
            isSyncingWorldToImage = false;
        }
    }

    private bool TryCalculateMapResolutionFromImage(IGeoTransform transform, out double resolution)
    {
        resolution = double.NaN;
        var center = new Point(ImagePane.ActualWidth / 2.0, ImagePane.ActualHeight / 2.0);
        var sampleDistance = CalculateSampleDistance(ImagePane.ActualWidth, ImagePane.ActualHeight);
        if (!IsFinitePositive(sampleDistance))
        {
            return false;
        }

        var right = OffsetWithinBounds(center, sampleDistance, 0, ImagePane.ActualWidth, ImagePane.ActualHeight);
        var down = OffsetWithinBounds(center, 0, sampleDistance, ImagePane.ActualWidth, ImagePane.ActualHeight);
        var horizontalResolution = CalculateImageSampleMapResolution(transform, center, right);
        var verticalResolution = CalculateImageSampleMapResolution(transform, center, down);
        resolution = AveragePositive(horizontalResolution, verticalResolution);
        return IsFinitePositive(resolution);
    }

    private double CalculateImageSampleMapResolution(IGeoTransform transform, Point firstPanePoint, Point secondPanePoint)
    {
        var screenDistance = Distance(firstPanePoint, secondPanePoint);
        if (!IsFinitePositive(screenDistance)
            || !TryImagePanePointToImageUnbounded(firstPanePoint, out var firstImage)
            || !TryImagePanePointToImageUnbounded(secondPanePoint, out var secondImage))
        {
            return double.NaN;
        }

        var firstWorld = NormalizedToMercatorMeters(transform.ImageToWorld(ToImagePoint(firstImage)));
        var secondWorld = NormalizedToMercatorMeters(transform.ImageToWorld(ToImagePoint(secondImage)));
        var worldMeters = Distance(firstWorld, secondWorld);
        return IsFinitePositive(worldMeters) ? worldMeters / screenDistance : double.NaN;
    }

    private static ImagePoint ToImagePoint(Point point) => new() { X = point.X, Y = point.Y };

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
        ApplyImageTransform(syncWorldZoom: false);
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

        var nextZoom = Math.Clamp(imageZoom * factor, MinImageZoom, MaxImageZoom);
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
        imagePanX = 0;
        imagePanY = 0;
        ApplyImageTransform();
    }

    private void ApplyImageTransform(bool redraw = true, bool syncWorldZoom = true)
    {
        ImageScaleTransform.ScaleX = imageZoom;
        ImageScaleTransform.ScaleY = imageZoom;
        ImageTranslateTransform.X = imagePanX;
        ImageTranslateTransform.Y = imagePanY;
        if (redraw)
        {
            RedrawMarkers();
            SyncWorldViewportToImage(syncWorldZoom);
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

    private void CenterWorldOnPoint(ControlPointViewModel point)
    {
        var map = WorldMapControl.Map;
        if (map is null)
        {
            return;
        }

        var (worldX, worldY) = SphericalMercator.FromLonLat(point.Longitude, point.Latitude);
        map.Navigator.CenterOn(worldX, worldY, 0, null!);
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
