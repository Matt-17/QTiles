# QTiles

[![CI](https://github.com/Matt-17/QTiles/actions/workflows/ci.yml/badge.svg)](https://github.com/Matt-17/QTiles/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

QTiles is a Windows desktop editor and command-line renderer for turning a georeferenced raster image into standard XYZ map tiles.

Use it when you have a scanned map, site plan, floor plan, historic image, or other raster source and want to manually align it to real-world coordinates, inspect the fit, and export web-map-ready tiles plus TileJSON metadata.

## Features

- WPF editor for pairing image pixels with longitude/latitude control points.
- Interactive OpenStreetMap basemap for positioning and visual checks.
- Image pane with pan, zoom, drag/drop source selection, recent files, and marker editing.
- Affine and similarity transform solving with RMS and max error feedback.
- NetVips-based tile renderer for PNG, JPEG, and WebP output.
- Auto or manual zoom ranges, tile size, quality, resampling, overwrite, empty-tile skipping, and TileJSON options.
- CLI commands for scripting project creation, validation, solving, rendering, and TileJSON generation.
- Per-user WiX MSI packaging for the WPF editor and CLI.

QTiles renders only your source raster. The editor basemap is for alignment and preview context; basemap tiles are not baked into the rendered output.

## Current Scope

QTiles currently focuses on manual raster georeferencing and folder-based XYZ tile output:

```text
tiles/{z}/{x}/{y}.png
tiles/tilejson.json
```

It is not a full GIS conversion suite. MBTiles, PMTiles, GeoTIFF export, vector tiles, batch reprojection, and automated ground-control-point detection are outside the current implementation.

## Installation

### GitHub Releases

When a release is available, download `QTiles-X.Y.Z-x64.msi` from the [GitHub Releases](https://github.com/Matt-17/QTiles/releases) page.

The MSI is self-contained, installs per user under `%LOCALAPPDATA%\Programs\QTiles`, and does not require administrator rights or a separate .NET Desktop Runtime installation.

### winget

The release workflow is prepared for the package identifier `Code-iX.QTiles`. Once the package exists in the winget community repository, install it with:

```powershell
winget install Code-iX.QTiles
```

Until a release package is published, build from source.

## Editor Quick Start

1. Start `QTiles.exe`.
2. Open an existing `qtiles.yaml` project or choose a source image.
3. Add control points by matching positions on the world map and source image.
4. Use at least 3 enabled points for an affine transform; 4 or more points are recommended so residual errors can be reviewed.
5. Choose output directory, format, zoom range, quality, and resampling options.
6. Render tiles, then enable preview to inspect the generated tile overlay.

Supported source-image extensions in the editor are `.png`, `.jpg`, `.jpeg`, `.tif`, `.tiff`, `.bmp`, and `.webp`.

## CLI Quick Start

Use `QTiles.Cli.exe` from the installer or publish folder. If you pack and install the CLI as a .NET tool, the command name is `qtiles`; the `editor` command still expects `QTiles.exe` to be available next to the CLI.

```powershell
QTiles.Cli.exe init --image .\source-map.png --out .\qtiles.yaml --name "Source map"
QTiles.Cli.exe validate .\qtiles.yaml
QTiles.Cli.exe solve .\qtiles.yaml
QTiles.Cli.exe render .\qtiles.yaml --out .\tiles --format png --min-zoom 10 --max-zoom 16
QTiles.Cli.exe tilejson .\qtiles.yaml
QTiles.Cli.exe editor .\qtiles.yaml
```

Useful render overrides:

```powershell
QTiles.Cli.exe render .\qtiles.yaml `
  --out .\tiles `
  --format webp `
  --quality 90 `
  --resampling nohalo `
  --overwrite `
  --verbose
```

Use `--json` with `validate`, `solve`, or `render` for machine-readable output. Use `--no-tilejson` on `render` to skip TileJSON creation.

## Project File

QTiles projects are YAML files. Relative paths are resolved from the project file location, so projects can be moved with their image assets and output folder.

```yaml
version: 1
project:
  name: Example map
source:
  image: ./source-map.png
georeference:
  inputCrs: EPSG:4326
  internalCrs: EPSG:3857
  transform:
    type: affine
    solve: least-squares
  controlPoints:
    - id: 1
      enabled: true
      image:
        x: 125.0
        y: 210.0
      world:
        lon: 13.404954
        lat: 52.520008
    - id: 2
      enabled: true
      image:
        x: 900.0
        y: 220.0
      world:
        lon: 13.414954
        lat: 52.520008
    - id: 3
      enabled: true
      image:
        x: 890.0
        y: 740.0
      world:
        lon: 13.414954
        lat: 52.510008
render:
  autoZoom: true
  format: png
  tileSize: 256
  resampling: nohalo
output:
  directory: ./tiles
  tileJson: true
  tileJsonPath: ./tiles/tilejson.json
```

Supported transform types:

- `affine`: requires at least 3 enabled control points.
- `similarity`: requires at least 2 enabled control points.

Supported render formats are `png`, `jpg`, `jpeg`, and `webp`. Supported resampling values are `nearest`, `bilinear`, `bicubic`, `lbb`, `nohalo`, and `vsqbs`.

## Build From Source

Requirements:

- Windows 10 or newer for the WPF editor and MSI.
- [.NET 10 SDK](https://dotnet.microsoft.com/) for build and test.

```powershell
git clone https://github.com/Matt-17/QTiles.git
cd QTiles

dotnet restore .\QTiles.slnx
dotnet build .\QTiles.slnx --configuration Release
dotnet test .\QTiles.slnx --configuration Release --no-build
```

Run the editor from source:

```powershell
dotnet run --project .\QTiles\QTiles.csproj -- .\qtiles.yaml
```

Run the CLI from source:

```powershell
dotnet run --project .\QTiles.Cli\QTiles.Cli.csproj -- validate .\qtiles.yaml
dotnet run --project .\QTiles.Cli\QTiles.Cli.csproj -- render .\qtiles.yaml
```

Build the MSI:

```powershell
dotnet build .\QTiles.Installer\QTiles.Installer.wixproj --configuration Release -p:ProductVersion=1.0.0
```

The installer build publishes both `QTiles.exe` and `QTiles.Cli.exe` as self-contained `win-x64` single-file executables before packaging, then writes the release MSI to `artifact\release\QTiles-X.Y.Z-x64.msi`.

## Repository Layout

```text
QTiles.Core       YAML, validation, Web Mercator math, transforms, and NetVips rendering
QTiles.Cli        command-line host and dotnet tool package
QTiles            Windows WPF editor
QTiles.Installer  per-user WiX MSI
QTiles.Tests      MSTest coverage for core and CLI behavior
```

## Release Flow

Releases are produced by pushing a tag in the form `vX.Y.Z`.

The GitHub Actions release workflow restores, builds, tests, publishes self-contained `win-x64` editor and CLI binaries, builds `QTiles-X.Y.Z-x64.msi`, creates a GitHub Release, and uses the annotated tag body as the release notes.

Keep the installer `UpgradeCode` stable across releases.

## License

QTiles is licensed under the [MIT License](LICENSE.txt).
