# Implementation checklist

- [x] Milestone 1: skeleton
- [x] Milestone 2: config and YAML
- [x] Milestone 3: geo math and transforms
- [x] Milestone 4: NetVips renderer
- [x] Milestone 5: TileJSON
- [x] Milestone 6: WPF shell
- [x] Milestone 7: Mapsui world map
- [x] Milestone 8: image pane
- [x] Milestone 9: solve + visual feedback
- [x] Milestone 10: render from editor

# QTiles implementation plan for Codex

Build **QTiles / QuickTiles** as a shared rendering engine plus two front ends:

```text
QTiles.Core        -> YAML, georeferencing, tile math, NetVips rendering
QTiles.Cli         -> dotnet tool command: qtiles
QTiles             -> Windows WPF editor using Mapsui
QTiles.Installer   -> per-user MSI for QTiles.exe and QTiles.Cli.exe
QTiles.Tests       -> xUnit tests for core and CLI behavior
```

The editor should **not shell out to the CLI** for normal work. Both the CLI and the WPF editor should call `QTiles.Core`. The CLI remains scriptable and automatable; the editor is a comfortable visual YAML authoring and rendering shell.

WPF should target `net10.0-windows`; Microsoft’s framework guidance explicitly treats WPF as a platform-specific Windows target. The CLI/Core can stay `net10.0`. .NET tools are packaged as NuGet packages and can be installed/run through the .NET CLI, so `QTiles.Cli` should be authored as a dotnet tool with `PackAsTool` and `ToolCommandName=qtiles`. ([Microsoft Learn][1])

---

## 1. Main product goals

### QTiles CLI

The CLI creates tiled raster output:

```text
<output_directory>/{z}/{x}/{y}.<format>
```

Example:

```bash
qtiles render ./qtiles.yaml
qtiles render ./qtiles.yaml --out ./tiles --min-zoom 10 --max-zoom 16 --format png
qtiles validate ./qtiles.yaml
qtiles tilejson ./qtiles.yaml
```

### QTiles WPF editor

The WPF editor is for creating and editing control points:

```text
image pixel point <-> real-world lon/lat point
```

The UI should have:

```text
left pane:  real-world map, Mapsui + OSM/custom XYZ tiles
right pane: source image, pan/zoom/rotate, image-space coordinates
bottom:     control-point table
side panel: solve/render settings, RMS error, render progress
```

Mapsui is a suitable WPF map component. Its docs show WPF support and a minimal OSM tile layer via `OpenStreetMap.CreateTileLayer()`. Mapsui v5 also centralizes pan/zoom/rotate behavior through shared touch/mouse handling and navigator components. ([Mapsui][2])

### Renderer

Use **NetVips** in `QTiles.Core` for actual image processing. NetVips is a .NET binding for libvips; libvips is demand-driven, horizontally threaded, and designed to run quickly with low memory usage. NetVips also has native NuGet packages for libvips binaries. ([Kleis Auke Wolthuizen][3])

---

## 2. Solution structure

Codex should create this structure:

```text
QTiles.sln

QTiles.Core/
  Config/
  Geo/
  Imaging/
  Rendering/
  Transforms/
  Validation/

QTiles.Cli/
  Program.cs
  Commands/

QTiles/
  App.xaml
  MainWindow.xaml
  MainWindow.xaml.cs
  ViewModels/
  Services/

QTiles.Installer/
  QTiles.Installer.wixproj
  Package.wxs

QTiles.Tests/
  CoreTests.cs
  CliTests.cs
```

---

## 3. Package choices

### `QTiles.Core`

Use:

```text
NetVips
NetVips.Native
YamlDotNet
MathNet.Numerics
Microsoft.Extensions.Logging.Abstractions
```

`YamlDotNet` provides YAML parsing, emitting, and object serialization for .NET. ([NuGet][4])

Use `MathNet.Numerics` for least-squares solving unless Codex implements a small QR/SVD solver directly. A direct solver is acceptable, but a library is less error-prone.

