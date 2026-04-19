# deploy-dev.ps1
# Builds, signs, and installs zRover.Retriever as a proper MSIX package.
# Requires: Windows SDK (signtool.exe) or microsoft.windows.sdk.buildtools NuGet package.
#
# Usage (local dev):
#   .\deploy-dev.ps1              # x64 Debug (default)
#   .\deploy-dev.ps1 -Arch arm64
#   .\deploy-dev.ps1 -Config Release
#
# Usage (CI / GitHub Actions — skips trust UAC and Add-AppxPackage):
#   .\deploy-dev.ps1 -Config Release -SkipInstall
#   Set env var SIGNING_CERT_THUMBPRINT if cert is already imported by CI.

param(
    [ValidateSet('x64','x86','arm64')]
    [string] $Arch        = 'x64',
    [ValidateSet('Debug','Release')]
    [string] $Config      = 'Debug',
    [switch] $SkipInstall           # CI: skip UAC trust + Add-AppxPackage
)

$ErrorActionPreference = 'Stop'
$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'zRover.Retriever.csproj'
$Rid         = "win-$Arch"

# ── Read version from Package.appxmanifest ────────────────────────────────────
$ManifestXml = [xml](Get-Content (Join-Path $ProjectDir 'Package.appxmanifest') -Raw)
$ns = @{ a = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10' }
$Version = (Select-Xml -Xml $ManifestXml -XPath '/a:Package/a:Identity/@Version' -Namespace $ns).Node.Value
if (-not $Version) { throw 'Could not read Version from Package.appxmanifest' }
Write-Host "Version: $Version"

# ── 0. Stop any running instance so DLLs are not locked ───────────────────────
if (-not $SkipInstall) {
    $running = Get-Process -Name 'zRover.Retriever' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping zRover.Retriever (PID $($running.Id -join ', '))..."
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

# ── 1. Clean previous build output ─────────────────────────────────────────
# Only clean final artifacts (.msix/.cer/.appinstaller). The bin\Deploy layout
# folder is an MSBuild/WinAppSDK intermediate — leave it so the WinAppSDK path
# is used when available; dotnet publish alone does not regenerate it.
Write-Host "Cleaning previous build output..."
Get-ChildItem (Join-Path $ProjectDir "bin\$Config") -Filter "zRover.Retriever_*_$Arch.*" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "  Removed $($_.Name)" }
$oldAppInstaller = Join-Path $ProjectDir "bin\$Config\zRover.Retriever.appinstaller"
if (Test-Path $oldAppInstaller) { Remove-Item $oldAppInstaller -Force; Write-Host "  Removed zRover.Retriever.appinstaller" }

# ── 2. Publish binary layout ──────────────────────────────────────────────────
Write-Host "Publishing ($Config|$Arch)..."
dotnet publish $ProjectFile -c $Config -r $Rid
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# WinAppSDK assembles the layout into bin\Deploy\<Config>-<Arch> when the Deploy
# target runs (VS build). When running dotnet publish alone (CI / command line)
# that target doesn't fire, so we fall back to the regular build output dir
# (bin\<Config>\net9.0-...\<rid>) which already contains Assets\, XBF files,
# and <ProjectName>.pri — we just need to add AppxManifest.xml and rename the
# PRI to resources.pri. Do NOT use the publish\ subdirectory: it has a broken
# PRI that lacks the scale-variant mappings the splash screen resolver needs.
$DeployDir   = Join-Path $ProjectDir "bin\Deploy\$Config-$Arch"
$BuildOutDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0\$Rid"

if (Test-Path $DeployDir) {
    $LayoutDir = $DeployDir
    Write-Host "Layout: $LayoutDir (WinAppSDK Deploy)"
} elseif (Test-Path $BuildOutDir) {
    $LayoutDir = $BuildOutDir
    Write-Host "Layout: $LayoutDir (manual assembly from build output)"

    # AppxManifest.xml — patch ProcessorArchitecture to match the target RID
    $content = Get-Content (Join-Path $ProjectDir 'Package.appxmanifest') -Raw
    $content = $content -replace 'ProcessorArchitecture="[^"]*"', ('ProcessorArchitecture="' + $Arch + '"')
    Set-Content (Join-Path $LayoutDir 'AppxManifest.xml') -Value $content -Encoding UTF8

    # Rename <ProjectName>.pri -> resources.pri (makeappx expects this name)
    $priSrc = Get-ChildItem $BuildOutDir -Filter '*.pri' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'resources.pri' } | Select-Object -First 1
    if ($priSrc) {
        Copy-Item $priSrc.FullName (Join-Path $LayoutDir 'resources.pri') -Force
    } elseif (-not (Test-Path (Join-Path $LayoutDir 'resources.pri'))) {
        throw "No .pri file found in $BuildOutDir - did the build succeed?"
    }

    # Remove the publish\ subdirectory dotnet publish creates inside the build
    # output dir — it duplicates all DLLs and would be packed as publish\*.dll
    $publishSubdir = Join-Path $BuildOutDir 'publish'
    if (Test-Path $publishSubdir) {
        Remove-Item $publishSubdir -Recurse -Force
        Write-Host "  Removed publish\ subdirectory from layout"
    }
} else {
    throw "MSIX layout not found at $DeployDir or $BuildOutDir - did the build succeed?"
}

