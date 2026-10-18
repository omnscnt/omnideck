# Builds the release folder and zip: artifacts\omnisound-<version>-win-x64(.zip).
# Layout: omnisound.exe (small launcher, .NET Framework 4.8 which ships with Windows) and app\ (the app,
# self-contained: .NET and Windows App SDK included). Unzip and run, nothing to install.
# Run: powershell -ExecutionPolicy Bypass -File tools/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Omnisound.App\Omnisound.App.csproj'
$launcher = Join-Path $root 'src\Omnisound.Launcher\Omnisound.Launcher.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "omnisound-$version-win-x64"
$out = Join-Path $root "artifacts\$name"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:DebugType=none -p:SatelliteResourceLanguages=en -o (Join-Path $out 'app') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$launcherOut = Join-Path $root 'artifacts\launcher'
dotnet build $launcher -c Release -p:Version=$version -o $launcherOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "launcher build failed" }
Copy-Item (Join-Path $launcherOut 'omnisound.exe') $out

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
