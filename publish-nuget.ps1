<#
.SYNOPSIS
  Builds and publishes the zRover.Uwp NuGet package to nuget.org.

.DESCRIPTION
  1. Builds zRover.Uwp.dll (Release)
  2. Publishes the FullTrust companion for win-x64 and win-arm64
  3. Packs the .nupkg via PackHelper.csproj
  4. Pushes to nuget.org using the NUGET_API_KEY environment variable

.PARAMETER Version
  Package version to publish (e.g. "0.2.0-preview"). If omitted, uses
  the version already in zRover.Uwp.nuspec.

.PARAMETER DryRun
  Build and pack only; skip the push to nuget.org.

.EXAMPLE
  .\publish-nuget.ps1
  .\publish-nuget.ps1 -Version 0.2.0-preview
  .\publish-nuget.ps1 -DryRun
#>
param(
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$srcDir = Join-Path $repoRoot 'src'

# --- Resolve tools ---
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    # Fall back to vswhere
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath
        $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    }
}
if (-not (Test-Path $msbuild)) { throw "MSBuild not found. Install Visual Studio with the UWP workload." }

# --- Optional: stamp version into nuspec ---
$nuspecPath = Join-Path $srcDir 'zRover.Uwp\zRover.Uwp.nuspec'
if ($Version) {
    Write-Host "Stamping version $Version into nuspec..."
    $xml = [xml](Get-Content $nuspecPath -Raw)
    $xml.package.metadata.version = $Version
    $xml.Save($nuspecPath)
}

# Read the version we'll publish
$nuspecXml = [xml](Get-Content $nuspecPath -Raw)
$pkgVersion = $nuspecXml.package.metadata.version
Write-Host "=== Building zRover.Uwp $pkgVersion ===" -ForegroundColor Cyan

# --- 1. Build zRover.Uwp.dll (Release) ---
Write-Host "`n--- Building zRover.Uwp (Release) ---"
& $msbuild (Join-Path $srcDir 'zRover.Uwp\zRover.Uwp.csproj') `
    /p:Configuration=Release /p:Platform=AnyCPU /t:Build /v:minimal
if ($LASTEXITCODE -ne 0) { throw "zRover.Uwp build failed" }

# --- 2. Publish FullTrust companion for both RIDs ---
$ftDir = Join-Path $srcDir 'zRover.FullTrust.McpServer'
foreach ($rid in @('win-x64', 'win-arm64')) {
    Write-Host "`n--- Publishing FullTrust ($rid) ---"
    Push-Location $ftDir
    dotnet publish -c Release -r $rid --no-self-contained -o "bin\publish-fdd-$($rid -replace 'win-','')"
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "FullTrust publish ($rid) failed" }
    Pop-Location
}

# --- 3. Pack .nupkg ---
Write-Host "`n--- Packing NuGet package ---"
$packDir = Join-Path $srcDir 'zRover.Uwp\build'
$outDir = Join-Path $srcDir 'zRover.Uwp\bin\nupkg'
Push-Location $packDir
dotnet restore PackHelper.csproj --verbosity quiet
dotnet pack PackHelper.csproj -o $outDir --no-build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "NuGet pack failed" }
Pop-Location

$nupkg = Join-Path $outDir "zRover.Uwp.$pkgVersion.nupkg"
if (-not (Test-Path $nupkg)) { throw "Expected package not found: $nupkg" }
$sizeMB = [math]::Round((Get-Item $nupkg).Length / 1MB, 2)
Write-Host "`nPackage ready: $nupkg ($sizeMB MB)" -ForegroundColor Green

# --- 4. Push to nuget.org ---
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

Write-Host "`nPublished zRover.Uwp $pkgVersion to nuget.org" -ForegroundColor Green
