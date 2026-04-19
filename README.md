# zRover — Autonomous App Control for AI Agents on Windows

zRover gives development AI agents control over Windows apps, closing the inner loop so agents can test their own changes autonomously — no human in the middle to click through the UI after every code change.

It is built on the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) and exposes screenshot capture, input injection, app-defined action dispatch, package management, and more as MCP tools that any AI agent or test harness can call over HTTP.

## Components

zRover is composed of two independent components that can be used separately or combined for a full end-to-end solution:

| Component | What it does |
|---|---|
| **In-app integration** (`zRover.Uwp`) | A NuGet library you drop into your UWP app. It starts an in-process MCP server that exposes the app's UI and actions directly to any connected agent. |
| **Background service** (`zRover Retriever`) | A standalone Windows service (packaged MSIX) that provides app management (install, update, launch) and multi-device federation. When running, it becomes the single MCP entry point for all interactions across every app that uses the in-app integration, so agents only need one connection to reach everything. |

Use the **in-app integration** alone when you only need to control a single app. Add the **background service** when you also need to deploy builds, manage the app lifecycle, or federate multiple devices. Together they form the complete inner loop: an agent can deploy a new build, launch the app, interact with its UI through a single MCP endpoint, and verify the result — all without human involvement.

## Add zRover to Your UWP App

Install the NuGet package and integrate in three lines of code:

```
dotnet add package zRover.Uwp --prerelease
```

See the **[Integration Guide](docs/integration-guide.md)** for complete setup instructions, manifest configuration, MCP client setup for VS Code / Claude / Cursor / Windsurf, the full tool reference (22 tools), and troubleshooting (or just point your agent to this .md and ask it to set it up for you).

## Connect an MCP Client

Once your app is running, point any MCP client at `http://localhost:5100/mcp`:

[<img src="https://img.shields.io/badge/VS_Code-VS_Code?style=flat-square&label=Install%20Server&color=0098FF" alt="Install in VS Code">](https://vscode.dev/redirect/mcp/install?name=zrover&config=%7B%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D) [<img src="https://img.shields.io/badge/VS_Code_Insiders-VS_Code_Insiders?style=flat-square&label=Install%20Server&color=24bfa5" alt="Install in VS Code Insiders">](https://insiders.vscode.dev/redirect/mcp/install?name=zrover&config=%7B%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D) [<img src="https://img.shields.io/badge/Visual_Studio-Install-C16FDE?logo=visualstudio&logoColor=white" alt="Install in Visual Studio">](https://vs-open.link/mcp-install?%7B%22name%22%3A%22rover%22%2C%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D)

See [Connecting MCP Clients](docs/integration-guide.md#connecting-mcp-clients) for manual configuration for each client.

## Install zRover Retriever

zRover Retriever is the packaged Windows app that runs the MCP endpoint, manages package installs, and federates multi-device sessions. It is distributed as a signed MSIX via GitHub Releases.

### One-time setup (run as admin, PowerShell)

The package is signed with a self-signed certificate that must be trusted before installation.

```powershell
# Resolve latest release asset URLs from GitHub API
$release = Invoke-RestMethod https://api.github.com/repos/arcadiogarcia/zRover/releases/latest
$cerUrl  = ($release.assets | Where-Object { $_.name -like '*.cer'  }).browser_download_url
$msixUrl = ($release.assets | Where-Object { $_.name -like '*.msix' }).browser_download_url

# 1. Trust the signing certificate (one-time, requires admin)
$cer = "$env:TEMP\zRover.cer"
Invoke-WebRequest $cerUrl -OutFile $cer
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Remove-Item $cer

# 2. Install (or update) the MSIX for all users
$msix = "$env:TEMP\zRover.Retriever.msix"
Invoke-WebRequest $msixUrl -OutFile $msix
Add-AppxProvisionedPackage -Online -PackagePath $msix -SkipLicense
Remove-Item $msix
```

> **Updating:** Run only step 2 for subsequent releases. The certificate only needs to be trusted once per machine. `Add-AppxProvisionedPackage` installs the app for all users on the machine.

Download links and release notes for all versions are on the [Releases page](https://github.com/arcadiogarcia/zRover/releases).

## MCP Tools

zRover exposes **22 tools** across screenshot capture, touch/mouse, keyboard, pen, gamepad input, app-defined action dispatch, diagnostic logging, XAML UI tree inspection, window management, and condition polling. All input tools use normalized coordinates (0.0–1.0) by default.

See the **[full tool reference](docs/integration-guide.md#available-tools)** for parameters, coordinate spaces, and dry-run preview support.

## Contributing

See the **[Developer Guide](docs/contributing/dev-guide.md)** for architecture, project structure, build instructions, and how to run the test suite.
