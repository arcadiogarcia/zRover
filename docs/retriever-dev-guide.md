# zRover Retriever — Developer Guide

This guide covers everything needed to build, modify, test, and deploy the **zRover Retriever** (`zRover.Retriever` project). The Retriever is a packaged WinAppSDK desktop app that runs as a startup task and exposes an MCP endpoint for package management and session orchestration.

## Table of Contents

- [Project Overview](#project-overview)
- [Prerequisites](#prerequisites)
- [Building](#building)
- [Generating and Installing the MSIX](#generating-and-installing-the-msix)
  - [Quick start (build + sign + install)](#quick-start-build--sign--install)
  - [How the pipeline works](#how-the-pipeline-works)
- [Day-to-day Development Workflow](#day-to-day-development-workflow)
- [Changing the MSIX Identity or Version](#changing-the-msix-identity-or-version)
- [Publishing a Release (GitHub Actions)](#publishing-a-release-github-actions)
  - [One-time setup: export and store the cert secret](#one-time-setup-export-and-store-the-cert-secret)
  - [Releasing a new version](#releasing-a-new-version)
  - [End-user installation](#end-user-installation)
  - [Auto-update](#auto-update)
- [Running the Tests](#running-the-tests)
- [Project Structure](#project-structure)

---

## Project Overview

| Item | Value |
|---|---|
| **Project file** | `src/zRover.Retriever/zRover.Retriever.csproj` |
| **Target framework** | `net9.0-windows10.0.19041.0` |
| **Default architecture** | `win-x64` (also supports `win-x86`, `win-arm64`) |
| **MSIX package identity** | `zRover.Retriever` |
| **Package publisher** | `CN=zRover Dev Signing` (matches the dev cert) |
| **Default MCP port** | 5200 |
| **Startup task** | Registered automatically on install; toggle in Task Manager → Startup apps |

---

## Prerequisites

- **Windows 10** (build 19041) or later
- **.NET 9 SDK** (`dotnet --version` should show `9.x`)
- **Windows App SDK runtime 1.8** installed (the MSIX declares a package dependency on `Microsoft.WindowsAppRuntime.1.8`; the installer pulls it in automatically)
- **Windows SDK** with `signtool.exe` (part of the "Windows SDK Signing Tools for Desktop Apps" component in Visual Studio, or standalone via [developer.microsoft.com/windows/downloads/windows-sdk](https://developer.microsoft.com/windows/downloads/windows-sdk/))
- If `signtool.exe` is not in `C:\Program Files (x86)\Windows Kits\10\bin\…`, `deploy-dev.ps1` falls back to a NuGet-cached copy from `microsoft.windows.sdk.buildtools` which is restored automatically via NuGet

---

## Building

A normal `dotnet build` compiles the project without packaging:

```powershell
cd src\zRover.Retriever
dotnet build -c Debug -r win-x64
```

Because `WindowsPackageType=MSIX` is set in the project, MSBuild configures the WinAppSDK MSIX targets, but **packing is a separate step** driven by `deploy-dev.ps1`. Plain builds in Visual Studio compile normally without packaging or installing.

---

## Generating and Installing the MSIX

### Quick start (build + sign + install)

Run from the project directory:

```powershell
cd src\zRover.Retriever
.\deploy-dev.ps1                  # Debug|x64  (default)
.\deploy-dev.ps1 -Config Release  # Release|x64
.\deploy-dev.ps1 -Arch arm64      # Debug|arm64
```

**On the first run**, the script creates a self-signed code-signing certificate (`CN=zRover Dev Signing`) in your `CurrentUser\My` cert store and shows a **UAC prompt** to trust it in `LocalMachine\TrustedPeople`. After that one-time prompt, all future runs are fully unattended.

> **Note:** The UAC prompt cannot be shown from VS Code's embedded terminal. Run `deploy-dev.ps1` from a regular PowerShell window (Windows Terminal, pwsh, etc.) for the first run.

### How the pipeline works

`deploy-dev.ps1` performs these steps in order:

| Step | What happens |
|---|---|
| **0. Stop running instance** | Kills any live `zRover.Retriever.exe` so its DLLs aren't locked (skipped with `-SkipInstall`) |
| **1. Read version** | Parses `Version` from `Package.appxmanifest` — used in filenames and `.appinstaller` |
| **2. `dotnet publish`** | Compiles and publishes to `bin\<Config>\net9.0-…\win-<Arch>\publish\` |
| **3. Assemble MSIX layout** | Copies `AppxManifest.xml` (architecture patched), `Assets\`, `.xbf` files, and `resources.pri` |
| **4. `makeappx pack`** | Creates `bin\<Config>\zRover.Retriever_<Version>_<Arch>.msix` |
| **5. Sign MSIX** | `signtool sign /fd SHA256 /sha1 <thumb> …` — cert loaded from `CurrentUser\My` |
| **6. Export `.cer`** | Public cert exported alongside the `.msix` for distribution |
| **7. Generate `.appinstaller`** | XML pointing at the GitHub Releases download URLs |
| **8. Trust cert** | First run only: UAC prompt to add cert to `LocalMachine\TrustedPeople` (skipped with `-SkipInstall`) |
| **9. Uninstall previous** | `Remove-AppxPackage` if an older version exists (skipped with `-SkipInstall`) |
| **10. Install** | `Add-AppxPackage <path>` (skipped with `-SkipInstall`) |

The dev cert state file at `%LOCALAPPDATA%\zRover.Retriever\dev-cert.json` is shared with `DevCertManager.cs` at runtime (for auto-signing packages before installation via MCP tools), so the two stay in sync automatically.

---

## Day-to-day Development Workflow

After the initial setup, iteration is fast:

1. Make code changes in `src\zRover.Retriever\`.
2. Run `.\deploy-dev.ps1` (or `.\deploy-dev.ps1 -Config Release`).
3. The script stops the running instance, rebuilds, repacks, re-signs, and reinstalls — usually under 30 seconds for an incremental compile.

The startup task re-launches the new version automatically at the next login. To run it immediately without logging out:

```powershell
Start-Process "shell:AppsFolder\zRover.Retriever_eqad6j9zhtw80!App"
```

(The package family name suffix `eqad6j9zhtw80` is deterministic for publisher `CN=zRover Dev Signing`.)

---

## Changing the MSIX Identity or Version

The MSIX identity lives in `src\zRover.Retriever\Package.appxmanifest`:

```xml
<Identity
  Name="zRover.Retriever"
  Publisher="CN=zRover Dev Signing"
  ProcessorArchitecture="x64"
  Version="1.0.0.0" />
```

- **`Publisher`** must match the signing cert subject exactly. For dev builds this is always `CN=zRover Dev Signing` — do not change it unless you also change the cert.
- **`Version`** must be a four-part number (`Major.Minor.Patch.Build`). Increment for each release to allow upgrading without removing the old package first.

After any manifest change, run `.\deploy-dev.ps1` to rebuild and reinstall.

---

## Publishing a Release (GitHub Actions)

The workflow at [`.github/workflows/release-retriever.yml`](../.github/workflows/release-retriever.yml) triggers on version tags (`v1.0.0.0`, `v1.2.3.4`, …) and publishes a GitHub Release with three assets:

```
zRover.Retriever_1.0.0.0_x64.msix        ← signed MSIX
zRover.Retriever_1.0.0.0_x64.cer         ← public signing cert (users trust once)
zRover.Retriever.appinstaller            ← stable URL, enables auto-update
```

### One-time setup: export and store the cert secret

This must be done **from your dev machine** where the cert was created by `deploy-dev.ps1`:

```powershell
# 1. Find the cert thumbprint
$thumb = (Get-Content "$env:LOCALAPPDATA\zRover.Retriever\dev-cert.json" | ConvertFrom-Json).thumbprint

# 2. Export as PFX (you'll be prompted to set a password)
certutil -exportPFX My $thumb "$env:TEMP\zrover-signing.pfx"

# 3. Base64-encode for the GitHub secret
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:TEMP\zrover-signing.pfx")) |
    Set-Clipboard
Write-Host "PFX base64 copied to clipboard"

# 4. Clean up
Remove-Item "$env:TEMP\zrover-signing.pfx"
```

Then in your GitHub repo go to **Settings → Secrets and variables → Actions** and add:

| Secret name | Value |
|---|---|
| `SIGNING_CERT_PFX_B64` | The base64 string you copied |
| `SIGNING_CERT_PASSWORD` | The password you set during export |

> **Important:** This cert must never be regenerated. If it is, all existing users will need to re-trust a new cert. Store the PFX backup securely outside the repo.

### Releasing a new version

1. Bump `Version` in `src/zRover.Retriever/Package.appxmanifest` (e.g. `1.0.0.0` → `1.0.1.0`)
2. Commit and push
3. Tag the commit matching the manifest version:
   ```powershell
   git tag v1.0.1.0
   git push origin v1.0.1.0
   ```
4. GitHub Actions builds, signs, and publishes the release automatically (~2 min)

### End-user installation

Users install from the GitHub Releases page — no Developer Mode required:

1. Download `zRover.Retriever_<version>_x64.cer` → double-click → **Install Certificate** → **Local Machine** → **Trusted People** (one admin UAC — first time only)
2. Download `zRover.Retriever.appinstaller` → double-click → Windows App Installer handles the rest

Or direct them to this deep link (opens Windows App Installer immediately):
```
ms-appinstaller:?source=https://github.com/arcadiogarcia/zRover/releases/latest/download/zRover.Retriever.appinstaller
```

### Auto-update

After the first install, Windows App Installer polls the `.appinstaller` URL every 24 hours. When a new version is released, users see an update prompt on next app launch (or it updates silently in the background — controlled by `UpdateBlocksActivation` in the `.appinstaller` XML).

---

## Running the Tests

The Retriever has its own unit test project at `src\zRover.Retriever.Tests\`:

```powershell
dotnet test src\zRover.Retriever.Tests\zRover.Retriever.Tests.csproj
```

These tests are pure unit tests with no app dependency. Coverage includes:
- `PackageInstallManagerTests` — enable/disable state, events, lazy cert init, error handling
- `PackageStagingManagerTests` — upload lifecycle, temp file cleanup
- `PackageStagingEndpointTests` — HTTP endpoint validation
- `PackageInstallManagerTests` — install gate enforcement

To run a specific class:

```powershell
dotnet test src\zRover.Retriever.Tests\zRover.Retriever.Tests.csproj `
    --filter "ClassName=zRover.Retriever.Tests.PackageInstallManagerTests"
```

---

## Project Structure

```
src/zRover.Retriever/
├── Package.appxmanifest        # MSIX identity, capabilities, startup task, protocol
├── app.manifest                # Win32 app manifest (DPI awareness, UAC level)
├── deploy-dev.ps1              # Build → pack → sign → install script
├── appsettings.json            # Default ports, log levels
├── Program.cs                  # Host builder entry point
├── Worker.cs                   # Background service: MCP server lifecycle
├── App.xaml / App.xaml.cs      # WinUI application entry point
├── MainWindow.xaml / .cs       # System tray-style settings window
├── SessionNotificationService.cs
│
├── Packages/                   # MSIX package management
│   ├── DevCertManager.cs       # Self-signed cert creation, signing (signtool), trust (UAC)
│   ├── IDevCertManager.cs
│   ├── PackageInstallManager.cs # Security gate (opt-in toggle) for install/uninstall/upload
│   ├── LocalDevicePackageManager.cs # PackageManager API + auto-sign before install
│   ├── PackageStagingManager.cs    # Temp file handling for upload-then-install flow
│   └── …
│
├── Server/                     # MCP tool handlers
│   ├── DevicePackageManagementTools.cs
│   ├── SessionManagementTools.cs
│   ├── ExternalAccessManager.cs
│   └── PackageStagingEndpoint.cs
│
└── Sessions/                   # Session registry and remote federation
    ├── SessionRegistry.cs
    ├── McpClientSession.cs
    ├── PropagatedSession.cs
    └── RemoteManagerRegistry.cs
```
