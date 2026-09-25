<#
.SYNOPSIS
  Writes the CHANGELOG.md section for a version (or the top section) to a file, for vpk --releaseNotes and the GitHub release body.
.EXAMPLE
  ./scripts/changelog-notes.ps1 -Version 0.2.0 -Out artifacts/notes.md
#>
param(
    [string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [string]$Changelog = (Join-Path $PSScriptRoot '..\CHANGELOG.md')
)
$ErrorActionPreference = 'Stop'

$lines = Get-Content $Changelog -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[?v?([^\]\s]+)') {
        if (-not $Version -or $Matches[1] -eq $Version) { $start = $i; break }
    }
}
if ($start -lt 0) {
    Write-Warning "No CHANGELOG section for '$Version'; using a generic note."
    $body = @("Helm $Version")
} else {
    $end = $lines.Count
    for ($j = $start + 1; $j -lt $lines.Count; $j++) { if ($lines[$j] -match '^## ') { $end = $j; break } }
    $body = $lines[($start + 1)..($end - 1)]
}

New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($Out))) | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), (($body -join "`n").Trim() + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "Release notes -> $Out"
