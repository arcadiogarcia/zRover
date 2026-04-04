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

# ── 1. Publish binary layout ──────────────────────────────────────────────────
Write-Host "Publishing ($Config|$Arch)..."
dotnet publish $ProjectFile -c $Config -r $Rid --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# WinAppSDK assembles the layout into bin\Deploy\<Config>-<Arch> when the Deploy
# target runs. On CI runners where that target doesn't fire, fall back to the
# standard dotnet publish output and assemble the layout manually.
$DeployDir  = Join-Path $ProjectDir "bin\Deploy\$Config-$Arch"
$PublishDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0\$Rid\publish"

if (Test-Path $DeployDir) {
    $LayoutDir = $DeployDir
    Write-Host "Layout: $LayoutDir (WinAppSDK Deploy)"
} elseif (Test-Path $PublishDir) {
    $LayoutDir = $PublishDir
    Write-Host "Layout: $LayoutDir (manual assembly)"

    # AppxManifest.xml — patch ProcessorArchitecture
    $content = Get-Content (Join-Path $ProjectDir 'Package.appxmanifest') -Raw
    $content = $content -replace 'ProcessorArchitecture="[^"]*"', ('ProcessorArchitecture="' + $Arch + '"')
    Set-Content (Join-Path $LayoutDir 'AppxManifest.xml') -Value $content -Encoding UTF8

    # Assets folder
    Copy-Item (Join-Path $ProjectDir 'Assets') (Join-Path $LayoutDir 'Assets') -Recurse -Force

    # XBF and resources.pri from the build output (parent of publish/)
    $BuildOutDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0\$Rid"
    Get-ChildItem $BuildOutDir -Filter '*.xbf' -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $LayoutDir $_.Name) -Force
    }
    $priSrc = Get-ChildItem $BuildOutDir -Filter '*.pri' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($priSrc) { Copy-Item $priSrc.FullName (Join-Path $LayoutDir 'resources.pri') -Force }
} else {
    throw "MSIX layout not found at $DeployDir or $PublishDir — did dotnet publish succeed?"
}

# ── 2. Locate makeappx ──────────────────────────────────────────────────────────
$makeappx = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx) {
    $makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
        -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $makeappx) { throw 'makeappx.exe not found. Install the Windows SDK or restore NuGet packages.' }

# ── 3. Pack MSIX ────────────────────────────────────────────────────────────────
$OutDir   = Join-Path $ProjectDir "bin\$Config"
$MsixName = "zRover.Retriever_${Version}_$Arch.msix"
$MsixPath = Join-Path $OutDir $MsixName
Write-Host "Packing MSIX -> $MsixPath"
& $makeappx pack /d "$LayoutDir" /p "$MsixPath" /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }
Write-Host "Packed OK"

# ── 4. Locate signtool ──────────────────────────────────────────────────────────
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
    -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK.' }

# ── 5. Ensure signing cert ──────────────────────────────────────────────────────
$CertSubject = 'CN=zRover Dev Signing'
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

# ── 6. Sign the MSIX ────────────────────────────────────────────────────────────
Write-Host "Signing $MsixPath ..."
& $signtool sign /fd SHA256 /sha1 $thumb "$MsixPath"
if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed' }
Write-Host 'Signed OK'

# ── 7. Export public .cer alongside the .msix ───────────────────────────────────
$CerPath  = [System.IO.Path]::ChangeExtension($MsixPath, '.cer')
$certObj  = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb }
$certBytes = $certObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes($CerPath, $certBytes)
Write-Host "Exported cert -> $CerPath"

# ── 8. Generate .appinstaller ─────────────────────────────────────────────────
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


$ErrorActionPreference = 'Stop'
$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'zRover.Retriever.csproj'
$Rid         = "win-$Arch"

# ── 0. Stop any running instance so DLLs are not locked ───────────────────────
$running = Get-Process -Name 'zRover.Retriever' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping zRover.Retriever (PID $($running.Id -join ', '))..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# ── 1. Publish binary layout ──────────────────────────────────────────────────
Write-Host "Publishing ($Config|$Arch)..."
$LayoutDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0\$Rid\publish"
dotnet publish $ProjectFile -c $Config -r $Rid --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# ── 2. Assemble MSIX layout (AppxManifest, XBF, resources.pri, Assets) ────────

