#!/usr/bin/env pwsh
# deploy.ps1 -- Build and deploy the zRover UWP sample app via CLI.
# Run from any directory -- all paths are derived from the script's own location.
#
# Usage:
#   .\deploy.ps1                        # ARM64 Debug build + deploy + launch
#   .\deploy.ps1 -Arch x64              # x64 instead of ARM64
#   .\deploy.ps1 -Config Release        # Release build
#   .\deploy.ps1 -SkipBuild             # Deploy existing binaries + launch
#   .\deploy.ps1 -SkipLaunch            # Build + deploy only, don't launch

param(
    [ValidateSet('ARM64','x64','x86')]
    [string] $Arch   = 'ARM64',
    [ValidateSet('Debug','Release')]
    [string] $Config = 'Debug',
    [switch] $SkipBuild,
    [switch] $SkipLaunch
)

$ErrorActionPreference = "Stop"

# -- Resolve paths relative to this script ------------------------------------
$repoRoot     = $PSScriptRoot
$sampleProj   = Join-Path $repoRoot "src\zRover.Uwp.Sample\zRover.Uwp.Sample.csproj"
$appxManifest = Join-Path $repoRoot "src\zRover.Uwp.Sample\bin\$Arch\$Config\AppX\AppxManifest.xml"

# -- Locate MSBuild (prefer amd64 host; fall back to any VS 2022 install) -----
function Find-MSBuild {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    # Try vswhere as last resort
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($vsPath) {
            foreach ($suffix in "MSBuild\Current\Bin\amd64\MSBuild.exe","MSBuild\Current\Bin\MSBuild.exe") {
                $candidate = Join-Path $vsPath $suffix
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }
    throw "MSBuild.exe not found. Install Visual Studio 2022 or set `$env:MSBUILD_PATH."
}

$msbuild = if ($env:MSBUILD_PATH -and (Test-Path $env:MSBUILD_PATH)) { $env:MSBUILD_PATH } else { Find-MSBuild }

Write-Host "=== zRover Deploy ($Arch | $Config) ===" -ForegroundColor Cyan
Write-Host "  Repo:    $repoRoot"
Write-Host "  MSBuild: $msbuild"

# -- 1. Stop running app (releases file locks) --------------------------------
Write-Host "`nStopping any running zRover processes..." -ForegroundColor Yellow
Get-Process | Where-Object { $_.Name -match "^zRover" } |
    ForEach-Object { Write-Host "  Stopping $($_.Name) ($($_.Id))"; $_ | Stop-Process -Force -ErrorAction SilentlyContinue }
Start-Sleep 2

# -- 2. Build -----------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "`nBuilding zRover.Uwp.Sample ($Arch | $Config)..." -ForegroundColor Yellow
    & $msbuild $sampleProj /p:Configuration=$Config /p:Platform=$Arch /t:Build /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed - check output above." }
    Write-Host "Build OK." -ForegroundColor Green
}

# -- 3. Verify key file -------------------------------------------------------
if (-not (Test-Path $appxManifest)) {
    throw "AppxManifest.xml not found at:`n  $appxManifest`nRun without -SkipBuild first, or verify -Arch / -Config match the build."
}

# -- 4. Register --------------------------------------------------------------
Write-Host "`nRegistering package..." -ForegroundColor Yellow
# Remove any installed version (all architectures) to avoid conflicts
Get-AppxPackage -Name "zRover.Uwp.Sample" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  Removing $($_.PackageFullName)"; Remove-AppxPackage $_ -ErrorAction SilentlyContinue }
Start-Sleep 1
Add-AppxPackage -Register $appxManifest -ForceApplicationShutdown
$installed = Get-AppxPackage -Name "zRover.Uwp.Sample"
Write-Host "Registered OK -- $($installed.PackageFullName)" -ForegroundColor Green

# -- 5. Launch ----------------------------------------------------------------
if (-not $SkipLaunch) {
    $appId     = (Get-AppxPackageManifest $installed).Package.Applications.Application.Id
    $launchUri = "shell:AppsFolder\$($installed.PackageFamilyName)!$appId"

    Write-Host "`nLaunching app ($launchUri)..." -ForegroundColor Yellow
    Start-Process $launchUri

    Write-Host "Waiting for MCP server (port 5100)..." -ForegroundColor Yellow
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep 1
        $listening = netstat -ano 2>$null | Select-String ":5100 " | Select-String "LISTEN"
        if ($listening) { $ready = $true; break }
        Write-Host -NoNewline "."
    }
    Write-Host ""

    if ($ready) {
        $serverPid  = ($listening[0].ToString() -split '\s+')[-1]
        $serverProc = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
        Write-Host "Port 5100 ready -- $($serverProc.Name) (PID $serverPid)" -ForegroundColor Green
    } else {
        Write-Warning "Port 5100 not listening after 30s. Check event log for errors."
        Get-WinEvent -LogName Application -ErrorAction SilentlyContinue |
            Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-2) -and $_.Id -in 1000,1026 } |
            Select-Object -First 3 |
            ForEach-Object { Write-Host (($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(300, $_.Message.Length))) -ForegroundColor Red }
    }
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan