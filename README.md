# QTiles

QTiles is a Windows desktop editor and command-line tool for georeferencing source images and rendering them as XYZ map tiles.

The app has three public pieces:

- `QTiles.exe`: WPF editor for placing control points and rendering tiles.
- `QTiles.Cli.exe`: scriptable validation, solving, rendering, and TileJSON output.
- `qtiles`: the .NET tool command name when the CLI is packed as a tool.

## Installation

### winget

```powershell
winget install Code-iX.QTiles
```

### Manual

Download `QTiles-X.Y.Z-x64.msi` from the latest GitHub Release and install it. The MSI is self-contained; no separate .NET Desktop Runtime install is required.

QTiles installs per user under `%LOCALAPPDATA%\Programs\QTiles` and does not require administrator rights.

## Command line

```powershell
QTiles.Cli.exe init --image .\map.png --out .\qtiles.yaml
QTiles.Cli.exe validate .\qtiles.yaml
QTiles.Cli.exe solve .\qtiles.yaml
QTiles.Cli.exe render .\qtiles.yaml
QTiles.Cli.exe tilejson .\qtiles.yaml
QTiles.Cli.exe editor .\qtiles.yaml
```

Rendered tiles use standard XYZ paths:

```text
<output_directory>\{z}\{x}\{y}.<format>
```

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet restore QTiles.slnx
dotnet build QTiles.slnx -c Release
dotnet test QTiles.slnx
```

Build the installer:

```powershell
dotnet build QTiles.Installer/QTiles.Installer.wixproj -c Release -p:ProductVersion=1.0.0
```

The installer project publishes both `QTiles.exe` and `QTiles.Cli.exe` as self-contained `win-x64` single-file executables before packaging.

## Releasing

- Push an annotated tag like `v1.2.3`.
- The release workflow builds/tests, publishes self-contained `win-x64`, builds `QTiles-1.2.3-x64.msi`, creates a GitHub Release, and uses the annotated tag body as the release description.
- Keep the installer `UpgradeCode` stable across releases.
- The winget identifier is `Code-iX.QTiles`; the installer is self-contained and should not declare a .NET runtime dependency.
- First winget listing requires a manual `wingetcreate` submission. After `Code-iX.QTiles` exists in `microsoft/winget-pkgs`, the workflow can submit update PRs with `winget-releaser`.

## Project structure

```text
QTiles.Core       shared YAML, validation, transforms, and rendering
QTiles.Cli        command-line host and dotnet tool package
QTiles            WPF editor
QTiles.Installer  per-user WiX MSI
QTiles.Tests      xUnit coverage for core and CLI behavior
```

## License

[MIT](LICENSE.txt) (c) Code-iX
