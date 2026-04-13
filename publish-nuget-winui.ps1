<#
.SYNOPSIS
  Builds and publishes the zRover.WinUI NuGet package to nuget.org.

.DESCRIPTION
  1. Builds zRover.WinUI (Release, x64)
  2. Packs the .nupkg via dotnet pack
  3. Pushes to nuget.org using the NUGET_API_KEY environment variable

  WinUI 3 apps run full-trust, so no FullTrust companion process is required.
  The MCP server runs in-process.

.PARAMETER Version
  Package version to publish (e.g. "0.1.0-preview"). If omitted, uses the
  version already in zRover.WinUI.csproj.

.PARAMETER DryRun
  Build and pack only; skip the push to nuget.org.

.EXAMPLE
  .\publish-nuget-winui.ps1
  .\publish-nuget-winui.ps1 -Version 0.1.0-preview
  .\publish-nuget-winui.ps1 -DryRun
#>
param(
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$projPath = Join-Path $repoRoot 'src\zRover.WinUI\zRover.WinUI.csproj'
$outDir   = Join-Path $repoRoot 'src\zRover.WinUI\bin\nupkg'

# --- Optional: stamp version into csproj ---
if ($Version) {
    Write-Host "Stamping version $Version into csproj..."
    $content = Get-Content $projPath -Raw
    $content = $content -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
    Set-Content $projPath $content -Encoding UTF8
}

# Read the version we'll publish
$xml = [xml](Get-Content $projPath -Raw)
$pkgVersion = $xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1 | ForEach-Object { $_.Version }
Write-Host "=== Building zRover.WinUI $pkgVersion ===" -ForegroundColor Cyan

# --- Resolve MSBuild ---
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath
        $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    }
}
if (-not (Test-Path $msbuild)) { throw "MSBuild not found. Install Visual Studio with the WinUI/Desktop workload." }

# --- 1. Build (x64 Release) ---
Write-Host "`n--- Building zRover.WinUI (Release, x64) ---"
& $msbuild $projPath /p:Configuration=Release /p:Platform=x64 /t:Build /v:minimal
if ($LASTEXITCODE -ne 0) { throw "zRover.WinUI build failed" }

# --- 2. Pack .nupkg ---
Write-Host "`n--- Packing NuGet package ---"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
dotnet pack $projPath -c Release /p:Platform=x64 --no-build -o $outDir
if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed" }

$nupkg = Join-Path $outDir "zRover.WinUI.$pkgVersion.nupkg"
if (-not (Test-Path $nupkg)) { throw "Expected package not found: $nupkg" }
$sizeMB = [math]::Round((Get-Item $nupkg).Length / 1MB, 2)
Write-Host "`nPackage ready: $nupkg ($sizeMB MB)" -ForegroundColor Green

# --- 3. Push to nuget.org ---
if ($DryRun) {
    Write-Host "`n[DryRun] Skipping push to nuget.org" -ForegroundColor Yellow
    return
}

$apiKey = $env:NUGET_API_KEY
if (-not $apiKey) { $apiKey = [Environment]::GetEnvironmentVariable('NUGET_API_KEY', 'User') }
if (-not $apiKey) {
    throw "NUGET_API_KEY environment variable is not set.`nSet it with: [Environment]::SetEnvironmentVariable('NUGET_API_KEY', 'your-key', 'User')"
}

Write-Host "`n--- Pushing to nuget.org ---"
dotnet nuget push $nupkg --source https://api.nuget.org/v3/index.json --api-key $apiKey --skip-duplicate
if ($LASTEXITCODE -ne 0) { throw "NuGet push failed" }

Write-Host "`nPublished zRover.WinUI $pkgVersion to nuget.org" -ForegroundColor Green
