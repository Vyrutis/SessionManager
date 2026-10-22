# Builds the Session Manager installers: artifacts\installer\SessionManager-<version>.msi (English) and SessionManager-<version>-lt.msi (Lithuanian)
# Usage:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\SessionManager\SessionManager.csproj'
$publish = Join-Path $root 'artifacts\publish\'

# The version comes from <Version> in the app's project file.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.0' }
Write-Host "Session Manager $version"

Write-Host 'Publishing the app (self-contained, win-x64)...'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -nologo -v q `
    -p:DebugType=none -p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Building the MSI...'
dotnet build (Join-Path $PSScriptRoot 'SessionManager.Installer.wixproj') -c Release -nologo -v q `
    "-p:ProductVersion=$version" "-p:PublishDir=$publish"
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }

$out = Join-Path $root 'artifacts\installer'
$msi = Join-Path $out "SessionManager-$version.msi"
$msiLt = Join-Path $out "SessionManager-$version-lt.msi"
Copy-Item (Join-Path $out "en-US\SessionManager-$version.msi") $msi -Force
Copy-Item (Join-Path $out "lt-LT\SessionManager-$version.msi") $msiLt -Force
Write-Host "Done: $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
Write-Host "      $msiLt"