### `QTiles.Cli`

Use:

```text
System.CommandLine
Spectre.Console
```

`System.CommandLine` is the direct fit for command parsing and subcommands; Microsoft’s documentation covers building .NET command-line apps with multiple commands and options. ([Microsoft Learn][5])

`Spectre.Console` is optional but useful for tables, validation output, and progress display.

### `QTiles`

Use:

```text
Mapsui.Wpf
CommunityToolkit.Mvvm
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Logging
```

Optional:

```text
SkiaSharp.Views.WPF
```

SkiaSharp is a .NET 2D graphics API based on Google’s Skia engine, with WPF view classes such as `SKElement`. It should be optional for custom image-pane drawing. Do not make the core renderer depend on SkiaSharp. ([NuGet][6])

---

## 4. Project files

### `QTiles.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NetVips" Version="3.2.0" />
    <PackageReference Include="NetVips.Native" Version="8.18.3" />
    <PackageReference Include="YamlDotNet" Version="18.1.0" />
    <PackageReference Include="MathNet.Numerics" Version="5.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
  </ItemGroup>

</Project>
```

### `QTiles.Cli.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <PackAsTool>true</PackAsTool>
    <ToolCommandName>qtiles</ToolCommandName>
    <PackageId>QTiles.Cli</PackageId>
    <AssemblyName>QTiles.Cli</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\QTiles.Core\QTiles.Core.csproj" />
    <PackageReference Include="System.CommandLine" Version="2.0.9" />
    <PackageReference Include="Spectre.Console" Version="0.57.1" />
  </ItemGroup>

</Project>
```

### `QTiles.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <AssemblyName>QTiles</AssemblyName>
    <RootNamespace>QTiles</RootNamespace>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>Assets\QTiles.ico</ApplicationIcon>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\QTiles.Core\QTiles.Core.csproj" />
    <PackageReference Include="Mapsui.Wpf" Version="5.1.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.9" />
  </ItemGroup>

</Project>
```

---

## 5. YAML schema v1

Use this as the first supported format.

```yaml
version: 1

project:
  name: "Old map"
  description: "Optional project description"

source:
  image: "./input/old-map.png"
  origin: "top-left"
  units: "pixels"

georeference:
  inputCrs: "EPSG:4326"
  internalCrs: "EPSG:3857"
  transform:
    type: "affine"
    solve: "least-squares"

  controlPoints:
    - id: 1
      name: "Optional readable name"
      enabled: true
      image:
        x: 1834.5
        y: 1220.0
      world:
        lon: 13.7416
        lat: 51.0519

    - id: 2
      enabled: true
      image:
        x: 1720.0
        y: 2050.0
      world:
        lon: 13.7320
        lat: 51.0400

    - id: 3
      enabled: true
      image:
        x: 2210.0
        y: 980.0
      world:
        lon: 13.7602
        lat: 51.0603

render:
  scheme: "xyz"
  tileSize: 256
  minZoom: 10
  maxZoom: 16
  format: "png"
  quality: 90
  resampling: "lanczos3"
  background: "transparent"
  skipEmptyTiles: true
  overwrite: true
  bounds: "auto"

output:
  directory: "./tiles"
  tileJson: true
  tileJsonPath: "./tiles/tilejson.json"

editor:
  basemap:
    type: "osm"
    url: "https://tile.openstreetmap.org/{z}/{x}/{y}.png"
  imagePane:
    lockWithTwoPoints: true
    showGrid: true
  preview:
    opacity: 0.65
