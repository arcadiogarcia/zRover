# zRover — MCP In-App Debug Host for UWP

zRover is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) debug host that runs inside a UWP application, exposing screenshot capture, input injection, and app-defined action dispatch as MCP tools. External clients (AI agents, test harnesses, etc.) connect over HTTP and can remotely observe and interact with the running app.

## Add zRover to Your UWP App

Install the NuGet package and integrate in three lines of code:

```
dotnet add package zRover.Uwp --prerelease
```

See the **[Integration Guide](docs/integration-guide.md)** for complete setup instructions, manifest configuration, MCP client setup for VS Code / Claude / Cursor / Windsurf, the full tool reference (22 tools), and troubleshooting.

## Connect an MCP Client

Once your app is running, point any MCP client at `http://localhost:5100/mcp`:

[<img src="https://img.shields.io/badge/VS_Code-VS_Code?style=flat-square&label=Install%20Server&color=0098FF" alt="Install in VS Code">](https://vscode.dev/redirect/mcp/install?name=zrover&config=%7B%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D) [<img src="https://img.shields.io/badge/VS_Code_Insiders-VS_Code_Insiders?style=flat-square&label=Install%20Server&color=24bfa5" alt="Install in VS Code Insiders">](https://insiders.vscode.dev/redirect/mcp/install?name=zrover&config=%7B%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D) [<img src="https://cursor.com/deeplink/mcp-install-dark.svg" alt="Install in Cursor">](https://cursor.com/en/install-mcp?name=zrover&config=eyJ1cmwiOiJodHRwOi8vbG9jYWxob3N0OjUxMDAvbWNwIn0%3D) [<img src="https://img.shields.io/badge/Visual_Studio-Install-C16FDE?logo=visualstudio&logoColor=white" alt="Install in Visual Studio">](https://vs-open.link/mcp-install?%7B%22name%22%3A%22rover%22%2C%22url%22%3A%22http%3A%2F%2Flocalhost%3A5100%2Fmcp%22%7D)

See [Connecting MCP Clients](docs/integration-guide.md#connecting-mcp-clients) for manual configuration for each client.

## Architecture

```
MCP Client (tests, AI agents, etc.)
    │
    │  HTTP Streamable MCP (port 5100)
    ▼
┌──────────────────────────────────┐
│  zRover.FullTrust.McpServer       │  .NET 8 console app
│  ASP.NET Core  MapMcp("/mcp")    │  Runs as FullTrust process
│                                  │
│  AppServiceConnection IPC        │
└──────────┬───────────────────────┘
           │  ValueSet messages
           │  (ping, list_tools, invoke_tool)
           ▼
┌──────────────────────────────────┐
│  zRover.Uwp.Sample               │  UWP AppContainer
│  ├─ App.xaml.cs                  │  In-process AppService handler
│  │  OnBackgroundActivated()      │
│  │                               │
│  ├─ DebugHost / DebugHostRunner  │  Orchestrates capabilities
│  │                               │
│  ├─ ToolRegistry (singleton)     │  Thread-safe tool lookup
│  │                               │
│  └─ Capabilities                 │
│     ├─ ScreenshotCapability      │  RenderTargetBitmap → PNG
│     ├─ InputInjectionCapability  │  InputInjector + Win32 SendInput
│     ├─ LoggingCapability         │  In-memory ring buffer (get_logs)
│     ├─ AppActionCapability       │  Delegates to IActionableApp
│     ├─ UiTreeCapability          │  XAML VisualTreeHelper walker
│     ├─ WindowCapability          │  ApplicationView resize
│     └─ WaitForCapability         │  visual_stable / log_match polling
└──────────────────────────────────┘
```

### Data flow for a tool call

1. Client sends MCP `tools/call` via HTTP to `:5100/mcp`
2. FullTrust server receives it, looks up the proxy tool, sends an AppService IPC message
3. UWP app's `OnBackgroundActivated` dispatches to `ToolRegistry`
4. The capability handler runs (e.g. captures screenshot on UI thread)
5. JSON result flows back: capability → AppService → FullTrust → MCP HTTP response

## Projects

| Project | Target | Purpose |
|---|---|---|
| **zRover.Core** | netstandard2.0 | Interfaces (`IDebugCapability`, `IMcpToolRegistry`, `IToolBackend`), DTOs, coordinate types |
| **zRover.Mcp** | netstandard2.0 | MCP SDK adapter — bridges `IMcpToolRegistry` to `McpServerTool` via `DelegateMcpServerTool` |
| **zRover.Uwp** | UAP 10.0.19041 | UWP class library — debug host, capabilities, AppService handler, coordinate resolver |
| **zRover.Uwp.Sample** | UAP 10.0.19041 | Sample UWP app with Color Picker test UI for E2E testing |
| **zRover.FullTrust.McpServer** | net8.0-windows | Out-of-process MCP HTTP server, bridges to UWP via AppService IPC |
| **zRover.Mcp.IntegrationTests** | net8.0 | 55 xUnit tests (unit + E2E) |

## MCP Tools

zRover exposes **22 tools** across screenshot capture, touch/mouse, keyboard, pen, gamepad input, app-defined action dispatch, diagnostic logging, XAML UI tree inspection, window management, and condition polling. All input tools use normalized coordinates (0.0–1.0) by default.

See the **[full tool reference](docs/integration-guide.md#available-tools)** for parameters, coordinate spaces, and dry-run preview support.

## Prerequisites

- **Windows 10/11** (Desktop)
- **Visual Studio 2022** with the UWP workload
- **.NET 8 SDK**
- **Developer Mode** enabled in Windows Settings → Privacy & Security → For Developers

## Building

All commands assume the working directory is the repository root.

### 1. Build and deploy the UWP app

```powershell
# Resolve devenv.exe via vswhere (works for any VS edition/version)
$devenv = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find Common7\IDE\devenv.exe

# Rebuild (compiles zRover.Uwp + zRover.Uwp.Sample; post-build target syncs FullTrust output into AppX\FullTrust\)
& $devenv src\zRover.sln /Rebuild "Debug|x64" /Project "zRover.Uwp.Sample\zRover.Uwp.Sample.csproj"

# Deploy (registers the AppX package for sideloading)
& $devenv src\zRover.sln /Deploy "Debug|x64" /Project "zRover.Uwp.Sample\zRover.Uwp.Sample.csproj"
```

> **Important:** Always use `devenv /Deploy` for deployment. Manual `Add-AppxPackage -Register` with file copying to the AppX directory is unreliable because the `bin\x64\Debug\AppX` subdirectory may contain stale files.

### 2. Launch the app

```powershell
Start-Process "shell:AppsFolder\zRover.Uwp.Sample_xaf3bmhg52ma0!App"
```

The app starts the debug host and launches the FullTrust MCP server automatically. Wait a few seconds, then verify:

```powershell
Test-NetConnection -ComputerName localhost -Port 5100 | Select-Object TcpTestSucceeded
```

## Running Tests

### Unit tests only (no app needed)

```powershell
dotnet test src\zRover.Mcp.IntegrationTests\zRover.Mcp.IntegrationTests.csproj `
    --filter "McpServerToolTests|McpToolRegistryAdapterTests|DelegateMcpServerToolInvocationTests|AppActionMcpHandlerTests"
```

Runs 27 tests that exercise the MCP adapter and App Action protocol handlers in isolation. No UWP app required.

### E2E tests only (app must be running on port 5100)

```powershell
dotnet test src\zRover.Mcp.IntegrationTests\zRover.Mcp.IntegrationTests.csproj `
    --filter "EndToEndPipelineTests|ColorPickerE2ETests"
```

Runs 28 tests that connect to the live MCP server. Requires the UWP app to be deployed, launched, and listening on port 5100.

### All tests

```powershell
dotnet test src\zRover.Mcp.IntegrationTests\zRover.Mcp.IntegrationTests.csproj
```

Runs all 55 tests. The E2E tests will fail if the app isn't running.

### Full workflow (build + deploy + launch + test)

```powershell
$devenv = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find Common7\IDE\devenv.exe

# 1. Rebuild UWP app (post-build target auto-syncs FullTrust build output into AppX)
& $devenv src\zRover.sln /Rebuild "Debug|x64" /Project "zRover.Uwp.Sample\zRover.Uwp.Sample.csproj"

# 2. Deploy
& $devenv src\zRover.sln /Deploy "Debug|x64" /Project "zRover.Uwp.Sample\zRover.Uwp.Sample.csproj"

# 3. Launch app and wait for MCP server
Start-Process "shell:AppsFolder\zRover.Uwp.Sample_xaf3bmhg52ma0!App"
Start-Sleep -Seconds 5

# 4. Run all tests
dotnet test src\zRover.Mcp.IntegrationTests\zRover.Mcp.IntegrationTests.csproj
```

### Environment variable

The E2E tests default to `http://localhost:5100/mcp`. Override with:

```powershell
$env:ZROVER_MCP_ENDPOINT = "http://localhost:5100/mcp"
```

## What's Not Yet Implemented

- **Non-UWP platforms** — no WinUI 3 or WPF adapter; UWP only
- **Auth** — Bearer token opt-in exists in `DebugHostOptions` but is disabled by default
