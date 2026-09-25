<#
.SYNOPSIS
  Sets <Version> in Directory.Build.props, makes sure CHANGELOG.md has a section for it, commits and tags vX.Y.Z.
  Pushing is left to you:  git push origin main --follow-tags   (the tag push starts the release workflow)
.EXAMPLE
  ./scripts/bump-version.ps1 0.2.0
  ./scripts/bump-version.ps1 0.3.0-preview.1
#>
param([Parameter(Mandatory, Position = 0)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$Version' is not a semantic version (x.y.z or x.y.z-suffix)." }

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    if (git status --porcelain) { throw 'Working tree is not clean; commit or stash first.' }
    if (git tag --list "v$Version") { throw "Tag v$Version already exists." }

    $utf8 = New-Object Text.UTF8Encoding($false)
    $propsPath = Join-Path $root 'Directory.Build.props'
    $props = [IO.File]::ReadAllText($propsPath, $utf8)
    $props = [regex]::Replace($props, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    [IO.File]::WriteAllText($propsPath, $props, $utf8)

    $changelogPath = Join-Path $root 'CHANGELOG.md'
    $changelog = [IO.File]::ReadAllText($changelogPath, $utf8)
    if ($changelog -notmatch "(?m)^## \[?$([regex]::Escape($Version))\]?") {
        $section = "## [$Version] - $(Get-Date -Format yyyy-MM-dd)`n`n- _Describe the changes here._`n`n"
        $index = $changelog.IndexOf("`n## ")
        $changelog = if ($index -ge 0) { $changelog.Insert($index + 1, $section) } else { $changelog + "`n" + $section }
        [IO.File]::WriteAllText($changelogPath, $changelog, $utf8)
        Write-Warning "Added an empty CHANGELOG section for $Version — edit it, then amend: git commit --amend --no-edit; git tag -f v$Version"
    }

    git add Directory.Build.props CHANGELOG.md
    git commit -m "chore: release v$Version"
    git tag -a "v$Version" -m "Helm $Version"
    Write-Host "Tagged v$Version. Publish with: git push origin HEAD --follow-tags" -ForegroundColor Green
}
finally {
    Pop-Location
}
