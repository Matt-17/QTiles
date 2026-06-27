using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using QTiles.Core.Config;
using QTiles.Core.Imaging;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;
using QTiles.Core.Validation;
using QTiles.Editor.Wpf.Services;

namespace QTiles.Editor.Wpf.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly EditorProjectService projectService = new();
    private readonly ProjectValidator validator = new();
    private QTilesProject project = new();
    private string? projectPath;
    private string status = "Ready";
    private string editorMode = "Select";
    private string solveState = "Unsolved";
    private string rmsText = "n/a";
    private string maxErrorText = "n/a";
    private double renderPercent;
    private bool canRender;
    private ControlPointViewModel? selectedPoint;
    private ControlPointViewModel? pendingPoint;
    private string? pendingSide;
    private TransformSolveResult? currentSolveResult;
    private bool isApplyingErrors;
    private bool isRendering;
    private bool useSatelliteMap;
    private bool isPreviewEnabled;
    private string renderSummaryText = "No render yet";
    private CancellationTokenSource? renderCancellation;
    private readonly Dictionary<ControlPointViewModel, PropertyChangedEventHandler> pointHandlers = [];

    public MainWindowViewModel()
    {
        OpenCommand = new AsyncCommand(OpenAsync);
        SaveCommand = new AsyncCommand(SaveAsync);
        ValidateCommand = new RelayCommand(Validate);
        SolveCommand = new RelayCommand(Solve);
        RenderCommand = new AsyncCommand(RenderAsync);
        CancelRenderCommand = new RelayCommand(CancelRender);
        OpenOutputCommand = new RelayCommand(OpenOutputFolder);
        AddPointCommand = new RelayCommand(AddPoint);
        DeletePointCommand = new RelayCommand(DeleteSelectedPoint);
        ControlPoints.CollectionChanged += ControlPointsOnCollectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ControlPointViewModel> ControlPoints { get; } = [];
    public IReadOnlyList<string> RenderFormats { get; } = ["png", "jpg", "webp"];
    public IReadOnlyList<string> EditorModes { get; } =
    [
        "Select",
        "Add point: image first",
        "Add point: world first",
        "Move point",
        "Delete point",
        "Lock preview"
    ];

    public ControlPointViewModel? SelectedPoint
    {
        get => selectedPoint;
        set
        {
            selectedPoint = value;
            OnPropertyChanged();
        }
    }

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand SolveCommand { get; }
    public ICommand RenderCommand { get; }
    public ICommand CancelRenderCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand AddPointCommand { get; }
    public ICommand DeletePointCommand { get; }

    public bool UseSatelliteMap
    {
        get => useSatelliteMap;
        set
        {
            useSatelliteMap = value;
            OnPropertyChanged();
        }
    }

    public bool IsPreviewEnabled
    {
        get => isPreviewEnabled;
        set
        {
            isPreviewEnabled = value;
            OnPropertyChanged();
        }
    }

    public double PreviewOpacity => project.Editor.Preview.Opacity;

    public int TileSize => project.Render.TileSize;

    public string EditorMode
    {
        get => editorMode;
        set
        {
            editorMode = value;
            pendingPoint = null;
            pendingSide = null;
            OnPropertyChanged();
        }
    }

    public string ProjectName
    {
        get => project.Project.Name;
        set
        {
            project.Project.Name = value;
            OnPropertyChanged();
        }
    }

    public string SourceImage
    {
        get => project.Source.Image;
        set
        {
            project.Source.Image = value;
            OnPropertyChanged();
        }
    }

    public int MinZoom
    {
        get => project.Render.MinZoom;
        set
        {
            project.Render.MinZoom = value;
            OnPropertyChanged();
        }
    }

    public int MaxZoom
    {
        get => project.Render.MaxZoom;
        set
        {
            project.Render.MaxZoom = value;
            OnPropertyChanged();
        }
    }

    public string Format
    {
        get => project.Render.Format;
        set
        {
            project.Render.Format = value;
            OnPropertyChanged();
        }
    }

    public string OutputDirectory
    {
        get => project.Output.Directory;
        set
        {
            project.Output.Directory = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => status;
        set
        {
            status = value;
            OnPropertyChanged();
        }
    }

    public string SolveState
    {
        get => solveState;
        set
        {
            solveState = value;
            OnPropertyChanged();
        }
    }

    public string RmsText
    {
        get => rmsText;
        set
        {
            rmsText = value;
            OnPropertyChanged();
        }
    }

    public string MaxErrorText
    {
        get => maxErrorText;
        set
        {
            maxErrorText = value;
            OnPropertyChanged();
        }
    }

    public double RenderPercent
    {
        get => renderPercent;
        set
        {
            renderPercent = value;
            OnPropertyChanged();
        }
    }

    public bool CanRender
    {
        get => canRender;
        set
        {
            canRender = value;
            OnPropertyChanged();
        }
    }

    public bool IsRendering
    {
        get => isRendering;
        set
        {
            isRendering = value;
            OnPropertyChanged();
        }
    }

    public string RenderSummaryText
    {
        get => renderSummaryText;
        set
        {
            renderSummaryText = value;
            OnPropertyChanged();
        }
    }

    public TransformSolveResult? CurrentSolveResult
    {
        get => currentSolveResult;
        private set
        {
            currentSolveResult = value;
            OnPropertyChanged();
        }
    }

    public async Task LoadStartupProjectAsync(string[] args)
    {
        if (args.FirstOrDefault() is { Length: > 0 } path && File.Exists(path))
        {
            await LoadAsync(path);
        }
    }

    public string ResolveSourceImagePath() => ProjectPaths.Resolve(project, project.Source.Image);

    public string ResolveOutputDirectory() => ProjectPaths.Resolve(project, project.Output.Directory);

    public void HandleImagePaneClick(double imageX, double imageY)
    {
        if (EditorMode == "Delete point")
        {
            return;
        }

        if (pendingPoint is not null && pendingSide == "world")
        {
            pendingPoint.ImageX = imageX;
            pendingPoint.ImageY = imageY;
            pendingPoint = null;
            pendingSide = null;
            EditorMode = "Move point";
            Status = "Paired world point with image point";
            ValidateAndSolve();
            return;
        }

        var point = CreatePoint();
        point.ImageX = imageX;
        point.ImageY = imageY;
        ControlPoints.Add(point);
        SelectedPoint = point;

        if (EditorMode == "Add point: image first")
        {
            pendingPoint = point;
            pendingSide = "image";
            Status = "Click the world map to finish the pair";
        }
        else
        {
            Status = "Added image-side control point";
            ValidateAndSolve();
        }
    }

    public void HandleWorldPaneClick(double lon, double lat)
    {
        if (EditorMode == "Delete point")
        {
            return;
        }

        if (pendingPoint is not null && pendingSide == "image")
        {
            pendingPoint.Longitude = lon;
            pendingPoint.Latitude = lat;
            pendingPoint = null;
            pendingSide = null;
            EditorMode = "Move point";
            Status = "Paired image point with world point";
            ValidateAndSolve();
            return;
        }

        var point = CreatePoint();
        point.Longitude = lon;
        point.Latitude = lat;
        ControlPoints.Add(point);
        SelectedPoint = point;

        if (EditorMode == "Add point: world first")
        {
            pendingPoint = point;
            pendingSide = "world";
            Status = "Click the source image to finish the pair";
        }
        else
        {
            Status = "Added world-side control point";
            ValidateAndSolve();
        }
    }

    public void MoveImagePoint(ControlPointViewModel point, double imageX, double imageY)
    {
        point.ImageX = imageX;
        point.ImageY = imageY;
        SelectedPoint = point;
        ValidateAndSolve();
    }

    public void MoveWorldPoint(ControlPointViewModel point, double lon, double lat)
    {
        point.Longitude = lon;
        point.Latitude = lat;
        SelectedPoint = point;
        ValidateAndSolve();
    }

    public void DeletePoint(ControlPointViewModel point)
    {
        ControlPoints.Remove(point);
        if (SelectedPoint == point)
        {
            SelectedPoint = null;
        }

        Status = $"Deleted point {point.Id}";
        ValidateAndSolve();
    }

    private async Task OpenAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "QTiles YAML (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }

    private async Task LoadAsync(string path)
    {
        project = await projectService.OpenAsync(path);
        projectPath = path;
        RefreshFromProject();
        Status = $"Opened {Path.GetFileName(path)}";
        Validate();
        Solve();
    }

    private async Task SaveAsync()
    {
        if (projectPath is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "QTiles YAML (*.yaml)|*.yaml" };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            projectPath = dialog.FileName;
        }

        SyncToProject();
        await projectService.SaveAsync(project, projectPath);
        Status = $"Saved {Path.GetFileName(projectPath)}";
    }

    private void Validate()
    {
        SyncToProject();
        var messages = validator.Validate(project, projectPath);
        CanRender = !ProjectValidator.HasErrors(messages);
        var errors = messages.Count(m => m.Severity == ValidationSeverity.Error);
        var warnings = messages.Count(m => m.Severity == ValidationSeverity.Warning);
        Status = errors == 0 ? $"Valid with {warnings} warning(s)" : $"{errors} error(s), {warnings} warning(s)";
        SolveState = errors == 0 ? "Ready to solve" : "Validation errors";
    }

    private void Solve()
    {
        SyncToProject();
        try
        {
            var image = new NetVipsImageInfoReader().Read(ProjectPaths.Resolve(project, project.Source.Image));
            var result = new TransformSolver().Solve(project, image.Width, image.Height);
            CurrentSolveResult = result;
            ApplyErrors(result);
            CanRender = result.TransformType == "affine" && project.Georeference.ControlPoints.Count(p => p.Enabled) >= 3;
            RmsText = $"{result.RmsPixelsAtMaxZoom:0.##} px";
            MaxErrorText = $"{result.MaxPixelsAtMaxZoom:0.##} px";
            SolveState = result.TransformType == "similarity" ? "2-point similarity preview" : "Affine solved";
            Status = result.TransformType == "similarity"
                ? "2-point similarity preview"
                : $"Affine solved, RMS {result.RmsPixelsAtMaxZoom:0.##} px, max {result.MaxPixelsAtMaxZoom:0.##} px";
        }
        catch (Exception ex)
        {
            CurrentSolveResult = null;
            CanRender = false;
            SolveState = "Unsolved";
            RmsText = "n/a";
            MaxErrorText = "n/a";
            Status = ex.Message;
        }
    }

    private async Task RenderAsync()
    {
        SyncToProject();
        IsRendering = true;
        RenderPercent = 0;
        renderCancellation = new CancellationTokenSource();
        var progress = new Progress<TileRenderProgress>(p =>
        {
            RenderPercent = p.Percent;
            Status = $"Rendering zoom {p.Zoom}: {p.CompletedTiles}/{p.TotalTiles}";
        });
        try
        {
            var summary = await new RenderJobService().RenderAsync(project, progress, renderCancellation.Token);
            RenderSummaryText = $"{summary.TilesWritten} written, {summary.TilesSkipped} skipped in {summary.Duration.TotalSeconds:0.0}s";
            Status = $"Render complete: {summary.TilesWritten} written, {summary.TilesSkipped} skipped";
        }
        catch (OperationCanceledException)
        {
            RenderSummaryText = "Render cancelled";
            Status = "Render cancelled";
        }
        finally
        {
            IsRendering = false;
            renderCancellation.Dispose();
            renderCancellation = null;
        }
    }

    private void CancelRender() => renderCancellation?.Cancel();

    private void OpenOutputFolder()
    {
        var path = ProjectPaths.Resolve(project, project.Output.Directory);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AddPoint()
    {
        var nextId = ControlPoints.Select(p => int.TryParse(p.Id, out var id) ? id : 0).DefaultIfEmpty().Max() + 1;
        ControlPoints.Add(new ControlPointViewModel
        {
            Id = nextId.ToString(),
            Name = "New point",
            Enabled = true
        });
        Status = "Added control point";
    }

    private void DeleteSelectedPoint()
    {
        if (SelectedPoint is not null)
        {
            ControlPoints.Remove(SelectedPoint);
            SelectedPoint = null;
            Status = "Deleted control point";
            ValidateAndSolve();
        }
    }

    private void RefreshFromProject()
    {
        ControlPoints.Clear();
        foreach (var point in project.Georeference.ControlPoints)
        {
            ControlPoints.Add(ControlPointViewModel.FromConfig(point));
        }

        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(MinZoom));
        OnPropertyChanged(nameof(MaxZoom));
        OnPropertyChanged(nameof(Format));
        OnPropertyChanged(nameof(OutputDirectory));
        OnPropertyChanged(nameof(PreviewOpacity));
        OnPropertyChanged(nameof(TileSize));
    }

    private void SyncToProject()
    {
        project.Georeference.ControlPoints = ControlPoints.Select(p => p.ToConfig()).ToList();
    }

    private void ApplyErrors(TransformSolveResult result)
    {
        isApplyingErrors = true;
        foreach (var error in result.Errors)
        {
            var point = ControlPoints.FirstOrDefault(p => p.Id == error.Id.Value);
            if (point is not null)
            {
                point.ErrorPixels = error.PixelsAtMaxZoom;
                point.ErrorMeters = error.EstimatedMeters;
            }
        }

        isApplyingErrors = false;
    }

    private ControlPointViewModel CreatePoint()
    {
        var nextId = ControlPoints.Select(p => int.TryParse(p.Id, out var id) ? id : 0).DefaultIfEmpty().Max() + 1;
        return new ControlPointViewModel
        {
            Id = nextId.ToString(),
            Name = $"Point {nextId}",
            Enabled = true
        };
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
                SubscribePoint(point);
            }
        }
    }

    private void SubscribePoint(ControlPointViewModel point)
    {
        if (pointHandlers.ContainsKey(point))
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (isApplyingErrors || e.PropertyName is nameof(ControlPointViewModel.ErrorPixels) or nameof(ControlPointViewModel.ErrorMeters))
            {
                return;
            }

            ValidateAndSolve();
        };

        point.PropertyChanged += handler;
        pointHandlers[point] = handler;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void ValidateAndSolve()
    {
        Validate();
        Solve();
    }
}
