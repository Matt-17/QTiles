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
    private string solveFeedbackText = "Add control points to solve the transform.";
    private string solveFeedbackBrush = "#AAB3C1";
    private string rmsText = "n/a";
    private string maxErrorText = "n/a";
    private double renderPercent;
    private bool canRender;
    private ControlPointViewModel? selectedPoint;
    private ControlPointViewModel? pendingPoint;
    private string? pendingSide;
    private TransformSolveResult? currentSolveResult;
    private bool isApplyingErrors;
    private bool isApplyingAutoZoomRange;
    private bool useAutoZoomRange = true;
    private bool isRendering;
    private bool hasUnsavedChanges;
    private bool suppressDirtyTracking;
    private bool useSatelliteMap;
    private bool isPreviewEnabled;
    private bool isViewSyncEnabled = true;
    private string renderSummaryText = "No render yet";
    private CancellationTokenSource? renderCancellation;
    private readonly RelayCommand cancelRenderCommand;
    private readonly Dictionary<ControlPointViewModel, PropertyChangedEventHandler> pointHandlers = [];
    private const double ReviewErrorPixels = 2.0;
    private const double HighErrorPixels = 8.0;
    private const string SolveGoodBrush = "#3BAA8D";
    private const string SolveReviewBrush = "#E0AD44";
    private const string SolveErrorBrush = "#EF6461";
    private const string SolveNeutralBrush = "#AAB3C1";
    private const string ProjectFilter = "QTiles project or image (*.yaml;*.yml;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp)|*.yaml;*.yml;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp|QTiles YAML (*.yaml;*.yml)|*.yaml;*.yml|Image files (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp|All files (*.*)|*.*";
    private const string ImageFilter = "Image files (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp|All files (*.*)|*.*";

    public MainWindowViewModel()
    {
        OpenCommand = new AsyncCommand(OpenAsync);
        SaveCommand = new AsyncCommand(SaveAsync);
        ChooseSourceImageCommand = new RelayCommand(ChooseSourceImage);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory);
        ValidateCommand = new RelayCommand(Validate);
        SolveCommand = new RelayCommand(Solve);
        RenderCommand = new AsyncCommand(RenderAsync);
        cancelRenderCommand = new RelayCommand(CancelRender, () => IsRendering);
        CancelRenderCommand = cancelRenderCommand;
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

    public bool HasUnsavedChanges
    {
        get => hasUnsavedChanges;
        private set
        {
            if (hasUnsavedChanges == value)
            {
                return;
            }

            hasUnsavedChanges = value;
            OnPropertyChanged();
        }
    }

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ChooseSourceImageCommand { get; }
    public ICommand ChooseOutputDirectoryCommand { get; }
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
            if (project.Project.Name == value)
            {
                return;
            }

            project.Project.Name = value;
            MarkDirty();
            OnPropertyChanged();
        }
    }

    public bool IsViewSyncEnabled
    {
        get => isViewSyncEnabled;
        set
        {
            isViewSyncEnabled = value;
            OnPropertyChanged();
        }
    }

    public string SourceImage
    {
        get => project.Source.Image;
        set
        {
            if (project.Source.Image == value)
            {
                return;
            }

            project.Source.Image = value;
            MarkDirty();
            OnPropertyChanged();
        }
    }

    public int MinZoom
    {
        get => project.Render.MinZoom;
        set
        {
            if (project.Render.MinZoom == value)
            {
                return;
            }

            project.Render.MinZoom = value;
            if (!isApplyingAutoZoomRange)
            {
                project.Render.AutoZoom = false;
                useAutoZoomRange = false;
            }

            MarkDirty();
            OnPropertyChanged();
        }
    }

    public int MaxZoom
    {
        get => project.Render.MaxZoom;
        set
        {
            if (project.Render.MaxZoom == value)
            {
                return;
            }

            project.Render.MaxZoom = value;
            if (!isApplyingAutoZoomRange)
            {
                project.Render.AutoZoom = false;
                useAutoZoomRange = false;
            }

            MarkDirty();
            OnPropertyChanged();
        }
    }

    public string Format
    {
        get => project.Render.Format;
        set
        {
            if (project.Render.Format == value)
            {
                return;
            }

            project.Render.Format = value;
            MarkDirty();
            OnPropertyChanged();
        }
    }

    public string OutputDirectory
    {
        get => project.Output.Directory;
        set
        {
            if (project.Output.Directory == value)
            {
                return;
            }

            project.Output.Directory = value;
            MarkDirty();
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
            OnPropertyChanged(nameof(IsNotRendering));
            cancelRenderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotRendering => !isRendering;

    public string SolveFeedbackText
    {
        get => solveFeedbackText;
        set
        {
            solveFeedbackText = value;
            OnPropertyChanged();
        }
    }

    public string SolveFeedbackBrush
    {
        get => solveFeedbackBrush;
        set
        {
            solveFeedbackBrush = value;
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

    public void HandleWorldPaneClick(double lon, double lat, double? imageX = null, double? imageY = null)
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
        if (imageX.HasValue && imageY.HasValue)
        {
            point.ImageX = imageX.Value;
            point.ImageY = imageY.Value;
        }

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

    public void SetPointLocked(ControlPointViewModel point, bool locked)
    {
        point.IsLocked = locked;
        SelectedPoint = point;
        Status = locked ? $"Locked point {point.Id}" : $"Unlocked point {point.Id}";
    }

    private async Task OpenAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Filter = ProjectFilter,
            Title = "Open QTiles project or source image"
        };

        if (dialog.ShowDialog() == true)
        {
            if (IsProjectFile(dialog.FileName))
            {
                await LoadAsync(dialog.FileName);
                return;
            }

            SetSourceImage(dialog.FileName);
        }
    }

    private async Task LoadAsync(string path)
    {
        suppressDirtyTracking = true;
        try
        {
            project = await projectService.OpenAsync(path);
            projectPath = path;
            RefreshFromProject();
            Status = $"Opened {Path.GetFileName(path)}";
            Validate();
            Solve();
        }
        finally
        {
            suppressDirtyTracking = false;
        }

        MarkClean();
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

        project.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        RebaseSourceImagePathForProject();
        SyncToProject();
        await projectService.SaveAsync(project, projectPath);
        MarkClean();
        Status = $"Saved {Path.GetFileName(projectPath)}";
    }

    private void ChooseSourceImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Filter = ImageFilter,
            Title = "Choose source image"
        };

        var currentPath = ResolveSourceImagePath();
        if (File.Exists(currentPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
        }

        if (dialog.ShowDialog() == true)
        {
            SetSourceImage(dialog.FileName);
        }
    }

    private void ChooseOutputDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose output directory"
        };

        var currentPath = ResolveOutputDirectory();
        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }

        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = CreateProjectPathForOutputDirectory(dialog.FolderName);
            Validate();
            if (CanAttemptSolve())
            {
                Solve();
            }

            Status = $"Output directory set: {OutputDirectory}";
        }
    }

    private void Validate()
    {
        SyncToProject();
        var messages = validator.Validate(project, projectPath);
        CanRender = !ProjectValidator.HasErrors(messages);
        var errors = messages.Count(m => m.Severity == ValidationSeverity.Error);
        var warnings = messages.Count(m => m.Severity == ValidationSeverity.Warning);
        Status = errors == 0 ? $"Valid with {warnings} warning(s)" : $"{errors} error(s), {warnings} warning(s)";
        SetSolveFeedback(
            errors == 0 ? "Ready to solve" : "Validation errors",
            errors == 0
                ? "Validation is clear. Solve feedback will show residual detail here."
                : "Fix validation errors before solving.",
            errors == 0 ? SolveGoodBrush : SolveErrorBrush);
    }

    private void Solve()
    {
        SyncToProject();
        try
        {
            var image = new NetVipsImageInfoReader().Read(ProjectPaths.Resolve(project, project.Source.Image));
            var solver = new TransformSolver();
            var initialResult = solver.Solve(project, image.Width, image.Height);
            var zoomRange = ZoomRangeCalculator.Resolve(project.Render, initialResult, image.Width, image.Height);
            if (useAutoZoomRange || ZoomRangeCalculator.UsesAutoZoom(project.Render))
            {
                ApplyAutoZoomRange(zoomRange);
            }

            var result = solver.Solve(project, image.Width, image.Height, zoomRange.MaxZoom);
            CurrentSolveResult = result;
            ApplyErrors(result);
            CanRender = result.TransformType == "affine" && project.Georeference.ControlPoints.Count(p => p.Enabled) >= 3;
            RmsText = $"{result.RmsPixelsAtMaxZoom:0.##} px";
            MaxErrorText = $"{result.MaxPixelsAtMaxZoom:0.##} px";
            UpdateSolveFeedback(result, zoomRange);
            var zoomText = useAutoZoomRange ? $" zoom {zoomRange.MinZoom}..{zoomRange.MaxZoom}" : "";
            Status = result.TransformType == "similarity"
                ? $"2-point similarity preview{zoomText}"
                : $"{SolveState}{zoomText}, RMS {result.RmsPixelsAtMaxZoom:0.##} px, max {result.MaxPixelsAtMaxZoom:0.##} px";
        }
        catch (Exception ex)
        {
            CurrentSolveResult = null;
            CanRender = false;
            ClearErrors();
            SetSolveFeedback("Unsolved", ex.Message, SolveErrorBrush);
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

    private void CancelRender()
    {
        if (renderCancellation is null)
        {
            return;
        }

        Status = "Cancelling render...";
        renderCancellation.Cancel();
    }

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
        useAutoZoomRange = ZoomRangeCalculator.UsesAutoZoom(project.Render);
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

    private void ApplyAutoZoomRange(ZoomRangeRecommendation zoomRange)
    {
        useAutoZoomRange = true;
        project.Render.AutoZoom = true;
        isApplyingAutoZoomRange = true;
        try
        {
            MinZoom = zoomRange.MinZoom;
            MaxZoom = zoomRange.MaxZoom;
        }
        finally
        {
            isApplyingAutoZoomRange = false;
        }
    }

    private void ResetAutoZoomRange()
    {
        useAutoZoomRange = true;
        project.Render.AutoZoom = true;
        isApplyingAutoZoomRange = true;
        try
        {
            MinZoom = 0;
            MaxZoom = 0;
        }
        finally
        {
            isApplyingAutoZoomRange = false;
        }
    }

    private void SyncToProject()
    {
        project.Georeference.ControlPoints = ControlPoints.Select(p => p.ToConfig()).ToList();
    }

    private void ApplyErrors(TransformSolveResult result)
    {
        isApplyingErrors = true;
        try
        {
            ClearErrors();
            foreach (var error in result.Errors)
            {
                var point = ControlPoints.FirstOrDefault(p => p.Id == error.Id.Value);
                if (point is not null)
                {
                    point.ErrorPixels = error.PixelsAtMaxZoom;
                    point.ErrorMeters = error.EstimatedMeters;
                }
            }
        }
        finally
        {
            isApplyingErrors = false;
        }
    }

    private void ClearErrors()
    {
        foreach (var point in ControlPoints)
        {
            point.ErrorPixels = 0;
            point.ErrorMeters = 0;
        }
    }

    private void UpdateSolveFeedback(TransformSolveResult result, ZoomRangeRecommendation zoomRange)
    {
        var enabledPointCount = project.Georeference.ControlPoints.Count(p => p.Enabled);
        var zoomText = useAutoZoomRange ? $" Zoom {zoomRange.MinZoom}..{zoomRange.MaxZoom}." : "";
        if (result.TransformType == "similarity")
        {
            SetSolveFeedback(
                "2-point similarity preview",
                $"Two enabled points can align scale and rotation, but affine rendering still needs three enabled points.{zoomText}",
                SolveReviewBrush);
            return;
        }

        var highErrors = result.Errors
            .Where(error => error.PixelsAtMaxZoom > HighErrorPixels)
            .OrderByDescending(error => error.PixelsAtMaxZoom)
            .ToList();
        if (highErrors.Count > 0)
        {
            SetSolveFeedback("Affine needs marker review", FormatResidualIssue(highErrors, "over 8 px", zoomText), SolveErrorBrush);
            return;
        }

        var reviewErrors = result.Errors
            .Where(error => error.PixelsAtMaxZoom > ReviewErrorPixels)
            .OrderByDescending(error => error.PixelsAtMaxZoom)
            .ToList();
        if (reviewErrors.Count > 0)
        {
            SetSolveFeedback("Affine solved with residuals", FormatResidualIssue(reviewErrors, "over 2 px", zoomText), SolveReviewBrush);
            return;
        }

        SetSolveFeedback(
            "Affine solved cleanly",
            $"{enabledPointCount} enabled points, RMS {result.RmsPixelsAtMaxZoom:0.##} px, max {result.MaxPixelsAtMaxZoom:0.##} px.{zoomText}",
            SolveGoodBrush);
    }

    private static string FormatResidualIssue(IReadOnlyList<ControlPointError> errors, string thresholdText, string zoomText)
    {
        var worst = errors[0];
        var pointText = errors.Count == 1
            ? $"Point {worst.Id.Value} is {thresholdText}"
            : $"{errors.Count} points are {thresholdText}: {string.Join(", ", errors.Take(4).Select(error => error.Id.Value))}";
        return $"{pointText}; worst is {worst.Id.Value} at {worst.PixelsAtMaxZoom:0.##} px / {worst.EstimatedMeters:0.##} m. Check that marker's image and world positions match.{zoomText}";
    }

    private void SetSolveFeedback(string state, string feedback, string brush)
    {
        SolveState = state;
        SolveFeedbackText = feedback;
        SolveFeedbackBrush = brush;
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
        MarkDirty();

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
            if (isApplyingErrors
                || e.PropertyName is nameof(ControlPointViewModel.ErrorPixels)
                    or nameof(ControlPointViewModel.ErrorMeters))
            {
                return;
            }

            MarkDirty();
            if (e.PropertyName is nameof(ControlPointViewModel.IsLocked))
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

    private void MarkDirty()
    {
        if (!suppressDirtyTracking)
        {
            HasUnsavedChanges = true;
        }
    }

    private void MarkClean() => HasUnsavedChanges = false;

    public void SetSourceImage(string imagePath)
    {
        if (!IsSupportedSourceImageFile(imagePath))
        {
            Status = $"Unsupported source image: {Path.GetFileName(imagePath)}";
            return;
        }

        SourceImage = CreateProjectPathForSourceImage(imagePath);
        ResetAutoZoomRange();
        if (string.IsNullOrWhiteSpace(ProjectName) || ProjectName == "QTiles project")
        {
            ProjectName = Path.GetFileNameWithoutExtension(imagePath);
        }

        CurrentSolveResult = null;
        RmsText = "n/a";
        MaxErrorText = "n/a";
        Validate();
        if (CanAttemptSolve())
        {
            Solve();
        }
        else
        {
            CanRender = false;
            SetSolveFeedback("Add control points", "Pair enough image and world markers to solve the transform.", SolveNeutralBrush);
        }

        Status = $"Source image set: {Path.GetFileName(imagePath)}";
    }

    private bool CanAttemptSolve()
    {
        var enabledPoints = ControlPoints.Count(p => p.Enabled);
        var transformType = project.Georeference.Transform.Type.Trim().ToLowerInvariant();
        return transformType == "similarity" ? enabledPoints >= 2 : enabledPoints >= 3;
    }

    private string CreateProjectPathForSourceImage(string imagePath)
        => CreateProjectPathForProject(imagePath);

    private string CreateProjectPathForOutputDirectory(string directoryPath)
        => CreateProjectPathForProject(directoryPath);

    private string CreateProjectPathForProject(string path)
    {
        var directory = ProjectDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return path;
        }

        try
        {
            var relative = Path.GetRelativePath(directory, path);
            return NormalizeProjectPath(relative);
        }
        catch
        {
            return path;
        }
    }

    private void RebaseSourceImagePathForProject()
    {
        if (projectPath is null || string.IsNullOrWhiteSpace(project.Source.Image))
        {
            return;
        }

        var sourcePath = ResolveSourceImagePath();
        if (!Path.IsPathRooted(sourcePath))
        {
            return;
        }

        SourceImage = CreateProjectPathForSourceImage(sourcePath);
    }

    private string? ProjectDirectory => projectPath is { Length: > 0 }
        ? Path.GetDirectoryName(Path.GetFullPath(projectPath))
        : project.BaseDirectory;

    private static bool IsProjectFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedSourceImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProjectPath(string path)
    {
        var isRooted = Path.IsPathRooted(path);
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        if (!isRooted
            && !normalized.StartsWith("./", StringComparison.Ordinal)
            && !normalized.StartsWith("../", StringComparison.Ordinal))
        {
            normalized = $"./{normalized}";
        }

        return normalized;
    }
}
