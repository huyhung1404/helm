<#
.SYNOPSIS
  SPIKE: registers the Helm Keyboard build next to this script (keyboard\x64, keyboard\x86) in place of the installed
  one. Apps opened afterwards type with it; apps already open keep the old one until they restart.
  To go back: restart Helm. On start it sees a keyboard that is not its own, registers its own again and deletes this
  copy once no app uses it.
.EXAMPLE
  Right-click > Run with PowerShell (asks for administrator), or in an elevated PowerShell:
  ./spike-install-keyboard.ps1
#>
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    return
}

# regsvr32 is a GUI program: '&' does not wait for it and sets no exit code, so run it with -Wait and read ExitCode.
function Register-Dll([string]$regsvr32, [string]$dll) {
    $process = Start-Process $regsvr32 -ArgumentList '/s', "`"$dll`"" -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "regsvr32 failed for $dll (exit code $($process.ExitCode))" }
}

try {
    $x64 = Join-Path $PSScriptRoot 'keyboard\x64\HelmKeyboard.dll'
    $x86 = Join-Path $PSScriptRoot 'keyboard\x86\HelmKeyboard.dll'
    foreach ($dll in $x64, $x86) { if (-not (Test-Path -LiteralPath $dll)) { throw "Missing $dll" } }

    $hash = (Get-FileHash -LiteralPath $x64 -Algorithm SHA256).Hash.Substring(0, 8).ToLowerInvariant()
    $target = Join-Path $env:ProgramFiles "Helm Keyboard\spike-$hash"
    foreach ($arch in 'x64', 'x86') {
        New-Item -ItemType Directory -Force (Join-Path $target $arch) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "keyboard\$arch\HelmKeyboard.dll") (Join-Path $target $arch) -Force
    }
    Register-Dll (Join-Path $env:windir 'System32\regsvr32.exe') (Join-Path $target 'x64\HelmKeyboard.dll')
    Register-Dll (Join-Path $env:windir 'SysWOW64\regsvr32.exe') (Join-Path $target 'x86\HelmKeyboard.dll')

    Write-Host "Spike keyboard registered from $target" -ForegroundColor Green
    Write-Host "To go back: restart Helm."
}
catch {
    Write-Host "FAILED: $_" -ForegroundColor Red
}
Read-Host 'Press Enter to close'
