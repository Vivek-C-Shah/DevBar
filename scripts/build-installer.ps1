#requires -version 5.1
<#
.SYNOPSIS
    Builds the DevBar installer end to end: self-contained publish + Inno
    Setup compile. One command instead of two, and finds dotnet/ISCC.exe
    itself instead of asking you to hardcode a path.

.EXAMPLE
    .\scripts\build-installer.ps1
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # winget installs the SDK to Program Files and adds it to the machine
    # PATH, but a terminal opened before the install won't see that until
    # it's restarted - fall back to the well-known path so this script
    # works even in that stale-session case.
    $candidate = "$env:ProgramFiles\dotnet\dotnet.exe"
    if (Test-Path $candidate) { return $candidate }

    throw "dotnet SDK not found. Install it with: winget install Microsoft.DotNet.SDK.8`n" +
          "Then open a NEW terminal window (PATH only refreshes for new sessions) and re-run this script."
}

function Find-Iscc {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    throw "Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup`n" +
          "Then re-run this script."
}

$dotnet = Find-Dotnet
$iscc = Find-Iscc
Write-Host "Using dotnet: $dotnet" -ForegroundColor DarkGray
Write-Host "Using ISCC:   $iscc" -ForegroundColor DarkGray

Write-Host "`n[1/2] Publishing self-contained build..." -ForegroundColor Cyan
& $dotnet publish src\DevBar\DevBar.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

Write-Host "`n[2/2] Compiling installer..." -ForegroundColor Cyan
& $iscc installer\DevBar.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed (exit $LASTEXITCODE)" }

$setup = Get-ChildItem dist\DevBar-Setup-*.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nDone: $($setup.FullName)" -ForegroundColor Green