```

### Control point IDs

Do **not** use descriptive names as required IDs.

Use:

```yaml
id: 1
name: "Frauenkirche"
```

or:

```yaml
id: "4b4f0ea3-3d50-49dc-b57e-94e87c2fdd45"
name: "Frauenkirche"
```

Implementation rule:

```text
id      -> required after save; editor auto-generates sequential int IDs by default
name    -> optional, human-readable
enabled -> allows excluding bad/outlier points without deleting them
```

In C#, model `id` internally as a string-like value object so both integers and GUIDs round-trip safely:

```csharp
public sealed record ControlPointId(string Value);
```

The WPF editor should generate IDs as `1`, `2`, `3`, etc. A future setting can switch to GUIDs.

---

## 6. CLI shape

### Commands

```bash
qtiles init --image ./input/map.png --out ./qtiles.yaml
qtiles validate ./qtiles.yaml
qtiles solve ./qtiles.yaml
qtiles render ./qtiles.yaml
qtiles tilejson ./qtiles.yaml
qtiles editor ./qtiles.yaml
```

### `qtiles init`

Creates a starter YAML file.

Options:

```text
--image <path>             required
--out <path>               default: qtiles.yaml
--name <name>              optional
--min-zoom <z>             default: 0
--max-zoom <z>             default: 0
--format <png|jpg|webp>    default: png
```

Behavior:

```text
- Validate source image exists.
- Read image width/height through NetVips.
- Create YAML with no control points.
- Create output directory setting as ./tiles.
```

### `qtiles validate`

Checks:

```text
- YAML is syntactically valid.
- source.image exists.
- render.tileSize is positive, default 256.
- render.minZoom <= render.maxZoom.
- render.format is png/jpg/webp.
- affine transform has at least 3 enabled control points.
- similarity transform has at least 2 enabled control points.
- lat/lon are valid.
- image points are within or near image bounds.
- output directory can be created.
```

Output should be machine-readable with `--json` and human-readable by default.

### `qtiles solve`

Computes the transform and prints a report:

```text
Transform: affine
Enabled points: 5
RMS error: 2.14 px at max zoom 16
Max error: 4.02 px at point id=3
Bounds: [west, south, east, north]
Recommended zoom range: 10..16
```

Options:

```text
--json
--write-report <path>
```

Do not write transform coefficients into YAML for v1 unless explicitly requested. The YAML should remain point-driven.

### `qtiles render`

Renders tiles.

Options override YAML:

```text
--out <directory>
--min-zoom <z>
--max-zoom <z>
--format <png|jpg|webp>
--quality <1-100>
--overwrite
--no-tilejson
--verbose
--json
```

Output:

```text
<out>/{z}/{x}/{y}.png
<out>/tilejson.json
```

Exit codes:

```text
0 success
1 validation error
2 file/IO error
3 transform solve error
4 render error
```

### `qtiles tilejson`

Writes only TileJSON metadata.

TileJSON is useful as tileset metadata: it describes name, attribution, bounds, center, min/max zoom, and tile URL templates. It should be an output artifact, not the primary project config. ([Mapbox][7])

### `qtiles editor`

On Windows:

```text
- Launch QTiles if available.
- Pass the YAML path.
```

Do not make the editor depend on the CLI. The editor references `QTiles.Core` directly.

---

## 7. Core georeferencing model

### Coordinate systems

User control points are stored as:

```text
image x/y pixels
world lon/lat degrees
```

Internally solve against **normalized Web Mercator**:

```text
u = normalized mercator x, range 0..1
v = normalized mercator y, range 0..1
```

This keeps the transform independent of zoom level.

### Web Mercator helper

Implement:

```csharp
public static class WebMercator
{
    public const double MaxLatitude = 85.05112878;

    public static NormalizedMercatorPoint LonLatToNormalized(double lon, double lat);

    public static GeoPoint NormalizedToLonLat(double x, double y);

    public static (double GlobalX, double GlobalY) NormalizedToGlobalPixel(
        double normalizedX,
        double normalizedY,
        int zoom,
        int tileSize);
}
```

Core formulas:

```csharp
x = (lon + 180.0) / 360.0;