# ── 3. Locate makeappx ──────────────────────────────────────────────────────────
$makeappx = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx) {
    $makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
        -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $makeappx) { throw 'makeappx.exe not found. Install the Windows SDK or restore NuGet packages.' }

# ── 4. Patch publisher in layout manifest then pack MSIX ────────────────────────
$CertSubject = 'CN=zRover Dev Signing'
$OutDir   = Join-Path $ProjectDir "bin\$Config"
$MsixName = "zRover.Retriever_${Version}_$Arch.msix"
$MsixPath = Join-Path $OutDir $MsixName

$LayoutManifest = Join-Path $LayoutDir 'AppxManifest.xml'
if (Test-Path $LayoutManifest) {
    $xml = [xml](Get-Content $LayoutManifest -Encoding UTF8)
    $ns  = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $xml.SelectSingleNode('//m:Identity', $ns)
    if ($identity -and $identity.Publisher -ne $CertSubject) {
        Write-Host "Patching manifest Publisher: '$($identity.Publisher)' -> '$CertSubject'"
        $identity.Publisher = $CertSubject
        $xml.Save($LayoutManifest)
    }
}

Write-Host "Packing MSIX -> $MsixPath"
& $makeappx pack /d "$LayoutDir" /p "$MsixPath" /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }
Write-Host "Packed OK"

# ── 5. Locate signtool ──────────────────────────────────────────────────────────
$signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
        -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK or restore NuGet packages.' }

# ── 6. Ensure signing cert ──────────────────────────────────────────────────────
$thumb       = $env:SIGNING_CERT_THUMBPRINT  # CI sets this after importing PFX

if (-not $thumb) {
    # Local dev: load from state file or create fresh
    $StateFile = Join-Path $env:LOCALAPPDATA 'zRover.Retriever\dev-cert.json'
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

# ── 7. Sign the MSIX ────────────────────────────────────────────────────────────
Write-Host "Signing $MsixPath ..."
& $signtool sign /fd SHA256 /sha1 $thumb "$MsixPath"
if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed' }
Write-Host 'Signed OK'

# ── 8. Export public .cer alongside the .msix ───────────────────────────────────
$CerPath  = [System.IO.Path]::ChangeExtension($MsixPath, '.cer')
$certObj  = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb }
$certBytes = $certObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes($CerPath, $certBytes)
Write-Host "Exported cert -> $CerPath"

# ── 9. Generate .appinstaller ─────────────────────────────────────────────────
$Tag             = "v$Version"
$BaseUrl         = "https://github.com/arcadiogarcia/zRover/releases"
$AppInstallerUrl = "$BaseUrl/latest/download/zRover.Retriever.appinstaller"
$MsixUrl         = "$BaseUrl/download/$Tag/$MsixName"

$appInstaller = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller
  xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
  Version="$Version"
  Uri="$AppInstallerUrl">

  <MainPackage
    Name="zRover.Retriever"
    Version="$Version"
    Publisher="CN=zRover Dev Signing"
    Uri="$MsixUrl"
    ProcessorArchitecture="$Arch" />

  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="24" UpdateBlocksActivation="false" />
  </UpdateSettings>
</AppInstaller>
"@

$AppInstallerPath = Join-Path $OutDir 'zRover.Retriever.appinstaller'
Set-Content $AppInstallerPath -Value $appInstaller.Trim() -Encoding UTF8
Write-Host "Generated -> $AppInstallerPath"

if ($SkipInstall) {
    Write-Host ''
    Write-Host "SkipInstall: done. Release assets in $OutDir :"
    Write-Host "  $MsixName"
    Write-Host "  $([System.IO.Path]::GetFileName($CerPath))"
    Write-Host "  zRover.Retriever.appinstaller"
    exit 0
}

# ── 10. Trust cert in LocalMachine\TrustedPeople (once; UAC prompt) ───────────
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
if (-not $trusted) {
    Write-Host 'Trusting dev cert in LocalMachine\TrustedPeople (UAC prompt)...'
    Start-Process certutil -ArgumentList "-addstore TrustedPeople `"$CerPath`"" -Verb RunAs -Wait
}

# ── 11. Remove any previously installed version ───────────────────────────────
$existing = Get-AppxPackage | Where-Object { $_.Name -eq 'zRover.Retriever' }
if ($existing) {
    Write-Host "Removing previous: $($existing.PackageFullName)"
    Remove-AppxPackage $existing.PackageFullName
}

# ── 12. Install ───────────────────────────────────────────────────────────────
Write-Host "Installing $MsixPath ..."
Add-AppxPackage $MsixPath

Write-Host ''
Write-Host 'Done. Package installed:'
Get-AppxPackage | Where-Object { $_.Name -eq 'zRover.Retriever' } |
    Select-Object Name, Version, PackageFullName | Format-List

Write-Host 'Startup task will appear in Task Manager > Startup apps after next login.'
