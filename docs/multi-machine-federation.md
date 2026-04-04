# Multi-Machine Federation

Rover Background Manager supports connecting multiple machines together so that a single MCP client can discover and control apps running across all of them. You point your AI tool (such as Copilot) at one machine — the **root** — and it sees apps on every other machine that has been linked to it.

---

## How It Works

Each machine runs its own Background Manager. Normally a manager only listens on `localhost` and is not reachable over the network. Federation works by:

1. **Machine B** opts in to external access, which opens a network listener and generates a one-time bearer token.
2. **Machine A** connects to Machine B using that token. Machine A's manager becomes an MCP client of Machine B's manager.
3. Machine A now sees all of Machine B's app sessions as if they were local. Any tool call targeting one of those sessions is transparently forwarded to Machine B.

The MCP client (e.g. Copilot) only ever talks to Machine A. It doesn't need to know anything about the underlying topology.

```
  Your AI tool
       │
       ▼
 ┌──────────────┐
 │  Machine A   │  ← you point Copilot here
 │  (root)      │
 └──┬───────┬───┘
    │       │
    ▼       ▼
 Machine B  Machine C
  App1       App3
  App2       App4
```

Chains can be deeper: Machine B can itself be connected to Machine D, and Machine A will see Machine D's apps too (with a `hops` value of 2 in the session metadata).

---

## Enabling External Access

By default, a Background Manager only accepts connections from the same machine. To allow other managers to connect, you need to explicitly enable external access.

### Via the dashboard

Open the Background Manager window and flip the **Allow external connections** toggle. The manager will:

- Start listening on your local network IP (port 5201 by default)
- Generate a fresh bearer token
- Show you a **Copy connection link** button

The connection link is a `zrover://` URI that encodes the URL and token. Clicking it on another machine (with Rover installed) will automatically connect that machine's manager to this one.

The token is regenerated every time you toggle external access on. Anyone who had a previous link will need a new one.

### Via protocol activation

You can also trigger this from the command line or a script by opening a `zrover://` URI:

| URI | Effect |
|-----|--------|
| `zrover://enable-external` | Enable external access on port 5201 |
| `zrover://enable-external?port=5300` | Enable on a custom port |
| `zrover://disable-external` | Disable external access |
| `zrover://status` | Bring the dashboard to the foreground |

On Windows, you can run these from a browser address bar, a Run dialog, or with `start zrover://enable-external`.

---

## Connecting to a Remote Manager

On the machine you want to act as the root (Machine A), open the dashboard and use the **Remote Managers** section.

**Using a connection link:** Open the `zrover://connect?url=...&token=...` link that was generated and copied on Machine B. This can be clicked in a browser or passed as a command-line argument to the Background Manager executable. The connection is established automatically.

Once connected, Machine B's apps appear in the sessions list with a `(via Machine-B)` label. They are immediately available to any MCP client connected to Machine A.

To disconnect, click **Disconnect** next to the manager in the dashboard.

---

## How Sessions Are Identified

When an app session is propagated from a remote machine, its session ID is prefixed with a short manager identifier to avoid collisions:

```
Local session:         abc123def456
From Machine B:        a1b2:abc123def456
From Machine C via B:  a1b2:c3d4:xyz789abc
```

The `hops` field in tool responses indicates how many manager-to-manager links a session has passed through:

- `hops: 0` — local session on the machine you're talking to
- `hops: 1` — one link away (directly connected machine)
- `hops: 2` — two links away, and so on

---

## Tool Calls Across Machines

From the perspective of an MCP client, there is no difference between a local and a remote session. You call `list_apps`, pick a session, call `set_active_app`, then invoke tools — the same as always.

Under the hood, when you invoke a tool on a remote session, the root manager:
1. Tells the remote manager to make that session active (`set_active_app`)
2. Forwards the tool call to the remote manager
3. Returns its result back to you

This adds one extra network round-trip per call. For most tools (screenshots, UI tree, gestures) this is imperceptible in practice.

**Note:** The remote manager only has one active session at a time, just like a local one. If you are rapidly alternating tool calls between two remote sessions on the same machine, each call will include a `set_active_app` step. For sustained interaction with a single remote app this is skipped automatically.

---

## Session Sync

When Machine B's session list changes (an app connects or disconnects), Machine A learns about it in real time via MCP's `tools/list_changed` notification — no polling. The session list on Machine A updates immediately.

If the network connection to a remote manager drops, a background health check detects this within ~10 seconds and marks the affected sessions as disconnected. They remain visible in the list so you can see what was lost.

---

## Security

- **Localhost-only by default.** A manager that has not enabled external access cannot be reached over the network, regardless of firewall settings.
- **Bearer token required.** Every request to an external listener must include the bearer token. There is no unauthenticated access.
- **Tokens are single-use per session.** Each time you toggle external access on, a new token is generated. Old tokens stop working immediately.
- **The token is in the connection link.** Treat the connection link like a password — anyone who has it can connect to your manager while external access is enabled.

---

## MSIX Package Management

Background Manager exposes a set of MCP tools for installing, launching, stopping, and removing MSIX packages — both on the local machine and on any connected remote machine. The same `deviceId` routing that applies to app sessions applies here too.

