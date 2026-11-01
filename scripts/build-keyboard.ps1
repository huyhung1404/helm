<#
.SYNOPSIS
  Builds the Helm Keyboard input method (NativeAOT, 64- and 32-bit) into artifacts/keyboard/{x64,x86}, where
  Helm.Modules.Keyboard picks it up to ship next to Helm (keyboard\x64, keyboard\x86). Needs the C++ build tools (MSVC)
  and a Windows SDK; without them the build of Helm still works, but leaves Helm Keyboard out.
.EXAMPLE
  ./scripts/build-keyboard.ps1
  ./scripts/build-keyboard.ps1 -Version 0.71.0
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
# NativeAOT finds MSVC through vswhere, which lives in the Visual Studio Installer folder.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }

foreach ($arch in 'x64', 'x86') {
    $publish = @('publish', (Join-Path $root 'src/Helm.Keyboard.Tip'), '-c', 'Release', '-r', "win-$arch",
        '-o', (Join-Path $root "artifacts/keyboard/$arch"), '--nologo', '-v', 'q')
    if ($Version) { $publish += "-p:Version=$Version" }
    & dotnet @publish
    if ($LASTEXITCODE -ne 0) { throw "Building Helm Keyboard ($arch) failed." }
}
Get-ChildItem (Join-Path $root 'artifacts/keyboard') -Recurse -Filter HelmKeyboard.dll | ForEach-Object { "$($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
