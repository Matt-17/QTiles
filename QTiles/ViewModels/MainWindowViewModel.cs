using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using QTiles.Core.Config;
using QTiles.Core.Imaging;
using QTiles.Core.Rendering;
using QTiles.Core.Transforms;
using QTiles.Core.Validation;
using QTiles.Services;

namespace QTiles.ViewModels;

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
    private ProjectSourceViewModel? selectedSource;
    private ProjectRenderPlan? currentPreviewPlan;
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
    private bool usesExplicitSources;
    private bool isLoadingControlPoints;
    private string renderSummaryText = "No render yet";
    private CancellationTokenSource? renderCancellation;
    private EditorSettings editorSettings = new();
    private Action? editorSettingsChanged;
    private readonly RelayCommand cancelRenderCommand;
    private readonly RelayCommand deletePointCommand;
    private readonly RelayCommand removeSourceCommand;
    private readonly Dictionary<ControlPointViewModel, PropertyChangedEventHandler> pointHandlers = [];
    private readonly Dictionary<ProjectSourceViewModel, PropertyChangedEventHandler> sourceHandlers = [];
    private const int MaxRecentItems = 8;
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
        AddSourceCommand = new RelayCommand(AddSource);
        removeSourceCommand = new RelayCommand(RemoveSelectedSource, () => CanRemoveSelectedSource);
        RemoveSourceCommand = removeSourceCommand;
        OpenRecentProjectCommand = new RelayCommand<string>(path => _ = OpenRecentProjectAsync(path ?? string.Empty), HasPath);
        OpenRecentImageCommand = new RelayCommand<string>(path => OpenRecentImage(path ?? string.Empty), HasPath);
        OpenRecentOutputDirectoryCommand = new RelayCommand<string>(path => OpenRecentOutputDirectory(path ?? string.Empty), HasPath);
        ChooseOutputDirectoryCommand = new RelayCommand(ChooseOutputDirectory);
        ValidateCommand = new RelayCommand(Validate);
        SolveCommand = new RelayCommand(Solve);
        RenderCommand = new AsyncCommand(RenderAsync);
        WriteTileJsonCommand = new AsyncCommand(WriteTileJsonOnlyAsync);
        cancelRenderCommand = new RelayCommand(CancelRender, () => IsRendering);
        CancelRenderCommand = cancelRenderCommand;
        OpenOutputCommand = new RelayCommand(OpenOutputFolder);
        AddPointCommand = new RelayCommand(AddPoint);
        deletePointCommand = new RelayCommand(DeleteSelectedPoint, () => HasSelectedPoint);
        DeletePointCommand = deletePointCommand;
        ControlPoints.CollectionChanged += ControlPointsOnCollectionChanged;
        Sources.CollectionChanged += SourcesOnCollectionChanged;
    }

    public void UseEditorSettings(EditorSettings settings, Action? settingsChanged = null)
    {
        editorSettings = settings;
        editorSettings.LastOpenDirectory ??= string.Empty;
        editorSettings.RecentProjects ??= [];
        editorSettings.RecentImages ??= [];
        editorSettings.RecentOutputDirectories ??= [];
        editorSettingsChanged = settingsChanged;
        RefreshRecentFiles();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ControlPointViewModel> ControlPoints { get; } = [];
    public ObservableCollection<ProjectSourceViewModel> Sources { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentProjects { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentImages { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentOutputDirectories { get; } = [];
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
            if (ReferenceEquals(selectedPoint, value))
            {
                return;
            }

            selectedPoint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedPoint));
            deletePointCommand.RaiseCanExecuteChanged();
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

    public bool HasSelectedPoint => SelectedPoint is not null;

    public bool HasRecentProjects => RecentProjects.Count > 0;

    public bool HasRecentImages => RecentImages.Count > 0;

    public bool HasRecentOutputDirectories => RecentOutputDirectories.Count > 0;

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ChooseSourceImageCommand { get; }
    public ICommand AddSourceCommand { get; }
    public ICommand RemoveSourceCommand { get; }
    public ICommand OpenRecentProjectCommand { get; }
    public ICommand OpenRecentImageCommand { get; }
    public ICommand OpenRecentOutputDirectoryCommand { get; }
    public ICommand ChooseOutputDirectoryCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand SolveCommand { get; }
    public ICommand RenderCommand { get; }
    public ICommand WriteTileJsonCommand { get; }
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
            if (value && !CanPreview)
            {
                value = false;
            }

            if (isPreviewEnabled == value)
            {
                return;
            }

            isPreviewEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool CanPreview => CurrentPreviewPlan is { Sources.Count: > 0 };

    public double PreviewOpacity => project.Editor.Preview.Opacity;

    public bool LimitPreviewTilesToZoomLevel
    {
        get => project.Editor.Preview.LimitTilesToZoomLevel;
        set
        {
            if (project.Editor.Preview.LimitTilesToZoomLevel == value)
            {
                return;
            }

            project.Editor.Preview.LimitTilesToZoomLevel = value;
            MarkDirty();
            OnPropertyChanged();
        }
    }

    public int TileSize => project.Render.TileSize;

    public string RenderOptionsSummary =>
        $"{RenderResampling.DisplayName(project.Render.Resampling)}, {project.Render.TileSize}px, q{project.Render.Quality}";

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

    public ProjectSourceViewModel? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (ReferenceEquals(selectedSource, value))
            {
                return;
            }

            SyncSelectedSourceControlPoints();
            selectedSource = value;
            pendingPoint = null;
            pendingSide = null;
            SelectedPoint = null;
            LoadControlPointsFromSelectedSource();
            CurrentSolveResult = null;
            ClearErrors();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceImage));
            OnPropertyChanged(nameof(SelectedSourceName));
            OnPropertyChanged(nameof(CanRemoveSelectedSource));
            OnPropertyChanged(nameof(CanPreview));
            removeSourceCommand.RaiseCanExecuteChanged();
            DisablePreviewIfUnavailable();
            if (selectedSource is not null)
            {
                Validate();
                if (CanAttemptSolve())
                {
                    Solve();
                }
            }
        }
    }

    public string SelectedSourceName => SelectedSource?.DisplayName ?? "Image";

    public bool CanRemoveSelectedSource => SelectedSource is not null && Sources.Count > 1;

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
        get => SelectedSource?.Image ?? project.Source.Image;
        set
        {
            var source = EnsureSelectedSource();
            if (source.Image == value)
            {
                return;
            }

            source.Image = value;
            if (!usesExplicitSources)
            {
                project.Source.Image = value;
            }

            MarkDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPreview));
            DisablePreviewIfUnavailable();
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
            OnPropertyChanged(nameof(RenderOptionsSummary));
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
            // TileJSON follows the output directory unless a project pins an explicit path.
            project.Output.TileJsonPath = string.Empty;
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

    public string RootStatusText => CurrentRootDirectory is { Length: > 0 } root
        ? $"Root: {root}"
        : "Root: (not set)";

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
            OnPropertyChanged(nameof(CanPreview));
            DisablePreviewIfUnavailable();
        }
    }

    public ProjectRenderPlan? CurrentPreviewPlan
    {
        get => currentPreviewPlan;
        private set
        {
            if (ReferenceEquals(currentPreviewPlan, value))
            {
                return;
            }

            currentPreviewPlan = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPreview));
            DisablePreviewIfUnavailable();
        }
    }

    public async Task LoadStartupProjectAsync(string[] args)
    {
        if (args.FirstOrDefault() is { Length: > 0 } path && File.Exists(path))
        {
            await LoadAsync(path);
        }
    }

    public string ResolveSourceImagePath() => ResolveProjectPath(SourceImage);

    public string ResolveOutputDirectory() => ResolveProjectPath(project.Output.Directory);

    public RenderOptionsSnapshot CreateRenderOptionsSnapshot() => new(
        project.Render.TileSize,
        project.Render.Format,
        project.Render.Quality,
        RenderResampling.Normalize(project.Render.Resampling),
        project.Render.Background,
        project.Render.SkipEmptyTiles,
        project.Render.Overwrite,
        project.Render.ClearOutputDirectory,
        project.Output.TileJson);

    public void ApplyRenderOptions(RenderOptionsSnapshot options)
    {
        var normalizedResampling = RenderResampling.Normalize(options.Resampling);
        var changed = false;

        if (project.Render.TileSize != options.TileSize)
        {
            project.Render.TileSize = options.TileSize;
            changed = true;
            OnPropertyChanged(nameof(TileSize));
        }

        if (!string.Equals(project.Render.Format, options.Format, StringComparison.OrdinalIgnoreCase))
        {
            project.Render.Format = options.Format;
            changed = true;
            OnPropertyChanged(nameof(Format));
        }

        if (project.Render.Quality != options.Quality)
        {
            project.Render.Quality = options.Quality;
            changed = true;
        }

        if (!string.Equals(project.Render.Resampling, normalizedResampling, StringComparison.OrdinalIgnoreCase))
        {
            project.Render.Resampling = normalizedResampling;
            changed = true;
        }

        if (!string.Equals(project.Render.Background, options.Background, StringComparison.OrdinalIgnoreCase))
        {
            project.Render.Background = options.Background;
            changed = true;
        }

        if (project.Render.SkipEmptyTiles != options.SkipEmptyTiles)
        {
            project.Render.SkipEmptyTiles = options.SkipEmptyTiles;
            changed = true;
        }

        if (project.Render.Overwrite != options.Overwrite)
        {
            project.Render.Overwrite = options.Overwrite;
            changed = true;
        }

        if (project.Render.ClearOutputDirectory != options.ClearOutputDirectory)
        {
            project.Render.ClearOutputDirectory = options.ClearOutputDirectory;
            changed = true;
        }

        if (project.Output.TileJson != options.WriteTileJson)
        {
            project.Output.TileJson = options.WriteTileJson;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        MarkDirty();
        OnPropertyChanged(nameof(RenderOptionsSummary));
        Validate();
        if (CanAttemptSolve())
        {
            Solve();
        }

        Status = $"Render options: {RenderResampling.DisplayName(normalizedResampling)}, q{options.Quality}";
    }

    public void HandleImagePaneClick(double imageX, double imageY, double? lon = null, double? lat = null)
    {
        if (IsRendering || EditorMode == "Delete point")
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
        if (lon.HasValue && lat.HasValue)
        {
            point.Longitude = lon.Value;
            point.Latitude = lat.Value;
        }

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
        if (IsRendering || EditorMode == "Delete point")
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
            Status = "Click the selected image to finish the pair";
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
        if (IsRendering)
        {
            return;
        }

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
            Title = "Open QTiles project or image"
        };
        ApplyInitialOpenDirectory(dialog);

        if (dialog.ShowDialog() == true)
        {
            RememberOpenDirectory(dialog.FileName);
            if (IsProjectFile(dialog.FileName))
            {
                await OpenProjectAsync(dialog.FileName);
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
            RememberOpenDirectory(path);
            projectPath = path;
            AddRecentProject(path);
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
        await SaveProjectAsync();
    }

    public Task<bool> SaveProjectForCloseAsync() => SaveProjectAsync();

    private async Task<bool> SaveProjectAsync()
    {
        if (projectPath is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "QTiles YAML (*.yaml)|*.yaml",
                FileName = "qtiles.yaml"
            };
            ApplyInitialOpenDirectory(dialog);
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            var savePath = dialog.FileName;
            return await SaveProjectAsync(savePath);
        }

        return await SaveProjectAsync(projectPath);
    }

    private async Task<bool> SaveProjectAsync(string savePath)
    {
        try
        {
            SyncToProject();
            var previousRootDirectory = CurrentRootDirectory;
            var saveDirectory = Path.GetDirectoryName(Path.GetFullPath(savePath)) ?? Environment.CurrentDirectory;
            RebaseProjectPaths(previousRootDirectory, saveDirectory);
            project.BaseDirectory = saveDirectory;
            projectPath = savePath;
            ApplyProjectPathsToViewModels();
            await projectService.SaveAsync(project, savePath);
            RememberOpenDirectory(savePath);
            AddRecentProject(savePath);
            MarkClean();
            OnPropertyChanged(nameof(OutputDirectory));
            OnPropertyChanged(nameof(SourceImage));
            OnPropertyChanged(nameof(RootStatusText));
            Status = $"Saved {Path.GetFileName(savePath)}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
            ShowError("Save failed", ex.Message);
            return false;
        }
    }

    private async Task OpenProjectAsync(string path)
    {
        if (!await ConfirmReplaceProjectAsync(path))
        {
            Status = "Open cancelled";
            return;
        }

        try
        {
            await LoadAsync(path);
        }
        catch (Exception ex)
        {
            Status = $"Open failed: {ex.Message}";
            ShowError("Open failed", ex.Message);
        }
    }

    private async Task<bool> ConfirmReplaceProjectAsync(string nextPath)
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        var currentName = projectPath is { Length: > 0 }
            ? Path.GetFileName(projectPath)
            : "the current project";
        var nextName = Path.GetFileName(nextPath);
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"Save changes to {currentName} before opening {nextName}?\n\nYes saves changes. No discards them. Cancel keeps the current project open.",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return result switch
        {
            MessageBoxResult.Yes => await SaveProjectAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private async Task OpenRecentProjectAsync(string path)
    {
        if (!File.Exists(path))
        {
            RemoveRecentProject(path);
            Status = $"Recent project not found: {Path.GetFileName(path)}";
            return;
        }

        await OpenProjectAsync(path);
    }

    private void OpenRecentImage(string path)
    {
        if (!File.Exists(path) || !IsSupportedSourceImageFile(path))
        {
            RemoveRecentImage(path);
            Status = $"Recent image not found: {Path.GetFileName(path)}";
            return;
        }

        SetSourceImage(path);
    }

    private void OpenRecentOutputDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            RemoveRecentOutputDirectory(path);
            Status = $"Recent output folder not found: {Path.GetFileName(path)}";
            return;
        }

        SetOutputDirectory(path);
    }

    private void ChooseSourceImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Filter = ImageFilter,
            Title = "Choose image"
        };
        ApplyInitialOpenDirectory(dialog, ResolveSourceImagePath());

        if (dialog.ShowDialog() == true)
        {
            RememberOpenDirectory(dialog.FileName);
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
        else if (TryGetExistingDirectory(CurrentRootDirectory, out var rootDirectory))
        {
            dialog.InitialDirectory = rootDirectory;
        }

        if (dialog.ShowDialog() == true)
        {
            SetOutputDirectory(dialog.FolderName);
        }
    }

    private void SetOutputDirectory(string directoryPath)
    {
        OutputDirectory = CreateProjectPathForOutputDirectory(directoryPath);
        AddRecentOutputDirectory(directoryPath);
        Validate();
        if (CanAttemptSolve())
        {
            Solve();
        }

        Status = $"Output directory set: {OutputDirectory}";
    }

    private void Validate()
    {
        SyncToProject();
        var messages = validator.Validate(project, projectPath);
        CanRender = !ProjectValidator.HasErrors(messages);
        if (!CanRender)
        {
            CurrentPreviewPlan = null;
        }

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
            var source = EnsureSelectedSource();
            var image = new NetVipsImageInfoReader().Read(ProjectPaths.Resolve(project, source.Image));
            var solver = new TransformSolver();
            var initialResult = solver.Solve(source.Georeference, project.Render, image.Width, image.Height);
            var zoomRange = ZoomRangeCalculator.Resolve(project.Render, initialResult, image.Width, image.Height);
            if (useAutoZoomRange || ZoomRangeCalculator.UsesAutoZoom(project.Render))
            {
                ApplyAutoZoomRange(zoomRange);
            }

            var result = solver.Solve(source.Georeference, project.Render, image.Width, image.Height, zoomRange.MaxZoom);
            CurrentSolveResult = result;
            ApplyErrors(result);
            CanRender = !ProjectValidator.HasErrors(validator.Validate(project, projectPath));
            RefreshPreviewPlan();
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
            CurrentPreviewPlan = null;
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
        NormalizeOutputPathsForCurrentRoot();
        EnsureProjectBaseDirectory();
        IsRendering = true;
        RenderPercent = 0;
        RenderSummaryText = "Rendering...";
        Status = "Starting render...";
        renderCancellation = new CancellationTokenSource();
        var progress = new Progress<TileRenderProgress>(p =>
        {
            RenderPercent = p.Percent;
            var tileNumber = Math.Min(p.CompletedTiles + 1, p.TotalTiles);
            Status = $"Rendering zoom {p.Zoom}: tile {tileNumber}/{p.TotalTiles}";
            RenderSummaryText = p.TotalImages > 0
                ? $"Rendering image {p.CurrentImage}/{p.TotalImages}"
                : "Rendering...";
        });
        try
        {
            var summary = await new RenderJobService().RenderAsync(project, progress, renderCancellation.Token);
            AddRecentOutputDirectory(ResolveOutputDirectory());
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

    private async Task WriteTileJsonOnlyAsync()
    {
        if (IsRendering)
        {
            return;
        }

        SyncToProject();
        NormalizeOutputPathsForCurrentRoot();
        EnsureProjectBaseDirectory();
        Status = "Writing TileJSON...";
        try
        {
            var path = await new RenderJobService().WriteTileJsonAsync(project);
            AddRecentOutputDirectory(ResolveOutputDirectory());
            RenderSummaryText = "TileJSON written";
            Status = $"TileJSON written: {path}";
        }
        catch (Exception ex)
        {
            Status = $"TileJSON failed: {ex.Message}";
            ShowError("TileJSON failed", ex.Message);
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
        NormalizeOutputPathsForCurrentRoot();
        EnsureProjectBaseDirectory();
        var path = ResolveOutputDirectory();
        Directory.CreateDirectory(path);
        AddRecentOutputDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AddPoint()
    {
        if (IsRendering)
        {
            return;
        }

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
        if (IsRendering)
        {
            return;
        }

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
        // tilejson.json always follows the output directory now; drop any legacy stored path.
        project.Output.TileJsonPath = string.Empty;
        useAutoZoomRange = ZoomRangeCalculator.UsesAutoZoom(project.Render);
        usesExplicitSources = ProjectSources.HasExplicitSources(project);
        Sources.Clear();
        foreach (var source in ProjectSources.GetEffectiveSources(project))
        {
            Sources.Add(ProjectSourceViewModel.FromConfig(source));
        }

        SelectedSource = Sources.FirstOrDefault();

        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(SourceImage));
        OnPropertyChanged(nameof(SelectedSourceName));
        OnPropertyChanged(nameof(CanRemoveSelectedSource));
        OnPropertyChanged(nameof(MinZoom));
        OnPropertyChanged(nameof(MaxZoom));
        OnPropertyChanged(nameof(Format));
        OnPropertyChanged(nameof(OutputDirectory));
        OnPropertyChanged(nameof(RootStatusText));
        OnPropertyChanged(nameof(PreviewOpacity));
        OnPropertyChanged(nameof(LimitPreviewTilesToZoomLevel));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(TileSize));
        OnPropertyChanged(nameof(RenderOptionsSummary));
        DisablePreviewIfUnavailable();
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

    private void AddSource()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Filter = ImageFilter,
            Title = "Add image"
        };
        ApplyInitialOpenDirectory(dialog, ResolveSourceImagePath());

        if (dialog.ShowDialog() == true)
        {
            AddSourceImage(dialog.FileName);
        }
    }

    private void AddSourceImage(string imagePath)
    {
        if (!IsSupportedSourceImageFile(imagePath))
        {
            Status = $"Unsupported image: {Path.GetFileName(imagePath)}";
            return;
        }

        RememberOpenDirectory(imagePath);
        AddRecentImage(imagePath);
        EnsureProjectRootForPath(imagePath);
        if (Sources.Count == 0
            || (Sources.Count == 1
                && string.IsNullOrWhiteSpace(Sources[0].Image)
                && Sources[0].Georeference.ControlPoints.Count == 0))
        {
            SelectedSource = Sources.FirstOrDefault() ?? EnsureSelectedSource();
            SetSourceImage(imagePath);
            Status = $"Added image: {Path.GetFileName(imagePath)}";
            return;
        }

        SyncToProject();
        usesExplicitSources = true;
        var nextNumber = Sources
            .Select(source => ParseTrailingNumber(source.Id))
            .DefaultIfEmpty(0)
            .Max() + 1;
        var source = new ProjectSourceViewModel
        {
            Id = $"source-{nextNumber}",
            Name = Path.GetFileNameWithoutExtension(imagePath),
            Image = CreateProjectPathForSourceImage(imagePath),
            Enabled = true,
            Georeference = new GeoreferenceConfig()
        };
        Sources.Add(source);
        SelectedSource = source;
        ResetAutoZoomRange();
        CurrentSolveResult = null;
        CurrentPreviewPlan = null;
        RmsText = "n/a";
        MaxErrorText = "n/a";
        Validate();
        Status = $"Added image: {Path.GetFileName(imagePath)}";
    }

    private void RemoveSelectedSource()
    {
        if (SelectedSource is null || Sources.Count <= 1)
        {
            return;
        }

        var index = Sources.IndexOf(SelectedSource);
        var removedName = SelectedSource.DisplayName;
        Sources.Remove(SelectedSource);
        SelectedSource = Sources[Math.Clamp(index, 0, Sources.Count - 1)];
        usesExplicitSources = Sources.Count > 1;
        Status = $"Removed {removedName}";
        ValidateAndSolve();
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
        SyncSelectedSourceControlPoints();
        var source = Sources.FirstOrDefault();
        if (usesExplicitSources || Sources.Count > 1 || (source is not null && HasNonDefaultOpacity(source)))
        {
            usesExplicitSources = true;
            project.Sources = Sources.Select(source => source.ToConfig()).ToList();
            return;
        }

        if (source is null)
        {
            return;
        }

        project.Sources = null;
        project.Source.Image = source.Image;
        project.Source.Origin = source.Origin;
        project.Source.Units = source.Units;
        project.Georeference = source.Georeference;
    }

    private void SyncSelectedSourceControlPoints()
    {
        if (SelectedSource is null)
        {
            return;
        }

        SelectedSource.Georeference.ControlPoints = ControlPoints.Select(point => point.ToConfig()).ToList();
    }

    private void LoadControlPointsFromSelectedSource()
    {
        isLoadingControlPoints = true;
        try
        {
            ControlPoints.Clear();
            if (SelectedSource is not { } source)
            {
                return;
            }

            foreach (var point in source.Georeference.ControlPoints)
            {
                ControlPoints.Add(ControlPointViewModel.FromConfig(point));
            }
        }
        finally
        {
            isLoadingControlPoints = false;
        }
    }

    private ProjectSourceViewModel EnsureSelectedSource()
    {
        if (SelectedSource is not null)
        {
            return SelectedSource;
        }

        var source = ProjectSourceViewModel.FromConfig(ProjectSources.CreateLegacySource(project));
        Sources.Add(source);
        SelectedSource = source;
        return source;
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
        var enabledPointCount = SelectedSource?.Georeference.ControlPoints.Count(p => p.Enabled)
            ?? project.Georeference.ControlPoints.Count(p => p.Enabled);
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
        if (!isLoadingControlPoints)
        {
            MarkDirty();
        }

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

    private void RefreshPreviewPlan()
    {
        try
        {
            CurrentPreviewPlan = new ProjectRenderPlanner().CreatePlan(project);
        }
        catch
        {
            CurrentPreviewPlan = null;
        }
    }

    private void RefreshPreviewSourceMetadata()
    {
        if (CurrentPreviewPlan is null)
        {
            return;
        }

        var enabledSources = ProjectSources.GetEnabledSources(project);
        var updatedSources = CurrentPreviewPlan.Sources
            .Select(source =>
            {
                var updatedSource = enabledSources.FirstOrDefault(candidate => SourceMatches(source.Source, candidate));
                return updatedSource is null ? source : source with { Source = updatedSource };
            })
            .ToList();
        CurrentPreviewPlan = CurrentPreviewPlan with { Sources = updatedSources };
    }

    private static bool SourceMatches(ProjectSourceConfig current, ProjectSourceConfig candidate)
    {
        if (!string.IsNullOrWhiteSpace(current.Id)
            && !string.IsNullOrWhiteSpace(candidate.Id)
            && string.Equals(current.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(current.Image, candidate.Image, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNonDefaultOpacity(ProjectSourceViewModel source) => Math.Abs(source.Opacity - 1.0) > 0.0000001;

    private void MarkDirty()
    {
        if (!suppressDirtyTracking)
        {
            HasUnsavedChanges = true;
        }
    }

    private void SourcesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MarkDirty();

        if (e.OldItems is not null)
        {
            foreach (ProjectSourceViewModel source in e.OldItems)
            {
                if (sourceHandlers.Remove(source, out var handler))
                {
                    source.PropertyChanged -= handler;
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ProjectSourceViewModel source in e.NewItems)
            {
                SubscribeSource(source);
            }
        }

        OnPropertyChanged(nameof(CanRemoveSelectedSource));
        removeSourceCommand.RaiseCanExecuteChanged();
    }

    private void SubscribeSource(ProjectSourceViewModel source)
    {
        if (sourceHandlers.ContainsKey(source))
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            MarkDirty();
            if (ReferenceEquals(source, SelectedSource))
            {
                OnPropertyChanged(nameof(SourceImage));
                OnPropertyChanged(nameof(SelectedSourceName));
                OnPropertyChanged(nameof(CanPreview));
                DisablePreviewIfUnavailable();
            }

            if (e.PropertyName is nameof(ProjectSourceViewModel.Opacity))
            {
                Validate();
                if (CanRender)
                {
                    RefreshPreviewSourceMetadata();
                }

                return;
            }

            if (e.PropertyName is nameof(ProjectSourceViewModel.Enabled)
                or nameof(ProjectSourceViewModel.Image)
                or nameof(ProjectSourceViewModel.Name)
                or nameof(ProjectSourceViewModel.Id))
            {
                ValidateAndSolve();
            }
        };

        source.PropertyChanged += handler;
        sourceHandlers[source] = handler;
    }

    private void MarkClean() => HasUnsavedChanges = false;

    private void DisablePreviewIfUnavailable()
    {
        if (isPreviewEnabled && !CanPreview)
        {
            isPreviewEnabled = false;
            OnPropertyChanged(nameof(IsPreviewEnabled));
        }
    }

    private bool SourceImageExists()
    {
        try
        {
            var path = ResolveSourceImagePath();
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public void SetSourceImage(string imagePath)
    {
        if (!IsSupportedSourceImageFile(imagePath))
        {
            Status = $"Unsupported image: {Path.GetFileName(imagePath)}";
            return;
        }

        RememberOpenDirectory(imagePath);
        AddRecentImage(imagePath);
        EnsureProjectRootForPath(imagePath);
        SourceImage = CreateProjectPathForSourceImage(imagePath);
        ResetAutoZoomRange();
        if (string.IsNullOrWhiteSpace(ProjectName) || ProjectName == "QTiles project")
        {
            ProjectName = Path.GetFileNameWithoutExtension(imagePath);
        }

        CurrentSolveResult = null;
        CurrentPreviewPlan = null;
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

        Status = $"Selected image set: {Path.GetFileName(imagePath)}";
    }

    private bool CanAttemptSolve()
    {
        var enabledPoints = ControlPoints.Count(p => p.Enabled);
        var transformType = (SelectedSource?.Georeference ?? project.Georeference).Transform.Type.Trim().ToLowerInvariant();
        return transformType == "similarity" ? enabledPoints >= 2 : enabledPoints >= 3;
    }

    private string CreateProjectPathForSourceImage(string imagePath)
        => CreateProjectPathForProject(imagePath);

    private string CreateProjectPathForOutputDirectory(string directoryPath)
        => CreateProjectPathForProject(directoryPath);

    private string CreateProjectPathForProject(string path)
    {
        return ProjectPathFormatter.FormatForProject(CurrentRootDirectory, path);
    }

    private void RebaseProjectPaths(string? oldRootDirectory, string newRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(oldRootDirectory))
        {
            return;
        }

        if (project.Sources is { Count: > 0 })
        {
            foreach (var source in project.Sources)
            {
                source.Image = RebaseProjectPath(source.Image, oldRootDirectory, newRootDirectory);
            }
        }
        else
        {
            project.Source.Image = RebaseProjectPath(project.Source.Image, oldRootDirectory, newRootDirectory);
        }

        project.Output.Directory = RebaseProjectPath(project.Output.Directory, oldRootDirectory, newRootDirectory);
        project.Output.TileJsonPath = RebaseProjectPath(project.Output.TileJsonPath, oldRootDirectory, newRootDirectory);
    }

    private string? ProjectDirectory => projectPath is { Length: > 0 }
        ? Path.GetDirectoryName(Path.GetFullPath(projectPath))
        : null;

    private string? CurrentRootDirectory =>
        ProjectDirectory
        ?? project.BaseDirectory
        ?? GetFirstSourceDirectory();

    private string? GetFirstSourceDirectory()
    {
        var sourcePath = Sources.Select(source => source.Image)
            .Concat([project.Source.Image])
            .FirstOrDefault(Path.IsPathRooted);

        return TryGetDirectoryForPath(sourcePath, out var directory) ? directory : null;
    }

    private string ResolveProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        var root = CurrentRootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(root, path));
    }

    private void EnsureProjectRootForPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(CurrentRootDirectory)
            || !TryGetDirectoryForPath(path, out var directory))
        {
            return;
        }

        project.BaseDirectory = directory;
        OnPropertyChanged(nameof(RootStatusText));
    }

    private void EnsureProjectBaseDirectory()
    {
        if (!string.IsNullOrWhiteSpace(project.BaseDirectory))
        {
            return;
        }

        project.BaseDirectory = CurrentRootDirectory;
        OnPropertyChanged(nameof(RootStatusText));
    }

    private void NormalizeOutputPathsForCurrentRoot()
    {
        var root = CurrentRootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var outputDirectory = ProjectPathFormatter.FormatForProject(root, ResolveProjectPath(project.Output.Directory));
        if (!string.Equals(project.Output.Directory, outputDirectory, StringComparison.Ordinal))
        {
            project.Output.Directory = outputDirectory;
            MarkDirty();
            OnPropertyChanged(nameof(OutputDirectory));
        }

        if (string.IsNullOrWhiteSpace(project.Output.TileJsonPath))
        {
            return;
        }

        var tileJsonPath = ProjectPathFormatter.FormatForProject(root, ResolveProjectPath(project.Output.TileJsonPath));
        if (!string.Equals(project.Output.TileJsonPath, tileJsonPath, StringComparison.Ordinal))
        {
            project.Output.TileJsonPath = tileJsonPath;
            MarkDirty();
        }
    }

    private static string RebaseProjectPath(string path, string oldRootDirectory, string newRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var absolutePath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(oldRootDirectory, path));
            return ProjectPathFormatter.FormatForProject(newRootDirectory, absolutePath);
        }
        catch
        {
            return path;
        }
    }

    private void ApplyProjectPathsToViewModels()
    {
        if (project.Sources is { Count: > 0 })
        {
            for (var i = 0; i < Sources.Count && i < project.Sources.Count; i++)
            {
                Sources[i].Image = project.Sources[i].Image;
            }

            return;
        }

        if (Sources.FirstOrDefault() is { } source)
        {
            source.Image = project.Source.Image;
        }
    }

    private static bool IsProjectFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyInitialOpenDirectory(Microsoft.Win32.FileDialog dialog, string? fallbackPath = null)
    {
        if (TryGetExistingDirectory(editorSettings.LastOpenDirectory, out var directory)
            || TryGetDirectoryForPath(fallbackPath, out directory))
        {
            dialog.InitialDirectory = directory;
        }
    }

    private void RememberOpenDirectory(string path)
    {
        if (!TryGetDirectoryForPath(path, out var directory)
            || string.Equals(editorSettings.LastOpenDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        editorSettings.LastOpenDirectory = directory;
        editorSettingsChanged?.Invoke();
    }

    private void AddRecentProject(string path)
    {
        if (AddRecentPath(editorSettings.RecentProjects, path))
        {
            RefreshRecentFiles();
            editorSettingsChanged?.Invoke();
        }
    }

    private void AddRecentImage(string path)
    {
        if (AddRecentPath(editorSettings.RecentImages, path))
        {
            RefreshRecentFiles();
            editorSettingsChanged?.Invoke();
        }
    }

    private void AddRecentOutputDirectory(string path)
    {
        if (AddRecentPath(editorSettings.RecentOutputDirectories, path))
        {
            RefreshRecentFiles();
            editorSettingsChanged?.Invoke();
        }
    }

    private void RemoveRecentProject(string path)
    {
        if (RemoveRecentPath(editorSettings.RecentProjects, path))
        {
            RefreshRecentFiles();
            editorSettingsChanged?.Invoke();
        }
    }

    private void RemoveRecentImage(string path)
    {
        if (RemoveRecentPath(editorSettings.RecentImages, path))
        {
            RefreshRecentFiles();
            editorSettingsChanged?.Invoke();
        }
    }

    private void RemoveRecentOutputDirectory(string path)
    {
        if (RemoveRecentPath(editorSettings.RecentOutputDirectories, path))
        {
            RefreshRecentFiles();
            editorSettingsChanged?.Invoke();
        }
    }

    private void RefreshRecentFiles()
    {
        RefreshRecentCollection(RecentProjects, editorSettings.RecentProjects);
        RefreshRecentCollection(RecentImages, editorSettings.RecentImages);
        RefreshRecentCollection(RecentOutputDirectories, editorSettings.RecentOutputDirectories);
        OnPropertyChanged(nameof(HasRecentProjects));
        OnPropertyChanged(nameof(HasRecentImages));
        OnPropertyChanged(nameof(HasRecentOutputDirectories));
    }

    private static void RefreshRecentCollection(
        ObservableCollection<RecentFileViewModel> collection,
        IEnumerable<string> paths)
    {
        collection.Clear();
        foreach (var path in paths)
        {
            collection.Add(new RecentFileViewModel(path));
        }
    }

    private static bool AddRecentPath(IList<string> paths, string path)
    {
        if (!TryNormalizeFilePath(path, out var normalizedPath))
        {
            return false;
        }

        var original = paths.ToArray();
        RemoveRecentPath(paths, normalizedPath);
        paths.Insert(0, normalizedPath);
        while (paths.Count > MaxRecentItems)
        {
            paths.RemoveAt(paths.Count - 1);
        }

        return !paths.SequenceEqual(original, StringComparer.OrdinalIgnoreCase);
    }

    private static bool RemoveRecentPath(IList<string> paths, string path)
    {
        var changed = false;
        for (var i = paths.Count - 1; i >= 0; i--)
        {
            if (string.Equals(paths[i], path, StringComparison.OrdinalIgnoreCase)
                || (TryNormalizeFilePath(paths[i], out var existing)
                    && TryNormalizeFilePath(path, out var normalizedPath)
                    && string.Equals(existing, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                paths.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    private static bool TryNormalizeFilePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPath(string? path) => !string.IsNullOrWhiteSpace(path);

    private static int ParseTrailingNumber(string value)
    {
        var digits = new string(value.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var number) ? number : 0;
    }

    private static bool TryGetDirectoryForPath(string? path, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var candidate = Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            return TryGetExistingDirectory(candidate, out directory);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetExistingDirectory(string? path, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            directory = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowError(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
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

}

public sealed class RecentFileViewModel
{
    public RecentFileViewModel(string filePath)
    {
        FilePath = filePath;
        DisplayName = CreateDisplayName(filePath);
        Folder = Path.GetDirectoryName(filePath) ?? string.Empty;
    }

    public string FilePath { get; }

    public string DisplayName { get; }

    public string Folder { get; }

    private static string CreateDisplayName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return string.IsNullOrWhiteSpace(fileName) ? filePath : fileName;
    }
}

public sealed record RenderOptionsSnapshot(
    int TileSize,
    string Format,
    int Quality,
    string Resampling,
    string Background,
    bool SkipEmptyTiles,
    bool Overwrite,
    bool ClearOutputDirectory,
    bool WriteTileJson);
