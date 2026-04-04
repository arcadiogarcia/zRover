# zRover Integration Guide

Add AI-driven UI automation to any UWP app. zRover exposes your app's screen and input as [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) tools, letting AI agents, test harnesses, and other MCP clients capture screenshots, inject touch/mouse/keyboard/pen/gamepad input, and validate coordinates — all over a standard HTTP endpoint.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Install the NuGet Package](#install-the-nuget-package)
- [Integration Steps](#integration-steps)
  - [1. Add the Package Reference](#1-add-the-package-reference)
  - [2. Update Package.appxmanifest](#2-update-packageappxmanifest)
  - [3. Wire Up App.xaml.cs](#3-wire-up-appxamlcs)
- [Configuration](#configuration)
- [Connecting MCP Clients](#connecting-mcp-clients)
  - [VS Code / GitHub Copilot](#vs-code--github-copilot)
  - [Claude Desktop](#claude-desktop)
  - [Claude Code](#claude-code)
  - [Cursor](#cursor)
  - [Windsurf](#windsurf)
  - [Programmatic (C#)](#programmatic-c)
- [Available Tools](#available-tools)
  - [Screenshot Tools](#screenshot-tools)
  - [Touch & Mouse Tools](#touch--mouse-tools)
  - [Keyboard Tools](#keyboard-tools)
  - [Pen Input Tools](#pen-input-tools)
  - [Gamepad Tools](#gamepad-tools)
  - [App Action Tools](#app-action-tools)
  - [Logging Tools](#logging-tools)
  - [UI Tree Tools](#ui-tree-tools)
  - [Window Tools](#window-tools)
  - [Wait Tools](#wait-tools)
  - [Retriever: Package Management Tools](#retriever-package-management-tools)
- [Coordinate Spaces](#coordinate-spaces)
- [Preview & Dry Run](#preview--dry-run)
- [Requirements & Limitations](#requirements--limitations)
- [Troubleshooting](#troubleshooting)

---

## Prerequisites

- **Windows 10** (version 1904 / build 19041) or later
- **.NET 8 runtime** installed on the target machine
- **Developer Mode** enabled: Settings → Privacy & Security → For Developers
- A UWP app targeting **UAP 10.0.19041** or higher

## Install the NuGet Package

> **Debug builds only.** zRover is a development and testing tool — it exposes your app's screen and input over an unauthenticated local HTTP endpoint. **Never include zRover in release or production builds.** Exclude it from your release configuration (see [Add the Package Reference](#1-add-the-package-reference)).

```
dotnet add package zRover.Uwp --prerelease
```

Or add to your `.csproj`:

```xml
<PackageReference Include="zRover.Uwp" />
```

Omit the `Version` attribute to always restore the latest available pre-release. If you need to pin a version, pass `--version` to the CLI or add a `Version` attribute.

The package includes:
- **zRover.Uwp.dll** — the UWP library you call from your app
- **FullTrust companion server** — a .NET 8 MCP HTTP server (included for both x64 and ARM64)
- **MSBuild targets** — automatically adds the FullTrust binaries to your AppX package and references the Desktop Extensions SDK

## Integration Steps

### 1. Add the Package Reference

Add the `zRover.Uwp` NuGet package to your UWP app project **for debug configurations only**. The simplest way is to wrap the reference in a condition so it is excluded from release builds:

```xml
<PackageReference Include="zRover.Uwp" Condition="'$(Configuration)' == 'Debug'" />
```

This ensures zRover's FullTrust binaries, manifest extensions, and capabilities are never shipped in a release package.

The package's MSBuild auto-import handles:

- Referencing the **Windows Desktop Extensions SDK** (needed for `FullTrustProcessLauncher`)
- Including the FullTrust companion server binaries in your AppX under `FullTrust\`

### 2. Update Package.appxmanifest

Open your `Package.appxmanifest` and add the required namespaces, extensions, and capabilities.

**Add XML namespaces** to the root `<Package>` element:

```xml
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  IgnorableNamespaces="uap rescap desktop">
```

**Add extensions** inside your `<Application>` element:

```xml
<Extensions>
  <!-- In-process AppService for internal communication -->
  <uap:Extension Category="windows.appService">
    <uap:AppService Name="com.zrover.toolinvocation" />
  </uap:Extension>

  <!-- FullTrust MCP server packaged alongside your app -->
  <desktop:Extension Category="windows.fullTrustProcess"
                     Executable="FullTrust\zRover.FullTrust.McpServer.exe">
    <desktop:FullTrustProcess>
      <desktop:ParameterGroup GroupId="McpServer" Parameters="" />
    </desktop:FullTrustProcess>
  </desktop:Extension>
</Extensions>
```

**Add capabilities** inside `<Capabilities>`:

```xml
<Capabilities>
  <Capability Name="internetClient" />
  <Capability Name="internetClientServer" />
  <Capability Name="privateNetworkClientServer" />
  <rescap:Capability Name="runFullTrust" />
  <rescap:Capability Name="inputInjectionBrokered" />
</Capabilities>
```

> **Note:** The `inputInjectionBrokered` restricted capability enables input injection. Without it, input tools will fall back to XAML automation (which supports a subset of interactions). The `runFullTrust` capability is required to launch the companion MCP server.

### 3. Wire Up App.xaml.cs

zRover requires exactly **three touch-points** in your `App.xaml.cs`:

```csharp
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

sealed partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
        this.Suspending += OnSuspending;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        Frame rootFrame = Window.Current.Content as Frame;

        if (rootFrame == null)
        {
            rootFrame = new Frame();
            Window.Current.Content = rootFrame;
        }

        if (e.PrelaunchActivated == false)
        {
            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }

        // --- zRover: Start the MCP server ---
        await zRover.Uwp.RoverMcp.StartAsync("MyApp", port: 5100,
            () => FullTrustProcessLauncher
                .LaunchFullTrustProcessForCurrentAppAsync("McpServer")
                .AsTask());
    }

    // --- zRover: Route AppService requests ---
    protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
    {
        if (zRover.Uwp.RoverMcp.HandleBackgroundActivation(args)) return;
        base.OnBackgroundActivated(args);
    }

    private void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();

        // --- zRover: Clean shutdown ---
        zRover.Uwp.RoverMcp.Stop();

        deferral.Complete();
    }
}
```

That's it. Build and run your app — the MCP server will start listening on `http://localhost:5100/mcp`.

## Configuration

`RoverMcp.StartAsync` accepts the following parameters:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `appName` | `string` | *(required)* | Display name shown to MCP clients |
| `port` | `int` | `5100` | TCP port the MCP server listens on |
| `launchFullTrust` | `Func<Task>?` | `null` | Callback to launch the FullTrust companion. Pass the `FullTrustProcessLauncher` call as shown above. If `null`, no companion server starts (tools register but aren't reachable externally). |
| `actionableApp` | `IActionableApp?` | `null` | Optional implementation of `zRover.Core.IActionableApp` to expose app-defined actions via the `list_actions` and `dispatch_action` MCP tools. See [App Action Tools](#app-action-tools). |

## Connecting MCP Clients

Once your app is running, any MCP client can connect to `http://localhost:<port>/mcp`. The server speaks [Streamable HTTP MCP](https://modelcontextprotocol.io/specification/2025-03-26/basic/transports#streamable-http).

### VS Code / GitHub Copilot

Add to `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "rover": {
      "type": "http",
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

Or via CLI:
```
code --add-mcp '{"name":"zrover","url":"http://localhost:5100/mcp"}'
```

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rover": {
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

### Claude Code

```
claude mcp add rover --transport http --url http://localhost:5100/mcp
```

### Cursor

Go to **Cursor Settings** → **MCP** → **New MCP Server**, then add:

```json
{
  "mcpServers": {
    "rover": {
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

### Windsurf

Add to your MCP config:

```json
{
  "mcpServers": {
    "rover": {
      "serverUrl": "http://localhost:5100/mcp"
    }
  }
}
```

### Programmatic (C#)

```csharp
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5100/mcp")
});
var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
```

## Available Tools

zRover registers **22 tools** organized across screenshot capture, touch/mouse, keyboard, pen, gamepad input, app-defined action dispatch, logging, UI tree inspection, window management, and condition polling. The Retriever additionally exposes **10 package management tools** (see [Retriever: Package Management Tools](#background-manager-package-management-tools)).

### Screenshot Tools

#### `capture_current_view`

Captures the entire app window as a PNG screenshot.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `format` | string | `"png"` | Image format |
| `maxWidth` | integer | `1280` | Maximum image width in pixels (scaled proportionally) |
| `maxHeight` | integer | `1280` | Maximum image height in pixels |

**Returns:** `{ success, filePath, bitmapWidth, bitmapHeight, windowWidth, windowHeight }` — `bitmapWidth`/`bitmapHeight` are the PNG dimensions; `windowWidth`/`windowHeight` are the render-pixel size of the window (use for the `pixels` coordinate space). Convert to normalized: `normalizedX = bitmapX / bitmapWidth`.

#### `validate_position`

Captures the app window with a crosshair overlay drawn at the specified coordinates. Use this to visually confirm a target position before injecting input.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `x` | number | *(required)* | X in normalized space (0.0–1.0) |
| `y` | number | *(required)* | Y in normalized space (0.0–1.0) |
| `maxWidth` | integer | `1280` | Maximum screenshot width |
| `maxHeight` | integer | `1280` | Maximum screenshot height |

#### `capture_region`

Captures a cropped rectangular region of the app window.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `x` | number | *(required)* | Left edge (normalized 0.0–1.0) |
| `y` | number | *(required)* | Top edge (normalized 0.0–1.0) |
| `width` | number | *(required)* | Region width (normalized) |
| `height` | number | *(required)* | Region height (normalized) |
| `maxWidth` | integer | `1280` | Maximum output width |
| `maxHeight` | integer | `1280` | Maximum output height |

---

### Touch & Mouse Tools

#### `inject_tap`

Taps at the specified coordinates. Returns an annotated screenshot showing the tap location **before** injection for verification.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `x` | number | *(required)* | X coordinate |
| `y` | number | *(required)* | Y coordinate |
| `coordinateSpace` | string | `"normalized"` | `"normalized"` (0.0–1.0 relative to window) or `"pixels"` (render pixels matching `windowWidth`/`windowHeight` from `capture_current_view`) |
| `device` | string | `"touch"` | `"touch"` or `"mouse"` |
| `dryRun` | boolean | `false` | If `true`, returns the preview screenshot without injecting |

#### `inject_drag_path`

Drags along a path of points. Returns an annotated screenshot with the drag path visualized (green start → cyan path → red end).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `points` | array | *(required)* | Array of `{ x, y }` objects (minimum 2) |
| `durationMs` | integer | `300` | Total drag duration in milliseconds |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `device` | string | `"touch"` | `"touch"` or `"mouse"` |
| `dryRun` | boolean | `false` | Preview only |

#### `inject_mouse_move`

Moves the mouse cursor to the specified coordinates without clicking.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `x` | number | *(required)* | Target X |
| `y` | number | *(required)* | Target Y |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `dryRun` | boolean | `false` | Preview only |

#### `inject_mouse_scroll`

Scrolls the mouse wheel at the specified position. One notch = 120 units.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `x` | number | *(required)* | Scroll position X |
| `y` | number | *(required)* | Scroll position Y |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `deltaY` | integer | `-120` | Vertical scroll (negative = down, positive = up) |
| `deltaX` | integer | `0` | Horizontal scroll (negative = left, positive = right) |
| `dryRun` | boolean | `false` | Preview only |

#### `inject_multi_touch`

Injects a multi-touch gesture with multiple simultaneous contact points.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pointers` | array | *(required)* | Array of pointer objects (see below) |
| `durationMs` | integer | `400` | Total gesture duration |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `dryRun` | boolean | `false` | Preview only |

Each pointer object:
| Field | Type | Description |
|---|---|---|
| `id` | integer | Unique pointer ID (1-based) |
| `path` | array | Array of `{ x, y }` waypoints |
| `pressure` | number | Contact pressure 0.0–1.0 (default 1.0) |
| `orientation` | integer | Contact angle 0–359 degrees (default 0) |
| `contactWidth` | integer | Contact patch width (default 4) |
| `contactHeight` | integer | Contact patch height (default 4) |

#### `inject_pinch`

Injects a two-finger pinch gesture. `endDistance < startDistance` = pinch in (zoom out); `endDistance > startDistance` = pinch out (zoom in).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `centerX` | number | *(required)* | Center X of pinch |
| `centerY` | number | *(required)* | Center Y of pinch |
| `startDistance` | number | `0.3` | Starting finger distance (normalized) |
| `endDistance` | number | `0.1` | Ending finger distance |
| `angle` | number | `0` | Pinch axis angle in degrees |
| `durationMs` | integer | `400` | Duration |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `dryRun` | boolean | `false` | Preview only |

#### `inject_rotate`

Injects a two-finger rotation gesture. Positive `endAngle` = clockwise.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `centerX` | number | *(required)* | Center X |
| `centerY` | number | *(required)* | Center Y |
| `distance` | number | `0.2` | Finger distance from center (normalized) |
| `startAngle` | number | `0` | Starting angle in degrees |
| `endAngle` | number | `90` | Ending angle in degrees |
| `durationMs` | integer | `400` | Duration |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `dryRun` | boolean | `false` | Preview only |

---

### Keyboard Tools

#### `inject_key_press`

Presses a keyboard key with optional modifiers.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `key` | string | *(required)* | Virtual key name (e.g. `"Enter"`, `"Tab"`, `"A"`, `"Escape"`, `"Left"`, `"F5"`, `"Back"`) |
| `modifiers` | array | `[]` | Modifier keys: `"Control"`, `"Shift"`, `"Menu"` (Alt), `"Windows"` |
| `holdDurationMs` | integer | `0` | How long to hold the key |

#### `inject_text`

Types a string by injecting individual key presses per character.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `text` | string | *(required)* | Text to type |
| `delayBetweenKeysMs` | integer | `30` | Delay between keystrokes |

---

### Pen Input Tools

#### `inject_pen_tap`

Taps with a pen stylus. Supports pressure, tilt, rotation, barrel button, and eraser mode.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `x` | number | *(required)* | X coordinate |
| `y` | number | *(required)* | Y coordinate |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `pressure` | number | `0.5` | Pen pressure 0.0–1.0 |
| `tiltX` | integer | `0` | X tilt (-90 to 90 degrees) |
| `tiltY` | integer | `0` | Y tilt (-90 to 90 degrees) |
| `rotation` | number | `0.0` | Pen rotation 0.0–359.0 degrees |
| `barrel` | boolean | `false` | Barrel button pressed |
| `eraser` | boolean | `false` | Eraser mode |
| `hover` | boolean | `false` | Hover without contact |
| `dryRun` | boolean | `false` | Preview only |

#### `inject_pen_stroke`

Draws a pen stroke along a series of points. Each point can override pressure, tilt, and rotation.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `points` | array | *(required)* | Array of point objects (minimum 2). Each: `{ x, y, pressure?, tiltX?, tiltY?, rotation? }` |
| `coordinateSpace` | string | `"normalized"` | Coordinate space |
| `pressure` | number | `0.5` | Default pressure for all points |
| `tiltX` | integer | `0` | Default X tilt |
| `tiltY` | integer | `0` | Default Y tilt |
| `rotation` | number | `0.0` | Default rotation |
| `barrel` | boolean | `false` | Barrel button |
| `eraser` | boolean | `false` | Eraser mode |
| `durationMs` | integer | `400` | Total stroke duration |
| `dryRun` | boolean | `false` | Preview only |

---

### Gamepad Tools

#### `inject_gamepad_input`

Injects a single gamepad input state. The state is held for the specified duration, then released.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `buttons` | array | `[]` | Button names: `"A"`, `"B"`, `"X"`, `"Y"`, `"LeftThumbstick"`, `"RightThumbstick"`, `"LeftShoulder"`, `"RightShoulder"`, `"View"`, `"Menu"`, `"DPadUp"`, `"DPadDown"`, `"DPadLeft"`, `"DPadRight"`, `"Paddle1"`–`"Paddle4"` |
| `leftStickX` | number | `0.0` | Left thumbstick X (-1.0 to 1.0) |
| `leftStickY` | number | `0.0` | Left thumbstick Y (-1.0 to 1.0) |
| `rightStickX` | number | `0.0` | Right thumbstick X |
| `rightStickY` | number | `0.0` | Right thumbstick Y |
| `leftTrigger` | number | `0.0` | Left trigger (0.0–1.0) |
| `rightTrigger` | number | `0.0` | Right trigger (0.0–1.0) |
| `holdDurationMs` | integer | `100` | How long to hold the state |

#### `inject_gamepad_sequence`

Injects a series of gamepad frames in order. After the last frame, all inputs are automatically released.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `frames` | array | *(required)* | Array of frame objects. Each frame accepts the same fields as `inject_gamepad_input` plus `durationMs` (default 100) per frame. |

---

### App Action Tools

App Action tools expose the host application's own discrete operations as MCP tools. Your app opts in by implementing `zRover.Core.IActionableApp` and passing the instance to `RoverMcp.StartAsync`. The two tools are always registered as a pair; if no `IActionableApp` is provided, neither tool appears in `tools/list`.

#### `list_actions`

Returns the set of actions the app currently supports. The list is dynamic — the app may return different actions depending on state (e.g. which objects are selected).

**Parameters:** none

**Returns:**
```json
{
  "actions": [
    {
      "name": "SetPresetColor",
      "description": "Sets the RGB sliders to a named color preset.",
      "parameterSchema": {
        "type": "object",
        "properties": {
          "color": { "type": "string", "enum": ["Red", "Green", "Blue", "Yellow", "White"] }
        },
        "required": ["color"]
      }
    }
  ]
}
```

#### `dispatch_action`

Dispatches a named action and returns whether it succeeded.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | string | *(required)* | Name of the action to dispatch (must match a name from `list_actions`) |
| `params` | object | `{}` | Parameters validated against the action's `parameterSchema` |

**Returns on success:**
```json
{ "success": true, "consequences": ["UpdateColorPreview"] }
```

**Returns on failure:**
```json
{ "success": false, "error": { "code": "unknown_action", "message": "No action named 'Foo'" } }
```

Error codes: `unknown_action`, `validation_error`, `not_available`, `execution_error`.

#### Implementing `IActionableApp`

Add the interface to any class accessible from `App.xaml.cs`, typically your main page:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using zRover.Core;

public sealed partial class MainPage : Page, IActionableApp
{
    private static readonly IReadOnlyList<ActionDescriptor> _actions = new[]
    {
        new ActionDescriptor(
            name: "SetPresetColor",
            description: "Sets the RGB sliders to a named color preset.",
            parameterSchema: @"{
              ""type"": ""object"",
              ""properties"": {
                ""color"": { ""type"": ""string"", ""enum"": [""Red"", ""Green"", ""Blue""] }
              },
              ""required"": [""color""]
            }"),
    };

    public IReadOnlyList<ActionDescriptor> GetAvailableActions() => _actions;

    public async Task<ActionResult> DispatchAsync(string actionName, string parametersJson)
    {
        switch (actionName)
        {
            case "SetPresetColor":
                return await DispatchSetPresetColorAsync(parametersJson);
            default:
                return ActionResult.Fail("unknown_action", $"No action named '{actionName}'");
        }
    }

    private async Task<ActionResult> DispatchSetPresetColorAsync(string parametersJson)
    {
        JObject p;
        try { p = JObject.Parse(parametersJson); }
        catch { return ActionResult.Fail("validation_error", "params is not valid JSON"); }

        var color = p["color"]?.Value<string>();
        if (string.IsNullOrEmpty(color))
            return ActionResult.Fail("validation_error", "Missing required parameter: color");

        // Marshal UI work to the dispatcher thread.
        await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        {
            switch (color)
            {
                case "Red":   RedSlider.Value = 255; GreenSlider.Value = 0;   BlueSlider.Value = 0;   break;
                case "Green": RedSlider.Value = 0;   GreenSlider.Value = 255; BlueSlider.Value = 0;   break;
                case "Blue":  RedSlider.Value = 0;   GreenSlider.Value = 0;   BlueSlider.Value = 255; break;
            }
        });

        return ActionResult.Ok(new[] { "UpdateColorPreview" });
    }
}
```

Pass the instance to `RoverMcp.StartAsync` in `App.xaml.cs`:

```csharp
protected override async void OnLaunched(LaunchActivatedEventArgs e)
{
    // ... navigation setup, Window.Current.Activate() ...

    var actionableApp = rootFrame.Content as zRover.Core.IActionableApp;
    await zRover.Uwp.RoverMcp.StartAsync("MyApp", port: 5100,
        launchFullTrust: () => FullTrustProcessLauncher
            .LaunchFullTrustProcessForCurrentAppAsync("McpServer").AsTask(),
        actionableApp: actionableApp);
}
```

> **Thread safety:** `GetAvailableActions()` is called from the AppService IPC thread (background). `DispatchAsync` receives the call on a background thread — marshal any UI work to `Dispatcher.RunAsync` as shown above.

---

### Logging Tools

#### `get_logs`

Returns recent entries from the zRover in-memory log store. Useful for understanding app state, diagnosing failures, and reading lifecycle and exception events logged by the host app.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `minimumLevel` | string | `"info"` | Minimum level: `"trace"`, `"debug"`, `"info"`, `"warning"`, `"error"`, `"fatal"` |
| `maxEntries` | integer | `100` | Maximum number of entries to return (newest first) |
| `category` | string | *(none)* | Filter to entries whose category starts with this string |
| `clear` | boolean | `false` | If `true`, clears the buffer after returning the snapshot |

---

### UI Tree Tools

#### `get_ui_tree`

Walks the XAML visual tree and returns a structured representation of every UI element. Useful for discovering element names, automation IDs, text content, and positions without taking a screenshot.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `maxDepth` | integer | `32` | Maximum depth to traverse into the visual tree |
| `visibleOnly` | boolean | `false` | If `true`, excludes elements whose `Visibility` is `Collapsed` |

**Returns:** A tree of nodes. Each node has:
- `type` — XAML class name (e.g. `"Button"`, `"TextBlock"`)
- `name` — `x:Name` value, if set
- `automationName` — `AutomationProperties.Name` value, if set
- `text` — text content extracted from `TextBlock`, `TextBox`, or `ContentControl`
- `bounds` — `{ x, y, width, height }` in normalized coordinates (0.0–1.0)
- `isVisible` — whether the element is in the visible state
- `isEnabled` — whether the element is enabled
- `children` — nested child nodes

---

### Window Tools

#### `resize_page`

Resizes the app window to the specified dimensions.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `width` | number | *(required)* | Desired window width in DIPs |
| `height` | number | *(required)* | Desired window height in DIPs |

**Returns:** `{ success, actualWidth, actualHeight }` — the actual bounds after resize (the OS may constrain the requested size).

---

### Wait Tools

#### `wait_for`

Blocks until a condition is met or the timeout expires. Use this to synchronize with animations, loading states, or async events before proceeding with other tools.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `condition` | string | *(required)* | `"visual_stable"` or `"log_match"` |
| `timeoutMs` | integer | `5000` | Maximum time to wait in milliseconds |
| `stabilityMs` | integer | `400` | (`visual_stable` only) How long the UI must remain unchanged to count as stable |
| `intervalMs` | integer | `150` | Polling interval in milliseconds |
| `pattern` | string | *(none)* | (`log_match` only) Regex pattern to match against log messages |

**Conditions:**
- `visual_stable` — polls screenshot hashes until the image is unchanged for `stabilityMs`. Good for waiting after animations, page transitions, or loading indicators.
- `log_match` — polls the in-memory log store until an entry matching `pattern` appears. Good for waiting for a specific event to be logged by the host app.

**Returns:** `{ success, condition, elapsedMs, reason }` — `reason` explains why the wait ended (e.g. `"stable"`, `"matched"`, `"timeout"`).

---

### Retriever: Package Management Tools

The Retriever exposes 10 additional MCP tools for MSIX package management. These tools are available on the Retriever's MCP endpoint (port 5200 by default) and support multi-device routing via the `deviceId` parameter.

For full documentation of the upload flow, hop-by-hop forwarding, and security model, see the [MSIX Package Management](multi-machine-federation.md#msix-package-management) section in the Multi-Machine Federation guide.

#### `list_devices`

Lists all devices reachable from this Retriever instance (local device plus all federated remote managers).

**Parameters:** none

**Returns:** Array of `{ deviceId, displayName, hops, isLocal }`.

#### `list_installed_packages`

Lists MSIX packages installed on a device.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `nameFilter` | string | *(none)* | Filter by package family name or display name substring. |
| `includeFrameworks` | boolean | `false` | Include framework packages. |
| `includeSystemPackages` | boolean | `false` | Include resource/system packages. |

**Returns:** Array of `PackageInfo` objects with family name, full name, display name, version, publisher, install date, architecture, signing status, running state, and app entries.

#### `get_package_info`

Returns full metadata for a single package including dependencies, capabilities, and health status flags.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `packageFamilyName` | string | *(required)* | Package family name to look up. |

#### `install_package`

Installs an MSIX package from a URI or a staged upload.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `packageUri` | string | *(required)* | URI of the package to install. Schemes: `https://`, `file://`, `ms-appinstaller://`, `staged://<stagingId>`. |
| `dependencyUris` | array | `[]` | Dependency package URIs to install alongside. |
| `forceAppShutdown` | boolean | `false` | Force-close the app if it is currently running. |
| `allowUnsigned` | boolean | `false` | Allow packages without a trusted signature (Developer Mode required). |
| `installForAllUsers` | boolean | `false` | Install as a full (non-stub) package for all users. |
| `deferRegistration` | boolean | `false` | Defer registration until the package is no longer in use. |

**Returns:** `{ success, packageFamilyName, packageFullName, version, wasUpdate, isDeferred }` on success.

#### `uninstall_package`

Removes an installed MSIX package.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `packageFamilyName` | string | *(required)* | Package to remove. |
| `removeForAllUsers` | boolean | `false` | Remove for all users (requires elevated privilege). |
| `preserveAppData` | boolean | `false` | Keep the app's local data after removal. |

#### `launch_app`

Launches an app entry within an installed package.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `packageFamilyName` | string | *(required)* | Package containing the app. |
| `appId` | string | *(none)* | Specific app entry ID. If omitted, the first entry is launched. |
| `arguments` | string | `""` | Command-line arguments to pass to the app. |

**Returns:** `{ success, pid, aumid }` on success.

#### `stop_app`

Stops all running instances of a package.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `packageFamilyName` | string | *(required)* | Package to stop. |
| `force` | boolean | `false` | Kill immediately without sending WM_CLOSE first. |

**Returns:** `{ success, stoppedCount }`.

#### `request_package_upload`

Negotiates an upload slot for sending an MSIX file to a device. In multi-hop federation, the upload flows through each manager in the chain.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `deviceId` | string | `"local"` | Target device. |
| `fileName` | string | *(required)* | Original file name (e.g. `MyApp_1.0.0.0_x64.msix`). |
| `fileSizeBytes` | integer | *(required)* | Exact file size in bytes. |
| `sha256` | string | *(required)* | Hex-encoded SHA-256 hash of the file (verified at every hop). |

**Returns:** `{ stagingId, uploadUrl, expiresAt }`. POST the raw binary of the MSIX to `uploadUrl` with `Content-Type: application/octet-stream`.

#### `get_package_stage_status`

Checks the status of a pending or completed staged upload.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `stagingId` | string | *(required)* | Staging ID returned by `request_package_upload`. |

**Returns:** `{ stagingId, status, fileName, fileSizeBytes, createdAt }`. Status values: `Pending`, `Ready`, `Forwarding`, `Expired`.

#### `discard_package_stage`

Cancels a staged upload and deletes any uploaded data.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `stagingId` | string | *(required)* | Staging ID to discard. |

---

## Coordinate Spaces

Most input tools accept a `coordinateSpace` parameter:

| Value | Description |
|---|---|
| `"normalized"` | **Default.** Values from `0.0` (top-left) to `1.0` (bottom-right), relative to the app window. Recommended for resolution-independent automation. |
| `"pixels"` | Render-pixel coordinates matching `windowWidth`/`windowHeight` from `capture_current_view`. Use when you have pixel positions from a screenshot or UI tree bounds. |

**Converting screenshot pixels to normalized coordinates:**

```
normalizedX = pixelX / screenshotWidth
normalizedY = pixelY / screenshotHeight
```

The `capture_current_view` response includes `width` and `height` for this conversion.

## Preview & Dry Run

The `inject_tap` and `inject_drag_path` tools return an **annotated preview screenshot** showing exactly where the input will land *before* injection occurs. This lets an AI agent visually verify its target.

Set `dryRun: true` on any input tool to get the preview without actually injecting input — useful for coordinate validation workflows.

## Requirements & Limitations

- **Developer Mode** must be enabled on the machine for input injection to work.
- **Input injection** requires the `inputInjectionBrokered` restricted capability. Without it, touch, pen, gamepad, and multi-touch injection will not work.
- **Architecture must match the host machine.** The `InputInjector` COM component is native-only — it does not work under emulation. On ARM64 machines, you **must** set your Solution Platform to **ARM64** in Configuration Manager. Both **AnyCPU** and **x64** produce packages that Windows runs under x64 emulation on ARM64, causing `InputInjector.TryCreate()` to fail with `HRESULT 0x800700C1` ("is not a valid Win32 application"). When InputInjector is unavailable, tap and drag tools fall back to Win32 `SendInput` (mouse-only, no touch/pen/gamepad). See [Troubleshooting](#troubleshooting) for details.
- The MCP server runs as a **FullTrust companion process** alongside your UWP app. Both processes must be running for tools to work.
- The server listens on **localhost only** — remote connections require your own tunneling solution.
- **.NET 8 runtime** must be installed on the target machine (the companion is published as framework-dependent).
- Screenshot tools operate on the XAML visual tree. Overlays or content rendered outside the XAML tree (e.g. DirectX swap chains) may not appear in captures.

## Troubleshooting

**MCP client can't connect**
- Verify the app is running and the port is not in use by another process: `Test-NetConnection -ComputerName localhost -Port 5100`
- Check that Developer Mode is enabled.
- The FullTrust companion needs a moment to start after launch. Wait a few seconds after the app window appears.

**Tools list is empty**
- Ensure `RoverMcp.StartAsync` is called **after** `Window.Current.Activate()` in `OnLaunched`.
- Verify `OnBackgroundActivated` calls `RoverMcp.HandleBackgroundActivation(args)`.

**Input injection has no effect**
- Confirm `inputInjectionBrokered` is declared in your manifest.
- Ensure Developer Mode is enabled (required by `InputInjector`).
- The app window must be in the foreground — the companion server automatically brings it forward before injection.

**InputInjector fails on ARM64**
- On ARM64 machines, the `InputInjector` COM interface is native ARM64 only. If your app is built as **x64** or **AnyCPU**, Windows runs the UWP process under x64 emulation, and `InputInjector.TryCreate()` fails.
- **Fix:** In Visual Studio Configuration Manager, set your Solution Platform to **ARM64** on ARM64 hardware.
- Check the diagnostic log at startup: the MCP server logs `InputInjectionCapability: InputInjector not available` with the specific error when this occurs.

**App crashes on launch**
- Check the crash log at `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\crash.log`.
- Ensure you've added all required manifest capabilities and extensions.

**Port conflict**
- Pass a different port: `RoverMcp.StartAsync("MyApp", port: 5200, ...)` — then update your MCP client URL accordingly.
