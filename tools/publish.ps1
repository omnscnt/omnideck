# Builds the release folder and zip: artifacts\omnideck-<version>-win-x64(.zip).
# Layout: omnideck.exe (small launcher, .NET Framework 4.8 which ships with Windows) and app\ (the app,
# self-contained: .NET and Windows App SDK included). Unzip and run, nothing to install.
# Run: powershell -ExecutionPolicy Bypass -File tools/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Omnideck.App\Omnideck.App.csproj'
$launcher = Join-Path $root 'src\Omnideck.Launcher\Omnideck.Launcher.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "omnideck-$version-win-x64"
$out = Join-Path $root "artifacts\$name"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:DebugType=none -p:SatelliteResourceLanguages=en -o (Join-Path $out 'app') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$launcherOut = Join-Path $root 'artifacts\launcher'
dotnet build $launcher -c Release -p:Version=$version -o $launcherOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "launcher build failed" }
Copy-Item (Join-Path $launcherOut 'omnideck.exe') $out
# Transition from the omnisound name: the update script of 0.3.x checks for and installs omnisound.exe,
# so the same launcher also ships under that name; the app renames it to omnideck.exe on first start
# (InstallLayout.RenameLegacyLauncher). Newer update scripts ignore it.
Copy-Item (Join-Path $launcherOut 'omnideck.exe') (Join-Path $out 'omnisound.exe')

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