sinLat = Math.Sin(latRad);
y = 0.5 - Math.Log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI);
```

Clamp latitude to:

```text
[-85.05112878, +85.05112878]
```

---

## 8. Transform solving

### Similarity transform

Used for **2-point provisional lock** in the editor.

Minimum:

```text
2 points
```

Supports:

```text
translation
uniform scale
rotation
```

Does **not** support shear.

Use this when the user has only two points and wants to roughly lock the image to the map.

### Affine transform

Used for actual v1 rendering.

Minimum:

```text
3 enabled points
```

Supports:

```text
translation
rotation
scale
non-uniform scale
shear
```

Solve:

```text
u = a*x + b*y + c
v = d*x + e*y + f
```

Where:

```text
x/y = source image pixel coordinates
u/v = normalized Web Mercator coordinates
```

For N control points, build matrix:

```text
[ x1 y1 1 ] [a] = [u1]
[ x2 y2 1 ] [b]   [u2]
[ x3 y3 1 ] [c]   [u3]
...
```

Solve once for `u` coefficients and once for `v` coefficients.

The result:

```csharp
public sealed record AffineTransform(
    double A, double B, double C,
    double D, double E, double F)
{
    public NormalizedMercatorPoint ImageToWorld(ImagePoint p)
        => new(
            A * p.X + B * p.Y + C,
            D * p.X + E * p.Y + F);

    public ImagePoint WorldToImage(NormalizedMercatorPoint p)
    {
        // invert 2x2 matrix
    }
}
```

### Error report

After solving, compute per-point errors.

For each enabled point:

```text
actual image point -> predicted normalized mercator -> predicted lon/lat
compare predicted world position with configured world point
```

Report:

```text
error in normalized units
error in estimated meters
error in pixels at maxZoom
```

The editor should show:

```text
RMS error
max error
per-point error
```

Use color/status levels:

```text
green   <= 2 px
yellow  <= 8 px
red     > 8 px
```

---

## 9. Tile rendering algorithm

### Tile coordinate convention

Use standard XYZ/slippy-map layout:

```text
{z}/{x}/{y}.png
```

Only implement:

```yaml
scheme: "xyz"
```

for v1.

### Bounds

For v1, compute bounds automatically from the transformed image corners:

```text
(0, 0)
(width, 0)
(width, height)
(0, height)
```

Transform those corners from image pixels to normalized Web Mercator, then to lon/lat bounds.

For each zoom:

```text
tileXMin = floor(minNormalizedX * 2^z)
tileXMax = floor(maxNormalizedX * 2^z)
tileYMin = floor(minNormalizedY * 2^z)
tileYMax = floor(maxNormalizedY * 2^z)
```

Clamp to:

```text
x: 0 .. 2^z - 1
y: 0 .. 2^z - 1
```

Antimeridian wrapping is out of scope for v1. Validate and reject if bounds cross it.

---

## 10. NetVips rendering strategy

For each tile, derive an affine transform from **source image pixel coordinates** to **tile-local pixel coordinates**.

Given the solved transform:

```text
u = A*x + B*y + C
v = D*x + E*y + F
```

At zoom `z`:

```text
globalX = u * tileSize * 2^z
globalY = v * tileSize * 2^z
```

For a specific tile:

```text
tileLocalX = globalX - tileX * tileSize
tileLocalY = globalY - tileY * tileSize
```

Therefore:

```text
tileLocalX = (A * scale) * x + (B * scale) * y + (C * scale - tileX * tileSize)
tileLocalY = (D * scale) * x + (E * scale) * y + (F * scale - tileY * tileSize)
```

Where:

```text
scale = tileSize * 2^z
```

Use libvips affine operation through NetVips. libvips’ affine operation maps input coordinates to output coordinates and supports interpolation/background options; NetVips exposes libvips operations as C# methods. ([libvips.org][8])

Conceptual implementation:

```csharp
var matrix = new[]
{
    transform.A * scale,
    transform.B * scale,
    transform.D * scale,
    transform.E * scale
};

var odx = transform.C * scale - tile.X * tileSize;
var ody = transform.F * scale - tile.Y * tileSize;

