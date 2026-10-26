#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes Astra Launcher as a single self-contained Windows x64 executable.

.DESCRIPTION
    Produces artifacts/AstraLauncher-win-x64-single/AstraLauncher.exe: one file that bundles
    the .NET runtime, WPF native libraries and all managed dependencies. No .NET installation
    is required on the target machine.

    The publish configuration:
      - self-contained win-x64
      - PublishSingleFile with native libraries and compression
      - no separate appsettings.json (optional; embedded defaults match the shipped file)

    Note: the first launch extracts the native runtime to %TEMP%\.net, so startup is a little
    slower than the folder build. User data continues to live in %LOCALAPPDATA%\AstraLauncher.

.EXAMPLE
    pwsh -File scripts/publish-single-file.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/AstraLauncher-win-x64-single'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'AstraLauncher.App/AstraLauncher.App.csproj'
$output = Join-Path $repoRoot $OutputDirectory
$targetExe = Join-Path $output 'AstraLauncher.exe'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

Write-Host "Publishing single-file executable to $output ..."
& dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExe = Get-ChildItem $output -File -Filter '*.exe' | Select-Object -First 1
if (-not $publishedExe) {
    throw "Publish produced no executable in $output."
}

if ($publishedExe.FullName -ne $targetExe) {
    Copy-Item $publishedExe.FullName $targetExe -Force
    Remove-Item $publishedExe.FullName -Force
}

$files = @(Get-ChildItem $output -File)
if ($files.Count -ne 1) {
    throw "Expected exactly one file in $output but found $($files.Count): $($files.Name -join ', ')"
}

$file = Get-Item $targetExe
$version = (Get-Item $targetExe).VersionInfo
Write-Host ''
Write-Host ("Single-file executable: {0}" -f $file.FullName)
Write-Host ("Size: {0:N1} MB ({1:N0} bytes)" -f ($file.Length / 1MB), $file.Length)
Write-Host ("File version: {0}" -f $version.FileVersion)
Write-Host ("Product version: {0}" -f $version.ProductVersion)
Write-Host 'Done.'
