#!/usr/bin/env pwsh
# deploy.ps1  — Build and deploy the zRover UWP sample app via CLI.
# Run from d:\Rover (or any directory).
# Usage:
#   .\deploy.ps1              # Full build + deploy + launch
#   .\deploy.ps1 -SkipBuild   # Deploy existing binaries + launch
#   .\deploy.ps1 -SkipLaunch  # Build + deploy only, don't launch

param(
    [switch]$SkipBuild,
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"

$msbuild      = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
$sampleProj   = "d:\Rover\src\zRover.Uwp.Sample\zRover.Uwp.Sample.csproj"
$appxManifest = "d:\Rover\src\zRover.Uwp.Sample\bin\x64\Debug\AppX\AppxManifest.xml"
$pkgFamily    = "zRover.Uwp.Sample_xaf3bmhg52ma0"

Write-Host "=== zRover Deploy ===" -ForegroundColor Cyan

# ── 1. Kill running app (releases file locks) ──────────────────────────────
Write-Host "Stopping app..." -ForegroundColor Yellow
Get-Process | Where-Object { $_.Name -match "zRover" } |
    ForEach-Object { Write-Host "  Stopping $($_.Name) ($($_.Id))"; $_ | Stop-Process -Force }
Start-Sleep 2

# ── 2. Build ────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "Building zRover.Uwp.Sample..." -ForegroundColor Yellow
    & $msbuild $sampleProj /p:Configuration=Debug /p:Platform=x64 /t:Build /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed — check output above." }
    Write-Host "Build OK." -ForegroundColor Green
}

# ── 3. Verify key files ─────────────────────────────────────────────────────
if (-not (Test-Path $appxManifest)) {
    throw "AppxManifest.xml not found at: $appxManifest`nRun without -SkipBuild first."
}

# ── 4. Register ─────────────────────────────────────────────────────────────
Write-Host "Registering package..." -ForegroundColor Yellow
# Remove first to avoid "already installed" error when the layout hasn't changed
Remove-AppxPackage "zRover.Uwp.Sample_1.0.0.0_x64__xaf3bmhg52ma0" -ErrorAction SilentlyContinue
Start-Sleep 1
Add-AppxPackage -Register $appxManifest -ForceApplicationShutdown
Write-Host "Registered OK." -ForegroundColor Green

# ── 5. Launch ────────────────────────────────────────────────────────────────
if (-not $SkipLaunch) {
    Write-Host "Launching app..." -ForegroundColor Yellow
    Start-Process "shell:AppsFolder\$pkgFamily!App"

    Write-Host "Waiting for MCP server (port 5100)..." -ForegroundColor Yellow
    $ready = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep 1
        $conn = Test-NetConnection localhost -Port 5100 -InformationLevel Quiet -WarningAction SilentlyContinue
        if ($conn) { $ready = $true; break }
        Write-Host -NoNewline "."
    }
    Write-Host ""

    if ($ready) {
        Write-Host "Port 5100 ready — zRover is running!" -ForegroundColor Green
    } else {
        Write-Warning "Port 5100 not listening after 20s. Check event log for errors."
        Get-WinEvent Application -ErrorAction SilentlyContinue |
            Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-2) -and $_.Id -in 1000,1026 } |
            Select-Object -First 3 |
            ForEach-Object { Write-Host (($_.Message -replace '\s+', ' ')[0..300] -join '') -ForegroundColor Red }
    }
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
$dstDll = "$appxDir\zRover.Uwp.dll"
if (Test-Path $srcDll) {
    try {
        $copied = $false
        # Try normal copy first
        try {
            Copy-Item $srcDll $dstDll -Force
            $copied = $true
        } catch {
            # Normal copy failed (file locked) - use FileShare.ReadWrite approach
            # Rename existing destination first (rename works even when locked)
            if (Test-Path $dstDll) {
                Rename-Item $dstDll "$dstDll.old" -Force -ErrorAction SilentlyContinue
            }
            # Read source with shared access, write as new file
            $srcInfo = Get-Item $srcDll
            $fs = [System.IO.FileStream]::new($srcDll, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite,Delete')
            $bytes = New-Object byte[] $fs.Length
            $null = $fs.Read($bytes, 0, $bytes.Length)
            $fs.Close()
            [System.IO.File]::WriteAllBytes($dstDll, $bytes)
            # Clean up old
            Remove-Item "$dstDll.old" -Force -ErrorAction SilentlyContinue
            $copied = $true
        }
        if ($copied) {
            $info = Get-Item $dstDll
            Write-Host "  Copied $($info.Length) bytes, $($info.LastWriteTime)" -ForegroundColor Green
        }
    } catch {
        Write-Warning "  Could not copy zRover.Uwp.dll: $_"
    }
} else {
    Write-Warning "  Source DLL not found: $srcDll"
}

# 6. Copy resources.pri from deploy_uwp (deploy_uwp has the correct 10312-byte version)
Write-Host "Copying resources.pri from deploy_uwp..." -ForegroundColor Yellow
Copy-Item "$deployDir\resources.pri" "$appxDir\resources.pri" -Force
Write-Host "  $($(Get-Item "$appxDir\resources.pri").Length) bytes" -ForegroundColor Green

# 7. Copy entrypoint native stub from deploy_uwp
Write-Host "Copying entrypoint stub..." -ForegroundColor Yellow
Copy-Item "$deployDir\entrypoint\zRover.Uwp.Sample.exe" "$appxDir\entrypoint\zRover.Uwp.Sample.exe" -Force
Write-Host "  $($(Get-Item "$appxDir\entrypoint\zRover.Uwp.Sample.exe").Length) bytes" -ForegroundColor Green

# 8. Show final AppX state
Write-Host "`nAppX state after fixes:" -ForegroundColor Cyan
Get-ChildItem $appxDir -File | Where-Object {$_.Name -like "zRover*" -or $_.Name -eq "resources.pri" -or $_.Name -eq "AppxManifest.xml"} `
    | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

# 9. Register the package
Write-Host "Registering package..." -ForegroundColor Yellow
Add-AppxPackage -Register "$appxDir\AppxManifest.xml" -ForceApplicationShutdown 2>&1 | Out-Null
$status = (Get-AppxPackage $pkgFull).Status
Write-Host "  Registration status: $status" -ForegroundColor $(if ($status -eq "Ok") {"Green"} else {"Red"})

if ($status -ne "Ok") {
    Write-Error "Package registration failed!"
    exit 1
}

# 10. Launch and verify
if (-not $SkipLaunch) {
    Write-Host "`nLaunching app..." -ForegroundColor Yellow
    explorer $launchUri
    Write-Host "Waiting for MCP server to start (up to 20s)..." -ForegroundColor Yellow
    $started = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep 1
        $listening = netstat -ano 2>&1 | Select-String ":5100"
        if ($listening) {
            $started = $true
            break
        }
    }
    
    if ($started) {
        Write-Host "SUCCESS - MCP server listening on :5100!" -ForegroundColor Green
        netstat -ano | Select-String ":5100"
    } else {
        Write-Warning "MCP server did not start within 20s. Check manually."
        Get-Process | Where-Object {$_.Name -like "*zRover*"} | Select-Object Name, Id
    }
}

Write-Host "`n=== Deploy complete ===" -ForegroundColor Cyan