var tileImage = source.Affine(
    matrix,
    interpolate: interpolate,
    oarea: new[] { 0, 0, tileSize, tileSize },
    odx: odx,
    ody: ody,
    background: transparentBackground,
    extend: Enums.Extend.Background);
```

Codex must verify the exact NetVips C# method signature during implementation. Add a renderer unit test with a synthetic image and known affine transform to catch matrix/sign/order mistakes.

### Important rendering rule

Do not implement the renderer by looping over every source pixel and pushing it into tiles.

Use inverse-safe or affine rasterization behavior so that output tiles are fully covered and do not contain holes.

### Large shrink handling

libvips documentation notes that affine transforms can alias for very large shrinks. For v1, render directly and add a warning when the effective shrink ratio is large. For v2, implement pre-shrinking or pyramid generation before affine rendering. ([libvips.org][9])

### Empty tile skipping

For PNG/WebP with transparency:

```text
- Render tile with transparent background.
- Check alpha channel.
- If alpha max == 0 and skipEmptyTiles=true, do not write tile.
```

For JPEG:

```text
- JPEG has no alpha.
- Composite against configured background before saving.
- skipEmptyTiles should only skip if transformed source does not intersect tile.
```

---

## 11. TileJSON output

Emit:

```json
{
  "tilejson": "3.0.0",
  "name": "Old map",
  "scheme": "xyz",
  "tiles": [
    "./{z}/{x}/{y}.png"
  ],
  "minzoom": 10,
  "maxzoom": 16,
  "bounds": [13.70, 51.02, 13.80, 51.08],
  "center": [13.74, 51.05, 13],
  "attribution": "Generated by QTiles"
}
```

TileJSON should be generated by:

```csharp
TileJsonWriter.WriteAsync(project, solveResult, renderSummary, path, ct);
```

Do not require TileJSON for rendering. It is metadata for clients and previews.

---

## 12. WPF editor design

### Main layout

Use a clean, practical editor layout:

```text
┌────────────────────────────────────────────────────────────────────┐
│ Menu / Toolbar                                                     │
│ Open  Save  Validate  Solve  Render  Export TileJSON  Settings     │
├───────────────────────────────┬────────────────────────────────────┤
│ World map                     │ Source image                       │
│ Mapsui + basemap              │ Mapsui image-space view            │
│ control-point markers         │ pan / zoom / rotate                │
│ image footprint               │ control-point markers              │
├───────────────────────────────┴────────────────────────────────────┤
│ Control points table                                               │
│ id | name | enabled | image x/y | lon/lat | error | actions        │
├────────────────────────────────────────────────────────────────────┤
│ Status: 3 points, affine solved, RMS 2.1 px, max 4.0 px            │
└────────────────────────────────────────────────────────────────────┘
```

### Left pane: world map

Use Mapsui WPF.

Layers:

```text
1. Basemap layer
2. Control point markers
3. Selected point highlight
4. Image footprint polygon
5. Optional rendered tile preview layer
```

Default basemap:

```csharp
map.Layers.Add(OpenStreetMap.CreateTileLayer());
```

OSM tiles are acceptable for low-volume interactive editor viewing, but QTiles must not bulk-download OSM tiles. OSM’s tile policy prohibits bulk downloading/scraping and offline prefetching. Rendering QTiles output must only process the user’s source image, not fetch basemap tiles. ([OSMF Operations][10])

Add support for configurable XYZ basemaps:

```yaml
editor:
  basemap:
    type: "xyz"
    url: "http://localhost:8080/{z}/{x}/{y}.png"