### Device IDs

Every package management tool accepts an optional `deviceId` parameter:

| `deviceId` value | Meaning |
|---|---|
| *(absent or `"local"`)* | Same machine the MCP client is talking to |
| `"a1b2"` | The remote manager with that short ID |
| `"a1b2:c3d4"` | A machine two hops away: first hop is `a1b2`, second hop is `c3d4` |

Use `list_devices` to discover all reachable devices and their IDs.

### Available Package Tools

| Tool | Description |
|---|---|
| `list_devices` | Lists all reachable devices (local + all federated managers). |
| `list_installed_packages` | Lists MSIX packages installed on a device. Supports name filter and framework/system package inclusion flags. |
| `get_package_info` | Returns full metadata for one package: version, publisher, install location, app entries, capabilities, dependencies, and health status. |
| `install_package` | Installs an MSIX from a URI (`https://`, `file://`, `ms-appinstaller://`) or from a staged upload (`staged://<stagingId>`). |
| `uninstall_package` | Removes a package by family name. Optionally preserves app data or removes for all users. |
| `launch_app` | Launches a specific app entry within a package by AUMID or app ID. Returns the new process ID. |
| `stop_app` | Stops all running instances of a package. Supports graceful and forced termination. |
| `request_package_upload` | Negotiates a one-time upload URL for sending an MSIX file to a specific device. In chained scenarios, the upload hops through every manager in the path. |
| `get_package_stage_status` | Checks the status of a staged upload by staging ID. |
| `discard_package_stage` | Cancels and deletes a staged upload. |

### Installing an MSIX from a URI

If the package is already accessible over the network or as a local file:

```
install_package(deviceId="a1b2", packageUri="https://example.com/MyApp.msix")
install_package(packageUri="file:///C:/drops/MyApp.msix")
install_package(packageUri="ms-appinstaller:?source=https://example.com/MyApp.appinstaller")
```

### Uploading an MSIX File

Use this when the MSIX is not reachable at a network URL. The upload is coordinated through the manager chain so the file never needs to be directly reachable by the target device.

**Step 1 — Request an upload slot:**

```
request_package_upload(
  deviceId = "a1b2:c3d4",
  fileName = "MyApp_1.0.0.0_x64.msix",
  fileSizeBytes = 12345678,
  sha256 = "abc123..."
)
```

This returns:
```json
{
  "stagingId": "s-abc123",
  "uploadUrl": "http://192.168.1.10:5201/packages/stage/<token>",
  "expiresAt": "2025-01-01T12:30:00Z"
}
```

**Step 2 — POST the file to `uploadUrl`:**

```http
POST http://192.168.1.10:5201/packages/stage/<token>
Content-Type: application/octet-stream
Content-Length: 12345678

<MSIX binary>
```

The upload token is single-use and expires after 30 minutes. No bearer token is required on this endpoint. In a multi-hop chain, the intermediate manager automatically forwards the upload to the next link.

**Step 3 — Install from the staging area:**

Once the upload completes (status `Ready`), install using the `staged://` scheme:

```
install_package(deviceId="a1b2:c3d4", packageUri="staged://s-abc123")
```

Staged files are automatically deleted 24 hours after upload.

### Security Model

- **Single-use upload tokens.** Each `request_package_upload` call generates a cryptographically random 256-bit (32-byte) URL token. The token is valid for exactly one `POST` upload.
- **SHA-256 verification.** The file hash you declare in `request_package_upload` is checked after the upload. If it doesn't match, the upload is rejected with HTTP 422.
- **Upload endpoint is separate from bearer auth.** The `/packages/stage/<token>` endpoint does not require the bearer authentication token. The upload URL is the credential — keep it secret.
- **Hop-by-hop integrity.** In a multi-hop chain, SHA-256 is verified at each hop before the data is forwarded. A tampered file cannot pass a downstream manager.
- **Automatic expiry.** Unuploaded tokens expire after 30 minutes; uploaded files expire after 24 hours. `PurgeExpired` runs on every health-check cycle.

### Manifest Capabilities

The Background Manager's `Package.appxmanifest` declares the restricted capabilities required for package management:

```xml
<rescap:Capability Name="appDiagnostics" />
<rescap:Capability Name="packageManagement" />
```

`packageManagement` is required for install/uninstall via the WinRT `PackageManager` API. `appDiagnostics` is required for the graceful `stop_app` path (via `AppDiagnosticInfo`).

---

## Things to Keep in Mind

- **One active session per manager.** Each Background Manager (local or remote) has a single active session slot. Setting a remote session active affects that remote manager's state for all clients connected to it.
- **Tool schema is uniform.** All Rover apps expose the same tools regardless of which machine they are on, so you don't need to think about schema differences when switching sessions.
- **Large payloads (screenshots, etc.) flow through the chain.** Screenshots are base64-encoded in the tool response and travel through each manager hop. On slow networks, large captures from deeply chained machines may take longer.
- **Disconnecting a manager removes its sessions.** When you disconnect a remote manager from the dashboard, all of its propagated sessions are removed from your local list immediately.