# AppxManifest.xml (patch ProcessorArchitecture to match the target RID)
$ManifestDst = Join-Path $LayoutDir 'AppxManifest.xml'
$content = Get-Content (Join-Path $ProjectDir 'Package.appxmanifest') -Raw
$content = $content -replace 'ProcessorArchitecture="[^"]*"', ('ProcessorArchitecture="' + $Arch + '"')
Set-Content $ManifestDst -Value $content -Encoding UTF8

# Assets folder
Copy-Item (Join-Path $ProjectDir 'Assets') (Join-Path $LayoutDir 'Assets') -Recurse -Force

# XBF and resources.pri from the build output (next to the binary)
$BuildOutDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0\$Rid"
Get-ChildItem $BuildOutDir -Filter '*.xbf' -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $LayoutDir $_.Name) -Force
}
$priSrc = Get-ChildItem $BuildOutDir -Filter '*.pri' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($priSrc) { Copy-Item $priSrc.FullName (Join-Path $LayoutDir 'resources.pri') -Force }

# ── 3. Pack MSIX with makeappx ────────────────────────────────────────────────
$makeappx = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx) {
    # Fall back to Windows SDK installation
    $makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
        -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $makeappx) { throw 'makeappx.exe not found. Install the Windows SDK or restore NuGet packages.' }

$OutDir  = Join-Path $ProjectDir "bin\$Config"
$MsixPath = Join-Path $OutDir "zRover.Retriever_1.0.0.0_$Arch.msix"
Write-Host "Packing MSIX -> $MsixPath"
& $makeappx pack /d "$LayoutDir" /p "$MsixPath" /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }
Write-Host "Packed OK"

# ── 3. Locate signtool from Windows SDK ───────────────────────────────────────
$SdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
$signtool = Get-ChildItem $SdkBin -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK.' }

# ── 4. Ensure dev signing cert (CN=zRover Dev Signing) ────────────────────────
$CertSubject = 'CN=zRover Dev Signing'
$StateFile   = Join-Path $env:LOCALAPPDATA 'zRover.Retriever\dev-cert.json'
$thumb       = $null

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

# ── 5. Sign the MSIX (cert must be in CurrentUser\My; trust not required yet) ──
Write-Host "Signing $MsixPath ..."
& $signtool sign /fd SHA256 /sha1 $thumb "$MsixPath"
if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed' }
Write-Host 'Signed OK'

# ── 6. Export public .cer alongside the .msix (for distribution) ──────────────
$certObj   = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb }
$certBytes = $certObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
$CerPath   = [System.IO.Path]::ChangeExtension($MsixPath, '.cer')
[System.IO.File]::WriteAllBytes($CerPath, $certBytes)
Write-Host "Exported cert -> $CerPath"

# ── 7. Trust cert in LocalMachine\TrustedPeople (once; UAC prompt) ────────────
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
if (-not $trusted) {
    Write-Host 'Trusting dev cert in LocalMachine\TrustedPeople (UAC prompt)...'
    Start-Process certutil -ArgumentList "-addstore TrustedPeople `"$CerPath`"" -Verb RunAs -Wait
}

# ── 8. Remove any previously installed version ────────────────────────────────
$existing = Get-AppxPackage | Where-Object { $_.Name -eq 'zRover.Retriever' }
if ($existing) {
    Write-Host "Removing previous: $($existing.PackageFullName)"
    Remove-AppxPackage $existing.PackageFullName
}

# ── 9. Install the signed MSIX ────────────────────────────────────────────────
Write-Host "Installing $MsixPath ..."
Add-AppxPackage $MsixPath

Write-Host ''
Write-Host 'Done. Package installed:'
Get-AppxPackage | Where-Object { $_.Name -eq 'zRover.Retriever' } |
    Select-Object Name, Version, PackageFullName | Format-List

Write-Host 'Startup task will appear in Task Manager > Startup apps after next login.'