```

### Right pane: source image

Use a second Mapsui control if practical.

This gives:

```text
- pan
- zoom
- rotation
- marker overlays
- matching interaction model with world pane
```

Coordinate system:

```text
x = image pixel x
y = image pixel y
origin = top-left
```

Render source image as:

```text
preferred: Mapsui image/raster layer
fallback: custom WPF/SkiaSharp image canvas
```

Mapsui has raster/image layer concepts, and Mapsui discussions/docs indicate raster features can display image data; use that path if it is stable enough. ([GitHub][11])

### Editor modes

Implement these modes:

```text
Select
Add point: image first
Add point: world first
Move point
Delete point
Lock preview
```

#### Add point: image first

```text
1. User clicks source image.
2. Temporary pending image point is created.
3. User clicks world map.
4. Full control point is created.
5. Editor assigns next integer ID.
6. Project becomes dirty.
```

#### Add point: world first

Same flow in reverse.

#### Move point

Allow dragging existing markers on either pane.

On drag end:

```text
- update YAML model
- re-solve transform
- update error table
- update footprint
```

### 2-point lock behavior

With exactly two enabled points:

```text
- Solve similarity transform.
- Show status: "2-point similarity preview".
- Allow image/map lock for rough alignment.
- Do not allow final affine render unless transform.type is explicitly similarity.
```

With three or more enabled points:

```text
- Solve affine transform.
- Enable render.
- Show RMS/max error.
```

This matches the intended workflow: two points are enough to orient and scale roughly; three points are the minimum for affine tile rendering.

### Locking left and right panes

When lock is enabled:

```text
- If user pans/zooms/rotates the world map, update the image pane viewport using the current transform.
- If user pans/zooms/rotates the image pane, update the world map viewport using the inverse transform.
```

Keep this feature conservative:

```text
- MVP: one-way lock from world map -> image pane.
- M2: bidirectional lock.
```

### Point table

Columns:

```text
Enabled checkbox
ID
Name
Image X
Image Y
Longitude
Latitude
Error px
Error m
Actions
```

Actions:

```text
Zoom to point
Rename
Disable/enable
Delete
```

### Visual design

Use a restrained desktop design:

```text
background: dark neutral or light neutral, consistent
accent: one color for selected point
error colors: green/yellow/red
toolbar: flat buttons with text labels
status bar: always visible
panes: splitter-based resizing
```

Control point markers:

```text
normal: small numbered circle
selected: larger circle with outline
disabled: gray/hollow
error high: red ring
pending pair: dashed marker
```

---

## 13. Editor rendering integration

The editor should call:

```csharp
await tileRenderer.RenderAsync(project, progress, cancellationToken);
```

Not:

```csharp
Process.Start("qtiles", "render ...")
```

Reason:

```text
- shared validation
- shared progress model
- no CLI parsing from GUI
- easier cancellation
- easier errors
```

The editor can still show the equivalent CLI command:

```text
qtiles render ./qtiles.yaml
```

as a copyable command for automation.

---

## 14. Basemap rules

Default:

```text
OSM public tile layer for interactive display only.
```

Do not:

```text
- cache large OSM regions
- prefetch tiles
- use OSM while rendering user output
- use OSM as an input source for generated tiles
```

Add config for local/no-API basemaps:

```yaml
editor:
  basemap:
    type: "xyz"
    url: "http://localhost:8080/basemap/{z}/{x}/{y}.png"
```

Later:

```yaml
editor:
  basemap:
    type: "mbtiles"
    file: "./basemap.mbtiles"
```

or:

```yaml
editor:
  basemap:
    type: "pmtiles"
    file: "./basemap.pmtiles"
```

Do not implement MBTiles/PMTiles in v1 unless time remains.

---

## 15. Validation rules

Implement `ProjectValidator`.

Severity:

```csharp
public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}
```

Rules:

```text
Error:
- YAML version unsupported.
- source.image missing.
- source image cannot be opened.
- tileSize <= 0.
- minZoom > maxZoom.
- unsupported format.
- affine selected with fewer than 3 enabled points.
- similarity selected with fewer than 2 enabled points.
- invalid lon/lat.
- transform solve matrix is singular.

Warning:
- fewer than 4 points; no redundancy for affine.
- high RMS error.
- image point outside image bounds.
- very large shrink may alias.
- output directory already contains tiles and overwrite=false.
- OSM basemap is configured; remind it is for interactive editor use only.

