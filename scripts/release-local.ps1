<#
.SYNOPSIS
  Publishes Helm and packs a Velopack release into ./releases (same steps as CI, without uploading).

.DESCRIPTION
  Version defaults to <Version> in Directory.Build.props. The channel follows the version: a pre-release suffix
  (0.4.0-preview.1) packs the "preview" channel, otherwise "stable". Running it again with a higher version into
  the same folder produces a delta package and an updated releases.<channel>.json — exactly what an installed
  Helm reads when its update source override points at this folder (docs/testing-updates.md).

.EXAMPLE
  ./scripts/release-local.ps1
  ./scripts/release-local.ps1 -Version 0.3.1
  ./scripts/release-local.ps1 -SelfContained      # bundle the .NET runtime (one switch)
#>
param(
    [string]$Version,
    [string]$Channel,
    [switch]$SelfContained,
    [string]$OutputDir = 'releases',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    if (-not $Version) {
        [xml]$props = Get-Content 'Directory.Build.props'
        $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    }
    if (-not $Channel) { $Channel = if ($Version -match '-') { 'preview' } else { 'stable' } }

    $publishDir = Join-Path 'artifacts' "publish-$Version"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    Write-Host "== Publishing Helm $Version ($Channel, self-contained: $([bool]$SelfContained))" -ForegroundColor Cyan
    dotnet publish src/Helm.App -c $Configuration -r win-x64 --self-contained:$([bool]$SelfContained) `
        -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    $notes = Join-Path 'artifacts' "notes-$Version.md"
    & (Join-Path $PSScriptRoot 'changelog-notes.ps1') -Version $Version -Out $notes

    dotnet tool restore | Out-Null
    $packArgs = @(
        'vpk', 'pack',
        '--packId', 'HelmApp',
        '--packTitle', 'Helm',
        '--packAuthors', 'huyhung1404',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'Helm.exe',
        '--icon', 'src/Helm.App/Assets/helm-tile.ico',
        '--runtime', 'win-x64',
        '--channel', $Channel,
        '--releaseNotes', $notes,
        '--outputDir', $OutputDir
    )
    # Framework-dependent builds ask Setup to install the .NET 8 Desktop Runtime when it is missing.
    if (-not $SelfContained) { $packArgs += @('--framework', 'net8.0-x64-desktop') }

    Write-Host "== Packing into $OutputDir" -ForegroundColor Cyan
    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

    Write-Host "Done. Installer: $OutputDir\HelmApp-$Channel-Setup.exe (published on GitHub as Helm-win-Setup.exe)" -ForegroundColor Green
}
finally {
    Pop-Location
}
