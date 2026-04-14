# deploy-dev.ps1
# Builds, signs, and installs zRover.WinUI.Sample as a dev MSIX so it can be
# deployed via Rover's install_package tool (file:// URI) instead of loose-manifest
# Add-AppPackage -Register.
#
# Usage:
#   .\deploy-dev.ps1              # ARM64 Debug (default)
#   .\deploy-dev.ps1 -Arch x64
#   .\deploy-dev.ps1 -Config Release
#   .\deploy-dev.ps1 -SkipBuild   # pack + sign + install from existing build output

param(
    [ValidateSet('x64','x86','ARM64')]
    [string] $Arch        = 'ARM64',
    [ValidateSet('Debug','Release')]
    [string] $Config      = 'Debug',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'zRover.WinUI.Sample.csproj'

# ── Read version from Package.appxmanifest ────────────────────────────────────
$ManifestXml = [xml](Get-Content (Join-Path $ProjectDir 'Package.appxmanifest') -Raw)
$ns      = @{ a = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10' }
$Version = (Select-Xml -Xml $ManifestXml -XPath '/a:Package/a:Identity/@Version' -Namespace $ns).Node.Value
if (-not $Version) { throw 'Could not read Version from Package.appxmanifest' }
Write-Host "Version: $Version"

# ── 1. Locate MSBuild ─────────────────────────────────────────────────────────
function Find-MSBuild {
    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($vsPath) {
            foreach ($suffix in 'MSBuild\Current\Bin\amd64\MSBuild.exe','MSBuild\Current\Bin\MSBuild.exe') {
                $candidate = Join-Path $vsPath $suffix
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }
    throw 'MSBuild.exe not found. Install Visual Studio 2022.'
}

# ── 2. Build ──────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    $msbuild = if ($env:MSBUILD_PATH -and (Test-Path $env:MSBUILD_PATH)) { $env:MSBUILD_PATH } else { Find-MSBuild }
    Write-Host "Building ($Config|$Arch) with MSBuild..."
    & $msbuild $ProjectFile /p:Configuration=$Config /p:Platform=$Arch /t:Build /m /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'MSBuild failed' }
    Write-Host 'Build OK'
}

# ── 3. Locate AppX layout produced by WinAppSDK tooling ──────────────────────
# Older SDK/VS builds put the layout in a nested AppX\ subfolder; newer ones
# put it directly in the bin output directory. Accept either.
$BinDir    = Join-Path $ProjectDir "bin\$Arch\$Config\net8.0-windows10.0.19041.0"
$LayoutDir = Join-Path $BinDir "AppX"
if (-not (Test-Path $LayoutDir)) {
    if (Test-Path (Join-Path $BinDir "AppxManifest.xml")) {
        $LayoutDir = $BinDir
    } else {
        throw "AppX layout not found at $LayoutDir (or $BinDir) - did the build succeed?"
    }
}
Write-Host "Layout: $LayoutDir"

# WinAppSDK MSIX packaging targets don't refresh the AppX layout on incremental
# builds, so we always copy zRover.* DLLs from the regular bin output to AppX.
# Skip when LayoutDir IS BinDir to avoid self-copy errors.
if ($LayoutDir -ne $BinDir) {
    Get-ChildItem $BinDir -Filter 'zRover.*.dll' | ForEach-Object {
        $dest = Join-Path $LayoutDir $_.Name
        if (Test-Path $dest) { Copy-Item $_.FullName $dest -Force }
    }
}

# ── 4. Locate makeappx ───────────────────────────────────────────────────────
$makeappx = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx) {
    $makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
        -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $makeappx) { throw 'makeappx.exe not found. Install the Windows SDK or restore NuGet packages.' }

# ── 5. Patch publisher in layout manifest then pack MSIX ─────────────────────
$CertSubject = 'CN=zRover Dev Signing'
$OutDir      = Join-Path $ProjectDir "bin\$Config"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$MsixName    = "zRover.WinUI.Sample_${Version}_$Arch.msix"
$MsixPath    = Join-Path $OutDir $MsixName

$LayoutManifest = Join-Path $LayoutDir 'AppxManifest.xml'
if (Test-Path $LayoutManifest) {
    $xml = [xml](Get-Content $LayoutManifest -Encoding UTF8)
    $ns2 = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns2.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $xml.SelectSingleNode('//m:Identity', $ns2)
    if ($identity -and $identity.Publisher -ne $CertSubject) {
        Write-Host "Patching manifest Publisher: '$($identity.Publisher)' -> '$CertSubject'"
        $identity.Publisher = $CertSubject
        $xml.Save($LayoutManifest)
    }
}

Write-Host "Packing MSIX -> $MsixPath"
& $makeappx pack /d "$LayoutDir" /p "$MsixPath" /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }
Write-Host 'Packed OK'

# ── 6. Locate signtool ───────────────────────────────────────────────────────
$signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
        -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK or restore NuGet packages.' }

# ── 7. Ensure dev signing cert ────────────────────────────────────────────────
# Reuse the same cert as zRover.Retriever (same subject + state file) so only
# one cert needs to be trusted on the dev machine.
$thumb     = $env:SIGNING_CERT_THUMBPRINT
$StateFile = Join-Path $env:LOCALAPPDATA 'zRover.Retriever\dev-cert.json'

if (-not $thumb) {
    if (Test-Path $StateFile) {
        try {
            $state = Get-Content $StateFile -Raw | ConvertFrom-Json
            $thumb = $state.thumbprint
            $match = Get-ChildItem Cert:\CurrentUser\My |
                Where-Object { $_.Thumbprint -eq $thumb -and $_.HasPrivateKey }
            if (-not $match) { $thumb = $null }
        } catch { $thumb = $null }
    }

    if (-not $thumb) {
        Write-Host 'Creating dev signing cert...'
        $cert  = New-SelfSignedCertificate `
            -Subject $CertSubject `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyUsage DigitalSignature `
            -Type CodeSigningCert `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(10)
        $thumb = $cert.Thumbprint
        $stateDir = Split-Path $StateFile
        if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir | Out-Null }
        [ordered]@{ thumbprint = $thumb; subject = $CertSubject } |
            ConvertTo-Json | Set-Content $StateFile -Encoding UTF8
        Write-Host "Dev cert created (thumb: $thumb)"
    } else {
        Write-Host "Reusing existing dev cert (thumb: $thumb)"
    }
}

# ── 8. Sign the MSIX ─────────────────────────────────────────────────────────
Write-Host "Signing $MsixPath ..."
& $signtool sign /fd SHA256 /sha1 $thumb "$MsixPath"
if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed' }
Write-Host 'Signed OK'

# ── 9. Done — print URI for Rover install_package ────────────────────────────
Write-Host ''
Write-Host 'Done. Use this URI with Rover install_package:'
Write-Host "  file:///$($MsixPath -replace '\\','/')"