Info:
- TileJSON will be written.
- computed bounds.
- estimated tile count.
```

---

## 16. Rendering progress model

Use:

```csharp
public sealed record TileRenderProgress(
    int Zoom,
    int CompletedTiles,
    int TotalTiles,
    string? CurrentPath,
    double Percent);
```

Renderer signature:

```csharp
public interface ITileRenderer
{
    Task<RenderSummary> RenderAsync(
        QTilesProject project,
        IProgress<TileRenderProgress>? progress,
        CancellationToken cancellationToken);
}
```

Render summary:

```csharp
public sealed record RenderSummary(
    int TilesWritten,
    int TilesSkipped,
    int MinZoom,
    int MaxZoom,
    GeoBounds Bounds,
    TimeSpan Duration);
```

The WPF editor should show:

```text
- progress bar
- current zoom
- written/skipped tiles
- cancel button
- render summary after completion
```

---

## 17. Tests Codex must add

### Geo tests

```text
WebMercator_LonLatToNormalized_KnownValues
WebMercator_ClampsLatitude
TileMath_BoundsToTileRange_Zoom0
TileMath_BoundsToTileRange_KnownCityBounds
```

### Transform tests

```text
AffineSolver_ThreePoints_Exact
AffineSolver_MoreThanThreePoints_LeastSquares
AffineTransform_Inverse_RoundTrips
SimilaritySolver_TwoPoints_Exact
Solver_DisabledPoints_AreIgnored
Solver_ReportsPerPointErrors
```

### YAML tests

```text
Yaml_RoundTrip_MinimalProject
Yaml_RoundTrip_ControlPointIds_IntLike
Yaml_RoundTrip_ControlPointIds_GuidLike
Yaml_RejectsUnsupportedVersion
```

### Renderer tests

Use generated test images.

```text
Renderer_WritesExpectedTilePaths
Renderer_SkipEmptyTiles_Works
Renderer_PngPreservesTransparency
Renderer_TileJson_WritesBoundsAndZooms
Renderer_AffineIdentityLikeMapping_ProducesExpectedPixels
```

### CLI tests

```text
Cli_Init_CreatesYaml
Cli_Validate_ReturnsNonZeroForInvalidProject
Cli_Solve_PrintsRms
Cli_Render_WritesTiles
```

---

## 18. Milestones for Codex

### Milestone 1: skeleton

Create:

```text
solution
projects
references
nullable enabled
basic CI build
```

Definition of done:

```text
dotnet build
dotnet test
```

### Milestone 2: config and YAML

Implement:

```text
QTilesProject model
Yaml serializer
basic validation
qtiles init
qtiles validate
```

Definition of done:

```text
qtiles init --image sample.png --out qtiles.yaml
qtiles validate qtiles.yaml
```

### Milestone 3: geo math and transforms

Implement:

```text
WebMercator
TileMath
Similarity solver
Affine least-squares solver
error report
qtiles solve
```

Definition of done:

```text
qtiles solve qtiles.yaml
```

prints bounds, RMS, max error.

### Milestone 4: NetVips renderer

Implement:

```text
image info reader
tile range calculation
NetVips affine tile writer
PNG output
skip empty tiles
render progress
qtiles render
```

Definition of done:

```text
qtiles render qtiles.yaml
```

creates:

```text
tiles/{z}/{x}/{y}.png
```

### Milestone 5: TileJSON

Implement:

```text
TileJsonWriter
qtiles tilejson
automatic tilejson during render
```

Definition of done:

```text
tiles/tilejson.json
```

contains valid metadata.

### Milestone 6: WPF shell

Implement:

```text
MainWindow
menus
toolbar
split panes
project open/save
validation panel
```

Definition of done:

```text
QTiles opens qtiles.yaml and shows source image path/settings
```

### Milestone 7: Mapsui world map

Implement:

```text
Mapsui OSM layer
point markers
click world point
drag marker
zoom to point
```

Definition of done:

```text
user can place and move world-side control points
```

### Milestone 8: image pane

Implement:

```text
image-space pane
pan/zoom/rotate
image point markers
click image point
drag marker
```

Definition of done:

```text
user can pair image and world points
```

### Milestone 9: solve + visual feedback

Implement:

```text
auto-solve on point changes
RMS/max error
per-point error table
image footprint on world map
2-point similarity preview
3-point affine ready state
```

Definition of done:

```text
editor clearly shows whether project can render
```

### Milestone 10: render from editor

Implement:

```text
render button
settings panel
progress dialog
cancel button
summary
open output folder
```

Definition of done:

```text
editor can save YAML and render tiles through QTiles.Core
```

---

## 19. Non-goals for v1

Do not implement these yet:

```text
thin-plate spline
homography
MBTiles output
PMTiles output
GeoTIFF input georeferencing
automatic OCR/map matching
bulk OSM downloading
offline basemap generation
multi-image mosaics
```

Keep v1 focused:

```text
YAML
control points
affine least squares
NetVips XYZ raster tile output
TileJSON
WPF editor
```

---

## 20. Final instruction block for Codex

Use this as the implementation directive:

```text
Implement QTiles as a .NET 10 solution.

