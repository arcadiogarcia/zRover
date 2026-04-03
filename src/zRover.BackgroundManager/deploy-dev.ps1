# deploy-dev.ps1
# Registers zRover.BackgroundManager as a loose-file MSIX package for development.
# Requires: Developer Mode enabled (Settings > Privacy & Security > For developers)
#
# Usage:
#   .\deploy-dev.ps1              # x64 Debug (default)
#   .\deploy-dev.ps1 -Arch arm64
#   .\deploy-dev.ps1 -Config Release

param(
    [ValidateSet('x64','x86','arm64')]
    [string] $Arch   = 'x64',
    [ValidateSet('Debug','Release')]
    [string] $Config = 'Debug'
)

$ErrorActionPreference = 'Stop'
$ProjectDir = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'zRover.BackgroundManager.csproj'
$Rid = "win-$Arch"

# 0. Stop any running instance so DLLs in the deploy folder are not locked
$running = Get-Process -Name 'zRover.BackgroundManager' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running zRover.BackgroundManager (PID $($running.Id -join ', '))..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# 1. Publish to a stable layout folder
$LayoutDir = Join-Path $ProjectDir "bin\Deploy\$Config-$Arch"
Write-Host "Building ($Config|$Arch) -> $LayoutDir"
dotnet publish $ProjectFile -c $Config -r $Rid --no-self-contained -o $LayoutDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# 1b. Copy WinUI build artifacts that dotnet publish does not include (.pri, .xbf)
$BuildDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0"
# Look for resources.pri in the build output (may be named resources.pri or <ProjectName>.pri)
$PriCandidates = @(
    (Join-Path $BuildDir 'resources.pri'),
    (Join-Path $BuildDir 'zRover.BackgroundManager.pri'),
    (Join-Path (Join-Path $BuildDir $Rid) 'resources.pri'),
    (Join-Path (Join-Path $BuildDir $Rid) 'zRover.BackgroundManager.pri')
)
$PriSrc = $PriCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $PriSrc) {
    Write-Host 'Building to generate WinUI artifacts...'
    dotnet build $ProjectFile -c $Config -r $Rid --no-self-contained --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
    $PriSrc = $PriCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($PriSrc) {
    Copy-Item $PriSrc (Join-Path $LayoutDir 'resources.pri') -Force
    Write-Host "Copied resources.pri from $PriSrc"
} else {
    Write-Warning 'resources.pri not found -- WinUI may fail to start'
}
# Copy compiled XAML binaries (.xbf) from both base and RID-specific build dirs
foreach ($dir in @($BuildDir, (Join-Path $BuildDir $Rid))) {
    Get-ChildItem $dir -Filter '*.xbf' -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $LayoutDir $_.Name) -Force
        Write-Host "Copied $($_.Name)"
    }
}

# 2. Copy AppxManifest
$ManifestSrc = Join-Path $ProjectDir 'Package.appxmanifest'
$ManifestDst = Join-Path $LayoutDir 'AppxManifest.xml'
Copy-Item $ManifestSrc $ManifestDst -Force

# Patch ProcessorArchitecture to match the target RID
$content = Get-Content $ManifestDst -Raw
$content = $content -replace 'ProcessorArchitecture="[^"]*"', ('ProcessorArchitecture="' + $Arch + '"')
Set-Content $ManifestDst -Value $content -Encoding UTF8

# 3. Copy Assets folder
$AssetsSrc = Join-Path $ProjectDir 'Assets'
$AssetsDst = Join-Path $LayoutDir 'Assets'
if (Test-Path $AssetsSrc) {
    Copy-Item $AssetsSrc $AssetsDst -Recurse -Force
}

# 4. Remove any previously registered version of this package
$existing = Get-AppxPackage | Where-Object { $_.Name -eq 'zRover.BackgroundManager' }
if ($existing) {
    Write-Host "Removing previous registration: $($existing.PackageFullName)"
    Remove-AppxPackage $existing.PackageFullName
}

# 5. Register the loose-file layout (no signing needed in Developer Mode)
Write-Host "Registering package from $ManifestDst"
Add-AppxPackage -Register $ManifestDst -ForceApplicationShutdown

Write-Host ''
Write-Host 'Done. Package registered:'
Get-AppxPackage | Where-Object { $_.Name -eq 'zRover.BackgroundManager' } |
    Select-Object Name, Version, PackageFullName | Format-List

Write-Host 'Startup task will appear in Task Manager > Startup apps after next login.'