Create QTiles.Core, QTiles.Cli, QTiles, QTiles.Installer, and QTiles.Tests.

QTiles.Core must contain all business logic:
- YAML project model
- validation
- Web Mercator math
- tile math
- similarity and affine transform solving
- least-squares error reporting
- NetVips-based XYZ tile rendering
- TileJSON output

QTiles.Cli must be a dotnet tool named qtiles.
It must expose init, validate, solve, render, tilejson, and editor commands.

QTiles must reference QTiles.Core directly.
It must not invoke the CLI for normal rendering.
Use Mapsui for the world map and preferably for the image-space pane too.
The editor must let the user create, edit, enable/disable, name, and delete control points.
The editor must support 2-point similarity preview and 3+ point affine rendering.
The editor must save the same YAML file consumed by the CLI.

Use integer control point IDs by default.
Allow GUID-like IDs to round-trip.
Use optional `name` for human-readable point labels.

Use NetVips for rendering.
Avoid System.Drawing.
Use XYZ output:
<output_directory>/{z}/{x}/{y}.<format>

Implement tests for YAML round-trip, WebMercator, tile math, transform solving, renderer smoke output, CLI validation, and TileJSON output.
```

[1]: https://learn.microsoft.com/en-us/dotnet/standard/frameworks?utm_source=chatgpt.com "Target frameworks in SDK-style projects - .NET"
[2]: https://mapsui.com/?utm_source=chatgpt.com "Mapsui Documentation: Introduction"
[3]: https://kleisauke.github.io/net-vips/?utm_source=chatgpt.com "NetVips documentation"
[4]: https://www.nuget.org/packages/yamldotnet?utm_source=chatgpt.com "YamlDotNet 18.0.0"
[5]: https://learn.microsoft.com/en-us/dotnet/standard/commandline/get-started-tutorial?utm_source=chatgpt.com "Tutorial: Get started with System.CommandLine - .NET"
[6]: https://www.nuget.org/packages/SkiaSharp.Views.WPF/?utm_source=chatgpt.com "SkiaSharp.Views.WPF 4.148.0"
[7]: https://docs.mapbox.com/help/glossary/tilejson/?utm_source=chatgpt.com "TileJSON | Help"
[8]: https://www.libvips.org/API/8.17/method.Image.affine.html?utm_source=chatgpt.com "Vips.Image.affine - libvips"
[9]: https://www.libvips.org/API/8.17/libvips-resample.html?utm_source=chatgpt.com "Vips – 8.0: Operator index > By section > Resample - libvips"
[10]: https://operations.osmfoundation.org/policies/tiles/?utm_source=chatgpt.com "Tile Usage Policy"
[11]: https://github.com/Mapsui/Mapsui/discussions/2184?utm_source=chatgpt.com "Use a georeferenced file for map layer? #2184"
